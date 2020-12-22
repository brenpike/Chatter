using Chatter.MessageBrokers.Configuration;
using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Exceptions;
using Chatter.MessageBrokers.Receiving;
using Chatter.MessageBrokers.Recovery;
using Chatter.MessageBrokers.Recovery.CircuitBreaker;
using Chatter.MessageBrokers.SqlServiceBroker.Configuration;
using Chatter.MessageBrokers.SqlServiceBroker.Scripts;
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

        public SqlServiceBrokerReceiver(SqlServiceBrokerOptions options,
                                        MessageBrokerOptions messageBrokerOptions,
                                        ILogger<SqlServiceBrokerReceiver> logger,
                                        IBodyConverterFactory bodyConverterFactory,
                                        IFailedReceiveRecoverer failedReceiveRecoverer,
                                        ICriticalFailureNotifier criticalFailureNotifier,
                                        ICircuitBreaker circuitBreaker)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _logger = logger;
            _bodyConverterFactory = bodyConverterFactory;
            _failedReceiveRecoverer = failedReceiveRecoverer;
            _criticalFailureNotifier = criticalFailureNotifier;
            _transactionMode = messageBrokerOptions?.TransactionMode ?? TransactionMode.None;
            _localReceiverDeliveryAttempts = new ConcurrentDictionary<Guid, int>();
            _circuitBreaker = circuitBreaker;
        }

        public string TargetServiceName { get; private set; }
        public string QueueName { get; private set; }
        public string ErrorQueueName { get; private set; }

        //TODO: move error codes elsewhere
        public const int _poisonMessageDeadletterErrorCode = 100;
        public const int _recoveryActionDeadletterErrorCode = 200;
        public const int _failedRecoveryDeadletterErrorCode = 300;
        public const int _circuitBreakerDeadletterErrorCode = 400;

        public async Task StartReceiver(ReceiverOptions options, Func<MessageBrokerContext, TransactionContext, Task> inboundMessageHandler)
        {
            try
            {
                this.TargetServiceName = options.SendingPath;
                this.QueueName = options.MessageReceiverPath;
                this.ErrorQueueName = options.ErrorQueuePath;
                if (options.TransactionMode != null)
                {
                    _transactionMode = options.TransactionMode ?? TransactionMode.None;
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
                                             this.QueueName,
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
                    await _circuitBreaker.Execute(async cbState =>
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
                            _logger.LogError($"Error receiving sql service broker message from queue '{this.QueueName}'");
                            throw;
                        }

                        if (message == null || _cancellationSource.IsCancellationRequested)
                        {
                            return;
                        }

                        if (message?.Body == null || _cancellationSource.IsCancellationRequested)
                        {
                            await CompleteAsync(connection, transaction, message); //TODO: do we really want to complete this? what are our options?
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

                                var headers = new Dictionary<string, object>
                                {
                                    [SSBMessageContext.ConversationGroupId] = message.ConvGroupHandle,
                                    [SSBMessageContext.ConversationHandle] = message.ConvHandle,
                                    [SSBMessageContext.MessageSequenceNumber] = message.MessageSeqNo,
                                    [SSBMessageContext.ServiceName] = message.ServiceName,
                                    [SSBMessageContext.ServiceContractName] = message.ServiceContractName,
                                    [SSBMessageContext.MessageTypeName] = message.MessageTypeName,
                                    [MessageContext.InfrastructureType] = SSBMessageContext.InfrastructureType
                                };

                                messageContext = new MessageBrokerContext(message.ConvHandle.ToString(), message.Body, headers, this.QueueName, receiverTokenSource.Token, bodyConverter);
                                messageContext.Container.Include(message);

                                transactionContext = new TransactionContext(this.QueueName, _transactionMode);

                                if (_transactionMode == TransactionMode.FullAtomicityViaInfrastructure && transaction != null)
                                {
                                    transactionContext.Container.Include<IDbTransaction>(transaction);
                                }
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
                                await DeadLetterAsync(connection, transaction, message, _poisonMessageDeadletterErrorCode,
                                                      $"Poisoned message received from queue '{this.QueueName}' but was not handled.",
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

                                var failureContext = new FailureContext(messageContext.BrokeredMessage,
                                                                        this.ErrorQueueName,
                                                                        "Unable to handle received message",
                                                                        e,
                                                                        _localReceiverDeliveryAttempts[message.ConvHandle],
                                                                        transactionContext);

                                state = await _failedReceiveRecoverer.Execute(failureContext).ConfigureAwait(false);

                                if (state == RecoveryState.DeadLetter)
                                {
                                    await DeadLetterAsync(connection, transaction, message, _recoveryActionDeadletterErrorCode,
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

                                var failureContext = new FailureContext(messageContext.BrokeredMessage,
                                                                        this.ErrorQueueName,
                                                                        "Unable to recover from error which occurred during message handling",
                                                                        aggEx,
                                                                        _localReceiverDeliveryAttempts[message.ConvHandle],
                                                                        transactionContext);

                                await _criticalFailureNotifier.Notify(failureContext).ConfigureAwait(false);

                                await DeadLetterAsync(connection, transaction, message, _failedRecoveryDeadletterErrorCode,
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

        private async Task<SqlTransaction> CreateTransaction(SqlConnection connection)
            => (SqlTransaction)(_transactionMode != TransactionMode.None ? await connection.BeginTransactionAsync(_cancellationSource.Token) : null);

        private Task AbandonAsync(IDbTransaction transaction, ReceivedMessage message)
        {
            transaction?.Rollback();
            _localReceiverDeliveryAttempts.AddOrUpdate(message.ConvHandle, 1, (ch, deliveryAttempts) => deliveryAttempts + 1);
            return Task.CompletedTask;
        }

        private async Task CompleteAsync(SqlConnection connection, IDbTransaction transaction, ReceivedMessage message, int errorCode = 0, string errorDescription = "")
        {
            try
            {
                var edc = new EndDialogConversationCommand(connection,
                                                           message.ConvHandle,
                                                           errorCode,
                                                           errorDescription,
                                                           _options.CleanupOnEndConversation,
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

        private async Task DeadLetterAsync(SqlConnection connection, IDbTransaction transaction, ReceivedMessage message, int errorCode, string reason, string description)
        {
            //TOOO: Error Messages not going to queue? Need to set message type to Error?
            var errorDescription = reason + Environment.NewLine + description;
            await CompleteAsync(connection, transaction, message, errorCode, errorDescription).ConfigureAwait(false);
            _logger.LogError(errorDescription);
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
