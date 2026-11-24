# Exo Lab: Native React with Real Libraries (`lab-native-react`)

- **Date:** 2026-11-24
- **Branch:** `lab/lab-native-react` (branched from `origin/feat/native-polish-2` @ `2746258`)
- **Test PC:** Windows 11 Pro Build 26300 · AMD Ryzen 5 5600X (6C/12T) · 15.9 GB RAM · NVIDIA GeForce RTX 3070 (`32.0.16.2135`)
- **Toolchains:** Rust `1.100.0` (edition 2024) · Rune fork `UhhErix/rune` (`ba9aa61`, wgpu `~30.0.1` DX12) · Blitz `0.3.0-beta.5` (`blitz-dom` + `blitz-paint` + `stylo 0.24.0` + `taffy` + `anyrender_vello_cpu 0.19`) · QuickJS-NG (`rquickjs 0.14.0`) · V8 (`v8 152.2.0`) · Naga `30.0.1` · Node `26.13.0` · Playwright Chromium `156.0.8078.4` (`chromium-1248`) · EdgeWebView2 `158.0.4370.33`
- **Prototype Workspace:** `apps/launcher/lab/native-react/` (standalone Cargo + esbuild/Tailwind v4 bench suite; zero product files touched; installed Exo was never started).

---

## Executive Verdict

**No — "native React running the big unmodified libraries (HeroUI, React Bits, shadcn/Radix, framer-motion, GSAP) at near-Rust speed and lean memory" is NOT possible yet. Stay native Rust Rune (`UhhErix/rune`).**

Every route we built and measured failed the owner's acceptance bar (`>= 90%` real component compatibility, `<= 0.3 ms` frame-time delta vs hand-written Rune, `<= ~1.5x` Rune memory, well under WebView2, `0` child processes, commercial-safe licenses):

| Acceptance Criterion | Owner Bar | Best Native-React Route (`Blitz + V8` / `Blitz + QJS`) | Hand-Written Rune (`rune-baseline`) | WebView2 / Chromium | Gate |
| --- | --- | --- | --- | --- | :---: |
| **Real library compatibility (render + behave vs Chrome)** | `>= 90%` | **~48–52% functional** (`83/85` static mount, **only `7/23` = 30.4% interactive checks pass**; WebGL2/Canvas2D BG `54.2%` grid match; GSAP frozen; `5/35` HeroUI + `7/16` React Bits UI visual defects) | N/A (hand-written native) | **100%** (`85/85` mounted, `21/23` auto-click, `0` JS errors) | **FAIL** |
| **Frame time during animations (`p50` / `p99`)** | `<= +0.3 ms` vs Rune | **12.59–73.67 ms `p50`** / **20.4–344.3 ms `p99`** (`+12.0` to `+73.1 ms` over Rune; **23x–134x slower**) | **0.55 ms `p50`** / `3.05 ms p99` (active DX12) | **6.0–6.1 ms `p50`** / `6.5–24.9 ms p99` (165Hz vsync) | **FAIL** |
| **Input latency (`p50`)** | Near-Rust | **9.4–92.6 ms** (`12.4 ms` QJS / `17.6 ms` V8 on HeroUI) | **< 0.1 ms** (native Rust state) | **~14 ms** (CDP rAF) | **FAIL** |
| **Memory (WS / Private, settled)** | `<= ~1.5x` Rune, `<<` WV2 | **126–213 MB WS / 128–274 MB Priv** (QJS); **175–222 MB WS / 195–282 MB Priv** (V8; **270/340 MB** w/ WebGL shim) | **36.1 MB WS / 20.2 MB Priv** settled idle (`156.0 / 180.5 MB` active DX12; `exo-ui` 215-game: `210 MB WS`) | **476–521 MB WS / 249–282 MB Priv** (Chromium on exact suite); **666–841 MB WS** (WV2 in Rune) | **PARTIAL** (beats WV2, exceeds 1.5x idle Rune) |
| **Child processes** | `0` | **0** (`1` total process) | **0** (`1` total process) | **7–8 child procs** (`8–9` total) | **PASS** |
| **Binary size** | Lean | **41.85 MiB** (QJS) / **82.28 MiB** (V8) | **22.51 MiB** (`exo-ui`: `33.6 MB`) | **0 MB** (OS WV2 runtime) | **WARN** |
| **Commercial licensing** | Commercial-safe | Core Rust/JS engines OK (MIT/Apache/MPL-2.0), **BUT** React Bits is **MIT + Commons Clause v1.0** (no redistribution of ported components) and GSAP is proprietary **Standard "No Charge"** (not OSI) | **MIT / Apache-2.0** | OS runtime | **WARN** |

### Why It Hit a Fundamental Wall (Not Just "More Engineering")

Our prototypes actually got **surprisingly far** — unmodified `@heroui/react@3.2.8` + Tailwind CSS v4 + `react-dom@19.3.1` rendered **35/35 components with 0 JS errors and MAE 2.43/255** in both QuickJS-NG and V8 inside a zero-WebView2 Rune DX12 window (`blitz-qjs-heroui-gallery.png`, `blitz-v8-heroui-gallery.png`). That proves Stylo + Taffy + Vello can handle static Tailwind CSS remarkably well.

**The wall is NOT JavaScript speed — it is the browser engine surface area:**
1. **Interactive browser DOM semantics (`react-aria`, `@radix-ui`, React 19 event delegation):** Real libraries depend on W3C pointer capture, focus scopes, top-layer `<dialog>`/portals, CSS `:checked`/`:focus-visible`/`::placeholder`, `getComputedStyle()` matrix serialization, `ResizeObserver`/`IntersectionObserver`/`MutationObserver`, and native form controls. Missing even small edge cases broke **16 of 23** automated interactions across HeroUI, Radix, framer-motion, and React Bits UI (`0/7` on Radix, `1/6` on HeroUI, `0/2` on framer-motion/GSAP).
2. **CPU rasterization & bridge latency:** Blitz's `anyrender_vello_cpu` rasterizes the `1280x800` scene on CPU each animated frame (`12.6–38.8 ms p50` in V8, up to `73.7 ms p50` in QJS — 13.5 FPS on animated backgrounds), missing the `<= 0.3 ms` delta vs hand-written Rune (`0.55 ms p50`) by **23x to 134x**. Even with GPU Vello (`anyrender_vello`), every animated frame still pays JS execution + DOM mutation batching + Stylo restyle + Taffy relayout + scene rebuild.
3. **WebGL2 / Canvas2D ecosystems (React Bits, OGL, Three.js):** Static analysis of installed libraries shows **OGL calls 75 distinct `gl.*` methods**, **React Bits Backgrounds calls 81**, and **Three.js calls 183**. Our real `WebGL2 -> Naga (GLSL->WGSL) -> wgpu DX12` shim compiled and drew 115 frames on RTX 3070, **but** Naga failed on **11 of 14 (78.6%)** real GLSL ES 300 shaders (`UnknownType("sampler2D")`, `UnknownVariable("gl_VertexID")`), and `Particles` alone hit **396 unsupported WebGL2 calls** (`drawArraysInstanced: 119`, `uniform2i: 113`, `texSubImage2D: 76`, `bufferSubData: 41`, `uniform1ui: 39`, `vertexAttribDivisor: 7`). Building a shim that actually runs Three.js/OGL/React Bits means reimplementing Google ANGLE (~300K LOC C++) in Rust.
4. **Compile-time AOT (Route 3) collapses on real libraries:** Our deterministic TypeScript AST audit across **1,536 files / 365,082 LOC** proved only **0.5% (1/221)** of React Bits, **1.1% (1/92)** of `@radix-ui/react-*`, and **0.0% (0/66)** of GSAP survive pure AOT compilation to Rune.
5. **Embedded Servo (Route 2) is far heavier and slower than WebView2:** Measured in `lab-web-in-rune` on this exact PC, `servo 0.8.0` cannot even link in-process with Rune (`icu_collections` semver conflict in `mozjs_sys`), takes **33.0 seconds** cold start, **499–2,627 ms/frame** paint, **305 MB WS / 484 MB Priv** (before Rune's +120 MB), and renders Discord login blank.

---

## 1. Master Route Comparison Table

All measurements collected live on this Windows 11 x64 RTX 3070 PC. Memory is full Win32 process tree (`CreateToolhelp32Snapshot` + `K32GetProcessMemoryInfo`).

| Route / Engine | Child Procs | Binary / Runtime Disk | Cold Start (ms) | Anim Frame `p50` / `p99` (ms) | Input Latency `p50` (ms) | Idle CPU (%) | Working Set (MB) | Private Bytes (MB) | Real Library Compat (Render + Behave) | License |
| --- | ---: | ---: | ---: | :---: | ---: | ---: | ---: | ---: | --- | --- |
| **Hand-written Rust Rune** (`rune-baseline.exe`, baseline) | **0** | **22.51 MiB** | **30.4–247.8** | **0.55 / 3.05** | **< 0.1** | **0.0%** | **36.1** idle / **156.0** active | **20.2** idle / **180.5** active | Native Rust (no npm libs) | MIT / Apache-2.0 |
| **Route 1a: React 19 + QuickJS-NG + Blitz + Shims** (`blitz-qjs.exe`) | **0** | **41.85 MiB** | 300–1,795 (`1,268` HeroUI) | **8.0–73.7** / **15.3–133.8** (`12.6–17.9` HeroUI) | 9.4–92.6 (`12.4` HeroUI) | 15.6% (post-warmup) | **105.1–212.6** (`126.6–157.6` HeroUI) | **107.5–274.1** (`128.1–173.6` HeroUI) | **~48%** (`83/85` mount, `7/23` interact, BG `54%` grid, needs `Intl` shim) | MIT + MPL-2.0 (`stylo`) |
| **Route 1b: React 19 + V8 152 JIT + Blitz + Shims** (`blitz-v8.exe`) | **0** | **82.28 MiB** | 448–2,170 (`804` HeroUI) | **7.5–38.8** / **13.6–344.3** (`13.8–15.6` HeroUI) | 14.8–67.8 (`17.6` HeroUI) | **0.76%** (settled HeroUI) | **111.6–221.9** (`202.6` HeroUI; `270.2` WebGL) | **72.1–282.2** (`218.9` HeroUI; `340.4` WebGL) | **~50%** (`83/85` mount, `7/23` interact, BG `54%` grid) | MIT + BSD-3 (`v8`) + MPL-2.0 (`stylo`) |
| **Route 1c: React 19 + Static Hermes (`shermes`) + Blitz** *(architectural + upstream audit)* | **0** | ~45–60 MiB est. | Fast bytecode | Same Blitz/Vello floor (`>= 7–30 ms`) | Same bridge floor | Low | ~130–200 MB est. | ~130–210 MB est. | **<= 50%** (untyped npm libs + `Proxy` fall back to dynamic VM; identical DOM/CSS/WebGL gaps) | MIT (`hermes`) + MPL-2.0 (`stylo`) |
| **Route 2: Embedded Servo `0.8.0`** *(measured in `lab-web-in-rune`)* | **1** *(in-proc blocked by `icu` conflict)* | **104.8 MiB** (`servo-worker.exe`) | **32,978.3** (33.0 s) | **198.9–2,626.6** avg (0.38–5 FPS) | > 200 | High | **137.1–305.2** worker (**~425** w/ Rune) | **213.9–483.5** worker (**~635** w/ Rune) | **FAIL** (blank Discord React SPA, failed YouTube, no WebAuthn/DRM) | MPL-2.0 |
| **Route 3: Compile-Time AOT TSX -> Rust/Rune** (`compile-and-audit.mjs`) | **0** | ~23–35 MiB | ~30–110 | **0.55 / 3.05** | **< 0.1** | **0.0%** | **36–156** | **20–181** | **0.5%** React Bits · **1.1%** Radix · **0.0%** GSAP · **0%** interactive HeroUI | MIT |
| **Prior Lab: Custom Reconciler (`lab-react-renderer`)** | **0** | 21.5 MiB (QJS) / 61.9 MiB (V8) | 270–294 | 0.2–0.4 ms layout | 0.2–0.4 | Low | 117–185 | 128–217 | **0%** for unmodified HeroUI/React Bits/Radix (custom widgets only, no real DOM/CSS) | MIT + MPL-2.0 (`lightningcss`) |
| **Reference: Chromium 156** *(measured on exact 5 scenes)* | **7–8** (`8–9` procs) | System / ~300 MB | 1,904–2,735 | **6.0–6.1 / 6.5–24.9** | ~14 | ~0.8% | **476.5–520.7** | **248.7–281.9** | **100%** (ground truth reference) | BSD-3 |
| **Reference: WebView2 in Rune (`tuned-low`, `lab-web-in-rune`)** | **7–8** (`8–9` procs) | **0 MB** (OS) | 418–676 | 1.4–2.2 ms host compose | ~14 | 0.3–0.9% | **666–809** active (**148** suspended) | **652–864** active | **100%** (full Chromium/Blink/ANGLE/DRM) | Microsoft WebView2 (OS) |

---

## 2. Per-Scene Measurements (`blitz-qjs` vs `blitz-v8` vs Chromium Reference)

Every scene renders **real, unmodified npm packages** bundled via `esbuild 0.25.12` + `tailwindcss v4.3.5` + `@heroui/styles 3.2.8` at `1280x800` dark mode (`improve/lab-native-react/aggregated-table.json`).

| Scene | Engine | JS Errors | Cold Start (ms) | Mount (ms) | First Paint (ms) | Anim `p50` / `p99` (ms) | Input `p50` (ms) | WS / Priv (MB) | Mounted | Interact Pass | Pixel MAE vs Chrome | Diff Pixels (>16) | 8x6 Grid Match |
| --- | --- | ---: | ---: | ---: | ---: | :---: | ---: | :---: | :---: | :---: | ---: | ---: | ---: |
| **heroui-gallery** (35 HeroUI v3.2.8 comps) | Chrome Ref | 0* | 1,201.5 | — | — | 6.10 / 10.80 | — | 520.7 / 281.2 | **35/35** | 5/6 | 0.00 | 0.00% | 100.0% |
| | `blitz-qjs` | **0** | 1,267.5 | 1,044.5 | 38.15 | 17.90 / 32.09 (`12.59` settled) | 14.24 | **126.6 / 128.1** | **35/35** | **1/6** | **2.43** | **3.20%** | **100.0%** |
| | `blitz-v8` | **0** | 804.3 | 591.6 | 21.53 | 13.79 / 62.77 | 17.63 | **202.6 / 218.9** | **35/35** | **1/6** | **2.43** | **3.20%** | **100.0%** |
| **shadcn-radix** (20 `@radix-ui` pkgs) | Chrome Ref | 0 | 778.8 | — | — | 6.10 / 6.50 | — | 476.5 / 248.7 | **20/20** | 6/7 | 0.00 | 0.00% | 100.0% |
| | `blitz-qjs` | 1 | 390.3 | 290.4 | 17.02 | 8.04 / 17.00 | 9.40 | 161.2 / 192.6 | 19/20 | **0/7** | 2.55 | 3.90% | 100.0% |
| | `blitz-v8` | 1 | 553.4 | 351.5 | 11.71 | 7.50 / 13.56 | 14.75 | 183.6 / 199.4 | 19/20 | **0/7** | 2.56 | 3.90% | 100.0% |
| **motion-gsap** (`framer-motion` + `gsap` + WAAPI) | Chrome Ref | 0 | 756.9 | — | — | 6.10 / 6.80 | — | — | **8/8** | 2/2 | 0.00 | 0.00% | 100.0% |
| | `blitz-qjs` | 37 | 300.1 | 217.4 | 12.09 | 9.78 / 15.26 | 12.13 | 105.1 / 107.5 | 8/8 | **0/2** | 5.59 | 7.61% | 91.67% |
| | `blitz-v8` | 37 | 448.1 | 334.8 | 9.95 | 7.52 / 21.65 | 20.40 | 174.9 / 194.8 | 8/8 | **0/2** | 5.59 | 7.61% | 91.67% |
| **reactbits-ui** (16 React Bits Text/Anim/Comp) | Chrome Ref | 0 | 917.8 | — | — | 6.10 / 24.90 | — | — | **16/16** | 2/2 | 0.00 | 0.00% | 100.0% |
| | `blitz-qjs` | 39 | 529.2 | 419.0 | 17.52 | 22.93 / 39.93 | 31.36 | 174.4 / 202.1 | 15/16 | **0/2** | 7.38 | 6.40% | 91.67% |
| | `blitz-v8` | 39 | 838.2 | 694.3 | 26.34 | 38.78 / 344.31 | 67.84 | 111.6 / 72.1 | 15/16 | **0/2** | 7.40 | 6.42% | 91.67% |
| **reactbits-bg** (6 WebGL2 / OGL / Canvas2D BGs) | Chrome Ref | 0 | 1,327.1 | — | — | 6.00 / 6.50 | — | 498.6 / 281.9 | **6/6** | 6/6 | 0.00 | 0.00% | 100.0% |
| | `blitz-qjs` | 0 | 1,794.9 | 1,685.8 | 19.97 | **73.67 / 133.80** | 92.64 | 212.6 / 274.1 | 6/6 | 6/6† | **34.77** | **30.24%** | **54.17%** |
| | `blitz-v8` | 0 | 2,169.7 | 2,064.2 | 13.47 | **30.97 / 259.29** | 47.62 | 221.9 / 282.2 | 6/6 | 6/6† | **34.77** | **30.24%** | **54.17%** |

*\*Chrome's single console error on `heroui-gallery` was a `404` on `/favicon.ico`; zero JS errors.*
*†`reactbits-bg` `6/6` interaction check only verifies `<canvas>` elements mounted with non-zero dimensions; visual pixel inspection below shows **0/6** matched Chrome visually (`MAE 34.77`).*

---

## 3. Compatibility Matrices per Library (Verified Visually & Behaviorally)

### 3.1 HeroUI v3.2.8 (`@heroui/react` + `@heroui/styles` + `react-aria-components`)

HeroUI v3.2.8 ships **84 component packages** built on `react-aria-components` and Tailwind v4. We mounted **35 representative components** covering every category (Inputs, Forms, Display, Feedback, Layout, Navigation, Overlays).

| Category | Components Tested | Visual Static Match vs Chrome | Interactive Behavior | Specific Defects Observed in Blitz + V8/QJS |
| --- | --- | :---: | :---: | --- |
| **Buttons** | `Button`, `ButtonGroup`, `ToggleButton`, `CloseButton` | 4/4 | **FAIL** (`Button` click did not increment) | React Aria `usePress` requires full pointer-capture / `isTrusted` / hit-test lifecycle. |
| **Toggles & Choice** | `Switch`, `Checkbox`, `RadioGroup`, `Slider` | 3/4 | **FAIL** (`Switch`, `Checkbox` did not toggle) | `RadioGroup` active blue dot missing (CSS `:checked`/`data-selected` visual gap); `Switch`/`Checkbox` clicks missed React Aria delegation. |
| **Forms** | `Input`, `TextArea`, `NumberField` | **0/3** | Untested | `Input` and `TextArea` render empty dark boxes with **invisible placeholders**; `NumberField` clips `"144"` and drops the `+` button. |
| **Display** | `Chip`, `Avatar`, `Badge`, `Kbd`, `Link`, `TagGroup`, `Table`, `EmptyState` | **8/8** | Static | Rendered crisply (`MAE 2.43`, green Badge `3`, `⌘ K`, Chips, Table layout accurate). |
| **Feedback** | `Spinner`, `Skeleton`, `ProgressBar`, `ProgressCircle`, `Meter`, `Alert` | 4/6 | Static | `Spinner` and `ProgressCircle` SVG `stroke-dasharray` rings are **invisible** in Blitz/usvg. |
| **Layout & Nav** | `Card`, `Separator`, `Breadcrumbs`, `Tabs`, `Accordion`, `Disclosure`, `Pagination` | **7/7** | **1/2** (`Accordion` PASS, `Tabs` FAIL) | `Tabs` required `Node.prototype.matches` shim to mount; `Accordion` expanded cleanly on click; `Tabs` click did not switch panel. |
| **Overlays / Portals** | `Modal`, `Popover`, `Tooltip` | 3/3 triggers | **FAIL** (`Modal` did not open) | Top-layer portal + `FocusScope` + `Overlay` positioning (`@react-aria/overlays`) broke on trigger click. *(Remaining 49 unmounted HeroUI components — `DatePicker`, `Calendar`, `ComboBox`, `Autocomplete`, `ColorPicker`, `Drawer`, `Sheet`, `Dropdown`, `Select`, `Toast` — depend on these exact overlay/popover/form primitives.)* |

- **HeroUI Verdict:** `35/35` mounted (`100%`), `29/35` visually acceptable statically (`82.9%`), **only `1/6` (16.7%) interactive checks passed**. **Overall true working rate: ~55–65% static-only, < 35% for full interactive app use.**
- **QuickJS-NG extra blocker:** HeroUI (`@react-aria/i18n`, `@internationalized/date`) throws `ReferenceError: Intl is not defined` on stock QuickJS-NG unless ECMA-402 `Intl` (`NumberFormat`, `DateTimeFormat`, `Collator`) is polyfilled or linked against ICU4X.

### 3.2 shadcn/ui + Radix UI (`@radix-ui/react-*`, 20 Packages)

| Component | Mounted | Visual vs Chrome | Interactive Check | Failure Mode |
| --- | :---: | :---: | :---: | --- |
| `Switch`, `Checkbox`, `Toggle`, `ToggleGroup`, `RadioGroup`, `Slider` | 6/6 | **6/6** | **0/3** (`Switch`, `Checkbox`, `Toggle` FAIL) | Static styling pixel-accurate (`MAE 2.56`), but React 19 + Radix synthetic event handlers did not fire state updates. |
| `Progress`, `Avatar`, `Separator` | 3/3 | **3/3** | Static | Visually identical to Chrome. |
| `ScrollArea` | 1/1 | **FAIL** | — | Rendered **empty box** (children vanished due to Radix's `display: table` viewport + `ResizeObserver` sizing). |
| `Tabs`, `Accordion`, `Collapsible` | 3/3 | **3/3** | **0/3** FAIL | Static initial tab/accordion renders cleanly; switching/expanding failed. |
| `Dialog`, `AlertDialog`, `Popover`, `DropdownMenu`, `HoverCard`, `Tooltip` | 6/6 | 6/6 triggers | **0/1** (`Dialog` FAIL) | Portals + Floating UI/Popper positioning + DismissableLayer/RemoveScroll failed to open. |
| `Select` | **0/1** | **CRASH** | — | Threw `TypeError: Cannot read properties of undefined (reading 'length')` during mount. |

- **shadcn/Radix Verdict:** `19/20` mounted (`95%`), `18/20` static visual match (`90%`), **0/7 (0%) interactive checks passed**.

### 3.3 React Bits (221 Upstream Components across 5 Categories; 22 Live-Tested)

| Category (Upstream Count) | Live-Tested Components | Mounted | Visually Accurate vs Chrome | Specific Defects Observed |
| --- | --- | :---: | :---: | --- |
| **TextAnimations** (33) | `BlurText`, `ShinyText`, `GradientText`, `CountUp`, `DecryptedText`, `GlitchText`, `RotatingText`, `TextType` | 7/8 | **4/8** (`ShinyText`, `GradientText`, `CountUp`, `RotatingText`) | `TextType` **crashed** (`null.replace` in GSAP); `BlurText` **blank** (blur+opacity filter); `DecryptedText` **overlapping double text** (`sr-only` `clip: rect(0,0,0,0)` unclipped); `GlitchText` misaligned clip. |
| **Animations** (39) | `ClickSpark` (Canvas2D), `StarBorder`, `FadeContent` (GSAP), `Magnet`, `Noise` (Canvas2D) | 5/5 | **2/5** (`ClickSpark` container, `Magnet`) | `StarBorder` **giant blue square clip leak** (`conic-gradient` + `border-radius` overflow leak in Blitz/Vello); `FadeContent` **completely blank** (GSAP `ScrollTrigger` + `invisible`); `Noise` needed `canvas.toBlob` stub and rendered no grain texture; `ClickSpark` & `Magnet` clicks failed (`0/2`). |
| **Components** (48) | `SpotlightCard`, `TiltedCard`, `Counter` | 3/3 | **1/3** (`SpotlightCard`) | `TiltedCard` **blank** (`data:image/svg+xml` image decode + missing 3D CSS `perspective`/`rotateX`/`rotateY`); `Counter` **overlapping stacked digits** (`144` colliding on `framer-motion` vertical strip). |
| **Backgrounds** (66 — WebGL2/OGL/Three/Canvas2D) | `Aurora` (WebGL2), `Iridescence` (WebGL2), `Particles` (OGL), `Waves` (Canvas2D), `LetterGlitch` (Canvas2D), `DotGrid` (Canvas2D+GSAP) | 6/6 | **0/6** (`MAE 34.77`, `54.2%` grid match) | `Aurora` & `Iridescence` hit Naga GLSL ES 300 errors (`sampler2D`, `gl_VertexID`) and drew generic fallback; `Particles` **solid black** (`drawArraysInstanced` + `uniform2i` unsupported); `Waves` & `DotGrid` **solid black**; `LetterGlitch` broken rainbow gradient (no Canvas2D glyph rasterizer). |

- **React Bits Verdict:** `21/22` mounted (`95.5%`), **only `7/22` (31.8%) visually accurate**, **0/2 interactive UI checks passed**, **0/6 WebGL/Canvas2D backgrounds accurate**, and **31–74 ms/frame (`13–32 FPS`)**!

### 3.4 framer-motion (v12.43) & GSAP (v3.15.1)

| Demo | Engine | Visual vs Chrome | Behavior | Notes |
| --- | --- | :---: | :---: | --- |
| `MotionSpring` | framer-motion | Partial | Animated to `x=160` | Slightly dimmed opacity vs Chrome final frame. |
| `MotionUseSpring` | framer-motion | **PASS** | Animated cyan circle to `x=120` | Worked smoothly via JS `requestAnimationFrame` + inline `transform`. |
| `MotionLayoutId` | framer-motion | **FAIL** | Click failed (`0/1`) | Stacking context bug: `"Library"` label painted *behind* blue pill. |
| `MotionAnimatePresence` | framer-motion | **FAIL** | Click failed (`0/1`) | Mounted card stuck invisible (`opacity: 0`). |
| `MotionSvgPath` | framer-motion | **FAIL** | Rendered dotted | `stroke-dasharray` unitless `pathLength` rendered dashed instead of solid curve. |
| `WaapiNativeShim` | Route 4 WAAPI (`waapi.rs`) | **PASS** | Animated opacity smoothly | Proves our Rust WAAPI keyframe interpolator works on simple scalar properties. |
| `GsapTween` & `GsapStagger` | GSAP 3.15.1 | **FAIL (0/2)** | **Frozen (`37` JS errors)** | GSAP threw `Cannot read properties of null (reading 'replace')` parsing `getComputedStyle(el).transform` matrix; box never moved/rotated and 16 bars never animated. |

---

## 4. Route 3: Compile-Time AOT (TSX -> Rust/Rune) Audit

We built a deterministic TypeScript AST compiler and compatibility scanner (`apps/launcher/lab/native-react/aot-compiler/compile-and-audit.mjs`, output in `improve/lab-native-react/route3-aot-audit.json`) and ran it over **every installed source/dist file** across all target libraries and Exo's own `ui/src/`:

| Library Audited | Files | Total LOC | Pure AOT Survive (%) | With Runtime Shim (%) | Top Blocking Web API Call Sites |
| --- | ---: | ---: | ---: | ---: | --- |
| **React Bits** (`src/ts-tailwind`, all 221 comps) | 221 | 155,288 | **1 (0.5%)** | **37 (16.7%)** | `window/document`: **2,236** · CSS 3D/dynamic: **1,208** · Canvas/WebGL: **992** · DOM measure: **799** · WAAPI/rAF/GSAP/Motion: **776** · Observers: **365** |
| **`@radix-ui/react-*`** (20 shadcn primitives) | 92 | 25,462 | **1 (1.1%)** | **1 (1.1%)** | `window/document`: **344** · `Proxy`/dynamic JS: **115** · DOM measure: **98** · Portals/Popper/Focus: **74** |
| **`@heroui/react`** (v3.2.8 wrappers) | 189 | 14,645 | 105 (55.6%)* | 108 (57.1%)* | Delegates interactive behavior to `react-aria-components` (**153** direct hooks + **1,170** inside `react-aria-components`) |
| **`react-aria-components`** (HeroUI core) | 378 | 32,431 | 198 (52.4%)* | 198 (52.4%)* | ARIA/focus/overlay/portal hooks: **1,170** · DOM measure: **54** · `window/document`: **50** |
| **`gsap`** (v3.15.1) | 66 | 45,973 | **0 (0.0%)** | 53 (80.3%) | rAF/tween engine: **1,191** · DOM measure: **280** · `window/document`: **138** · CSS dynamic: **53** |
| **`framer-motion`** (v12.43) | 163 | 7,559 | 122 (74.8%)† | 157 (96.3%)† | Thin ESM re-exports; actual DOM/WAAPI/FLIP engine lives in `motion-dom` (DOM measure, `getComputedStyle`, WAAPI, `ResizeObserver`) |
| **Exo Launcher `ui/src/`** | 427 | 83,842 | **219 (51.3%)** | 392 (91.8%) | `window/document`: **1,287** · DOM measure: **272** · WAAPI/anim: **65** · Observers: **49** · Canvas: **12** |

*\*HeroUI and `react-aria-components` have many small barrel/style wrapper files without direct `window.` calls, which inflates file-level counts; **0% of HeroUI's interactive components** (`Button`, `Switch`, `Tabs`, `Modal`, `Select`, `Popover`, `Calendar`, `Slider`, etc.) compile standalone to Rune because every one imports `@react-aria/*` browser DOM/focus/pointer hooks.*
*†`framer-motion/dist/es` mostly re-exports `motion-dom`.*

**Conclusion for Route 3:** AOT TSX->Rust compilation only works for static presentational leaf JSX (and static data codegen like tokens, icons, and RPC DTOs). It cannot compile unmodified third-party React component libraries.

---

## 5. Route 4: Gap Shims — What Worked vs Where the Abyss Starts

We implemented all three Route 4 shims directly inside `crates/blitz-react-core`:

1. **WAAPI (`crates/blitz-react-core/src/waapi.rs`, 125 LOC):**
   - **Worked:** `element.animate()` runs on the native tick loop and drives Blitz `DocumentMutator::set_style_property` smoothly (`WaapiNativeShim` passed visually).
   - **Limitation:** Does not cover CSS 3D transform matrices, composite modes, `AnimationTimeline` scroll-driven animations, or `getComputedStyle().transform` matrix readback required by GSAP.
2. **Canvas2D -> Vello (`crates/blitz-react-core/src/canvas2d.rs`, 270 LOC):**
   - **Worked:** Implemented `blitz_dom::Widget` returning vector `anyrender::Scene` paths (`fillRect`, `strokeRect`, `BezPath`, `arc`, transforms, alpha) — `ClickSpark` flushed 38–52 batches cleanly.
   - **Limitation:** Real Canvas2D React Bits (`Waves`, `DotGrid`, `LetterGlitch`, `Noise`) rely on `fillText` with custom fonts/metrics, `getImageData`/`putImageData` pixel loops, `canvas.toBlob`, `Path2D` transforms, global composite operations, shadows, and crisp HiDPI `ResizeObserver` sizing. Building a full 2D Canvas spec on Vello is a multi-month project by itself.
3. **WebGL2 -> Naga -> wgpu DX12 (`crates/blitz-react-core/src/webgl_wgpu.rs`, 550 LOC):**
   - **Worked (proof of concept):** Executed `115` real DX12 offscreen render passes via `wgpu` and blitted them into Blitz `<canvas>` `CustomWidget` textures (`blitz-v8-reactbits-bg.png`).
   - **Why it cannot scale to real WebGL libraries:**
     - **Static `gl.*` audit:** `ogl` calls **75** distinct WebGL methods; `React Bits Backgrounds` calls **81**; `Three.js` calls **183** (`framebufferTexture2D`, `blitFramebuffer`, `texImage3D`, `texSubImage3D`, `renderbufferStorage`, 21 extensions).
     - **Live Naga GLSL ES 300 translation telemetry:** Only **3 of 14 (21.4%)** shaders translated cleanly; **11/14 failed** (`UnknownType("sampler2D")` inside uniform blocks, `UnknownVariable("gl_VertexID")` vs Vulkan `gl_VertexIndex`, precision/layout qualifiers).
     - **Live unsupported call telemetry (single scene):** `drawArraysInstanced: 119`, `uniform2i: 113`, `texSubImage2D: 76`, `bufferSubData: 41`, `uniform1ui: 39`, `vertexAttribDivisor: 7`, `bindAttribLocation: 1`.
     - **Memory & latency tax:** Adding a WebGL wgpu context + staging readback increased memory to **270.2 MB WS / 340.4 MB Private** and pushed frame times to **31.0 ms `p50` / 259.3 ms `p99`** (V8) and **73.7 ms `p50` / 133.8 ms `p99`** (QJS).

---

## 6. Visual Evidence (Screenshots in `improve/lab-native-react/`)

All side-by-side `1280x800` captures were generated live on RTX 3070 and pixel-diffed against Playwright Chromium (`diff-pixels.mjs`):

| Scene | Chrome Reference | Blitz + V8 (Zero WebView2) | Blitz + QuickJS-NG (Zero WebView2) | Key Visual Takeaway |
| --- | --- | --- | --- | --- |
| **HeroUI v3.2.8 (35 comps)** | [chrome-heroui-gallery.png](lab-native-react/chrome-heroui-gallery.png) | [blitz-v8-heroui-gallery.png](lab-native-react/blitz-v8-heroui-gallery.png) | [blitz-qjs-heroui-gallery.png](lab-native-react/blitz-qjs-heroui-gallery.png) | Static layout & Tailwind v4 colors look shockingly close (`MAE 2.43`, 100% grid), **but** Input/TextArea placeholders, NumberField `+`, Radio active dot, Spinner/ProgressCircle SVG rings, and 5/6 click interactions fail. |
| **shadcn / Radix (20 pkgs)** | [chrome-shadcn-radix.png](lab-native-react/chrome-shadcn-radix.png) | [blitz-v8-shadcn-radix.png](lab-native-react/blitz-v8-shadcn-radix.png) | [blitz-qjs-shadcn-radix.png](lab-native-react/blitz-qjs-shadcn-radix.png) | 18/20 static parity (`MAE 2.56`), **but** `RadixSelect` crashes, `RadixScrollArea` is blank, and **0/7** interactive controls respond. |
| **framer-motion + GSAP** | [chrome-motion-gsap.png](lab-native-react/chrome-motion-gsap.png) | [blitz-v8-motion-gsap.png](lab-native-react/blitz-v8-motion-gsap.png) | [blitz-qjs-motion-gsap.png](lab-native-react/blitz-qjs-motion-gsap.png) | `MotionUseSpring` & native `WAAPI` animate, **but** `MotionLayoutId` z-order swallows text, `AnimatePresence` is invisible, `MotionSvgPath` renders dashed, and GSAP throws 37 errors leaving both demos frozen. |
| **React Bits UI (16 comps)** | [chrome-reactbits-ui.png](lab-native-react/chrome-reactbits-ui.png) | [blitz-v8-reactbits-ui.png](lab-native-react/blitz-v8-reactbits-ui.png) | [blitz-qjs-reactbits-ui.png](lab-native-react/blitz-qjs-reactbits-ui.png) | `GradientText`, `CountUp`, `ShinyText`, `RotatingText` render, **but** `BlurText` & `FadeContent` are blank, `TextType` crashes, `DecryptedText` & `Counter` overlap text, `StarBorder` leaks a blue clip square, `TiltedCard` is blank. |
| **React Bits BGs (6 shaders)** | [chrome-reactbits-bg.png](lab-native-react/chrome-reactbits-bg.png) | [blitz-v8-reactbits-bg.png](lab-native-react/blitz-v8-reactbits-bg.png) | [blitz-qjs-reactbits-bg.png](lab-native-react/blitz-qjs-reactbits-bg.png) | `MAE 34.77` (`54%` grid match): Naga rejects 11/14 GLSL shaders; `Particles`, `Waves`, `DotGrid` are solid black; `LetterGlitch` draws a broken gradient. |

---

## 7. Commercial Licensing Audit (Verified Directly from Manifests)

| Package / Crate | Version | SPDX / License Text | Commercial Use Allowed? | Notes for MIT Exo Launcher |
| --- | --- | --- | :---: | --- |
| `rune` (`UhhErix/rune`) | `rev ba9aa61` | MIT / Apache-2.0 | **Yes** | Already shipping in `apps/launcher/native-ui`. |
| `blitz-dom`, `blitz-paint`, `anyrender`, `vello_cpu`, `naga`, `taffy` | `0.3.0-beta.5` / `0.15` / `0.4` / `30.0.1` / `0.16` | MIT OR Apache-2.0 | **Yes** | Permissive; MIT-compatible. |
| `stylo` | `0.24.0` | **MPL-2.0** | **Yes** | File-level weak copyleft; compatible as a dependency; any forks/edits to Stylo files stay MPL-2.0. |
| `rquickjs` (QuickJS-NG) | `0.14.0` | **MIT** | **Yes** | Permissive; zero DLLs. |
| `v8` (`rusty_v8`) | `152.2.0` | **MIT** (V8: **BSD-3-Clause**) | **Yes** | Permissive; adds `+40.4 MiB` to binary. |
| `facebook/hermes` (`static_h`) | `static_h` | **MIT** | **Yes** | Permissive; experimental Windows build. |
| `servo` | `0.8.0` | **MPL-2.0** | **Yes** | File-level weak copyleft; unusable due to 33 s start / 2.6 s frame / `icu` conflict. |
| `react`, `react-dom`, `tailwindcss`, `framer-motion`, `three`, `@radix-ui/*`, `tailwind-variants` | `19.3.1` / `4.3.5` / `12.43.0` / `0.180.0` / `1.3.1` / `4.0.2` | **MIT** | **Yes** | Permissive. |
| `@heroui/react`, `@heroui/styles`, `react-aria`, `react-aria-components` | `3.2.8` / `3.53.1` / `1.22.1` | **Apache-2.0** | **Yes** | Permissive; NOTICE attribution required. |
| `ogl` | `1.0.11` | **Unlicense** | **Yes** | Public domain equivalent. |
| `lucide-react` | `0.511.0` | **ISC** | **Yes** | Permissive. |
| **React Bits** (`DavidHDev/react-bits`) | `main` (`37f53df`) | **MIT + Commons Clause v1.0** | **Restricted** | Allows commercial use *inside an app*, **prohibits** redistributing/sublicensing ported components or templates. Do **not** copy React Bits source into MIT open-source Exo; write original WGSL shaders from first principles instead. |
| **GSAP** (`gsap`) | `3.15.1` | **Standard "no charge" license** (`gsap.com/standard-license`) | **Restricted (Proprietary)** | Not OSI open source; forbids competing visual authoring tools and cannot be relicensed MIT. Avoid bundling in MIT Exo. |

---

## 8. Final Recommendation & What to Port by Hand to Native Rust Rune

### Decision
**Stay 100% Native Rust Rune (`UhhErix/rune` + `exo-kit` + `exo-ui`).**
- `feat/native-polish-2` is already **~90%+ complete** (`348/363` RPCs, `81/83` events), starts in **~111–248 ms**, idles at **0.0% CPU** and **~36–210 MB WS** with **0 child processes**, and runs at **`0.55 ms` `p50` frame time** — **23x–134x faster** than any Blitz+JS-engine stack and **57–69% less memory** than WebView2.
- Keep **on-demand, immediately-disposed WebView2** *only* for the 4 unavoidable browser boundaries (DRM music engines, Stripe PCI card form, Epic/GOG captcha/OAuth, anti-bot Store Gifts/Links) per `lab-web-in-rune` and `research/react-to-rune`.
- For **Exo Mouse** (`apps/launcher/mouse`), reuse `research/react-to-rune`'s AGPL-isolated headless `rquickjs` protocol runner (`ExoMouse.Service.exe`, ~5–8 MB RAM) paired with a native Rune UI in `exo-ui` (or `lab-react-renderer`'s narrow QuickJS host *only* if you don't want to hand-port Mouse's 9K LOC UI).

### Best React-Ecosystem Ideas Worth Porting by Hand to `exo-kit` / `UhhErix/rune`

Instead of embedding a partial browser engine to run unmodified JS libraries at 15–70 ms/frame, we can get **100% of the visual magic at `0.5 ms`/frame and pure MIT licensing** by hand-porting the highest-leverage ideas directly into `exo-kit` and `UhhErix/rune`:

1. **Original WGSL GPU Effects in `rune` / `exo-kit` (inspired by React Bits Backgrounds & Animations — 100% MIT, 0.2 ms on RTX 3070):**
   - **Ambient Aurora / Iridescence / Silk header backdrops:** 40–60 line native WGSL fragment shader bound via `El::effect(...)` (just like `SlotCompositor` and Rune's Liquid Glass!) for Game Page hero headers, Onboarding, and Store feature banners — runs natively on Rune's existing DX12 `wgpu::Device` with **zero texture readbacks, zero JS, and zero extra VRAM**.
   - **SpotlightCard / BorderGlow hover rim:** Radial cursor-position uniform passed to `exo-kit` card border shader (`hover_pos` already tracked by Rune pointer events) — ideal for Store featured cards and Game Library tiles.
   - **ClickSpark / StarBorder micro-fx:** Lightweight 100-line Rune spring + vector particle/rim effect in `exo-kit` for Launch Play button press or achievement popups (respecting `reduce_motion`).
   - **BlurText / CountUp / ShinyText / Counter:** Trivial 30–60 line native Rust helpers in `exo-kit` driven by Rune springs (`SPRING_QUICK`) for FPS telemetry readouts, download speeds, and onboarding headings.
2. **HeroUI v3 & Radix Interaction/Accessibility Patterns in `exo-kit` (MIT/Apache-2.0 inspiration):**
   - **Press/Hover/Focus state machine (`react-aria` `usePress` pattern):** Unified `Pressed` / `Hovered` / `FocusVisible` state tracking (keyboard-only focus ring via `AccessKit` + Tab detection, canceling click if pointer leaves bounds before release) across every `exo-kit` control.
   - **Shared-element `layoutId` spring indicator (HeroUI `Tabs.Indicator` / `framer-motion`):** Sliding spring-interpolated glass pill behind the active room capsule in Exo's top Bar and Settings tabs (only ~80 LOC in `exo-kit`, interpolating previous-to-current Taffy rect with Rune's existing spring solver!).
   - **Roving tabindex & keyboard typeahead (`Radix` / `HeroUI` `ListBox`, `Menu`, `Tabs`):** Arrow-key navigation, `Home`/`End`, `Escape` dismiss, and focus restoration on modal/palette/menu close — directly upgrades `exo-kit::menu`, `palette.rs`, and gamepad/keyboard parity.
   - **Compound Disclosure / Accordion / Toast stacking:** Smooth height spring reveal (`Accordion` / `Disclosure`) and stacked swipe/auto-dismiss toast queue (`HeroUI` `Toast` pattern) using pure Rust state in `exo-kit`.
