// node --test scripts/
import assert from "node:assert/strict";
import { mkdtempSync, mkdirSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import { bom, dotnetComponents, hexFromSri, licenseProblems, merge, nativeComponents, nuspecLicense, postgresqlComponents, webComponents } from "./sbom.mjs";

test("SRI hashes become hex", () => {
  assert.equal(hexFromSri("sha512-AAEC"), "000102");
  assert.equal(hexFromSri(""), null);
  assert.equal(hexFromSri(undefined), null);
});

test("nuspec licenses: expressions, nuget URLs, unknown", () => {
  assert.equal(nuspecLicense('<license type="expression">Apache-2.0</license>'), "Apache-2.0");
  assert.equal(nuspecLicense("<licenseUrl>https://licenses.nuget.org/MIT</licenseUrl>"), "MIT");
  assert.equal(nuspecLicense("<licenseUrl>https://example.test/eula</licenseUrl>"), null);
});

test("only packages a program loads at run time are listed", () => {
  const cache = mkdtempSync(join(tmpdir(), "sbom-"));
  mkdirSync(join(cache, "npgsql", "10.0.3"), { recursive: true });
  writeFileSync(join(cache, "npgsql", "10.0.3", "npgsql.nuspec"), '<license type="expression">PostgreSQL</license>');
  const deps = {
    runtimeTarget: { name: "rt/win-x64" },
    targets: {
      "rt/win-x64": {
        "DataScope.Api/1.0.0": { runtime: {} },
        "runtimepack.Microsoft.NETCore.App.Runtime.win-x64/10.0.12": { native: {} },
        "Npgsql/10.0.3": { runtime: {} },
        "Microsoft.EntityFrameworkCore.Design/10.0.12": { dependencies: {} },
        "Mystery/1.0.0": { runtime: {} },
      },
    },
    libraries: {
      "DataScope.Api/1.0.0": { type: "project" },
      "runtimepack.Microsoft.NETCore.App.Runtime.win-x64/10.0.12": { type: "runtimepack", sha512: "" },
      "Npgsql/10.0.3": { type: "package", sha512: "sha512-AAEC" },
      "Microsoft.EntityFrameworkCore.Design/10.0.12": { type: "package", sha512: "sha512-AAEC" },
      "Mystery/1.0.0": { type: "package", sha512: "sha512-AAEC" },
    },
  };
  const list = dotnetComponents(deps, "api", cache);
  assert.deepEqual(list.map((c) => [c.purl, c.license]), [
    ["pkg:nuget/Microsoft.NETCore.App.Runtime.win-x64@10.0.12", "MIT"],
    ["pkg:nuget/Npgsql@10.0.3", "PostgreSQL"],
    ["pkg:nuget/Mystery@1.0.0", null],
  ]);
  assert.equal(list[1].hash, "000102");
});

test("the web UI lists production npm packages, not build tools", () => {
  const lock = {
    packages: {
      "": { name: "web" },
      "node_modules/react": { version: "19.1.0", license: "MIT", integrity: "sha512-AAEC" },
      "node_modules/@fontsource/ibm-plex-sans": { version: "5.2.6", license: "OFL-1.1" },
      "node_modules/vite": { version: "6.3.5", license: "MIT", dev: true },
      "node_modules/fsevents": { version: "2.3.3", license: "MIT", devOptional: true },
    },
  };
  assert.deepEqual(webComponents(lock).map((c) => c.purl), ["pkg:npm/react@19.1.0", "pkg:npm/%40fontsource/ibm-plex-sans@5.2.6"]);
});

test("a component in several programs is listed once, naming each", () => {
  const a = { purl: "pkg:nuget/Npgsql@10.0.3", name: "Npgsql", version: "10.0.3", program: "api" };
  const merged = merge([[a], [{ ...a, program: "worker" }], [{ ...a, program: "worker" }]]);
  assert.equal(merged.length, 1);
  assert.deepEqual(merged[0].programs, ["api", "worker"]);
  const doc = bom(merged, "1.2.3", new Date("2026-10-10T00:00:00Z"), "00000000-0000-0000-0000-000000000000");
  assert.equal(doc.specVersion, "1.5");
  assert.equal(doc.metadata.component.version, "1.2.3");
  assert.deepEqual(doc.components[0].properties.map((p) => p.value), ["api", "worker"]);
  assert.deepEqual(doc.dependencies[0].dependsOn, ["pkg:nuget/Npgsql@10.0.3"]);
});

test("copyleft licenses are refused and unknown ones reported", () => {
  const { denied, unknown } = licenseProblems([
    { purl: "a", license: "MIT" },
    { purl: "b", license: "GPL-3.0-only" },
    { purl: "c", license: "LGPL-2.1-or-later" },
    { purl: "d", license: "Apache-2.0 OR AGPL-3.0" },
    { purl: "e", license: null },
  ]);
  assert.deepEqual(denied.map((c) => c.purl), ["b", "c", "d"]);
  assert.deepEqual(unknown.map((c) => c.purl), ["e"]);
});

test("native files are hashed from the package, with what was compiled into them", () => {
  const folder = mkdtempSync(join(tmpdir(), "sbom-native-"));
  mkdirSync(join(folder, "app", "worker", "ocr"), { recursive: true });
  writeFileSync(join(folder, "app", "worker", "ocr", "lib.dll"), "abc");
  const native = [{
    file: "app/worker/ocr/lib.dll", name: "lib", version: "1.0", license: "MIT", purl: "pkg:generic/lib@1.0",
    compiledIn: [{ name: "zlib", version: "1.2.11", license: "Zlib", purl: "pkg:github/madler/zlib@1.2.11" }],
  }];
  const list = nativeComponents(folder, native);
  assert.equal(list[0].sha256, "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad");
  assert.equal(list[1].note, "Compiled into lib.dll");
  const doc = bom(merge([list]), "1.0.0", new Date(0), "00000000-0000-0000-0000-000000000000");
  assert.deepEqual(doc.components.find((c) => c.name === "lib").hashes, [{ alg: "SHA-256", content: list[0].sha256 }]);
  assert.throws(() => nativeComponents(folder, [{ ...native[0], file: "app/worker/ocr/missing.dll" }]), /not in the package/);
});

test("the bundled PostgreSQL is listed with its DLLs, and only its two LGPL DLLs pass the licence gate", () => {
  const folder = mkdtempSync(join(tmpdir(), "sbom-pg-"));
  const bin = join(folder, "app", "postgresql", "bin");
  mkdirSync(bin, { recursive: true });
  writeFileSync(join(folder, "app", "postgresql", "VERSION"), "16.15\n");
  for (const f of ["postgres.exe", "libintl-9.dll", "libiconv-2.dll", "libssl-3-x64.dll", "mystery.dll"]) writeFileSync(join(bin, f), f);
  mkdirSync(join(folder, "app", "vcredist"));
  writeFileSync(join(folder, "app", "vcredist", "vc_redist.x64.exe"), "redist");

  const components = postgresqlComponents(folder);
  const byName = Object.fromEntries(components.map((c) => [c.name, c]));
  assert.equal(byName.postgresql.version, "16.15");
  assert.equal(byName.postgresql.license, "PostgreSQL");
  assert.equal(byName["libintl-9.dll"].license, "LGPL-2.1-or-later");
  assert.equal(byName["libssl-3-x64.dll"].license, "Apache-2.0");
  assert.equal(byName["vc_redist.x64.exe"].license, "LicenseRef-Microsoft-Visual-CPP-Redistributable");

  const { denied, unknown } = licenseProblems(merge([components]));
  assert.deepEqual(denied, []);
  assert.deepEqual(unknown.map((c) => c.name), ["mystery.dll"]);
  const lgplElsewhere = { name: "x", purl: "pkg:nuget/x@1", license: "LGPL-2.1-only", program: "api" };
  assert.equal(licenseProblems(merge([[lgplElsewhere]])).denied.length, 1);
});

test("a package without PostgreSQL lists none", () => {
  assert.deepEqual(postgresqlComponents(mkdtempSync(join(tmpdir(), "sbom-nopg-"))), []);
});
