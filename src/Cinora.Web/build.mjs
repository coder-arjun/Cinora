// Cinora frontend build: Tailwind v4 (CSS) + esbuild (TS bundle incl. Alpine + HTMX) → wwwroot/dist,
// plus the service worker (Scripts/sw.ts → wwwroot/sw.js at the web root, Milestone 6.1 / ADR 0019).
//
//   node build.mjs            production: minified + content-hashed filenames + manifest.json
//   node build.mjs --watch    dev: non-minified, sourcemaps, fixed names, watch mode
//
// Content hashing gives cache-busting URLs; a manifest.json maps logical -> hashed names, resolved
// in the layout by IAssetManifest. See docs/architecture/solution-structure.md §5.
//
// The service worker is a SECOND esbuild entry, built AFTER the main bundle (so manifest.json exists). Two
// esbuild `define`s are injected from that manifest: __SW_VERSION__ (a short SHA-256 of the manifest JSON —
// any asset-hash change rotates the SW version) and __PRECACHE__ (the exact hashed asset URLs to precache).
// sw.js is emitted at the web ROOT (scope "/") with a STABLE, non-hashed name (byte-compare update check).
import { createRequire } from "node:module";
import { execFileSync, spawn } from "node:child_process";
import { createHash } from "node:crypto";
import { mkdirSync, readFileSync, renameSync, rmSync, writeFileSync } from "node:fs";
import { basename, dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import * as esbuild from "esbuild";

const require = createRequire(import.meta.url);
const root = dirname(fileURLToPath(import.meta.url));
const watch = process.argv.includes("--watch");

const cssEntry = join(root, "Styles", "app.css");
const jsEntry = join(root, "Scripts", "site.ts");
const swEntry = join(root, "Scripts", "sw.ts");
const outDir = join(root, "wwwroot", "dist");
const swOutFile = join(root, "wwwroot", "sw.js");
const manifestPath = join(outDir, "manifest.json");

// Same-origin, stable-URL assets the service worker precaches alongside the hashed CSS/JS (icons are
// referenced by manifest.webmanifest; they are precached so installability + the app-shell work offline).
const precacheIcons = [
  "/icons/icon-192.png",
  "/icons/icon-512.png",
  "/icons/icon-maskable-512.png",
];

// Resolve the Tailwind v4 CLI's JS entry so it runs cross-platform via `node <entry>`.
const twPkgPath = require.resolve("@tailwindcss/cli/package.json");
const twPkg = require("@tailwindcss/cli/package.json");
const twBinRel = typeof twPkg.bin === "string" ? twPkg.bin : twPkg.bin.tailwindcss;
const twBin = join(dirname(twPkgPath), twBinRel);

/** First 10 hex chars of the content's SHA-256 — enough for a cache-busting fingerprint. */
function shortHash(buffer) {
  return createHash("sha256").update(buffer).digest("hex").slice(0, 10);
}

function resetOutDir() {
  rmSync(outDir, { recursive: true, force: true });
  mkdirSync(outDir, { recursive: true });
}

function writeManifest(manifest) {
  writeFileSync(manifestPath, `${JSON.stringify(manifest, null, 2)}\n`);
}

/**
 * Build the esbuild `define` map for the service worker from the just-written manifest: a VERSION keyed to
 * the manifest hash (so a bundle change rotates it) and the exact precache URL list.
 */
function serviceWorkerDefines(manifest) {
  const precache = [manifest["app.css"], manifest["site.js"], ...precacheIcons];
  // VERSION = SHA-256 of the manifest JSON PLUS the raw bytes of every precached icon, so an icon-only
  // change (which leaves the hashed app.css/site.js names untouched) STILL rotates the SW version → the old
  // icon is purged from the static cache on activate. Kept a short hex slice (a cache-busting fingerprint).
  const hash = createHash("sha256").update(JSON.stringify(manifest));
  for (const iconUrl of precacheIcons) {
    hash.update(readFileSync(join(root, "wwwroot", iconUrl.replace(/^\//, ""))));
  }
  const version = hash.digest("hex").slice(0, 12);
  return {
    __SW_VERSION__: JSON.stringify(version),
    __PRECACHE__: JSON.stringify(precache),
  };
}

const esbuildShared = {
  entryPoints: [jsEntry],
  bundle: true,
  format: "esm",
  target: ["es2022"],
  platform: "browser",
  logLevel: "info",
};

// The service worker is a classic (non-module) worker registered as `register("/sw.js")`, so it bundles to
// an IIFE. It imports nothing, so the bundle is self-contained; the two constants are injected via `define`.
function serviceWorkerBuildOptions(manifest, { minify, sourcemap }) {
  return {
    entryPoints: [swEntry],
    bundle: true,
    format: "iife",
    target: ["es2022"],
    platform: "browser",
    outfile: swOutFile,
    minify,
    sourcemap,
    legalComments: "none",
    define: serviceWorkerDefines(manifest),
    logLevel: "info",
  };
}

async function buildProduction() {
  resetOutDir();

  // 1. CSS — Tailwind CLI, minified, to a temp file, then content-hash + rename.
  const tmpCss = join(outDir, "app.tmp.css");
  execFileSync(
    process.execPath,
    [twBin, "--input", cssEntry, "--output", tmpCss, "--minify"],
    { stdio: "inherit" },
  );
  const cssBytes = readFileSync(tmpCss);
  const cssName = `app-${shortHash(cssBytes)}.css`;
  renameSync(tmpCss, join(outDir, cssName));

  // 2. JS — esbuild bundle (Alpine + HTMX + site.ts), minified, content-hashed by esbuild itself. Code splitting
  //    is ON (esm + outdir) so the dynamic import("@microsoft/signalr") in site.ts (Milestone 6.4 §6.5) is emitted
  //    as a SEPARATE content-hashed chunk under /dist, lazy-loaded only on authed pages — it no longer rides the
  //    shared site-*.js on every page. The layout already loads site.js as type="module", so a split entry is fine.
  const result = await esbuild.build({
    ...esbuildShared,
    minify: true,
    legalComments: "none",
    outdir: outDir,
    entryNames: "[name]-[hash]",
    chunkNames: "[name]-[hash]",
    splitting: true,
    metafile: true,
  });
  // Pick the ENTRY output (it alone carries an entryPoint in the metafile); split chunks have none, so a plain
  // "first .js" match could return the signalr chunk instead of the site bundle.
  const jsOut = Object.entries(result.metafile.outputs).find(
    ([file, meta]) => file.endsWith(".js") && meta.entryPoint !== undefined,
  )?.[0];
  if (!jsOut) {
    throw new Error("esbuild did not emit a JS entry output file.");
  }
  const jsName = basename(jsOut);

  const manifest = { "app.css": `/dist/${cssName}`, "site.js": `/dist/${jsName}` };
  writeManifest(manifest);

  // 3. Service worker — built LAST, keyed to the manifest just written (VERSION rotates with the bundle).
  await esbuild.build(serviceWorkerBuildOptions(manifest, { minify: true, sourcemap: false }));

  console.log(`Cinora assets built: dist/${cssName}, dist/${jsName}, sw.js`);
}

async function watchDev() {
  resetOutDir();

  const cssOut = join(outDir, "app.css");
  const jsOut = join(outDir, "site.js");
  // Fixed names in dev — the manifest points straight at them so the layout resolves either mode.
  const manifest = { "app.css": "/dist/app.css", "site.js": "/dist/site.js" };
  writeManifest(manifest);

  const tailwind = spawn(
    process.execPath,
    [twBin, "--input", cssEntry, "--output", cssOut, "--watch"],
    { stdio: "inherit" },
  );

  const context = await esbuild.context({
    ...esbuildShared,
    minify: false,
    sourcemap: true,
    outfile: jsOut,
  });
  await context.watch();

  // The service worker watches too — its precache list is the fixed-name manifest, so VERSION is stable in
  // dev (no update-toast churn), and editing sw.ts rebuilds wwwroot/sw.js.
  const swContext = await esbuild.context(
    serviceWorkerBuildOptions(manifest, { minify: false, sourcemap: true }),
  );
  await swContext.watch();

  console.log("Watching Styles/ + Scripts/ … (Ctrl+C to stop)");

  const shutdown = () => {
    tailwind.kill();
    void context.dispose();
    void swContext.dispose();
    process.exit(0);
  };
  process.on("SIGINT", shutdown);
  process.on("SIGTERM", shutdown);
}

if (watch) {
  await watchDev();
} else {
  await buildProduction();
}
