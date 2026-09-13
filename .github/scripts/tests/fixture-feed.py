#!/usr/bin/env python3
"""Offline stand-in for the nuget.org flat-container API, for deploy-dependency-guard.test.sh.

Serves `/<lowercased-package-id>/index.json` from a fixture directory holding one
`<lowercased-package-id>.json` file per published package, so the guard under test can be
exercised without ever reaching the network.

INVARIANT: stdlib only. The harness that starts this stub runs in a GitHub Actions job with no
`setup-python` step and no `pip install`, so a third-party import would turn a hermetic test into
an environment dependency.

INVARIANT: lookups are case-sensitive and fixture names are lowercased, mirroring the real
flat-container, which 404s a mixed-case id. A guard that forgot to lowercase would therefore fail
the all-deps-present case rather than silently passing it.

Usage: fixture-feed.py --fixture-dir DIR [--fail-id ID] [--log-file FILE]
  --fixture-dir: directory of `<lowercased-id>.json` flat-container index documents; an id with
                 no file present answers 404, which is the "never published" case
  --fail-id:     lowercased id that answers 500, which is the transport/infrastructure case
  --log-file:    appends one request path per line, so a caller can assert how many times an id
                 was queried (the multi-framework dedupe assertion)

Binds 127.0.0.1 on an ephemeral port and prints that port on stdout, then serves until killed.
"""
import argparse
import sys
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path


def build_handler(fixture_dir, fail_id, log_file):
    class FlatContainerHandler(BaseHTTPRequestHandler):
        protocol_version = "HTTP/1.1"

        def log_message(self, format, *args):
            # The default implementation writes to stderr, which would interleave with the
            # harness's own assertion output and read as test noise.
            pass

        def record_request(self):
            if log_file is None:
                return
            with open(log_file, "a", encoding="utf-8") as handle:
                handle.write(self.path + "\n")

        def respond(self, status, body):
            payload = body.encode("utf-8")
            self.send_response(status)
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(payload)))
            self.end_headers()
            self.wfile.write(payload)

        def do_GET(self):
            self.record_request()
            parts = self.path.lstrip("/").split("/")
            if len(parts) != 2 or parts[1] != "index.json" or not parts[0]:
                self.respond(404, '{"error":"not a flat-container index path"}')
                return

            package_id = parts[0]
            if fail_id is not None and package_id == fail_id:
                self.respond(500, '{"error":"fixture feed is unavailable"}')
                return

            index_path = fixture_dir / (package_id + ".json")
            if not index_path.is_file():
                self.respond(404, '{"error":"package id not found"}')
                return

            self.respond(200, index_path.read_text(encoding="utf-8"))

    return FlatContainerHandler


def main():
    parser = argparse.ArgumentParser(description="Offline flat-container fixture feed.")
    parser.add_argument("--fixture-dir", required=True)
    parser.add_argument("--fail-id", default=None)
    parser.add_argument("--log-file", default=None)
    arguments = parser.parse_args()

    fixture_dir = Path(arguments.fixture_dir)
    if not fixture_dir.is_dir():
        print("fixture directory does not exist: %s" % fixture_dir, file=sys.stderr)
        return 2

    handler = build_handler(fixture_dir, arguments.fail_id, arguments.log_file)
    server = ThreadingHTTPServer(("127.0.0.1", 0), handler)
    # The port is the only thing written to stdout, and it is flushed immediately: the harness
    # blocks on reading this line to know the stub is bound and ready.
    print(server.server_address[1], flush=True)
    server.serve_forever()
    return 0


if __name__ == "__main__":
    sys.exit(main())
