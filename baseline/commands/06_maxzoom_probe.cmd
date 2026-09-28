@echo off
REM Read auto maxzoom from ctb-extents first stdout line.
set "CTB=D:\Map\ctb"
REM Uses the shipped bundle (not the conda env) so the measurement reflects the
REM same PROJ/GDAL data the real pipeline uses. See ctb.cmd header for why both
REM variables are mandatory.
set "PROJ_DATA=%CTB%\share\proj"
set "GDAL_DATA=%CTB%\share\gdal"
set "CALD=D:\Map\baseline\maxzoom_calibration"
set "OUT=%CALD%\maxzoom_out"
if not exist "%OUT%" mkdir "%OUT%"

REM How to read the output below: ctb-extents does NOT read any raster data --
REM it only inspects the geotransform. Its first stdout line is
REM   "creating <dir>\<N>.geojson"
REM and that N is the auto-computed max zoom level. The .geojson files it
REM writes into maxzoom_out\ (named 0..15) are a side effect, not the result.
REM 2>&1 is here to catch that first line on either stream.
REM
REM Expected: 15 / 14 / 13 / 12 -- see 05_maxzoom_calibration.cmd for the
REM pixel sizes each corresponds to, and calibration_results.txt for the log.

echo --- res_05_3600 ---
"%CTB%\bin\ctb-extents.exe" -o "%OUT%" "%CALD%\res_05_3600.tif" 2>&1
echo --- res_1_3600 ---
"%CTB%\bin\ctb-extents.exe" -o "%OUT%" "%CALD%\res_1_3600.tif" 2>&1
echo --- res_2_3600 ---
"%CTB%\bin\ctb-extents.exe" -o "%OUT%" "%CALD%\res_2_3600.tif" 2>&1
echo --- res_3_3600 ---
"%CTB%\bin\ctb-extents.exe" -o "%OUT%" "%CALD%\res_3_3600.tif" 2>&1
