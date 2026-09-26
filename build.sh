#!/bin/bash
# Build bmwdiag. Downloads the official EdiabasLib release (GPL-3.0, github.com/uholeschak/ediabaslib)
# once, extracts only the files needed on macOS into lib/, then builds into bin/.
#
#   ./build.sh                          normal build
#   EDIABASLIB_ZIP=/path/Binaries-20260607.zip ./build.sh    use an already downloaded release zip
set -euo pipefail
cd "$(dirname "$0")"

TAG="binaries_20260607"
ZIPNAME="Binaries-20260607.zip"
URL="https://github.com/uholeschak/ediabaslib/releases/download/$TAG/$ZIPNAME"
SHA256="79800aa79c172933f74eda7b618d6e63fb6768238ab6e31437aab372aba278f4"
SRC="S29CertGenerator"   # the net10.0 build of EdiabasLib inside the release zip

# --- .NET SDK ---
if [ -x "$HOME/.dotnet/dotnet" ]; then export DOTNET_ROOT="$HOME/.dotnet"; export PATH="$DOTNET_ROOT:$PATH"; fi
if ! command -v dotnet >/dev/null || ! dotnet --list-sdks | grep -q '^10\.'; then
    echo "error: .NET 10 SDK not found. Install it (see README):"
    echo "  brew install --cask dotnet-sdk"
    echo "  or without admin rights: curl -sSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 10.0"
    exit 1
fi

case "$(uname -m)" in
    arm64)  RID="osx-arm64" ;;
    x86_64) RID="osx-x64" ;;
    *) echo "error: unsupported CPU $(uname -m)"; exit 1 ;;
esac

# --- EdiabasLib ---
if [ ! -f lib/EdiabasLib.dll ] || [ ! -f lib/libSystem.IO.Ports.Native.dylib ]; then
    mkdir -p .cache lib
    ZIP="${EDIABASLIB_ZIP:-.cache/$ZIPNAME}"
    if [ ! -f "$ZIP" ]; then
        echo "downloading EdiabasLib release $TAG (~90 MB, once)..."
        curl -fL --retry 5 --progress-bar -C - -o "$ZIP.part" "$URL"
        mv "$ZIP.part" "$ZIP"
    fi
    echo "$SHA256  $ZIP" | shasum -a 256 -c - >/dev/null || { echo "error: checksum mismatch for $ZIP"; exit 1; }
    TMP="$(mktemp -d)"
    unzip -q -o "$ZIP" \
        "$SRC/EdiabasLib.dll" "$SRC/BouncyCastle.Cryptography.dll" "$SRC/Newtonsoft.Json.dll" \
        "$SRC/InTheHand.BluetoothLE.dll" "$SRC/InTheHand.Net.Bluetooth.dll" \
        "$SRC/runtimes/unix/lib/net10.0/System.IO.Ports.dll" \
        "$SRC/runtimes/$RID/native/libSystem.IO.Ports.Native.dylib" -d "$TMP"
    cp "$TMP/$SRC"/*.dll lib/
    # the System.IO.Ports.dll in the root of the folder is a Windows stub — use the unix one
    cp "$TMP/$SRC/runtimes/unix/lib/net10.0/System.IO.Ports.dll" lib/
    cp "$TMP/$SRC/runtimes/$RID/native/libSystem.IO.Ports.Native.dylib" lib/
    rm -rf "$TMP"
    echo "EdiabasLib ($TAG, $RID) -> lib/"
fi

# --- build ---
dotnet build src/BmwDiag/BmwDiag.csproj -c Release -o bin --nologo -v quiet
echo
echo "done. try:  ./bmwdiag ports"
