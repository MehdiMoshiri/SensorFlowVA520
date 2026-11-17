#!/bin/sh
# Builds dist/VA520Tester.exe directly on the Raspberry Pi with Mono's C# compiler (mcs).
# Needed only if you do not want to copy the ready-made exe from the dist folder.
#
#   sudo apt update && sudo apt install -y mono-complete libgdiplus
#   chmod +x build_on_pi.sh dist/run.sh
#   ./build_on_pi.sh
#   ./dist/run.sh
set -e
cd "$(dirname "$0")"

if ! command -v mcs >/dev/null 2>&1; then
    echo "mcs (Mono C# compiler) not found."
    echo "Install Mono first:  sudo apt update && sudo apt install -y mono-complete libgdiplus"
    exit 1
fi

mkdir -p dist
mcs -target:winexe -optimize+ -out:dist/VA520Tester.exe \
    -r:System.dll -r:System.Core.dll -r:System.Windows.Forms.dll -r:System.Drawing.dll \
    src/*.cs
cp app.config dist/VA520Tester.exe.config
chmod +x dist/run.sh 2>/dev/null || true
echo "Built dist/VA520Tester.exe  ->  start it with ./dist/run.sh"
