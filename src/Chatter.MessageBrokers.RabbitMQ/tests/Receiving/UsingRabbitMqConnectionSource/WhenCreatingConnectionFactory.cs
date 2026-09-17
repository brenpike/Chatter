using Chatter.MessageBrokers.RabbitMQ.Configuration;
using Chatter.MessageBrokers.RabbitMQ.Receiving;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using RabbitMQ.Client;
using System;
using System.Net.Security;
using Xunit;

namespace Chatter.MessageBrokers.RabbitMQ.Tests.Receiving.UsingRabbitMqConnectionSource
{
    // Pins how RabbitMqOptions becomes a ConnectionFactory, with the TLS surface as the focus.
    //
    // INVARIANT (default-off): a configuration that never sets UseTls produces a factory whose Ssl is untouched,
    // so every pre-existing consumer observes byte-identical behaviour.
    // INVARIANT (URI precedence): the AMQP URI fully determines the transport. TLS from the discrete settings is
    // applied only on the URI-less path, so an amqps URI keeps the client-owned TLS configuration it parses.
    // INVARIANT (strict validation): the discrete TLS path sets ONLY Ssl.Enabled and Ssl.ServerName. Every other
    // SslOption member — notably AcceptablePolicyErrors — stays at its strict default, so certificate validation
    // is never weakened. The URI path is LAXER (the client relaxes a name mismatch on an amqps scheme); that
    // relaxation is deliberately not copied here.
    //
    // These drive the production RabbitMqConnectionSource through the InternalsVisibleTo seam; no broker is
    // contacted because building a ConnectionFactory does not connect.
    public class WhenCreatingConnectionFactory : Testing.Core.Context
    {
        private const int DefaultAmqpPort = 5672;
        private const int DefaultAmqpSslPort = 5671;

        private static ConnectionFactoryProbe Probe(RabbitMqOptions options)
        {
            using var source = new RabbitMqConnectionSource(options);
            return ConnectionFactoryProbe.From(source.CreateConnectionFactory());
        }

        private sealed class ConnectionFactoryProbe
        {
            public bool SslEnabled { get; private init; }
            public string SslServerName { get; private init; }
            public SslPolicyErrors AcceptablePolicyErrors { get; private init; }
            public string HostName { get; private init; }
            public int Port { get; private init; }

            public static ConnectionFactoryProbe From(ConnectionFactory factory)
                => new ConnectionFactoryProbe
                {
                    SslEnabled = factory.Ssl.Enabled,
                    SslServerName = factory.Ssl.ServerName,
                    AcceptablePolicyErrors = factory.Ssl.AcceptablePolicyErrors,
                    HostName = factory.HostName,
                    Port = factory.Endpoint.Port
                };
        }

        // --- default-off regression guard ---

        [Fact]
        public void MustLeaveTlsDisabledWhenNotRequested()
        {
            var probe = Probe(new RabbitMqOptions(hostName: "rabbit-host", userName: "user", password: "pass"));

            probe.SslEnabled.Should().BeFalse();
            probe.HostName.Should().Be("rabbit-host");
            probe.Port.Should().Be(DefaultAmqpPort);
        }

        // --- discrete TLS path ---

        [Fact]
        public void MustEnableTlsAndDefaultServerNameToHostNameWhenTlsRequested()
        {
            var probe = Probe(new RabbitMqOptions(hostName: "rabbit-host") { UseTls = true });

            probe.SslEnabled.Should().BeTrue();
            probe.SslServerName.Should().Be("rabbit-host");
            probe.Port.Should().Be(DefaultAmqpSslPort);
        }

        [Fact]
        public void MustUseConfiguredTlsServerNameOverHostName()
        {
            var probe = Probe(new RabbitMqOptions(hostName: "rabbit-host")
            {
                UseTls = true,
                TlsServerName = "certificate-common-name"
            });

            probe.SslEnabled.Should().BeTrue();
            probe.SslServerName.Should().Be("certificate-common-name");
        }

        [Fact]
        public void MustNotWeakenCertificateValidationWhenTlsRequested()
        {
            var probe = Probe(new RabbitMqOptions(hostName: "rabbit-host") { UseTls = true });

            probe.AcceptablePolicyErrors.Should().Be(SslPolicyErrors.None);
        }

        // --- URI precedence ---

        [Fact]
        public void MustEnableTlsFromAmqpsUriWithoutUseTls()
        {
            var probe = Probe(new RabbitMqOptions(uri: "amqps://user:pass@rabbit-host/vhost"));

            probe.SslEnabled.Should().BeTrue();
            probe.Port.Should().Be(DefaultAmqpSslPort);
        }

        [Fact]
        public void MustNotApplyDiscreteTlsOverAnAmqpsUri()
        {
            var probe = Probe(new RabbitMqOptions(uri: "amqps://user:pass@uri-host/vhost", hostName: "discrete-host")
            {
                UseTls = true,
                TlsServerName = "discrete-name"
            });

            probe.SslEnabled.Should().BeTrue();
            probe.SslServerName.Should().NotBe("discrete-name");
            probe.HostName.Should().Be("uri-host");
        }

        // --- trust-boundary guard: TLS is never SILENTLY dropped in favour of a plaintext URI ---
        //
        // RabbitMqOptionsBuilder.Build() rejects this same combination at registration, but RabbitMqOptions is a
        // public MUTABLE type that AddRabbitMq registers as a singleton and that the caller may keep a reference to,
        // so the registration-time check can be stepped around entirely: by assigning the properties directly
        // (below), or by mutating the very instance Build() returned. CreateConnectionFactory is the ONLY production
        // path to a real connection, so these pin the invariant where the transport is actually decided — a TLS
        // request can never resolve to a cleartext connection carrying the URI's credentials.

        [Fact]
        public void MustRejectTlsRequestedAlongsideAPlaintextUri()
        {
            var options = new RabbitMqOptions(uri: "amqp://user:pass@uri-host/vhost", hostName: "discrete-host");
            options.UseTls = true;

            Action act = () => Probe(options);

            act.Should().Throw<InvalidOperationException>().WithMessage("*amqps*");
        }

        [Fact]
        public void MustRejectTlsRequestedAlongsideAUriMutatedToPlaintextAfterBuild()
        {
            var options = new RabbitMqOptionsBuilder(new ServiceCollection())
                .AddRabbitMqOptions(new RabbitMqOptions(uri: "amqps://user:pass@uri-host/vhost"))
                .WithTls()
                .Build();

            // Build() accepted the amqps URI; the caller still holds the registered instance and downgrades it.
            options.Uri = "amqp://user:pass@uri-host/vhost";

            Action act = () => Probe(options);

            act.Should().Throw<InvalidOperationException>().WithMessage("*amqps*");
        }

        [Fact]
        public void MustRejectTlsRequestedAlongsideAnUnparsableUri()
        {
            var options = new RabbitMqOptions(uri: "not-a-uri", hostName: "discrete-host");
            options.UseTls = true;

            Action act = () => Probe(options);

            act.Should().Throw<InvalidOperationException>().WithMessage("*amqps*");
        }

        [Fact]
        public void MustLeaveAPlaintextUriUntouchedWhenTlsIsNotRequested()
        {
            var probe = Probe(new RabbitMqOptions(uri: "amqp://user:pass@uri-host/vhost"));

            probe.SslEnabled.Should().BeFalse();
            probe.HostName.Should().Be("uri-host");
            probe.Port.Should().Be(DefaultAmqpPort);
        }
    }
}
