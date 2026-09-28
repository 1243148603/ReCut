r"""maxzoom.py against the baseline calibration.

Two independent answer keys, both produced by running the real toolchain:

  baseline/maxzoom_calibration/calibration_results.txt
      ctb-extents' auto zoom level for four crops of the same window at
      1/2, 1, 2 and 3 arcsec. This is the check that the reference-zoom formula
      agrees with CTB and not just with itself.

  baseline/manifests/tile_counts.txt
      the tile count per zoom level for the Chongqing window, counted from the
      actual 223,495-file output tree. The formula has to reproduce all fifteen
      levels exactly -- not approximately, not within a few percent. It can,
      because it is CTB's own arithmetic rather than an area estimate.

Both files are read rather than transcribed, so a test failure means the code
disagrees with the recorded toolchain output, not with a number someone typed
into this file.
"""

import re
import unittest
from pathlib import Path

from recut.core.geoinfo import BBox
from recut.core.maxzoom import (
    MAX_TILES,
    count_tiles_at,
    estimate,
    reference_maxzoom,
    tile_index,
    tile_range,
    validate_bbox,
    validate_cost,
)

CALIBRATION = Path(r"D:\Map\baseline\maxzoom_calibration\calibration_results.txt")
TILE_COUNTS = Path(r"D:\Map\baseline\manifests\tile_counts.txt")

# The window the baseline mosaic used and the tiles were cut from.
CHONGQING = BBox(105.3, 28.1, 110.2, 32.2)

# Pixel sizes as exact fractions of a degree, matching what
# 05_maxzoom_calibration.cmd passed to gdal_translate -tr.
CALIBRATED_PIXEL_SIZES = {
    "res_05_3600": 0.5 / 3600.0,
    "res_1_3600": 1.0 / 3600.0,
    "res_2_3600": 2.0 / 3600.0,
    "res_3_3600": 3.0 / 3600.0,
}


def _read_calibration():
    if not CALIBRATION.is_file():
        raise unittest.SkipTest(f"calibration not present: {CALIBRATION}")
    out = {}
    for line in CALIBRATION.read_text(encoding="utf-8").splitlines():
        match = re.match(r"(\S+)\s*->\s*maxzoom\s+(\d+)", line.strip())
        if match:
            out[match.group(1)] = int(match.group(2))
    return out


def _read_tile_counts():
    if not TILE_COUNTS.is_file():
        raise unittest.SkipTest(f"tile counts not present: {TILE_COUNTS}")
    per_zoom, total = {}, None
    for line in TILE_COUNTS.read_text(encoding="utf-8").splitlines():
        match = re.match(r"zoom\s+(\d+):\s+(\d+)", line.strip())
        if match:
            per_zoom[int(match.group(1))] = int(match.group(2))
            continue
        match = re.match(r"total:\s+(\d+)", line.strip())
        if match:
            total = int(match.group(1))
    return per_zoom, total


class TestReferenceMaxzoom(unittest.TestCase):
    """PRD acceptance #7: the reference level must match CTB's own."""

    def test_matches_the_four_calibration_crops(self):
        expected = _read_calibration()
        self.assertEqual(sorted(expected), sorted(CALIBRATED_PIXEL_SIZES))
        for name, pixel_size in CALIBRATED_PIXEL_SIZES.items():
            with self.subTest(crop=name):
                self.assertEqual(reference_maxzoom(pixel_size), expected[name])

    def test_one_arcsec_gives_fourteen(self):
        """The GLO-30 case, stated on its own because it is the one that
        actually runs in production."""
        self.assertEqual(reference_maxzoom(1.0 / 3600.0), 14)

    def test_halving_the_pixel_size_adds_one_level(self):
        """A resampling to half the pixel size earns exactly one more level.
        This is the property that makes "maxzoom > reference" a meaningful
        warning: past the reference level the tiles get bigger, not sharper."""
        for base in (1.0 / 3600.0, 2.0 / 3600.0, 5.0 / 3600.0):
            with self.subTest(pixel=base):
                self.assertEqual(reference_maxzoom(base / 2),
                                 reference_maxzoom(base) + 1)

    def test_rejects_a_non_positive_pixel_size(self):
        with self.assertRaises(ValueError):
            reference_maxzoom(0.0)


class TestTileCounts(unittest.TestCase):
    """PRD acceptance #7: exact counts, not estimates."""

    def test_every_zoom_level_matches_the_baseline(self):
        expected, expected_total = _read_tile_counts()
        self.assertEqual(len(expected), 15, "baseline covers zoom 0..14")
        for zoom, count in expected.items():
            with self.subTest(zoom=zoom):
                self.assertEqual(count_tiles_at(CHONGQING, zoom), count)
        self.assertEqual(sum(expected.values()), expected_total)

    def test_the_total_is_223495(self):
        self.assertEqual(estimate(CHONGQING, 14).total, 223495)

    def test_deepest_level_dominates(self):
        """The size estimate uses the deepest level's average tile size on the
        grounds that it dominates the total. If that ever stops being true the
        estimate needs a different constant."""
        per_zoom = estimate(CHONGQING, 14).per_zoom
        self.assertGreater(per_zoom[14] / sum(per_zoom.values()), 0.7)

    def test_deepest_range_is_anchored_where_the_baseline_tiles_are(self):
        """A spot check that the range is anchored correctly, not merely the
        right size: 447 x 374 tiles starting at (25968, 10749)."""
        self.assertEqual(tile_range(CHONGQING, 14), (25968, 10749, 26414, 11122))

    def test_tile_index_is_tms_not_slippy(self):
        """y counts up from the south pole, so the north-west corner of the
        world is at the TOP of the y range, not at y = 0.

        The grid also is not the 1x1 square a slippy map starts from: CTB's
        geodetic profile roots at two tiles spanning the two hemispheres, so
        zoom z is 2**(z+1) tiles wide and 2**z tall. At z=2 that is 8 x 4.
        """
        self.assertEqual(tile_index(-180.0, -90.0, 2), (0, 0))
        self.assertEqual(tile_index(-180.0, 89.9, 2), (0, 3))
        self.assertEqual(tile_index(179.9, 89.9, 2), (7, 3))

        # The 2 x 1 root, stated as a count so the profile cannot be mistaken
        # for the slippy one by anyone reading this later. Note the count is
        # one wider than the grid: CTB floors both corners and loops `<=`, so a
        # window that ends exactly on the world's eastern edge also reports the
        # column beyond it, which CTB would then iterate and find empty.
        # Reproducing that is the point -- see count_tiles_at's docstring.
        whole_world = BBox(-180.0, -90.0, 180.0, 90.0)
        self.assertEqual(count_tiles_at(whole_world, 0), 3 * 2)
        self.assertEqual(count_tiles_at(whole_world, 2), 9 * 5)


class TestEstimate(unittest.TestCase):
    def test_per_zoom_and_total_agree(self):
        est = estimate(CHONGQING, 14)
        self.assertEqual(est.total, sum(est.per_zoom.values()))
        self.assertEqual(len(est.per_zoom), 15)

    def test_summary_is_human_readable_and_bounded(self):
        """The UI shows this verbatim, so it has to name the level, the count
        and an estimate in units an operator can act on."""
        text = estimate(CHONGQING, 14).summary()
        self.assertIn("14", text)
        self.assertIn("223,495", text)
        self.assertIn("GB", text)


class TestGuardrails(unittest.TestCase):
    """FR-02 requires refusing an out-of-control slice, not warning about it."""

    def test_a_normal_window_passes(self):
        self.assertEqual(
            validate_bbox(CHONGQING), [])
        self.assertEqual(
            validate_cost(estimate(CHONGQING, 14)), [])

    def test_out_of_range_windows_are_rejected(self):
        for bad in (
            BBox(-181.0, 28.1, 110.2, 32.2),
            BBox(105.3, -91.0, 110.2, 32.2),
            BBox(110.2, 28.1, 105.3, 32.2),   # west past east
            BBox(105.3, 32.2, 110.2, 28.1),   # south past north
            BBox(105.3, 28.1, 105.3, 32.2),   # zero width
        ):
            with self.subTest(bbox=bad):
                self.assertTrue(validate_bbox(bad), f"{bad} should be rejected")

    def test_a_runaway_slice_is_refused(self):
        """The whole window at z16 is about 2.7 million tiles, roughly 16 GB
        and an hour of slicing -- past the point where the operator has almost
        certainly asked for something they did not mean."""
        est = estimate(BBox(-180.0, -90.0, 180.0, 90.0), 16)
        self.assertGreater(est.total, MAX_TILES)
        self.assertTrue(validate_cost(est))

    def test_the_baseline_itself_passes_the_cap(self):
        """The guardrail must not block the job this project exists to run."""
        est = estimate(CHONGQING, 14)
        self.assertLess(est.total, MAX_TILES)
        self.assertEqual(validate_cost(est), [])


if __name__ == "__main__":
    unittest.main()
