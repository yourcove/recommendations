#!/usr/bin/env node
// Validates that extensions/catalog.json and each extension's extension.json stay consistent:
//  • every catalog entry points to a real directory with an extension.json
//  • catalog id matches the manifest id, and the entryDll matches the project assembly name
//  • ids and tagPrefixes are unique
//  • each manifest's minCoveVersion is >= the repo's CoveMinVersion (from Directory.Build.props)
import * as fs from "node:fs";
import * as path from "node:path";
import { fileURLToPath } from "node:url";

const rootDir = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const errors = [];
const fail = (msg) => errors.push(msg);

function readJson(file) {
  return JSON.parse(fs.readFileSync(file, "utf8"));
}

function parseSemver(v) {
  const m = /^(\d+)\.(\d+)\.(\d+)/.exec(String(v || "").trim());
  return m ? [Number(m[1]), Number(m[2]), Number(m[3])] : null;
}

function gte(a, b) {
  for (let i = 0; i < 3; i += 1) {
    if (a[i] > b[i]) return true;
    if (a[i] < b[i]) return false;
  }
  return true;
}

// Repo's CoveMinVersion from Directory.Build.props.
let coveMin = null;
try {
  const props = fs.readFileSync(path.join(rootDir, "Directory.Build.props"), "utf8");
  const m = /<CoveMinVersion[^>]*>([^<]+)</.exec(props);
  if (m) coveMin = parseSemver(m[1]);
} catch { /* optional */ }

const catalogPath = path.join(rootDir, "extensions", "catalog.json");
if (!fs.existsSync(catalogPath)) {
  console.error("ERROR: extensions/catalog.json not found.");
  process.exit(1);
}

const catalog = readJson(catalogPath);
if (!Array.isArray(catalog.extensions) || catalog.extensions.length === 0) {
  fail("catalog.json has no extensions.");
}

const seenIds = new Set();
const seenPrefixes = new Set();

for (const entry of catalog.extensions || []) {
  const label = entry.name || entry.id || "(unnamed)";

  if (!entry.id) fail(`${label}: missing id.`);
  if (!entry.path) fail(`${label}: missing path.`);
  if (!entry.tagPrefix) fail(`${label}: missing tagPrefix.`);
  if (entry.tagPrefix && !entry.tagPrefix.endsWith("/")) fail(`${label}: tagPrefix '${entry.tagPrefix}' must end with '/'.`);

  if (entry.id && seenIds.has(entry.id)) fail(`Duplicate id '${entry.id}'.`);
  seenIds.add(entry.id);
  if (entry.tagPrefix && seenPrefixes.has(entry.tagPrefix)) fail(`Duplicate tagPrefix '${entry.tagPrefix}'.`);
  seenPrefixes.add(entry.tagPrefix);

  const extDir = path.join(rootDir, entry.path || "");
  const manifestPath = path.join(extDir, "extension.json");
  if (!fs.existsSync(manifestPath)) {
    fail(`${label}: extension.json not found at ${entry.path}.`);
    continue;
  }

  const manifest = readJson(manifestPath);
  if (manifest.id !== entry.id) fail(`${label}: manifest id '${manifest.id}' != catalog id '${entry.id}'.`);

  if (!entry.manifestOnly) {
    if (!manifest.entryDll) fail(`${label}: manifest missing entryDll.`);
    const csproj = path.join(extDir, `${entry.name}.csproj`);
    if (!fs.existsSync(csproj)) fail(`${label}: project ${entry.name}.csproj not found.`);
  }

  const min = parseSemver(manifest.minCoveVersion);
  if (!min) {
    fail(`${label}: invalid or missing minCoveVersion '${manifest.minCoveVersion}'.`);
  } else if (coveMin && !gte(min, coveMin)) {
    fail(`${label}: minCoveVersion ${manifest.minCoveVersion} is below repo CoveMinVersion ${coveMin.join(".")}.`);
  }
}

if (errors.length > 0) {
  console.error("Extension repo validation failed:");
  for (const e of errors) console.error(`  - ${e}`);
  process.exit(1);
}

console.log(`Validated ${catalog.extensions.length} extension(s). OK.`);
