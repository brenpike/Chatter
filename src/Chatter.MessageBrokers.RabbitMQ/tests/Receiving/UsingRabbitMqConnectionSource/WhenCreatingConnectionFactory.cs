using Chatter.MessageBrokers.RabbitMQ.Configuration;
using Chatter.MessageBrokers.RabbitMQ.Receiving;
using FluentAssertions;
using RabbitMQ.Client;
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
    }
}
