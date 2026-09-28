#!/usr/bin/env python
r"""
serve.py -- static file server for the Cesium terrain tileset in this repo.

Why this exists (and why `python -m http.server` is NOT enough):
  `ctb-tile` writes GZIPPED terrain tiles. CTB's own README, line 28:
  "This creates gzipped terrain tiles from a GDAL raster". The file on disk
  therefore starts with the gzip magic 1f 8b, and its 86..2555 bytes inflate
  to the full 8452-byte heightmap tile.

  Cesium's CesiumTerrainProvider does NOT gunzip in JavaScript -- it relies on
  the browser to have transparently decompressed the response already, which
  only happens if the server sends `Content-Encoding: gzip`. Without that
  header the browser hands Cesium the compressed bytes, and the heightmap
  parser dies on the very first line:

      new Uint16Array(buffer, 0, 65 * 65)   // wants 8450 bytes

  against an 86-byte buffer -> RangeError. The tile request shows 200 in the
  access log, no tile ever renders, and the globe stays pinned at zoom 0 with
  nothing below it requested. That is the whole failure mode.

  CTB's own deployment docs solve this in Apache with
  `Header set Content-Encoding gzip` for /\.terrain$/; this script is the
  equivalent for local viewing.

Usage:
  python serve.py                      # 127.0.0.1:8080, serves this directory
  python serve.py --port 9000
  python serve.py --directory data/outputs/cesium_terrain

Then open http://127.0.0.1:8080/terrain_viewer.html

The gzip header is applied ONLY when the file actually begins with the gzip
magic, so this server also does the right thing if the tileset is ever
re-sliced uncompressed. Each request is logged with a `[gzip]` marker when the
header was set -- that marker is the quickest way to confirm the deployment is
correct.

.terrain and .json responses are sent with `Cache-Control: no-store`. Without
it a browser applies heuristic freshness and can keep replaying a stale
response from before a fix, which makes a corrected server look broken.

(Raw string: the Windows paths and regex in this docstring are literal.)
"""

import argparse
import functools
import http.server
import os
import sys

GZIP_MAGIC = b"\x1f\x8b"


class TerrainHandler(http.server.SimpleHTTPRequestHandler):
    """SimpleHTTPRequestHandler that declares terrain tiles as gzip-encoded."""

    # Set by send_head(); read by end_headers(). Class-level defaults so a 404
    # (where send_head takes the send_error path) never trips an AttributeError.
    _gzip_ok = False
    _terrain_response = False
    _no_store = False

    def send_head(self):
        # Decide before super() emits anything, because Content-Encoding must
        # accompany the headers, not follow the body.
        self._gzip_ok = False
        path_only = self.path.split("?", 1)[0].lower()
        self._terrain_response = path_only.endswith(".terrain")

        if self._terrain_response or path_only.endswith(".json"):
            self._no_store = True
            if self._terrain_response:
                try:
                    with open(self.translate_path(self.path), "rb") as fh:
                        # Sniff rather than trust the extension: an uncompressed
                        # tileset must NOT be labelled gzip.
                        self._gzip_ok = fh.read(2) == GZIP_MAGIC
                except OSError:
                    # Missing file -> super() will send 404, header stays off.
                    pass
        return super().send_head()

    def end_headers(self):
        if self._gzip_ok:
            # Content-Length was already sent by super().send_head() and is the
            # COMPRESSED size, which is exactly right: Content-Encoding tells the
            # client to inflate, and length describes the encoded body.
            self.send_header("Content-Encoding", "gzip")
            self.send_header("Vary", "Accept-Encoding")

        if self._no_store:
            # Critical for diagnosis: SimpleHTTPRequestHandler sends only
            # Last-Modified, no Cache-Control, so browsers apply *heuristic*
            # freshness (typically ~10% of the file's age). The tiles are days
            # old, so a broken response fetched before the Content-Encoding fix
            # stays fresh in the cache for hours and keeps being reused -- the
            # page looks broken even though the server is now correct.
            #
            # no-store forbids that entirely. A 304 revalidation would not be
            # enough: the browser would reuse the stored BODY together with the
            # stored headers, i.e. still without Content-Encoding.
            #
            # Caching is pointless for this server anyway -- it is a local,
            # single-user viewer over files already on local disk.
            self.send_header("Cache-Control", "no-store")

        super().end_headers()

    def log_message(self, fmt, *args):
        # Append the gzip marker so one glance at the log shows whether the
        # Content-Encoding header went out on the tile requests.
        marker = " [gzip]" if self._gzip_ok else ""
        super().log_message(fmt + marker, *args)


class Server(http.server.ThreadingHTTPServer):
    # Threaded: the globe fires dozens of tile requests in parallel and a
    # single-threaded server serialises them behind one another.
    daemon_threads = True
    allow_reuse_address = True


def main():
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[2])
    ap.add_argument("--port", type=int, default=8080)
    ap.add_argument("--bind", default="127.0.0.1",
                    help="interface to bind (default 127.0.0.1, local only)")
    ap.add_argument("--directory", default=os.path.dirname(os.path.abspath(__file__)),
                    help="root to serve (default: this script's directory)")
    args = ap.parse_args()

    root = os.path.abspath(args.directory)
    if not os.path.isdir(root):
        print(f"error: not a directory: {root}", file=sys.stderr)
        return 2

    handler = functools.partial(TerrainHandler, directory=root)
    try:
        httpd = Server((args.bind, args.port), handler)
    except OSError as exc:
        print(f"error: cannot bind {args.bind}:{args.port}: {exc}", file=sys.stderr)
        print("       (is another server already running on that port?)",
              file=sys.stderr)
        return 1

    print(f"serving {root}")
    print(f"  viewer : http://{args.bind}:{args.port}/terrain_viewer.html")
    print(f"  terrain: http://{args.bind}:{args.port}/data/outputs/cesium_terrain/")
    print("  .terrain responses carry Content-Encoding: gzip (logged as [gzip])")
    print("Ctrl-C to stop.")
    try:
        httpd.serve_forever()
    except KeyboardInterrupt:
        print("\nstopped.")
    finally:
        httpd.server_close()
    return 0


if __name__ == "__main__":
    sys.exit(main())
