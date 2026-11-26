#![forbid(unsafe_code)]

use exo_kit::rune_ui::charts::{
    AreaChart, BarChart, ChartSeries, DonutChart, DonutSlice, LineChart, Sparkline, SparklineKind,
};
use exo_kit::rune_ui::components::{
    AccordionMode, AccordionState, Avatar, AvatarGroup, Carousel, CarouselState, Chip, CivilDate,
    DataGrid, DataGridColumn, DataGridRow, DataGridState, DatePicker, DatePickerState, Pagination,
    Skeleton, TabVariant, Tone, UiAccordion, UiAccordionItem, UiBadge, UiBadgeVariant, UiProgress,
    UiTabItem, UiTabs,
};
use exo_kit::rune_ui::controls::{
    CheckState, RangeSlider, SliderMark, UiButton, UiButtonVariant, UiCheckbox, UiInput,
    UiRadioGroup, UiRadioOption, UiSlider, UiSwitch,
};
use exo_kit::rune_ui::effects::{
    BackgroundEffect, BackgroundKind, BorderEffect, EffectPalette, GradientText, Marquee,
    NumberTicker, ScrambleText, ShinyText, UiEffectsCompositor,
};
use exo_kit::rune_ui::overlays::{
    SonnerState, SonnerToast, SonnerToaster, ToastPosition, ToastVariant, UiCombobox,
    UiComboboxOption, UiComboboxState, UiSelect, UiSelectOption, UiSelectState,
};
use exo_kit::rune_ui::primitives::portal::PortalHost;
use exo_kit::{
    BG, FG, FONT_FAMILIES, FONTS, RADIUS_CARD, exo_effect_gate, exo_ui_theme, float_line, good,
    hex, surface_card, type_control, type_page, type_small,
};
use rune::{AppSpec, Color, Cx, El, TextEdit, col, el, row, spacer, text};

struct ExoGallery {
    tab: String,
    game_running: bool,
    name: TextEdit,
    slider: f32,
    range: (f32, f32),
    hdr: bool,
    check: CheckState,
    power: String,
    select_val: Option<String>,
    select_state: UiSelectState,
    combo_state: UiComboboxState,
    accordion: AccordionState,
    grid: DataGridState,
    date: DatePickerState,
    page: usize,
    carousel: CarouselState,
    toasts: SonnerState,
}

impl ExoGallery {
    fn new() -> Self {
        let mut combo_state = UiComboboxState::multi();
        combo_state.selected = vec!["dlss".into(), "hdr".into()];
        let mut accordion = AccordionState::new(AccordionMode::Single { collapsible: true });
        accordion.toggle("glass");
        let mut toasts = SonnerState::new(ToastPosition::BottomRight);
        toasts.push(
            SonnerToast::new("Exo Native Kit")
                .description("Void + Geist + Liquid Glass on wgpu DX12")
                .variant(ToastVariant::Success),
            None,
            0.0,
        );
        Self {
            tab: "overview".into(),
            game_running: false,
            name: TextEdit::new("Exo v4 Native"),
            slider: 72.0,
            range: (60.0, 240.0),
            hdr: true,
            check: CheckState::Indeterminate,
            power: "balanced".into(),
            select_val: Some("dx12".into()),
            select_state: UiSelectState::new(),
            combo_state,
            accordion,
            grid: DataGridState {
                page_size: 4,
                ..DataGridState::default()
            },
            date: DatePickerState::new(CivilDate {
                year: 2026,
                month: 11,
                day: 24,
            }),
            page: 2,
            carousel: CarouselState::new(3),
            toasts,
        }
    }
}

fn card(title: &str, body: El) -> El {
    col()
        .w_full()
        .padding(16.0)
        .gap(12.0)
        .radius(RADIUS_CARD)
        .fill(surface_card())
        .border(1.0, float_line())
        .child(text(title.to_owned()).font(type_control()).color(FG))
        .child(body)
}

fn view(s: &ExoGallery, _cx: &mut Cx<ExoGallery>) -> El {
    let theme = exo_ui_theme(s.game_running);
    let mut portals = PortalHost::new();

    let nav = UiTabs::new(
        "exo-gal-tabs",
        &s.tab,
        vec![
            UiTabItem::new("overview", "Overview"),
            UiTabItem::new("controls", "Controls"),
            UiTabItem::new("effects", "Effects"),
            UiTabItem::new("charts", "Charts"),
        ],
    )
    .variant(TabVariant::Segmented)
    .on_change(|g: &mut ExoGallery, id| g.tab = id)
    .build(&theme);

    let header = row()
        .w_full()
        .justify_between()
        .center_y()
        .child(
            row()
                .gap(10.0)
                .center_y()
                .child(text("Exo Native Kit").font(type_page()).color(FG))
                .child(
                    UiBadge::new("Void + Geist + Liquid Glass")
                        .tone(Tone::Success)
                        .variant(UiBadgeVariant::Dot)
                        .build(&theme),
                ),
        )
        .child(nav)
        .child(spacer());

    let controls_col = col()
        .w(560.0)
        .gap(12.0)
        .child(card(
            "Controls",
            col()
                .w_full()
                .gap(10.0)
                .child(
                    row()
                        .w_full()
                        .gap(8.0)
                        .wrap()
                        .child(
                            UiButton::new("Play")
                                .variant(UiButtonVariant::Primary)
                                .build(&theme),
                        )
                        .child(
                            UiButton::new("Properties")
                                .variant(UiButtonVariant::Secondary)
                                .build(&theme),
                        )
                        .child(
                            UiButton::new("Outline")
                                .variant(UiButtonVariant::Outline)
                                .build(&theme),
                        )
                        .child(
                            UiButton::new("Ghost")
                                .variant(UiButtonVariant::Ghost)
                                .build(&theme),
                        )
                        .child(
                            UiButton::new("Uninstall")
                                .variant(UiButtonVariant::Danger)
                                .build(&theme),
                        ),
                )
                .child(
                    UiInput::new("exo-name", &s.name)
                        .label("Profile")
                        .clearable(true)
                        .on_edit(|g: &mut ExoGallery| &mut g.name)
                        .on_clear(|g: &mut ExoGallery| g.name.clear())
                        .build(&theme),
                )
                .child(
                    row()
                        .w_full()
                        .gap(16.0)
                        .child(
                            UiSlider::new("exo-vol", s.slider, 0.0, 100.0)
                                .label("Master volume")
                                .show_value("%")
                                .marks([
                                    SliderMark::new(0.0),
                                    SliderMark::new(50.0),
                                    SliderMark::new(100.0),
                                ])
                                .on_change(|g: &mut ExoGallery, v| g.slider = v)
                                .build(&theme),
                        )
                        .child(
                            RangeSlider::new("exo-fps", s.range.0, s.range.1, 0.0, 240.0)
                                .label("FPS window")
                                .on_change(|g: &mut ExoGallery, r| g.range = r)
                                .build(&theme),
                        ),
                )
                .child(
                    row()
                        .w_full()
                        .gap(16.0)
                        .items_start()
                        .child(
                            col()
                                .gap(10.0)
                                .child(
                                    UiSwitch::new("exo-hdr", s.hdr)
                                        .label("Auto HDR")
                                        .on_toggle(|g: &mut ExoGallery| g.hdr = !g.hdr)
                                        .build(&theme),
                                )
                                .child(
                                    UiCheckbox::new("exo-cb", s.check)
                                        .label("Sync shaders")
                                        .on_change(|g: &mut ExoGallery, c| g.check = c)
                                        .build(&theme),
                                ),
                        )
                        .child(
                            UiRadioGroup::new("exo-pwr", &s.power)
                                .horizontal()
                                .options([
                                    UiRadioOption::new("quiet", "Quiet"),
                                    UiRadioOption::new("balanced", "Balanced"),
                                    UiRadioOption::new("max", "Max"),
                                ])
                                .on_change(|g: &mut ExoGallery, v| g.power = v)
                                .build(&theme),
                        ),
                ),
        ))
        .child(card(
            "Pickers, DataGrid & Display",
            col()
                .w_full()
                .gap(10.0)
                .child(
                    row()
                        .w_full()
                        .gap(8.0)
                        .wrap()
                        .child(
                            UiSelect::new("exo-sel", s.select_val.clone())
                                .width(150.0)
                                .options(vec![
                                    UiSelectOption::new("dx12", "Direct3D 12"),
                                    UiSelectOption::new("vk", "Vulkan 1.3"),
                                ])
                                .open(&s.select_state)
                                .on_toggle(|g: &mut ExoGallery| {
                                    g.select_state.open = !g.select_state.open
                                })
                                .on_close(|g: &mut ExoGallery| g.select_state.close())
                                .on_select(|g: &mut ExoGallery, v| {
                                    g.select_val = Some(v);
                                    g.select_state.close();
                                })
                                .build(&theme, &mut portals),
                        )
                        .child(
                            UiCombobox::new("exo-combo", &s.combo_state)
                                .width(205.0)
                                .options(vec![
                                    UiComboboxOption::new("dlss", "DLSS 4"),
                                    UiComboboxOption::new("hdr", "HDR10"),
                                    UiComboboxOption::new("vrr", "VRR"),
                                ])
                                .bind_state(|g: &mut ExoGallery| &mut g.combo_state)
                                .build(&theme, &mut portals),
                        )
                        .child(
                            DatePicker::new("exo-dp", &s.date)
                                .width(150.0)
                                .bind_state(|g: &mut ExoGallery| &mut g.date)
                                .build(&theme, &mut portals),
                        ),
                )
                .child(
                    row()
                        .w_full()
                        .gap(12.0)
                        .center_y()
                        .child(Avatar::new("Erix").status(Tone::Success).build(&theme))
                        .child(
                            AvatarGroup::new(vec![
                                Avatar::new("A"),
                                Avatar::new("B"),
                                Avatar::new("C"),
                                Avatar::new("D"),
                            ])
                            .max(3)
                            .build(&theme),
                        )
                        .child(Chip::new("Steam").selected(true).build(&theme))
                        .child(
                            UiProgress::circular(40.0, 0.82)
                                .tone(Tone::Success)
                                .build(&theme),
                        )
                        .child(Skeleton::text_lines(110.0, 2).build(&theme)),
                )
                .child(
                    DataGrid::new("exo-grid", &s.grid)
                        .columns(vec![
                            DataGridColumn::new("t", "Game").sortable(),
                            DataGridColumn::new("h", "Hours").width(76.0).sortable(),
                            DataGridColumn::new("f", "FPS").width(64.0).sortable(),
                        ])
                        .rows(vec![
                            DataGridRow::new(
                                "1",
                                vec!["Elden Ring".into(), "142".into(), "120".into()],
                            ),
                            DataGridRow::new(
                                "2",
                                vec!["Cyberpunk 2077".into(), "96".into(), "138".into()],
                            ),
                            DataGridRow::new(
                                "3",
                                vec!["Hades II".into(), "64".into(), "240".into()],
                            ),
                        ])
                        .selectable(true)
                        .striped(true)
                        .bind_state(|g: &mut ExoGallery| &mut g.grid)
                        .build(&theme),
                )
                .child(
                    Pagination::new("exo-pg", s.page, 8)
                        .on_change(|g: &mut ExoGallery, p| g.page = p)
                        .build(&theme),
                ),
        ));

    let cats: Vec<String> = ["Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"]
        .into_iter()
        .map(String::from)
        .collect();

    let fx_tile = |id: u16, label: &str, kind: BackgroundKind| {
        el().size(185.0, 92.0)
            .radius(RADIUS_CARD)
            .fill(BG)
            .border(1.0, float_line())
            .child(
                BackgroundEffect::new(format!("ebg-{id}"), id, kind)
                    .radius(RADIUS_CARD)
                    .build(&theme),
            )
            .child(
                text(label.to_owned())
                    .font(type_small())
                    .color(FG)
                    .absolute(10.0, 8.0),
            )
    };

    let right_col = col()
        .grow(1.0)
        .shrink()
        .gap(12.0)
        .child(card(
            "Clean-Room wgpu Effects (zero-GPU fallback + game pause)",
            col()
                .w_full()
                .gap(10.0)
                .child(
                    row()
                        .w_full()
                        .gap(10.0)
                        .wrap()
                        .child(fx_tile(1, "Aurora", BackgroundKind::Aurora))
                        .child(fx_tile(2, "Particles", BackgroundKind::Particles))
                        .child(fx_tile(3, "Waves", BackgroundKind::Waves))
                        .child(fx_tile(4, "Grid", BackgroundKind::Grid))
                        .child(fx_tile(5, "Iridescence", BackgroundKind::Iridescence))
                        .child(
                            el().size(185.0, 92.0)
                                .radius(RADIUS_CARD)
                                .fill(BG)
                                .center()
                                .child(
                                    BorderEffect::beam("ebeam", 6)
                                        .radius(RADIUS_CARD)
                                        .build(&theme),
                                )
                                .child(text("BorderBeam").font(type_small()).color(FG)),
                        ),
                )
                .child(
                    row()
                        .w_full()
                        .gap(16.0)
                        .center_y()
                        .child(
                            GradientText::new(
                                "Gradient Title",
                                vec![FG, good(), hex(0x60a5fa, 1.0)],
                            )
                            .font(type_page())
                            .build(&theme),
                        )
                        .child(
                            ShinyText::new("Shiny Specular", 0.9)
                                .font(type_page())
                                .build(&theme),
                        )
                        .child(
                            ScrambleText::new("escram", "240HZ NATIVE", 1.0)
                                .font(type_control())
                                .build(&theme),
                        )
                        .child(
                            NumberTicker::new("etick", 240.0)
                                .suffix(" FPS")
                                .font(type_page())
                                .build(&theme),
                        ),
                )
                .child(
                    Marquee::new("emarq", 760.0, 520.0, 1.2, || {
                        [
                            "Void",
                            "Geist",
                            "Liquid Glass",
                            "Radix",
                            "Vaul",
                            "Sonner",
                            "cmdk",
                            "Tremor",
                        ]
                        .into_iter()
                        .map(|l| {
                            UiBadge::new(l)
                                .variant(UiBadgeVariant::Outline)
                                .build(&theme)
                        })
                        .collect()
                    })
                    .build(&theme),
                ),
        ))
        .child(card(
            "Telemetry Charts (Line, Area, Bar, Donut, Sparkline)",
            col()
                .w_full()
                .gap(10.0)
                .child(
                    row()
                        .w_full()
                        .gap(12.0)
                        .wrap()
                        .child(
                            LineChart::new(
                                "elc",
                                cats.clone(),
                                vec![
                                    ChartSeries::new(
                                        "FPS",
                                        vec![120.0, 132.0, 141.0, 148.0, 144.0, 160.0, 165.0],
                                    ),
                                    ChartSeries::new(
                                        "1% Low",
                                        vec![84.0, 90.0, 96.0, 102.0, 98.0, 112.0, 118.0],
                                    ),
                                ],
                            )
                            .size(410.0, 185.0)
                            .hovered(Some(3))
                            .unit(" fps")
                            .build(&theme),
                        )
                        .child(
                            AreaChart::area_chart(
                                "eac",
                                cats.clone(),
                                vec![
                                    ChartSeries::new(
                                        "VRAM",
                                        vec![4.2, 4.9, 5.4, 5.8, 5.6, 6.2, 6.0],
                                    ),
                                    ChartSeries::new("RT", vec![1.2, 1.5, 1.8, 2.0, 1.9, 2.2, 2.1]),
                                ],
                            )
                            .stacked(true)
                            .size(410.0, 185.0)
                            .unit(" GB")
                            .build(&theme),
                        ),
                )
                .child(
                    row()
                        .w_full()
                        .gap(12.0)
                        .wrap()
                        .center_y()
                        .child(
                            BarChart::new(
                                "ebc",
                                cats,
                                vec![ChartSeries::new(
                                    "Hours",
                                    vec![2.1, 3.4, 1.9, 4.5, 5.2, 6.0, 4.8],
                                )],
                            )
                            .size(360.0, 175.0)
                            .unit("h")
                            .build(&theme),
                        )
                        .child(
                            DonutChart::new(
                                "edc",
                                vec![
                                    DonutSlice::new("Steam", 640.0),
                                    DonutSlice::new("Epic", 210.0),
                                    DonutSlice::new("GOG", 145.0),
                                ],
                            )
                            .diameter(148.0)
                            .center_kpi("995 GB", "Games")
                            .unit(" GB")
                            .build(&theme),
                        )
                        .child(
                            col()
                                .gap(8.0)
                                .child(
                                    Sparkline::new(vec![54.0, 58.0, 62.0, 64.0, 63.0, 66.0, 61.0])
                                        .kind(SparklineKind::Area)
                                        .build(&theme),
                                )
                                .child(
                                    Sparkline::new(vec![7.2, 6.8, 6.5, 6.4, 6.7, 6.2, 6.0])
                                        .kind(SparklineKind::Line)
                                        .build(&theme),
                                )
                                .child(
                                    Sparkline::new(vec![40.0, 72.0, 95.0, 118.0, 102.0, 115.0])
                                        .kind(SparklineKind::Bar)
                                        .build(&theme),
                                ),
                        ),
                ),
        ))
        .child(
            row()
                .w_full()
                .gap(12.0)
                .child(
                    col().w(380.0).child(card(
                        "Accordion",
                        UiAccordion::new("eacc", &s.accordion)
                            .item(UiAccordionItem::new(
                                "glass",
                                "Liquid Glass tokens",
                                text("Floats and controls read ExoGlass; page stays Void #000.")
                                    .font(type_small())
                                    .color(FG.alpha(0.72))
                                    .w_full()
                                    .wrap(),
                            ))
                            .bind_state(|g: &mut ExoGallery| &mut g.accordion)
                            .build(&theme),
                    )),
                )
                .child(
                    col().grow(1.0).shrink().child(card(
                        "Carousel",
                        Carousel::new(
                            "ecar",
                            &s.carousel,
                            vec![
                                el().w_full()
                                    .h_full()
                                    .center()
                                    .fill(Color {
                                        r: 0.08,
                                        g: 0.12,
                                        b: 0.20,
                                        a: 1.0,
                                    })
                                    .child(
                                        text("Featured · Native Rune Kit")
                                            .font(type_control())
                                            .color(FG),
                                    ),
                                el().w_full()
                                    .h_full()
                                    .center()
                                    .fill(Color {
                                        r: 0.12,
                                        g: 0.08,
                                        b: 0.20,
                                        a: 1.0,
                                    })
                                    .child(
                                        text("Shaders · Zero-GPU Fallback")
                                            .font(type_control())
                                            .color(FG),
                                    ),
                                el().w_full()
                                    .h_full()
                                    .center()
                                    .fill(Color {
                                        r: 0.06,
                                        g: 0.16,
                                        b: 0.14,
                                        a: 1.0,
                                    })
                                    .child(
                                        text("Charts · Native Vector")
                                            .font(type_control())
                                            .color(FG),
                                    ),
                            ],
                        )
                        .size(380.0, 104.0)
                        .bind_state(|g: &mut ExoGallery| &mut g.carousel)
                        .build(&theme),
                    )),
                ),
        );

    SonnerToaster::new("exo-sonner", &s.toasts)
        .bind_state(|g: &mut ExoGallery| &mut g.toasts, |_, _| {})
        .build(&theme, &mut portals);

    let root = col()
        .w_full()
        .h_full()
        .padding(18.0)
        .gap(14.0)
        .fill(BG)
        .child(header)
        .child(
            row()
                .w_full()
                .gap(14.0)
                .items_start()
                .child(controls_col)
                .child(right_col),
        );

    portals.mount(root)
}

fn main() {
    let comp = UiEffectsCompositor::new(|s: &ExoGallery| exo_effect_gate(s.game_running, true))
        .with_palette(EffectPalette {
            primary: hex(0x60a5fa, 1.0),
            secondary: good(),
            tertiary: hex(0xc084fc, 1.0),
        });
    let mut spec = AppSpec::new(
        "Exo Native Kit Gallery",
        (1920.0, 1080.0),
        ExoGallery::new,
        view,
    );
    spec.fonts = FONTS;
    spec.families = FONT_FAMILIES;
    rune::run_with(spec, comp);
}
