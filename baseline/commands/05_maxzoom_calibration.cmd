@echo off
REM Maxzoom calibration -- verifies PRD acceptance #7 formula:
REM   maxzoom = ceil(log2( (180/65) / pixel_size_deg ))
REM ctb-extents does no raster reads; its first stdout line is
REM "creating <dir>/<N>.geojson" where N is the auto maxzoom level.
REM ASCII-only: cmd.exe parses .cmd in the OEM codepage.
set "GDAL=C:\Users\hwj\miniconda3\envs\geo\Library\bin\gdal_translate.exe"
set "SRC=D:\Map\data\inputs\dem\N28_E105.tif"
set "CALD=D:\Map\baseline\maxzoom_calibration"
set "PROJ_DATA=C:\Users\hwj\miniconda3\envs\geo\Library\share\proj"
set "GDAL_DATA=C:\Users\hwj\miniconda3\envs\geo\Library\share\gdal"

REM All four crops use the SAME window so the only variable is pixel size.
REM -projwin is west north east south -- note it is NOT the same order as
REM gdalwarp's -te (west south east north). 0.05 deg square, a patch of the
REM N28_E105 tile that is well inside the data (no nodata edge).
REM
REM Pixel sizes are exact fractions of a degree (one 3600th), which is what
REM makes the expected maxzoom an integer rather than something to round:
REM   0.000277...  = 1/3600 deg = 1 arcsec   (native GLO-30)
REM   0.000555...  = 2/3600 deg = 2 arcsec
REM   0.000833...  = 3/3600 deg = 3 arcsec
REM   0.000138...  = 0.5/3600   = 0.5 arcsec
REM Writing them as decimals rather than 1/3600 etc. because cmd.exe has no
REM floating point arithmetic. Keep the long literals exact if editing.

echo [crop] res_1_3600
"%GDAL%" -q -projwin 105.40 28.60 105.45 28.55 -tr 0.0002777777777777778 0.0002777777777777778 -r bilinear "%SRC%" "%CALD%\res_1_3600.tif"
echo [crop] res_2_3600
"%GDAL%" -q -projwin 105.40 28.60 105.45 28.55 -tr 0.0005555555555555556 0.0005555555555555556 -r bilinear "%SRC%" "%CALD%\res_2_3600.tif"
echo [crop] res_3_3600
"%GDAL%" -q -projwin 105.40 28.60 105.45 28.55 -tr 0.0008333333333333333 0.0008333333333333333 -r bilinear "%SRC%" "%CALD%\res_3_3600.tif"
echo [crop] res_05_3600
"%GDAL%" -q -projwin 105.40 28.60 105.45 28.55 -tr 0.0001388888888888889 0.0001388888888888889 -r bilinear "%SRC%" "%CALD%\res_05_3600.tif"

REM Next: 06_maxzoom_probe.cmd. Expected results (all four match the formula):
REM   res_05 -> 15, res_1 -> 14, res_2 -> 13, res_3 -> 12
