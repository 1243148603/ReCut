r"""geoinfo.py against the baseline manifests.

The baseline `dem_gdalinfo.json` was produced by `baseline/commands/verify_dem.py`
and is the natural answer key for FR-01's per-file summary. It is authoritative
for everything except one field.

`bbox_wgs84` in that file is one whole pixel too generous on the east and south
sides, because verify_dem.py computes the span in the pixel-EDGE sense
(`cols * gt[1]`) and then applies a pixel-CENTRE half-pixel expansion on top.
The two conventions cannot be mixed: either the origin is the corner, in which
case the grid box is already the extent and nothing is added, or it is the
pixel centre, in which case the last pixel's centre is `cols - 1` steps away.
Measured on N28_E105, the baseline reports 1.000278 degrees of longitude for a
tile that is 3600 x 1/3600 = exactly 1.000000 degrees wide.

That error points the dangerous way. FR-01 is a gate that refuses input, and an
extent that is too generous makes it accept a mosaic with gaps in it. So Recut
implements the correct formula and this file pins the difference with numbers,
rather than silently matching the baseline or silently diverging from it.

`geotransform_pixel_centre` is also absent from Recut's output: the baseline
hardcodes it `true`, which states a conclusion about the file's product spec
while looking like a measurement. Recut records `bbox_convention` instead --
what was actually assumed in order to produce the numbers next to it.
"""

import json
import unittest
from pathlib import Path

from recut.core.geoinfo import (
    BBox,
    Origin,
    check_coverage,
    read_raster_info,
    suggest_origin,
    union_bbox,
)

BASELINE = Path(r"D:\Map\baseline\manifests\dem_gdalinfo.json")
DEM_DIR = Path(r"D:\Map\data\inputs\dem")

# Fields compared verbatim against the baseline. bbox_wgs84 is checked
# separately, and geotransform_pixel_centre is deliberately not produced.
SHARED_FIELDS = ("size", "dtype", "epsg", "geotransform",
                 "pixel_size_deg", "nodata", "file_bytes")


def _load_baseline():
    if not BASELINE.is_file():
        raise unittest.SkipTest(f"baseline manifest not present: {BASELINE}")
    with open(BASELINE, encoding="utf-8") as fh:
        return json.load(fh)


class TestAgainstBaselineManifest(unittest.TestCase):
    """Every input DEM must reproduce the baseline record, bbox aside."""

    @classmethod
    def setUpClass(cls):
        cls.baseline = _load_baseline()
        if not DEM_DIR.is_dir():
            raise unittest.SkipTest(f"DEM directory not present: {DEM_DIR}")
        cls.infos = {
            name: read_raster_info(DEM_DIR / name) for name in cls.baseline
        }

    def test_every_baseline_file_is_readable(self):
        self.assertEqual(sorted(self.infos), sorted(self.baseline))

    def test_shared_fields_match(self):
        for name, info in self.infos.items():
            got = info.summary(Origin.PIXEL_CENTER)
            for field in SHARED_FIELDS:
                with self.subTest(file=name, field=field):
                    self.assertEqual(got[field], self.baseline[name][field])

    def test_corrected_bbox_is_exactly_one_tile_wide(self):
        """A 3600-pixel tile of 1 arcsec spans exactly one degree. Not
        1.000278. This is the property the baseline breaks and the clearest
        statement of what "correct" means here."""
        for name, info in self.infos.items():
            box = info.data_bbox(Origin.PIXEL_CENTER)
            with self.subTest(file=name):
                self.assertAlmostEqual(box.width, 1.0, places=12)
                self.assertAlmostEqual(box.height, 1.0, places=12)

    def test_deviation_from_baseline_is_one_pixel_on_east_and_south(self):
        """Pin the exact disagreement so it cannot drift unnoticed.

        West and north agree exactly. East and south differ by exactly one
        pixel, and in the direction that makes the baseline too generous --
        baseline east is further east, baseline south is further south.
        """
        for name, info in self.infos.items():
            with self.subTest(file=name):
                px_x, px_y = info.pixel_size_deg
                correct = info.data_bbox(Origin.PIXEL_CENTER)
                base = self.baseline[name]["bbox_wgs84"]
                w, s, e, n = correct

                self.assertAlmostEqual(w - base[0], 0.0, places=12)
                self.assertAlmostEqual(n - base[3], 0.0, places=12)
                self.assertAlmostEqual(e - base[2], -px_x, places=12)
                self.assertAlmostEqual(s - base[1], px_y, places=12)

    def test_union_extent_is_the_nominal_six_by_five_degrees(self):
        """The 30 tiles are N28..N32 x E105..E110, so their union is 6 x 5
        degrees. The baseline's union comes out 6.000278 x 5.000278."""
        boxes = [i.data_bbox(Origin.PIXEL_CENTER) for i in self.infos.values()]
        union = union_bbox(boxes)
        self.assertAlmostEqual(union.width, 6.0, places=9)
        self.assertAlmostEqual(union.height, 5.0, places=9)


class TestOriginConvention(unittest.TestCase):
    """`Origin` must change the answer, and be impossible to leave implicit."""

    def setUp(self):
        if not DEM_DIR.is_dir():
            raise unittest.SkipTest(f"DEM directory not present: {DEM_DIR}")
        self.info = read_raster_info(DEM_DIR / "N28_E105.tif")

    def test_the_two_conventions_differ_by_half_a_pixel_per_side(self):
        px_x, px_y = self.info.pixel_size_deg
        edge = self.info.data_bbox(Origin.PIXEL_EDGE)
        center = self.info.data_bbox(Origin.PIXEL_CENTER)

        self.assertAlmostEqual(edge.west - center.west, px_x / 2, places=12)
        self.assertAlmostEqual(edge.north - center.north, -px_y / 2, places=12)
        # Both are exactly one degree wide; the disagreement is an offset, not
        # a size difference. That is why the wrong convention is hard to spot.
        self.assertAlmostEqual(edge.width, center.width, places=12)

    def test_glo30_is_suggested_as_pixel_center_with_reasons(self):
        origin, reasons = suggest_origin(self.info)
        self.assertIs(origin, Origin.PIXEL_CENTER)
        self.assertTrue(reasons, "a suggestion without evidence cannot be audited")

    def test_a_warp_product_is_suggested_as_pixel_edge(self):
        """A gdalwarp output has its origin exactly on the -te window edge and
        a span that is not a whole degree, so it must not be mistaken for a
        GLO-30 tile."""
        from recut.core.geoinfo import RasterInfo
        warp_like = RasterInfo(
            path=Path("chongqing_dem.tif"),
            size=(17640, 14760),
            dtype="Float32",
            epsg="4326",
            geotransform=(105.3, 1 / 3600, 0.0, 32.2, 0.0, -1 / 3600),
            nodata=None,
            file_bytes=0,
        )
        origin, _ = suggest_origin(warp_like)
        self.assertIs(origin, Origin.PIXEL_EDGE)


class TestCoverageCheck(unittest.TestCase):
    """FR-01: the gate must quantify the shortfall, not just report one."""

    def setUp(self):
        if not DEM_DIR.is_dir():
            raise unittest.SkipTest(f"DEM directory not present: {DEM_DIR}")
        self.infos = [read_raster_info(p)
                      for p in sorted(DEM_DIR.glob("*.tif"))]

    def test_the_mosaic_window_is_inside_the_inputs(self):
        """105.3/28.1/110.2/32.2 is the window the baseline mosaic used. It is
        inset on all four sides precisely so the half-pixel at the edges never
        matters; this asserts that reasoning still holds."""
        target = BBox(105.3, 28.1, 110.2, 32.2)
        result = check_coverage(self.infos, target, Origin.PIXEL_CENTER)
        self.assertTrue(result.covered, result.describe())
        self.assertEqual(result.gaps, ())

    def test_a_window_reaching_past_the_inputs_reports_which_side(self):
        target = BBox(100.0, 28.1, 110.2, 32.2)
        result = check_coverage(self.infos, target, Origin.PIXEL_CENTER)
        self.assertFalse(result.covered)
        self.assertEqual([g.side for g in result.gaps], ["west"])
        gap = result.gaps[0]
        # Under the centre convention the E105 column of tiles reaches west to
        # 105 minus a whole pixel -- the origin is the *centre* of the first
        # pixel, so one more half pixel sits beyond it. Hence 5 degrees less
        # 1/3600, not 5 degrees less half of that.
        self.assertAlmostEqual(gap.shortfall_deg, 5.0 - 1 / 3600, places=9)
        self.assertGreater(gap.shortfall_m, 400_000)

    def test_no_inputs_is_not_coverage(self):
        result = check_coverage([], BBox(105.3, 28.1, 110.2, 32.2),
                                Origin.PIXEL_CENTER)
        self.assertFalse(result.covered)
        self.assertIsNone(result.available)


if __name__ == "__main__":
    unittest.main()
