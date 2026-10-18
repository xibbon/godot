#!/usr/bin/env bash
#
# LOCAL DEVELOPMENT ONLY. The shippable `-dotnet` template payloads are built by
# the XogotExportTemplates repo (scripts/assemble-templates.sh + export-apps.sh),
# which is also what uploads them to R2 and writes the manifest slots. This
# script exists so a single developer can produce a payload for `make
# dev-template` without a full template-release run; if the two ever disagree
# about the payload shape, XogotExportTemplates is right.
#
# Turn Godot's macOS `generate_bundle=yes` output into a Xogot template payload.
#
# `scons ... generate_bundle=yes` emits `bin/godot_macos*.zip` containing only
# `macos_template.app/`, whose Info.plist is still a *template*: `$binary`, `$name`,
# `$bundle_identifier` and friends are literal placeholders, so the file is not a valid plist.
#
# Xogot's deploy path clones a ready-to-use `game.app` out of the payload
# (`DeploymentCoordinator.resolveTemplateApp` / `clonePrecompiledTemplate`) and only then stamps
# the real bundle id, name and version through `MacBundleConfigurationService`. Handing it the
# raw Godot zip fails with:
#
#   Bundle preparation failed: Failed to read Info.plist ... isn't in the correct format
#
# The published payloads therefore contain BOTH `game.app/` and `macos_template.app/`. This
# script produces that shape, so a locally built engine can be installed with `make dev-template`.
#
# Usage: make-macos-template-payload.sh <godot generate_bundle zip> <output zip>
set -euo pipefail

if [[ $# -ne 2 ]]; then
    echo "Usage: $(basename "$0") <godot-macos-bundle.zip> <output.zip>" >&2
    exit 64
fi

source_zip=$1
output_zip=$2

[[ -f "$source_zip" ]] || { echo "error: no such archive: $source_zip" >&2; exit 64; }

work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT

/usr/bin/unzip -q "$source_zip" -d "$work"

template_app="$work/macos_template.app"
[[ -d "$template_app" ]] || { echo "error: $source_zip has no macos_template.app" >&2; exit 1; }

if [[ -d "$work/game.app" ]]; then
    echo "==> payload already contains game.app; repacking unchanged"
else
    echo "==> synthesizing game.app from macos_template.app"
    cp -R "$template_app" "$work/game.app"

    # Godot names the template binary godot_macos_{debug,release}.universal; the bundle Xogot
    # clones must be self-consistent, so the binary becomes `game` and CFBundleExecutable with it.
    binary=$(find "$work/game.app/Contents/MacOS" -maxdepth 1 -type f | head -1)
    [[ -n "$binary" ]] || { echo "error: no binary in macos_template.app/Contents/MacOS" >&2; exit 1; }
    mv "$binary" "$work/game.app/Contents/MacOS/game"

    # Placeholder values mirror the published payloads. They are deliberately generic: every one
    # of them is overwritten per-deploy by MacBundleConfigurationService, so what matters here is
    # only that the result parses and that CFBundleExecutable matches the binary above.
    plist="$work/game.app/Contents/Info.plist"
    /usr/bin/sed -i '' \
        -e 's/\$binary/game/g' \
        -e 's/\$name/game/g' \
        -e 's/\$bundle_identifier/com.godot.game/g' \
        -e 's/\$short_version/1.0.0/g' \
        -e 's/\$version/1.0.0/g' \
        -e 's/\$signature/????/g' \
        -e 's/\$copyright//g' \
        -e 's/public.app-category.\$app_category/public.app-category.games/g' \
        -e 's/\$min_version_arm64/11.00/g' \
        -e 's/\$min_version_x86_64/10.13/g' \
        -e 's/\$platfbuild//g' \
        -e 's/\$sdkver//g' \
        -e 's/\$sdkbuild//g' \
        -e 's/\$sdkname//g' \
        -e 's/\$xcodever//g' \
        -e 's/\$xcodebuild//g' \
        "$plist"
    # Line-oriented placeholders stand in for whole elements, not attribute values.
    /usr/bin/sed -i '' \
        -e 's/^\$highres$/\t<true\/>/' \
        -e '/^\$liquid_glass_icon$/d' \
        -e '/^\$usage_descriptions$/d' \
        -e '/^\$additional_plist_content$/d' \
        -e 's/\$highres/<true\/>/g' \
        "$plist"

    if ! /usr/bin/plutil -lint "$plist" >/dev/null; then
        echo "error: synthesized Info.plist is not a valid plist" >&2
        /usr/bin/plutil -lint "$plist" >&2 || true
        exit 1
    fi
fi

rm -f "$output_zip"
mkdir -p "$(dirname "$output_zip")"
( cd "$work" && /usr/bin/zip -qry "$output_zip" game.app macos_template.app )
echo "==> wrote $output_zip"
/usr/bin/unzip -l "$output_zip" | awk '{print $4}' | grep -oE '^[^/]+/' | sort -u | sed 's/^/    /'
