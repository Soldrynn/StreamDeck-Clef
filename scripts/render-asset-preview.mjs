import { mkdir } from "node:fs/promises";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import sharp from "sharp";

const root = dirname(dirname(fileURLToPath(import.meta.url)));
const assets = join(root, "com.davedev.clef.sdPlugin", "assets");
const output = join(root, "docs", "asset-preview.png");

const items = [
  { label: "Plugin", file: "plugin-icon.png", x: 46, y: 73, size: 160 },
  { label: "Playback", file: "actions/playback.svg", x: 258, y: 86, size: 144 },
  { label: "Volume", file: "actions/volume.svg", x: 439, y: 86, size: 144 },
  { label: "No artwork", file: "placeholders/album.svg", x: 632, y: 104, size: 108 },
  { label: "Volume strip", file: "placeholders/volume.svg", x: 771, y: 104, size: 108 },
  { label: "Category", file: "category@2x.svg", x: 74, y: 302, size: 56 },
  { label: "Playback list", file: "actions/playback-list@2x.svg", x: 276, y: 310, size: 40 },
  { label: "Volume list", file: "actions/volume-list@2x.svg", x: 462, y: 310, size: 40 }
];

const labels = items.map(item =>
  `<text x="${item.x + item.size / 2}" y="${item.y - 18}" text-anchor="middle" fill="#C8CBCF" font-family="Segoe UI, sans-serif" font-size="13" font-weight="600">${item.label}</text>`
).join("");

const backdrop = Buffer.from(`<svg xmlns="http://www.w3.org/2000/svg" width="920" height="390">
  <defs>
    <linearGradient id="bg" x1="0" y1="0" x2="920" y2="390" gradientUnits="userSpaceOnUse">
      <stop stop-color="#1B1D20"/><stop offset="1" stop-color="#0D0E10"/>
    </linearGradient>
  </defs>
  <rect width="920" height="390" rx="24" fill="url(#bg)"/>
  <text x="36" y="37" fill="#FFFFFF" font-family="Segoe UI, sans-serif" font-size="18" font-weight="700">Clef — precision glass assets</text>
  <path d="M36 51h848" stroke="#FFFFFF" stroke-opacity=".1"/>
  ${labels}
</svg>`);

const composites = [{ input: backdrop, left: 0, top: 0 }];
for (const item of items) {
  const input = await sharp(join(assets, item.file)).resize(item.size, item.size).png().toBuffer();
  composites.push({ input, left: item.x, top: item.y });
}

await mkdir(dirname(output), { recursive: true });
await sharp({ create: { width: 920, height: 390, channels: 4, background: "#00000000" } })
  .composite(composites)
  .png()
  .toFile(output);

console.log(output);
