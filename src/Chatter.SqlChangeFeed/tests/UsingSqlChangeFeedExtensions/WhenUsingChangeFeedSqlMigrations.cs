using Chatter.SqlChangeFeed.Configuration;
using Chatter.SqlChangeFeed.DependencyInjection;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Chatter.SqlChangeFeed.Tests.UsingSqlChangeFeedExtensions
{
    /// <summary>
    /// Pins the deprecated synchronous Change Feed Migration overloads: they must bridge to the
    /// asynchronous installation without deadlocking under a non-pumping ambient
    /// <see cref="SynchronizationContext"/>, while preserving their observable contract - the same
    /// <see cref="IServiceProvider"/> back, the seven derived object names in signature order, the
    /// supplied <see cref="CancellationToken"/>, and unwrapped exception propagation.
    /// </summary>
    public class WhenUsingChangeFeedSqlMigrations : Testing.Core.Context
    {
        private const string ExpectedInstallProcName = "Chatter_InstallChangeFeed_FakeRowData";
        private const string ExpectedUninstallProcName = "Chatter_UninstallChangeFeed_FakeRowData";
        private const string ExpectedQueueName = "Chatter_Queue_FakeRowData";
        private const string ExpectedServiceName = "Chatter_Service_FakeRowData";
        private const string ExpectedTriggerName = "Chatter_ChangeFeedTrigger_FakeRowData";
        private const string ExpectedDeadLetterQueueName = "Chatter_DeadLetterQueue_FakeRowData";
        private const string ExpectedDeadLetterServiceName = "Chatter_DeadLetterService_FakeRowData";

        [Fact]
        public void MustNotDeadlockWhenAmbientSynchronizationContextNeverPumpsContinuations()
        {
            var manager = new RecordingSqlDependencyManager(installationGate: DeferredInstallationGate());
            var provider = CreateProvider(manager);
            Exception observedFailure = null;

            var migrationThread = new Thread(() =>
            {
                SynchronizationContext.SetSynchronizationContext(new NonPumpingSynchronizationContext());
                try
                {
#pragma warning disable CS0618 // the deprecated synchronous overload is the subject under test
                    provider.UseChangeFeedSqlMigrations(typeof(FakeRowData));
#pragma warning restore CS0618
                }
                catch (Exception failure)
                {
                    observedFailure = failure;
                }
            });
            migrationThread.IsBackground = true;

            migrationThread.Start();

            migrationThread.Join(TimeSpan.FromSeconds(10))
                .Should().BeTrue("the synchronous bridge must not block on a continuation posted to a context it is itself blocking");
            observedFailure.Should().BeNull();
        }

        [Fact]
        public void MustReturnTheServiceProviderItWasInvokedOn()
        {
            var provider = CreateProvider(new RecordingSqlDependencyManager());

#pragma warning disable CS0618 // the deprecated synchronous overload is the subject under test
            var returned = provider.UseChangeFeedSqlMigrations(typeof(FakeRowData));
#pragma warning restore CS0618

            returned.Should().BeSameAs(provider);
        }

        [Fact]
        public void MustReturnTheServiceProviderItWasInvokedOnFromGenericOverload()
        {
            var provider = CreateProvider(new RecordingSqlDependencyManager());

#pragma warning disable CS0618 // the deprecated synchronous overload is the subject under test
            var returned = provider.UseChangeFeedSqlMigrations<FakeRowData>();
#pragma warning restore CS0618

            returned.Should().BeSameAs(provider);
        }

        [Fact]
        public void MustPassDerivedObjectNamesInSignatureOrder()
        {
            var manager = new RecordingSqlDependencyManager();

#pragma warning disable CS0618 // the deprecated synchronous overload is the subject under test
            CreateProvider(manager).UseChangeFeedSqlMigrations(typeof(FakeRowData));
#pragma warning restore CS0618

            manager.InstalledObjectNames.Should().Equal(
                ExpectedInstallProcName,
                ExpectedUninstallProcName,
                ExpectedQueueName,
                ExpectedServiceName,
                ExpectedTriggerName,
                ExpectedDeadLetterQueueName,
                ExpectedDeadLetterServiceName);
        }

        [Fact]
        public void MustDeriveObjectNamesFromTypeArgumentOfGenericOverload()
        {
            var manager = new RecordingSqlDependencyManager();

#pragma warning disable CS0618 // the deprecated synchronous overload is the subject under test
            CreateProvider(manager).UseChangeFeedSqlMigrations<FakeRowData>();
#pragma warning restore CS0618

            manager.InstalledObjectNames.Should().Equal(
                ExpectedInstallProcName,
                ExpectedUninstallProcName,
                ExpectedQueueName,
                ExpectedServiceName,
                ExpectedTriggerName,
                ExpectedDeadLetterQueueName,
                ExpectedDeadLetterServiceName);
        }

        [Fact]
        public void MustPassSuppliedCancellationTokenToTheDependencyManager()
        {
            var manager = new RecordingSqlDependencyManager();
            using var tokenSource = new CancellationTokenSource();

#pragma warning disable CS0618 // the deprecated synchronous overload is the subject under test
            CreateProvider(manager).UseChangeFeedSqlMigrations(typeof(FakeRowData), tokenSource.Token);
#pragma warning restore CS0618

            manager.ObservedToken.Should().Be(tokenSource.Token);
        }

        [Fact]
        public void MustPropagateInstallationFailureUnwrapped()
        {
            var manager = new RecordingSqlDependencyManager(installationFailure: new InvalidOperationException("installation failed"));
            var provider = CreateProvider(manager);

#pragma warning disable CS0618 // the deprecated synchronous overload is the subject under test
            FluentActions.Invoking(() => provider.UseChangeFeedSqlMigrations(typeof(FakeRowData)))
#pragma warning restore CS0618
                .Should().ThrowExactly<InvalidOperationException>()
                .WithMessage("installation failed");
        }

        [Fact]
        public void MustPropagateCancellationOfAnAlreadyCancelledToken()
        {
            var provider = CreateProvider(new RecordingSqlDependencyManager());
            using var tokenSource = new CancellationTokenSource();
            tokenSource.Cancel();

#pragma warning disable CS0618 // the deprecated synchronous overload is the subject under test
            FluentActions.Invoking(() => provider.UseChangeFeedSqlMigrations(typeof(FakeRowData), tokenSource.Token))
#pragma warning restore CS0618
                .Should().Throw<OperationCanceledException>();
        }

        [Fact]
        public void MustMarkGenericSynchronousOverloadObsoleteAsAWarning()
        {
            var obsolete = SynchronousOverload(genericDefinition: true).GetCustomAttribute<ObsoleteAttribute>();

            obsolete.Should().NotBeNull();
            obsolete.IsError.Should().BeFalse();
            obsolete.Message.Should().Contain("UseChangeFeedSqlMigrationsAsync");
        }

        [Fact]
        public void MustMarkNonGenericSynchronousOverloadObsoleteAsAWarning()
        {
            var obsolete = SynchronousOverload(genericDefinition: false).GetCustomAttribute<ObsoleteAttribute>();

            obsolete.Should().NotBeNull();
            obsolete.IsError.Should().BeFalse();
            obsolete.Message.Should().Contain("UseChangeFeedSqlMigrationsAsync");
        }

        [Fact]
        public void MustLeaveGenericAsynchronousOverloadUnmarked()
            => AsynchronousOverload(genericDefinition: true).GetCustomAttribute<ObsoleteAttribute>().Should().BeNull();

        [Fact]
        public void MustLeaveNonGenericAsynchronousOverloadUnmarked()
            => AsynchronousOverload(genericDefinition: false).GetCustomAttribute<ObsoleteAttribute>().Should().BeNull();

        private static MethodInfo SynchronousOverload(bool genericDefinition)
            => typeof(SqlChangeFeedExtensions).GetMethods()
                .Single(m => m.Name == "UseChangeFeedSqlMigrations" && m.IsGenericMethodDefinition == genericDefinition);

        private static MethodInfo AsynchronousOverload(bool genericDefinition)
            => typeof(SqlChangeFeedExtensions).GetMethods()
                .Single(m => m.Name == "UseChangeFeedSqlMigrationsAsync" && m.IsGenericMethodDefinition == genericDefinition);

        private static IServiceProvider CreateProvider(ISqlDependencyManager<FakeRowData> dependencyManager)
        {
            var scopedProvider = new Mock<IServiceProvider>();
            scopedProvider.Setup(p => p.GetService(typeof(ISqlDependencyManager<FakeRowData>))).Returns(dependencyManager);

            var scope = new Mock<IServiceScope>();
            scope.SetupGet(s => s.ServiceProvider).Returns(scopedProvider.Object);

            var scopeFactory = new Mock<IServiceScopeFactory>();
            scopeFactory.Setup(f => f.CreateScope()).Returns(scope.Object);

            var provider = new Mock<IServiceProvider>();
            provider.Setup(p => p.GetService(typeof(IServiceScopeFactory))).Returns(scopeFactory.Object);

            return provider.Object;
        }

        // INVARIANT: a task still incomplete when the fake's await observes it, completed later from a background
        // thread that never touches the ambient SynchronizationContext. A task already complete at await time
        // registers no continuation, so it would never exercise the context capture the deadlock depends on.
        private static Task DeferredInstallationGate()
        {
            var completionSource = new TaskCompletionSource<object>();
            Task.Run(() =>
            {
                Thread.Sleep(50);
                completionSource.SetResult(null);
            });
            return completionSource.Task;
        }

        // INVARIANT: models a single-threaded context whose only pump thread is the one blocked inside the
        // synchronous bridge - every posted continuation is queued and never executed.
        private sealed class NonPumpingSynchronizationContext : SynchronizationContext
        {
            private readonly ConcurrentQueue<Tuple<SendOrPostCallback, object>> _neverPumped =
                new ConcurrentQueue<Tuple<SendOrPostCallback, object>>();

            public override void Post(SendOrPostCallback d, object state) => _neverPumped.Enqueue(Tuple.Create(d, state));
        }

        // INVARIANT: hand-written rather than a Moq setup because the context capture under test happens inside the
        // awaiting method body - awaiting the gate WITHOUT ConfigureAwait(false) is what posts the continuation back
        // to the ambient SynchronizationContext, and no mock configuration can express that.
        private sealed class RecordingSqlDependencyManager : ISqlDependencyManager<FakeRowData>
        {
            private readonly Task _installationGate;
            private readonly Exception _installationFailure;

            public RecordingSqlDependencyManager(Task installationGate = null, Exception installationFailure = null)
            {
                _installationGate = installationGate ?? Task.CompletedTask;
                _installationFailure = installationFailure;
            }

            public SqlChangeFeedOptions Options { get; } = new SqlChangeFeedOptions("connection-string", "database", "table");

            public string[] InstalledObjectNames { get; private set; }

            public CancellationToken ObservedToken { get; private set; }

            public async Task InstallSqlDependencies(string installationProcedureName = "",
                                                     string uninstallationProcedureName = "",
                                                     string conversationQueueName = "",
                                                     string conversationServiceName = "",
                                                     string conversationTriggerName = "",
                                                     string deadLetterQueueName = "",
                                                     string deadLetterServiceName = "",
                                                     CancellationToken token = default)
            {
                token.ThrowIfCancellationRequested();

                InstalledObjectNames = new[]
                {
                    installationProcedureName,
                    uninstallationProcedureName,
                    conversationQueueName,
                    conversationServiceName,
                    conversationTriggerName,
                    deadLetterQueueName,
                    deadLetterServiceName
                };
                ObservedToken = token;

                await _installationGate;

                if (_installationFailure != null)
                {
                    throw _installationFailure;
                }
            }

            public Task UninstallSqlDependencies(string uninstallationProcedureName = "", CancellationToken token = default)
                => Task.CompletedTask;
        }
    }
}
