#!/usr/bin/env bash
# Builds the Windows install package: a zip with self-contained win-x64 builds of
# the API (with the web UI), the scan worker and the setup tool, plus the install
# scripts, the setup wizard, PostgreSQL and the Visual C++ runtime for it (D169), and a
# CycloneDX SBOM (sbom.cdx.json). Then the one-file installer: the wizard with that zip inside.
# Runs on Linux or Windows (Git Bash); needs the .NET SDK, Node, curl, zip and unzip.
#
#   scripts/package.sh [version]        e.g. scripts/package.sh 0.2.0-lab.7
#
# DATASCOPE_BUNDLE_POSTGRESQL=0 leaves PostgreSQL and the Visual C++ runtime out (no download; customers then bring
# their own PostgreSQL). Downloads are kept in artifacts/cache.
#
# Output: artifacts/DataScope-<version>.zip, artifacts/DataScope-Setup-<version>.exe and their .sha256 files, and
# copies of the scripts an administrator runs on other servers before and after setup (D171), so they come
# with the setup file and no one has to unpack it.
set -euo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"
version="${1:-0.2.0-dev}"
if [[ ! "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.]+)?$ ]]; then
  echo "Version must look like 1.2.3 or 1.2.3-label, got '$version'" >&2
  exit 2
fi

name="DataScope-$version"
out="$root/artifacts/$name"
setup="$root/artifacts/DataScope-Setup-$version.exe"
customer_scripts=(Prepare-DataScopeDomain.ps1 Enable-DataScopeAuditing.ps1)
rm -rf "$out" "$root/artifacts/$name.zip" "$root/artifacts/$name.zip.sha256" "$setup" "$setup.sha256"
for s in "${customer_scripts[@]}"; do rm -f "$root/artifacts/$s"; done
mkdir -p "$out/app"

echo "==> Building the web UI"
npm --prefix "$root/web" ci --no-audit --no-fund
npm --prefix "$root/web" run build

publish() {
  local project="$1" target="$2"
  echo "==> Publishing $project"
  dotnet publish "$root/src/$project/$project.csproj" -c Release -r win-x64 --self-contained true \
    -p:Version="$version" -p:DebugType=none -p:GenerateDocumentationFile=false -o "$out/app/$target" --nologo
  # Development settings (fake sign-in, lab folders) never ship.
  rm -f "$out/app/$target/appsettings.Development.json"
}
publish DataScope.Api api
publish DataScope.Worker worker
publish DataScope.Setup setup
cp -R "$root/web/dist" "$out/app/api/wwwroot"

echo "==> Building the setup wizard"
dotnet build "$root/src/DataScope.Installer/DataScope.Installer.csproj" -c Release -p:Version="$version" \
  -o "$root/artifacts/setup-wizard" --nologo
cp "$root/artifacts/setup-wizard/DataScope-Setup.exe" "$out/"

# PostgreSQL (bin, lib and share; not pgAdmin, StackBuilder, headers or docs) and the Visual C++ runtime it needs.
bundle_postgresql() {
  # shellcheck source=windows-dependencies.env
  source "$root/scripts/windows-dependencies.env"
  local cache="$root/artifacts/cache"
  local zip="$cache/postgresql-$POSTGRESQL_VERSION-windows-x64-binaries.zip"
  mkdir -p "$cache"
  echo "==> Bundling PostgreSQL $POSTGRESQL_VERSION"
  if [[ ! -f "$zip" ]]; then
    curl -fsSL --retry 3 -o "$zip.part" "$POSTGRESQL_URL"
    mv -f "$zip.part" "$zip"
  fi
  local actual
  actual="$(sha256sum "$zip" | cut -d' ' -f1)"
  if [[ "$actual" != "$POSTGRESQL_SHA256" ]]; then
    echo "$POSTGRESQL_URL downloaded with SHA-256 $actual, but scripts/windows-dependencies.env pins '$POSTGRESQL_SHA256'." >&2
    echo "Check the download, then pin its SHA-256 there." >&2
    exit 2
  fi
  local unpacked="$cache/pgsql-$POSTGRESQL_VERSION"
  rm -rf "$unpacked"
  unzip -q "$zip" 'pgsql/bin/*' 'pgsql/lib/*' 'pgsql/share/*' -d "$unpacked"
  find "$unpacked/pgsql" -type f \( -name '*.lib' -o -name '*.pdb' \) -delete
  mkdir -p "$out/app/postgresql"
  cp -R "$unpacked/pgsql/." "$out/app/postgresql/"
  printf '%s\n' "${POSTGRESQL_VERSION%-*}" > "$out/app/postgresql/VERSION"

  echo "==> Bundling the Visual C++ Redistributable"
  mkdir -p "$out/app/vcredist"
  curl -fsSL --retry 3 -o "$out/app/vcredist/vc_redist.x64.exe" "$VC_REDIST_URL"
}
if [[ "${DATASCOPE_BUNDLE_POSTGRESQL:-1}" != "0" ]]; then
  bundle_postgresql
else
  echo "==> Not bundling PostgreSQL (DATASCOPE_BUNDLE_POSTGRESQL=0)"
fi

cp "$root/deploy/windows/Install-DataScope.ps1" "$root/deploy/windows/Update-DataScope.ps1" "$root/deploy/windows/Uninstall-DataScope.ps1" \
  "$root/deploy/windows/Prepare-DataScopeDomain.ps1" "$root/deploy/windows/Enable-DataScopeAuditing.ps1" "$out/"
cp "$root/docs/INSTALL-DATASCOPE.md" "$out/INSTALL.md"
cp "$root/docs/ADMIN-GUIDE.md" "$out/"
cp "$root/THIRD-PARTY-NOTICES.md" "$out/"
printf '%s\n' "$version" > "$out/VERSION"

echo "==> Writing the software bill of materials"
node "$root/scripts/sbom.mjs" "$out" "$version"

echo "==> Writing checksums"
( cd "$out" && find app -type f | LC_ALL=C sort | while read -r f; do
    printf '%s  %s\n' "$(sha256sum "$f" | cut -d' ' -f1)" "$f"
  done > SHA256SUMS )

echo "==> Zipping"
( cd "$root/artifacts" && zip -qr -X "$name.zip" "$name" && sha256sum "$name.zip" > "$name.zip.sha256" )
echo "Built artifacts/$name.zip ($(du -h "$root/artifacts/$name.zip" | cut -f1))"

echo "==> Writing the one-file installer"
node "$root/scripts/make-setup.mjs" "$out/DataScope-Setup.exe" "$root/artifacts/$name.zip" "$setup"
for s in "${customer_scripts[@]}"; do cp "$out/$s" "$root/artifacts/$s"; done
