using System;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.Azure.Cosmos;

namespace Chatter.MessageBrokers.Reliability.Cosmos.Tests.Integration
{
    // Builds a CosmosClient wired for the linux vnext-preview emulator and provisions the database + document
    // (aggregate) container + lease container the integration suite needs. The emulator self-signs its TLS cert and
    // only answers on its gateway endpoint, so the client runs in Gateway mode, limits itself to the resolved
    // endpoint, and trusts the emulator cert via a ServerCertificateCustomValidationCallback. The document container is
    // created with DefaultTimeToLive enabled (-1) so the relay's post-delivery TTL stamp takes effect (criterion 4).
    //
    // Edge-of-the-test use ONLY: the suite drives reads/writes through Chatter's public contracts; this client exists
    // to stand the emulator up and to seed/assert at the test edges (never as the system under test). It is the SAME
    // CosmosClient instance the harness registers as the app singleton, so the provider derives its container handles
    // from exactly the client these provisioned containers live on.
    public sealed class CosmosTestClient : IAsyncDisposable
    {
        // A single shared database/container name set per suite run; unique-enough so concurrent suite runs against one
        // emulator do not collide, while every test in a collection shares the provisioned containers.
        public const string DatabaseName = "chatter-cosmos-it";
        public const string DocumentContainerName = "documents";
        public const string SecondDocumentContainerName = "documents-2";
        public const string LeaseContainerName = "leases";

        // The container partition-key path the aggregate/outbox/inbox-marker docs are stamped at and the relay recovers
        // the delivered/TTL patch partition from. A single-segment path keeps every write single-partition (ADR-0007).
        public const string PartitionKeyPath = "/pk";

        private CosmosTestClient(CosmosClient client) => Client = client;

        // The provisioned CosmosClient. Registered as the app singleton by the harness; used at test edges for
        // seeding/asserting.
        public CosmosClient Client { get; }

        // Builds the emulator-targeted CosmosClient and provisions the database + both document containers (with TTL
        // enabled) + the lease container, create-if-not-exists so re-runs are idempotent.
        public static async Task<CosmosTestClient> CreateAsync(string endpoint, string key)
        {
            if (string.IsNullOrWhiteSpace(endpoint))
            {
                throw new ArgumentException("A non-null, non-whitespace emulator endpoint is required.", nameof(endpoint));
            }
            if (string.IsNullOrWhiteSpace(key))
            {
                throw new ArgumentException("A non-null, non-whitespace emulator key is required.", nameof(key));
            }

            var options = new CosmosClientOptions
            {
                // The emulator answers only on its gateway endpoint; direct mode would try to reach internal replica
                // addresses that are not published out of the container.
                ConnectionMode = ConnectionMode.Gateway,
                LimitToEndpoint = true,
                // Trust the emulator's self-signed TLS cert. The HttpClientFactory + ServerCertificateCustomValidation
                // callback both bypass validation; the callback covers the SDK's own gateway calls and the factory
                // covers any HttpClient the SDK constructs.
                HttpClientFactory = () => new HttpClient(new HttpClientHandler
                {
                    ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
                }),
                ServerCertificateCustomValidationCallback = (_, _, _) => true,
            };

            var client = new CosmosClient(endpoint, key, options);

            DatabaseResponse database = await client.CreateDatabaseIfNotExistsAsync(DatabaseName).ConfigureAwait(false);

            await CreateDocumentContainerAsync(database.Database, DocumentContainerName).ConfigureAwait(false);
            await CreateDocumentContainerAsync(database.Database, SecondDocumentContainerName).ConfigureAwait(false);

            // The lease container's partition key is /id per the change-feed processor lease-store contract.
            await database.Database.CreateContainerIfNotExistsAsync(
                new ContainerProperties(LeaseContainerName, "/id")).ConfigureAwait(false);

            return new CosmosTestClient(client);
        }

        // Creates a document/aggregate container at the declared PK path with DefaultTimeToLive enabled (-1): docs do
        // NOT expire by default (ttl unset), but a per-document positive ttl IS honored — which is what the relay
        // stamps on a delivered outbox doc (criterion 4). Without DefaultTimeToLive enabled the per-document ttl is
        // ignored by Cosmos.
        private static Task CreateDocumentContainerAsync(Database database, string containerName)
        {
            var properties = new ContainerProperties(containerName, PartitionKeyPath)
            {
                DefaultTimeToLive = -1,
            };

            return database.CreateContainerIfNotExistsAsync(properties);
        }

        public ValueTask DisposeAsync()
        {
            Client.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
