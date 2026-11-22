use crate::pc_state::SettingsSection;
use crate::state::AppState;
use exo_kit::{
    BG, FG, PRESS_SCALE, RADIUS_PANEL, RADIUS_PILL, RADIUS_SMALL, SPRING_QUICK, TRANSPARENT,
    amoled_card, card_border, fg_muted, glass_button, glass_control_fill, glass_rim,
    glass_rim_bottom, glass_rim_top, good, hairline, hover_fill, icon_check, icon_chevron_down,
    icon_chevron_left, icon_download, icon_folder, icon_library, icon_music, icon_refresh,
    icon_search, icon_sliders, icon_sparkles, icon_trophy, pressed_fill, primary_button,
    progress_bar, switch_pill, type_body, type_control, type_small,
};
use rune::{El, TextStyle, col, edit_access, el, row, text, text_input};

fn nav_label(sec: SettingsSection) -> &'static str {
    match sec {
        SettingsSection::General => "General",
        SettingsSection::Account => "Library",
        SettingsSection::Appearance => "Downloads",
        SettingsSection::Graphics => "In game",
        SettingsSection::Updates => "Music",
        SettingsSection::About => "About",
    }
}

fn nav_icon(sec: SettingsSection, active: bool) -> El {
    let c = if active { FG } else { fg_muted() };
    match sec {
        SettingsSection::General => icon_sliders(14.0, c),
        SettingsSection::Account => icon_library(14.0, c),
        SettingsSection::Appearance => icon_download(14.0, c),
        SettingsSection::Graphics => icon_trophy(14.0, c),
        SettingsSection::Updates => icon_music(14.0, c),
        SettingsSection::About => icon_check(14.0, c),
    }
}

fn set_row(title: &str, subtitle: Option<&str>, control: El) -> El {
    let mut left = col()
        .grow(1.0)
        .min_w(0.0)
        .gap(2.0)
        .child(text(title.to_string()).font(TextStyle::new(13.0, 600.0)).color(FG));
    if let Some(sub) = subtitle {
        left = left.child(
            text(sub.to_string())
                .font(TextStyle::new(12.0, 400.0))
                .color(fg_muted())
                .wrap(),
        );
    }
    row()
        .w_full()
        .min_h(54.0)
        .px(20.0)
        .py(12.0)
        .gap(16.0)
        .justify_between()
        .center_y()
        .child(left)
        .child(control)
}

fn toggle_row(key: &'static str, title: &'static str, field: &'static str, value: bool) -> El {
    set_row(
        title,
        None,
        switch_pill(key, title, value).on_press(move |s: &mut AppState| {
            s.set_bool_setting(field, !value);
        }),
    )
}

fn dropdown_pill(label: &str) -> El {
    row()
        .h(30.0)
        .px(12.0)
        .gap(6.0)
        .center_y()
        .radius(RADIUS_PILL)
        .fill(glass_control_fill())
        .border_gradient(1.0, glass_rim_top(), glass_rim_bottom())
        .child(text(label.to_string()).font(type_control()).color(FG))
        .child(icon_chevron_down(12.0, fg_muted()))
}

fn section_general(s: &AppState) -> El {
    let set = &s.settings;
    let main_card = amoled_card(RADIUS_PANEL)
        .w_full()
        .child(set_row("Text size", None, dropdown_pill("Small")))
        .child(el().w_full().h(1.0).fill(hairline()))
        .child(toggle_row(
            "set-reduce-motion",
            "Reduce motion",
            "reduceMotion",
            set.reduce_motion,
        ))
        .child(el().w_full().h(1.0).fill(hairline()))
        .child(toggle_row(
            "set-start-win",
            "Open Exo when Windows starts",
            "startWithWindows",
            set.start_with_windows,
        ));

    let backup_card = amoled_card(RADIUS_PANEL)
        .w_full()
        .child(set_row(
            "Last backup",
            None,
            row()
                .gap(12.0)
                .center_y()
                .child(text("Today").font(type_small()).color(fg_muted()))
                .child(
                    glass_button("set-backup-now", "Back up now", None)
                        .on_press(|s: &mut AppState| s.sync_exo_account()),
                ),
        ))
        .child(el().w_full().h(1.0).fill(hairline()))
        .child(set_row(
            "Backup file",
            None,
            row()
                .gap(8.0)
                .center_y()
                .child(
                    glass_button("set-backup-export", "Export", None)
                        .on_press(|s: &mut AppState| s.open_app_logs()),
                )
                .child(
                    glass_button("set-backup-import", "Import", None)
                        .on_press(|s: &mut AppState| s.sync_exo_account()),
                ),
        ));

    col()
        .w_full()
        .gap(20.0)
        .child(main_card)
        .child(
            col()
                .w_full()
                .gap(10.0)
                .child(
                    text("Backups")
                        .font(TextStyle::new(13.0, 600.0))
                        .color(FG),
                )
                .child(backup_card),
        )
}

fn section_library(s: &AppState) -> El {
    let set = &s.settings;
    let card = amoled_card(RADIUS_PANEL)
        .w_full()
        .child(toggle_row(
            "set-lib-tools",
            "Show library tools",
            "showLibraryTools",
            set.show_library_tools,
        ))
        .child(el().w_full().h(1.0).fill(hairline()))
        .child(toggle_row(
            "set-quiet-clients",
            "Keep store clients quiet",
            "storeClientsQuiet",
            set.store_clients_quiet,
        ))
        .child(el().w_full().h(1.0).fill(hairline()))
        .child(set_row(
            "Riot playtime sync",
            Some("Refresh Valorant and League of Legends hours"),
            glass_button("set-riot-sync", "Sync hours", Some(icon_refresh(13.0, FG)))
                .on_press(|s: &mut AppState| s.refresh_riot_playtime()),
        ))
        .child(el().w_full().h(1.0).fill(hairline()))
        .child(set_row(
            "Cover & metadata cache",
            Some("Rebuild cached library covers if artwork looks stale"),
            glass_button("set-repair-cache", "Repair cache", None)
                .on_press(|s: &mut AppState| s.repair_app_cache()),
        ));
    col().w_full().gap(16.0).child(card)
}

fn section_downloads(s: &AppState) -> El {
    let set = &s.settings;
    let card = amoled_card(RADIUS_PANEL)
        .w_full()
        .child(toggle_row(
            "set-pause-dl",
            "Pause downloads while playing",
            "pauseDownloadsWhilePlaying",
            set.pause_downloads_while_playing,
        ))
        .child(el().w_full().h(1.0).fill(hairline()))
        .child(toggle_row(
            "set-awake-dl",
            "Keep PC awake for downloads",
            "keepAwakeDownloads",
            set.keep_awake_downloads,
        ))
        .child(el().w_full().h(1.0).fill(hairline()))
        .child(toggle_row(
            "set-auto-upd",
            "Auto-update games when idle",
            "autoUpdateGames",
            set.auto_update_games,
        ));
    col().w_full().gap(16.0).child(card)
}

fn section_in_game(s: &AppState) -> El {
    let set = &s.settings;
    let card = amoled_card(RADIUS_PANEL)
        .w_full()
        .child(toggle_row(
            "set-ach-banners",
            "Achievement banners",
            "achievementBanners",
            set.achievement_banners,
        ))
        .child(el().w_full().h(1.0).fill(hairline()))
        .child(toggle_row(
            "set-fps-counter",
            "FPS counter",
            "fpsCounter",
            set.fps_counter,
        ))
        .child(el().w_full().h(1.0).fill(hairline()))
        .child(toggle_row(
            "set-hdr-games",
            "Auto HDR for games",
            "hdrForGames",
            set.hdr_for_games,
        ))
        .child(el().w_full().h(1.0).fill(hairline()))
        .child(toggle_row(
            "set-auto-upscalers",
            "Auto-update DLSS, FSR & XeSS",
            "autoUpdateUpscalers",
            set.auto_update_upscalers,
        ));
    col().w_full().gap(16.0).child(card)
}

fn section_music(s: &AppState) -> El {
    let current = s.media.music_service.label();
    let card = amoled_card(RADIUS_PANEL)
        .w_full()
        .child(set_row(
            "Music service",
            Some("Active source for the Music room and top-bar mini player"),
            dropdown_pill(current),
        ));
    col().w_full().gap(16.0).child(card)
}

fn section_about(s: &AppState) -> El {
    let ver = s
        .pc
        .system_about
        .as_ref()
        .and_then(|a| a.version.as_deref())
        .or_else(|| s.pc.update_check.as_ref().and_then(|u| u.current.as_deref()))
        .unwrap_or("3.29.0");
    let upd_avail = s.pc.update_check.as_ref().is_some_and(|u| u.update_available);

    let mut card = amoled_card(RADIUS_PANEL)
        .w_full()
        .child(set_row(
            "Exo Launcher",
            Some(ver),
            row()
                .gap(8.0)
                .center_y()
                .child(
                    glass_button("set-check-upd", "Check for updates", None)
                        .on_press(|s: &mut AppState| s.check_app_update()),
                )
                .children(upd_avail.then(|| {
                    primary_button("set-install-upd", "Install update", None)
                        .on_press(|s: &mut AppState| s.install_app_update())
                })),
        ))
        .child(el().w_full().h(1.0).fill(hairline()))
        .child(set_row(
            "Guided tour",
            Some("Walk through Library, Store, Discord, Music, Devices, and Exo AI"),
            glass_button("set-replay-tour", "Replay tour", Some(icon_sparkles(13.0, FG)))
                .on_press(|s: &mut AppState| s.start_tour()),
        ))
        .child(el().w_full().h(1.0).fill(hairline()))
        .child(set_row(
            "Diagnostic logs",
            Some("Open the local Exo Launcher log folder"),
            glass_button("set-open-logs", "Open logs", Some(icon_folder(13.0, FG)))
                .on_press(|s: &mut AppState| s.open_app_logs()),
        ));

    if let Some(ref prog) = s.pc.update_progress {
        let pct = (prog.percent.unwrap_or(0.0) / 100.0).clamp(0.0, 1.0) as f32;
        card = card.child(
            col()
                .w_full()
                .px(20.0)
                .pb(16.0)
                .gap(6.0)
                .child(text(prog.status.clone()).font(type_small()).color(good()))
                .child(progress_bar(320.0, pct)),
        );
    }

    col().w_full().gap(16.0).child(card)
}

pub fn view(s: &AppState) -> El {
    let q = s.pc.settings_query.text.trim().to_lowercase();

    let sidebar = col()
        .w(212.0)
        .min_h(720.0)
        .pt(60.0)
        .px(14.0)
        .pb(28.0)
        .gap(14.0)
        .border_sides([0.0, 1.0, 0.0, 0.0], hairline())
        .child(
            row().child(
                glass_button("settings-back", "Back", Some(icon_chevron_left(14.0, FG)))
                    .on_press(|s: &mut AppState| s.toggle_settings()),
            ),
        )
        .child(
            text("Settings")
                .font(TextStyle::new(26.0, 700.0))
                .color(FG),
        )
        .child(
            row()
                .w_full()
                .h(34.0)
                .px(11.0)
                .gap(8.0)
                .center_y()
                .radius(RADIUS_PILL)
                .fill(BG)
                .border(1.0, glass_rim())
                .child(icon_search(13.0, fg_muted()))
                .child(
                    text_input(&s.pc.settings_query, type_body())
                        .key("settings-search")
                        .label("Search settings")
                        .grow(1.0)
                        .single_line()
                        .color(FG)
                        .placeholder("Search settings", fg_muted())
                        .on_edit(edit_access(|s: &mut AppState| &mut s.pc.settings_query)),
                ),
        )
        .child(col().w_full().gap(2.0).children(SettingsSection::ALL.into_iter().map(|sec| {
            let active = s.pc.settings_section == sec && q.is_empty();
            row()
                .key(sec.key())
                .role("tab")
                .label(nav_label(sec))
                .focusable(true)
                .hit()
                .press_on_down()
                .press_scale(PRESS_SCALE)
                .w_full()
                .h(36.0)
                .px(12.0)
                .gap(10.0)
                .center_y()
                .radius(RADIUS_SMALL)
                .fill(if active { FG.alpha(0.11) } else { TRANSPARENT })
                .hover(hover_fill())
                .press(pressed_fill())
                .border(if active { 1.0 } else { 0.0 }, card_border())
                .motion(SPRING_QUICK)
                .on_press(move |s: &mut AppState| {
                    s.pc.settings_section = sec;
                    s.pc.settings_query.text.clear();
                    s.pc.settings_query.sync_invariants();
                })
                .child(nav_icon(sec, active))
                .child(
                    text(nav_label(sec))
                        .font(TextStyle::new(13.0, if active { 600.0 } else { 500.0 }))
                        .color(if active { FG } else { fg_muted() }),
                )
        })));

    let title = nav_label(s.pc.settings_section);
    let body = match s.pc.settings_section {
        SettingsSection::General => section_general(s),
        SettingsSection::Account => section_library(s),
        SettingsSection::Appearance => section_downloads(s),
        SettingsSection::Graphics => section_in_game(s),
        SettingsSection::Updates => section_music(s),
        SettingsSection::About => section_about(s),
    };

    let right_col = col()
        .grow(1.0)
        .min_w(0.0)
        .pt(64.0)
        .px(44.0)
        .pb(40.0)
        .items_center()
        .child(
            col()
                .w_full()
                .max_w(680.0)
                .gap(20.0)
                .child(text(title).font(TextStyle::new(24.0, 700.0)).color(FG))
                .child(body),
        );

    row()
        .w_full()
        .items_start()
        .child(sidebar)
        .child(right_col)
}
