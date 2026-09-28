#!/usr/bin/env python
r"""
geoinfo.py -- A raster's true footprint, and whether a set of DEM tiles covers
a target window (FR-01).

The one thing worth reading this file for is `Origin`.

A geotransform says where pixel (0, 0) is. It does not say whether that point is
the pixel's CENTRE or its top-left CORNER, and the answer moves the data half a
pixel -- about 15 m -- on every side. No measurement of the file can settle it:
both readings describe byte-for-byte identical data. Only the product spec
knows. So the convention is an explicit, required argument everywhere below,
never a default that could be quietly wrong.

Two conventions meet in this pipeline, one at each end:

  * Copernicus GLO-30 tiles (the input) are pixel-CENTRE. 3600 pixels of one
    arc-second span exactly one degree, and the origin sits half a pixel inside
    the nominal edge: N28_E105 starts at 104.999861, not 105.0.
  * gdalwarp products (this program's own output) are pixel-EDGE. The origin is
    the corner, and gdalinfo's corner coordinates are already the true extent.

Each is wrong by half a pixel read as the other. FR-01 decides whether to refuse
the user's input, so an error in the "too generous" direction silently lets
through a mosaic with gaps in it -- which is what the baseline verify_dem.py
does, by a full pixel rather than half. See `data_bbox`.
"""

import math
import os
from dataclasses import dataclass
from enum import Enum
from pathlib import Path
from typing import Iterable, NamedTuple

from osgeo import gdal

# GDAL 4.0 will raise exceptions by default; until then it warns on every
# import that nobody has chosen. Choosing explicitly is not cosmetic: with
# exceptions off, a corrupt file makes gdal.Open return None and every other
# call return a silent default, which is the opposite of NFR-04's "never fail
# silently". Declared once, here, so the whole program shares one behaviour.
gdal.UseExceptions()

# Mean metres per degree. Used only to render a shortfall in units an operator
# can picture ("差 687 米" beats "差 0.0062 度"); never used for geometry, so the
# third decimal place does not matter and these are not the WGS84 ellipsoidal
# values at any particular latitude.
M_PER_DEG_LAT = 110574.0
M_PER_DEG_LON_EQUATOR = 111320.0


class Origin(Enum):
    """Where the geotransform origin sits relative to the first pixel."""

    PIXEL_EDGE = "pixel-edge"
    PIXEL_CENTER = "pixel-center"


class BBox(NamedTuple):
    """An extent in WGS84 degrees, ordered west, south, east, north.

    That order is not arbitrary: it is the order `gdalwarp -te` takes. Getting
    it wrong is silent and produces a plausible-looking wrong answer, so there
    is one order in this program and it is this one. (Note `gdal_translate
    -projwin` uses west, NORTH, east, SOUTH -- do not assume they match.)
    """

    west: float
    south: float
    east: float
    north: float

    @property
    def width(self) -> float:
        return self.east - self.west

    @property
    def height(self) -> float:
        return self.north - self.south

    @property
    def center(self) -> tuple[float, float]:
        return ((self.west + self.east) / 2.0, (self.south + self.north) / 2.0)

    def contains(self, other: "BBox") -> bool:
        return (self.west <= other.west and self.south <= other.south
                and self.east >= other.east and self.north >= other.north)

    def as_te(self) -> list[float]:
        """The four numbers to hand to `gdalwarp -te`, in its order."""
        return [self.west, self.south, self.east, self.north]

    def describe(self) -> str:
        return (f"{self.west:.6f}, {self.south:.6f}, "
                f"{self.east:.6f}, {self.north:.6f}")


class RasterError(RuntimeError):
    """A file could not be opened, or is not the kind of raster expected."""


def data_bbox(gt, cols: int, rows: int, origin: Origin) -> BBox:
    """The footprint the raster's pixels actually occupy.

    The two branches differ by exactly half a pixel per side, and picking the
    wrong one is not detectable from the result -- both look like sane numbers.

    PIXEL_EDGE: gt[0], gt[3] are the north-west corner, so the grid box
    `cols x rows` cells wide IS the footprint. Nothing to correct.

    PIXEL_CENTER: gt[0], gt[3] are the centre of the FIRST pixel, so the
    footprint starts half a pixel before the origin and ends half a pixel after
    the LAST pixel's centre. That last step is `cols - 1` pixels, not `cols` --
    the baseline verify_dem.py used `cols` and so reported every 1-degree
    GLO-30 tile as 1.000278 degrees wide. One whole pixel too generous, on the
    east and south sides only, in the direction that makes the FR-01 coverage
    gate pass inputs it should refuse.

    The half-pixel is derived from the raster's own pixel size rather than
    hardcoded to GLO-30's 1/3600: a DEM resampled to 2 arcsec has a
    proportionally larger half-pixel, and a constant would silently understate
    it.
    """
    # gt[5] is negative for north-up rasters, so abs() before halving. The
    # signs work themselves out below because every use keeps gt's own sign.
    half_x = abs(gt[1]) / 2.0
    half_y = abs(gt[5]) / 2.0

    if origin is Origin.PIXEL_EDGE:
        west = gt[0]
        north = gt[3]
        east = gt[0] + cols * gt[1]
        south = gt[3] + rows * gt[5]
    else:
        west = gt[0] - half_x
        north = gt[3] + half_y
        east = gt[0] + (cols - 1) * gt[1] + half_x
        south = gt[3] + (rows - 1) * gt[5] - half_y

    return BBox(west, south, east, north)


@dataclass(frozen=True)
class RasterInfo:
    """What FR-01 shows per input file: path / CRS / extent / resolution.

    Deliberately unopinionated about `Origin`: the same file has two possible
    footprints and which one is real is the caller's product knowledge, not a
    property of the bytes. Ask for one explicitly via `data_bbox`.
    """

    path: Path
    size: tuple[int, int]
    dtype: str
    epsg: str | None
    geotransform: tuple[float, ...]
    nodata: float | None
    file_bytes: int

    @property
    def cols(self) -> int:
        return self.size[0]

    @property
    def rows(self) -> int:
        return self.size[1]

    @property
    def pixel_size_deg(self) -> tuple[float, float]:
        """Pixel size in degrees, both axes positive.

        gt[5] is negative for a north-up raster; taking the absolute value here
        means every consumer can stop worrying about the sign, and the sign
        stays available in `geotransform` for anyone who needs it.
        """
        return (abs(self.geotransform[1]), abs(self.geotransform[5]))

    def data_bbox(self, origin: Origin) -> BBox:
        return data_bbox(self.geotransform, self.cols, self.rows, origin)

    def summary(self, origin: Origin) -> dict:
        """The per-file record FR-01 displays and the metadata json stores.

        Field names and order match the baseline `dem_gdalinfo.json` so the two
        can be compared key by key -- see tests/test_geoinfo.py.

        `geotransform_pixel_centre` is NOT carried over from the baseline. That
        baseline field was hardcoded `true`, which is an assertion about the
        file's product spec dressed up as a measurement. `bbox_convention`
        records what was actually assumed when computing the extent, which is
        the thing a reader needs in order to reproduce the numbers.
        """
        bbox = self.data_bbox(origin)
        return {
            "size": [self.cols, self.rows],
            "dtype": self.dtype,
            "epsg": self.epsg,
            "geotransform": list(self.geotransform),
            "pixel_size_deg": list(self.pixel_size_deg),
            "bbox_wgs84": [bbox.west, bbox.south, bbox.east, bbox.north],
            "bbox_convention": origin.value,
            "nodata": self.nodata,
            "file_bytes": self.file_bytes,
        }


def read_raster_info(path) -> RasterInfo:
    """Open a raster read-only and summarise it. Raises RasterError if it will
    not open.

    Reads only header metadata -- no pixels -- so this is cheap enough to run on
    every file during a UI refresh.
    """
    path = Path(path)
    try:
        ds = gdal.Open(str(path), gdal.GA_ReadOnly)
    except RuntimeError as exc:
        raise RasterError(f"无法打开栅格文件：{path}（{exc}）") from exc
    if ds is None:
        raise RasterError(f"无法打开栅格文件：{path}")
    try:
        band = ds.GetRasterBand(1)
        if band is None:
            raise RasterError(f"栅格没有波段：{path}")
        srs = ds.GetSpatialRef()
        return RasterInfo(
            path=path,
            size=(ds.RasterXSize, ds.RasterYSize),
            dtype=gdal.GetDataTypeName(band.DataType),
            # GetAuthorityCode(None) returns whichever authority the CRS
            # carries, which for these products is EPSG. None when the file
            # has no CRS at all -- reported as None rather than guessed.
            epsg=srs.GetAuthorityCode(None) if srs else None,
            geotransform=tuple(ds.GetGeoTransform()),
            nodata=band.GetNoDataValue(),
            file_bytes=os.path.getsize(path),
        )
    finally:
        # CPython would collect this anyway, but GDAL keeps a file handle and a
        # dataset cache behind it. Closing explicitly keeps a scan over
        # thousands of tiles from accumulating handles.
        ds = None


def union_bbox(boxes: Iterable[BBox]) -> BBox | None:
    """The smallest box containing all of `boxes`, or None if there are none."""
    boxes = list(boxes)
    if not boxes:
        return None
    return BBox(
        min(b.west for b in boxes),
        min(b.south for b in boxes),
        max(b.east for b in boxes),
        max(b.north for b in boxes),
    )


class CoverageGap(NamedTuple):
    """One side of the target window the inputs do not reach."""

    side: str
    shortfall_deg: float
    shortfall_m: float


@dataclass(frozen=True)
class CoverageResult:
    covered: bool
    target: BBox
    available: BBox | None
    gaps: tuple[CoverageGap, ...]

    def describe(self) -> str:
        """A one-paragraph verdict for the operator.

        FR-01 requires the check to say where the shortfall is, not just that
        one exists: "coverage insufficient" sends the operator back to the
        download page to guess, while "south edge short by 0.4 deg (44 km)"
        tells them which tiles to fetch.
        """
        if self.available is None:
            return "没有可用的输入数据。"
        if self.covered:
            return f"覆盖完整。输入范围 {self.available.describe()}。"

        parts = []
        for gap in self.gaps:
            parts.append(f"{_SIDE_LABELS[gap.side]}差 {gap.shortfall_deg:.4f}°"
                         f"（约 {gap.shortfall_m / 1000:.1f} km）")
        return ("输入数据未覆盖目标范围：" + "；".join(parts)
                + f"。输入合计范围 {self.available.describe()}。")


_SIDE_LABELS = {
    "west": "西边界",
    "south": "南边界",
    "east": "东边界",
    "north": "北边界",
}


def check_coverage(infos: Iterable[RasterInfo], target: BBox,
                   origin: Origin) -> CoverageResult:
    """Does the union of `infos` contain `target`?

    `origin` must describe the inputs, not the target: this compares the input
    files' footprint against a window the user typed, and only the inputs have
    a geotransform whose origin convention is in question.
    """
    available = union_bbox(info.data_bbox(origin) for info in infos)
    if available is None:
        return CoverageResult(False, target, None, ())

    # Metres are computed per-side rather than from a single scale factor: a
    # degree of longitude is 111 km at the equator and 94 km at Chongqing's
    # latitude, so quoting east/west shortfalls with the equatorial figure
    # would overstate them by 18%.
    center_lat = target.center[1]
    m_per_deg_lon = M_PER_DEG_LON_EQUATOR * math.cos(math.radians(center_lat))

    gaps = []
    for side, shortfall, m_per_deg in (
        ("west", available.west - target.west, m_per_deg_lon),
        ("south", available.south - target.south, M_PER_DEG_LAT),
        ("east", target.east - available.east, m_per_deg_lon),
        ("north", target.north - available.north, M_PER_DEG_LAT),
    ):
        # Positive means the input falls short on that side. Compare against a
        # tolerance rather than zero: the arithmetic above is a chain of
        # floating-point subtractions of degree-sized numbers, and demanding
        # exact equality would report a phantom gap of 1e-16 degrees on a
        # perfectly aligned input.
        if shortfall > 1e-9:
            gaps.append(CoverageGap(side, shortfall, shortfall * m_per_deg))

    return CoverageResult(not gaps, target, available, tuple(gaps))


# GLO-30's signature, and the reason it is only a *suggestion*: the two
# conventions differ by half a pixel on each side and the bytes are identical
# either way, so no amount of inspection can promote this to a decision. It is
# offered to the wizard as a default the operator can see and override.
_GLO30_PIXEL_DEG = 1.0 / 3600.0


def suggest_origin(info: RasterInfo) -> tuple[Origin, list[str]]:
    """Guess which convention a file uses, with the evidence that led there.

    Returns the guess plus human-readable reasons, so the UI can show *why* it
    pre-selected something instead of presenting an unexplained default. The
    caller decides whether to accept it; nothing here is applied silently.

    A file whose span is a whole number of degrees and whose origin is offset
    from a whole-degree grid line by half a pixel is the GLO-30 pattern. A
    gdalwarp product fails on both counts: its extent is whatever `-te` asked
    for (rarely a whole degree) and its origin sits exactly on the window edge.
    """
    reasons = []
    half_x = info.pixel_size_deg[0] / 2.0
    half_y = info.pixel_size_deg[1] / 2.0
    if half_x <= 0 or half_y <= 0:
        return Origin.PIXEL_EDGE, ["像元尺寸为零或负，无法判断原点口径"]

    span_x = info.cols * info.pixel_size_deg[0]
    span_y = info.rows * info.pixel_size_deg[1]
    origin_x, origin_y = info.geotransform[0], info.geotransform[3]

    # The origin's distance from the nearest whole-degree line, expressed in
    # half pixels. GLO-30 lands on exactly 1.0 on both axes, because its origin
    # is placed half a pixel inside the nominal degree boundary.
    off_x = abs(origin_x - round(origin_x)) / half_x
    off_y = abs(origin_y - round(origin_y)) / half_y
    origin_is_inset = abs(off_x - 1.0) < 1e-6 and abs(off_y - 1.0) < 1e-6
    reasons.append(
        f"原点距最近整度线 {off_x:.6f} / {off_y:.6f} 个半像元"
        + ("（恰为半像元，符合整度分幅的像元中心原点）" if origin_is_inset
           else "（不在半像元处）"))

    span_is_whole = (
        abs(span_x - round(span_x)) < 1e-9 and abs(span_y - round(span_y)) < 1e-9)
    reasons.append(
        f"跨度 {span_x:.6f}° x {span_y:.6f}°"
        + ("为整度数" if span_is_whole else "不是整度数"))

    is_arcsec = abs(info.pixel_size_deg[0] - _GLO30_PIXEL_DEG) < 1e-12
    reasons.append("像元大小 " + ("为 1 角秒" if is_arcsec else
                              f"为 {info.pixel_size_deg[0]:.8f}°（非 1 角秒）"))

    if span_is_whole and origin_is_inset and is_arcsec:
        return Origin.PIXEL_CENTER, reasons
    return Origin.PIXEL_EDGE, reasons
