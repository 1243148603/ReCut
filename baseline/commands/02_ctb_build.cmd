@echo off
REM ============================================================
REM 02_ctb_build.cmd -- Build CTB (tutorial section 2.3), paths adapted.
REM
REM NOTE: This file is deliberately ASCII-only. cmd.exe parses .cmd files
REM using the OEM codepage (GBK/CP936 here), so UTF-8 Chinese comments
REM corrupt the parsing and get executed as commands.
REM
REM Differences from the tutorial command:
REM   1. conda_geo  = C:\Users\hwj\miniconda3\envs\geo
REM   2. VS_CMAKE   = D:\VStudio\...\CMake\bin\cmake.exe
REM   3. Added -DGDAL_LIBRARY_DIR. src/CMakeLists.txt:8 calls
REM      check_library_exists(gdal GDALOpenEx "${GDAL_LIBRARY_DIR}" ...)
REM      which stock FindGDAL never sets -- without it the check fails silently.
REM ============================================================
set "CMAKE=D:\VStudio\Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe"
set "GEO=C:\Users\hwj\miniconda3\envs\geo"
set "SRC=C:\code\Map\cesium-terrain-builder-master"

echo [1/3] Configure...
cd /d "%SRC%\build" || exit /b 1

REM GDAL from conda is not discoverable by stock FindGDAL, so every path is
REM passed explicitly: headers, the import lib, and the lib DIRECTORY.
REM The directory one is not redundant -- see the note at the top of this file.
REM ZLIB_LIBRARY must be z.lib (conda naming), NOT zlib.lib or zlib1.lib.
"%CMAKE%" .. -G "Visual Studio 17 2022" -A x64 ^
  -DGDAL_INCLUDE_DIR="%GEO%\Library\include" ^
  -DGDAL_LIBRARY="%GEO%\Library\lib\gdal.lib" ^
  -DGDAL_LIBRARY_DIR="%GEO%\Library\lib" ^
  -DZLIB_INCLUDE_DIR="%GEO%\Library\include" ^
  -DZLIB_LIBRARY="%GEO%\Library\lib\z.lib"
if errorlevel 1 (
  echo [FAIL] cmake configure failed
  exit /b 1
)

echo.
echo [2/3] Build Release...
REM --parallel 20 matches this machine's 20 logical CPUs.
"%CMAKE%" --build . --config Release --parallel 20
if errorlevel 1 (
  echo [FAIL] build failed
  exit /b 1
)

echo.
echo [3/3] Copy runtime DLLs...
REM The tutorial's blanket `copy Library\bin\*.dll` pulls ~450MB of Intel MKL
REM and ~32MB of ICU into the bundle (~687MB total). For a shippable bundle
REM that is untenable -- use resolve_ctb_deps.py to get the real 49-DLL set
REM instead. This command is kept only to make a runnable-in-place tree.
REM ctb-tile.exe imports ctb.dll, so it must be copied out of src\Release\.
copy /Y "%GEO%\Library\bin\*.dll" "%SRC%\build\tools\Release\" >nul
copy /Y "%SRC%\build\src\Release\ctb.dll" "%SRC%\build\tools\Release\" >nul

echo.
echo ============================================
echo Build complete. Tools:
dir /b "%SRC%\build\tools\Release\*.exe"
echo ============================================
REM NOTE: DLLs beside the exes are NOT sufficient. The tools still need
REM PROJ_DATA / GDAL_DATA pointing at share\proj and share\gdal, or every
REM run fails with "Cannot find proj.db". See D:\Map\ctb\ctb.cmd.
exit /b 0
