# Rune Native Component Kit (`rune-ui`) & Exo Theme Integration

## Architecture

- **Generic upstream-friendly crate:** `crates/rune-ui` on `UhhErix/rune` (`feat/rune-kit-big-libs` -> `exo`, PR #5, pinned rev `c73c6f51c4a49b866e115c33cc77a8c99f2c7ad8`).
- **Exo theme layer:** `apps/launcher/native-ui/crates/exo-kit/src/ui_theme.rs` layers Void (`#000`), Goeist typography, Liquid Glass (`ExoGlass`), damped springs (`SPRING_PRESS/QUICK/BASE/MOVE/FLIGHT`), and `FocusRingPolicy::SubtleFill` (no white selection outline) on top of `rune_ui::UiTheme`.
- **Native gallery:** `examples/ui-gallery` (in `UhhErix/rune`) and `crates/exo-kit/examples/exo_gallery.rs` (Exo Void + Goeist + Liquid Glass gallery).

## Verified Reference Library Licenses (2026-11-24)

| Library | Repository | License | Porting Policy |
| --- | --- | --- | --- |
| HeroUI | `heroui-inc/heroui` | `Apache-2.0` | Patterns allowed with attribution (`THIRD_PARTY_NOTICES.md`) |
| shadcn/ui | `shadcn-ui/ui` | `MIT` | Patterns allowed with attribution |
| Radix UI Primitives | `radix-ui/primitives` | `MIT` | Accessibility, roving tabindex, focus trap & dismiss semantics allowed with attribution |
| React Aria | `adobe/react-spectrum` | `Apache-2.0` | Keyboard, calendar & range slider semantics allowed with attribution |
| cmdk | `pacocoursey/cmdk` | `MIT` | Fuzzy ranking, dynamic groups & nested pages allowed with attribution |
| Sonner | `emilkowalski/sonner` | `MIT` | Toast stack, hover expand, pause & swipe physics allowed with attribution |
| Vaul | `emilkowalski/vaul` | `MIT` | Drawer snap points, 1:1 drag, fling & background scale allowed with attribution |
| Magic UI | `magicuidesign/magicui` | `MIT` | Effect concepts allowed with attribution |
| React Bits | `DavidHDev/react-bits` | **MIT + Commons Clause v1.0** | **ZERO code ported.** All WGSL shaders and visual effects in `rune-ui::effects` are 100% clean-room original implementations. |
| Mantine | `mantinedev/mantine` | `MIT` | Input addons, pagination & calendar ergonomics allowed with attribution |
| Tremor | `tremorlabs/tremor` | `Apache-2.0` | Dashboard chart framing & KPI donut ergonomics allowed with attribution |
| Recharts | `recharts/recharts` | `MIT` | Line, area, bar, donut & sparkline composition allowed with attribution |
| Motion | `motiondivision/motion` | `MIT` | Layout, shared-element (`El::matched`) & gesture patterns allowed with attribution |

## Component & Module Coverage

1. **Behavior Primitives (`rune-ui::primitives`):** `FocusScope`, `RovingState` (`Horizontal`/`Vertical`/`Both`, skip disabled, gamepad D-pad), `TypeaheadBuffer` (multi-char + repeated-char cycling), LIFO `DismissStack`, `compute_position` (flip + shift + arrow + collision padding), ordered `PortalHost` (`LayerSlot`), `a11y` AccessKit helpers, unified Keyboard + Gamepad `NavCommand`.
2. **Core Controls (`rune-ui::controls`):** `UiButton` (6 variants, 4 sizes, loading, press scale), `UiInput`, `UiTextArea` (clearable, password toggle, char count, error), `UiSlider`, `RangeSlider` (ticks, 1:1 drag, keyboard, AccessKit), `UiSwitch`, tri-state `UiCheckbox` (`Unchecked`/`Checked`/`Indeterminate`), `UiRadioGroup` (classic & card tiles).
3. **Overlays & Feedback (`rune-ui::overlays`):** `UiSelect`, single/multi `UiCombobox` with removable chips, `DropdownMenu`, `ContextMenu`, `UiDialog`, `AlertDialog`, Vaul-physics `Sheet`/`Drawer` (snap points, rubber-band overdrag, velocity fling & dismiss, background scale), `UiPopover`, `UiTooltip` + `TooltipGroupState`, Sonner-physics `SonnerToaster` (6 variants, stack, swipe-to-dismiss), cmdk `CommandPalette` (`command_score`, dynamic groups, nested pages + Backspace pop).
4. **Navigation & Data (`rune-ui::components`):** `UiTabs` (`El::matched` sliding indicator), `UiAccordion` (`Single`/`Multiple`), `DataGrid` (sort, filter, paginate, tri-state select-all, striped, loading skeleton), pure-Rust `DatePicker` & `DateRangePicker`, `Avatar` & `AvatarGroup`, `UiBadge`, `Chip`, `Skeleton`, linear/circular `UiProgress`, `Pagination` (smart ellipsis), `Carousel` (drag/fling/loop).
5. **Motion (`rune-ui::motion`):** `MotionBox`, `MotionPreset` (reduced-motion fade downgrade), `StaggerList`, `SharedElement`, `shared_indicator`, `DragGestureState` (elastic bounds + velocity fling).
6. **Clean-Room wgpu Effects (`rune-ui::effects`):** `UiEffectsCompositor` + WGSL shaders (`Aurora`, `Particles`, `Waves`, `Grid`, `Iridescence`, `BorderBeam`, `GlowBorder`, `Spotlight` rim), text effects (`BlurInText`, `SplitText`, `ScrambleText`, `GradientText`, `ShinyText`), `NumberTicker`, `ClickSpark`, `Marquee`. Every effect has a zero-GPU static fallback and pauses completely (`animating() == false`, 0% idle CPU) when `game_running = true`.
7. **Charts (`rune-ui::charts`):** `LineChart` (Fritsch-Carlson monotone cubic spline + crosshair + tooltip), `AreaChart` (stacked/overlapping), `BarChart` (grouped/stacked, vertical/horizontal), `DonutChart` (center KPI + hover pop-out + interactive legend), `Sparkline` (`Line`/`Area`/`Bar`).
