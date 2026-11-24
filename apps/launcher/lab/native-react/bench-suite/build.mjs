import { execSync } from 'node:child_process';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import esbuild from 'esbuild';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const distDir = path.join(__dirname, 'dist');
fs.mkdirSync(distDir, { recursive: true });

console.log('[1/3] Compiling Tailwind v4 + @heroui/styles CSS...');
execSync(
  `npx @tailwindcss/cli -i src/styles.css -o dist/suite.css`,
  { cwd: __dirname, stdio: 'inherit' },
);
const suiteCss = fs.readFileSync(path.join(distDir, 'suite.css'), 'utf8');

const exoUiCssFiles = [
  '../../../../ui/src/tokens.css',
  '../../../../ui/src/styles/glass.css',
  '../../../../ui/src/styles/chrome.css',
  '../../../../ui/src/styles/settings.css',
];
let exoCss = suiteCss + '\n';
for (const rel of exoUiCssFiles) {
  const p = path.resolve(__dirname, rel);
  if (fs.existsSync(p)) {
    exoCss += `\n/* ${path.basename(p)} */\n` + fs.readFileSync(p, 'utf8');
  }
}
fs.writeFileSync(path.join(distDir, 'exo-settings.css'), exoCss, 'utf8');

const scenes = [
  { id: 'heroui-gallery', entry: 'src/scenes/heroui-gallery.tsx', css: 'suite.css' },
  { id: 'shadcn-radix', entry: 'src/scenes/shadcn-radix.tsx', css: 'suite.css' },
  { id: 'motion-gsap', entry: 'src/scenes/motion-gsap.tsx', css: 'suite.css' },
  { id: 'reactbits-ui', entry: 'src/scenes/reactbits-ui.tsx', css: 'suite.css' },
  { id: 'reactbits-bg', entry: 'src/scenes/reactbits-bg.tsx', css: 'suite.css' },
];

const motionAliasPlugin = {
  name: 'motion-alias',
  setup(build) {
    build.onResolve({ filter: /^motion\/react$/ }, () => ({
      path: path.resolve(__dirname, 'node_modules/framer-motion/dist/es/index.mjs'),
    }));
  },
};

console.log('[2/3] Bundling scenes for Chrome/WebView2 + Blitz-React (QJS/V8)...');
const manifest = [];
for (const scene of scenes) {
  const browserOut = path.join(distDir, `${scene.id}.browser.js`);
  const blitzOut = path.join(distDir, `${scene.id}.blitz.js`);

  await esbuild.build({
    entryPoints: [path.join(__dirname, scene.entry)],
    outfile: browserOut,
    bundle: true,
    format: 'iife',
    target: 'es2022',
    jsx: 'automatic',
    nodePaths: [path.join(__dirname, 'node_modules')],
    define: { 'process.env.NODE_ENV': '"production"' },
    plugins: [motionAliasPlugin],
    logLevel: 'warning',
  });

  await esbuild.build({
    entryPoints: [path.join(__dirname, scene.entry)],
    outfile: blitzOut,
    bundle: true,
    format: 'iife',
    target: 'es2022',
    jsx: 'automatic',
    nodePaths: [path.join(__dirname, 'node_modules')],
    inject: [path.join(__dirname, 'src/shim/blitzDomShim.ts')],
    define: { 'process.env.NODE_ENV': '"production"' },
    plugins: [motionAliasPlugin],
    logLevel: 'warning',
  });

  const html = `<!DOCTYPE html>
<html class="dark">
<head>
<meta charset="utf-8" />
<title>${scene.id}</title>
<link rel="stylesheet" href="./${scene.css}" />
</head>
<body class="dark bg-[#050508] text-white m-0 p-0">
<div id="root"></div>
<script src="./${scene.id}.browser.js"></script>
</body>
</html>`;
  fs.writeFileSync(path.join(distDir, `${scene.id}.html`), html, 'utf8');

  manifest.push({
    id: scene.id,
    css: path.join(distDir, scene.css),
    browserHtml: path.join(distDir, `${scene.id}.html`),
    blitzBundle: blitzOut,
    browserSizeKb: Math.round(fs.statSync(browserOut).size / 1024),
    blitzSizeKb: Math.round(fs.statSync(blitzOut).size / 1024),
  });
  console.log(`  built ${scene.id} (browser ${manifest.at(-1).browserSizeKb} KB, blitz ${manifest.at(-1).blitzSizeKb} KB)`);
}

fs.writeFileSync(path.join(distDir, 'manifest.json'), JSON.stringify(manifest, null, 2));
console.log('[3/3] Done.');
