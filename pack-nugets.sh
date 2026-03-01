#!/usr/bin/env bash
# ─────────────────────────────────────────────────────────────
# pack-nugets.sh — Build and pack all SharedMeta NuGet packages
#
# Usage:
#   ./pack-nugets.sh          # version from Directory.Build.props (0.1.0)
#   ./pack-nugets.sh 1.2.3    # override version
# ─────────────────────────────────────────────────────────────
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$SCRIPT_DIR"

VERSION="${1:-}"
OUTPUT_DIR="nupkgs"
CONFIG="Release"

# Clean output
rm -rf "$OUTPUT_DIR"
mkdir -p "$OUTPUT_DIR"

echo "═══════════════════════════════════════════"
echo "  SharedMeta NuGet Pack"
if [ -n "$VERSION" ]; then
    echo "  Version: $VERSION (override)"
    VERSION_ARG="/p:Version=$VERSION"
else
    echo "  Version: from Directory.Build.props"
    VERSION_ARG=""
fi
echo "  Config:  $CONFIG"
echo "  Output:  $OUTPUT_DIR/"
echo "═══════════════════════════════════════════"
echo ""

# Build the full solution first (ensures generator DLL is up to date)
echo "▸ Building solution..."
dotnet build SharedMeta.slnx -c "$CONFIG" $VERSION_ARG --nologo -v q
echo "  ✓ Build succeeded"
echo ""

# Pack all IsPackable=true projects
echo "▸ Packing NuGet packages..."
dotnet pack SharedMeta.slnx -c "$CONFIG" $VERSION_ARG --no-build --nologo -v q -o "$OUTPUT_DIR"
echo ""

# Copy updated generator DLL to UPM package
GENERATOR_DLL="src/SharedMeta.Generator/bin/$CONFIG/netstandard2.0/SharedMeta.Generator.dll"
UPM_ANALYZER="com.coregame.sharedmeta/Runtime/Analyzers/SharedMeta.Generator.dll"
if [ -f "$GENERATOR_DLL" ]; then
    cp "$GENERATOR_DLL" "$UPM_ANALYZER"
    echo "  ✓ Updated UPM analyzer: $UPM_ANALYZER"
fi

# Summary
echo ""
echo "═══════════════════════════════════════════"
echo "  Packages:"
for pkg in "$OUTPUT_DIR"/*.nupkg; do
    echo "    $(basename "$pkg")"
done
echo "═══════════════════════════════════════════"
echo "  Done!"
