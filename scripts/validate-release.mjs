import assert from "node:assert/strict";
import { readFile, stat } from "node:fs/promises";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const root = dirname(dirname(fileURLToPath(import.meta.url)));
const read = async (...parts) => readFile(join(root, ...parts), "utf8");

const packageJson = JSON.parse(await read("package.json"));
const packageLock = JSON.parse(await read("package-lock.json"));
const manifest = JSON.parse(await read("com.davedev.clef.sdPlugin", "manifest.json"));
const releaseVersion = manifest.Version;
const project = await read("helper", "ClefBridge", "ClefBridge.csproj");
const program = await read("helper", "ClefBridge", "Program.cs");
const pluginReadme = await read("com.davedev.clef.sdPlugin", "README.txt");
const publicReadme = await read("README.md");
const license = await read("LICENSE");
const notices = await read("THIRD_PARTY_NOTICES.md");
const helper = await stat(join(root, "com.davedev.clef.sdPlugin", "helper", "ClefBridge.exe"));
const repositoryUrl = "https://github.com/Soldrynn/StreamDeck-Clef";

assert.equal(packageJson.license, "Apache-2.0", "package license");
assert.equal(packageJson.repository?.url, `git+${repositoryUrl}.git`, "package repository URL");
assert.equal(packageJson.bugs?.url, `${repositoryUrl}/issues`, "package issue URL");
assert.equal(packageJson.homepage, `${repositoryUrl}#readme`, "package homepage URL");
assert.match(releaseVersion, new RegExp(`^${packageJson.version.replaceAll(".", "\\.")}\\.\\d+$`), "manifest version");
assert.match(project, new RegExp(`<Version>${releaseVersion.replaceAll(".", "\\.")}</Version>`), "helper project version");
assert.match(program, new RegExp(`version = "${releaseVersion.replaceAll(".", "\\.")}"`), "helper protocol version");
assert.match(pluginReadme, new RegExp(`^Clef ${releaseVersion}$`, "m"), "plugin README version");
assert.match(publicReadme, /^# Clef for Stream Deck \+$/m, "public README title");
assert.match(license, /^\s*Apache License\s*$/m, "Apache license text");
assert(helper.isFile() && helper.size > 1_000_000, "self-contained helper executable");

for (const name of ["LICENSE", "NOTICE", "THIRD_PARTY_NOTICES.md"]) {
  const source = await read(name);
  const packaged = await read("com.davedev.clef.sdPlugin", name);
  assert.equal(packaged, source, `${name} package copy`);
}

for (const name of ["@elgato/streamdeck", "@elgato/schemas", "@elgato/utils", "ws", "zod"]) {
  const dependency = packageLock.packages[`node_modules/${name}`];
  assert(dependency?.version, `${name} lockfile version`);
  const label = name === "zod" ? "Zod" : name;
  assert(
    notices.includes(`${label} ${dependency.version}`) || notices.includes(`\`${label}\` ${dependency.version}`),
    `${name} third-party notice`
  );
}

assert.equal(packageLock.packages[""].version, packageJson.version, "lockfile root version");
assert.equal(packageLock.packages[""].name, packageJson.name, "lockfile root name");

console.log(`Validated release metadata and licenses for ${releaseVersion}.`);
