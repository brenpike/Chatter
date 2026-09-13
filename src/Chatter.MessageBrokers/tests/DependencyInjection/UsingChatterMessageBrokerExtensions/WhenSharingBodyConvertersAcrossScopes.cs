#nullable disable

using Chatter.CQRS;
using Chatter.MessageBrokers.Receiving;
using Chatter.MessageBrokers.Sending;
using Chatter.MessageBrokers.Tests.Receiving.Fakes;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using System;
using System.Linq;
using Xunit;

namespace Chatter.MessageBrokers.Tests.DependencyInjection.UsingChatterMessageBrokerExtensions
{
    /// <summary>
    /// Drives the REAL <c>AddMessageBrokers</c> graph through <see cref="IServiceScopeFactory"/> the way senders and
    /// receivers drive it: every delivery and every outbox poll opens its own scope, so a body converter factory
    /// resolved per scope is rebuilt, and its converter lookup re-populated, once per message.
    /// </summary>
    /// <remarks>
    /// INVARIANT: every provider here is built with <see cref="ServiceProviderOptions.ValidateScopes"/> on, exactly as a
    /// host builds it, so a caller-registered scoped converter captured by the process-lifetime factory throws at the
    /// first resolution rather than silently living for the process lifetime.
    /// </remarks>
    public class WhenSharingBodyConvertersAcrossScopes : Testing.Core.Context
    {
        private const string UnknownContentType = "application/unknown";
        private const string JsonContentType = "application/json";
        private const string TextPlainContentType = "text/plain";
        private const string CallerContentType = "application/x-caller";

        private static readonly System.Reflection.Assembly NoBrokeredMessageAssembly = typeof(IMessage).Assembly;

        // ------------------------------------------------------------------ fakes

        private sealed class ScopedStubBodyConverter : IBrokeredMessageBodyConverter
        {
            public string ContentType => "application/x-scoped-stub";
            public TBody Convert<TBody>(byte[] body) => throw new NotSupportedException();
            public byte[] Convert(object body) => throw new NotSupportedException();
            public string Stringify(byte[] body) => throw new NotSupportedException();
            public string Stringify(object body) => throw new NotSupportedException();
            public byte[] GetBytes(string body) => throw new NotSupportedException();
        }

        // ------------------------------------------------------------------ infrastructure

        // Builds the Chatter DI graph with optional registration hooks that run before and after AddMessageBrokers so
        // callers can prove last-registration-wins in both orders.
        private static ServiceProvider BuildProvider(Action<IServiceCollection> preRegister = null,
                                                     Action<IServiceCollection> postRegister = null,
                                                     bool validateOnBuild = false)
        {
            var services = BuildServices(preRegister, postRegister);
            return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = validateOnBuild });
        }

        private static IServiceCollection BuildServices(Action<IServiceCollection> preRegister, Action<IServiceCollection> postRegister)
        {
            var configuration = new ConfigurationBuilder().Build();
            var services = new ServiceCollection();

            services.AddLogging();
            services.AddSingleton<IMessagingInfrastructure>(BuildInMemoryInfrastructure());

            preRegister?.Invoke(services);

            services
                .AddChatterCqrs(configuration, NoBrokeredMessageAssembly)
                .AddMessageBrokers(
                    optionsBuilder: null,
                    receiverHandlerSourceBuilder: b => b.WithExplicitAssemblies(NoBrokeredMessageAssembly));

            postRegister?.Invoke(services);

            return services;
        }

        private static IServiceScope OpenScope(IServiceProvider provider)
            => provider.GetRequiredService<IServiceScopeFactory>().CreateScope();

        private static IMessagingInfrastructure BuildInMemoryInfrastructure()
        {
            var dispatcherFactory = new Mock<IMessagingInfrastructureDispatcherFactory>();
            dispatcherFactory.Setup(f => f.Create()).Returns(new Mock<IMessagingInfrastructureDispatcher>().Object);
            var receiverFactory = new Mock<IMessagingInfrastructureReceiverFactory>();
            receiverFactory.Setup(f => f.Create()).Returns(new Mock<IMessagingInfrastructureReceiver>().Object);
            return new MessagingInfrastructure(
                type: InMemoryMessagingInfrastructureProvider.InfrastructureType,
                receiveInfrastructure: receiverFactory.Object,
                dispatchInfrastructure: dispatcherFactory.Object);
        }

        private static IBrokeredMessageBodyConverter ConverterFor(string contentType)
        {
            var converter = new Mock<IBrokeredMessageBodyConverter>();
            converter.SetupGet(c => c.ContentType).Returns(contentType);
            return converter.Object;
        }

        // ------------------------------------------------------------------ shipped graph

        [Fact]
        public void FactoryDefault_ResolvesSameInstanceAcrossScopes()
        {
            using var provider = BuildProvider();
            using var sendingScope = OpenScope(provider);
            using var receivingScope = OpenScope(provider);

            var sendingFactory = sendingScope.ServiceProvider.GetRequiredService<IBodyConverterFactory>();
            var receivingFactory = receivingScope.ServiceProvider.GetRequiredService<IBodyConverterFactory>();

            ReferenceEquals(sendingFactory, receivingFactory).Should().BeTrue("a factory built per scope rebuilds its converter lookup for every message");
        }

        [Fact]
        public void FactoryDefault_ResolvesFromRootProviderUnderScopeValidation()
        {
            using var provider = BuildProvider();

            Action resolveFromRoot = () => provider.GetRequiredService<IBodyConverterFactory>();

            resolveFromRoot.Should().NotThrow("process-lifetime hosted services resolve the factory from the root provider");
        }

        [Fact]
        public void ShippedConvertersAndFallback_ResolveSameInstancesAcrossScopes()
        {
            using var provider = BuildProvider();
            using var sendingScope = OpenScope(provider);
            using var receivingScope = OpenScope(provider);

            var sendingConverters = sendingScope.ServiceProvider.GetServices<IBrokeredMessageBodyConverter>().ToArray();
            var receivingConverters = receivingScope.ServiceProvider.GetServices<IBrokeredMessageBodyConverter>().ToArray();
            var sendingFactory = sendingScope.ServiceProvider.GetRequiredService<IBodyConverterFactory>();
            var receivingFactory = receivingScope.ServiceProvider.GetRequiredService<IBodyConverterFactory>();

            sendingConverters.Should().HaveCount(2).And.Equal(receivingConverters, ReferenceEquals, "the shipped converters are stateless and live for the process lifetime");
            sendingFactory.CreateBodyConverter(JsonContentType).Should().BeSameAs(receivingFactory.CreateBodyConverter(JsonContentType));
            sendingFactory.CreateBodyConverter(TextPlainContentType).Should().BeSameAs(receivingFactory.CreateBodyConverter(TextPlainContentType));
            sendingFactory.CreateBodyConverter(UnknownContentType).Should().BeSameAs(receivingFactory.CreateBodyConverter(UnknownContentType), "the unknown-content-type fallback is shared, not allocated per message");
        }

        [Fact]
        public void ValidateOnBuild_ShippedGraphBuilds()
        {
            Action build = () => BuildProvider(validateOnBuild: true).Dispose();

            build.Should().NotThrow();
        }

        // ------------------------------------------------------------------ caller registrations

        [Fact]
        public void CallerSingletonConverterRegisteredBeforeAddMessageBrokers_WinsForItsContentType()
        {
            var callerConverter = ConverterFor(CallerContentType);
            using var provider = BuildProvider(preRegister: services => services.AddSingleton(callerConverter));
            using var scope = OpenScope(provider);

            scope.ServiceProvider.GetRequiredService<IBodyConverterFactory>().CreateBodyConverter(CallerContentType).Should().BeSameAs(callerConverter);
        }

        [Fact]
        public void CallerSingletonConverterRegisteredAfterAddMessageBrokers_WinsForApplicationJson()
        {
            var callerJsonConverter = ConverterFor(JsonContentType);
            using var provider = BuildProvider(postRegister: services => services.AddSingleton(callerJsonConverter));
            using var scope = OpenScope(provider);

            scope.ServiceProvider.GetRequiredService<IBodyConverterFactory>().CreateBodyConverter(JsonContentType).Should().BeSameAs(callerJsonConverter, "the last registered converter for a content type wins");
        }

        [Fact]
        public void CallerScopedConverter_ThrowsWhenFactoryResolvedUnderScopeValidation()
        {
            using var provider = BuildProvider(preRegister: services => services.AddScoped<IBrokeredMessageBodyConverter, ScopedStubBodyConverter>());
            using var scope = OpenScope(provider);

            Action resolveFactory = () => scope.ServiceProvider.GetRequiredService<IBodyConverterFactory>();

            resolveFactory.Should().Throw<InvalidOperationException>("a scoped converter captured by the process-lifetime factory is not supported")
                          .WithMessage("*IBodyConverterFactory*");
        }

        [Fact]
        public void ValidateOnBuild_CallerScopedConverterFailsBuild()
        {
            Action build = () => BuildProvider(preRegister: services => services.AddScoped<IBrokeredMessageBodyConverter, ScopedStubBodyConverter>(),
                                               validateOnBuild: true).Dispose();

            build.Should().Throw<AggregateException>();
        }
    }
}
