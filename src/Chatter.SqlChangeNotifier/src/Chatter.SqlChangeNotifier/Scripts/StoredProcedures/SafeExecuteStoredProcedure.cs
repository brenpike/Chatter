using System;

namespace Chatter.SqlChangeNotifier.Scripts.StoredProcedures
{
    /// <summary>
    /// Ensures stored procedures existence before executing
    /// </summary>
    public class SafeExecuteStoredProcedure : ExecutableSqlScript
    {
        private readonly string _databaseName;
        private readonly string _storedProcedureName;
        private readonly string _schemaName;

        /// <summary>
        /// Ensures stored procedures existence before executing
        /// </summary>
        /// <param name="connectionString">The SQL connection string</param>
        /// <param name="databaseName">The database where the target stored procedure exists</param>
        /// <param name="storedProcedureName">The name of the stored procedure to execute</param>
        /// <param name="schemaName">The schema of the stored procedure to execute</param>
        public SafeExecuteStoredProcedure(string connectionString,
                                          string databaseName,
                                          string storedProcedureName,
                                          string schemaName)
            : base(connectionString)
        {
            if (string.IsNullOrWhiteSpace(databaseName))
            {
                throw new ArgumentException($"'{nameof(databaseName)}' cannot be null or whitespace", nameof(databaseName));
            }

            if (string.IsNullOrWhiteSpace(storedProcedureName))
            {
                throw new ArgumentException($"'{nameof(storedProcedureName)}' cannot be null or whitespace", nameof(storedProcedureName));
            }

            if (string.IsNullOrWhiteSpace(schemaName))
            {
                throw new ArgumentException($"'{nameof(schemaName)}' cannot be null or whitespace", nameof(schemaName));
            }

            _databaseName = databaseName;
            _storedProcedureName = storedProcedureName;
            _schemaName = schemaName;
        }

        public override string ToString()
        {
            return string.Format(@"
                USE [{0}]
                IF OBJECT_ID ('{2}.{1}', 'P') IS NOT NULL
                    EXEC {2}.{1}
            ", _databaseName, _storedProcedureName, _schemaName);
        }
    }
}
