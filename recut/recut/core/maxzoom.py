#!/usr/bin/env python
r"""
maxzoom.py -- CTB's tile grid: the reference zoom level, and how many tiles a
window costs at that level (FR-02, PRD acceptance #7).

Everything here mirrors cesium-terrain-builder's own arithmetic, because the
numbers are used to decide whether to refuse the user's submission and a
disagreement with the tool would make the gate lie. The two sources are:

  src/Grid.hpp          mInitialResolution = (extent.width / rootTiles) / tileSize
                        resolution(z)      = mInitialResolution / 2**z
                        zoomForResolution  = ceil(log2(mInitialResolution / res))
  src/GDALTiler.cpp     with no reprojection, resolution = |gt[1]| (pixel size)

For the geodetic (TMS-compatible) profile CTB uses rootTiles = 2, tileSize = 65,
so mInitialResolution = (360 / 2) / 65 = 180/65. That constant is the whole
content of `reference_maxzoom` -- it is not a tuned fudge factor, it is CTB's
own initial resolution, and it is what makes the four calibration crops in
baseline/maxzoom_calibration produce 15/14/13/12.
"""

import math
from dataclasses import dataclass

from .geoinfo import BBox

# CTB's geodetic profile: the root is two 65x65 tiles spanning 360 degrees.
CTB_INITIAL_RESOLUTION = (360.0 / 2.0) / 65.0

# Tiles are 65x65 samples, so one tile spans 65 - 1 = 64 sample intervals. The
# terrain format stores a 1-sample overlap with each neighbour; the overlap is
# why the count formula below uses closed intervals on both ends.
TILE_SIZE = 65

# Refuse to submit above this many tiles. Two million is ~9x the Chongqing
# baseline (223,495 tiles / 1.29 GB / 354 s), i.e. roughly 12 GB and an hour --
# past the point where the operator has almost certainly typed a window or a
# zoom level they did not mean, and would rather be told now than discover it
# 40 minutes into a slice. PRD FR-02 requires the tool to refuse, not warn.
MAX_TILES = 2_000_000

# Measured on this machine's baseline: the z=14 level holds 167,178 tiles in
# 995.4 MB, an average of 5,954 bytes. The deepest level is the one to use for
# an estimate because it dominates: it is 75% of the tile count and 77% of the
# bytes. Shallow levels are all layer.json and empty-water tiles at a few
# hundred bytes each and would drag a blended average well below reality.
AVERAGE_TILE_BYTES = 5954

# Baseline timing: 354 s for 223,495 tiles on 20 cores with ctb-tile's default
# thread count. Machine-specific and only ever shown as "about 6 minutes" --
# never used for a decision.
SECONDS_PER_TILE = 354.0 / 223495.0


def reference_maxzoom(pixel_size_deg: float) -> int:
    """The deepest zoom level worth generating for a given pixel size.

    This is what ctb-tile computes when `-s` is omitted. Recut always passes
    `-s` explicitly with this value, so that the level shown in the UI, written
    to the log, and actually sliced are the same number by construction rather
    than by coincidence -- see `slicer.build_argv`.

    `pixel_size_deg` is the raster's pixel size in degrees (1 arcsec =
    1/3600). For a projected CRS this formula does not apply and the caller
    must probe the tool instead.
    """
    if pixel_size_deg <= 0:
        raise ValueError(f"pixel size must be positive, got {pixel_size_deg}")
    return math.ceil(math.log2(CTB_INITIAL_RESOLUTION / pixel_size_deg))


def tile_index(lon: float, lat: float, zoom: int) -> tuple[int, int]:
    """The (x, y) tile containing a point, in CTB's TMS grid.

    y counts from the SOUTH pole, not the north -- the TMS convention, and the
    one the `.terrain` directory layout uses. A slippy-map y would be
    `2**zoom - 1 - y`; do not mix them.

    The arithmetic deliberately reproduces CTB's own double-precision
    expressions (Grid.hpp crsToPixels then pixelsToTile) rather than being
    algebraically rearranged into something that reads more cleanly. Floor of a
    value that lands exactly on a tile boundary is where a rearrangement would
    differ from the C++ by one, and matching the tool matters more here than
    the formula looking tidy.
    """
    f = (2.0 ** zoom) / 180.0
    return (math.floor((lon + 180.0) * f), math.floor((lat + 90.0) * f))


def tile_range(bbox: BBox, zoom: int) -> tuple[int, int, int, int]:
    """The inclusive (x0, y0, x1, y1) tile window covering `bbox`.

    Inclusive on both ends because that is what CTB does, not because it is the
    tidier convention. `GridIterator` takes the lower-left and upper-right
    corners, floors both to a tile, and loops `<=` over the result; see
    `count_tiles_at`. A half-open treatment of the upper edge -- which is what
    an interval purist would write -- would disagree with the tool by a row and
    a column whenever a window edge lands exactly on a tile boundary.
    """
    x0, y0 = tile_index(bbox.west, bbox.south, zoom)
    x1, y1 = tile_index(bbox.east, bbox.north, zoom)
    return x0, y0, x1, y1


def count_tiles_at(bbox: BBox, zoom: int) -> int:
    """How many tiles `bbox` produces at one zoom level.

    Transcribed from CTB's own `GridIterator::getSize()`:

        TileCoordinate ll = grid.crsToTile(gridExtent.getLowerLeft(),  zoom),
                       ur = grid.crsToTile(gridExtent.getUpperRight(), zoom);
        size += (zoomBounds.getWidth() + 1) * (zoomBounds.getHeight() + 1);

    The `+ 1` is the whole reason this counts exactly instead of nearly. It
    also means a window edge lying exactly on a tile boundary includes the tile
    beyond it, and at the eastern edge of the world that counts a column which
    does not exist -- CTB iterates it too and simply produces nothing for it.
    Faithfulness to the tool is worth more here than tidiness, because these
    numbers are used to verify the tool's own output.
    """
    x0, y0, x1, y1 = tile_range(bbox, zoom)
    return (x1 - x0 + 1) * (y1 - y0 + 1)


@dataclass(frozen=True)
class TileEstimate:
    """The cost of a slice, before committing to it."""

    bbox: BBox
    maxzoom: int
    per_zoom: dict[int, int]
    total: int
    bytes: int
    seconds: float

    def summary(self) -> str:
        gb = self.bytes / (1024 ** 3)
        minutes = self.seconds / 60.0
        return (f"最深 {self.maxzoom} 级，共 {self.total:,} 个瓦片，"
                f"约 {gb:.2f} GB，预计 {minutes:.1f} 分钟")


def estimate(bbox: BBox, maxzoom: int) -> TileEstimate:
    """Tile count per level from 0 to `maxzoom`, plus size and time estimates.

    Every level is counted, not extrapolated: the counts are exact and the
    arithmetic is trivial, so there is no reason to approximate. This is what
    lets FR-02's guardrail be a decision instead of a heuristic.
    """
    per_zoom = {z: count_tiles_at(bbox, z) for z in range(maxzoom + 1)}
    total = sum(per_zoom.values())
    return TileEstimate(
        bbox=bbox,
        maxzoom=maxzoom,
        per_zoom=per_zoom,
        total=total,
        bytes=total * AVERAGE_TILE_BYTES,
        seconds=total * SECONDS_PER_TILE,
    )


def validate_bbox(bbox: BBox) -> list[str]:
    """Reject windows that cannot describe a place on Earth.

    Returns Chinese problem strings for the UI to display; returns an empty
    list when the window is usable. Deliberately does not raise: FR-07 wants
    the offending field marked and the submit button disabled, which means the
    caller needs a value to render, not an exception to catch.

    Bounds are the WGS84 limits. An out-of-range window is not merely wrong, it
    is expensive: the 1-degree tiling loop happily enumerates thousands of
    tiles for a DEM that does not exist, and the failure surfaces much later as
    a slice full of nodata.
    """
    problems = []
    if not (-180.0 <= bbox.west <= 180.0 and -180.0 <= bbox.east <= 180.0):
        problems.append("经度必须在 -180 到 180 之间。")
    if not (-90.0 <= bbox.south <= 90.0 and -90.0 <= bbox.north <= 90.0):
        problems.append("纬度必须在 -90 到 90 之间。")
    if bbox.west >= bbox.east:
        problems.append("西边界必须小于东边界。")
    if bbox.south >= bbox.north:
        problems.append("南边界必须小于北边界。")
    return problems


def validate_cost(estimate_: TileEstimate) -> list[str]:
    """Reject submissions whose cost is out of proportion to any real job.

    PRD FR-02 requires the tool to *refuse* an out-of-control slice rather than
    warn about it, so this returns problems the caller must treat as blocking.
    """
    if estimate_.total > MAX_TILES:
        return [
            f"瓦片数 {estimate_.total:,} 超过上限 {MAX_TILES:,}"
            f"（最深 {estimate_.maxzoom} 级）。"
            "请降低最大层级或缩小范围。"
        ]
    return []
