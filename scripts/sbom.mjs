#!/usr/bin/env node
// Writes a CycloneDX 1.5 software bill of materials for a built package, from what is actually in it:
// the .NET packages each published program loads (its .deps.json, runtime assets only), the .NET runtime
// itself, the native text recognition libraries and model the worker ships (NATIVE below, hashed from the
// files), and the npm packages bundled into the web UI (production dependencies in web/package-lock.json).
// Licenses come from the local NuGet cache and the npm lock file, so nothing is looked up online.
// It fails when a shipped component has a license on the deny list, and lists any it couldn't identify.
//
//   node scripts/sbom.mjs <package folder> <version>
//
// Output: <package folder>/sbom.cdx.json
import { createHash, randomUUID } from "node:crypto";
import { existsSync, readdirSync, readFileSync, writeFileSync } from "node:fs";
import { homedir } from "node:os";
import { join, resolve } from "node:path";

// Copyleft licenses would put obligations on customers that an Apache-2.0 project that people build into their own products can't meet.
const DENIED = /\b(A?GPL|LGPL|SSPL|EUPL|CC-BY-SA|CC-BY-NC|OSL|CPAL|Commons-Clause)\b/i;

export function hexFromSri(sri) {
  const match = /^sha512-(.+)$/.exec(sri ?? "");
  return match ? Buffer.from(match[1], "base64").toString("hex") : null;
}

/** The license expression in a .nuspec: the SPDX expression, else a well-known license URL. */
export function nuspecLicense(xml) {
  const expression = /<license\s+type="expression"\s*>([^<]+)<\/license>/i.exec(xml);
  if (expression) return expression[1].trim();
  const url = /<licenseUrl>([^<]+)<\/licenseUrl>/i.exec(xml)?.[1] ?? "";
  if (/licenses\.nuget\.org\/(.+)$/i.test(url)) return decodeURIComponent(/licenses\.nuget\.org\/(.+)$/i.exec(url)[1]);
  if (/go\.microsoft\.com\/fwlink\/\?LinkId=329770|dotnet\.microsoft\.com\/.*license/i.test(url)) return "MIT";
  return null;
}

function nugetLicense(cache, id, version) {
  const dir = join(cache, id.toLowerCase(), version.toLowerCase());
  const nuspec = existsSync(dir) ? readdirSync(dir).find((f) => f.endsWith(".nuspec")) : undefined;
  return nuspec ? nuspecLicense(readFileSync(join(dir, nuspec), "utf8")) : null;
}

/** .NET components loaded by one published program. */
export function dotnetComponents(depsJson, program, cache) {
  const target = depsJson.targets[depsJson.runtimeTarget.name];
  const components = [];
  for (const [key, assets] of Object.entries(target)) {
    const library = depsJson.libraries[key];
    const ships = assets.runtime || assets.native || assets.runtimeTargets;
    if (!ships || library.type === "project") continue;
    const slash = key.lastIndexOf("/");
    let [name, version] = [key.slice(0, slash), key.slice(slash + 1)];
    let license;
    if (library.type === "runtimepack") {
      // The .NET and ASP.NET Core runtimes, bundled because the programs are self-contained.
      name = name.replace(/^runtimepack\./, "");
      license = "MIT";
    } else {
      license = nugetLicense(cache, name, version);
    }
    components.push({
      name,
      version,
      purl: `pkg:nuget/${name}@${version}`,
      hash: hexFromSri(library.sha512),
      license,
      program,
    });
  }
  return components;
}

/**
 * Files shipped outside any .NET or npm package: Tesseract and Leptonica for text recognition (D129) and the English
 * model. Versions are the ones the files state; the libraries compiled into Leptonica are listed with it, from the
 * version strings in the DLL. A file listed here that is missing from the package fails the build.
 */
export const NATIVE = [
  {
    file: "app/worker/ocr/tesseract55.dll", name: "tesseract", version: "5.5.0", license: "Apache-2.0",
    purl: "pkg:github/tesseract-ocr/tesseract@5.5.0", note: "Windows x64 build from the TesseractOCR NuGet package 5.5.2",
  },
  {
    file: "app/worker/ocr/leptonica-1.85.0.dll", name: "leptonica", version: "1.85.0", license: "BSD-2-Clause",
    purl: "pkg:github/danbloomberg/leptonica@1.85.0", note: "Windows x64 build from the TesseractOCR NuGet package 5.5.2",
    compiledIn: [
      { name: "libjpeg-turbo", version: "2.0.6", license: "IJG AND BSD-3-Clause AND Zlib", purl: "pkg:github/libjpeg-turbo/libjpeg-turbo@2.0.6" },
      { name: "libpng", version: "1.6.37", license: "libpng-2.0", purl: "pkg:github/glennrp/libpng@1.6.37" },
      { name: "libtiff", version: "4.3.0", license: "libtiff", purl: "pkg:gitlab/libtiff/libtiff@4.3.0" },
      { name: "zlib", version: "1.2.11", license: "Zlib", purl: "pkg:github/madler/zlib@1.2.11" },
      { name: "giflib", version: "unknown", license: "MIT", purl: "pkg:sourceforge/giflib/giflib" },
    ],
  },
  {
    file: "app/worker/tessdata/eng.traineddata", name: "tessdata_fast eng.traineddata", version: "4.1.0", license: "Apache-2.0",
    purl: "pkg:github/tesseract-ocr/tessdata_fast@4.1.0#eng.traineddata", note: "English text recognition model",
  },
];

/** The NATIVE files in a package folder, with their SHA-256 hashes, and what was compiled into them. */
export function nativeComponents(folder, native = NATIVE) {
  const components = [];
  for (const n of native) {
    const path = join(folder, n.file);
    if (!existsSync(path)) throw new Error(`${n.file} is listed as shipped but is not in the package.`);
    const sha256 = createHash("sha256").update(readFileSync(path)).digest("hex");
    components.push({ name: n.name, version: n.version, purl: n.purl, sha256, license: n.license, program: "worker", note: n.note });
    for (const c of n.compiledIn ?? []) {
      components.push({ ...c, program: "worker", note: `Compiled into ${n.file.split("/").pop()}` });
    }
  }
  return components;
}

/**
 * DLLs in the bin folder of the PostgreSQL that DataScope-Setup installs (EDB's Windows build, in app/postgresql), by
 * file name. Two are LGPL: gettext's libintl and libiconv, which PostgreSQL uses for translated messages. The owner
 * allowed exactly these two (D169, 2026-10-24) because they ship unmodified as separate, replaceable files, with their
 * licence and where to get their source in THIRD-PARTY-NOTICES.md. No other LGPL component passes licenseProblems.
 */
export const POSTGRESQL_LIBRARIES = [
  { match: /^libintl-\d+\.dll$/i, license: "LGPL-2.1-or-later", lgplAllowed: true, note: "gettext libintl; source: https://ftp.gnu.org/pub/gnu/gettext/" },
  { match: /^libiconv-\d+\.dll$/i, license: "LGPL-2.1-or-later", lgplAllowed: true, note: "GNU libiconv; source: https://ftp.gnu.org/pub/gnu/libiconv/" },
  { match: /^lib(ssl|crypto)-3(-x64)?\.dll$/i, license: "Apache-2.0", note: "OpenSSL 3" },
  { match: /^icu(uc|in|dt|io|tu)\d+\.dll$/i, license: "Unicode-3.0", note: "ICU" },
  { match: /^libxml2\.dll$/i, license: "MIT", note: "libxml2" },
  { match: /^libxslt\.dll$/i, license: "MIT", note: "libxslt" },
  { match: /^zlib1?\.dll$/i, license: "Zlib", note: "zlib" },
  { match: /^liblz4\.dll$/i, license: "BSD-2-Clause", note: "LZ4" },
  { match: /^libzstd\.dll$/i, license: "BSD-3-Clause", note: "Zstandard" },
  { match: /^libcurl\.dll$/i, license: "curl", note: "curl" },
  { match: /^(libpq|libecpg|libecpg_compat|libpgtypes)\.dll$/i, license: "PostgreSQL", note: "PostgreSQL client library" },
  { match: /^(krb5_64|gssapi64|comerr64|k5sprt64|krbcc64|xpprof64|wshelp64)\.dll$/i, license: "MIT", note: "MIT Kerberos" },
  { match: /^libwinpthread-1\.dll$/i, license: "MIT", note: "mingw-w64 winpthreads" },
  { match: /^(vcruntime140(_1)?|msvcp140(_\d+)?|concrt140)\.dll$/i, license: "LicenseRef-Microsoft-Visual-CPP-Redistributable", note: "Microsoft Visual C++ runtime" },
];

const sha256Of = (path) => createHash("sha256").update(readFileSync(path)).digest("hex");

/** PostgreSQL, the DLLs it ships with, and the Visual C++ runtime installer, when the package bundles them (D169). */
export function postgresqlComponents(folder, libraries = POSTGRESQL_LIBRARIES) {
  const home = join(folder, "app", "postgresql");
  if (!existsSync(join(home, "bin", "postgres.exe"))) return [];
  const version = readFileSync(join(home, "VERSION"), "utf8").trim();
  const shipped = `As shipped in EDB's PostgreSQL ${version} build for Windows x64`;
  const components = [{
    name: "postgresql", version, purl: `pkg:generic/postgresql@${version}`, sha256: sha256Of(join(home, "bin", "postgres.exe")),
    license: "PostgreSQL", program: "database", note: "EDB's Windows x64 build: bin, lib and share",
  }];
  for (const file of readdirSync(join(home, "bin")).filter((f) => /\.dll$/i.test(f)).sort()) {
    const known = libraries.find((l) => l.match.test(file));
    components.push({
      name: file, version: "bundled", purl: `pkg:generic/postgresql-windows/${file.toLowerCase()}@${version}`,
      sha256: sha256Of(join(home, "bin", file)), license: known?.license ?? null, lgplAllowed: known?.lgplAllowed ?? false,
      program: "database", note: known ? `${known.note}. ${shipped}` : shipped,
    });
  }
  const redist = join(folder, "app", "vcredist", "vc_redist.x64.exe");
  if (existsSync(redist)) {
    components.push({
      name: "vc_redist.x64.exe", version: "latest", purl: "pkg:generic/microsoft/vc_redist.x64", sha256: sha256Of(redist),
      license: "LicenseRef-Microsoft-Visual-CPP-Redistributable", program: "database",
      note: "Microsoft Visual C++ Redistributable (2015-2022, x64), installed when missing: PostgreSQL and text recognition need it",
    });
  }
  return components;
}

/** npm packages bundled into the web UI: everything in the lock file not marked dev. */
export function webComponents(lock) {
  const components = [];
  for (const [path, entry] of Object.entries(lock.packages)) {
    if (path === "" || entry.dev || entry.devOptional || entry.link) continue;
    const name = entry.name ?? path.slice(path.lastIndexOf("node_modules/") + "node_modules/".length);
    components.push({
      name,
      version: entry.version,
      purl: `pkg:npm/${name.startsWith("@") ? "%40" + name.slice(1) : name}@${entry.version}`,
      hash: hexFromSri(entry.integrity),
      license: entry.license ?? null,
      program: "web",
    });
  }
  return components;
}

/** Merges per-program lists, recording every program that ships a component. */
export function merge(lists) {
  const byRef = new Map();
  for (const c of lists.flat()) {
    const existing = byRef.get(c.purl);
    if (existing) {
      if (!existing.programs.includes(c.program)) existing.programs.push(c.program);
    } else {
      byRef.set(c.purl, { ...c, programs: [c.program] });
    }
  }
  return [...byRef.values()].sort((a, b) => a.purl.localeCompare(b.purl));
}

export function bom(components, version, now = new Date(), serial = randomUUID()) {
  return {
    bomFormat: "CycloneDX",
    specVersion: "1.5",
    serialNumber: `urn:uuid:${serial}`,
    version: 1,
    metadata: {
      timestamp: now.toISOString(),
      component: { type: "application", name: "Data Scope", version, "bom-ref": "data-scope" },
    },
    components: components.map((c) => ({
      type: "library",
      "bom-ref": c.purl,
      name: c.name,
      version: c.version,
      purl: c.purl,
      ...(c.hash ? { hashes: [{ alg: "SHA-512", content: c.hash }] } : c.sha256 ? { hashes: [{ alg: "SHA-256", content: c.sha256 }] } : {}),
      ...(c.license ? { licenses: [{ expression: c.license }] } : {}),
      properties: [
        ...c.programs.map((p) => ({ name: "datascope:shipped-in", value: p })),
        ...(c.note ? [{ name: "datascope:note", value: c.note }] : []),
        ...(c.lgplAllowed ? [{ name: "datascope:licence-decision", value: "D169: unmodified, separately replaceable LGPL DLL of the bundled PostgreSQL" }] : []),
      ],
    })),
    dependencies: [{ ref: "data-scope", dependsOn: components.map((c) => c.purl) }],
  };
}

export function licenseProblems(components) {
  return {
    denied: components.filter((c) => c.license && DENIED.test(c.license) && !c.lgplAllowed),
    unknown: components.filter((c) => !c.license),
  };
}

function main() {
  const [folder, version] = process.argv.slice(2);
  if (!folder || !version) {
    console.error("Usage: node scripts/sbom.mjs <package folder> <version>");
    process.exit(2);
  }
  const root = resolve(import.meta.dirname, "..");
  const cache = process.env.NUGET_PACKAGES ?? join(homedir(), ".nuget", "packages");
  const lists = [];
  for (const program of ["api", "worker", "setup"]) {
    const dir = join(folder, "app", program);
    const deps = readdirSync(dir).find((f) => f.endsWith(".deps.json"));
    if (!deps) throw new Error(`No .deps.json in ${dir}`);
    lists.push(dotnetComponents(JSON.parse(readFileSync(join(dir, deps), "utf8")), program, cache));
  }
  lists.push(nativeComponents(folder));
  lists.push(postgresqlComponents(folder));
  lists.push(webComponents(JSON.parse(readFileSync(join(root, "web", "package-lock.json"), "utf8"))));
  const components = merge(lists);
  const { denied, unknown } = licenseProblems(components);
  const text = JSON.stringify(bom(components, version), null, 2) + "\n";
  writeFileSync(join(folder, "sbom.cdx.json"), text);
  console.log(`Wrote sbom.cdx.json: ${components.length} components (sha256 ${createHash("sha256").update(text).digest("hex").slice(0, 16)}...).`);
  for (const c of unknown) console.warn(`  License not identified: ${c.purl}. Check it by hand and add it to THIRD-PARTY-NOTICES.md.`);
  if (denied.length > 0) {
    for (const c of denied) console.error(`  Not allowed in the product: ${c.purl} (${c.license})`);
    process.exit(1);
  }
}

if (import.meta.url === `file://${process.argv[1]}`) main();
