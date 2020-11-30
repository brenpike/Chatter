using Chatter.CQRS;
using Chatter.CQRS.Context;
using Chatter.CQRS.Events;
using Chatter.SqlChangeNotifier.Configuration;
using Chatter.SqlChangeNotifier.Context;
using Chatter.SqlChangeNotifier.Scripts;
using Chatter.SqlChangeNotifier.Scripts.Misc;
using Chatter.SqlChangeNotifier.Scripts.ServiceBroker;
using Chatter.SqlChangeNotifier.Scripts.StoredProcedures;
using Microsoft.Extensions.Logging;
using System;
using System.Data;
using System.Data.SqlClient;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Chatter.SqlChangeNotifier
{
    public class SqlServiceBrokerReceiver<TMessageData> : IDisposable where TMessageData : class, IEvent
    {
        private const int _receiveTimeoutInMilliseconds = 60000;
        private readonly SqlServiceBrokerOptions _options;
        private readonly IMessageDispatcher _dispatcher;
        private readonly ILogger<SqlServiceBrokerReceiver<TMessageData>> _logger;
        private CancellationTokenSource _cancellationSource;

        public SqlServiceBrokerReceiver(SqlServiceBrokerOptions options,
                                        IMessageDispatcher dispatcher,
                                        ILogger<SqlServiceBrokerReceiver<TMessageData>> logger)
        {
            _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
            _logger = logger;
            Identity = typeof(TMessageData).Name;
            _options = options ?? throw new ArgumentNullException(nameof(options));
        }
    
        public string ConversationQueueName => $"{ChatterServiceBrokerConstants.ChatterQueuePrefix}{this.Identity}";

        public string ConversationServiceName => $"{ChatterServiceBrokerConstants.ChatterServicePrefix}{this.Identity}";

        public string ConversationTriggerName => $"{ChatterServiceBrokerConstants.ChatterTriggerPrefix}{this.Identity}";

        public string InstallNotificationsStoredProcName => $"{ChatterServiceBrokerConstants.ChatterInstallNotificationsPrefix}{this.Identity}";

        public string UninstallNotificationsStoredProcName => $"{ChatterServiceBrokerConstants.ChatterUninstallNotificationsPrefix}{this.Identity}";

        public string ConnectionString => _options?.ConnectionString;

        public string DatabaseName => _options?.DatabaseName;

        public string TableName => _options?.TableName;

        public string SchemaName => _options?.SchemaName;

        public NotificationTypes NotificaionTypes => _options.NotificationsToReceive;

        public bool DetailsIncluded => _options.ReceiveDetails;

        public string Identity
        {
            get;
            private set;
        }

        public event EventHandler NotificationProcessStopped;

        public async Task<IDisposable> Start()
        {
            try
            {
                UninstallNotification();

                InstallNotification();

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
            if ((_cancellationSource == null) || (_cancellationSource.Token.IsCancellationRequested))
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

        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                UninstallNotification();
                Cancel();
            }

            _cancellationSource = null;
        }

        public void CleanDatabase()
        {
            var cleanup = new DatabaseCleanupScript(_options.ConnectionString, _options.DatabaseName);
            cleanup.Execute();
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
                OnNotificationProcessStopped();
                _logger.LogInformation($"");
            }
        }

        private async Task<string> WaitForServiceBrokerMessage(CancellationToken cancellationToken)
        {
            var receiveMessageFromConversationScript =
                new ReceiveMessageFromConversation(
                _options.ConnectionString,
                _options.DatabaseName,
                this.ConversationQueueName,
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

        private void UninstallNotification()
        {
            var execUninstallationProcedureScript =
                new SafeExecuteStoredProcedure(
                _options.ConnectionString,
                _options.DatabaseName,
                this.UninstallNotificationsStoredProcName,
                _options.SchemaName);

            execUninstallationProcedureScript.Execute();
        }

        private void InstallNotification()
        {
            var execInstallationProcedureScript
                = new SafeExecuteStoredProcedure(_options.ConnectionString,
                                                 _options.DatabaseName,
                                                 this.InstallNotificationsStoredProcName,
                                                 _options.SchemaName);

            var installNotificationScript
                = new InstallNotificationsScript(_options,
                                                 this.InstallNotificationsStoredProcName,
                                                 this.ConversationQueueName,
                                                 this.ConversationServiceName,
                                                 this.ConversationTriggerName);

            var uninstallNotificationScript
                = new UninstallNotificationsScript(_options,
                                                   this.UninstallNotificationsStoredProcName,
                                                   this.ConversationQueueName,
                                                   this.ConversationServiceName,
                                                   this.ConversationTriggerName,
                                                   this.InstallNotificationsStoredProcName);

            installNotificationScript.Execute();
            uninstallNotificationScript.Execute();
            execInstallationProcedureScript.Execute();
        }

        private void OnNotificationProcessStopped()
        {
            var evnt = NotificationProcessStopped;
            if (evnt == null) return;

            evnt.BeginInvoke(this, EventArgs.Empty, null, null);
        }
    }
}
