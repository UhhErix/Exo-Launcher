# Source import security review

Cloudflare deployment exports, backup bundles and original Git history were not imported.

## Launcher 3.26.0

The release-tree scan flagged 234 matches across 64 files. The internal
`apps/launcher/security/attacks/` tree, including its one flagged file, was
not imported. The original scanner configuration was also left out; the
import was scanned without its allowlists.

- Identity vector secrets are represented by generated-field markers. The
  Launcher tests still build the canonical vectors and check their original hash.
- Synthetic credential tests, a test public key, the common-password denylist
  and the UI mock use split literals to retain their inputs without intact
  credential-shaped strings. The compressed driver HTML fixtures use
  placeholders for scanner matches outside the driver data.
- Vendored Exo Shield rule data uses placeholders for matches. SponsorBlock's
  embedded YouTube API key is replaced by a placeholder; requests requiring a
  YouTube API key need a separately supplied key.
- Launcher Worker configs omit production routes and schedules and use
  placeholder resource IDs and bucket names.

### Files with replaced matches

- `apps/launcher/Exo.Identity/Vectors/exo-identity-vectors.json`
- `apps/launcher/ExoLauncher.Tests/AccountSessionEndTests.cs`
- `apps/launcher/ExoLauncher.Tests/AccountSessionTests.cs`
- `apps/launcher/ExoLauncher.Tests/AppLogRedactTests.cs`
- `apps/launcher/ExoLauncher.Tests/CrashReportTests.cs`
- `apps/launcher/ExoLauncher.Tests/Fixtures/DriverAdvisories/node-signed.json`
- `apps/launcher/ExoLauncher.Tests/Fixtures/GpuDrivers/amd-drivers.html.gz`
- `apps/launcher/ExoLauncher.Tests/Fixtures/GpuDrivers/amd-radeon-pro-w7900.html.gz`
- `apps/launcher/ExoLauncher.Tests/Fixtures/GpuDrivers/amd-radeon-rx-6800-xt.html.gz`
- `apps/launcher/ExoLauncher.Tests/Fixtures/GpuDrivers/amd-radeon-rx-9070-xt.html.gz`
- `apps/launcher/ExoLauncher.Tests/Fixtures/GpuDrivers/amd-ryzen-7-7840hs.html.gz`
- `apps/launcher/ExoLauncher.Tests/Fixtures/GpuDrivers/intel-11th-14th-gen-864990.html.gz`
- `apps/launcher/ExoLauncher.Tests/Fixtures/GpuDrivers/intel-7th-10th-gen-776137.html.gz`
- `apps/launcher/ExoLauncher.Tests/Fixtures/GpuDrivers/intel-arc-785597.html.gz`
- `apps/launcher/ExoLauncher.Tests/Fixtures/GpuDrivers/intel-chipset-19347.html.gz`
- `apps/launcher/ExoLauncher.Tests/Fixtures/GpuDrivers/radeon-rx-580.html.gz`
- `apps/launcher/ExoLauncher.Tests/FriendLinkTests.cs`
- `apps/launcher/ExoLauncher.Tests/IdentityCryptoTests.cs`
- `apps/launcher/ExoLauncher.Tests/IdentityVectorsTests.cs`
- `apps/launcher/ExoLauncher.Tests/LocalFileHostTests.cs`
- `apps/launcher/ExoLauncher.Tests/PlayerDataTests.cs`
- `apps/launcher/ExoLauncher.Tests/SteamSignInServiceTests.cs`
- `apps/launcher/ExoLauncher.Tests/SteamTokenLogScrubTests.cs`
- `apps/launcher/ExoLauncher/Assets/Extensions/exo-shield/adguard-mobile.json`
- `apps/launcher/ExoLauncher/Assets/Extensions/exo-shield/chn-0.json`
- `apps/launcher/ExoLauncher/Assets/Extensions/exo-shield/easylist.json`
- `apps/launcher/ExoLauncher/Assets/Extensions/exo-shield/easyprivacy.json`
- `apps/launcher/ExoLauncher/Assets/Extensions/exo-shield/hagezi-pro.json`
- `apps/launcher/ExoLauncher/Assets/Extensions/exo-shield/ind-0.json`
- `apps/launcher/ExoLauncher/Assets/Extensions/exo-shield/nor-0.json`
- `apps/launcher/ExoLauncher/Assets/Extensions/exo-shield/pol-3.json`
- `apps/launcher/ExoLauncher/Assets/Extensions/exo-shield/rulesets/scripting/scriptlet/isolated/adguard-mobile.js`
- `apps/launcher/ExoLauncher/Assets/Extensions/exo-shield/rulesets/scripting/scriptlet/isolated/annoyances-cookies.js`
- `apps/launcher/ExoLauncher/Assets/Extensions/exo-shield/rulesets/scripting/scriptlet/isolated/ublock-filters.js`
- `apps/launcher/ExoLauncher/Assets/Extensions/exo-shield/rulesets/scripting/scriptlet/main/adguard-mobile.js`
- `apps/launcher/ExoLauncher/Assets/Extensions/exo-shield/rulesets/scripting/scriptlet/main/annoyances-cookies.js`
- `apps/launcher/ExoLauncher/Assets/Extensions/exo-shield/rulesets/scripting/scriptlet/main/annoyances-overlays.js`
- `apps/launcher/ExoLauncher/Assets/Extensions/exo-shield/rulesets/scripting/scriptlet/main/jpn-1.js`
- `apps/launcher/ExoLauncher/Assets/Extensions/exo-shield/rulesets/scripting/scriptlet/main/ublock-filters.js`
- `apps/launcher/ExoLauncher/Assets/Extensions/exo-shield/rulesets/scripting/scriptlet/main/ukr-0.js`
- `apps/launcher/ExoLauncher/Assets/Extensions/exo-shield/rulesets/scripting/specific/adguard-mobile.json`
- `apps/launcher/ExoLauncher/Assets/Extensions/exo-shield/rulesets/scripting/specific/easylist.json`
- `apps/launcher/ExoLauncher/Assets/Extensions/exo-shield/rulesets/scripting/specific/jpn-1.json`
- `apps/launcher/ExoLauncher/Assets/Extensions/exo-shield/rulesets/scripting/specific/ublock-filters.json`
- `apps/launcher/ExoLauncher/Assets/Extensions/exo-shield/rulesets/strictblock/adguard-mobile.json`
- `apps/launcher/ExoLauncher/Assets/Extensions/exo-shield/rulesets/strictblock/chn-0.json`
- `apps/launcher/ExoLauncher/Assets/Extensions/exo-shield/rulesets/strictblock/easylist.json`
- `apps/launcher/ExoLauncher/Assets/Extensions/exo-shield/rulesets/strictblock/easyprivacy.json`
- `apps/launcher/ExoLauncher/Assets/Extensions/exo-shield/rulesets/strictblock/pol-3.json`
- `apps/launcher/ExoLauncher/Assets/Extensions/exo-shield/rulesets/strictblock/ublock-badware.json`
- `apps/launcher/ExoLauncher/Assets/Extensions/exo-shield/rulesets/strictblock/urlhaus-full.json`
- `apps/launcher/ExoLauncher/Assets/Extensions/exo-shield/rus-0.json`
- `apps/launcher/ExoLauncher/Assets/Extensions/exo-shield/ublock-badware.json`
- `apps/launcher/ExoLauncher/Assets/Extensions/exo-shield/ublock-filters.json`
- `apps/launcher/ExoLauncher/Assets/Extensions/exo-shield/urlhaus-full.json`
- `apps/launcher/ExoLauncher/Assets/Extensions/sponsorblock/js/content.js`
- `apps/launcher/ExoLauncher/Assets/Extensions/sponsorblock/js/options.js`
- `apps/launcher/ExoLauncher/Assets/Extensions/sponsorblock/js/popup.js`
- `apps/launcher/cloud/account/src/common-passwords.js`
- `apps/launcher/cloud/mailbox/test/mailbox.test.mjs`
- `apps/launcher/cloud/reports/test/reports.test.mjs`
- `apps/launcher/cloud/reports/test/telemetry.test.mjs`
- `apps/launcher/ui/src/lib/hostMock.ts`
