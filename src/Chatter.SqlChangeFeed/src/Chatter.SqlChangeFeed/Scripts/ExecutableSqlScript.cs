using System;
using System.Data;
using Microsoft.Data.SqlClient;
using System.Threading;
using System.Threading.Tasks;

namespace Chatter.SqlChangeFeed.Scripts
{
    public abstract class ExecutableSqlScript
    {
        private readonly string _connectionString;

        public ExecutableSqlScript(string connectionString)
        {
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                throw new ArgumentException($"'{nameof(connectionString)}' cannot be null or whitespace", nameof(connectionString));
            }

            _connectionString = connectionString;
        }

        public virtual void Execute()
        {
            using SqlConnection conn = new SqlConnection(_connectionString);
            using SqlCommand command = new SqlCommand(ToString(), conn);
            conn.Open();
            command.CommandType = CommandType.Text;
            command.ExecuteNonQuery();
        }

        public virtual async Task ExecuteAsync(CancellationToken token = default)
        {
            using SqlConnection conn = new SqlConnection(_connectionString);
            using (SqlCommand command = new SqlCommand(ToString(), conn))
            {
                await conn.OpenAsync(token);
                command.CommandType = CommandType.Text;
                await command.ExecuteNonQueryAsync(token);
            }
        }
    }
}
