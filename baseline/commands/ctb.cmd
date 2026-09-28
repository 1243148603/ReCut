@echo off
REM ============================================================
REM ctb.cmd -- launcher for the bundled CTB tools.
REM
REM This is the NFR-02 pattern Recut must reproduce: run the CTB tools
REM with PROJ_DATA and GDAL_DATA pointed at the bundle's own share dirs.
REM
REM Why (verified on this machine, 2026-09-24):
REM   Without these two variables every CTB invocation prints
REM     ERROR 1: PROJ: proj_create_from_database: Cannot find proj.db
REM   because proj.db lives in Library\share\proj\, which is NOT alongside
REM   the DLLs in Library\bin\. Copying DLLs alone is not enough -- this
REM   breaks even on a machine that has conda installed, so a clean-machine
REM   failure is guaranteed.
REM
REM Usage:  ctb.cmd <tool> [args...]
REM   e.g.  ctb.cmd ctb-tile -o D:\out D:\dem.tif
REM ============================================================
setlocal
set "CTB_HOME=%~dp0.."
set "PROJ_DATA=%CTB_HOME%\share\proj"
set "GDAL_DATA=%CTB_HOME%\share\gdal"

set "TOOL=%~1"
if "%TOOL%"=="" (
  echo Usage: ctb.cmd ^<tool^> [args...]
  echo Tools: ctb-tile ctb-info ctb-extents ctb-export
  exit /b 2
)

REM Re-quote each remaining argument so paths containing spaces survive.
set "ARGS="
:collect
shift
if "%~1"=="" goto :run
set ARGS=%ARGS% "%~1"
goto :collect

:run
"%CTB_HOME%\bin\%TOOL%.exe" %ARGS%
exit /b %errorlevel%
