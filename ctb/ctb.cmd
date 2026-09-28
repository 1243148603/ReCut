@echo off
REM ============================================================
REM ctb.cmd -- launcher for the bundled CTB tools.
REM Lives at the BUNDLE ROOT; CTB_HOME is derived from %~dp0.
REM
REM This is the NFR-02 pattern Recut must reproduce: run the CTB tools
REM with PROJ_DATA and GDAL_DATA pointed at the bundle's own share dirs.
REM
REM Why (verified on this machine, 2026-09-24):
REM   Without these two variables every CTB invocation prints
REM     ERROR 1: PROJ: proj_create_from_database: Cannot find proj.db
REM   because proj.db lives in share\proj\, which is NOT alongside the DLLs
REM   in bin\. Copying DLLs alone is not enough -- and this breaks even on a
REM   machine that HAS conda installed, so a clean-machine failure is certain.
REM
REM Usage:  ctb.cmd <tool> [args...]
REM   e.g.  ctb.cmd ctb-tile -o D:\out D:\dem.tif
REM ============================================================
setlocal
set "CTB_HOME=%~dp0"
set "PROJ_DATA=%CTB_HOME%share\proj"
set "GDAL_DATA=%CTB_HOME%share\gdal"

set "TOOL=%~1"
if "%TOOL%"=="" (
  echo Usage: ctb.cmd ^<tool^> [args...]
  echo Tools: ctb-tile ctb-info ctb-extents ctb-export
  exit /b 2
)

REM Re-quote each remaining argument so paths containing spaces survive.
REM %~1 strips the surrounding quotes, then they are re-added, so a caller may
REM pass either "D:\my dir\x.tif" or D:\my dir\x.tif and both arrive intact.
REM Delayed expansion is deliberately NOT enabled; that is why ARGS is built
REM one token at a time here rather than with a single %* .
set "ARGS="
:collect
shift
if "%~1"=="" goto :run
set ARGS=%ARGS% "%~1"
goto :collect

:run
REM CTB_HOME is %~dp0, which already ends in a backslash -- do not insert one.
REM exit /b (not exit) so a caller invoking this from another .cmd keeps its own
REM console; the tool's status code is passed through unchanged.
"%CTB_HOME%bin\%TOOL%.exe" %ARGS%
exit /b %errorlevel%
