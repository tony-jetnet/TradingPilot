@echo off
REM Build hook_native.dll using CMake + MSVC
REM Requires: Visual Studio 2022 with C++ workload, CMake

setlocal

set SCRIPT_DIR=%~dp0
cd /d "%SCRIPT_DIR%"

REM Find cmake - try VS2022 bundled cmake first, then PATH
set CMAKE=cmake
set VS_CMAKE="%ProgramFiles%\Microsoft Visual Studio\2022\Community\Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe"
if exist %VS_CMAKE% set CMAKE=%VS_CMAKE%

REM Clean and create build directory
if exist build rmdir /s /q build
mkdir build
cd build

REM Configure for x64 Release with static CRT
%CMAKE% .. -G "Visual Studio 17 2022" -A x64 -DCMAKE_BUILD_TYPE=Release
if errorlevel 1 (
    echo CMake configure failed!
    exit /b 1
)

REM Build
%CMAKE% --build . --config Release
if errorlevel 1 (
    echo Build failed!
    exit /b 1
)

REM Copy output to a known location
copy /y Release\hook_native.dll "%SCRIPT_DIR%hook_native.dll" >nul 2>&1
if exist Release\hook_native.dll (
    echo.
    echo Build successful: hook_native.dll
    echo Output: %SCRIPT_DIR%hook_native.dll
) else (
    echo Build output not found!
    exit /b 1
)

endlocal
