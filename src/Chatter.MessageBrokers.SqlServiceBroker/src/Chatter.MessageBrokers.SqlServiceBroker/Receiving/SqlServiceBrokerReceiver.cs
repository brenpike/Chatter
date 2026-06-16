using Chatter.MessageBrokers.Configuration;
using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Exceptions;
using Chatter.MessageBrokers.Receiving;
using Chatter.MessageBrokers.Sending;
using Chatter.MessageBrokers.SqlServiceBroker.Configuration;
using Chatter.MessageBrokers.SqlServiceBroker.Scripts;
using Chatter.MessageBrokers.SqlServiceBroker.Sending;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Microsoft.Data.SqlClient;
using System.Threading;
using System.Threading.Tasks;
using System.Transactions;

namespace Chatter.MessageBrokers.SqlServiceBroker.Receiving
{
    internal class SqlServiceBrokerReceiver : IMessagingInfrastructureReceiver
    {
        private readonly SqlServiceBrokerOptions _ssbOptions;
        private readonly ISqlConnectionSource _connectionSource;
        private readonly ILogger<SqlServiceBrokerReceiver> _logger;
        private readonly IBodyConverterFactory _bodyConverterFactory;
        private TransactionMode _transactionMode;
        private readonly ConcurrentDictionary<Guid, int> _localReceiverDeliveryAttempts;
        private readonly IServiceScopeFactory _serviceFactory;
        private readonly ServiceBrokerMessageClassifier _classifier;
        private ReceiverOptions _options;

        public SqlServiceBrokerReceiver(SqlServiceBrokerOptions ssbOptions,
                                        ISqlConnectionSource connectionSource,
                                        MessageBrokerOptions messageBrokerOptions,
                                        ILogger<SqlServiceBrokerReceiver> logger,
                                        IBodyConverterFactory bodyConverterFactory,
                                        IServiceScopeFactory serviceFactory)
        {
            _ssbOptions = ssbOptions ?? throw new ArgumentNullException(nameof(ssbOptions));
            _connectionSource = connectionSource ?? throw new ArgumentNullException(nameof(connectionSource));
            _logger = logger;
            _bodyConverterFactory = bodyConverterFactory;
            _transactionMode = messageBrokerOptions?.TransactionMode ?? TransactionMode.ReceiveOnly;
            _localReceiverDeliveryAttempts = new ConcurrentDictionary<Guid, int>();
            _serviceFactory = serviceFactory;
            _classifier = new ServiceBrokerMessageClassifier();
        }

        public Task InitializeAsync(ReceiverOptions options, CancellationToken cancellationToken)
        {
            _options = options;
            // options.TransactionMode is already core-normalized: BrokeredMessageReceiver.StartReceiverImpl
            // does `options.TransactionMode ??= _messageBrokerOptions.TransactionMode` before calling
            // InitializeAsync, so per-receiver wins; absent per-receiver inherits the ctor-captured global;
            // absent both, the ctor default (ReceiveOnly) holds.
            _transactionMode = options.TransactionMode ?? _transactionMode;
            return Task.CompletedTask;
        }

        public Task StopReceiver()
        {
            Cancel();
            return Task.CompletedTask;
        }

        private void Cancel()
        {
        }

        private async Task<ReceivedMessage> ReceiveAsync(SqlConnection connection, SqlTransaction transaction, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var receiveMessageFromQueue = new ReceiveMessageFromQueueCommand(connection,
                                             _options.MessageReceiverPath,
                                             _ssbOptions.ReceiverTimeoutInMilliseconds,
                                             transaction: transaction);

            return await receiveMessageFromQueue.ExecuteAsync(cancellationToken);
        }

        public async Task<MessageBrokerContext> ReceiveMessageAsync(TransactionContext transactionContext, CancellationToken cancellationToken)
        {
            ReceivedMessage message = null;
            MessageBrokerContext messageContext = null;
            SqlConnection connection = null;
            SqlTransaction transaction = null;
            ReceiveSession session;

            try
            {
                connection = await _connectionSource.OpenAsync(cancellationToken);
                transaction = await CreateTransaction(connection, cancellationToken);
            }
            catch (Exception ex) when (ex is ArgumentException || ex is InvalidOperationException)
            {
                transaction?.Dispose();
                connection?.Dispose();
                throw new CriticalReceiverException("Error connecting to sql", ex);
            }

            session = new ReceiveSession(connection, transaction);

            try
            {
                message = await ReceiveAsync(connection, transaction, cancellationToken);
            }
#if NET5_0_OR_GREATER
            catch (SqlException e) when (e.IsTransient)
            {
                await session.DisposeAsync();
                _logger.LogWarning(e, "Failure to receive message from Sql Service Broker due to transient error");
                throw;
            }
#endif
            catch (SqlException e) when (e.Number == 102)
            {
                await session.DisposeAsync();
                throw new CriticalReceiverException($"Unable to receive message from configured queue '{_options.MessageReceiverPath}'", e);
            }
            catch (Exception e)
            {
                await session.DisposeAsync();
                _logger.LogError(e, $"Error receiving sql service broker message from queue '{_options.MessageReceiverPath}'");
                throw;
            }

            var outcome = _classifier.Classify(message);

            switch (outcome)
            {
                case ClassificationOutcome.DiscardNull:
                    // Empty RECEIVE (null message from an idle WAITFOR timeout): settle once and continue the loop.
                    await DiscardMessageAsync(session, "Discarding null message", cancellationToken);
                    return null;
                case ClassificationOutcome.EndDialog:
                    await AckEndDialogAsync(session, message.ConvHandle, cancellationToken);
                    return null;
                case ClassificationOutcome.DiscardWrongType:
                    await DiscardMessageAsync(session
                        , $"Discarding message of type '{message.MessageTypeName}'. Only messages of type '{ServicesMessageTypes.DefaultType}' or '{ServicesMessageTypes.ChatterBrokeredMessageType}' will be received."
                        , cancellationToken);
                    return null;
                case ClassificationOutcome.DiscardNullBody:
                    await DiscardMessageAsync(session
                        , $"Discarding message of type '{message.MessageTypeName}' with null message body"
                        , cancellationToken);
                    return null;
            }

            transactionContext.Container.Include(connection);
            if (_transactionMode != TransactionMode.None && transaction != null)
            {
                transactionContext.Container.Include(transaction);
            }

            _localReceiverDeliveryAttempts.AddOrUpdate(message.ConvHandle, 1, (ch, deliveryAttempts) => deliveryAttempts + 1);

            // The envelope is serialized on the wire using the SSB-configured MessageBodyType (UTF-16 by
            // default via JsonUnicodeBodyConverter), so the envelope must be decoded with that converter.
            IBrokeredMessageBodyConverter envelopeConverter = new JsonUnicodeBodyConverter();
            // The INNER DTO body is encoded by the core dispatcher using the routing ContentType
            // (RoutingOptions.DefaultContentType = "application/json", UTF-8) — independent of the SSB
            // envelope's wire encoding. It must therefore be decoded with the converter for the inner body's
            // own content type (carried in the envelope's ContentType header), NOT the UTF-16 envelope
            // converter. Reusing the envelope converter here mis-decodes UTF-8 inner bytes as UTF-16 and
            // surfaces a PoisonedMessageException (e.g. "'0xE2' is an invalid start of a value").
            IBrokeredMessageBodyConverter bodyConverter = envelopeConverter;
            byte[] messagePayload = message.Body;
            string messageId = message.ConvHandle.ToString();
            IDictionary<string, object> headers = new Dictionary<string, object>();

            try
            {
                envelopeConverter = _bodyConverterFactory.CreateBodyConverter(_ssbOptions.MessageBodyType);
                bodyConverter = envelopeConverter;
                if (message.MessageTypeName == ServicesMessageTypes.ChatterBrokeredMessageType)
                {
                    var brokeredMessage = envelopeConverter.Convert<OutboundBrokeredMessage>(message.Body);

                    if (brokeredMessage == null)
                    {
                        throw new ArgumentNullException(nameof(brokeredMessage), $"Unable to deserialize {nameof(OutboundBrokeredMessage)} from message body");
                    }

                    messagePayload = brokeredMessage.Body;
                    messageId = brokeredMessage.MessageId;
                    // The envelope was deserialized via System.Text.Json (JsonUnicodeBodyConverter) through
                    // ChatterJson.Options, where the global MaterializingObjectConverter already restored
                    // OutboundBrokeredMessage.MessageContext's object-typed values to CLR types. So an
                    // upstream-stamped non-string header (e.g. a numeric ReceiveAttempts from a prior SSB hop)
                    // does not throw InvalidCastException on the downstream GetMessageContextByKey<T> casts —
                    // no per-seam materialization needed, only the null-guard.
                    headers = brokeredMessage.MessageContext ?? new Dictionary<string, object>();

                    // Resolve the inner-body converter from the envelope's own ContentType header so the
                    // typed payload is decoded with the encoding it was actually sent in (UTF-8 by default),
                    // keeping the UTF-16 envelope wire format intact. Falls back to the envelope converter
                    // when no usable content-type header is present.
                    if (headers.TryGetValue(MessageContext.ContentType, out var innerContentType)
                        && innerContentType is string innerContentTypeValue
                        && !string.IsNullOrWhiteSpace(innerContentTypeValue))
                    {
                        bodyConverter = _bodyConverterFactory.CreateBodyConverter(innerContentTypeValue);
                    }
                }
            }
            catch (Exception e)
            {
                _logger.LogWarning(e, $"Error creating body converter for content type '{_ssbOptions.MessageBodyType}'. Defaulting to {nameof(JsonUnicodeBodyConverter)}.");
            }
            finally
            {
                _localReceiverDeliveryAttempts.TryGetValue(message.ConvHandle, out var deliveryAttempts);

                headers[SSBMessageContext.ConversationGroupId] = message.ConvGroupHandle;
                headers[SSBMessageContext.ConversationHandle] = message.ConvHandle;
                headers[SSBMessageContext.MessageSequenceNumber] = message.MessageSeqNo;
                headers[SSBMessageContext.ServiceName] = message.ServiceName;
                headers[SSBMessageContext.ServiceContractName] = message.ServiceContractName;
                headers[SSBMessageContext.MessageTypeName] = message.MessageTypeName;
                headers[MessageContext.InfrastructureType] = SSBMessageContext.InfrastructureType;
                headers[MessageContext.ReceiveAttempts] = deliveryAttempts;

                messageContext = new MessageBrokerContext(messageId, messagePayload, headers, _options.MessageReceiverPath, cancellationToken, bodyConverter);
                messageContext.Container.Include(message);
            }

            return messageContext;
        }

        private async Task AckEndDialogAsync(ReceiveSession session, Guid convHandle, CancellationToken cancellationToken)
        {
            try
            {
                var edc = new EndDialogConversationCommand(session.Connection,
                                  convHandle,
                                  enableCleanup: _ssbOptions.CleanupOnEndConversation,
                                  transaction: session.Transaction);
                await edc.ExecuteAsync(cancellationToken);
                await session.CommitAsync(cancellationToken);
            }
            finally
            {
                await session.DisposeAsync();
            }
        }

        private async Task DiscardMessageAsync(ReceiveSession session, string discardMessage, CancellationToken cancellationToken)
        {
            try
            {
                await session.CommitAsync(cancellationToken);
                _logger.LogTrace(discardMessage);
            }
            finally
            {
                await session.DisposeAsync();
            }
        }

        private async Task<SqlTransaction> CreateTransaction(SqlConnection connection, CancellationToken cancellationToken)
            => (_transactionMode != TransactionMode.None ? await connection.BeginTransactionAsync(cancellationToken) : null) as SqlTransaction;

        public async Task<bool> AckMessageAsync(MessageBrokerContext context, TransactionContext transactionContext, CancellationToken cancellationToken)
        {
            ReceivedMessage msg = null;
            if (!context?.Container.TryGet(out msg) ?? false)
            {
                _logger.LogTrace($"No {nameof(ReceivedMessage)} contained in {nameof(context)}.");
            }

            transactionContext.Container.TryGet<SqlConnection>(out var connection);
            transactionContext.Container.TryGet<SqlTransaction>(out var transaction);
            var session = new ReceiveSession(connection, transaction);

            try
            {
                if (msg != null)
                {
                    var edc = new EndDialogConversationCommand(connection,
                                           msg.ConvHandle,
                                           enableCleanup: _ssbOptions.CleanupOnEndConversation,
                                           transaction: transaction);
                    await edc.ExecuteAsync(cancellationToken);
                    _localReceiverDeliveryAttempts.TryRemove(msg.ConvHandle, out var _);
                }
                else
                {
                    _logger.LogTrace($"Unable end dialog conversation during message acknowledgment. {nameof(msg)} is null.");
                }
                await session.CommitAsync(cancellationToken);
                _logger.LogTrace("Message acknowledgment complete");
                return true;
            }
            finally
            {
                await session.DisposeAsync();
            }
        }

        public async Task<bool> NackMessageAsync(MessageBrokerContext context, TransactionContext transactionContext, CancellationToken cancellationToken)
        {
            transactionContext.Container.TryGet<SqlConnection>(out var connection);
            transactionContext.Container.TryGet<SqlTransaction>(out var transaction);
            var session = new ReceiveSession(connection, transaction);

            try
            {
                await session.RollbackAsync(cancellationToken);
                _logger.LogTrace("Message negative acknowledgment complete");
                return true;
            }
            finally
            {
                await session.DisposeAsync();
            }
        }

        public async Task<bool> DeadletterMessageAsync(MessageBrokerContext context, TransactionContext transactionContext, string deadLetterReason, string deadLetterErrorDescription, CancellationToken cancellationToken)
        {
            ReceivedMessage msg = null;
            if (!context?.Container.TryGet(out msg) ?? false)
            {
                throw new ArgumentException($"Unable to deadletter message. No {nameof(ReceivedMessage)} contained in {nameof(context)}.", nameof(msg));
            }

            transactionContext.Container.TryGet<SqlConnection>(out var connection);
            transactionContext.Container.TryGet<SqlTransaction>(out var transaction);
            var session = new ReceiveSession(connection, transaction);

            try
            {
                var edc = new EndDialogConversationCommand(connection,
                                       msg.ConvHandle,
                                       enableCleanup: _ssbOptions.CleanupOnEndConversation,
                                       transaction: transaction);
                await edc.ExecuteAsync(cancellationToken);

                using var scope = _serviceFactory.CreateScope();
                var ssbSender = scope.ServiceProvider.GetRequiredService<SqlServiceBrokerSender>();
                var bodyConverter = _bodyConverterFactory.CreateBodyConverter(_ssbOptions.MessageBodyType);

                _localReceiverDeliveryAttempts.TryGetValue(msg.ConvHandle, out var deliveryAttempts);

                var headers = new Dictionary<string, object>()
                {
                    [SSBMessageContext.ConversationHandle] = msg.ConvHandle,
                    [SSBMessageContext.ServiceName] = msg.ServiceName,
                    [MessageContext.FailureDescription] = deadLetterErrorDescription,
                    [MessageContext.FailureDetails] = deadLetterReason,
                    [MessageContext.InfrastructureType] = SSBMessageContext.InfrastructureType,
                    [SSBMessageContext.MessageTypeName] = ServicesMessageTypes.ChatterBrokeredMessageType,
                    [SSBMessageContext.ServiceContractName] = ServicesMessageTypes.ChatterServiceContract,
                    [MessageContext.ReceiveAttempts] = deliveryAttempts
                };
                await ssbSender.Dispatch(new OutboundBrokeredMessage(context.BrokeredMessage.MessageId, msg.Body, headers, _options.DeadLetterQueuePath, bodyConverter), transactionContext);
                await session.CommitAsync(cancellationToken);
                _localReceiverDeliveryAttempts.TryRemove(msg.ConvHandle, out var _);
                _logger.LogTrace($"Message deadlettered.");
                return true;
            }
            finally
            {
                await session.DisposeAsync();
            }
        }

        /// <summary>
        /// Owns the lifecycle of a single RECEIVE's connection and (optional) transaction. Every receive
        /// outcome routes its terminal SQL through exactly one of <see cref="CommitAsync"/> /
        /// <see cref="RollbackAsync"/>, then disposes via <see cref="DisposeAsync"/>.
        /// INVARIANT: terminal settle (commit or rollback) runs at most once — the <c>_settled</c> guard
        /// makes a second settle a no-op, so a connection/transaction is committed-or-rolled-back exactly
        /// once and disposed exactly once. All terminal ops are null-transaction-safe (TransactionMode.None
        /// leaves <see cref="Transaction"/> null), so they never <c>await</c> a null Task.
        /// </summary>
        private sealed class ReceiveSession : IAsyncDisposable, IDisposable
        {
            public SqlConnection Connection { get; }
            public SqlTransaction Transaction { get; private set; }
            private bool _settled;
            private bool _disposed;

            public ReceiveSession(SqlConnection connection, SqlTransaction transaction)
            {
                Connection = connection;
                Transaction = transaction;
            }

            public async Task CommitAsync(CancellationToken cancellationToken)
            {
                if (_settled)
                {
                    return;
                }
                _settled = true;
                if (Transaction != null)
                {
                    await Transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                }
                DisposeTransaction();
            }

            public async Task RollbackAsync(CancellationToken cancellationToken)
            {
                if (_settled)
                {
                    return;
                }
                _settled = true;
                if (Transaction != null)
                {
                    await Transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                }
                DisposeTransaction();
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return default;
            }

            public void Dispose()
            {
                if (_disposed)
                {
                    return;
                }
                _disposed = true;
                DisposeTransaction();
                Connection?.Dispose();
            }

            private void DisposeTransaction()
            {
                Transaction?.Dispose();
                Transaction = null;
            }
        }

        public void Dispose()
        {
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }

        public async ValueTask DisposeAsync()
        {
            await Task.CompletedTask;
            Dispose(disposing: false);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                Cancel();
            }
        }
    }
}
