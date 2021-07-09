using Chatter.CQRS.Queries;
using Chatter.Testing.Core.Creators.Common;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using System;
using Xunit;

namespace Chatter.CQRS.Tests.Queries.UsingQueryDispatcher
{
    public class WhenInitializing : Testing.Core.Context
    {
        private readonly Mock<IServiceProvider> _serviceProvider = new Mock<IServiceProvider>();
        private readonly LoggerCreator<QueryDispatcher> _logger;

        public WhenInitializing()
            => _logger = New.Common().Logger<QueryDispatcher>();

        [Fact]
        public void MustThrowWhenServiceProviderIsNull()
        {
            Action ctor = () => new QueryDispatcher(null, _logger.Creation);
            ctor.Should().Throw<ArgumentNullException>();
        }

        [Fact]
        public void MustThrowWhenLoggerIsNull()
        {
            Action ctor = () => new QueryDispatcher(_serviceProvider.Object, null);
            ctor.Should().Throw<ArgumentNullException>();
        }

        [Fact]
        public void MustNotThrowWhenServiceProviderAndLoggerHaveValue()
        {
            Action ctor = () => new QueryDispatcher(_serviceProvider.Object, _logger.Creation);
            ctor.Should().NotThrow<ArgumentNullException>();
            ctor.Should().NotThrow();
        }

        [Fact]
        public void MustThrowWhenServiceProviderAndLoggerAreNull()
        {
            Action ctor = () => new QueryDispatcher(null, null);
            ctor.Should().Throw<ArgumentNullException>();
        }

        [Fact]
        public void MustThrowIfLoggerHasNotBeenRegisteredWithServiceCollection()
        {
            var sc = new ServiceCollection();
            sc.AddScoped<IQueryDispatcher, QueryDispatcher>();

            var sp = sc.BuildServiceProvider();
            FluentActions.Invoking(() => sp.GetRequiredService<IQueryDispatcher>()).Should().ThrowExactly<InvalidOperationException>()
                .WithMessage("Unable to resolve service for type 'Microsoft.Extensions.Logging.ILogger*");
        }

        [Fact]
        public void MustResolveQueryDispatcherIfLoggingDependencyIsRegistered()
        {
            var sc = new ServiceCollection();
            sc.AddLogging();
            sc.AddScoped<IQueryDispatcher, QueryDispatcher>();

            var sp = sc.BuildServiceProvider();
            var concrete = sp.GetRequiredService<IQueryDispatcher>();
            concrete.Should().BeOfType(typeof(QueryDispatcher));
        }
    }
}
