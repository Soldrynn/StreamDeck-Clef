import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import sharp from "sharp";

const root = dirname(dirname(fileURLToPath(import.meta.url)));
const assets = join(root, "com.davedev.clef.sdPlugin", "assets");
const specifications = [
  ["plugin-icon.svg", 256, 256],
  ["plugin-icon@2x.svg", 512, 512],
  ["category.svg", 28, 28],
  ["category@2x.svg", 56, 56],
  ["actions/playback-list.svg", 20, 20],
  ["actions/playback-list@2x.svg", 40, 40],
  ["actions/volume-list.svg", 20, 20],
  ["actions/volume-list@2x.svg", 40, 40],
  ["actions/playback.svg", 72, 72],
  ["actions/playback@2x.svg", 144, 144],
  ["actions/volume.svg", 72, 72],
  ["actions/volume@2x.svg", 144, 144],
  ["placeholders/album.svg", 108, 108],
  ["placeholders/volume.svg", 108, 108],
  ["backgrounds/glass.svg", 200, 100],
  ["backgrounds/glass@2x.svg", 400, 200]
];

for (const [relativePath, width, height] of specifications) {
  const path = join(assets, relativePath);
  const metadata = await sharp(path).metadata();
  assert.equal(metadata.width, width, `${relativePath} width`);
  assert.equal(metadata.height, height, `${relativePath} height`);
  await sharp(path).resize(width, height).png().toBuffer();
}

for (const relativePath of [
  "category.svg",
  "category@2x.svg",
  "actions/playback-list.svg",
  "actions/playback-list@2x.svg",
  "actions/volume-list.svg",
  "actions/volume-list@2x.svg"
]) {
  const source = await readFile(join(assets, relativePath), "utf8");
  const colors = source.match(/#[0-9a-f]{6}/gi) ?? [];
  assert(colors.length > 0, `${relativePath} has a foreground`);
  assert(colors.every(color => color.toUpperCase() === "#FFFFFF"), `${relativePath} is monochrome white`);
}

for (const relativePath of ["backgrounds/glass.svg", "backgrounds/glass@2x.svg"]) {
  const source = await readFile(join(assets, relativePath), "utf8");
  assert.match(source, /fill-opacity="0"/, `${relativePath} remains transparent`);
}

console.log(`Validated and rendered ${specifications.length} SVG assets.`);
