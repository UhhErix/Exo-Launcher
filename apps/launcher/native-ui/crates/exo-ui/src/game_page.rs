use crate::state::{AppState, title_hue};
use exo_kit::{
    BG, FG, PRESS_SCALE, PRIMARY_FG, RADIUS_PANEL, RADIUS_PILL, SPRING_QUICK,
    TRANSPARENT, amoled_card, card_border, danger_button, exo_play_button, fg_muted, glass_button,
    glass_control_fill, glass_icon_circle, glass_rim, glass_rim_bottom, glass_rim_top, good,
    hairline, hover_fill, icon_chevron_down, icon_chevron_left, icon_chevron_right, icon_download,
    icon_folder, icon_play, icon_stop, icon_store, icon_trash, icon_wrench, pressed_fill,
    primary_button, switch_pill, type_body, type_control, type_small,
};
use exo_rpc::Game;
use rune::{El, SurfaceFit, TextStyle, col, edit_access, el, oklch, row, text, text_input};

const PROPS_TABS: [&str; 5] = ["General", "Installed files", "Betas", "DLC", "Artwork"];

fn format_played(minutes: u64) -> String {
    if minutes == 0 {
        return "Never".into();
    }
    let hrs = minutes / 60;
    let mins = minutes % 60;
    if hrs == 0 {
        format!("{mins} min")
    } else if mins == 0 {
        format!("{hrs} hr")
    } else {
        format!("{hrs} hr {mins} min")
    }
}

fn format_last_played(raw: Option<&str>) -> String {
    let Some(s) = raw.filter(|v| !v.is_empty()) else {
        return "Never".into();
    };
    if s.len() >= 10 {
        s[..10].to_string()
    } else {
        s.to_string()
    }
}

fn card_row(title: &str, subtitle: Option<(String, rune::Color)>, right: Option<El>) -> El {
    let mut left = col()
        .grow(1.0)
        .min_w(0.0)
        .gap(3.0)
        .child(text(title.to_string()).font(TextStyle::new(13.0, 600.0)).color(FG));
    if let Some((sub, col_c)) = subtitle {
        left = left.child(text(sub).font(type_small()).color(col_c).truncate());
    }
    row()
        .w_full()
        .px(20.0)
        .py(15.0)
        .gap(16.0)
        .justify_between()
        .center_y()
        .child(left)
        .children(right)
}

fn performance_card(s: &AppState, game: &Game) -> El {
    let upscalers_sub = if s.settings.auto_update_upscalers {
        ("Up to date: FSR, XeSS, DLSS".to_string(), good())
    } else {
        ("Automatic updates off".to_string(), fg_muted())
    };
    let opt_sub = if game.installed {
        ("Applied".to_string(), good())
    } else {
        ("Available once installed".to_string(), fg_muted())
    };

    let borderless_pill = row()
        .h(30.0)
        .px(12.0)
        .gap(6.0)
        .center_y()
        .radius(RADIUS_PILL)
        .fill(glass_control_fill())
        .border_gradient(1.0, glass_rim_top(), glass_rim_bottom())
        .child(text("Borderless").font(type_control()).color(FG))
        .child(icon_chevron_down(12.0, fg_muted()));

    amoled_card(RADIUS_PANEL)
        .grow(1.0)
        .min_w(0.0)
        .child(
            el().w_full().px(20.0).pt(18.0).pb(10.0).child(
                text("Performance & graphics")
                    .font(TextStyle::new(16.0, 650.0))
                    .color(FG),
            ),
        )
        .child(card_row(
            "Upscalers",
            Some(upscalers_sub),
            Some(icon_chevron_right(14.0, fg_muted())),
        ))
        .child(el().w_full().h(1.0).fill(hairline()))
        .child(card_row(
            "Frame generation",
            Some(("Built into the game".into(), fg_muted())),
            None,
        ))
        .child(el().w_full().h(1.0).fill(hairline()))
        .child(card_row("Optimize", Some(opt_sub), Some(borderless_pill)))
}

fn properties_card(s: &AppState, game: &Game) -> El {
    let props = s.game_props.as_ref();
    let overlay_on = props.and_then(|p| p.steam_overlay).unwrap_or(false);
    let language = props
        .and_then(|p| p.language.as_deref())
        .filter(|v| !v.is_empty())
        .unwrap_or("English");
    let beta = props
        .and_then(|p| p.beta.as_deref())
        .filter(|v| !v.is_empty())
        .unwrap_or("None");
    let folder = props
        .and_then(|p| p.folder.as_deref())
        .filter(|v| !v.is_empty())
        .unwrap_or("Managed by store");

    let tab_bar = row().px(20.0).pb(14.0).gap(6.0).flex_wrap().children(
        PROPS_TABS.iter().enumerate().map(|(i, label)| {
            let active = s.game_props_tab == i;
            let key = if i == 0 {
                "gp-props".to_string()
            } else {
                format!("gp-prop-tab-{i}")
            };
            el().key(key)
                .role("tab")
                .label(*label)
                .focusable(true)
                .hit()
                .press_on_down()
                .press_scale(PRESS_SCALE)
                .h(28.0)
                .px(12.0)
                .center()
                .radius(RADIUS_PILL)
                .fill(if active { FG } else { TRANSPARENT })
                .hover(if active { FG } else { hover_fill() })
                .press(if active { FG } else { pressed_fill() })
                .border(if active { 0.0 } else { 1.0 }, glass_rim())
                .motion(SPRING_QUICK)
                .on_press(move |s: &mut AppState| {
                    s.game_props_tab = i;
                    s.props_open = false;
                })
                .child(
                    text(*label)
                        .font(TextStyle::new(12.0, if active { 600.0 } else { 500.0 }))
                        .color(if active { PRIMARY_FG } else { FG }),
                )
        }),
    );

    let mut card = amoled_card(RADIUS_PANEL)
        .grow(1.0)
        .min_w(0.0)
        .child(
            el().w_full().px(20.0).pt(18.0).pb(12.0).child(
                text("Properties")
                    .font(TextStyle::new(16.0, 650.0))
                    .color(FG),
            ),
        )
        .child(tab_bar);

    match s.game_props_tab {
        0 => {
            let gid_save = game.id.clone();
            let gid_overlay = game.id.clone();
            let launch_input = row()
                .w(196.0)
                .h(32.0)
                .px(12.0)
                .center_y()
                .radius(RADIUS_PILL)
                .fill(BG)
                .border(1.0, glass_rim())
                .child(
                    text_input(&s.props_launch_opts, type_body())
                        .key("props-launch-input")
                        .label("Launch options")
                        .w_full()
                        .single_line()
                        .color(FG)
                        .placeholder("", fg_muted())
                        .on_edit(edit_access(|s: &mut AppState| &mut s.props_launch_opts))
                        .on_submit(move |s: &mut AppState, _| {
                            let val = s.props_launch_opts.text.clone();
                            s.set_game_property(&gid_save, "launchOptions", &val);
                        }),
                );

            let lang_pill = row()
                .h(30.0)
                .px(12.0)
                .gap(6.0)
                .center_y()
                .radius(RADIUS_PILL)
                .fill(glass_control_fill())
                .border_gradient(1.0, glass_rim_top(), glass_rim_bottom())
                .child(text(language.to_string()).font(type_control()).color(FG))
                .child(icon_chevron_down(12.0, fg_muted()));

            let overlay_sw = switch_pill("props-overlay-switch", "Steam overlay", overlay_on)
                .on_press(move |s: &mut AppState| {
                    s.set_game_property(
                        &gid_overlay,
                        "steamOverlay",
                        if overlay_on { "false" } else { "true" },
                    );
                });

            card = card
                .child(card_row("Launch options", None, Some(launch_input)))
                .child(el().w_full().h(1.0).fill(hairline()))
                .child(card_row("Language", None, Some(lang_pill)))
                .child(el().w_full().h(1.0).fill(hairline()))
                .child(card_row("Steam overlay", None, Some(overlay_sw)));
        }
        1 => {
            let gid_folder = game.id.clone();
            let gid_repair = game.id.clone();
            let gid_uninst = game.id.clone();
            let size_label = game
                .size_label()
                .unwrap_or_else(|| if game.installed { "Installed".into() } else { "Not installed".into() });

            card = card
                .child(card_row(
                    "Install folder",
                    Some((folder.to_string(), fg_muted())),
                    Some(
                        glass_button("gp-props-browse", "Browse", Some(icon_folder(13.0, FG)))
                            .on_press(move |s: &mut AppState| s.open_game_folder(&gid_folder)),
                    ),
                ))
                .child(el().w_full().h(1.0).fill(hairline()))
                .child(card_row(
                    "Integrity",
                    Some((size_label, fg_muted())),
                    Some(
                        glass_button("gp-repair", "Verify files", Some(icon_wrench(13.0, FG)))
                            .on_press(move |s: &mut AppState| s.repair_game(&gid_repair)),
                    ),
                ))
                .child(el().w_full().h(1.0).fill(hairline()))
                .child(card_row(
                    "Remove game",
                    Some(("Uninstall from this PC".into(), fg_muted())),
                    Some(if s.uninstall_confirm {
                        danger_button(
                            "gp-uninstall-confirm",
                            "Confirm uninstall",
                            Some(icon_trash(13.0, PRIMARY_FG)),
                        )
                        .on_press(move |s: &mut AppState| s.uninstall_game(&gid_uninst))
                    } else {
                        glass_button("gp-uninstall", "Uninstall", Some(icon_trash(13.0, FG)))
                            .on_press(|s: &mut AppState| s.uninstall_confirm = true)
                    }),
                ));
        }
        2 => {
            card = card.child(card_row(
                "Beta participation",
                Some((beta.to_string(), fg_muted())),
                None,
            ));
        }
        3 => {
            let dlc_count = game.includes.len();
            let msg = if dlc_count > 0 {
                format!("{dlc_count} included titles / add-ons")
            } else {
                "Managed by store client".into()
            };
            card = card.child(card_row("DLC & expansions", Some((msg, fg_muted())), None));
        }
        _ => {
            let gid_save = game.id.clone();
            card = card.child(card_row(
                "Custom artwork",
                Some(("Using official store cover & hero art".into(), fg_muted())),
                Some(
                    primary_button("gp-art-refresh", "Refresh", None)
                        .on_press(move |s: &mut AppState| {
                            let _ = &gid_save;
                            s.refresh_library();
                        }),
                ),
            ));
        }
    }

    card
}

pub fn view(s: &AppState, game: &Game, scale: f32) -> El {
    let hue = title_hue(&game.title);
    let cover = s.cover_image(game, scale);
    let hero = s.hero_image(game, scale);

    let mut hero_bg = el()
        .pin(Some(0.0), Some(0.0), Some(0.0), None)
        .w_full()
        .h(370.0);
    if let Some(ref img) = hero {
        hero_bg = hero_bg.pixels_fit(img.pixels(), SurfaceFit::Fill);
    } else {
        hero_bg = hero_bg.linear_gradient(
            0.3,
            [
                (0.0, oklch(0.32, 0.14, hue).alpha(0.65)),
                (1.0, BG),
            ],
        );
    }

    let hero_fade_v = el()
        .pin(Some(0.0), Some(0.0), Some(0.0), None)
        .w_full()
        .h(370.0)
        .linear_gradient(
            std::f32::consts::PI,
            [
                (0.0, BG.alpha(0.30)),
                (0.20, BG.alpha(0.04)),
                (0.58, BG.alpha(0.42)),
                (0.84, BG.alpha(0.86)),
                (1.0, BG),
            ],
        );

    let hero_fade_h = el()
        .pin(Some(0.0), Some(0.0), Some(0.0), None)
        .w_full()
        .h(370.0)
        .linear_gradient(
            std::f32::consts::FRAC_PI_2,
            [
                (0.0, BG.alpha(0.70)),
                (0.28, BG.alpha(0.14)),
                (0.72, BG.alpha(0.10)),
                (1.0, BG.alpha(0.66)),
            ],
        );

    let top_row = row()
        .w_full()
        .justify_between()
        .center_y()
        .child(
            glass_button("gp-back", "Library", Some(icon_chevron_left(14.0, FG)))
                .on_press(|s: &mut AppState| s.close_game_page()),
        )
        .child(
            row()
                .h(32.0)
                .px(12.0)
                .gap(6.0)
                .center_y()
                .radius(RADIUS_PILL)
                .fill(glass_control_fill())
                .border_gradient(1.0, glass_rim_top(), glass_rim_bottom())
                .child(icon_store(13.0, FG))
                .child(text(game.store_display()).font(type_control()).color(FG)),
        );

    let mut poster = el()
        .w(116.0)
        .h(174.0)
        .radius(12.0)
        .continuous_corners()
        .clip_rounded(12.0)
        .shadow(0.0, 20.0, BG.alpha(0.75));
    if let Some(ref img) = cover {
        poster = poster.pixels_fit(img.pixels(), SurfaceFit::Fill);
    } else {
        poster = poster.linear_gradient(
            0.35,
            [
                (0.0, oklch(0.46, 0.16, hue)),
                (1.0, oklch(0.12, 0.03, hue)),
            ],
        );
    }

    let id_primary = game.id.clone();
    let primary_btn = if game.is_running && game.can_stop {
        exo_play_button("gp-stop", "Stop", Some(icon_stop(13.0, PRIMARY_FG)))
            .w(188.0)
            .h(42.0)
            .on_press(move |s: &mut AppState| s.stop_game(&id_primary))
    } else if game.update_available {
        exo_play_button("gp-update", "Update", Some(icon_download(14.0, PRIMARY_FG)))
            .w(188.0)
            .h(42.0)
            .on_press(move |s: &mut AppState| s.update_game(&id_primary))
    } else if game.installed {
        exo_play_button("gp-play", "Play", Some(icon_play(13.0, PRIMARY_FG)))
            .w(188.0)
            .h(42.0)
            .on_press(move |s: &mut AppState| s.launch_game(&id_primary))
    } else {
        exo_play_button("gp-install", "Install", Some(icon_download(14.0, PRIMARY_FG)))
            .w(188.0)
            .h(42.0)
            .on_press(move |s: &mut AppState| s.install_game(&id_primary))
    };

    let id_folder = game.id.clone();
    let folder_btn = glass_icon_circle("gp-folder", "Open folder", 42.0, icon_folder(16.0, FG))
        .on_press(move |s: &mut AppState| s.open_game_folder(&id_folder));

    let left_identity = row()
        .gap(20.0)
        .items_end()
        .child(poster)
        .child(
            col()
                .gap(14.0)
                .pb(4.0)
                .child(
                    text(game.title.clone())
                        .font(TextStyle::new(26.0, 700.0))
                        .color(FG)
                        .truncate(),
                )
                .child(
                    row()
                        .gap(10.0)
                        .center_y()
                        .child(primary_btn)
                        .children(game.installed.then_some(folder_btn)),
                ),
        );

    let played_str = format_played(game.playtime_minutes);
    let last_played_str = format_last_played(game.last_played_utc.as_deref());
    let storage_str = game
        .size_label()
        .unwrap_or_else(|| if game.installed { "Installed".into() } else { "—".into() });
    let (ach_str, ach_pct) = match s.game_extras.as_ref() {
        Some(ex) if ex.achievements_total.unwrap_or(0) > 0 => {
            let u = ex.achievements_unlocked.unwrap_or(0);
            let t = ex.achievements_total.unwrap_or(1).max(1);
            (format!("{u} / {t}"), (u as f32 / t as f32).clamp(0.0, 1.0))
        }
        _ => ("—".into(), 0.0),
    };

    let stat_col = |val: String, label: &'static str, bar: Option<f32>| {
        let mut c = col()
            .gap(3.0)
            .child(text(val).font(TextStyle::new(15.0, 650.0)).color(FG))
            .child(text(label).font(TextStyle::new(11.0, 400.0)).color(fg_muted()));
        if let Some(p) = bar {
            let fill_w = (72.0 * p).max(if p > 0.0 { 4.0 } else { 0.0 });
            c = c.child(
                el().w(72.0)
                    .h(3.0)
                    .mt(3.0)
                    .radius(2.0)
                    .fill(FG.alpha(0.14))
                    .child(el().w(fill_w).h(3.0).radius(2.0).fill(FG)),
            );
        }
        c
    };

    let stats_card = row()
        .px(20.0)
        .py(14.0)
        .gap(26.0)
        .radius(16.0)
        .continuous_corners()
        .fill(BG.alpha(0.76))
        .border(1.0, card_border())
        .child(stat_col(played_str, "Played", None))
        .child(stat_col(last_played_str, "Last played", None))
        .child(stat_col(storage_str, "Storage", None))
        .child(stat_col(ach_str, "Achievements", Some(ach_pct)));

    let hero_bar = row()
        .w_full()
        .pt(76.0)
        .justify_between()
        .items_end()
        .gap(20.0)
        .child(left_identity)
        .child(stats_card);

    let lower_cards = row()
        .w_full()
        .gap(16.0)
        .items_start()
        .child(performance_card(s, game))
        .child(properties_card(s, game));

    let content = col()
        .w_full()
        .pt(60.0)
        .px(26.0)
        .pb(36.0)
        .gap(24.0)
        .child(top_row)
        .child(hero_bar)
        .child(lower_cards);

    el().w_full()
        .child(hero_bg)
        .child(hero_fade_v)
        .child(hero_fade_h)
        .child(content)
}
