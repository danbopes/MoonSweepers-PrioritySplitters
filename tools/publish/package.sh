#!/usr/bin/env bash
# Builds the Release DLL and packs release/PrioritySplitters-<version>.zip in the layout Nexus users extract
# into the game folder. The version comes from the csproj and must agree with Plugin.cs, the Nexus README
# title and the changelog, so a half-done bump fails here instead of on Nexus.
set -euo pipefail
root=$(cd "$(dirname "$0")/../.." && pwd)
version=$(sed -n 's/.*<Version>\(.*\)<\/Version>.*/\1/p' "$root/src/PrioritySplitters.csproj")
plugin=$(sed -n 's/.*public const string Version = "\(.*\)";.*/\1/p' "$root/src/Plugin.cs")
[ -n "$version" ] || { echo "no <Version> in src/PrioritySplitters.csproj" >&2; exit 1; }
[ "$version" = "$plugin" ] || { echo "csproj says $version but Plugin.cs says $plugin" >&2; exit 1; }
grep -qx "Priority Splitters $version for Moon Sweepers" "$root/tools/nexus/README.txt" || { echo "tools/nexus/README.txt title is not $version" >&2; exit 1; }
grep -qx "$version" "$root/tools/nexus/changelog.txt" || { echo "tools/nexus/changelog.txt has no $version entry" >&2; exit 1; }
if pgrep -f 'MoonSweepers\.exe' >/dev/null; then
  echo "note: the game is running; a Release build does not touch its plugins folder" >&2
fi

dotnet build "$root/src" -c Release --nologo -v q
dll="$root/src/bin/Release/net6.0/PrioritySplitters.dll"
strings -el "$dll" | grep -qx "$version" || { echo "the built DLL does not carry version $version" >&2; exit 1; }

# /tmp is a small tmpfs on this machine; tools/.tmp is the gitignored scratch folder.
stage="$root/tools/.tmp/package"
rm -rf "$stage"
mkdir -p "$stage/BepInEx/plugins/PrioritySplitters" "$root/release"
cp "$dll" "$stage/BepInEx/plugins/PrioritySplitters/"
cp "$root/tools/nexus/README.txt" "$stage/README.txt"
zip="$root/release/PrioritySplitters-$version.zip"
rm -f "$zip"
(cd "$stage" && zip -r -X "$zip" BepInEx README.txt >/dev/null)
rm -rf "$stage"
unzip -l "$zip"
echo "$zip"
