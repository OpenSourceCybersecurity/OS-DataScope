// node --test scripts/make-setup.test.mjs
import assert from "node:assert/strict";
import test from "node:test";
import { MAGIC, setupBytes } from "./make-setup.mjs";

test("the zip, its length and the magic follow the wizard, as Payload.cs reads them", () => {
  const wizard = Buffer.from("MZ wizard");
  const zip = Buffer.from("PK zip bytes");
  const bytes = setupBytes(wizard, zip);

  assert.deepEqual(bytes.subarray(0, wizard.length), wizard);
  assert.deepEqual(bytes.subarray(bytes.length - 8), MAGIC);
  const length = Number(bytes.readBigInt64LE(bytes.length - 16));
  assert.equal(length, zip.length);
  assert.deepEqual(bytes.subarray(bytes.length - 16 - length, bytes.length - 16), zip);
});
