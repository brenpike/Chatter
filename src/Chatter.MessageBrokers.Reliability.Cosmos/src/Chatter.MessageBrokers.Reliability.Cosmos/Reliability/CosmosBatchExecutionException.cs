using Microsoft.Azure.Cosmos;
using System;
using System.Net;

namespace Chatter.MessageBrokers.Reliability.Cosmos
{
    /// <summary>
    /// Thrown by the Document-Tier Batch-Lifecycle Behavior when the single framework-owned batch-execute returns a
    /// non-success <see cref="TransactionalBatchResponse"/>. A Cosmos <see cref="TransactionalBatch"/> is
    /// all-or-nothing, so a non-success batch means no aggregate, outbox, or marker write committed; throwing prevents
    /// the message from being acked, so the transport redelivers. The forced aggregate ETag/412 conflict surfaces here.
    /// </summary>
    /// <remarks>
    /// The non-success <see cref="StatusCode"/> is carried so the response-inspection seam in #220 can distinguish a
    /// confirmed-duplicate (a 409 on the inbox-marker op = swallow-no-throw) from a genuine failure without rewriting
    /// the execute path.
    /// </remarks>
    public sealed class CosmosBatchExecutionException : Exception
    {
        public CosmosBatchExecutionException(TransactionalBatchResponse response)
            : base(BuildMessage(response))
        {
            StatusCode = response?.StatusCode;
        }

        /// <summary>
        /// The non-success HTTP status code of the failed batch, or <c>null</c> when no response was produced.
        /// </summary>
        public HttpStatusCode? StatusCode { get; }

        private static string BuildMessage(TransactionalBatchResponse response)
            => response is null
                ? "The Cosmos transactional batch produced no response; the atomic write did not commit."
                : $"The Cosmos transactional batch failed with status '{(int)response.StatusCode} {response.StatusCode}'; the atomic write did not commit.";
    }
}
