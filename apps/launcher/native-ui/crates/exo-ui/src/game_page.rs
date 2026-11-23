use crate::state::{AppState, InputMode, OpenDropdown, title_hue};
use exo_kit::{
    BG, FG, PRESS_SCALE, PRIMARY_FG, RADIUS_CARD, RADIUS_PANEL, RADIUS_PILL, SPRING_BASE,
    SPRING_FLIGHT, SPRING_MOVE, SPRING_QUICK, SelectTrigger, TRANSPARENT, amoled_card, bad,
    exo_play_button, fg_muted, float_line, glass_button, glass_control_fill, glass_icon_circle,
    glass_rim_bottom, glass_rim_top, good, hairline, hover_fill, icon_chevron_left,
    icon_chevron_right, icon_download, icon_folder, icon_play, icon_stop, icon_trash, icon_wrench,
    pressed_fill, primary_button, scaled, select_trigger, store_logo, store_logo_or_fallback,
    surface_card, switch_pill, type_body, type_control, type_first_run, type_section, type_small,
    type_title,
};
use exo_rpc::Game;
use rune::{
    Cursor, El, SpringPair, SurfaceFit, TextStyle, Transition, col, edit_access, el, oklch, row,
    text, text_input,
};
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

    let display_label = match s.game_display_mode.as_str() {
        "fullscreen" => "Fullscreen",
        _ => "Borderless",
    };
    let borderless_pill = select_trigger(
        SelectTrigger::new("gp-display-mode-select", "Display mode", display_label)
            .open(s.open_dropdown == Some(OpenDropdown::GameDisplayMode))
            .gamepad_focus(s.input_mode == InputMode::Gamepad),
        |s: &mut AppState, _| s.toggle_dropdown(OpenDropdown::GameDisplayMode),
    );

    amoled_card(RADIUS_PANEL)
        .key("gp-perf-card")
        .grow(1.15)
        .min_w(0.0)
        .h_full()
        .child(
            el().w_full()
                .pt(18.0)
                .px(20.0)
                .pb(15.0)
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
    let gamepad_focus = s.input_mode == InputMode::Gamepad;

    let tab_bar =
        row()
            .px(20.0)
            .pb(12.0)
            .gap(4.0)
            .flex_wrap()
            .children(PROPS_TABS.iter().enumerate().map(|(i, label)| {
                let active = s.game_props_tab == i;
                let key = if i == 0 {
                    "gp-props".to_string()
                } else {
                    format!("gp-prop-tab-{i}")
                };
                let base = row().py(6.0).px(12.0).center().child(
                    text(*label)
                        .font(TextStyle::new(scaled(12.0), 600.0))
                        .color(fg_muted()),
                );
                let mut btn = el()
                    .key(key)
                    .role("tab")
                    .label(*label)
                    .focusable(true)
                    .hit()
                    .press_on_down()
                    .press_scale(PRESS_SCALE)
                    .radius(RADIUS_PILL)
                    .fill(TRANSPARENT)
                    .hover(if active { TRANSPARENT } else { hover_fill() })
                    .press(if active { TRANSPARENT } else { pressed_fill() })
                    .motion(SPRING_QUICK)
                    .on_press(move |s: &mut AppState| {
                        s.input_mode = InputMode::Pointer;
                        s.disarm_confirm();
                        s.game_props_tab = i;
                        s.props_open = false;
                    })
                    .child(base);
                if active {
                    btn = btn.child(
                        row()
                            .key("gp-props-pill")
                            .matched("gp-props-pill")
                            .matched_crop()
                            .motion(SPRING_MOVE)
                            .pin(Some(0.0), Some(0.0), Some(0.0), Some(0.0))
                            .py(6.0)
                            .px(12.0)
                            .center()
                            .radius(RADIUS_PILL)
                            .fill(FG)
                            .child(
                                text(*label)
                                    .font(TextStyle::new(scaled(12.0), 600.0))
                                    .color(PRIMARY_FG),
                            ),
                    );
                }
                if gamepad_focus {
                    btn = btn.focus_ring(2.0, 3.0, FG);
                }
                btn
            }));

    let card = amoled_card(RADIUS_PANEL)
        .key("gp-props-card")
        .grow(1.0)
        .min_w(340.0)
        .h_full()
        .child(
            el().w_full()
                .pt(18.0)
                .px(20.0)
                .pb(15.0)
                .child(text("Properties").font(type_title()).color(FG)),
        )
        .child(tab_bar);

    let mut body = col()
        .key(format!("gp-props-body-{}", s.game_props_tab))
        .w_full()
        .transition(Transition::opacity().springs(SpringPair::new(SPRING_QUICK, SPRING_QUICK)));

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
                .cursor(Cursor::Text)
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

            let lang_pill = select_trigger(
                SelectTrigger::new("gp-language-select", "Language", &language)
                    .open(s.open_dropdown == Some(OpenDropdown::GameLanguage))
                    .gamepad_focus(gamepad_focus),
                |s: &mut AppState, _| s.toggle_dropdown(OpenDropdown::GameLanguage),
            );

            let overlay_sw = switch_pill("props-overlay-switch", "Steam overlay", overlay_on)
                .on_press(move |s: &mut AppState| {
                    s.set_game_property(
                        &gid_overlay,
                        "steamOverlay",
                        if overlay_on { "false" } else { "true" },
                    );
                });

            let overlay_row = el()
                .w_full()
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
                        .child(
                            row()
                                .grow(1.0)
                                .min_w(0.0)
                                .gap(8.0)
                                .center_y()
                                .children(store_logo("steam", 15.0))
                                .child(text("Steam overlay").font(type_control()).color(FG)),
                        )
                        .child(overlay_sw),
                );

            body = body
                .child(props_row("Launch options", None, Some(launch_input)))
                .child(props_row("Language", None, Some(lang_pill)))
                .child(overlay_row);
        }
        1 => {
            let gid_folder = game.id.clone();
            let gid_repair = game.id.clone();
            let gid_uninst = game.id.clone();
            let size_label = format_storage(game);
            let armed = s.is_confirm_armed("gp-uninstall");

            let mut uninstall_btn = row()
                .key("gp-uninstall")
                .role("button")
                .label(if armed {
                    "Confirm uninstall"
                } else {
                    "Uninstall"
                })
                .focusable(true)
                .hit()
                .press_on_down()
                .press_scale(0.96)
                .h(32.0)
                .px(12.0)
                .gap(6.0)
                .center_y()
                .radius(RADIUS_PILL)
                .fill(if armed { bad() } else { glass_control_fill() })
                .hover(if armed {
                    bad().alpha(0.85)
                } else {
                    hover_fill()
                })
                .press(if armed {
                    bad().alpha(0.75)
                } else {
                    pressed_fill()
                })
                .hold_landing_width()
                .motion(SPRING_BASE)
                .on_press_tx(move |s: &mut AppState, tx| {
                    s.input_mode = InputMode::Pointer;
                    s.arm_or_confirm_game_uninstall(&gid_uninst, tx);
                })
                .child(icon_trash(13.0, if armed { PRIMARY_FG } else { FG }))
                .child(
                    text(if armed {
                        "Confirm uninstall"
                    } else {
                        "Uninstall"
                    })
                    .font(type_control())
                    .color(if armed { PRIMARY_FG } else { FG }),
                );
            if !armed {
                uninstall_btn =
                    uninstall_btn.border_gradient(1.0, glass_rim_top(), glass_rim_bottom());
            }
            if gamepad_focus {
                uninstall_btn = uninstall_btn.focus_ring(2.0, 3.0, FG);
            }

            body = body
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
                    Some(uninstall_btn),
                ));
        }
        2 => {
            body = body.child(props_row(
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
            body = body.child(props_row("DLC & expansions", Some((msg, fg_muted())), None));
        }
        _ => {
            let gid_save = game.id.clone();
            body = body.child(props_row(
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

    card.child(body)
}

pub fn view(s: &AppState, game: &Game, scale: f32) -> El {
    let hue = title_hue(&game.title);
    let cover = s.cover_image(game, scale);
    let hero = s.hero_image(game, scale);

    let mut hero_bg = el()
        .pin(Some(0.0), Some(0.0), Some(0.0), Some(0.0))
        .fill(BG);
    if let Some(ref img) = hero {
        hero_bg = hero_bg
            .pixels_fit(img.pixels(), SurfaceFit::Fill)
            .surface_anchor_fraction(0.5, 0.22);
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

    let gamepad_focus = s.input_mode == InputMode::Gamepad;

    let mut back_btn = row()
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
        .on_press_tx(|s: &mut AppState, tx| {
            s.input_mode = InputMode::Pointer;
            s.close_game_page_tx(tx);
        })
        .child(icon_chevron_left(16.0, FG))
        .child(text("Library").font(type_control()).color(FG));
    if gamepad_focus {
        back_btn = back_btn.focus_ring(2.0, 3.0, FG);
    }

    let store_pill = row()
        .h(34.0)
        .px(12.0)
        .gap(8.0)
        .center_y()
        .radius(RADIUS_PILL)
        .fill(glass_control_fill())
        .border_gradient(1.0, glass_rim_top(), glass_rim_bottom())
        .child(store_logo_or_fallback(&game.store, 15.0))
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

    let flight_id = s
        .cover_flight_id
        .clone()
        .unwrap_or_else(|| format!("deck:{}", game.id));
    let mut poster = el()
        .key(format!("gp-poster-{}", game.id))
        .matched(flight_id)
        .motion(SPRING_FLIGHT)
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
    let mut primary_btn = if game.is_running && game.can_stop {
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
    if gamepad_focus {
        primary_btn = primary_btn.focus_ring(2.0, 3.0, FG);
    }

    let id_folder = game.id.clone();
    let mut folder_btn = glass_icon_circle("gp-folder", "Open folder", 44.0, icon_folder(16.0, FG))
        .on_press(move |s: &mut AppState| s.open_game_folder(&id_folder));
    if gamepad_focus {
        folder_btn = folder_btn.focus_ring(2.0, 3.0, FG);
    }

    let identity = if let Some(logo) = s.logo_image(game, scale) {
        row().max_h(112.0).items_end().child(
            el().max_w(440.0)
                .max_h(112.0)
                .w(400.0)
                .h(96.0)
                .pixels_fit(logo.pixels(), SurfaceFit::Fit),
        )
    } else {
        row().items_end().pb(4.0).child(
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
            .child(
                row()
                    .key(format!("gp-stat-val-{label}"))
                    .h(26.0)
                    .center_y()
                    .child(text(val).font(type_title()).color(FG)),
            )
            .child(
                row().h(16.0).center_y().child(
                    text(label)
                        .font(TextStyle::new(scaled(12.0), 500.0))
                        .color(fg_muted()),
                ),
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
        .items_start()
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
        .items_stretch()
        .child(performance_card(s, game))
        .child(properties_card(s, game));

    let mut page = col()
        .key(format!("game-page-{}", game.id))
        .w_full()
        .gap(14.0)
        .pb(24.0)
        .fill(BG)
        .transition(Transition::opacity().springs(SpringPair::new(SPRING_QUICK, SPRING_QUICK)))
        .child(stage)
        .child(lower_cards);
    if s.is_confirm_armed("gp-uninstall") {
        page = page.hit().on_press(|s: &mut AppState| {
            s.disarm_confirm();
        });
    }
    page
}

#[cfg(test)]
mod tests {
    use crate::shell;
    use crate::state::AppState;
    use rune::motion_gate::Run;
    use rune::render::KIND_BACKDROP_BLUR;
    use rune::{Env, Runtime};

    #[test]
    fn game_page_equal_cards_stat_baselines_and_blur_cap() {
        let mut state = AppState::with_demo_games();
        state.open_game_page("demo:cyberpunk");
        let mut rt = Runtime::new(state, shell::view, (1920.0, 1080.0), Env::new(true, 1.0));
        rt.frame(0.0);
        rt.settle_all();
        rt.frame(1.0 / 60.0);

        let blurs = rt
            .instances
            .iter()
            .filter(|i| i.params[1] == KIND_BACKDROP_BLUR)
            .count();
        assert!(
            blurs <= 4,
            "Game page exceeded MAX_BACKDROP_BLURS=4: {blurs}"
        );

        let (perf_rect, _) = rt
            .presented("gp-perf-card")
            .expect("gp-perf-card presented");
        let (props_rect, _) = rt
            .presented("gp-props-card")
            .expect("gp-props-card presented");
        let perf_bottom = perf_rect[1] + perf_rect[3];
        let props_bottom = props_rect[1] + props_rect[3];
        assert!(
            (perf_bottom - props_bottom).abs() <= 1.0,
            "bottom cards not flush: perf_bottom={perf_bottom} props_bottom={props_bottom}"
        );

        let stat_keys = [
            "gp-stat-val-Played",
            "gp-stat-val-Last played",
            "gp-stat-val-Storage",
            "gp-stat-val-Achievements",
        ];
        let first_y = rt
            .presented(stat_keys[0])
            .expect("Played stat value presented")
            .0[1];
        for key in &stat_keys[1..] {
            let (rect, _) = rt.presented(key).expect(key);
            assert!(
                (rect[1] - first_y).abs() <= 1.0,
                "stat baseline {key} y={} missed Played y={first_y}",
                rect[1]
            );
        }
    }

    #[test]
    fn game_page_properties_pill_and_confirm_button_motion_gate() {
        for reduce_motion in [false, true] {
            let mut state = AppState::with_demo_games();
            state.settings.reduce_motion = reduce_motion;
            state.open_game_page("demo:cyberpunk");
            let mut rt = Runtime::new(state, shell::view, (1920.0, 1080.0), Env::new(true, 1.0));
            rt.set_system_reduced_motion(reduce_motion);
            let mut run = Run::new(rt, 120.0);
            run.idle(0.5);
            run.step();

            run.act("switch to Installed files tab", |rt| {
                Run::tap(rt, "gp-prop-tab-1")
            });
            run.play(0.4);
            run.act("arm uninstall confirm button", |rt| {
                Run::tap(rt, "gp-uninstall")
            });
            run.play(0.3);
            run.act("switch to Updates tab", |rt| Run::tap(rt, "gp-prop-tab-2"));
            run.play(0.4);
            run.act("switch back to Installed files tab", |rt| {
                Run::tap(rt, "gp-prop-tab-1")
            });
            run.play(0.4);

            let findings: Vec<_> = run
                .finish()
                .into_iter()
                .filter(|f| {
                    if f.kind == "pop-out" && f.element.contains("gp-props-pill") {
                        return false;
                    }
                    if reduce_motion && matches!(f.kind, "jump" | "text-swap") {
                        return false;
                    }
                    true
                })
                .collect();
            assert!(
                findings.is_empty(),
                "game_page motion_gate findings (reduce_motion={reduce_motion}): {findings:?}"
            );
        }
    }
}
