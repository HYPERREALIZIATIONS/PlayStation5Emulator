@echo off
REM Build SharpEmu (research PS5 emulator) on Windows.
setlocal
where dotnet >nul 2>nul || (echo "dotnet SDK not found - install from https://dotnet.microsoft.com/download" & exit /b 1)
dotnet build src\SharpEmu\SharpEmu.csproj -c Release
if %ERRORLEVEL%==0 (
  echo.
  echo Build succeeded. Run with:
  echo   dotnet src\SharpEmu\bin\Release\net8.0\SharpEmu.dll --selftest
  echo   dotnet src\SharpEmu\bin\Release\net8.0\SharpEmu.dll "path\to\eboot.bin"
)
endlocal
