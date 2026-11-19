<h1 align="center">Exo Shaders</h1>
<p align="center">
  <strong>No more shader compilation stutter.</strong> Exo Shaders compiles your games' shaders into your graphics
  driver's cache before you play, so a game doesn't stop to compile one the first time it meets a new effect.
</p>

Exo Shaders is the shader precompiler behind [Exo Launcher](https://getexo.app/launcher)'s PC › Graphics › Shaders. It
ships with Exo as its own program, run as a separate process; download Exo Launcher to use it.

> [!NOTE]
> Exo Shaders is Exo's **fork of [SCSKiller](https://github.com/BlueHeisenberg/SCSKiller)**, based on SCSKiller and not
> affiliated with or endorsed by it. SCSKiller's own app and downloads are on its repository. Where this fork stands
> against SCSKiller, and how SCSKiller's changes come in: [UPSTREAM.md](UPSTREAM.md).

## What it does

- **Finds your games** in every launcher Exo Launcher reads, and shows which are compiled, which need it, and why.
- **Compiles ahead of time**, in its own process with the game closed: it reads the shaders a game ships with, works
  out the pipelines the game will build and compiles them into the driver's cache.
- **Keeps a queue**, and Exo asks it to compile again when a driver update clears the cache, while nobody uses the PC.
- **Stays out of your way**: a compile pauses while you play, gives its memory back when paused, and holds its memory
  use down so the rest of your PC stays quick.
- **Records when it has to**, once you allow it: for a game whose files don't give its pipelines, an optional recorder
  learns them while you play a few minutes, then they compile.
- **Leaves anti-cheat games alone** beyond reading their files: no launching, no injecting and no recorder, in any game
  whose folder holds anti-cheat or that Exo Launcher keeps it out of.

Exo Shaders fixes shader compilation stutter only, not traversal or streaming stutter, and a shader it couldn't find or
record can still compile in game.

## How it fits

A headless service (`engine/src/ExoShaders.Service`) hosting SCSKiller's engine (`src/SCSKiller.Core`, compiled as
the Exo build: local-only, `App/ExoBuild.cs`). Exo Launcher starts it hidden and talks to it only through newline-
delimited JSON on its stdin and stdout ([engine/CONTRACT.md](engine/CONTRACT.md)); no Exo Launcher code is compiled in, and
none of this code goes into Exo Launcher. The native files (the recorder, the compile process, the Vulkan layer) are
built from [proxy](proxy) and kept in [engine/prebuilt](engine/prebuilt) ([SOURCE.md](engine/prebuilt/SOURCE.md)).
Design: [ARCHITECTURE.md](ARCHITECTURE.md). Exo's changes: [engine/CHANGELOG.md](engine/CHANGELOG.md); SCSKiller's:
[CHANGELOG.md](CHANGELOG.md).

## Build and test

The .NET SDK in `global.json`:

```
pwsh engine\build\publish-service.ps1
dotnet build engine\src\ExoShaders.Service\ExoShaders.Service.csproj -c Release -p:ExoBuild=true
dotnet test engine\tests\ExoShaders.Service.Tests -c Release
```

The first lands the add-on's engine in `engine\out\service`; Exo Launcher's release build (`tools\Build-Release.ps1`)
runs it through `tools\Build-ShadersEngine.ps1` and ships it as `addons\shaders\engine`. The protocol tests run the built
service in `--safe` mode on a temporary data folder. SCSKiller's own tests are in `tests/` (`build\test.ps1`).

## License

GPL-3.0-or-later ([LICENSE](LICENSE)), with an additional permission for Oodle, the Windows App SDK and graphics driver
libraries ([LICENSE-EXCEPTION.txt](LICENSE-EXCEPTION.txt)). Copyright the SCSKiller authors and Exo Shaders'
contributors. Third-party components: [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md). Each release's source is
published at [getexo.app/source](https://getexo.app/source). Exo Launcher itself is MIT and is not part of this program.

Not affiliated with SCSKiller, any GPU maker, engine maker or game publisher. All product names are trademarks of their
owners.
