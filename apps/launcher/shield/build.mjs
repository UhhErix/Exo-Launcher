#!/usr/bin/env node
// Builds Exo Shield (ExoLauncher/Assets/Extensions/exo-shield) from rules/ and pins its hash in Helpers/WebExtensions.cs.
//   node shield/build.mjs           build and re-pin
//   node shield/build.mjs --check   exit 1 if the shipped extension or its pin differs from what rules/ builds
import { createHash } from 'node:crypto';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

// Bump on every change to rules/ (year.monthday.n).
const VERSION = '2026.1119.1';
// The provider pages a rule may name in "from". A rule on any other site would be a general-purpose blocker.
export const PROVIDERS = ['music.youtube.com', 'open.spotify.com', 'music.apple.com'];
// What a build may ship: a few dozen rules, not a filter list.
export const MAX_RULES = 100;

const here = path.dirname(fileURLToPath(import.meta.url));
const launcher = path.resolve(here, '..');
const out = path.join(launcher, 'ExoLauncher', 'Assets', 'Extensions', 'exo-shield');
const pinFile = path.join(launcher, 'ExoLauncher', 'Helpers', 'WebExtensions.cs');
const rulesDir = path.join(here, 'rules');
const read = (name) => fs.readFileSync(path.join(rulesDir, name), 'utf8').replace(/\r\n/g, '\n');

export function buildRules(source) {
  const rules = [];
  for (const group of source.groups) {
    if (!group.why || !Array.isArray(group.from) || group.from.length === 0) throw new Error('A group needs "why" and "from".');
    for (const site of group.from) if (!PROVIDERS.includes(site)) throw new Error(`"${site}" is not a music provider page.`);
    const initiatorDomains = [...group.from].sort();
    if (group.domains?.length)
      rules.push({ condition: { requestDomains: [...group.domains].sort(), initiatorDomains } });
    for (const urlFilter of group.urls ?? []) rules.push({ condition: { urlFilter, initiatorDomains } });
  }
  return rules.map((rule, index) => ({ id: index + 1, priority: 1, action: { type: 'block' }, condition: rule.condition }));
}

export function files() {
  const rules = buildRules(JSON.parse(read('network.json')));
  if (rules.length > MAX_RULES) throw new Error(`${rules.length} rules; Exo Shield keeps to ${MAX_RULES}.`);
  const manifest = {
    manifest_version: 3,
    name: 'Exo Shield',
    short_name: 'Exo Shield',
    version: VERSION,
    description: "Exo's blocker for the music players: ads and trackers on YouTube Music, Spotify and Apple Music.",
    author: 'Exo',
    minimum_chrome_version: '122',
    permissions: ['declarativeNetRequest'],
    declarative_net_request: { rule_resources: [{ id: 'exo-shield', enabled: true, path: 'rules.json' }] },
    content_scripts: [
      { matches: ['https://music.youtube.com/*'], js: ['youtube-music.js'], world: 'MAIN', run_at: 'document_start' },
      { matches: ['https://music.youtube.com/*'], css: ['youtube-music.css'], run_at: 'document_start' },
      { matches: ['https://open.spotify.com/*'], css: ['spotify.css'], run_at: 'document_start' },
    ],
  };
  return {
    'manifest.json': JSON.stringify(manifest, null, 2) + '\n',
    'rules.json': JSON.stringify(rules, null, 1) + '\n',
    'youtube-music.js': read('youtube-music.js'),
    'youtube-music.css': read('youtube-music.css'),
    'spotify.css': read('spotify.css'),
  };
}

// WebExtensionManifest.ComputeManifestHash: sha256 over "relpath\nsha256(file)\n", ordinal by path.
export function manifestHash(contents) {
  const sha = (data) => createHash('sha256').update(data).digest('hex');
  const lines = Object.keys(contents).sort((a, b) => (a < b ? -1 : a > b ? 1 : 0))
    .map((name) => `${name}\n${sha(Buffer.from(contents[name], 'utf8'))}\n`).join('');
  return sha(Buffer.from(lines, 'utf8'));
}

const pinPattern = /(new\("exo-shield", "Exo Shield", ")[^"]*(", "exo-shield",\s*")[0-9a-f]{64}(")/;

function main() {
  const built = files();
  const hash = manifestHash(built);
  const cs = fs.readFileSync(pinFile, 'utf8');
  if (!pinPattern.test(cs)) throw new Error('Exo Shield\'s entry in WebExtensions.cs was not found.');
  const pinned = cs.replace(pinPattern, `$1${VERSION}$2${hash}$3`);
  if (process.argv.includes('--check')) {
    const shipped = fs.existsSync(out) ? fs.readdirSync(out).filter((name) => name !== '_metadata').sort() : [];
    const differ = [...new Set([...shipped, ...Object.keys(built)])].filter((name) =>
      !(name in built) || !fs.existsSync(path.join(out, name)) || fs.readFileSync(path.join(out, name), 'utf8') !== built[name]);
    if (pinned !== cs) differ.push('WebExtensions.cs pin');
    if (differ.length) { console.error('Exo Shield is out of date (run node shield/build.mjs): ' + differ.join(', ')); process.exit(1); }
    console.log(`Exo Shield ${VERSION} is up to date: ${JSON.parse(built['rules.json']).length} rules, hash ${hash}.`);
    return;
  }
  fs.rmSync(out, { recursive: true, force: true });
  fs.mkdirSync(out, { recursive: true });
  for (const [name, text] of Object.entries(built)) fs.writeFileSync(path.join(out, name), text);
  fs.writeFileSync(pinFile, pinned);
  console.log(`Exo Shield ${VERSION}: ${JSON.parse(built['rules.json']).length} rules, hash ${hash}.`);
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) main();
