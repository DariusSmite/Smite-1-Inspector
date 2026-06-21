@echo off
setlocal
cd /d "%~dp0"
echo ============================================================
echo  Building Smite 1 Inspector  (portable single-file exe)
echo ============================================================
echo.
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
echo.
if %errorlevel%==0 (
  echo DONE. Your portable exe is here:
  echo   bin\Release\net8.0-windows\win-x64\publish\SmiteInspector.exe
) else (
  echo Build failed. If you do not have the .NET 8 SDK, get it from:
  echo   https://dotnet.microsoft.com/download/dotnet/8.0
)
echo.
pause
