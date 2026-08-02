import { build } from "esbuild";
import { copyFile, mkdir } from "node:fs/promises";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import sharp from "sharp";

const root = dirname(dirname(fileURLToPath(import.meta.url)));
const pluginDirectory = join(root, "com.davedev.clef.sdPlugin");
const outputDirectory = join(pluginDirectory, "bin");
await mkdir(outputDirectory, { recursive: true });

for (const name of ["LICENSE", "NOTICE", "THIRD_PARTY_NOTICES.md"]) {
  await copyFile(join(root, name), join(pluginDirectory, name));
}

const iconSource = join(pluginDirectory, "assets", "plugin-icon.svg");
const iconDirectory = dirname(iconSource);
await sharp(iconSource).resize(256, 256).png().toFile(join(iconDirectory, "plugin-icon.png"));
await sharp(iconSource).resize(512, 512).png().toFile(join(iconDirectory, "plugin-icon@2x.png"));

await build({
  absWorkingDir: root,
  entryPoints: ["./src/plugin.ts"],
  outfile: "com.davedev.clef.sdPlugin/bin/plugin.js",
  bundle: true,
  format: "esm",
  platform: "node",
  target: "node24",
  banner: {
    js: "import { createRequire as __nodeCreateRequire } from 'node:module'; const require = __nodeCreateRequire(import.meta.url);"
  },
  sourcemap: true,
  minify: false,
  legalComments: "none",
  tsconfig: "tsconfig.json"
});
