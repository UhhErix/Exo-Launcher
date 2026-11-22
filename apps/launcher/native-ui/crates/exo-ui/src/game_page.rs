use crate::state::{AppState, title_hue};
use exo_kit::{
    BG, FG, PRIMARY_FG, RADIUS_CARD, RADIUS_PANEL, RADIUS_PILL, RADIUS_SMALL, SPRING_BASE,
    danger_button, fg_muted, glass_button, glass_control_fill, glass_panel, glass_rim, good,
    hairline, icon_back, icon_download, icon_folder, icon_play, icon_sliders, icon_stop,
    icon_trash, icon_wrench, primary_button, stat_chip, subtle_fill, switch_pill, type_body,
    type_control, type_page, type_small, type_title,
};
use exo_rpc::Game;
use rune::{El, SurfaceFit, col, edit_access, el, oklch, row, text, text_input};

fn properties_drawer(s: &AppState, game: &Game) -> El {
    let props = s.game_props.as_ref();
    let overlay_on = props.and_then(|p| p.steam_overlay).unwrap_or(false);
    let cloud_text = match props.and_then(|p| p.cloud_sync) {
        Some(true) => "On",
        Some(false) => "Off",
        None => "Managed by store",
    };

    let launch_input = text_input(&s.props_launch_opts, type_body())
        .key("props-launch-input")
        .label("Launch options")
        .w_full()
        .single_line()
        .color(FG)
        .placeholder("-novid -high", fg_muted())
        .on_edit(edit_access(|s: &mut AppState| &mut s.props_launch_opts));

    let game_id_save = game.id.clone();
    let game_id_overlay = game.id.clone();

    let body = col()
        .w_full()
        .px(20.0)
        .py(18.0)
        .gap(14.0)
        .child(text("Game properties").font(type_title()).color(FG))
        .child(
            col()
                .w_full()
                .gap(6.0)
                .child(text("Launch options").font(type_small()).color(fg_muted()))
                .child(
                    row()
                        .w_full()
                        .h(38.0)
                        .px(12.0)
                        .center_y()
                        .radius(RADIUS_SMALL)
                        .fill(BG.alpha(0.55))
                        .border(1.0, glass_rim())
                        .child(launch_input),
                ),
        )
        .child(
            row()
                .w_full()
                .justify_between()
                .center_y()
                .gap(12.0)
                .child(
                    col()
                        .grow(1.0)
                        .min_w(0.0)
                        .gap(2.0)
                        .child(text("Steam overlay").font(type_control()).color(FG))
                        .child(
                            text("Defaults off to save frame time; opt in per game.")
                                .font(type_small())
                                .color(fg_muted())
                                .w_full()
                                .wrap(),
                        ),
                )
                .child(
                    switch_pill("props-overlay-switch", "Steam overlay", overlay_on).on_press(
                        move |s: &mut AppState| {
                            s.set_game_property(
                                &game_id_overlay,
                                "steamOverlay",
                                if overlay_on { "false" } else { "true" },
                            );
                        },
                    ),
                ),
        )
        .child(
            row()
                .w_full()
                .justify_between()
                .center_y()
                .child(text("Cloud sync").font(type_control()).color(FG))
                .child(text(cloud_text).font(type_small()).color(fg_muted())),
        );

    let footer = row()
        .w_full()
        .px(20.0)
        .py(14.0)
        .justify_end()
        .gap(8.0)
        .child(
            glass_button("props-close", "Close", None)
                .on_press(|s: &mut AppState| s.props_open = false),
        )
        .child(
            primary_button("props-save", "Save", None).on_press(move |s: &mut AppState| {
                let value = s.props_launch_opts.text.clone();
                s.set_game_property(&game_id_save, "launchOptions", &value);
            }),
        );

    let card = glass_panel("game-properties-modal", 480.0)
        .motion(SPRING_BASE)
        .child(body)
        .child(el().w_full().h(1.0).fill(hairline()))
        .child(footer);

    el().key("props-scrim")
        .pin(Some(0.0), Some(0.0), Some(0.0), Some(0.0))
        .fill(BG.alpha(0.52))
        .hit()
        .on_press(|s: &mut AppState| s.props_open = false)
        .child(col().w_full().h_full().center().child(card))
}

pub fn view(s: &AppState, game: &Game, scale: f32) -> El {
    let hue = title_hue(&game.title);
    let cover = s.cover_image(game, scale);

    let mut hero_bg = el()
        .pin(Some(0.0), Some(0.0), Some(0.0), None)
        .w_full()
        .h(360.0);
    if let Some(ref img) = cover {
        hero_bg = hero_bg
            .pixels_fit(img.pixels(), SurfaceFit::Fill)
            .blur(26.0)
            .opacity(0.34);
    } else {
        hero_bg = hero_bg.linear_gradient(
            0.3,
            [
                (0.0, oklch(0.32, 0.14, hue).alpha(0.55)),
                (1.0, BG.alpha(0.0)),
            ],
        );
    }
    let hero_fade = el()
        .pin(Some(0.0), Some(0.0), Some(0.0), None)
        .w_full()
        .h(360.0)
        .linear_gradient(
            std::f32::consts::PI,
            [(0.0, BG.alpha(0.24)), (0.62, BG.alpha(0.84)), (1.0, BG)],
        );

    let mut poster = el()
        .w(200.0)
        .h(284.0)
        .radius(RADIUS_CARD)
        .shadow(14.0, 34.0, BG.alpha(0.65));
    if let Some(ref img) = cover {
        poster = poster.pixels_fit(img.pixels(), SurfaceFit::Fill);
    } else {
        poster = poster.linear_gradient(
            0.35,
            [
                (0.0, oklch(0.50, 0.16, hue)),
                (1.0, oklch(0.14, 0.04, hue)),
            ],
        );
    }

    let id_primary = game.id.clone();
    let primary_btn = if game.is_running && game.can_stop {
        primary_button("gp-stop", "Stop", Some(icon_stop(13.0, PRIMARY_FG)))
            .on_press(move |s: &mut AppState| s.stop_game(&id_primary))
    } else if game.update_available {
        primary_button("gp-update", "Update", Some(icon_download(14.0, PRIMARY_FG)))
            .on_press(move |s: &mut AppState| s.update_game(&id_primary))
    } else if game.installed {
        primary_button("gp-play", "Play", Some(icon_play(13.0, PRIMARY_FG)))
            .on_press(move |s: &mut AppState| s.launch_game(&id_primary))
    } else {
        primary_button("gp-install", "Install", Some(icon_download(14.0, PRIMARY_FG)))
            .on_press(move |s: &mut AppState| s.install_game(&id_primary))
    };

    let mut tools = row().gap(8.0).flex_wrap().center_y().child(primary_btn);

    if game.installed {
        let id_folder = game.id.clone();
        let id_props = game.id.clone();
        let id_repair = game.id.clone();
        let id_uninst = game.id.clone();

        tools = tools
            .child(
                glass_button("gp-folder", "Folder", Some(icon_folder(14.0, FG)))
                    .on_press(move |s: &mut AppState| s.open_game_folder(&id_folder)),
            )
            .child(
                glass_button("gp-props", "Properties", Some(icon_sliders(14.0, FG)))
                    .on_press(move |s: &mut AppState| s.open_game_properties(&id_props)),
            )
            .child(
                glass_button("gp-repair", "Verify", Some(icon_wrench(14.0, FG)))
                    .on_press(move |s: &mut AppState| s.repair_game(&id_repair)),
            );

        if s.uninstall_confirm {
            tools = tools.child(
                danger_button("gp-uninstall-confirm", "Confirm uninstall", Some(icon_trash(14.0, PRIMARY_FG)))
                    .on_press(move |s: &mut AppState| s.uninstall_game(&id_uninst)),
            );
        } else {
            tools = tools.child(
                glass_button("gp-uninstall", "Uninstall", Some(icon_trash(14.0, FG)))
                    .on_press(|s: &mut AppState| s.uninstall_confirm = true),
            );
        }
    }

    let playtime_val = {
        let h = game.hours_label();
        if h.is_empty() { "Never played".into() } else { h }
    };
    let size_val = game
        .size_label()
        .unwrap_or_else(|| if game.installed { "Installed".into() } else { "Not installed".into() });
    let ach_val = match s.game_extras.as_ref() {
        Some(ex) if ex.achievements_total.unwrap_or(0) > 0 => format!(
            "{} / {}",
            ex.achievements_unlocked.unwrap_or(0),
            ex.achievements_total.unwrap_or(0)
        ),
        _ => "—".into(),
    };

    let mut stats = row()
        .gap(10.0)
        .flex_wrap()
        .child(stat_chip("Playtime", playtime_val))
        .child(stat_chip("Storage", size_val))
        .child(stat_chip("Store", game.store_display().into()))
        .child(stat_chip("Achievements", ach_val));

    if game.family_shared {
        let lender = game.shared_by.clone().unwrap_or_else(|| "Steam Family".into());
        stats = stats.child(stat_chip("Shared by", lender));
    }

    let mut meta_col = col()
        .grow(1.0)
        .min_w(0.0)
        .gap(14.0)
        .child(
            row()
                .gap(8.0)
                .center_y()
                .flex_wrap()
                .child(text(game.title.clone()).font(type_page()).color(FG).wrap())
                .children(game.is_running.then(|| {
                    row()
                        .h(24.0)
                        .px(10.0)
                        .gap(6.0)
                        .center_y()
                        .radius(RADIUS_PILL)
                        .fill(glass_control_fill())
                        .border(1.0, glass_rim())
                        .child(el().size(7.0, 7.0).radius(3.5).fill(good()))
                        .child(text("Running").font(type_small()).color(FG))
                })),
        )
        .child(tools)
        .child(stats);

    if let Some(ref note) = game.launch_note {
        meta_col = meta_col.child(
            el().w_full()
                .px(14.0)
                .py(10.0)
                .radius(RADIUS_PANEL)
                .fill(subtle_fill())
                .border(1.0, glass_rim())
                .child(text(note.clone()).font(type_small()).color(fg_muted()).w_full().wrap()),
        );
    }

    if !game.tags.is_empty() {
        let tags = row().gap(6.0).flex_wrap().children(game.tags.iter().take(8).map(|t| {
            el().h(24.0)
                .px(10.0)
                .radius(RADIUS_PILL)
                .center()
                .fill(subtle_fill())
                .child(text(t.clone()).font(type_small()).color(fg_muted()))
        }));
        meta_col = meta_col.child(tags);
    }

    let content = col()
        .w_full()
        .pt(72.0)
        .px(28.0)
        .pb(36.0)
        .gap(22.0)
        .child(
            row().child(
                glass_button("gp-back", "Library", Some(icon_back(14.0, FG)))
                    .on_press(|s: &mut AppState| s.close_game_page()),
            ),
        )
        .child(row().w_full().gap(26.0).items_start().child(poster).child(meta_col));

    let mut page = el().w_full().child(hero_bg).child(hero_fade).child(content);

    if s.props_open {
        page = page.child(properties_drawer(s, game));
    }
    page
}
