using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using System;
using Xunit;

namespace Chatter.CQRS.Tests.UsingMessageDispatcher
{
    public class WhenInitializing
    {
        private readonly Mock<IMessageDispatcherProvider> _messageDispatcherProvider;
        private readonly Mock<IExternalDispatcher> _externalDispatcher;

        public WhenInitializing()
        {
            _messageDispatcherProvider = new Mock<IMessageDispatcherProvider>();
            _externalDispatcher = new Mock<IExternalDispatcher>();
        }

        [Fact]
        public void MustThrowWhenMessageDispatcherProviderIsNull()
            => FluentActions.Invoking(() => new MessageDispatcher(null, _externalDispatcher.Object)).Should().ThrowExactly<ArgumentNullException>();

        [Fact]
        public void MustThrowWhenExternalDispatcherIsNull()
            => FluentActions.Invoking(() => new MessageDispatcher(_messageDispatcherProvider.Object, null)).Should().ThrowExactly<ArgumentNullException>();

        [Fact]
        public void MustThrowIfMessageDispatcherProviderHasNotBeenRegisteredWIthServiceCollection()
        {
            var sc = new ServiceCollection();
            sc.AddScoped<IMessageDispatcher, MessageDispatcher>();

            var sp = sc.BuildServiceProvider();
            FluentActions.Invoking(() => sp.GetRequiredService<IMessageDispatcher>()).Should().ThrowExactly<InvalidOperationException>()
                .WithMessage("Unable to resolve service for type*");
        }

        [Fact]
        public void MustThrowIfLoggerHasNotBeenRegisteredWithServiceCollection()
        {
            var sc = new ServiceCollection();
            sc.AddScoped(sp => _messageDispatcherProvider.Object);
            sc.AddScoped<IMessageDispatcher, MessageDispatcher>();

            var sp = sc.BuildServiceProvider();
            FluentActions.Invoking(() => sp.GetRequiredService<IMessageDispatcher>()).Should().ThrowExactly<InvalidOperationException>()
                .WithMessage("Unable to resolve service for type*");
        }

        [Fact]
        public void MustResolveFromServiceProviderIfAllRequiredDependenciesAreRegistered()
        {
            var sc = new ServiceCollection();
            sc.AddScoped(sp => _messageDispatcherProvider.Object);
            sc.AddScoped(sp => _externalDispatcher.Object);
            sc.AddScoped<IMessageDispatcher, MessageDispatcher>();

            var sp = sc.BuildServiceProvider();
            var concrete = sp.GetRequiredService<IMessageDispatcher>();
            concrete.Should().BeOfType(typeof(MessageDispatcher));
        }
    }
}
