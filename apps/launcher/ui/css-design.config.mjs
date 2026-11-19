// DESIGN.md's type scale for scripts/css-design.mjs (hover rules need no settings).
const scaled = (px) => `calc(${px}px * var(--exo-scale, 1))`

// exo/token-colour: the literal colours in the page's CSS on 2026-11-10, when the rule landed. Each is "file | selector |
// property: value" and waits for a token; the check fails on any literal not listed here and on an entry that no longer
// matches, so this list only shrinks. Do not add to it: name a token in src/tokens.css instead.
const COLOUR_BASELINE = [
  "src/styles/chrome.css | :is(.exo-app, .exo-game-overlay-presence, .exo-hung, .exo-portal) input[type=\"range\"]::-webkit-slider-thumb | box-shadow: 0 1px 3px rgb(0 0 0 / 0.35)",
  "src/styles/devices.css | .exo-device-art[style] | --exo-art-part: color-mix(in srgb, var(--exo-art-body), black 16%)",
  "src/styles/devices.css | .exo-device-art[style] | --exo-art-key: color-mix(in srgb, var(--exo-art-body), black 30%)",
  "src/styles/devices.css | .exo-device-art.is-dark | --exo-art-body: rgb(20 20 23)",
  "src/styles/devices.css | .exo-device-art.is-dark | --exo-art-part: rgb(38 39 44)",
  "src/styles/devices.css | .exo-device-art.is-dark | --exo-art-key: rgb(62 63 70)",
  "src/styles/discord-admin.css | .exo-dc-colorfield-sv > i, .exo-dc-colorfield-hue > i | box-shadow: 0 0 0 1px rgb(0 0 0 / 40%)",
  "src/styles/discord.css | .exo-dc-code | background: #141414",
  "src/styles/discord.css | .exo-dc-reaction.is-mine | background: #121a33",
  "src/styles/discord.css | .exo-dc-chat-head | box-shadow: 0 1px 0 rgb(0 0 0 / 0.4)",
  "src/styles/discord.css | .exo-dc-mention | background: rgb(88 101 242 / 0.3)",
  "src/styles/home.css | .exo-folder-plate | box-shadow: inset 0 1px 0 rgb(255 255 255 / 0.18)",
  "src/styles/home.css | .exo-folder-paper | box-shadow: 0 4px 14px rgb(0 0 0 / 0.45)",
  "src/styles/home.css | .exo-folder-tile .exo-folder-flap | box-shadow: inset 0 1px 0 rgb(255 255 255 / 0.22)",
  "src/styles/home.css | .exo-folder-light | background: radial-gradient(circle at var(--lx, 50%) var(--ly, 0%), rgb(255 255 255 / 0.22), transparent 62%)",
  "src/styles/onboarding.css | .exo-step-tile.is-selected | --color-muted: #3a3a3a",
  "src/styles/onboarding.css | .exo-step-tile.is-selected | --color-faint: #3a3a3a",
  "src/styles/onboarding.css | .exo-step-tile-icon | background: rgb(255 255 255 / 0.04)",
  "src/styles/progress.css | .exo-progress-fill | background: linear-gradient(90deg, color-mix(in srgb, var(--exo-progress-ink) 55%, transparent), var(--exo-progress-ink) calc(100% - 10px), color-mix(in srgb, var(--exo-progress-ink) 60%, white) 100%)",
  "src/styles/progress.css | .exo-progress-fill::after | background: linear-gradient(90deg, transparent 0%, rgb(255 255 255 / 0.55) 50%, transparent 100%)",
  "src/styles/store.css | .exo-store-hero-logo | filter: drop-shadow(0 4px 18px rgba(0, 0, 0, 0.6))",
  "src/styles/store.css | .exo-sgp-logo | filter: drop-shadow(0 4px 18px rgba(0, 0, 0, 0.6))",
  "src/styles/store.css | .exo-sgp-thumb.is-trailer .exo-glyph | filter: drop-shadow(0 1px 4px rgba(0, 0, 0, 0.8))",
  "src/styles/system.css | .switch .switch__thumb | box-shadow: 0 1px 3px rgb(0 0 0 / 0.35), 0 0 0 0.5px rgb(0 0 0 / 0.2)",
]

export default {
  // exo-design-system DECISIONS.md › Type: label 10, small 12, body and controls 13, read 15, title 20, page 28, first run 32.
  sizes: [20, 15, 13, 12, 10],
  // Exo's one slider (components/ui/Slider.tsx); tokens.css only adds its forced-colors fallback.
  sliderFiles: ['src/styles/chrome.css', 'src/tokens.css'],
  // Discord's Nitro name and profile effects keep Discord's own timings and curves (DESIGN.md › Motion › In the background).
  // A profile's look (frames, name styles, its backdrop and the sign-in page's parade of looks) is the person's own art.
  motionExempt: ['src/styles/discord-collectibles.css', 'src/styles/profile.css'],
  // exo/token-colour: colours are defined here and nowhere else.
  tokenFiles: ['src/tokens.css'],
  // <style> blocks and style="" attributes in the pages' HTML.
  htmlFiles: ['index.html', 'setup.html'],
  colourBaseline: COLOUR_BASELINE,
  // Each exception is one selector on one surface, at one exact value.
  exceptions: [
    { selector: ':root', value: scaled(16), why: 'The rem base Tailwind spacing is measured in, not a text size on a surface' },
    { selector: '.exo-game-title', value: scaled(28), why: 'Game sheet: the game title' },
    { selector: '.exo-music-header h2', value: scaled(28), why: 'Music: a playlist, album or artist page title beside its cover, as on the game sheet' },
    { selector: '.exo-music-immersive-text h2', value: scaled(28), why: "Music's Immersive: the song's title under its full-window cover, a page title as on a playlist's page" },
    { selector: '.exo-music-immersive .exo-music-lyrics', value: scaled(32), why: "Music's Immersive: the lyrics as the full window's subject, read from across the room" },
    { selector: '.exo-pc-about-name', value: scaled(28), why: "PC: this PC's name as the room's hero, a page title as on the game sheet" },
    { selector: ':root', value: scaled(28), why: 'The page title token (--exo-type-page)' },
    { selector: ':root', value: scaled(32), why: 'The first-run title token (--exo-type-first-run)' },
    { selector: '.exo-ob-step .exo-ob-title.is-display', value: scaled(56), why: 'First run (SPEC-v2): the welcome headline, the one display size' },
    { selector: '.exo-ob-welcome-lede', value: scaled(17), why: 'First run (SPEC-v2): the welcome line under the headline, 17 px muted' },
    { selector: '.exo-dc-picker-char', value: '30px', why: 'Discord emoji picker: a Unicode emoji drawn the 32px a server emoji\'s picture is, not text' },
    { selector: '.exo-dc-picker-foot .exo-dc-picker-char', value: '26px', why: 'Discord emoji picker footer: the emoji pointed at, the 28px of a picture there' },
    { selector: '.exo-cover-mono > span', value: 'clamp(14px, 16cqi, 30px)', why: 'Library card with no art: its title on a tinted tile, 14 to 30 px as the card allows' },
    { selector: '.exo-profile-hero-name', value: 'clamp(36px, 4.8vw, 68px)', why: 'Profile: the name as the room\'s hero, in its own style, sized to the window' },
    { selector: '.exo-profile-block-name', value: 'calc(100cqh * 0.72 * var(--name-fit, 1))', why: 'Profile: the name as a block the person placed, sized to fill its block and shrunk only to fit its width' },
    { selector: '.exo-profile-welcome-title', value: 'clamp(44px, 5.6vw, 80px)', why: 'Profile signed out: the showcase\'s title in the look on stage' },
    { selector: '.exo-profile-avatar-letter', value: 'calc(var(--profile-avatar) * 0.42)', why: 'Profile: the first letter standing in for a picture, sized to the picture' },
    { selector: '.exo-profile-opening-letter', value: 'calc(var(--profile-avatar) * 0.42)', why: 'Profile: the first letter in the opening, standing in for a picture, sized to the picture' },
    { selector: '.exo-profile-stat-value', value: scaled(28), why: 'Profile: a stat\'s figure, a page title\'s size as the PC room\'s name is' },
    { selector: '.exo-profile-tracker-value', value: scaled(28), why: 'Profile: a tracker\'s figure, as a stat\'s is' },
    { selector: '.exo-profile-block .exo-profile-stat-value', value: 'min(calc(28px * var(--exo-scale, 1)), 36cqh)', why: 'Profile: a stat\'s figure in a block, smaller in a short block so it stays whole' },
    { selector: '.exo-profile-block .exo-profile-stat-label', value: 'min(calc(13px * var(--exo-scale, 1)), 17cqh)', why: 'Profile: a stat\'s words in a block, smaller in a short block so they stay whole' },
    { selector: '.exo-profile-nametag.is-tile', value: 'min(calc(20px * var(--exo-scale, 1)), 14cqi, calc(88cqi / var(--n, 4)))', why: 'Profile Customize: a name\'s sample, sized to its tile by its letters so it is never clipped' },
    { selector: '.exo-profile-text.is-xl', value: scaled(28), why: 'Profile: a text block the person made huge, their words as art' },
    { selector: '.exo-profile-text > h3', value: '1.6em', why: 'Profile: a text block\'s heading, sized from the block\'s own size' },
    { selector: '.exo-profile-text > h4', value: '1.25em', why: 'Profile: a text block\'s smaller heading, from the block\'s own size' },
    { selector: '.exo-profile-text-art', value: '0.85em', why: 'Profile: ASCII and Unicode art drawn exactly, a little under the block\'s size' },
  ],
}
