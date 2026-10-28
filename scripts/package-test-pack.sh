#!/usr/bin/env bash
# Builds the detection test pack zip for a Windows lab (D138): the synthetic files from tests/test-pack, what Data
# Scope should report for each one, and the scripts that share them and check a scan. Needs zip.
#
#   scripts/package-test-pack.sh [version]        e.g. scripts/package-test-pack.sh 0.2.0-lab.7
#
# Output: artifacts/DataScope-Test-Pack-<version>.zip and its .sha256 file.
set -euo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"
version="${1:-0.2.0-dev}"
if [[ ! "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.]+)?$ ]]; then
  echo "Version must look like 1.2.3 or 1.2.3-label, got '$version'" >&2
  exit 2
fi

name="DataScope-Test-Pack-$version"
work="$root/artifacts/test-pack-build"
out="$work/DataScope-Test-Pack"
rm -rf "$work" "$root/artifacts/$name.zip" "$root/artifacts/$name.zip.sha256"
mkdir -p "$out"

cp -R "$root/tests/test-pack/files" "$out/files"
cp "$root/tests/test-pack/expected.csv" "$out/expected.csv"
# Windows line endings for the text a person opens in Notepad.
sed 's/$/\r/' "$root/tests/test-pack/README.txt" > "$out/README.txt"
cp "$root/deploy/windows/lab/Install-DataScopeTestPack.ps1" "$root/deploy/windows/lab/Test-DataScopeTestPack.ps1" "$out/"

(cd "$work" && zip -qrX "$root/artifacts/$name.zip" DataScope-Test-Pack)
(cd "$root/artifacts" && sha256sum "$name.zip" > "$name.zip.sha256")
rm -rf "$work"
echo "Wrote artifacts/$name.zip ($(find "$root/tests/test-pack/files" -type f | wc -l) files)"
