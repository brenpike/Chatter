namespace Chatter.MessageBrokers.SqlServiceBroker.Scripts.Misc
{
    /// <summary>
    /// Displays SQL permissions the user account will require to install and configure notifications
    /// </summary>
    public class PermissionInfoDisplayScript : ExecutableSqlScript
    {
        /// <summary>
        /// Displays SQL permissions the user account will require to install and configure notifications
        /// </summary>
        /// <param name="connectionString">The SQL connection string</param>
        public PermissionInfoDisplayScript(string connectionString)
            : base(connectionString)
        { }

        public override string ToString()
        {
            return @"
                    DECLARE @msg VARCHAR(MAX)
                    DECLARE @crlf CHAR(1)
                    SET @crlf = CHAR(10)
                    SET @msg = 'Current user must have following permissions: '
                    SET @msg = @msg + '[CREATE PROCEDURE, CREATE SERVICE, CREATE QUEUE, SUBSCRIBE QUERY NOTIFICATIONS, CONTROL, REFERENCES] '
                    SET @msg = @msg + 'that are required to start query notifications. '
                    SET @msg = @msg + 'Grant described permissions with following script: ' + @crlf
                    SET @msg = @msg + 'GRANT CREATE PROCEDURE TO [<username>];' + @crlf
                    SET @msg = @msg + 'GRANT CREATE SERVICE TO [<username>];' + @crlf
                    SET @msg = @msg + 'GRANT CREATE QUEUE  TO [<username>];' + @crlf
                    SET @msg = @msg + 'GRANT REFERENCES ON CONTRACT::[DEFAULT] TO [<username>];' + @crlf
                    SET @msg = @msg + 'GRANT SUBSCRIBE QUERY NOTIFICATIONS TO [<username>];' + @crlf
                    SET @msg = @msg + 'GRANT CONTROL ON SCHEMA::[<schemaname>] TO [<username>];'
                    
                    PRINT @msg
                ";
        }
    }
}
