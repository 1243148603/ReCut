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
  python serve.py                      # serve this directory (repo root) at 127.0.0.1:8080
  python serve.py --tileset <dir>      # 指定瓦片集（挂到 /__tileset__/，放在哪个盘都行）
  python serve.py --port 9000
  python serve.py --directory <dir>    # 换服务根（默认：本脚本所在目录）

Then open the viewer URL printed on startup (it carries ?tiles=...&debug=1).

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
import glob
import http.server
import os
import sys
import urllib.parse

GZIP_MAGIC = b"\x1f\x8b"


class TerrainHandler(http.server.SimpleHTTPRequestHandler):
    """SimpleHTTPRequestHandler that declares terrain tiles as gzip-encoded."""

    # Set by send_head(); read by end_headers(). Class-level defaults so a 404
    # (where send_head takes the send_error path) never trips an AttributeError.
    _gzip_ok = False
    _terrain_response = False
    _no_store = False

    def __init__(self, *args, tileset_dir=None, **kwargs):
        # 要预览的瓦片集目录（经 /__tileset__/ 挂载）；None 表示没有。
        self.tileset_dir = tileset_dir
        super().__init__(*args, **kwargs)

    def translate_path(self, path):
        """把 /__tileset__/… 映射到瓦片集目录，其余走常规静态服务。

        这样**瓦片可以放在任意位置**（仓库外、另一个盘），不必为了预览先拷进仓库树；
        `?tiles=./__tileset__/` 就能用。路径做了规范化并丢弃 '..'，避免目录穿越。
        """
        prefix = "/__tileset__/"
        if self.tileset_dir and path.startswith(prefix):
            rel = path[len(prefix):].split("?", 1)[0].split("#", 1)[0]
            rel = urllib.parse.unquote(rel).replace("\\", "/")
            parts = [p for p in rel.split("/") if p not in ("", ".", "..")]
            return os.path.join(os.path.abspath(self.tileset_dir), *parts)
        return super().translate_path(path)

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
    request_queue_size = 128
    allow_reuse_address = True


def find_tilesets(root, max_depth=4):
    """找出 root 下所有含 layer.json 的目录（瓦片集），供预览页的 ?tiles= 用。

    Cesium 的入口就是 layer.json，所以"哪个目录是瓦片集"不需要用户解释。
    限制深度并跳过 .git/node_modules：在一个大仓库里全盘扫会很慢。
    """
    found = []
    root_depth = root.rstrip(os.sep).count(os.sep)
    for dirpath, dirnames, filenames in os.walk(root):
        dirnames[:] = [d for d in dirnames if d not in (".git", "node_modules")]
        if dirpath.count(os.sep) - root_depth >= max_depth:
            dirnames[:] = []
        if "layer.json" in filenames:
            found.append(dirpath)
            dirnames[:] = []
    return sorted(found)


def main():
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[2])
    ap.add_argument("--port", type=int, default=8080)
    ap.add_argument("--bind", default="127.0.0.1",
                    help="interface to bind (default 127.0.0.1, local only)")
    ap.add_argument("--directory", default=os.path.dirname(os.path.abspath(__file__)),
                    help="root to serve (default: this script's directory)")
    ap.add_argument("--tileset", default=None,
                    help="要预览的瓦片集目录（含 layer.json）；默认自动发现")
    args = ap.parse_args()

    root = os.path.abspath(args.directory)
    if not os.path.isdir(root):
        print(f"error: not a directory: {root}", file=sys.stderr)
        return 2

    # 选一个瓦片集给预览 URL 用：--tileset 指定优先，否则自动发现。
    tileset = None
    if args.tileset:
        tileset = os.path.abspath(args.tileset)
        if not os.path.exists(os.path.join(tileset, "layer.json")):
            print(f"warning: {tileset} 里没有 layer.json —— Cesium 靠它识别瓦片集，"
                  "缺了 3D 加载不了。", file=sys.stderr)
            # 常见误指：把"下载下来的 DEM 分幅目录"当成切片产物。给一句能照做的提示，
            # 比只说"没有 layer.json"有用。
            if glob.glob(os.path.join(tileset, "*.tif")) or glob.glob(os.path.join(tileset, "*.tiff")):
                print("         这个目录看着是 DEM 分幅（*.tif）：要先拼接裁剪 → 切片 → 打包，"
                      "打包那一步才会写 layer.json。", file=sys.stderr)
            else:
                print("         请指向含 layer.json 的切片产物目录；没有就对该目录跑一次「打包」。",
                      file=sys.stderr)
    else:
        found = find_tilesets(root)
        if found:
            tileset = found[0]
            if len(found) > 1:
                print(f"发现 {len(found)} 个瓦片集，默认用第一个（--tileset 可指定）：")
                for t in found:
                    print("    " + os.path.relpath(t, root).replace(os.sep, "/"))

    handler = functools.partial(TerrainHandler, directory=root, tileset_dir=tileset)
    try:
        httpd = Server((args.bind, args.port), handler)
    except OSError as exc:
        print(f"error: cannot bind {args.bind}:{args.port}: {exc}", file=sys.stderr)
        print("       (is another server already running on that port?)",
              file=sys.stderr)
        return 1

    # 预览 URL 带上 ?tiles=：瓦片集经 /__tileset__/ 挂载，所以它在哪个盘都行。
    query = ""
    if tileset:
        # 手写而不是 urlencode：编码后 %2F 一串不好读，浏览器两种都认。
        query = "?tiles=./__tileset__/&debug=1"

    print(f"serving {root}")
    print(f"  viewer : http://{args.bind}:{args.port}/terrain_viewer.html{query}")
    if tileset:
        print(f"  tileset: {tileset}   （挂载在 /__tileset__/；换个瓦片集重跑 --tileset）")
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
