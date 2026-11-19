# Prebuilt native files

Built from this repository's `proxy\` after merging SCSKiller v1.4.8 (`c5badf849a17756401b42f78c3950c43455a1968`) with
`-DSCSK_VERSION=1.4.8-exo.1`, Visual Studio 18 Build Tools (MSVC 14.51), Release:

```
cmake -S proxy -B <x64 folder> -A x64 -DSCSK_VERSION=1.4.8-exo.1
cmake -S proxy -B <x86 folder> -A Win32 -DSCSK_VERSION=1.4.8-exo.1
cmake --build <folder> --config Release
```

The tests (AppTests, OverlayIniTests) run the test-hook build in `proxy\build` instead, configured with
`-DSCSK_VERSION=0.0.0-internal.0`: that turns on SCSK_TEST_HOOKS, and it is never shipped.

These replace the 1.4.6 builds (`1.4.6-exo.1`), carrying Exo's own change (the recorder log's count of creates over
20 ms) plus upstream 1.4.7 and 1.4.8's `proxy\` updates. A version other than `0.0.0-internal*` leaves
`SCSK_TEST_HOOKS` out, as a release's does. `native\x86\d3d9.dll` comes from the Win32 folder, everything else from the
x64 one. publish-service.ps1 copies them into engine\out\service in the app's layout.

Not code-signed: SCSKiller's signature came with its release, and these are Exo's builds.

| File | SHA-256 | From |
| --- | --- | --- |
| native/VkLayer_scskiller.dll | cab0198f515bfcffa2d9abd6aff3536ce21fd1d424b8f249e4dd8b63450c9dd4 | built here |
| native/d3d12.dll | 6d6c5572ab7fcd38082b841165abf6d89862487e329df099ab72d066eb23c172 | built here |
| native/d3d9.dll | d05793faf988d1e9edebfb07ec82990fa7dd6b81455a9dca8af92a1a55e063f2 | built here |
| native/fossilize-replay.exe | c712bdbffb66214a1d7b52fc1ddfb1d18939c655eba23a1f1b0dee04954c3cc6 | built here |
| native/preload/dinput8.dll | 9064901cc955203fe1b74a17352d0ecb2960a322eac2fbb8e6f4e1e0a5518bd2 | built here |
| native/preload/version.dll | 5ddb48a38b7bb41ece182cf5fbf8dd3fa4f4adfed0dbde48d161bfd0a7c75d26 | built here |
| native/preload/winmm.dll | 39e1bdf0aa115620bd614045b3ec392d273f67c0f9d2e5a183c0095486bd2969 | built here |
| native/scskiller_ags.dll | 86f4339c1594801d7b7b7202d39898936b69b6eae7a53e4ae1525947243b3721 | built here |
| native/scskiller_warm.exe | 505520e9af8443614a0d1a5906235aa1c11008063d4f2a6f03dbeb8c9bdbf803 | built here |
| native/segheap/scskiller_warm.exe | 6f98c74e7b04fe713a19ff05239e7f79f95645eff5b9edd113eb13b3effcdfa8 | built here |
| native/x86/d3d9.dll | 36aedfb16e2621983cfcb82198795912d6024a3567a8085ec4fdc81b211238b2 | built here |
| native/amd_ags_x64.dll | 482b241ea855fdd59aaa7687c385ce4165b5340857e6a0e6d15ba27ecb65ee70 | AMD's, unchanged |
| zlib-ng2.dll | 454be2f3d10f804ace577198401431db5e95d0286b59589bc28a40085388e7c2 | SCSKiller 1.4.2's release, unchanged since 1.3.0 |
