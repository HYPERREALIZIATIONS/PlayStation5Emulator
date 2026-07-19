#!/usr/bin/env bash
# Build SharpEmu (research PS5 emulator) on Linux / macOS.
set -e
command -v dotnet >/dev/null 2>&1 || { echo "dotnet SDK not found - install from https://dotnet.microsoft.com/download"; exit 1; }
dotnet build src/SharpEmu/SharpEmu.csproj -c Release
echo
echo "Build succeeded. Run with:"
echo "  dotnet src/SharpEmu/bin/Release/net8.0/SharpEmu.dll --selftest"
echo "  dotnet src/SharpEmu/bin/Release/net8.0/SharpEmu.dll \"path/to/eboot.bin\""
