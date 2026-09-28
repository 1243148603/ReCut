@echo off
REM ============================================================
REM 04_ctb_slice.cmd -- Slice the mosaic into Cesium terrain tiles
REM                     (tutorial section 5.2)
REM
REM IMPORTANT -- the tutorial's section 5.3 is WRONG about -m:
REM   The tutorial claims "-m <level> sets the max level". It does not.
REM   -m is --warp-memory <bytes> (verified in ctb-tile's own --help output).
REM   The zoom options are:
REM     -s/--start-zoom  the DEEP end (default = auto max zoom level)
REM     -e/--end-zoom    the SHALLOW end (default 0)
REM   So to slice to the deepest level, simply OMIT the zoom options --
REM   which is what this script does.
REM
REM NOTE: ASCII-only. cmd.exe parses .cmd in the OEM codepage, so UTF-8
REM Chinese comments corrupt parsing.
REM ============================================================
set "CTB=D:\Map\ctb\ctb.cmd"
set "OUT=D:\Map\data\outputs\cesium_terrain"
set "SRC=D:\Map\data\outputs\terrain_tif\chongqing_dem.tif"

REM ctb-tile errors out if the output directory does not exist.
if not exist "%OUT%" mkdir "%OUT%"

REM Always go through ctb.cmd (not bin\ctb-tile.exe directly) so PROJ_DATA and
REM GDAL_DATA get set -- without them every run fails on "Cannot find proj.db".
REM
REM -o names the output ROOT; tiles land at <root>\{z}\{x}\{y}.terrain.
REM No zoom options are passed on purpose (deepest level, see header).
REM No -c/--thread-count either: ctb-tile defaults to all CPUs, which is what
REM the 354s baseline timing was measured with. Pinning it changes the number.
call "%CTB%" ctb-tile -o "%OUT%" "%SRC%"
exit /b %errorlevel%
