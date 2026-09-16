@echo off
setlocal enabledelayedexpansion
cd /d "%~dp0"

set "OUT=dist\app"
set "ISS=installer\ClassNote.iss"

rem --- locate Inno Setup compiler (goto-form avoids the "if ( ... )" parse bug with "(x86)" in path) ---
set "ISCC=%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe"
if exist "%ISCC%" goto :iscc_found
set "ISCC=%ProgramFiles%\Inno Setup 6\ISCC.exe"
if exist "%ISCC%" goto :iscc_found
echo [ERROR] Inno Setup 6 not found. Install it from https://jrsoftware.org/isinfo.php
exit /b 1
:iscc_found

echo ============================================
echo   ClassNote one-click installer build
echo ============================================
echo.

rem --- prerequisites ---
where dotnet >nul 2>&1
if errorlevel 1 (
  echo [ERROR] dotnet SDK not found. Install .NET SDK 8.0 first.
  exit /b 1
)

rem --- 1. publish self-contained ---
echo [1/3] Cleaning old publish output...
if exist "%OUT%" rmdir /s /q "%OUT%"

echo [2/3] Publishing self-contained win-x64 ...
dotnet publish ClassNote\ClassNote.csproj -c Release -r win-x64 --self-contained true -o "%OUT%"
if errorlevel 1 goto :fail

rem --- 2. verify key payload files ---
echo [3/3] Verifying key payload files...
set "MISSING="
for %%F in (
  "models\sensevoice\model_quant.onnx"
  "models\sensevoice\tokens.json"
  "models\sensevoice\am.mvn"
  "onnxruntime.dll"
  "SQLite.Interop.dll"
  "runtimes\win-x64\native\WebView2Loader.dll"
  "assets\mathjax-tex-svg.js"
) do (
  if not exist "%OUT%\%%~F" set "MISSING=!MISSING! %%~F"
)
if defined MISSING (
  echo [ERROR] Missing files after publish:%MISSING%
  goto :fail
)

rem --- 3. compile installer ---
echo [4/4] Compiling installer with Inno Setup...
"%ISCC%" "%ISS%"
if errorlevel 1 goto :fail

echo.
echo ============================================
echo   SUCCESS. Installer written to:
echo   %CD%\dist\ClassNote-1.0.2-setup-x64.exe
echo ============================================
exit /b 0

:fail
echo.
echo [FAILED] Build aborted. See messages above.
exit /b 1
