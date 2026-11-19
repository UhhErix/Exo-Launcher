# Store quiet

## What it is

One switch, "store clients stay quiet" (`storeClientsQuiet` in settings, `AppSettings.StoreClientsQuiet`, on by default and synced with the account). On, Exo keeps the store clients' own notifications, sounds and pop-ups off; off, it puts every value back as it was. Only Steam and Epic have a writer today. Turning the switch off or on at `saveSettingsPatch` runs the Steam pass and Epic's hand-off at once. There is no component yet (item G).

## What Exo writes

Steam, per account under `userdata/<id>/` (`StoreQuiet.cs`), only while Steam is closed (Steam holds both files in memory and writes them as it exits):

- `7/remote/sharedconfig.vdf` › `UserRoamingConfigStore`: `DisableAllToasts`, `DisableToastsInGame`, `PlaySoundOnToast`, the achievement, controller and signed-in-user toast and sound keys, and the friends list's JSON (`FriendsUIJSON`: every notification and sound off, taskbar flashing off).
- `config/localconfig.vdf` › `UserLocalConfigStore`: the `friends` notification and sound keys and `ChatFlashMode`, `system` screenshot toast and sound, `news › NotifyAvailableGames`, and in `WebStorage` the controller dialogs Steam puts before a game (`StoreQuiet.LaunchDialogs`).

Not written, for lack of a confirmed key: marketing messages and family-sharing notifications. The audit names settings `enable_marketing_messages` and `show_family_sharing_notifications`, but no file in the repo or fixture holds a key by those or any other name, and the audit says the stored names are unknown. A guessed key is worse than a gap: they stay owed until the keys are read from a real `localconfig.vdf` after the switch is changed in Steam.

Epic (`EpicQuietSettings.cs`): `settings/app-settings.json` in its data folder: `enableNotifications` and `showWhatsNewOnStartup` off (held at every start), `performanceProfile.preset` `default` to `lowResources` and `verboseLogs` off (set once, so a choice the player makes later in Epic stays theirs). Written only while Epic is closed.

Steam's per-game overlay is a different switch (`game.properties.set`, `SteamOverlayConfig`): off unless the game needs it (`SteamOverlayConfig.Needed`: Steam purchases, online play with friends, Remote Play Together, Steam Input); a choice the player makes in Properties is kept over the rule and across passes and Steam's own rewrites of the file.

## What it keeps

- Steam: `keep.json` › `Stores["steam/<account id>"].Held`, one entry per value by file, section path and key: the text as the file had it (escapes kept), or "absent" with how many of the value's sections the file had. Per PC, in the backup, never synced (the `keep.json` row in `PlayerData.cs`). It is recorded BEFORE the first write of each value, and a value whose record could not be saved is not written. A second write never replaces it: Off returns what the player had, not Exo's own value or one Steam put back.
- Epic: `epic-quiet.json` (this PC, in the backup registry), `{ "state": "intended" | "done", "before": { key: value }, "off": bool }`. The order is: the record as "intended" (what each key held), then Epic's settings, then the settings read back, then the record "done". A crash between leaves a record whose undo only touches a key that holds what Exo wrote, so a write that never happened is undone as nothing. The file as an earlier Exo made it (the keys and what they held) reads as done. The record is also the marker that quiet is wanted.

Epic's quiet is wanted when the switch is on and Epic's start with Windows was handed to Exo (`StartupHandedToExo` holds `epic`). Otherwise Epic's settings are not touched, no marker is made, and a marker an earlier Exo made without asking is undone at the next start.

## When it writes again

Every Steam pass that finds Steam closed compares the values with what Exo wants (`StoreQuietPass.Pending`, `Run`) and writes again whatever Steam, a Steam update or Steam Cloud changed back; each write is read back from the disk and a value that did not take is logged by key and returned. A pass that finds Steam running and everything in place arms the 30 s check once, so a pass runs after Steam exits (Steam rewrites its files as it exits). These values have no switch in Steam the player would use while quiet is on, so a reset is a reset with a known cause and says nothing. Epic is checked as Exo starts it and when Exo starts.

## What Off does

Switching the switch off: Steam's recorded values go back exactly (a key that was absent is removed, with a section only Exo added) the next time Steam is closed, read back, and the record forgets only what was put back; while off, no pass writes a quiet value (`Run(root, wanted: false)` only puts back). Epic's keys go back only where they still hold what Exo wrote (a key the player changed since is theirs), now if Epic is closed, else before Exo next starts it (the record's `off` flag stops any quiet write meanwhile), and the record is removed. Exo's uninstall puts Epic's settings back the same way. It does not yet put Steam's back: that needs Steam's folder, which a test cannot read without touching this PC's.

## What is NOT covered, and why

GOG Galaxy, the EA app, Ubisoft Connect, Battle.net, the Riot Client and Xbox: Exo has no writer for any of their settings files, and their formats are not verified (BETA-BUILD-ORDER item 30 lists several as guessed). Writing a client's config in a guessed format can break that client. For them this wave delivers start with Windows off and kept off (the start-up guard, every client is in its catalog), their windows hidden and their prompts shown. Their switches are owed until each client's real settings file has been read on this PC (with plain CLI tools, by the orchestrator: a client's config can hold account names and tokens, so it is never handed to a gateway worker). Riot: only the Riot Client's own windows and its signed-in account; nothing of Vanguard or the games.

## Licences

Steam shows each game's EULA for the player to accept or decline. Play and Install are not consent: no settings pass writes accepted licence versions into Steam's files, including deferred passes after Steam closes. `SteamGameEulaPromptAutomator` leaves agreement dialogs alone and only handles non-agreement launch choices and Play anyway after a cloud-sync failure.

## Tests

`StoreQuietTests`, `StoreQuietKeepTests` (the Steam record, read back, written again, Off, nothing while off, the rest of the file), `EpicQuietKeepTests` (the marker, the order, Off, a key the player changed), `SteamLicenceTests` (licences, the per-game overlay across passes), `DisposableHostGuardTests` (every writer here is guarded). Steam's files are the fixtures and temp copies; Epic's is `Fixtures/Epic/app-settings.json`. What cannot be checked without a real client: that Steam and Epic take these values and read them as quiet, and what a Steam update or Steam Cloud really does to the files.
