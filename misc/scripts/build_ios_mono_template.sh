#!/usr/bin/env bash
#
# LOCAL DEVELOPMENT ONLY. The shippable `-dotnet` template payloads are built by
# the XogotExportTemplates repo (scripts/assemble-templates.sh + export-apps.sh),
# which is also what uploads them to R2 and writes the manifest slots. This
# script exists so a single developer can produce a payload for `make
# dev-template` without a full template-release run; if the two ever disagree
# about the payload shape, XogotExportTemplates is right.

set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
OUT_DIR="$ROOT_DIR/DEVEL/ios-debug-device"
BUILD_DIR=""
APP_NAME="game"
DISPLAY_NAME="Game"
BUNDLE_ID="com.example.game"
XCODE_APP="/Applications/Xcode_26.0.1.app"
JOBS=""

usage() {
	cat <<EOF
Usage: $0 [options]

Build a maximal-debug iOS device template, then package:
  - game.app
  - ios_template.zip, containing game.app
  - ios.zip, the Godot iOS Xcode export template zip

Options:
  --out-dir PATH       Output directory. Default: DEVEL/ios-debug-device
  --app-name NAME      Bundle/executable name. Default: game
  --display-name NAME  Display name. Default: Game
  --bundle-id ID       Bundle identifier. Default: com.example.game
  --xcode PATH         Xcode.app path. Default: /Applications/Xcode_26.0.1.app
  -j N                 SCons jobs.
  -h, --help           Show this help.

Environment:
  BUILD_NAME           Godot build name. Default: local
  GODOT_VERSION_STATUS Godot version status. Default: dev
EOF
}

while [[ $# -gt 0 ]]; do
	case "$1" in
		--out-dir)
			OUT_DIR="$2"
			shift 2
			;;
		--app-name)
			APP_NAME="$2"
			shift 2
			;;
		--display-name)
			DISPLAY_NAME="$2"
			shift 2
			;;
		--bundle-id)
			BUNDLE_ID="$2"
			shift 2
			;;
		--xcode)
			XCODE_APP="$2"
			shift 2
			;;
		-j)
			JOBS="-j$2"
			shift 2
			;;
		-h | --help)
			usage
			exit 0
			;;
		*)
			echo "Unknown argument: $1" >&2
			usage >&2
			exit 2
			;;
	esac
done

if [[ "$APP_NAME" == *.* || "$APP_NAME" == */* || "$APP_NAME" == *" "* ]]; then
	echo "--app-name must be a simple product name without spaces, dots, or slashes." >&2
	exit 2
fi

if [[ -d "$XCODE_APP" ]]; then
	export DEVELOPER_DIR="$XCODE_APP/Contents/Developer"
else
	echo "Warning: $XCODE_APP not found; using the active xcode-select developer dir." >&2
fi

export BUILD_NAME="${BUILD_NAME:-local}"
export GODOT_VERSION_STATUS="${GODOT_VERSION_STATUS:-dev}"

BUILD_DIR="$OUT_DIR/build"
STAGE_DIR="$BUILD_DIR/xcode_template"
XCODE_BUILD_DIR="$BUILD_DIR/xcodebuild"
GODOT_IOS_ZIP="$ROOT_DIR/bin/godot_ios_dev.zip"

echo "==> Building Godot iOS template_debug for arm64 device"
(
	cd "$ROOT_DIR"
	scons \
		platform=ios \
		module_mono_enabled=yes \
		werror=no \
		target=template_debug \
		arch=arm64 \
		dev_build=yes \
		dev_mode=yes \
		tests=no \
		debug_symbols=yes \
		optimize=none \
		disable_path_overrides=no \
		vulkan=no \
		metal=yes \
		module_text_server_fb_enabled=yes \
		generate_bundle=yes \
		redirect_build_objects=no \
		cache_path=.scons_cache \
		${JOBS}
)

if [[ ! -f "$GODOT_IOS_ZIP" ]]; then
	echo "Expected generated template zip was not found: $GODOT_IOS_ZIP" >&2
	exit 1
fi

echo "==> Preparing Xcode project"
rm -rf "$BUILD_DIR"
mkdir -p "$STAGE_DIR" "$OUT_DIR"
/usr/bin/unzip -q "$GODOT_IOS_ZIP" -d "$STAGE_DIR"

DEBUG_TEMPLATE_LIB="$STAGE_DIR/libgodot.ios.debug.xcframework/ios-arm64/libgodot.a"
RELEASE_TEMPLATE_DEVICE_DIR="$STAGE_DIR/libgodot.ios.release.xcframework/ios-arm64"
if [[ -f "$DEBUG_TEMPLATE_LIB" && -d "$RELEASE_TEMPLATE_DEVICE_DIR" ]]; then
	echo "==> Mirroring debug device library into release slot for ios.zip"
	rm -f "$RELEASE_TEMPLATE_DEVICE_DIR/empty"
	cp "$DEBUG_TEMPLATE_LIB" "$RELEASE_TEMPLATE_DEVICE_DIR/libgodot.a"
fi

rm -f "$OUT_DIR/ios.zip"
(
	cd "$STAGE_DIR"
	/usr/bin/ditto --norsrc -c -k . "$OUT_DIR/ios.zip"
)

python3 - "$STAGE_DIR" "$APP_NAME" "$DISPLAY_NAME" "$BUNDLE_ID" <<'PY'
import os
import re
import shutil
import sys
from pathlib import Path

stage = Path(sys.argv[1])
app_name = sys.argv[2]
display_name = sys.argv[3]
bundle_id = sys.argv[4]

old_prefix = "godot_apple_embedded"

def move_if_exists(src: Path, dst: Path) -> None:
    if src.exists():
        if dst.exists():
            if dst.is_dir():
                shutil.rmtree(dst)
            else:
                dst.unlink()
        src.rename(dst)

for path in stage.glob("libgodot.*.xcframework"):
    if path.name != "libgodot.ios.debug.xcframework":
        shutil.rmtree(path)

move_if_exists(stage / "libgodot.ios.debug.xcframework", stage / f"{app_name}.xcframework")
move_if_exists(stage / "data.pck", stage / f"{app_name}.pck")
move_if_exists(stage / f"{old_prefix}.xcodeproj", stage / f"{app_name}.xcodeproj")
move_if_exists(stage / old_prefix, stage / app_name)

for root, dirs, files in os.walk(stage, topdown=False):
    root_path = Path(root)
    for filename in files:
        if old_prefix in filename:
            move_if_exists(root_path / filename, root_path / filename.replace(old_prefix, app_name))
    for dirname in dirs:
        if old_prefix in dirname:
            move_if_exists(root_path / dirname, root_path / dirname.replace(old_prefix, app_name))

tokens = {
    "$additional_pbx_files": "",
    "$additional_pbx_frameworks_build": "",
    "$additional_pbx_frameworks_refs": "",
    "$additional_pbx_resources_build": "",
    "$additional_pbx_resources_refs": "",
    "$additional_plist_content": "",
    "$binary": app_name,
    "$bundle_identifier": bundle_id,
    "$camera_usage_description": "Camera access is requested by the project.",
    "$code_sign_identity_debug": "Apple Development",
    "$code_sign_identity_release": "Apple Distribution",
    "$code_sign_style_debug": "Automatic",
    "$code_sign_style_release": "Automatic",
    "$cpp_code": "\n// Godot Plugins\nvoid godot_apple_embedded_plugins_initialize() {\n}\n\nvoid godot_apple_embedded_plugins_deinitialize() {\n}\n",
    "$default_build_config": "Debug",
    "$docs_in_place": "<false/>",
    "$docs_sharing": "<false/>",
    "$entitlements_full": "",
    "$export_method": "development",
    "$godot_archs": "arm64",
    "$interface_orientations": "<string>UIInterfaceOrientationPortrait</string>\n\t\t<string>UIInterfaceOrientationLandscapeLeft</string>\n\t\t<string>UIInterfaceOrientationLandscapeRight</string>",
    "$ipad_interface_orientations": "<string>UIInterfaceOrientationPortrait</string>\n\t\t<string>UIInterfaceOrientationLandscapeLeft</string>\n\t\t<string>UIInterfaceOrientationLandscapeRight</string>\n\t\t<string>UIInterfaceOrientationPortraitUpsideDown</string>",
    "$launch_screen_background_color": 'red="0" green="0" blue="0" alpha="1"',
    "$launch_screen_image_mode": "scaleAspectFit",
    "$linker_flags": "",
    "$microphone_usage_description": "Microphone access is requested by the project.",
    "$modules_buildfile": "",
    "$modules_buildgrp": "",
    "$modules_buildphase": "",
    "$modules_fileref": "",
    "$moltenvk_buildfile": "",
    "$moltenvk_buildgrp": "",
    "$moltenvk_buildphase": "",
    "$moltenvk_fileref": "",
    "$name": display_name,
    "$os_deployment_target": "IPHONEOS_DEPLOYMENT_TARGET = 14.0;",
    "$pbx_embeded_frameworks": "",
    "$pbx_launch_screen_build_phase": "",
    "$pbx_launch_screen_build_reference": "",
    "$pbx_launch_screen_copy_files": "",
    "$pbx_launch_screen_file_reference": "",
    "$pbx_locale_build_reference": "",
    "$pbx_locale_file_reference": "",
    "$photolibrary_usage_description": "Photo library access is requested by the project.",
    "$plist_launch_screen_name": "",
    "$priv_api_types": "<array>\n\t</array>",
    "$priv_collection": "",
    "$priv_tracking": "<key>NSPrivacyTracking</key>\n\t<false/>",
    "$provisioning_profile_specifier_debug": "",
    "$provisioning_profile_specifier_release": "",
    "$provisioning_profile_specifier": "",
    "$provisioning_profile_uuid_debug": "",
    "$provisioning_profile_uuid_release": "",
    "$provisioning_profile_uuid": "",
    "$required_device_capabilities": "",
    "$sdkroot": "iphoneos",
    "$short_version": "1.0",
    "$signature": "????",
    "$targeted_device_family": "1,2",
    "$team_id": '""',
    "$valid_archs": "arm64",
    "$version": "1",
}

token_items = sorted(tokens.items(), key=lambda item: len(item[0]), reverse=True)
binary_suffixes = {".a", ".png", ".jpg", ".jpeg", ".icns"}

for path in stage.rglob("*"):
    if not path.is_file():
        continue
    if path.suffix.lower() in binary_suffixes:
        continue
    data = path.read_bytes()
    if b"\0" in data[:4096]:
        continue
    try:
        text = data.decode("utf-8")
    except UnicodeDecodeError:
        continue
    text = text.replace(old_prefix, app_name)
    for token, value in token_items:
        text = text.replace(token, value)
    text = text.replace("\t\t\t\tASSETCATALOG_COMPILER_APPICON_NAME = AppIcon;\n", "")
    path.write_text(text, encoding="utf-8")

asset_dir = stage / app_name / "Images.xcassets"
asset_dir.mkdir(parents=True, exist_ok=True)
(asset_dir / "Contents.json").write_text('{"info":{"author":"xcode","version":1}}\n', encoding="utf-8")

unresolved = []
token_re = re.compile(r"(?<!\()\$[A-Za-z_][A-Za-z0-9_]*")
for path in stage.rglob("*"):
    if not path.is_file():
        continue
    if path.suffix.lower() in binary_suffixes:
        continue
    try:
        text = path.read_text(encoding="utf-8")
    except UnicodeDecodeError:
        continue
    matches = token_re.findall(text)
    if matches:
        unresolved.append((path.relative_to(stage), sorted(set(matches))))

if unresolved:
    for path, matches in unresolved:
        print(f"Unresolved template tokens in {path}: {', '.join(matches)}", file=sys.stderr)
    sys.exit(1)
PY

echo "==> Building unsigned device app bundle"
rm -rf "$XCODE_BUILD_DIR"
xcodebuild \
	-project "$STAGE_DIR/$APP_NAME.xcodeproj" \
	-scheme "$APP_NAME" \
	-configuration Debug \
	-sdk iphoneos \
	-destination "generic/platform=iOS" \
	-derivedDataPath "$XCODE_BUILD_DIR/DerivedData" \
	CONFIGURATION_BUILD_DIR="$XCODE_BUILD_DIR/Debug-iphoneos" \
	CODE_SIGNING_ALLOWED=NO \
	CODE_SIGNING_REQUIRED=NO \
	CODE_SIGN_IDENTITY= \
	DEVELOPMENT_TEAM= \
	IPHONEOS_DEPLOYMENT_TARGET=15.0 \
	build

APP_BUNDLE="$XCODE_BUILD_DIR/Debug-iphoneos/$APP_NAME.app"
if [[ ! -d "$APP_BUNDLE" ]]; then
	echo "Expected app bundle was not produced: $APP_BUNDLE" >&2
	exit 1
fi

rm -rf "$OUT_DIR/$APP_NAME.app" "$OUT_DIR/ios_template.zip"
cp -R "$APP_BUNDLE" "$OUT_DIR/$APP_NAME.app"

(
	cd "$OUT_DIR"
	/usr/bin/ditto --norsrc -c -k --keepParent "$APP_NAME.app" ios_template.zip
)

echo "==> Wrote:"
echo "    $OUT_DIR/$APP_NAME.app"
echo "    $OUT_DIR/ios_template.zip"
echo "    $OUT_DIR/ios.zip"
