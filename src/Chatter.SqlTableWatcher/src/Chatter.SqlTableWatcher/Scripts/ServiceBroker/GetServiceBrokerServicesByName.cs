using System;

namespace Chatter.SqlTableWatcher.Scripts.ServiceBroker
{
    /// <summary>
    /// Gets all SQL service broker SERVICES that are currently installed
    /// </summary>
    public class GetServiceBrokerServicesByName : ExecutableSqlScript
    {
        private readonly string _databaseName;
        private readonly string _installedServicesPrefix;

        /// <summary>
        /// Gets all SQL service broker SERVICES that are currently installed
        /// </summary>
        /// <param name="connectionString">The SQL connection string</param>
        /// <param name="databaseName">The name of the database to enumerate all installed SQL service broker SERVICES</param>
        /// <param name="installedServicesPrefix">The prefix used when installing SQL Service Broker SERVICES</param>
        public GetServiceBrokerServicesByName(string connectionString,
                                              string databaseName,
                                              string installedServicesPrefix = ChatterServiceBrokerConstants.ChatterServicePrefix)
            : base(connectionString)
        {
            if (string.IsNullOrWhiteSpace(databaseName))
            {
                throw new ArgumentException($"'{nameof(databaseName)}' cannot be null or whitespace", nameof(databaseName));
            }

            if (string.IsNullOrWhiteSpace(installedServicesPrefix))
            {
                throw new ArgumentException($"'{nameof(installedServicesPrefix)}' cannot be null or whitespace", nameof(installedServicesPrefix));
            }

            _databaseName = databaseName;
            _installedServicesPrefix = installedServicesPrefix;
        }

        public override string ToString()
        {
            return string.Format(@"
                USE [{0}]
                
                SELECT REPLACE(name , '{1}' , '') 
                FROM sys.services 
                WHERE [name] like '{1}%';
            ", _databaseName, _installedServicesPrefix);
        }
    }
}
