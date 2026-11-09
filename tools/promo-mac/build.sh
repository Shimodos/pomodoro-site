#!/bin/sh
# build.sh — compiles the two renderers into ./bin (stills, clip). Needs the Command Line Tools.
set -eu
cd "$(dirname "$0")"
mkdir -p bin .tmp
for tool in stills clip; do
  mkdir -p ".tmp/$tool"
  cp "$tool.swift" ".tmp/$tool/main.swift"
  swiftc -O Common.swift ".tmp/$tool/main.swift" -o "bin/$tool"
done
rm -rf .tmp
echo "built bin/stills bin/clip"
