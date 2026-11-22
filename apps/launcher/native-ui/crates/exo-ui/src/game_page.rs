use crate::state::{AppState, title_hue};
use exo_kit::{
    BG, FG, PRESS_SCALE, PRIMARY_FG, RADIUS_CARD, RADIUS_PANEL, RADIUS_PILL, SPRING_QUICK,
    amoled_card, danger_button, exo_play_button, fg_muted, float_line, glass_button,
    glass_control_fill, glass_icon_circle, glass_rim_bottom, glass_rim_top, good, hairline,
    hover_fill, icon_chevron_down, icon_chevron_left, icon_chevron_right, icon_download,
    icon_folder, icon_play, icon_stop, icon_store, icon_trash, icon_wrench, pressed_fill,
    primary_button, scaled, surface_card, switch_pill, type_body, type_control, type_first_run,
    type_section, type_small, type_title,
};
use exo_rpc::Game;
use rune::{El, SurfaceFit, TextStyle, col, edit_access, el, oklch, row, text, text_input};
use std::time::{SystemTime, UNIX_EPOCH};

const GAME_EDGE: f32 = 32.0;
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

fn parse_ymd_days(s: &str) -> Option<i64> {
    if s.len() < 10 {
        return None;
    }
    let y: i64 = s[0..4].parse().ok()?;
    let m: i64 = s[5..7].parse().ok()?;
    let d: i64 = s[8..10].parse().ok()?;
    let y_adj = y - if m <= 2 { 1 } else { 0 };
    let era = y_adj.div_euclid(400);
    let yoe = y_adj - era * 400;
    let doy = (153 * (m + if m > 2 { -3 } else { 9 }) + 2) / 5 + d - 1;
    let doe = yoe * 365 + yoe / 4 - yoe / 100 + doy;
    Some(era * 146097 + doe - 719468)
}

fn format_last_played(raw: Option<&str>) -> String {
    let Some(s) = raw.filter(|v| !v.is_empty()) else {
        return "Never".into();
    };
    if let (Some(day), Ok(dur)) = (
        parse_ymd_days(s),
        SystemTime::now().duration_since(UNIX_EPOCH),
    ) {
        let today = (dur.as_secs() as i64) / 86_400;
        let diff = today - day;
        if diff <= 0 {
            return "Today".into();
        }
        if diff == 1 {
            return "Yesterday".into();
        }
        if diff < 7 {
            return format!("{diff} days ago");
        }
    }
    if s.len() >= 10 {
        s[..10].to_string()
    } else {
        s.to_string()
    }
}

fn format_storage(game: &Game) -> String {
    if let Some(bytes) = game.size_bytes.filter(|&b| b > 0) {
        let gb = bytes as f64 / 1_073_741_824.0;
        if gb >= 10.0 {
            return format!("{:.0} GB", gb);
        }
        if gb >= 1.0 {
            return format!("{:.1} GB", gb);
        }
        let mb = bytes as f64 / 1_048_576.0;
        return format!("{:.0} MB", mb.max(1.0));
    }
    if game.installed {
        "Installed".into()
    } else {
        "—".into()
    }
}

fn capitalize_first(raw: &str) -> String {
    let mut chars = raw.chars();
    match chars.next() {
        Some(first) => format!("{}{}", first.to_uppercase(), chars.as_str()),
        None => String::new(),
    }
}

fn perf_line(title: &str, subtitle: Option<(String, rune::Color)>, right: Option<El>) -> El {
    let mut left = col()
        .grow(1.0)
        .min_w(0.0)
        .gap(3.0)
        .child(text(title.to_string()).font(type_section()).color(FG));
    if let Some((sub, col_c)) = subtitle {
        left = left.child(text(sub).font(type_small()).color(col_c).truncate());
    }
    el().w_full()
        .py(6.0)
        .px(20.0)
        .border_sides([1.0, 0.0, 0.0, 0.0], float_line())
        .child(
            row()
                .w_full()
                .min_h(44.0)
                .py(6.0)
                .gap(12.0)
                .justify_between()
                .center_y()
                .child(left)
                .children(right),
        )
}

fn props_row(title: &str, subtitle: Option<(String, rune::Color)>, right: Option<El>) -> El {
    let mut left = col()
        .grow(1.0)
        .min_w(0.0)
        .gap(3.0)
        .child(text(title.to_string()).font(type_control()).color(FG));
    if let Some((sub, col_c)) = subtitle {
        left = left.child(text(sub).font(type_small()).color(col_c).truncate());
    }
    el().w_full()
        .py(14.0)
        .px(20.0)
        .border_sides([1.0, 0.0, 0.0, 0.0], hairline())
        .child(
            row()
                .w_full()
                .min_h(38.0)
                .gap(16.0)
                .justify_between()
                .center_y()
                .child(left)
                .children(right),
        )
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
        .h(32.0)
        .px(12.0)
        .gap(6.0)
        .center_y()
        .radius(RADIUS_PILL)
        .fill(glass_control_fill())
        .border_gradient(1.0, glass_rim_top(), glass_rim_bottom())
        .child(text("Borderless").font(type_control()).color(FG))
        .child(icon_chevron_down(12.0, fg_muted()));

    amoled_card(RADIUS_PANEL)
        .grow(1.15)
        .min_w(0.0)
        .child(
            el().w_full()
                .pt(15.0)
                .px(20.0)
                .pb(12.0)
                .child(text("Performance & graphics").font(type_title()).color(FG)),
        )
        .child(perf_line(
            "Upscalers",
            Some(upscalers_sub),
            Some(icon_chevron_right(14.0, fg_muted())),
        ))
        .child(perf_line(
            "Frame generation",
            Some(("Built into the game".into(), fg_muted())),
            None,
        ))
        .child(perf_line("Optimize", Some(opt_sub), Some(borderless_pill)))
}

fn properties_card(s: &AppState, game: &Game) -> El {
    let props = s.game_props.as_ref();
    let overlay_on = props.and_then(|p| p.steam_overlay).unwrap_or(false);
    let language = props
        .and_then(|p| p.language.as_deref())
        .filter(|v| !v.is_empty())
        .map(capitalize_first)
        .unwrap_or_else(|| "English".to_string());
    let beta = props
        .and_then(|p| p.beta.as_deref())
        .filter(|v| !v.is_empty())
        .unwrap_or("None");
    let folder = props
        .and_then(|p| p.folder.as_deref())
        .filter(|v| !v.is_empty())
        .unwrap_or("Managed by store");

    let tab_bar =
        row()
            .px(20.0)
            .pb(12.0)
            .gap(6.0)
            .flex_wrap()
            .children(PROPS_TABS.iter().enumerate().map(|(i, label)| {
                let active = s.game_props_tab == i;
                let key = if i == 0 {
                    "gp-props".to_string()
                } else {
                    format!("gp-prop-tab-{i}")
                };
                let mut btn = el()
                    .key(key)
                    .role("tab")
                    .label(*label)
                    .focusable(true)
                    .hit()
                    .press_on_down()
                    .press_scale(PRESS_SCALE)
                    .py(6.0)
                    .px(12.0)
                    .center()
                    .radius(RADIUS_PILL)
                    .fill(if active { FG } else { glass_control_fill() })
                    .hover(if active { FG } else { hover_fill() })
                    .press(if active { FG } else { pressed_fill() })
                    .motion(SPRING_QUICK)
                    .on_press(move |s: &mut AppState| {
                        s.game_props_tab = i;
                        s.props_open = false;
                    })
                    .child(
                        text(*label)
                            .font(TextStyle::new(scaled(12.0), 600.0))
                            .color(if active { PRIMARY_FG } else { fg_muted() }),
                    );
                if !active {
                    btn = btn.border_gradient(1.0, glass_rim_top(), glass_rim_bottom());
                }
                btn
            }));

    let mut card = amoled_card(RADIUS_PANEL)
        .grow(1.0)
        .min_w(340.0)
        .child(
            el().w_full()
                .pt(15.0)
                .px(20.0)
                .pb(12.0)
                .child(text("Properties").font(type_title()).color(FG)),
        )
        .child(tab_bar);

    match s.game_props_tab {
        0 => {
            let gid_save = game.id.clone();
            let gid_overlay = game.id.clone();
            let launch_input = row()
                .w(220.0)
                .h(32.0)
                .px(12.0)
                .center_y()
                .radius(RADIUS_PILL)
                .fill(glass_control_fill())
                .border_gradient(1.0, glass_rim_top(), glass_rim_bottom())
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
                .h(32.0)
                .px(12.0)
                .gap(6.0)
                .center_y()
                .radius(RADIUS_PILL)
                .fill(glass_control_fill())
                .border_gradient(1.0, glass_rim_top(), glass_rim_bottom())
                .child(text(language).font(type_control()).color(FG))
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
                .child(props_row("Launch options", None, Some(launch_input)))
                .child(props_row("Language", None, Some(lang_pill)))
                .child(props_row("Steam overlay", None, Some(overlay_sw)));
        }
        1 => {
            let gid_folder = game.id.clone();
            let gid_repair = game.id.clone();
            let gid_uninst = game.id.clone();
            let size_label = format_storage(game);

            card = card
                .child(props_row(
                    "Install folder",
                    Some((folder.to_string(), fg_muted())),
                    Some(
                        glass_button("gp-props-browse", "Browse", Some(icon_folder(13.0, FG)))
                            .on_press(move |s: &mut AppState| s.open_game_folder(&gid_folder)),
                    ),
                ))
                .child(props_row(
                    "Integrity",
                    Some((size_label, fg_muted())),
                    Some(
                        glass_button("gp-repair", "Verify files", Some(icon_wrench(13.0, FG)))
                            .on_press(move |s: &mut AppState| s.repair_game(&gid_repair)),
                    ),
                ))
                .child(props_row(
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
            card = card.child(props_row(
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
            card = card.child(props_row("DLC & expansions", Some((msg, fg_muted())), None));
        }
        _ => {
            let gid_save = game.id.clone();
            card = card.child(props_row(
                "Custom artwork",
                Some(("Using official store cover & hero art".into(), fg_muted())),
                Some(primary_button("gp-art-refresh", "Refresh", None).on_press(
                    move |s: &mut AppState| {
                        let _ = &gid_save;
                        s.refresh_library();
                    },
                )),
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
        .pin(Some(0.0), Some(0.0), Some(0.0), Some(0.0))
        .clip_rounded(0.0)
        .fill(BG);
    if let Some(ref img) = hero {
        hero_bg = hero_bg.pixels_fit(img.pixels(), SurfaceFit::Fill);
    } else {
        hero_bg =
            hero_bg.linear_gradient(0.3, [(0.0, oklch(0.32, 0.14, hue).alpha(0.65)), (1.0, BG)]);
    }

    let hero_fade_v = el()
        .pin(Some(0.0), Some(0.0), Some(0.0), Some(0.0))
        .linear_gradient(
            std::f32::consts::PI,
            [
                (0.0, BG.alpha(0.30)),
                (0.20, BG.alpha(0.0)),
                (0.72, BG.alpha(0.48)),
                (1.0, BG),
            ],
        );

    let hero_fade_h = el()
        .pin(Some(0.0), Some(0.0), Some(0.0), Some(0.0))
        .linear_gradient(
            std::f32::consts::FRAC_PI_2,
            [
                (0.0, BG.alpha(0.62)),
                (0.42, BG.alpha(0.20)),
                (0.76, BG.alpha(0.0)),
            ],
        );

    let back_btn = row()
        .key("gp-back")
        .role("button")
        .label("Library")
        .focusable(true)
        .hit()
        .press_on_down()
        .press_scale(PRESS_SCALE)
        .h(34.0)
        .pl(10.0)
        .pr(14.0)
        .gap(6.0)
        .center_y()
        .radius(RADIUS_PILL)
        .fill(glass_control_fill())
        .hover(hover_fill())
        .press(pressed_fill())
        .border_gradient(1.0, glass_rim_top(), glass_rim_bottom())
        .motion(SPRING_QUICK)
        .on_press(|s: &mut AppState| s.close_game_page())
        .child(icon_chevron_left(16.0, FG))
        .child(text("Library").font(type_control()).color(FG));

    let store_pill = row()
        .h(34.0)
        .px(12.0)
        .gap(8.0)
        .center_y()
        .radius(RADIUS_PILL)
        .fill(glass_control_fill())
        .border_gradient(1.0, glass_rim_top(), glass_rim_bottom())
        .child(icon_store(14.0, fg_muted()))
        .child(
            text(game.store_display())
                .font(type_control())
                .color(fg_muted()),
        );

    let top_row = row()
        .w_full()
        .justify_between()
        .center_y()
        .gap(12.0)
        .child(back_btn)
        .child(store_pill);

    let mut poster = el()
        .w(148.0)
        .h(222.0)
        .radius(RADIUS_CARD)
        .continuous_corners()
        .clip_rounded(RADIUS_CARD)
        .fill(surface_card())
        .shadow(0.0, 20.0, BG.alpha(0.75));
    if let Some(ref img) = cover {
        poster = poster.pixels_fit(img.pixels(), SurfaceFit::Fill);
    } else {
        poster = poster.linear_gradient(
            0.35,
            [(0.0, oklch(0.46, 0.16, hue)), (1.0, oklch(0.12, 0.03, hue))],
        );
    }

    let id_primary = game.id.clone();
    let primary_btn = if game.is_running && game.can_stop {
        exo_play_button("gp-stop", "Stop", Some(icon_stop(14.0, PRIMARY_FG)))
            .min_w(196.0)
            .h(44.0)
            .px(22.0)
            .on_press(move |s: &mut AppState| s.stop_game(&id_primary))
    } else if game.update_available {
        exo_play_button("gp-update", "Update", Some(icon_download(14.0, PRIMARY_FG)))
            .min_w(196.0)
            .h(44.0)
            .px(22.0)
            .on_press(move |s: &mut AppState| s.update_game(&id_primary))
    } else if game.installed {
        exo_play_button("gp-play", "Play", Some(icon_play(14.0, PRIMARY_FG)))
            .min_w(196.0)
            .h(44.0)
            .px(22.0)
            .on_press(move |s: &mut AppState| s.launch_game(&id_primary))
    } else {
        exo_play_button(
            "gp-install",
            "Install",
            Some(icon_download(14.0, PRIMARY_FG)),
        )
        .min_w(196.0)
        .h(44.0)
        .px(22.0)
        .on_press(move |s: &mut AppState| s.install_game(&id_primary))
    };

    let id_folder = game.id.clone();
    let folder_btn = glass_icon_circle("gp-folder", "Open folder", 44.0, icon_folder(16.0, FG))
        .on_press(move |s: &mut AppState| s.open_game_folder(&id_folder));

    let identity = if let Some(logo) = s.logo_image(game, scale) {
        row().h(112.0).items_end().child(
            el().max_w(440.0)
                .max_h(112.0)
                .w(400.0)
                .h(96.0)
                .pixels_fit(logo.pixels(), SurfaceFit::Fit),
        )
    } else {
        row().h(112.0).items_end().child(
            text(game.title.clone())
                .font(type_first_run())
                .color(FG)
                .truncate(),
        )
    };

    let left_identity = row().gap(24.0).items_end().child(poster).child(
        col().gap(16.0).child(identity).child(
            row()
                .gap(10.0)
                .center_y()
                .child(primary_btn)
                .children(game.installed.then_some(folder_btn)),
        ),
    );

    let played_str = format_played(game.playtime_minutes);
    let last_played_str = format_last_played(game.last_played_utc.as_deref());
    let storage_str = format_storage(game);
    let (ach_str, ach_pct) = match s.game_extras.as_ref() {
        Some(ex) if ex.achievements_total.unwrap_or(0) > 0 => {
            let u = ex.achievements_unlocked.unwrap_or(0);
            let t = ex.achievements_total.unwrap_or(1).max(1);
            (format!("{u} / {t}"), (u as f32 / t as f32).clamp(0.0, 1.0))
        }
        _ => ("—".into(), 0.0),
    };

    let stat_col = |val: String, label: &'static str, bar: Option<f32>, divider: bool| {
        let mut c = col()
            .px(22.0)
            .gap(3.0)
            .child(text(val).font(type_title()).color(FG))
            .child(
                text(label)
                    .font(TextStyle::new(scaled(12.0), 500.0))
                    .color(fg_muted()),
            );
        if let Some(p) = bar {
            let fill_w = (76.0 * p).max(if p > 0.0 { 4.0 } else { 0.0 });
            c = c.child(
                el().w(76.0)
                    .h(3.0)
                    .mt(4.0)
                    .radius(2.0)
                    .fill(float_line())
                    .child(el().w(fill_w).h(3.0).radius(2.0).fill(FG)),
            );
        }
        if divider {
            c = c.border_sides([0.0, 0.0, 0.0, 1.0], float_line());
        }
        c
    };

    let stats_card = row()
        .py(14.0)
        .px(4.0)
        .radius(RADIUS_PANEL)
        .continuous_corners()
        .fill(glass_control_fill())
        .border_gradient(1.0, glass_rim_top(), glass_rim_bottom())
        .child(stat_col(played_str, "Played", None, false))
        .child(stat_col(last_played_str, "Last played", None, true))
        .child(stat_col(storage_str, "Storage", None, true))
        .child(stat_col(ach_str, "Achievements", Some(ach_pct), true));

    let hero_bar = row()
        .w_full()
        .justify_between()
        .items_end()
        .gap(32.0)
        .child(left_identity)
        .child(stats_card);

    let stage = el()
        .w_full()
        .min_h(460.0)
        .clip_rounded(0.0)
        .fill(BG)
        .child(hero_bg)
        .child(hero_fade_v)
        .child(hero_fade_h)
        .child(
            col()
                .w_full()
                .min_h(460.0)
                .pt(56.0)
                .px(GAME_EDGE)
                .pb(24.0)
                .justify_between()
                .gap(20.0)
                .child(top_row)
                .child(hero_bar),
        );

    let lower_cards = row()
        .w_full()
        .px(GAME_EDGE)
        .gap(16.0)
        .items_start()
        .child(performance_card(s, game))
        .child(properties_card(s, game));

    col()
        .w_full()
        .gap(14.0)
        .pb(24.0)
        .fill(BG)
        .child(stage)
        .child(lower_cards)
}
