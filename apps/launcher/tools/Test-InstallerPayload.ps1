param([string]$EvidenceDirectory = (Join-Path $PSScriptRoot '..\artifacts\installer-fixtures'))
$ErrorActionPreference = 'Stop'
$root = Join-Path ([IO.Path]::GetTempPath()) ('exo-installer-fixture-' + [guid]::NewGuid().ToString('N'))
$payload = Join-Path $root "O'Brien profile\app.incoming-123"
[IO.Directory]::CreateDirectory((Join-Path $payload 'wwwroot')) | Out-Null
$required = @('ExoLauncher.exe', 'ExoLauncher.dll', 'ExoLauncher.deps.json', 'ExoLauncher.runtimeconfig.json', 'exo_apo.dll', 'exo-apo-helper.exe', 'wwwroot\index.html')
foreach ($relative in $required) { [IO.File]::WriteAllText((Join-Path $payload $relative), 'fixture-' + $relative) }
& (Join-Path $PSScriptRoot 'installer\New-PayloadManifest.ps1') -PayloadRoot $payload -Version '1.2.3' | Out-Null
. (Join-Path $PSScriptRoot 'installer\Verify-Payload.ps1') -DefinitionsOnly
. (Join-Path $PSScriptRoot 'installer\Close-ExoLauncher.ps1') -DefinitionsOnly
$checks = [Collections.Generic.List[string]]::new()
$null = Test-ExoPayload $payload; $checks.Add('complete payload accepted')
function MustReject([scriptblock]$Action, [string]$Name) {
    $rejected = $false
    try { & $Action | Out-Null } catch { $rejected = $true }
    if (-not $rejected) { throw "Fixture was not rejected: $Name" }
    $checks.Add($Name)
}
$file = Join-Path $payload 'ExoLauncher.dll'
$original = [IO.File]::ReadAllText($file)
[IO.File]::WriteAllText($file, $original.Replace('fixture', 'changed'))
MustReject { Test-ExoPayload $payload } 'same-size corruption rejected'
[IO.File]::WriteAllText($file, $original)
[IO.File]::Move($file, (Join-Path $root 'held.dll'))
MustReject { Test-ExoPayload $payload } 'missing DLL rejected'
[IO.File]::Move((Join-Path $root 'held.dll'), $file)
[IO.File]::WriteAllText((Join-Path $payload 'unexpected.dll'), 'unexpected')
MustReject { Test-ExoPayload $payload } 'unexpected DLL rejected'
[IO.File]::Delete((Join-Path $payload 'unexpected.dll'))
$null = Test-ExoPayload $payload
$apoFile = Join-Path $payload 'exo_apo.dll'
$apoOriginal = [IO.File]::ReadAllText($apoFile)
[IO.File]::WriteAllText($apoFile, $apoOriginal.Replace('fixture', 'tamperd'))
MustReject { Test-ExoPayload $payload } 'corrupted exo_apo.dll rejected'
[IO.File]::WriteAllText($apoFile, $apoOriginal)
[IO.File]::Move($apoFile, (Join-Path $root 'held-apo.dll'))
MustReject { Test-ExoPayload $payload } 'missing exo_apo.dll rejected'
[IO.File]::Move((Join-Path $root 'held-apo.dll'), $apoFile)
$null = Test-ExoPayload $payload
$installScripts = @(Get-Item (Join-Path $PSScriptRoot 'ExoLauncher.nsi')) + @(Get-ChildItem (Join-Path $PSScriptRoot 'installer') -Filter '*.ps1')
foreach ($scriptFile in $installScripts) {
    $scriptText = [IO.File]::ReadAllText($scriptFile.FullName)
    if ($scriptText -match 'regsvr32|MMDevices\\Audio|DisableProtectedAudioDG|exo-apo-helper\S*\s+register|--explicit-user-consent') {
        throw "Installer script $($scriptFile.Name) must never register exo_apo.dll during install."
    }
}
$checks.Add('installer scripts never register exo_apo.dll during install')
$data = Join-Path $root "O'Brien profile"
$profiles = @(Get-ExoProfileRoots (Join-Path $data 'app') $data)
foreach ($profile in $profiles) {
    if (-not (Test-ExoWebViewCommandLine ('msedgewebview2.exe --user-data-dir="' + $profile + '" --flag') $profiles)) { throw 'Owned profile was not matched.' }
}
if (Test-ExoWebViewCommandLine ('msedgewebview2.exe --user-data-dir="' + $profiles[0] + '-another-app"') $profiles) { throw 'Sibling profile was matched.' }
if (Test-ExoWebViewCommandLine ('msedgewebview2.exe --other="' + $profiles[0] + '"') $profiles) { throw 'Unrelated argument was matched.' }
$checks.Add('all current and legacy WebView profiles matched exactly')
$checks.Add('apostrophe and spaces remain literal paths')
$app = Join-Path $data 'app'
$hosted = Join-Path $app 'addons\lucid\Lucid.exe'
if (-not (Test-ExoHostedLucid $hosted $app)) { throw 'The Lucid in Exo was not matched.' }
if (-not (Test-ExoHostedLucid ($hosted.ToUpperInvariant()) $app)) { throw 'Capitals changed the Lucid in Exo.' }
if (-not (Test-ExoHostedLucid (Join-Path $app 'addons\..\addons\lucid\Lucid.exe') $app)) { throw 'A path with .. to the Lucid in Exo was not resolved.' }
foreach ($other in @(
    (Join-Path $data 'app.previous-dev-1\addons\lucid\Lucid.exe'),   # a backup that shares the folder's first letters
    (Join-Path $data 'app-old\addons\lucid\Lucid.exe'),
    (Join-Path $app 'addons\lucid\Lucid.exe.bak'),
    (Join-Path $app 'addons\lucid\sub\Lucid.exe'),
    (Join-Path $app 'addons\..\..\Lucid.exe'),                       # climbs out of the folder
    (Join-Path $root 'Documents\Lucid.exe'),                          # the user's own copy
    'Lucid.exe', '', $null)) {
    if (Test-ExoHostedLucid $other $app) { throw "A Lucid outside Exo's folder was matched: $other" }
}
$checks.Add("only the Lucid in Exo's folder is matched, not a copy beside it or a folder that shares its name")
# The newer Lucid Exo downloaded lives in its data folder, outside the app folder, and is Exo's too.
$downloaded = Join-Path $data 'addons\lucid-current\Lucid.exe'
if (-not (Test-ExoHostedLucid $downloaded $app $data)) { throw 'The downloaded Lucid was not matched.' }
if (Test-ExoHostedLucid $downloaded $app) { throw 'The downloaded Lucid was matched without the data folder.' }
foreach ($other in @((Join-Path $data 'addons\lucid-current-old\Lucid.exe'), (Join-Path $data 'addons\lucid-current\Lucid.previous.exe'), (Join-Path $data 'addons\lucid\Lucid.exe'), (Join-Path $root 'other\addons\lucid-current\Lucid.exe'))) {
    if (Test-ExoHostedLucid $other $app $data) { throw "A Lucid outside Exo's two folders was matched: $other" }
}
$checks.Add("the downloaded Lucid in the data folder is matched, nothing beside it")
New-Item -ItemType Directory -Path $EvidenceDirectory -Force | Out-Null
$result = @{ ok = $true; powershell = $PSVersionTable.PSVersion.ToString(); checks = @($checks); fixtureRoot = $root }
$result | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $EvidenceDirectory 'installer-payload-fixtures.json') -Encoding UTF8
$result | ConvertTo-Json -Depth 5
# Keep only the isolated fixture for inspection; no application or installer was executed.
