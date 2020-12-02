using Chatter.CQRS;
using Chatter.CQRS.Context;
using Chatter.CQRS.Events;
using Chatter.MessageBrokers.SqlServiceBroker.Configuration;
using Chatter.MessageBrokers.SqlServiceBroker.Context;
using Chatter.MessageBrokers.SqlServiceBroker.Scripts;
using Chatter.MessageBrokers.SqlServiceBroker.Scripts.Misc;
using Chatter.MessageBrokers.SqlServiceBroker.Scripts.ServiceBroker;
using Chatter.MessageBrokers.SqlServiceBroker.Scripts.StoredProcedures;
using Microsoft.Extensions.Logging;
using System;
using System.Data;
using System.Data.SqlClient;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.SqlServiceBroker
{
    public class SqlServiceBrokerReceiver<TMessageData> : IDisposable, IAsyncDisposable where TMessageData : class, IEvent
    {
        private const int _receiveTimeoutInMilliseconds = 60000;
        private readonly SqlServiceBrokerOptions _options;
        private readonly IMessageDispatcher _dispatcher;
        private readonly ILogger<SqlServiceBrokerReceiver<TMessageData>> _logger;
        private CancellationTokenSource _cancellationSource;
        private readonly string _receiverName;

        public SqlServiceBrokerReceiver(SqlServiceBrokerOptions options,
                                        IMessageDispatcher dispatcher,
                                        ILogger<SqlServiceBrokerReceiver<TMessageData>> logger)
        {
            _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
            _logger = logger;
            _receiverName = typeof(TMessageData).Name;
            _options = options ?? throw new ArgumentNullException(nameof(options));
        }

        public string ConversationQueueName => $"{ChatterServiceBrokerConstants.ChatterQueuePrefix}{_receiverName}";
        public string ConversationServiceName => $"{ChatterServiceBrokerConstants.ChatterServicePrefix}{_receiverName}";
        public string ConversationTriggerName => $"{ChatterServiceBrokerConstants.ChatterTriggerPrefix}{_receiverName}";
        public string InstallNotificationsStoredProcName => $"{ChatterServiceBrokerConstants.ChatterInstallNotificationsPrefix}{_receiverName}";
        public string UninstallNotificationsStoredProcName => $"{ChatterServiceBrokerConstants.ChatterUninstallNotificationsPrefix}{_receiverName}";
        public string ConnectionString => _options?.ConnectionString;
        public string DatabaseName => _options?.DatabaseName;
        public string TableName => _options?.TableName;
        public string SchemaName => _options?.SchemaName;
        public NotificationTypes NotificationTypesToReceive => _options.NotificationsToReceive;

        public async Task<IAsyncDisposable> Start()
        {
            try
            {
                await UninstallNotification().ConfigureAwait(false);

                await InstallNotification().ConfigureAwait(false);

                _cancellationSource = new CancellationTokenSource();

                await ReceiveLoop(_cancellationSource.Token).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                _logger.LogCritical($"Unable to start sql change notifier for '{typeof(TMessageData).Name}': {e.Message}{Environment.NewLine}{e.StackTrace}");
            }

            return this;
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

        public void Dispose()
        {
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }

        public async ValueTask DisposeAsync()
        {
            await UninstallNotification().ConfigureAwait(false);
            Cancel();
            Dispose(disposing: false);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                UninstallNotification();
                Cancel();
            }

            _cancellationSource = null;
        }

        public Task CleanDatabase()
        {
            var cleanup = new DatabaseCleanupScript(_options.ConnectionString, _options.DatabaseName);
            return cleanup.ExecuteAsync();
        }

        private async Task ReceiveLoop(CancellationToken cancellationToken)
        {
            try
            {
                while (!_cancellationSource.IsCancellationRequested)
                {
                    var message = await WaitForServiceBrokerMessage(cancellationToken);
                    if (string.IsNullOrWhiteSpace(message))
                    {
                        continue;
                    }

                    var envelop = JsonSerializer.Deserialize<SqlMessageEnvelope<TMessageData>>(message);

                    var chgType = envelop.GetChangeType();
                    dynamic @event = null;
                    SqlChangeNotificationContext<TMessageData> context = null;

                    //TODO: clean this up/refactor

                    if (chgType == ChangeType.Update)
                    {
                        @event = envelop.Inserted?.FirstOrDefault();
                        context = SqlChangeNotificationContext<TMessageData>.Create(chgType, envelop.Deleted?.FirstOrDefault());
                    }
                    else if (chgType == ChangeType.Insert)
                    {
                        @event = envelop.Inserted?.FirstOrDefault();
                        context = SqlChangeNotificationContext<TMessageData>.Create<TMessageData>(chgType);
                    }
                    else
                    {
                        @event = envelop.Deleted?.FirstOrDefault();
                        context = SqlChangeNotificationContext<TMessageData>.Create<TMessageData>(chgType);
                    }

                    var mhc = new MessageHandlerContext();
                    mhc.Container.Include(context);

                    await _dispatcher.Dispatch(@event, mhc).ConfigureAwait(false);
                }
            }
            catch (Exception e)
            {
                _logger.LogCritical($"Receiver stopped due to critical error: {e.Message}{Environment.NewLine}{e.StackTrace}");
            }
            finally
            {
                //TODO: cleanup? raise event?
                _logger.LogInformation($"{nameof(ReceiveLoop)} stopped.");
            }
        }

        private async Task<string> WaitForServiceBrokerMessage(CancellationToken cancellationToken)
        {
            var receiveMessageFromConversationScript =
                new ReceiveMessageFromConversation(
                _options.ConnectionString,
                _options.DatabaseName,
                ConversationQueueName,
                _receiveTimeoutInMilliseconds / 2,
                _options.SchemaName).ToString();

            using SqlConnection conn = new SqlConnection(_options.ConnectionString);
            using SqlCommand command = new SqlCommand(receiveMessageFromConversationScript, conn);
            conn.Open();
            command.CommandType = CommandType.Text;
            command.CommandTimeout = _receiveTimeoutInMilliseconds;

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!reader.Read() || reader.IsDBNull(0))
            {
                return string.Empty;
            }

            return reader.GetString(0);
        }

        private Task UninstallNotification()
        {
            var execUninstallationProcedureScript =
                new SafeExecuteStoredProcedure(
                _options.ConnectionString,
                _options.DatabaseName,
                UninstallNotificationsStoredProcName,
                _options.SchemaName);

            return execUninstallationProcedureScript.ExecuteAsync();
        }

        private async Task InstallNotification()
        {
            var execInstallationProcedureScript
                = new SafeExecuteStoredProcedure(_options.ConnectionString,
                                                 _options.DatabaseName,
                                                 InstallNotificationsStoredProcName,
                                                 _options.SchemaName);

            var installNotificationScript
                = new InstallNotificationsScript(_options,
                                                 InstallNotificationsStoredProcName,
                                                 ConversationQueueName,
                                                 ConversationServiceName,
                                                 ConversationTriggerName);

            var uninstallNotificationScript
                = new UninstallNotificationsScript(_options,
                                                   UninstallNotificationsStoredProcName,
                                                   ConversationQueueName,
                                                   ConversationServiceName,
                                                   ConversationTriggerName,
                                                   InstallNotificationsStoredProcName);

            await installNotificationScript.ExecuteAsync().ConfigureAwait(false);
            await uninstallNotificationScript.ExecuteAsync().ConfigureAwait(false);
            await execInstallationProcedureScript.ExecuteAsync().ConfigureAwait(false);
        }
    }
}
