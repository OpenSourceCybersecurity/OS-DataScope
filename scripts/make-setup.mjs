// Writes DataScope-Setup-<version>.exe: the setup wizard with the package zip appended (D169).
// Layout: [wizard][zip][zip length, 8 bytes little-endian]["DSSETUP1"]. The wizard finds the zip from the end of its own
// file (src/DataScope.Installer.Core/Payload.cs); Sign-DataScopePackage.ps1 writes the same layout for signed releases.
//
//   node scripts/make-setup.mjs <DataScope-Setup.exe> <DataScope-<version>.zip> <output.exe>
//
// Also writes <output.exe>.sha256.
import { createHash } from "node:crypto";
import { readFileSync, writeFileSync } from "node:fs";
import { basename } from "node:path";

export const MAGIC = Buffer.from("DSSETUP1", "ascii");

/** The setup program's bytes: the wizard, the zip, its length and the magic. */
export function setupBytes(wizard, zip) {
  const length = Buffer.alloc(8);
  length.writeBigInt64LE(BigInt(zip.length));
  return Buffer.concat([wizard, zip, length, MAGIC]);
}

function main() {
  const [wizard, zip, output] = process.argv.slice(2);
  if (!wizard || !zip || !output) {
    console.error("Usage: node scripts/make-setup.mjs <DataScope-Setup.exe> <package.zip> <output.exe>");
    process.exit(2);
  }
  const bytes = setupBytes(readFileSync(wizard), readFileSync(zip));
  writeFileSync(output, bytes);
  writeFileSync(`${output}.sha256`, `${createHash("sha256").update(bytes).digest("hex")}  ${basename(output)}\n`);
  console.log(`Wrote ${output} (${(bytes.length / 1048576).toFixed(1)} MB)`);
}

if (import.meta.url === `file://${process.argv[1]}`) main();
