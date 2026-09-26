#!/bin/bash
# Double-click in Finder: opens Terminal with the bmw-mac-diag menu (builds on the first run).
cd "$(dirname "$0")"
if [ ! -x bin/bmwdiag ]; then
    echo "first start: building bmw-mac-diag..."
    ./build.sh || { echo; read -r -p "build failed — press Enter to close"; exit 1; }
fi
./bmwdiag
