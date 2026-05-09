#!/usr/bin/env bash
# ─────────────────────────────────────────────────────────────
# refresh-expedition-nugets.sh
#
# 1) Removes CoreGame.SharedMeta.* from the local NuGet global cache
# 2) Packs SharedMeta NuGets at the requested version (or the version
#    from com.coregame.sharedmeta/package.json if no arg is given)
# 3) Verifies that examples/Unity/Expedition/ServerSolution/NuGet.Config
#    contains a local feed pointing at $SCRIPT_DIR/nupkgs
# 4) Runs `dotnet restore --force --no-cache` against the ServerSolution
#
# Usage:
#   ./refresh-expedition-nugets.sh          # version from package.json
#   ./refresh-expedition-nugets.sh 0.20.1   # override version
# ─────────────────────────────────────────────────────────────
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$SCRIPT_DIR"

PACKAGE_JSON="com.coregame.sharedmeta/package.json"
SERVER_SOLUTION_DIR="examples/Unity/Expedition/ServerSolution"
NUGET_CONFIG="$SERVER_SOLUTION_DIR/NuGet.Config"
NUPKGS_DIR="$SCRIPT_DIR/nupkgs"
PACK_SCRIPT="$SCRIPT_DIR/pack-nugets.sh"

VERSION="${1:-}"

# ── 0. Resolve version ──────────────────────────────────────
if [ -z "$VERSION" ]; then
    if [ ! -f "$PACKAGE_JSON" ]; then
        echo "✗ $PACKAGE_JSON not found and no version argument provided" >&2
        exit 1
    fi
    # Extract "version": "X.Y.Z" without depending on jq
    VERSION="$(sed -n 's/.*"version"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' "$PACKAGE_JSON" | head -n1)"
    if [ -z "$VERSION" ]; then
        echo "✗ Could not parse version from $PACKAGE_JSON" >&2
        exit 1
    fi
    VERSION_SOURCE="package.json"
else
    VERSION_SOURCE="argument"
fi

echo "═══════════════════════════════════════════"
echo "  Refresh Expedition NuGets"
echo "  Version: $VERSION ($VERSION_SOURCE)"
echo "═══════════════════════════════════════════"
echo ""

# ── 1. Purge SharedMeta from NuGet global cache ─────────────
echo "▸ [1/4] Clearing CoreGame.SharedMeta.* from NuGet global-packages cache..."

# Ask dotnet where the global cache lives. Output line looks like:
#   global-packages: C:\Users\You\.nuget\packages\
GLOBAL_PACKAGES="$(
    dotnet nuget locals global-packages --list 2>/dev/null \
        | sed -n 's/^global-packages:[[:space:]]*//p' \
        | head -n1 \
        | sed 's/[[:space:]]*$//'
)"

if [ -z "$GLOBAL_PACKAGES" ] || [ ! -d "$GLOBAL_PACKAGES" ]; then
    echo "  ! Could not resolve NuGet global-packages folder; skipping purge."
else
    echo "  Cache: $GLOBAL_PACKAGES"
    REMOVED=0
    # NuGet stores cached packages in lower-case directories named after the package id
    for dir in "$GLOBAL_PACKAGES"/coregame.sharedmeta*; do
        [ -e "$dir" ] || continue
        rm -rf "$dir"
        echo "    removed $(basename "$dir")"
        REMOVED=$((REMOVED + 1))
    done
    if [ "$REMOVED" -eq 0 ]; then
        echo "  (nothing to remove)"
    else
        echo "  ✓ Removed $REMOVED cached package directorie(s)"
    fi

    # Also wipe any http-cache entries that could serve stale metadata for these IDs
    HTTP_CACHE="$(
        dotnet nuget locals http-cache --list 2>/dev/null \
            | sed -n 's/^http-cache:[[:space:]]*//p' \
            | head -n1 \
            | sed 's/[[:space:]]*$//'
    )"
    if [ -n "$HTTP_CACHE" ] && [ -d "$HTTP_CACHE" ]; then
        # Best-effort: drop any cache files that mention coregame.sharedmeta
        find "$HTTP_CACHE" -type f -iname '*coregame.sharedmeta*' -delete 2>/dev/null || true
    fi
fi
echo ""

# ── 2. Pack NuGets ──────────────────────────────────────────
echo "▸ [2/4] Packing SharedMeta NuGets at $VERSION..."
if [ ! -x "$PACK_SCRIPT" ] && [ ! -f "$PACK_SCRIPT" ]; then
    echo "✗ $PACK_SCRIPT not found" >&2
    exit 1
fi
bash "$PACK_SCRIPT" "$VERSION"
echo ""

# ── 3. Verify local feed in NuGet.Config ────────────────────
echo "▸ [3/4] Verifying local feed in $NUGET_CONFIG..."
if [ ! -f "$NUGET_CONFIG" ]; then
    echo "✗ $NUGET_CONFIG not found" >&2
    exit 1
fi

# Normalise a path so Windows (D:\foo\bar), MSYS (/d/foo/bar) and POSIX
# (/d/foo/bar) forms compare equal: lowercase + forward slashes + drive-letter
# prefix collapsed to "x:/...".
normalize_path() {
    local p="$1"
    # backslash → forward slash, then lowercase
    p="$(printf '%s' "$p" | tr '\\' '/' | tr '[:upper:]' '[:lower:]')"
    # MSYS-style "/x/..." → "x:/..."
    case "$p" in
        /?/*) p="${p:1:1}:${p:2}" ;;
    esac
    # strip trailing slash
    p="${p%/}"
    printf '%s' "$p"
}

NORM_TARGET="$(normalize_path "$NUPKGS_DIR")"
FOUND_LOCAL_FEED=0
while IFS= read -r feed_value; do
    norm_feed="$(normalize_path "$feed_value")"
    if [ "$norm_feed" = "$NORM_TARGET" ]; then
        FOUND_LOCAL_FEED=1
        echo "  ✓ Local feed present: $feed_value"
        break
    fi
done < <(
    sed -n 's/.*<add[^>]*value="\([^"]*\)".*/\1/p' "$NUGET_CONFIG"
)

if [ "$FOUND_LOCAL_FEED" -eq 0 ]; then
    echo "  ✗ No <add ... value=\"$NUPKGS_DIR\" /> entry found in $NUGET_CONFIG" >&2
    echo "    Current <packageSources> entries:" >&2
    sed -n 's/.*<add[^>]*value="\([^"]*\)".*/      - \1/p' "$NUGET_CONFIG" >&2
    exit 1
fi
echo ""

# ── 4. Force-restore the ServerSolution ─────────────────────
echo "▸ [4/4] Running dotnet restore --force --no-cache in $SERVER_SOLUTION_DIR..."
(
    cd "$SERVER_SOLUTION_DIR"
    dotnet restore --force --no-cache
)
echo ""

echo "═══════════════════════════════════════════"
echo "  Done. Expedition ServerSolution restored against $VERSION."
echo "═══════════════════════════════════════════"
