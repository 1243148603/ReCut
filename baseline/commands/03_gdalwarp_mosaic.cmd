@echo off
REM ============================================================
REM 03_gdalwarp_mosaic.cmd -- Mosaic 30 DEM tiles + clip (tutorial section 4.2)
REM All 30 input paths listed explicitly (no wildcard) so this is a
REM byte-reproducible record for FR-10 log export.
REM
REM NOTE: ASCII-only. cmd.exe parses .cmd in the OEM codepage, so UTF-8
REM Chinese comments corrupt parsing.
REM ============================================================
set "GEO=C:\Users\hwj\miniconda3\envs\geo"
REM gdalwarp itself tolerates missing PROJ_DATA (conda's GDAL finds its own
REM share dir), but setting them keeps this run identical to the CTB runs.
set "PROJ_DATA=%GEO%\Library\share\proj"
set "GDAL_DATA=%GEO%\Library\share\gdal"

REM -t_srs EPSG:4326      output stays geographic (inputs already are; explicit
REM                       so the result does not depend on GDAL defaults)
REM -te ... -te_srs ...   clip window in lon/lat degrees, west south east north.
REM                       -te_srs is required to disambiguate the axis order:
REM                       without it the window is read as lat/lon.
REM                       Data covers 105-111E / 28-33N; the window is inset on
REM                       all four sides to avoid GLO-30's lower-quality margins.
REM                       Output size = (110.2-105.3)*3600 x (32.2-28.1)*3600
REM                                   = 17640 x 14760 px
REM -r bilinear           resampling kernel. Required, not optional: the clip
REM                       window lands between pixel centres, so every output
REM                       pixel is interpolated. This is also where this path
REM                       diverges from the VRT path -- see 07_vrt_equivalence.
REM -co TILED=YES         store as 256x256 blocks; CTB reads sequentially and
REM                       benefits from tiling plus the 256 block size
REM -co COMPRESS=LZW      lossless; ~1.09GB instead of ~2GB+ uncompressed
REM -overwrite            make reruns idempotent
REM GDAL_CACHEMAX 2048    2GB cache. With 30 inputs processed one at a time this
REM                       avoids thrashing when stitching seams between tiles.
"%GEO%\Library\bin\gdalwarp.exe" -t_srs EPSG:4326 -te 105.3 28.1 110.2 32.2 -te_srs EPSG:4326 -r bilinear ^
  -co TILED=YES -co COMPRESS=LZW -overwrite ^
  --config GDAL_CACHEMAX 2048 ^
  D:\Map\data\inputs\dem\N28_E105.tif ^
  D:\Map\data\inputs\dem\N28_E106.tif ^
  D:\Map\data\inputs\dem\N28_E107.tif ^
  D:\Map\data\inputs\dem\N28_E108.tif ^
  D:\Map\data\inputs\dem\N28_E109.tif ^
  D:\Map\data\inputs\dem\N28_E110.tif ^
  D:\Map\data\inputs\dem\N29_E105.tif ^
  D:\Map\data\inputs\dem\N29_E106.tif ^
  D:\Map\data\inputs\dem\N29_E107.tif ^
  D:\Map\data\inputs\dem\N29_E108.tif ^
  D:\Map\data\inputs\dem\N29_E109.tif ^
  D:\Map\data\inputs\dem\N29_E110.tif ^
  D:\Map\data\inputs\dem\N30_E105.tif ^
  D:\Map\data\inputs\dem\N30_E106.tif ^
  D:\Map\data\inputs\dem\N30_E107.tif ^
  D:\Map\data\inputs\dem\N30_E108.tif ^
  D:\Map\data\inputs\dem\N30_E109.tif ^
  D:\Map\data\inputs\dem\N30_E110.tif ^
  D:\Map\data\inputs\dem\N31_E105.tif ^
  D:\Map\data\inputs\dem\N31_E106.tif ^
  D:\Map\data\inputs\dem\N31_E107.tif ^
  D:\Map\data\inputs\dem\N31_E108.tif ^
  D:\Map\data\inputs\dem\N31_E109.tif ^
  D:\Map\data\inputs\dem\N31_E110.tif ^
  D:\Map\data\inputs\dem\N32_E105.tif ^
  D:\Map\data\inputs\dem\N32_E106.tif ^
  D:\Map\data\inputs\dem\N32_E107.tif ^
  D:\Map\data\inputs\dem\N32_E108.tif ^
  D:\Map\data\inputs\dem\N32_E109.tif ^
  D:\Map\data\inputs\dem\N32_E110.tif ^
  D:\Map\data\outputs\terrain_tif\chongqing_dem.tif
