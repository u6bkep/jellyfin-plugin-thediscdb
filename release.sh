#!/usr/bin/env bash
set -euo pipefail

REPO="u6bkep/jellyfin-plugin-thediscdb"
PLUGIN_NAME="jellyfin-plugin-thediscdb"
DLL_NAME="Jellyfin.Plugin.TheDiscDb.dll"
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"

# Determine version from argument or prompt
VERSION="${1:-}"
if [[ -z "$VERSION" ]]; then
    read -rp "Version (e.g. 0.1.0): " VERSION
fi

TAG="v${VERSION}"
FOUR_PART="${VERSION}.0"
ZIP_NAME="${PLUGIN_NAME}-${TAG}.zip"

# Optional changelog
CHANGELOG="${2:-}"
if [[ -z "$CHANGELOG" ]]; then
    read -rp "Changelog (one line, or empty to skip): " CHANGELOG
fi
CHANGELOG="${CHANGELOG:-Release ${TAG}}"

echo "==> Building release..."
dotnet build -c Release "$SCRIPT_DIR"

echo "==> Creating zip..."
BUILD_DIR="$SCRIPT_DIR/bin/Release/net9.0"
ZIP_PATH="$SCRIPT_DIR/$ZIP_NAME"
(cd "$BUILD_DIR" && zip -j "$ZIP_PATH" "$DLL_NAME")

echo "==> Computing checksum..."
CHECKSUM="$(md5sum "$ZIP_PATH" | awk '{print $1}')"
echo "    $CHECKSUM"

echo "==> Updating manifest.json..."
TIMESTAMP="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
SOURCE_URL="https://github.com/${REPO}/releases/download/${TAG}/${ZIP_NAME}"

# Build the new version entry and prepend it to the versions array
python3 -c "
import json, sys

version_entry = {
    'version': '${FOUR_PART}',
    'changelog': $(python3 -c "import json; print(json.dumps('${CHANGELOG}'))"),
    'targetAbi': '10.11.6.0',
    'sourceUrl': '${SOURCE_URL}',
    'checksum': '${CHECKSUM}',
    'timestamp': '${TIMESTAMP}'
}

with open('$SCRIPT_DIR/manifest.json', 'r') as f:
    manifest = json.load(f)

# Remove any existing entry with this version, then prepend
manifest[0]['versions'] = [
    v for v in manifest[0]['versions']
    if v['version'] != '${FOUR_PART}'
]
manifest[0]['versions'].insert(0, version_entry)

with open('$SCRIPT_DIR/manifest.json', 'w') as f:
    json.dump(manifest, f, indent=2)
    f.write('\n')
"

echo "==> Creating GitHub release and uploading..."
cd "$SCRIPT_DIR"
gh release create "$TAG" "$ZIP_PATH" \
    --repo "$REPO" \
    --title "$TAG" \
    --notes "$CHANGELOG"

echo ""
echo "Done! Release ${TAG} published."
echo "  Zip:      $ZIP_PATH"
echo "  Checksum: $CHECKSUM"
echo "  URL:      $SOURCE_URL"
echo ""
echo "Don't forget to commit and push the updated manifest.json."
