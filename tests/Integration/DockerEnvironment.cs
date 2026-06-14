using System;
using System.IO;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Chatter.Testing.Core.Integration
{
    // Discovery-time Docker reachability probe shared by every module's integration tests. The integration
    // fixtures need Docker to start their container; when Docker is absent OR the daemon is not actually
    // responding, the integration facts are SKIPPED (never failed) so a plain `dotnet test` on a Docker-free
    // machine stays green. The probe issues a real Docker `/_ping` over the resolved endpoint — a connectable
    // socket file is NOT sufficient (e.g. a WSL /var/run/docker.sock accepts connections even when no daemon
    // serves it), so only a successful daemon ping reports available. The result is cached for the test assembly.
    public static class DockerEnvironment
    {
        private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(3);

        private static readonly Lazy<bool> _isAvailable = new Lazy<bool>(Probe);

        public static bool IsAvailable => _isAvailable.Value;

        public const string SkipReason =
            "Docker is not available (daemon not reachable); the integration container cannot be started. " +
            "Run with a working Docker daemon to execute the Category=Integration tests.";

        // Resolves the Docker endpoint (DOCKER_HOST wins; otherwise the default unix socket) and pings the
        // daemon. Any failure — missing socket, refused connection, non-success status, timeout — reports
        // unavailable so the tests skip rather than fail.
        private static bool Probe()
        {
            try
            {
                var dockerHost = Environment.GetEnvironmentVariable("DOCKER_HOST");

                if (string.IsNullOrWhiteSpace(dockerHost))
                {
                    return PingUnixSocket("/var/run/docker.sock");
                }

                if (!Uri.TryCreate(dockerHost, UriKind.Absolute, out var uri))
                {
                    return false;
                }

                return uri.Scheme switch
                {
                    "unix" => PingUnixSocket(uri.LocalPath),
                    "tcp" => PingTcp(uri.Host, uri.Port > 0 ? uri.Port : 2375),
                    "http" => PingTcp(uri.Host, uri.Port > 0 ? uri.Port : 80),
                    "https" => PingTcp(uri.Host, uri.Port > 0 ? uri.Port : 443),
                    // Named-pipe endpoints (Windows) are not probed here; report available and let the
                    // fixture surface any failure rather than skip a working Windows daemon.
                    "npipe" => true,
                    _ => false,
                };
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static bool PingUnixSocket(string socketPath)
        {
            if (!File.Exists(socketPath))
            {
                return false;
            }

            using var handler = new SocketsHttpHandler
            {
                ConnectCallback = async (_, cancellationToken) =>
                {
                    var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                    try
                    {
                        await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), cancellationToken);
                        return new NetworkStream(socket, ownsSocket: true);
                    }
                    catch
                    {
                        socket.Dispose();
                        throw;
                    }
                },
            };

            // The host segment is ignored for unix-socket transport but is required to form a valid URI.
            return Ping(handler, "http://localhost/_ping");
        }

        private static bool PingTcp(string host, int port)
        {
            using var handler = new SocketsHttpHandler();
            return Ping(handler, $"http://{host}:{port}/_ping");
        }

        private static bool Ping(SocketsHttpHandler handler, string pingUrl)
        {
            try
            {
                using var client = new HttpClient(handler, disposeHandler: false) { Timeout = ProbeTimeout };
                using var cts = new CancellationTokenSource(ProbeTimeout);
                using var response = client.GetAsync(pingUrl, cts.Token).GetAwaiter().GetResult();
                return response.IsSuccessStatusCode;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
