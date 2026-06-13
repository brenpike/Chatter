using Chatter.MessageBrokers.RabbitMQ.Configuration;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using System;
using Xunit;

namespace Chatter.MessageBrokers.RabbitMQ.Tests.Configuration.UsingRabbitMqOptionsBuilder
{
    // Pins RabbitMqOptionsBuilder and RabbitMqOptions AS-IS, including the latent quirk that a fluent setter
    // (WithUri/WithHostName/...) NREs when called before an AddRabbitMqOptions overload constructs the options
    // instance — mirroring the SSB builder's WhenBuilding characterization.
    // INVARIANT: the default message body type const is "application/json; charset=utf-8" and matches the
    // property initializer, WithJsonBodyType(), and the AddRabbitMqOptions(...) overload default.
    public class WhenBuilding : Testing.Core.Context
    {
        private const string DefaultMessageBodyType = "application/json; charset=utf-8";

        private static RabbitMqOptionsBuilder NewBuilder()
            => new RabbitMqOptionsBuilder(new ServiceCollection());

        // --- constructor guard ---

        [Fact]
        public void MustThrowArgumentNullExceptionWhenServicesNull()
        {
            Action act = () => new RabbitMqOptionsBuilder(null);
            act.Should().Throw<ArgumentNullException>();
        }

        [Fact]
        public void MustExposeProvidedServicesInstance()
        {
            var services = new ServiceCollection();
            new RabbitMqOptionsBuilder(services).Services.Should().BeSameAs(services);
        }

        // --- fluent setter before AddRabbitMqOptions dereferences null options ---

        [Fact]
        public void MustThrowNullReferenceExceptionWhenWithUriCalledBeforeAddOptions()
        {
            Action act = () => NewBuilder().WithUri("amqp://localhost");
            act.Should().Throw<NullReferenceException>();
        }

        [Fact]
        public void MustThrowNullReferenceExceptionWhenWithHostNameCalledBeforeAddOptions()
        {
            Action act = () => NewBuilder().WithHostName("localhost");
            act.Should().Throw<NullReferenceException>();
        }

        [Fact]
        public void MustThrowNullReferenceExceptionWhenWithQueueTypeCalledBeforeAddOptions()
        {
            Action act = () => NewBuilder().WithQueueType(QueueType.Classic);
            act.Should().Throw<NullReferenceException>();
        }

        // --- Build() guards ---

        [Fact]
        public void MustThrowArgumentNullExceptionWhenBuildingWithNoOptions()
        {
            Action act = () => NewBuilder().Build();
            act.Should().Throw<ArgumentNullException>();
        }

        [Fact]
        public void MustThrowArgumentNullExceptionWhenNeitherUriNorHostNameProvided()
        {
            Action act = () => NewBuilder()
                .AddRabbitMqOptions(new RabbitMqOptions(uri: null, hostName: "   "))
                .Build();
            act.Should().Throw<ArgumentNullException>();
        }

        [Fact]
        public void MustThrowArgumentNullExceptionWhenMessageBodyTypeBlank()
        {
            Action act = () => NewBuilder()
                .AddRabbitMqOptions(new RabbitMqOptions(hostName: "localhost", messageBodyType: "   "))
                .Build();
            act.Should().Throw<ArgumentNullException>();
        }

        [Fact]
        public void MustBuildWhenOnlyUriProvided()
        {
            var options = NewBuilder()
                .AddRabbitMqOptions(new RabbitMqOptions(uri: "amqp://localhost:5672"))
                .Build();
            options.Uri.Should().Be("amqp://localhost:5672");
        }

        [Fact]
        public void MustReturnSameOptionsInstanceFromBuildWhenValid()
        {
            var options = new RabbitMqOptions(hostName: "localhost");
            NewBuilder().AddRabbitMqOptions(options).Build().Should().BeSameAs(options);
        }

        // --- AddRabbitMqOptions overloads ---

        [Fact]
        public void MustStoreOptionsFromInstanceOverloadAndReturnBuilder()
        {
            var builder = NewBuilder();
            var options = new RabbitMqOptions(hostName: "localhost");
            builder.AddRabbitMqOptions(options).Should().BeSameAs(builder);
            builder.Build().Should().BeSameAs(options);
        }

        [Fact]
        public void MustStoreOptionsFromFuncOverloadAndReturnBuilder()
        {
            var builder = NewBuilder();
            var options = new RabbitMqOptions(hostName: "localhost");
            builder.AddRabbitMqOptions(() => options).Should().BeSameAs(builder);
            builder.Build().Should().BeSameAs(options);
        }

        [Fact]
        public void MustStoreOptionsFromParameterizedOverloadAndReturnBuilder()
        {
            var builder = NewBuilder();
            builder.AddRabbitMqOptions(hostName: "localhost").Should().BeSameAs(builder);
        }

        [Fact]
        public void MustApplyParameterizedOverloadDefaults()
        {
            var options = NewBuilder().AddRabbitMqOptions(hostName: "localhost").Build();
            options.HostName.Should().Be("localhost");
            options.MessageBodyType.Should().Be(DefaultMessageBodyType);
            options.Prefetch.Should().Be(1);
            options.QueueType.Should().Be(QueueType.Quorum);
        }

        // --- fluent setters map onto the options and return the builder ---

        [Fact]
        public void MustApplyWithUriAndReturnBuilder()
        {
            var builder = NewBuilder().AddRabbitMqOptions(hostName: "localhost");
            builder.WithUri("amqp://other").Should().BeSameAs(builder);
            builder.Build().Uri.Should().Be("amqp://other");
        }

        [Fact]
        public void MustApplyWithHostNameAndReturnBuilder()
        {
            var builder = NewBuilder().AddRabbitMqOptions(hostName: "localhost");
            builder.WithHostName("other-host").Should().BeSameAs(builder);
            builder.Build().HostName.Should().Be("other-host");
        }

        [Fact]
        public void MustApplyWithCredentialsAndReturnBuilder()
        {
            var builder = NewBuilder().AddRabbitMqOptions(hostName: "localhost");
            builder.WithCredentials("user", "pass").Should().BeSameAs(builder);
            var options = builder.Build();
            options.UserName.Should().Be("user");
            options.Password.Should().Be("pass");
        }

        [Fact]
        public void MustApplyWithMessageBodyTypeAndReturnBuilder()
        {
            var builder = NewBuilder().AddRabbitMqOptions(hostName: "localhost");
            builder.WithMessageBodyType("text/plain").Should().BeSameAs(builder);
            builder.Build().MessageBodyType.Should().Be("text/plain");
        }

        [Fact]
        public void MustApplyWithJsonBodyTypeAsDefaultConstAndReturnBuilder()
        {
            var builder = NewBuilder().AddRabbitMqOptions(hostName: "localhost").WithMessageBodyType("text/plain");
            builder.WithJsonBodyType().Should().BeSameAs(builder);
            builder.Build().MessageBodyType.Should().Be(DefaultMessageBodyType);
        }

        [Fact]
        public void MustApplyWithPrefetchAndReturnBuilder()
        {
            var builder = NewBuilder().AddRabbitMqOptions(hostName: "localhost");
            builder.WithPrefetch(64).Should().BeSameAs(builder);
            builder.Build().Prefetch.Should().Be(64);
        }

        [Fact]
        public void MustApplyWithQueueTypeAndReturnBuilder()
        {
            var builder = NewBuilder().AddRabbitMqOptions(hostName: "localhost");
            builder.WithQueueType(QueueType.Classic).Should().BeSameAs(builder);
            builder.Build().QueueType.Should().Be(QueueType.Classic);
        }

        // --- RabbitMqOptions ctor defaults ---

        [Fact]
        public void MustApplyOptionsCtorDefaults()
        {
            var options = new RabbitMqOptions();
            options.MessageBodyType.Should().Be(DefaultMessageBodyType);
            options.Prefetch.Should().Be(1);
            options.QueueType.Should().Be(QueueType.Quorum);
        }
    }
}
