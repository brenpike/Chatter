using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using System;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Reliability.EntityFramework.Tests.Support
{
    public enum TransactionFaultPhase
    {
        Commit,
        Rollback,
        Dispose
    }

    [Flags]
    public enum TransactionFaults
    {
        None = 0,
        Commit = 1,
        Rollback = 2,
        Dispose = 4
    }

    /// <summary>
    /// Raised by <see cref="FaultingRelationalTransaction"/> in place of the phase it was configured to fail.
    /// Deliberately not an <see cref="InvalidOperationException"/>: the tests assert that an operation's own
    /// <see cref="InvalidOperationException"/> reaches the caller, which a shared base type would not prove.
    /// </summary>
    public sealed class TransactionFaultException : Exception
    {
        public TransactionFaultException(TransactionFaultPhase phase)
            : base($"The transaction's {phase} phase was configured to fail.")
            => Phase = phase;

        public TransactionFaultPhase Phase { get; }
    }

    /// <summary>
    /// Base for the relational transaction factories that make a chosen terminal phase - commit, rollback or
    /// dispose - fail against a real relational provider. Installed with
    /// <c>optionsBuilder.ReplaceService&lt;IRelationalTransactionFactory, ...&gt;()</c>. Each fault combination
    /// gets its own named subclass because <c>ReplaceService</c> takes a type, not an instance, so there is
    /// nowhere to hand a configuration value in.
    /// </summary>
    public abstract class FaultingRelationalTransactionFactory : RelationalTransactionFactory
    {
        protected FaultingRelationalTransactionFactory(RelationalTransactionFactoryDependencies dependencies)
            : base(dependencies)
        { }

        protected abstract TransactionFaults Faults { get; }

        public override RelationalTransaction Create(
            IRelationalConnection connection,
            DbTransaction transaction,
            Guid transactionId,
            IDiagnosticsLogger<DbLoggerCategory.Database.Transaction> logger,
            bool transactionOwned)
            => new FaultingRelationalTransaction(
                connection,
                transaction,
                transactionId,
                logger,
                transactionOwned,
                Dependencies.SqlGenerationHelper,
                Faults);
    }

    public sealed class FaultingRelationalTransaction : RelationalTransaction
    {
        private readonly TransactionFaults _faults;
        private bool _disposeFaultRaised;

        public FaultingRelationalTransaction(
            IRelationalConnection connection,
            DbTransaction transaction,
            Guid transactionId,
            IDiagnosticsLogger<DbLoggerCategory.Database.Transaction> logger,
            bool transactionOwned,
            ISqlGenerationHelper sqlGenerationHelper,
            TransactionFaults faults)
            : base(connection, transaction, transactionId, logger, transactionOwned, sqlGenerationHelper)
            => _faults = faults;

        public override Task CommitAsync(CancellationToken cancellationToken = default)
            => _faults.HasFlag(TransactionFaults.Commit)
                ? throw new TransactionFaultException(TransactionFaultPhase.Commit)
                : base.CommitAsync(cancellationToken);

        public override Task RollbackAsync(CancellationToken cancellationToken = default)
            => _faults.HasFlag(TransactionFaults.Rollback)
                ? throw new TransactionFaultException(TransactionFaultPhase.Rollback)
                : base.RollbackAsync(cancellationToken);

        // INVARIANT: the dispose fault is raised after the base class has detached the underlying transaction, and
        // only the first time. A dispose that both failed and left the connection's transaction attached would make
        // the context's own teardown throw too, which would mask the behavior under test rather than expose it.
        public override async ValueTask DisposeAsync()
        {
            await base.DisposeAsync().ConfigureAwait(false);

            if (!_faults.HasFlag(TransactionFaults.Dispose) || _disposeFaultRaised)
            {
                return;
            }

            _disposeFaultRaised = true;
            throw new TransactionFaultException(TransactionFaultPhase.Dispose);
        }
    }

    /// <summary>
    /// Fails the commit alone, leaving the rollback working, so a test can observe what a unit of work does after
    /// its commit failed rather than what it does when its recovery also fails.
    /// </summary>
    public sealed class CommitFaultingRelationalTransactionFactory : FaultingRelationalTransactionFactory
    {
        public CommitFaultingRelationalTransactionFactory(RelationalTransactionFactoryDependencies dependencies)
            : base(dependencies)
        { }

        protected override TransactionFaults Faults => TransactionFaults.Commit;
    }

    public sealed class RollbackFaultingRelationalTransactionFactory : FaultingRelationalTransactionFactory
    {
        public RollbackFaultingRelationalTransactionFactory(RelationalTransactionFactoryDependencies dependencies)
            : base(dependencies)
        { }

        protected override TransactionFaults Faults => TransactionFaults.Rollback;
    }

    public sealed class DisposeFaultingRelationalTransactionFactory : FaultingRelationalTransactionFactory
    {
        public DisposeFaultingRelationalTransactionFactory(RelationalTransactionFactoryDependencies dependencies)
            : base(dependencies)
        { }

        protected override TransactionFaults Faults => TransactionFaults.Dispose;
    }

    public sealed class RollbackAndDisposeFaultingRelationalTransactionFactory : FaultingRelationalTransactionFactory
    {
        public RollbackAndDisposeFaultingRelationalTransactionFactory(RelationalTransactionFactoryDependencies dependencies)
            : base(dependencies)
        { }

        protected override TransactionFaults Faults => TransactionFaults.Rollback | TransactionFaults.Dispose;
    }

    public sealed class CommitAndRollbackFaultingRelationalTransactionFactory : FaultingRelationalTransactionFactory
    {
        public CommitAndRollbackFaultingRelationalTransactionFactory(RelationalTransactionFactoryDependencies dependencies)
            : base(dependencies)
        { }

        protected override TransactionFaults Faults => TransactionFaults.Commit | TransactionFaults.Rollback;
    }
}
