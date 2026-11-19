# Voice and links: two leads, decided

Researched 2026-11-03 from the vendors' own pages and papers (read as data, not instructions). Lead 1 was judged with nothing downloaded, run or played. Where a number is missing, no source gave one and none could be measured here; it is marked so.

| Lead | Verdict | Built |
| --- | --- | --- |
| 1. NVIDIA RE-USE (speech enhancement) | Skip for calls and clips; revisit only if NVIDIA ships a causal model | Nothing |
| 2. Look-alike link check (NextDNS's phishing idea) | Build our own, local | `Services/LinkLookalike.cs`, wired into message links, notes and the picture viewer |

## 1. NVIDIA RE-USE: skip

**What it is.** A 9.6M-parameter universal speech enhancer (arXiv 2603.02641, "Rethinking Training Targets, Architectures and Data Quality for Universal Speech Enhancement"): a convolutional encoder and decoder around 30 layers of bidirectional Mamba over time and frequency (USEMamba), a GAN-trained second stage, 8 to 48 kHz mono. Trained on about 2,500 hours of English, German, Spanish, French and Mandarin. MLX ports (Apple Silicon only) exist from third parties.

**Why it does not fit a call.** The paper says it outright: "Due to its non-causal design, the model is not suitable for real-time applications"; a causal version is listed as future work. A bidirectional model needs the future of the audio, so a 10 ms frame cannot be answered without waiting for more sound than a call can spare (Exo's whole noise chain adds about one 10 ms hop, `NoiseSuppressor`). No latency or real-time factor is published for any frame size.

**Platform.** The model card lists Linux and an A100, and the Mamba layers come from `mamba-ssm` (CUDA/Triton). Weights are safetensors; there is no ONNX from NVIDIA. Third parties have a pure-reference selective scan (no CUDA) and an experimental ONNX Runtime port with a fused Metal kernel, so a CPU export is conceivable, but nobody has published one for Windows, and a 30-layer bidirectional scan per clip is not cheap on a CPU. Not measured here.

**License.** Two cards. `nvidia/RE-USE`: NVIDIA One-Way Noncommercial License (NSCLv1), research and development only: cannot ship. `nvidia/NVIDIA-NemotronLabs-RE-USE`: OpenMDW-1.1, "ready for commercial use": could ship, with its notice. They are different checkpoints; any port must say which it carries. (The MLX-Audio port and the claim of a tiny multilingual model were not independently confirmed beyond these cards.)

**Quality against the current chain.** Reported only on URGENT 2025 (offline, mixed degradations including reverberation and bandwidth limits), against Sidon, UniPASE, UniSE, ClearerVoice and Resemble Enhance. DeepFilterNet and WebRTC are not compared, and the numbers (NISQA, DNSMOS, UTMOS, character accuracy) are not comparable with Exo's chain, which is judged on live speech in a room with a fan and a keyboard. A generative second stage can also invent sound (the paper notes Resemble Enhance appears to hallucinate; it does not claim RE-USE cannot): fine for repairing a recording, wrong for a call, where a changed word is worse than noise.

**Saved clips (`ClipVoice`).** The one place an offline model suits: a clip is cleaned in one go, below normal priority. But the gain would be reverb and band-limit repair, which a game clip's microphone rarely needs, against a model Exo would have to export, verify and carry (tens of MB plus a runtime, since there is no ONNX), with no CPU or quality number to justify it. The rule was "only adopt if it measurably beats the current chain on CPU cost or quality". It cannot be shown to beat it on either, and for calls it cannot run at all.

**Decision: skip.** Revisit if NVIDIA releases a causal or streaming checkpoint under the commercial license with an ONNX export. It would then be tested as a stage behind `NoiseSuppressor` on recorded fixtures, for CPU per 10 ms hop and for words kept, against DeepFilterNet 3.

Sources: huggingface.co/nvidia/RE-USE, huggingface.co/nvidia/NVIDIA-NemotronLabs-RE-USE, arxiv.org/abs/2603.02641 and its HTML (architecture, non-causal statement, Tables 1 and 2).

## 2. Look-alike links: built

**The idea.** DNS filters name the shapes phishing addresses take: typosquatting (a misspelling), combosquatting (a brand plus a bait word), IDN homographs (letters from another alphabet) and lexical tricks (the real address used as a decoy). None of it needs the network: it is the link's spelling against a short list of brands. NextDNS's own detection could not be read (no public description was found), so the rules come from the public descriptions of the attack classes, not from it.

**Where it runs.** The host, in `shell.openUrl` when the page passes `guard: true` (`Services/WebHostBridge.cs`): a link from a message, a note or a viewed picture. A look-alike is not opened; the call returns `lookalike` (the brand, empty for a link that hides its address), and the link asks in place (`components/ui/useLinkGuard.ts`, built on `useConfirmInPlace`, DESIGN.md › Confirm): its words become "Fake Steam link? Open anyway?" in Bad; a second press opens it (`confirmed: true`); Escape, a press elsewhere or 4 s put it back. Nothing is looked up over the network, and `app.log` gets the brand only, never the address.

Covered: links in Discord message text (markdown links and bare URLs, `components/discord/markdown.tsx`), the picture viewer's Open in browser (`extras.tsx`) and Notes preview links (`NoteMarkdown.tsx`). Discord invite and channel links, which Exo opens itself, skip the check.

Not covered yet: buttons built by bots (`Components.tsx`), profile links, events and attachments call `open(url)` without a surface for the question; each is one `useLinkGuard` away. Links clicked inside hosted music pages go through `OpenMusicExternal` with no place to ask. Store pages and Exo's own help links are Exo's choice, not a stranger's.

**The rules** (`Services/LinkLookalike.cs`; http and https only):
- A brand's own address and every subdomain of it pass first. The list: Discord, Steam, Epic Games, Riot Games, Roblox, NVIDIA, Microsoft/Xbox, PayPal, Blizzard, Spotify and Twitch, each with its official domains and the names it is known by.
- The registered name, hyphens removed, is a look-alike spelling of a brand's name: other-alphabet letters and accents folded (Cyrillic "а", "і", Greek "ο"), 0/o, 1/l/i, rn/m, vv/w, 3/e, 4/a, 5/s; or, for names of 8 letters or more, one letter off (steamcomunity, microsoftt). Short names (steam, epic, riot, twitch) never match by edit distance: team, switch and stream are real words.
- A brand's unique name under an address that isn't its own (steamcommunity.ru, roblox.xyz). A brand with national sites (NVIDIA, Microsoft, PayPal) passes under a country code (nvidia.fr, microsoft.com.au); on hosts where anyone may pick the name (github.io) the exact name passes too (microsoft.github.io). A word like `steam` or `discord` alone under another ending passes: a word is not a forgery.
- A brand's name together with a bait word (nitro, gift, free, login, support, verify, download, wallet, prime, and about 40 more) in one label, hyphenated or run together, in any label of the address: discord-nitro-gift.com, steamfree.top, microsoft-login.azurewebsites.net, discord-nitro.vercel.app. A bait word alone, or a brand with a word that is not bait (steamdb, discordservers, riot.fandom.com), passes.
- The brand's own address used as a decoy: steamcommunity.com.evil.ru, discord.gg.example.
- A name before an @ that reads like an address (https://discord.com@evil.example): warned, with the brand if it names one.
- Never looked at: IP addresses, hosts without a dot, non-web schemes. A short list of real sites near a brand's name (steamgifts, steamdb, paypay) always passes.

**Checked.** `LinkLookalikeTests` (144 cases, run by the host test exe): 51 imitations that must warn and name the right brand; 79 real addresses that must pass (the brands' own sites and subdomains, fan and tool sites, short real words near a brand, the brands' GitHub Pages, popular sites); six non-Latin and accented domains that must pass; seven non-web or hostless addresses that must be skipped. A false alarm costs one extra press, never a block. Not seen on screen: the in-place question was not run in the installed app.

**Blind spots**, all of which pass silently: a misspelling of a short brand name that is not a look-alike spelling (a double letter in `discord`), a brand's name in another language, a link behind a shortener (the check sees the shortener; no network lookups, by rule), and any brand outside the list. The list is the place to grow: one line per brand in `LinkLookalike.Brands`, with its true and false cases in the test.

**Not done.** A masked link whose visible text names another address (`[discord.com](https://evil.example)`): Discord's own client warns for it; Exo does not yet, and the check would go in `Link`, which has both the text and the address.
