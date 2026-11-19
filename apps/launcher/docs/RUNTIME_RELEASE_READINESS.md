# .NET 11 release readiness

`global.json` pins the stable .NET 10 SDK (`10.0.403`, `rollForward: latestFeature`, no
prereleases), so any newer 10.0 SDK builds it too. .NET 11 comes in only as a stable
release that passes release validation, not as a preview or RC.
As of October 28, 2026, the official [.NET 11 downloads](https://dotnet.microsoft.com/en-us/download/dotnet/11.0)
list RC3; the [.NET release schedule](https://github.com/dotnet/core/blob/main/release-notes/README.md)
places the final release on November 10, 2026. .NET 11's ReadyToRun code targets x86-64-v3
(AVX2), so validation also measures startup with `DOTNET_EnableAVX2=0`.

Once a stable SDK is published, review its release notes and supported Windows
versions, install the exact candidate, and run:

```powershell
./tools/Test-RuntimeCandidate.ps1 -CandidateSdkVersion <reviewed-11.0-SDK-version> `
  -PackageDirectory ./artifacts/runtime-candidate/package -BuildAndPublish
```

The gate selects the exact SDK from a disposable working directory (never rewriting the
repository's pin), builds the full Release solution and publishes the self-contained
app, checks that the package declares a .NET 11 runtime, and writes
`artifacts/runtime-candidate/runtime-candidate.json`: SDK, packaged runtime, executable
hash, OS build, architecture, CPU, GPU and driver. This script is the gate; it is run locally (there is no GitHub Actions). Preview SDKs and
runtimes are rejected unless `-AllowPreviewBaseline` records the current baseline. It
runs no tests and does not start the app: run `dotnet test` and the E2E suite
(AGENTS.md › Gates, Testing) on the candidate build as well.

The host declares Windows 11 build 22000 as its minimum (`TargetPlatformMinVersion` in
`ExoLauncher/ExoLauncher.csproj`). Run the candidate package on that build itself with
`-RequireMinimumWindowsBuild`, and on the supported x64 CPU/GPU targets; a newer
developer or CI machine is not evidence for the minimum build.

Before changing `global.json`, keep the target-machine evidence, review the manual
Windows SDK targeting entries in `Directory.Build.props`, and confirm whether the
stable SDK supports the `10.0.28000.0` target directly. Remove those entries only after
restore, WinUI resource indexing, native capture and the packaged app pass without
them. Keep the tested pin if any of these fail.
