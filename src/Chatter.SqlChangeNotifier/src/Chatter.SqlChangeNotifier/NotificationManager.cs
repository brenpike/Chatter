using Chatter.SqlChangeNotifier.Scripts;
using Chatter.SqlChangeNotifier.Scripts.ServiceBroker;
using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;

namespace Chatter.SqlChangeNotifier
{
    public class NotificationManager
    {
        public IEnumerable<string> GetInstalledNotifications(string connectionString, string database)
        {
            if (connectionString == null)
            {
                throw new ArgumentNullException(nameof(connectionString));
            }

            if (database == null)
            {
                throw new ArgumentNullException(nameof(database));
            }

            List<string> result = new List<string>();

            using (SqlConnection connection = new SqlConnection(connectionString))
            using (SqlCommand command = connection.CreateCommand())
            {
                connection.Open();
                command.CommandText = new GetServiceBrokerServicesByName(connectionString, database).ToString();
                command.CommandType = CommandType.Text;
                using SqlDataReader reader = command.ExecuteReader();
                while (reader.Read())
                    result.Add(reader.GetString(0));
            }

            return result;
        }

    }
}
