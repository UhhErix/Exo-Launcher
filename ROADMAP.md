# Rooms roadmap

These are the Launcher's Rooms: five tabs in the [top bar](apps/launcher/ui/src/components/shell/TitleBar.tsx), and Devices, a page of the PC room.
**Shipped** means the Room is present in 3.26.0, not that every feature works
with every store, service or device. See the [support matrix](apps/launcher/docs/SUPPORT_MATRIX.md)
for those limits.

[CODEOWNERS](.github/CODEOWNERS) has one repository-wide rule and no Room-specific
overrides, so every Room has the same code owner.

| Room | Code owner | Status | What's available |
| --- | --- | --- | --- |
| [Library](apps/launcher/docs/HELP.md#library) | @UhhErix | Shipped | Browse and launch games from one library; install and update where the store supports it. |
| [Store](apps/launcher/docs/HELP.md#store) | @UhhErix | Shipped | Browse Steam-ranked shelves, compare prices and track a wishlist. Purchases open in the official store. |
| [Discord](apps/launcher/docs/HELP.md#discord) | @UhhErix | Shipped | Exo's Discord client for servers, messages and calls. Requires a Discord account. |
| [Music](apps/launcher/docs/HELP.md#music) | @UhhErix | Shipped | Play Spotify, Apple Music and YouTube Music through their web players. Each service has its own sign-in and subscription requirements. |
| [PC](apps/launcher/docs/HELP.md#pc) | @UhhErix | Shipped | View hardware and manage supported drivers, display, HDR, upscalers and shaders. Available controls depend on the hardware. |
| [Devices](apps/launcher/docs/HELP.md#devices) | @UhhErix | Shipped; keyboard setup not yet available | Configure supported mice, audio devices and controllers. Keyboards and controllers Exo can't configure are listed without setup. |

Settings opens from the gear; Captures opens from its own button. Neither is a Room tab.
