using Chatter.MessageBrokers.Configuration;
using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Exceptions;
using Chatter.MessageBrokers.Receiving;
using Chatter.MessageBrokers.Recovery;
using Chatter.MessageBrokers.Recovery.CircuitBreaker;
using Chatter.MessageBrokers.Sending;
using Chatter.MessageBrokers.SqlServiceBroker.Configuration;
using Chatter.MessageBrokers.SqlServiceBroker.Scripts;
using Chatter.MessageBrokers.SqlServiceBroker.Sending;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Threading;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.SqlServiceBroker.Receiving
{
    public class SqlServiceBrokerReceiver : IMessagingInfrastructureReceiver
    {
        private readonly SqlServiceBrokerOptions _options;
        private readonly ILogger<SqlServiceBrokerReceiver> _logger;
        private readonly IBodyConverterFactory _bodyConverterFactory;
        private readonly IFailedReceiveRecoverer _failedReceiveRecoverer;
        private readonly ICriticalFailureNotifier _criticalFailureNotifier;
        private CancellationTokenSource _cancellationSource;
        private TransactionMode _transactionMode;
        private readonly ConcurrentDictionary<Guid, int> _localReceiverDeliveryAttempts;
        private readonly ICircuitBreaker _circuitBreaker;
        private readonly IServiceScopeFactory _serviceFactory;

        public SqlServiceBrokerReceiver(SqlServiceBrokerOptions options,
                                        MessageBrokerOptions messageBrokerOptions,
                                        ILogger<SqlServiceBrokerReceiver> logger,
                                        IBodyConverterFactory bodyConverterFactory,
                                        IFailedReceiveRecoverer failedReceiveRecoverer,
                                        ICriticalFailureNotifier criticalFailureNotifier,
                                        ICircuitBreaker circuitBreaker,
                                        IServiceScopeFactory serviceFactory)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _logger = logger;
            _bodyConverterFactory = bodyConverterFactory;
            _failedReceiveRecoverer = failedReceiveRecoverer;
            _criticalFailureNotifier = criticalFailureNotifier;
            _transactionMode = messageBrokerOptions?.TransactionMode ?? TransactionMode.None;
            _localReceiverDeliveryAttempts = new ConcurrentDictionary<Guid, int>();
            _circuitBreaker = circuitBreaker;
            _serviceFactory = serviceFactory;
        }

        public string SendingPath { get; private set; }
        public string MessageReceiverPath { get; private set; }
        public string ErrorQueueName { get; private set; }
        public string DeadLetterQueueName { get; private set; }

        //TODO: move error codes elsewhere
        public const int _poisonMessageDeadletterErrorCode = 100;
        public const int _recoveryActionDeadletterErrorCode = 200;
        public const int _failedRecoveryDeadletterErrorCode = 300;
        public const int _circuitBreakerDeadletterErrorCode = 400;

        public async Task StartReceiver(ReceiverOptions options, Func<MessageBrokerContext, TransactionContext, Task> inboundMessageHandler)
        {
            try
            {
                this.SendingPath = options.SendingPath;
                this.MessageReceiverPath = options.MessageReceiverPath;
                this.ErrorQueueName = options.ErrorQueuePath;
                this.DeadLetterQueueName = options.DeadLetterQueuePath;

                if (options.TransactionMode != null)
                {
                    _transactionMode = options.TransactionMode ?? TransactionMode.ReceiveOnly;
                }

                _cancellationSource = new CancellationTokenSource();

                await MessageReceiverLoop(inboundMessageHandler).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                _logger.LogCritical(e, $"Receiver stopped due to critical error");
            }
        }

        public Task StopReceiver()
        {
            Cancel();
            return Task.CompletedTask;
        }

        private void Cancel()
        {
            if (_cancellationSource == null || _cancellationSource.Token.IsCancellationRequested)
            {
                return;
            }

            if (!_cancellationSource.Token.CanBeCanceled)
            {
                return;
            }

            _cancellationSource.Cancel();
            _cancellationSource.Dispose();
        }

        private async Task<ReceivedMessage> ReceiveAsync(SqlConnection connection, SqlTransaction transaction)
        {
            var receiveMessageFromQueue = new ReceiveMessageFromQueueCommand(connection,
                                             this.MessageReceiverPath,
                                             _options.ReceiverTimeoutInMilliseconds,
                                             transaction: transaction);

            return await receiveMessageFromQueue.ExecuteAsync(_cancellationSource.Token).ConfigureAwait(false);
        }

        private async Task MessageReceiverLoop(Func<MessageBrokerContext, TransactionContext, Task> brokeredMessageHandler)
        {
            while (!_cancellationSource.IsCancellationRequested)
            {
                try
                {
                    await _circuitBreaker.Execute(async _ =>
                    {
                        using SqlConnection connection = new SqlConnection(_options.ConnectionString);
                        await connection.OpenAsync();
                        using SqlTransaction transaction = await CreateTransaction(connection).ConfigureAwait(false);

                        ReceivedMessage message = null;

                        try
                        {
                            message = await ReceiveAsync(connection, transaction).ConfigureAwait(false);
                        }
                        catch (Exception)
                        {
                            _logger.LogError($"Error receiving sql service broker message from queue '{this.MessageReceiverPath}'");
                            throw;
                        }

                        if (message == null || _cancellationSource.IsCancellationRequested)
                        {
                            return;
                        }

                        //check all message types and handle appropriately

                        if (message?.Body == null)
                        {
                            await CompleteAsync(connection, transaction, message).ConfigureAwait(false);
                            return;
                        }

                        MessageBrokerContext messageContext = null;
                        TransactionContext transactionContext = null;

                        try
                        {
                            using var receiverTokenSource = new CancellationTokenSource();
                            try
                            {
                                var bodyConverter = _bodyConverterFactory.CreateBodyConverter(_options.MessageBodyType);
                                var headers = CreateHeaders(message);

                                transactionContext = new TransactionContext(this.MessageReceiverPath, _transactionMode);

                                if (_transactionMode != TransactionMode.None && transaction != null)
                                {
                                    transactionContext.Container.Include<IDbTransaction>(transaction);
                                }

                                messageContext = new MessageBrokerContext(message.ConvHandle.ToString(), message.Body, headers, this.MessageReceiverPath, receiverTokenSource.Token, bodyConverter);
                                messageContext.Container.Include(message);

                                //throw new Exception("fake");
                            }
                            catch (Exception e)
                            {
                                throw new PoisonedMessageException($"Unable to build {typeof(MessageBrokerContext).Name} due to poisoned message", e);
                            }

                            await brokeredMessageHandler(messageContext, transactionContext).ConfigureAwait(false);

                            if (!receiverTokenSource.IsCancellationRequested)
                            {
                                await CompleteAsync(connection, transaction, message).ConfigureAwait(false);
                            }
                            else
                            {
                                await AbandonAsync(transaction, message).ConfigureAwait(false);
                            }
                        }
                        catch (PoisonedMessageException pme)
                        {
                            _logger?.LogError(pme, "Poisoned message received");
                            try
                            {
                                await DeadLetterAsync(connection, messageContext, transactionContext, message, _poisonMessageDeadletterErrorCode,
                                                      $"Poisoned message received from queue '{this.MessageReceiverPath}' cannot be handled.",
                                                      pme.Message).ConfigureAwait(false);
                            }
                            catch (Exception)
                            {
                                _logger?.LogError("Error deadlettering poisoned message");
                                throw;
                            }

                            return;
                        }
                        catch (Exception e)
                        {
                            try
                            {
                                _logger.LogError(e, "Error handling recevied message. Attempting recovery.");

                                RecoveryState state = RecoveryState.Retrying;

                                _localReceiverDeliveryAttempts.TryGetValue(message.ConvHandle, out var deliveryAttempts);

                                var failureContext = new FailureContext(messageContext.BrokeredMessage,
                                                                        this.ErrorQueueName,
                                                                        "Unable to handle received message",
                                                                        e,
                                                                        deliveryAttempts,
                                                                        transactionContext);

                                state = await _failedReceiveRecoverer.Execute(failureContext).ConfigureAwait(false);

                                if (state == RecoveryState.DeadLetter)
                                {
                                    await DeadLetterAsync(connection, messageContext, transactionContext, message, _recoveryActionDeadletterErrorCode,
                                                          $"Deadlettering message by request of recovery action.",
                                                          $"Conversation Handle: '{message.ConvHandle}, Conversation Group Id: '{message.ConvGroupHandle}'").ConfigureAwait(false);
                                }

                                if (state == RecoveryState.RecoveryActionExecuted)
                                {
                                    await CompleteAsync(connection, transaction, message).ConfigureAwait(false);
                                }

                                if (state == RecoveryState.Retrying)
                                {
                                    await AbandonAsync(transaction, message).ConfigureAwait(false);
                                }
                            }
                            catch (Exception onErrorException)
                            {
                                var aggEx = new AggregateException(e, onErrorException);

                                _logger.LogError(aggEx, $"Recovery was unsuccessful. Conversation Handle: '{message.ConvHandle}, Conversation Group Id: '{message.ConvGroupHandle}'");

                                _localReceiverDeliveryAttempts.TryGetValue(message.ConvHandle, out var deliveryAttempts);

                                var failureContext = new FailureContext(messageContext.BrokeredMessage,
                                                                        this.ErrorQueueName,
                                                                        "Unable to recover from error which occurred during message handling",
                                                                        aggEx,
                                                                        deliveryAttempts,
                                                                        transactionContext);

                                await _criticalFailureNotifier.Notify(failureContext).ConfigureAwait(false);

                                await DeadLetterAsync(connection, messageContext, transactionContext, message, _failedRecoveryDeadletterErrorCode,
                                                      $"Critical error encountered receiving message. Conversation Handle: '{message.ConvHandle}, Conversation Group Id: '{message.ConvGroupHandle}'",
                                                      aggEx.ToString()).ConfigureAwait(false);
                            }
                        }
                    }, _cancellationSource.Token);
                }
                catch (ArgumentException ae)
                {
                    _logger.LogCritical(ae, $"Stopping {nameof(MessageReceiverLoop)}. SQL Connection string supplied is invalid. {nameof(_options.ConnectionString)}='{_options.ConnectionString}'");
                    return;
                }
                catch (Exception e)
                {
                    _logger.LogError(e, $"Error occurred in {nameof(MessageReceiverLoop)}.");
                }
            }
        }

        private IDictionary<string, object> CreateHeaders(ReceivedMessage message)
        {
            return new Dictionary<string, object>
            {
                [SSBMessageContext.ConversationGroupId] = message.ConvGroupHandle,
                [SSBMessageContext.ConversationHandle] = message.ConvHandle,
                [SSBMessageContext.MessageSequenceNumber] = message.MessageSeqNo,
                [SSBMessageContext.ServiceName] = message.ServiceName,
                [SSBMessageContext.ServiceContractName] = message.ServiceContractName,
                [SSBMessageContext.MessageTypeName] = message.MessageTypeName,
                [MessageContext.InfrastructureType] = SSBMessageContext.InfrastructureType
            };
        }

        private async Task<SqlTransaction> CreateTransaction(SqlConnection connection)
            => (SqlTransaction)(_transactionMode != TransactionMode.None ? await connection.BeginTransactionAsync(_cancellationSource.Token) : null);

        private Task AbandonAsync(IDbTransaction transaction, ReceivedMessage message)
        {
            transaction?.Rollback();
            _localReceiverDeliveryAttempts.AddOrUpdate(message.ConvHandle, 1, (ch, deliveryAttempts) => deliveryAttempts + 1);
            return Task.CompletedTask;
        }

        private async Task CompleteAsync(SqlConnection connection, IDbTransaction transaction, ReceivedMessage message)
        {
            try
            {
                var edc = new EndDialogConversationCommand(connection,
                                                           message.ConvHandle,
                                                           enableCleanup: _options.CleanupOnEndConversation,
                                                           transaction: (SqlTransaction)transaction);
                await edc.ExecuteAsync(_cancellationSource.Token).ConfigureAwait(false);
                transaction?.Commit();
                _localReceiverDeliveryAttempts.TryRemove(message.ConvHandle, out var _);
            }
            catch (Exception e)
            {
                await AbandonAsync(transaction, message).ConfigureAwait(false);
                _logger.LogError(e, "Unable to complete receive operation");
            }
        }

        private async Task DeadLetterAsync(SqlConnection connection, MessageBrokerContext messageContext, TransactionContext transactionContext, ReceivedMessage message, int errorCode, string reason, string description)
        {
            var errorDescription = reason + Environment.NewLine + description;
            var contextTransactionMode = transactionContext?.TransactionMode ?? TransactionMode.None;
            IDbTransaction transaction = null;
            transactionContext?.Container.TryGet(out transaction);
            try
            {
                var edc = new EndDialogConversationCommand(connection,
                                                           message.ConvHandle,
                                                           enableCleanup: _options.CleanupOnEndConversation,
                                                           transaction: (SqlTransaction)transaction);
                await edc.ExecuteAsync(_cancellationSource.Token).ConfigureAwait(false);

                using var scope = _serviceFactory.CreateScope();
                var ssbSender = scope.ServiceProvider.GetRequiredService<SqlServiceBrokerSender>();
                var bodyConverter = _bodyConverterFactory.CreateBodyConverter(_options.MessageBodyType);
                var headers = new Dictionary<string, object>()
                {
                    [SSBMessageContext.ServiceName] = null,
                    [MessageContext.FailureDescription] = errorDescription,
                    [MessageContext.FailureDetails] = $"{reason} (code: {errorCode})",
                    [MessageContext.InfrastructureType] = SSBMessageContext.InfrastructureType
                };

                await ssbSender.Dispatch(new OutboundBrokeredMessage(Guid.NewGuid().ToString(), message.Body, headers, this.DeadLetterQueueName, bodyConverter), transactionContext);

                if (contextTransactionMode != TransactionMode.None && transaction != null)
                {
                    transaction?.Commit();
                }
            }
            catch (Exception e)
            {
                await AbandonAsync(transaction, message).ConfigureAwait(false);
                _logger.LogError(e, "Unable to complete receive operation");
            }

            _localReceiverDeliveryAttempts.TryRemove(message.ConvHandle, out var _);
            _logger.LogError("Message successfully deadlettered:" + errorDescription + Environment.NewLine + $"{reason} (code: {errorCode})");
        }

        public Task<MessageBrokerContext> ReceiveMessageAsync(TransactionContext transactionContext, CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }

        public Task InitializeAsync(ReceiverOptions options, CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }

        public Task AckMessageAsync(MessageBrokerContext context, TransactionContext transactionContext, CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }

        public Task NackMessageAsync(MessageBrokerContext context, TransactionContext transactionContext, CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }

        public Task DeadletterMessageAsync(MessageBrokerContext context, TransactionContext transactionContext, string deadLetterReason, string deadLetterErrorDescription, CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }

        public IDisposable BeginTransaction(TransactionContext transactionContext)
        {
            throw new NotImplementedException();
        }

        public void RollbackTransaction(TransactionContext transactionContext)
        {
            throw new NotImplementedException();
        }

        public void CompleteTransaction(TransactionContext transactionContext)
        {
            throw new NotImplementedException();
        }

        public Task<int> CurrentMessageDeliveryCountAsync(MessageBrokerContext context, CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }


        public void Dispose()
        {
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }

        public async ValueTask DisposeAsync()
        {
            await Task.CompletedTask;
            Cancel();
            Dispose(disposing: false);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                Cancel();
            }

            _cancellationSource = null;
        }

    }
}
