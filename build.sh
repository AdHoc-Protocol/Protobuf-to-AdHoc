#!/usr/bin/env bash
# Compiles the converter and converts every sample into AdHoc/.
#   ./build.sh            # build + convert samples/ → AdHoc/
set -eu
cd "$(dirname "$0")"
rm -rf out
mkdir -p AdHoc
rm -f AdHoc/*.cs   # descriptions of samples that are gone must not stay; the folder itself is kept
dotnet build Protobuf2AdHoc.csproj -c Release -o out --nologo -v quiet -clp:NoSummary
dotnet out/Protobuf2AdHoc.dll samples AdHoc
