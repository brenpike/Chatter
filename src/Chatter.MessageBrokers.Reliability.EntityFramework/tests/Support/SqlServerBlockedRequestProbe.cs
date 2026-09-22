using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Reliability.EntityFramework.Tests.Support
{
    /// <summary>
    /// What SQL Server itself says about one request that is waiting on another session's lock, read from
    /// <c>sys.dm_exec_requests</c>.
    /// </summary>
    /// <remarks>
    /// INVARIANT: a concurrency fact asserts on THESE four values and never on elapsed time. A stopwatch reads the
    /// same whether a drain waited on a lock or merely ran second, so a timing assertion passes on a race that never
    /// happened. No test pins that rule; it is a rule about how the facts using this type are written.
    /// </remarks>
    public readonly struct BlockedRequest
    {
        public BlockedRequest(short sessionId, short blockingSessionId, string waitType, string status)
        {
            SessionId = sessionId;
            BlockingSessionId = blockingSessionId;
            WaitType = waitType;
            Status = status;
        }

        /// <summary>The session that is waiting.</summary>
        public short SessionId { get; }

        /// <summary>The session whose lock it is waiting on.</summary>
        public short BlockingSessionId { get; }

        /// <summary>The lock wait the server named, e.g. <c>LCK_M_U</c>.</summary>
        public string WaitType { get; }

        /// <summary>The request status the server named, e.g. <c>suspended</c>.</summary>
        public string Status { get; }

        public override string ToString()
            => $"session_id={SessionId}, blocking_session_id={BlockingSessionId}, wait_type={WaitType}, status={Status}";
    }

    /// <summary>
    /// Observes lock waits from the server's own view. Every read is filtered to <c>DB_ID()</c> of the connection it
    /// runs on and to a NAMED blocking session, so a sibling test class sharing the container cannot produce a false
    /// positive and neither can an unrelated wait inside this database.
    /// </summary>
    /// <remarks>
    /// This differs from the blocked-request helper <c>WhenDeduplicatingInboxOnSqlServer</c> keeps to itself in four
    /// ways, each of which a drain-arbitration fact needs. It identifies the BLOCKER by session id rather than
    /// accepting any non-zero <c>blocking_session_id</c>, so a fact can say the waiter is held by THAT drain rather
    /// than by something. It carries the waiter's own <c>session_id</c> and the request <c>status</c> as well as the
    /// wait type. It exposes a single non-waiting read alongside the bounded poll, which is what lets a fact
    /// establish that a block was STILL in place after some other work completed rather than only that it once was.
    /// And it answers with EVERY request blocked by that session rather than the first, which is how a fact tells a
    /// block one row deep from one the whole table is behind.
    /// </remarks>
    public static class SqlServerBlockedRequestProbe
    {
        // Finite cap on the observation. The cap bounds the WAIT, never the claim: the assertions are on what
        // sys.dm_exec_requests reported, not on how long the poll took to report it. It is generous because a
        // freshly started SQL Server container is slow for its first seconds and a drain that has not yet REACHED
        // its claim is not a drain that failed to block - measured, at a tighter cap, as eight false reds on a cold
        // container that pass on a warm one.
        private static readonly TimeSpan ObservationBudget = TimeSpan.FromSeconds(60);
        private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(50);

        private const string BlockedRequestQuery =
            "SELECT session_id, blocking_session_id, wait_type, status FROM sys.dm_exec_requests " +
            "WHERE database_id = DB_ID() AND blocking_session_id = @blockingSessionId AND wait_type LIKE 'LCK%';";

        /// <summary>
        /// Polls until at least one request in this database is waiting on <paramref name="blockingSessionId"/>'s
        /// lock, and returns the first the server reported. Returns null when the budget is spent with nothing
        /// blocked by it.
        /// </summary>
        public static async Task<BlockedRequest?> WaitForRequestBlockedByAsync(string connectionString, short blockingSessionId)
        {
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync();

            var polls = (int)(ObservationBudget.Ticks / PollInterval.Ticks);
            for (var attempt = 0; attempt < polls; attempt++)
            {
                var blockedRequests = await ReadBlockedRequestsAsync(connection, blockingSessionId);
                if (blockedRequests.Count > 0)
                {
                    return blockedRequests[0];
                }

                await Task.Delay(PollInterval);
            }

            return null;
        }

        /// <summary>
        /// Reads ONCE, without waiting: every request in this database waiting on
        /// <paramref name="blockingSessionId"/>'s lock at this instant.
        /// </summary>
        public static async Task<IReadOnlyList<BlockedRequest>> ReadRequestsBlockedByAsync(string connectionString, short blockingSessionId)
        {
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync();

            return await ReadBlockedRequestsAsync(connection, blockingSessionId);
        }

        /// <summary>
        /// Reads the session id SQL Server gave the connection <paramref name="context"/> is holding.
        /// </summary>
        /// <remarks>
        /// INVARIANT: this must be read while the context HOLDS its connection - inside an open transaction - or the
        /// answer names a pooled session the transaction may never use. A context between operations has returned its
        /// connection to the pool. No test pins that rule; it is a rule about where this is called from.
        /// </remarks>
        public static Task<short> ReadSessionIdAsync(DbContext context)
            => context.Database.SqlQueryRaw<short>("SELECT @@SPID AS Value").SingleAsync();

        private static async Task<IReadOnlyList<BlockedRequest>> ReadBlockedRequestsAsync(SqlConnection connection, short blockingSessionId)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = BlockedRequestQuery;
            command.Parameters.AddWithValue("@blockingSessionId", blockingSessionId);

            var blockedRequests = new List<BlockedRequest>();

            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                blockedRequests.Add(new BlockedRequest(reader.GetInt16(0), reader.GetInt16(1), reader.GetString(2), reader.GetString(3)));
            }

            return blockedRequests;
        }
    }
}
