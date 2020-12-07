using Chatter.MessageBrokers.Configuration;
using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Exceptions;
using Chatter.MessageBrokers.Receiving;
using Chatter.MessageBrokers.Recovery;
using Chatter.MessageBrokers.SqlServiceBroker.Configuration;
using Chatter.MessageBrokers.SqlServiceBroker.Scripts.ServiceBroker.Core;
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
        private const int _receiveTimeoutInMilliseconds = 60000;
        private readonly SqlServiceBrokerOptions _options;
        private readonly ILogger<SqlServiceBrokerReceiver> _logger;
        private readonly IBodyConverterFactory _bodyConverterFactory;
        private readonly IFailedReceiveRecoverer _failedReceiveRecoverer;
        private readonly ICriticalFailureNotifier _criticalFailureNotifier;
        private CancellationTokenSource _cancellationSource;
        private readonly string _contentType;
        private TransactionMode _transactionMode;
        private readonly ConcurrentDictionary<Guid, int> _localReceiverDeliveryAttempts;

        public SqlServiceBrokerReceiver(SqlServiceBrokerOptions options,
                        MessageBrokerOptions messageBrokerOptions,
                        ILogger<SqlServiceBrokerReceiver> logger,
                        IBodyConverterFactory bodyConverterFactory,
                        IFailedReceiveRecoverer failedReceiveRecoverer,
                        ICriticalFailureNotifier criticalFailureNotifier)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _logger = logger;
            _bodyConverterFactory = bodyConverterFactory;
            _failedReceiveRecoverer = failedReceiveRecoverer;
            _criticalFailureNotifier = criticalFailureNotifier;
            _contentType = "application/json"; //TODO: move this to SqlServiceBrokerOptions
            _transactionMode = messageBrokerOptions?.TransactionMode ?? TransactionMode.None;
            _localReceiverDeliveryAttempts = new ConcurrentDictionary<Guid, int>();
        }

        public string QueueName { get; private set; }
        public string TargetServiceName { get; private set; }
        public string ErrorQueueName { get; private set; }

        public const int _poisonMessageDeadletterErrorCode = 100;
        public const int _recoveryActionDeadletterErrorCode = 200;
        public const int _failedRecoveryDeadletterErrorCode = 300;
        public const int _circuitBreakerDeadletterErrorCode = 400;

        public async Task StartReceiver(ReceiverOptions options, Func<MessageBrokerContext, TransactionContext, Task> inboundMessageHandler)
        {
            try
            {
                this.QueueName = options.SendingPath;
                this.TargetServiceName = options.MessageReceiverPath;
                this.ErrorQueueName = options.ErrorQueuePath;
                if (options.TransactionMode != null)
                {
                    _transactionMode = options.TransactionMode ?? TransactionMode.None;
                }

                _cancellationSource = new CancellationTokenSource();

                await ReceiveLoop(inboundMessageHandler).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                _logger.LogCritical($"Receiver stopped due to critical error: {e.Message}{Environment.NewLine}{e.StackTrace}");
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
                                             _options.SchemaName,
                                             _receiveTimeoutInMilliseconds / 2,
                                             transaction: transaction);

            var message = await receiveMessageFromQueue.ExecuteAsync(_cancellationSource.Token);

            if (message == null || _cancellationSource.IsCancellationRequested)
            {
                return null;
            }

            return message;
        }

        private async Task ReceiveLoop(Func<MessageBrokerContext, TransactionContext, Task> brokeredMessageHandler)
        {
            while (!_cancellationSource.IsCancellationRequested)
            {
                try
                {
                    using SqlConnection connection = new SqlConnection(_options.ConnectionString);
                    await connection.OpenAsync();
                    SqlTransaction transaction = await CreateTransaction(connection).ConfigureAwait(false);

                    ReceivedMessage message = null;

                    try
                    {
                        message = await ReceiveAsync(connection, transaction).ConfigureAwait(false);
                    }
                    catch (Exception e)
                    {
                        //TODO: circuit breaker - if receive fails (i.e. poison message handling is enabled and queue can be auto disabled, receive will fail forever)
                        //                        if circuit breaker triggers, we want to deadletter (Deadletterto end convo with error message type. use _circuitBreakerDeadletterErrorCode
                        //                        POSSIBLY ADD CIRCUIT BREAKER TO ABANDONASYNC???
                        _logger.LogError($"Error receiving sql service broker message from queue '{this.QueueName}': {e.Message}{Environment.NewLine}{e.StackTrace}");
                    }

                    try
                    {
                        if (message?.Message == null || _cancellationSource.IsCancellationRequested)
                        {
                            continue;
                        }

                        MessageBrokerContext messageContext = null;
                        TransactionContext transactionContext = null;

                        try
                        {
                            using var receiverTokenSource = new CancellationTokenSource();
                            try
                            {
                                var bodyConverter = _bodyConverterFactory.CreateBodyConverter(_contentType);

                                var headers = new Dictionary<string, object>
                                {
                                    [SSBMessageContext.ConversationGroupId] = message.ConvGroupHandle,
                                    [SSBMessageContext.ConversationHandle] = message.ConvHandle,
                                    [SSBMessageContext.MessageSequenceNumber] = message.MessageSeqNo,
                                    [SSBMessageContext.ServiceName] = message.ServiceName,
                                    [SSBMessageContext.ServiceContractName] = message.ServiceContractName,
                                    [SSBMessageContext.MessageTypeName] = message.MessageTypeName
                                };

                                messageContext = new MessageBrokerContext(message.ConvHandle.ToString(), message.Message, headers, this.TargetServiceName, receiverTokenSource.Token, bodyConverter);
                                messageContext.Container.Include(message);

                                transactionContext = new TransactionContext(this.TargetServiceName, _transactionMode);

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
                            try
                            {
                                await DeadLetterAsync(connection, transaction, message, _poisonMessageDeadletterErrorCode,
                                                      $"Poisoned message received from queue: '{this.QueueName}' (target service: {this.TargetServiceName}) but was not handled.",
                                                      pme.Message).ConfigureAwait(false);
                            }
                            catch (Exception deadLetterException)
                            {
                                _logger?.LogError($"Error deadlettering message: {pme.Message}{Environment.NewLine}{deadLetterException.StackTrace}");
                            }

                            continue;
                        }
                        catch (Exception e)
                        {
                            try
                            {
                                _logger.LogError($"Error handling recevied message. Conversation Handle: '{message.ConvHandle}, Conversation Group Id: '{message.ConvGroupHandle}'"
                                                    + "\n"
                                                    + $"{e}");

                                RecoveryState state = RecoveryState.Retrying;

                                var failureContext = new FailureContext(messageContext.BrokeredMessage,
                                                                        this.ErrorQueueName,
                                                                        "Unable to handle received message",
                                                                        e.StackTrace,
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

                                _logger.LogError($"Critical error encountered receiving message. Conversation Handle: '{message.ConvHandle}, Conversation Group Id: '{message.ConvGroupHandle}'"
                                               + "\n"
                                               + $"{onErrorException.StackTrace}");

                                var failureContext = new FailureContext(messageContext.BrokeredMessage,
                                                                        this.ErrorQueueName,
                                                                        "Unable to recover from error which occurred during message handling",
                                                                        aggEx.ToString(),
                                                                        _localReceiverDeliveryAttempts[message.ConvHandle],
                                                                        transactionContext);

                                await _criticalFailureNotifier.Notify(failureContext).ConfigureAwait(false);

                                await DeadLetterAsync(connection, transaction, message, _failedRecoveryDeadletterErrorCode,
                                                      $"Critical error encountered receiving message. Conversation Handle: '{message.ConvHandle}, Conversation Group Id: '{message.ConvGroupHandle}'",
                                                      onErrorException.StackTrace).ConfigureAwait(false);
                            }
                        }
                    }
                    catch (Exception e)
                    {
                        _logger.LogCritical($"Error processing {typeof(ReceivedMessage).Name} with conversation handle '{message.ConvHandle}' and conversation group id '{message.ConvGroupHandle}': {e.StackTrace}");
                    }
                }
                catch (Exception e)
                {
                    //TODO: circuit breaker - invalid connection string could cause infinite loop
                    _logger.LogError($"Unable to create/open {typeof(SqlConnection).Name}: {e.Message}{Environment.NewLine}{e.StackTrace}");
                }
            }
        }

        private async Task<SqlTransaction> CreateTransaction(SqlConnection connection)
            => (SqlTransaction)(_transactionMode != TransactionMode.None ? await connection.BeginTransactionAsync(_cancellationSource.Token) : null);

        private Task AbandonAsync(IDbTransaction transaction, ReceivedMessage message)
        {
            transaction?.Rollback();
            _localReceiverDeliveryAttempts.AddOrUpdate(message.ConvHandle, 1, (ch, deliveryAttempts) => deliveryAttempts++);
            return Task.CompletedTask;
        }

        private async Task CompleteAsync(SqlConnection connection, IDbTransaction transaction, ReceivedMessage message, int errorCode = 0, string errorDescription = "")
        {
            try
            {
                var edc = new EndDialogConversationCommand(connection,
                                                           errorCode,
                                                           errorDescription,
                                                           transaction: (SqlTransaction)transaction);
                await edc.ExecuteAsync(message.ConvHandle, _cancellationSource.Token).ConfigureAwait(false);
                transaction?.Commit();
                _localReceiverDeliveryAttempts.TryRemove(message.ConvHandle, out var _);
            }
            catch (Exception e)
            {
                await AbandonAsync(transaction, message).ConfigureAwait(false);
                _logger.LogError($"Unable to complete receive operation: {e.Message}{Environment.NewLine}{e.StackTrace}");
            }
        }

        private async Task DeadLetterAsync(SqlConnection connection, IDbTransaction transaction, ReceivedMessage message, int errorCode, string reason, string description)
        {
            var errorDescription = reason + Environment.NewLine + description;
            await CompleteAsync(connection, transaction, message, errorCode, errorDescription).ConfigureAwait(false);
            _logger.LogCritical(errorDescription);
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
