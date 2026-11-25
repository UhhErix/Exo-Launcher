# Exo Launcher

Exo Launcher brings games from different stores into one library on Windows 11.
The [Launcher source](apps/launcher) is in this repository.

- [Exo Cinema](https://github.com/UhhErix/Exo-Cinema) has its own source and releases.
- [Exo](https://github.com/UhhErix/exo) holds the website, self-hostable Workers and shared packages.

## Download

Download the [Exo Launcher 3.31.1 installer](https://github.com/UhhErix/Exo-Launcher/releases/tag/v3.31.1) for Windows 11 x64 from GitHub Releases.
See [RELEASES.md](RELEASES.md) for the installer filename and SHA-256 checksum.

## What's in this snapshot

The Launcher host, UI, Exo Mouse, Exo Shaders (GPL-3.0-or-later, in
`apps/launcher/shaders`) and Exo Capture (MIT, in `apps/launcher/capture`;
bundled OBS Studio game capture files are GPL-2.0-or-later) source are included.

The snapshot also leaves out internal security test harnesses and production
Cloudflare deployment configuration. See the [source import notes](SECURITY-SCRUB.md)
and [third-party notices](THIRD_PARTY_NOTICES.md) for details.

See the [Rooms roadmap](ROADMAP.md) for what's implemented and who owns it.

## Build and test

Use Windows 11 x64, the [.NET SDK pinned in global.json](apps/launcher/global.json),
the Windows 11 SDK, Node.js, and Visual Studio or Build Tools with the .NET desktop
and C++ desktop workloads. The host builds the Steam IPC helper with Native AOT.
From the repository root:

```powershell
cd apps/launcher
npm ci --prefix ui
npm ci --prefix mouse
dotnet build ExoLauncher.sln -c Release -p:Platform=x64
dotnet test --project ExoLauncher.Tests -c Release -p:Platform=x64
npm test --prefix ui
```

See [BUILDING.md](apps/launcher/BUILDING.md) for development commands and add-on
requirements.

First-party source is [MIT licensed](LICENSE). Third-party components retain their
own terms; see [third-party notices](THIRD_PARTY_NOTICES.md).
