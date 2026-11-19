# Store windows

How Exo keeps a store client (Steam, Epic, Riot, GOG and the others) out of sight while it uses it, and what it never hides. Code: `ExoLauncher\Adapters\StoreWindowRules.cs` (the rules), `StoreWindowEngine.cs` (what is done to a window), `StoreWindowSeam.cs` (the Win32 calls behind `IStoreWindows` and `IStoreProcesses`), `StoreWindowHider.cs` (the hook and the sweep), `StoreClientIdentity.cs`, `SuspensionBook.cs`, `PromptAwareDeadline.cs`.

## A store is its program, by path

A process is a store's client only when its file name is in that store's list **and** its executable's full path is under that client's install folder (`StoreClientIdentity.Of`, `BelongsTo`; the folders come from the adapters' own locators). A program whose path cannot be read is not the store: it is left alone and the log says so once per program name. The close paths (`ProcessHelper.TryCloseStoreClient`, `IsStoreClientRunning`) use the same test.

Never touched, whatever folder they sit in: anti-cheat programs (`StoreClientActivity.AntiCheatProcessNames`, the EasyAntiCheat and BattlEye prefixes), Riot's protected processes (`RiotCli.IsProtectedProcess`) and Riot's games.

## What kind of window it is

`StoreWindowRules.Table`, first row that matches wins. Titles are used only for the prompt word list, never matched exactly. A test walks every row.

| Row | Kind | What the hider does |
| --- | --- | --- |
| zero size, or an input-method class (`IME`, `MSCTFIME UI`) | Other | left alone |
| owned by another window | Prompt | never hidden |
| a prompt word in the title (licence, agreement, EULA, terms, sign in, log in, password, verify, two-factor, 2FA, authenticator, security code, captcha, payment, purchase, checkout, redeem, activate, register, confirm; whole words, any case) | Prompt | never hidden |
| dialog style (class `#32770`, a dialog frame without a caption, or the modal-frame style) | Prompt | never hidden |
| at least 640x400, no owner, not a dialog | Shell | hidden while the store is kept quiet; its extended style is put back exactly when shown |
| small, no owner, never takes focus, and topmost or a tool window | Toast | hidden |
| small, no owner, takes keyboard focus | Prompt | never hidden |
| anything else | Prompt | never hidden |

When the rules cannot tell, a window is a prompt: showing a notification is a smaller fault than hiding a sign-in. A prompt an older pass hid is shown again with its own style; a prompt is brought to the front once per window, and only while Exo keeps that client quiet.

Exo has no verified list of each client's window classes. The first time the hider sees a (client, class, kind) it writes one line to app.log (`Store windows: <Store> window class "<class>" is a <Kind>.`); titles are never logged. That log is how the list gets learned.

## A launch waiting on a prompt

While a Riot launch waits on the client and a prompt of that client is on screen, the wait's time limit stands still (`PromptAwareDeadline`, for up to 10 minutes in all; the stretch between two looks counts as the player's when a prompt was up at either end) and the launch status says `Riot needs you to finish something in its window.` Steam's and Epic's waits are unchanged.

## Suspensions end when their reason ends

`HiddenStoreRuntime.Suspend(store, reason, upTo)` returns a handle; disposing it ends the suspension unless another one for the same store is open (`SuspensionBook`). The time limit is only the backstop.

- Callers that stay for the whole job hold the handle for the method: uninstall, the Epic install watch, Steam's uninstall confirmation, GOG removal, Riot uninstall.
- Callers that open the client for the player and leave (Epic's update check, hand-off, uninstall and remove; Steam's and Riot's reveal; Riot's fallback install) use `HoldUntilNextLaunch`: it ends when the client is seen not running, when Exo starts another launch through that store, or at the time limit.
- The player opening the client is a 30-second hold renewed while its main window is visible.
- The Epic update's 3-hour suspension is gone: it switched the hider off for a job that says it runs without showing the launcher, and a prompt is never hidden anyway.

## How often it looks

The event hook is the fast path. The sweep (`SweepCadence.Next`) runs every 250 ms for the first 10 seconds after a client's process starts or the hider is started for a launch, then every 2 seconds while a client runs, and stops when none does.

## Not proven

Nothing here has run against real store clients on screen. Until a verification run: a client's splash or loading window that is small and takes focus counts as a prompt (it stays visible and pauses a Riot launch's time limit); which classes each client really uses is only what the log has collected.
