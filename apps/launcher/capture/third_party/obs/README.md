# OBS Studio game capture files

Exo Capture uses OBS Studio's graphics hook to take a game's frames from inside the game, the way OBS's Game Capture does. These are OBS's own release files, unchanged and signed by OBS Project, LLC, taken from `OBS-Studio-33.0.3-Windows-x64.zip` (`core/win-capture/data`):

| File | SHA-256 |
|---|---|
| graphics-hook64.dll | 1261cf476829354a3831e97bc77b9af6a1340832e3ed4e079dc2b98b79857d2a |
| graphics-hook32.dll | e51b81645df3b06458547d9433eaa6b679b1e8feb214ec14ecdfeb23e2a34b66 |
| inject-helper64.exe | 4ecc338f9232817f19a3f0228bd3de4fb8bddabffa65a94b9227c02a1545bb0d |
| inject-helper32.exe | 2bcd432d538ef4a9999ff9be5be1cd9079dff94eb60c7e1ad0886bf6a822d122 |
| get-graphics-offsets64.exe | 46aa1bd9b246d5165a27b597b791b3b6d5e8bf6a1b4008f510bc9995cccebdac |
| get-graphics-offsets32.exe | d761a8abfc2afb8392d561766a06a7d44d072558c49996dc78952ca221b6ad13 |

OBS Studio is free software under the GNU General Public License, version 2 or later. Its source for these files is at https://github.com/obsproject/obs-studio/tree/33.0.3 (`plugins/win-capture`, `shared/obs-hook-config`, `shared/obs-inject-library`). `build.ps1` copies them to `out\obs`, and Exo's installer puts them in `addons\capture\obs`.

To update: take the same files from a newer OBS release, check each is signed by OBS Project, LLC, check `graphics-hook-ver.h` still has major version 1 and `hook_info` is still 648 bytes, and update the table.
