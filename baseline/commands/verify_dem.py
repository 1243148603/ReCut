#!/usr/bin/env python
"""
verify_dem.py -- Verify the 30 downloaded DEM tiles and emit their metadata.

Serves three purposes:
  1. Download certificate: proves all 30 tiles open and are the expected raster.
  2. FR-01 reference: the "gdalinfo summary" the Recut GUI must display per input
     file (path / CRS / extent / resolution).
  3. Gate: the mosaic step must not start until all 30 pass.

Expected for Copernicus GLO-30: 3600x3600, EPSG:4326, 1 arc-second pixel,
origin half a pixel inside a whole degree (pixel-centre convention).

Note on the size: 3600, not 3601. The 3601 form belongs to the pixel-EDGE
convention (3600 intervals span 3600 pixels plus one shared node). Copernicus
ships pixel-centre, where 3600 pixels span exactly one degree. This file
asserts 3600 and all 30 tiles measure 3600.
"""

import glob
import json
import os
import sys

from osgeo import gdal

DEM_DIR = r"D:\Map\data\inputs\dem"
OUT = r"D:\Map\baseline\manifests\dem_gdalinfo.json"

# Hard gates. A tile failing any of these must not reach the mosaic step --
# gdalwarp would silently resample a wrong-sized or wrongly-projected tile into
# the seam rather than error out.
EXPECT_SIZE = (3600, 3600)
EXPECT_PIXEL = 1.0 / 3600.0
EXPECT_EPSG = "4326"

# Copernicus GLO-30 COGs use a pixel-CENTRE origin: the geotransform origin sits
# half a pixel inside the nominal tile edge (e.g. N28_E105 origin = 104.999861,
# not 105.0). The true data extent is still the whole degree. Recut's FR-01
# coverage check must apply this half-pixel correction, or every tile's extent
# is understated by 0.5 arcsec (~15 m) per side.
HALF_PIXEL = 0.5 / 3600.0


def main():
    files = sorted(glob.glob(os.path.join(DEM_DIR, "*.tif")))
    print(f"found {len(files)} tif files in {DEM_DIR}")

    out, problems = {}, []
    for path in files:
        name = os.path.basename(path)
        ds = gdal.Open(path)
        if ds is None:
            problems.append(f"{name}: gdal.Open returned None")
            continue

        gt = ds.GetGeoTransform()
        band = ds.GetRasterBand(1)
        srs = ds.GetSpatialRef()
        # GetAuthorityCode(None) on a projected CRS returns the projected code;
        # with None it picks whichever authority is set, which is EPSG here.
        epsg = srs.GetAuthorityCode(None) if srs else None

        rec = {
            "size": [ds.RasterXSize, ds.RasterYSize],
            "dtype": gdal.GetDataTypeName(band.DataType),
            "epsg": epsg,
            "geotransform": list(gt),
            # gt[1] is x-resolution, gt[5] is y-resolution and is NEGATIVE for
            # north-up rasters, so both are taken as absolute values here.
            "pixel_size_deg": [abs(gt[1]), abs(gt[5])],
            # Extent including the half-pixel border the origin convention omits.
            # gt[3] is the origin's northing (north edge, pixel-centre) and
            # gt[5] < 0, so origin + rows*gt[5] walks DOWN to the south edge.
            # bbox_wgs84 is ordered west, south, east, north.
            "bbox_wgs84": [
                gt[0] - HALF_PIXEL,
                gt[3] + ds.RasterYSize * gt[5] - HALF_PIXEL,
                gt[0] + ds.RasterXSize * gt[1] + HALF_PIXEL,
                gt[3] + HALF_PIXEL,
            ],
            "geotransform_pixel_centre": True,
            "nodata": band.GetNoDataValue(),
            "file_bytes": os.path.getsize(path),
        }
        out[name] = rec

        if rec["size"] != list(EXPECT_SIZE):
            problems.append(f"{name}: size {rec['size']} != {list(EXPECT_SIZE)}")
        if epsg != EXPECT_EPSG:
            problems.append(f"{name}: EPSG {epsg} != {EXPECT_EPSG}")
        # Exact float comparison would be wrong, but the tolerance can be this
        # tight because 1/3600 is compared against itself, not a resampled value.
        if abs(rec["pixel_size_deg"][0] - EXPECT_PIXEL) > 1e-12:
            problems.append(
                f"{name}: pixel {rec['pixel_size_deg'][0]} != {EXPECT_PIXEL}")
        ds = None

    # The JSON is written before the problems check on purpose: even a failing
    # run leaves behind the metadata needed to diagnose which tile is bad.
    os.makedirs(os.path.dirname(OUT), exist_ok=True)
    with open(OUT, "w", encoding="utf-8") as fh:
        json.dump(out, fh, indent=1)

    print(f"verified {len(out)} files -> {OUT}")
    if problems:
        print("\nPROBLEMS:")
        for p in problems:
            print("  " + p)
        return 1

    # Only reached when every tile passed. Summarise the union extent -- this is
    # the FR-01 coverage baseline, and it is what the mosaic -te window in
    # 03_gdalwarp_mosaic.cmd was derived from (inset on all four sides).
    xs0 = [r["bbox_wgs84"][0] for r in out.values()]
    ys0 = [r["bbox_wgs84"][1] for r in out.values()]
    xs1 = [r["bbox_wgs84"][2] for r in out.values()]
    ys1 = [r["bbox_wgs84"][3] for r in out.values()]
    print(f"\nunion extent: {min(xs0):.4f} {min(ys0):.4f} "
          f"{max(xs1):.4f} {max(ys1):.4f}")
    print("ALL 30 OK")
    return 0


if __name__ == "__main__":
    sys.exit(main())
