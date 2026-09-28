@echo off
REM PRD section 5 acceptance #1 evidence: prove that the VRT-based path
REM (what Recut uses via Python gdal.Warp) produces a byte-identical mosaic
REM to the direct 30-input gdalwarp path.
set "B=C:\Users\hwj\miniconda3\envs\geo\Library\bin"
set "PROJ_DATA=C:\Users\hwj\miniconda3\envs\geo\Library\share\proj"
set "GDAL_DATA=C:\Users\hwj\miniconda3\envs\geo\Library\share\gdal"
set "DEM=D:\Map\data\inputs\dem"
set "OUT=D:\Map\data\outputs\terrain_tif"

REM Step 1: build the virtual mosaic. A VRT stores only an index -- no pixel
REM copies -- so this is instant and writes a ~13KB file. Note gdalbuildvrt has
REM its own -r/--resampling option and this command does NOT pass one, so it
REM falls back to its default (nearest). That default is the suspected cause of
REM the mismatch documented below.

"%B%\gdalbuildvrt.exe" -q "%OUT%\mosaic.vrt" "%DEM%\N28_E105.tif" "%DEM%\N28_E106.tif" "%DEM%\N28_E107.tif" "%DEM%\N28_E108.tif" "%DEM%\N28_E109.tif" "%DEM%\N28_E110.tif" "%DEM%\N29_E105.tif" "%DEM%\N29_E106.tif" "%DEM%\N29_E107.tif" "%DEM%\N29_E108.tif" "%DEM%\N29_E109.tif" "%DEM%\N29_E110.tif" "%DEM%\N30_E105.tif" "%DEM%\N30_E106.tif" "%DEM%\N30_E107.tif" "%DEM%\N30_E108.tif" "%DEM%\N30_E109.tif" "%DEM%\N30_E110.tif" "%DEM%\N31_E105.tif" "%DEM%\N31_E106.tif" "%DEM%\N31_E107.tif" "%DEM%\N31_E108.tif" "%DEM%\N31_E109.tif" "%DEM%\N31_E110.tif" "%DEM%\N32_E105.tif" "%DEM%\N32_E106.tif" "%DEM%\N32_E107.tif" "%DEM%\N32_E108.tif" "%DEM%\N32_E109.tif" "%DEM%\N32_E110.tif"

REM Step 2: warp the VRT with the SAME options as 03_gdalwarp_mosaic.cmd, so
REM any difference in the output is attributable to the VRT step alone.
"%B%\gdalwarp.exe" -t_srs EPSG:4326 -te 105.3 28.1 110.2 32.2 -te_srs EPSG:4326 -r bilinear -co TILED=YES -co COMPRESS=LZW -overwrite --config GDAL_CACHEMAX 2048 "%OUT%\mosaic.vrt" "%OUT%\chongqing_dem_vrt.tif"

REM RESULT: NOT equivalent. Do not treat as passed.
REM   direct 30-input path : 1,090,884,393 bytes, gdalinfo Checksum 58775
REM   VRT path (this one)  : 1,091,184,483 bytes, gdalinfo Checksum 31298
REM The Checksum band covers the whole raster, so different values mean the
REM pixel data itself differs, not just container overhead. Likely cause is the
REM nearest-resampled VRT being bilinear-resampled again in step 2, versus the
REM direct path doing a single bilinear pass over all 30 inputs.
REM Recorded in golden\mosaic_checksums.txt and logs\vrt_equiv.log.
exit /b %errorlevel%
