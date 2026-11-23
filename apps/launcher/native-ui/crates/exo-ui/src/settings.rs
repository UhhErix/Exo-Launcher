use crate::pc_state::SettingsSection;
use crate::state::{AppState, InputMode, OpenDropdown};
use exo_kit::{
    BG, FG, PRESS_SCALE, RADIUS_CARD, RADIUS_PILL, RADIUS_SMALL, SPRING_QUICK, SelectTrigger,
    TRANSPARENT, amoled_card, fg_muted, float_line, glass_button, glass_control_fill,
    glass_rim_bottom, glass_rim_top, good, hairline, hover_fill, icon_chevron_left, icon_download,
    icon_folder, icon_info, icon_library, icon_music, icon_refresh, icon_search, icon_sliders,
    icon_sparkles, icon_trophy, pressed_fill, primary_button, progress_bar, scaled, select_trigger,
    selected_fill, store_logo, switch_pill, type_body, type_page, type_section, type_small,
};
use rune::{El, ScrollSpec, TextStyle, Transition, col, edit_access, el, row, text, text_input};

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
    let c = if active { FG } else { FG.alpha(0.48) };
    match sec {
        SettingsSection::General => icon_sliders(15.0, c),
        SettingsSection::Account => icon_library(15.0, c),
        SettingsSection::Appearance => icon_download(15.0, c),
        SettingsSection::Graphics => icon_trophy(15.0, c),
        SettingsSection::Updates => icon_music(15.0, c),
        SettingsSection::About => icon_info(15.0, c),
    }
}

fn text_size_label(raw: &str) -> &'static str {
    match raw {
        "small" => "Small",
        "large" => "Large",
        _ => "Normal",
    }
}

fn set_row(title: &str, subtitle: Option<&str>, control: El) -> El {
    let mut left = col().grow(1.0).min_w(0.0).gap(3.0).child(
        text(title.to_string())
            .key(format!("set-label-{title}"))
            .font(TextStyle::new(scaled(13.0), 500.0))
            .color(FG),
    );
    if let Some(sub) = subtitle {
        left = left.child(
            text(sub.to_string())
                .font(TextStyle::new(scaled(13.0), 400.0))
                .color(fg_muted())
                .wrap(),
        );
    }
    el().w_full().py(12.0).px(18.0).child(
        row()
            .w_full()
            .min_h(38.0)
            .gap(16.0)
            .justify_between()
            .center_y()
            .child(left)
            .child(control),
    )
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

fn section_head(title: &str) -> El {
    row()
        .w_full()
        .min_h(28.0)
        .px(18.0)
        .center_y()
        .justify_between()
        .child(
            text(title.to_string())
                .key(format!("set-sec-head-{title}"))
                .font(type_section())
                .color(FG),
        )
}

fn section_general(s: &AppState) -> El {
    let set = &s.settings;
    let gamepad_focus = s.input_mode == InputMode::Gamepad;
    let main_card = amoled_card(RADIUS_CARD)
        .w_full()
        .child(set_row(
            "Text size",
            None,
            select_trigger(
                SelectTrigger::new(
                    "set-text-size-select",
                    "Text size",
                    text_size_label(set.text_size.as_deref().unwrap_or("normal")),
                )
                .open(s.open_dropdown == Some(OpenDropdown::TextSize))
                .gamepad_focus(gamepad_focus),
                |s: &mut AppState, _| s.toggle_dropdown(OpenDropdown::TextSize),
            ),
        ))
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

    let backup_card = amoled_card(RADIUS_CARD)
        .w_full()
        .child(set_row(
            "Last backup",
            None,
            row()
                .gap(8.0)
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

    col().w_full().gap(24.0).child(main_card).child(
        col()
            .w_full()
            .gap(8.0)
            .child(section_head("Backups"))
            .child(backup_card),
    )
}

fn section_library(s: &AppState) -> El {
    let set = &s.settings;
    let card = amoled_card(RADIUS_CARD)
        .w_full()
        .child(toggle_row(
            "set-lib-tools",
            "Show tools and soundtracks",
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
    col().w_full().gap(24.0).child(card)
}

fn section_downloads(s: &AppState) -> El {
    let set = &s.settings;
    let card = amoled_card(RADIUS_CARD)
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
    col().w_full().gap(24.0).child(card)
}

fn section_in_game(s: &AppState) -> El {
    let set = &s.settings;
    let card = amoled_card(RADIUS_CARD)
        .w_full()
        .child(toggle_row(
            "set-ach-banners",
            "Show achievement pop-ups",
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
    col().w_full().gap(24.0).child(card)
}

fn section_music(s: &AppState) -> El {
    let current = s.media.music_service.label();
    let logo_id = match s.media.music_service.id() {
        "apple" => "apple-music",
        other => other,
    };
    let gamepad_focus = s.input_mode == InputMode::Gamepad;
    let card = amoled_card(RADIUS_CARD).w_full().child(set_row(
        "Music service",
        Some("Active source for the Music room and top-bar mini player"),
        select_trigger(
            SelectTrigger::new("set-music-service-select", "Music service", current)
                .leading(store_logo(logo_id, 15.0))
                .open(s.open_dropdown == Some(OpenDropdown::MusicService))
                .gamepad_focus(gamepad_focus),
            |s: &mut AppState, _| s.toggle_dropdown(OpenDropdown::MusicService),
        ),
    ));
    col().w_full().gap(24.0).child(card)
}

fn section_about(s: &AppState) -> El {
    let ver =
        s.pc.system_about
            .as_ref()
            .and_then(|a| a.version.as_deref())
            .or_else(|| {
                s.pc.update_check
                    .as_ref()
                    .and_then(|u| u.current.as_deref())
            })
            .unwrap_or("3.29.0");
    let upd_avail =
        s.pc.update_check
            .as_ref()
            .is_some_and(|u| u.update_available);

    let mut card = amoled_card(RADIUS_CARD)
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
            "Setup",
            Some(&s.setup.summary()),
            glass_button(
                "set-resume-setup",
                "Resume setup",
                Some(icon_sparkles(13.0, FG)),
            )
            .on_press(|s: &mut AppState| s.setup_resume()),
        ))
        .child(el().w_full().h(1.0).fill(hairline()))
        .child(set_row(
            "Guided tour",
            Some("Walk through Library, Store, Discord, Music, Devices, and Exo AI"),
            glass_button(
                "set-replay-tour",
                "Replay tour",
                Some(icon_sparkles(13.0, FG)),
            )
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
                .px(18.0)
                .pb(16.0)
                .gap(6.0)
                .child(text(prog.status.clone()).font(type_small()).color(good()))
                .child(progress_bar(320.0, pct)),
        );
    }

    col().w_full().gap(24.0).child(card)
}

pub fn view(s: &AppState) -> El {
    let q = s.pc.settings_query.text.trim().to_lowercase();

    let back_btn = row()
        .key("settings-back")
        .role("button")
        .label("Back")
        .focusable(true)
        .hit()
        .press_on_down()
        .press_scale(PRESS_SCALE)
        .h(34.0)
        .pl(12.0)
        .pr(14.0)
        .gap(6.0)
        .center_y()
        .radius(RADIUS_PILL)
        .fill(glass_control_fill())
        .hover(hover_fill())
        .press(pressed_fill())
        .border_gradient(1.0, glass_rim_top(), glass_rim_bottom())
        .motion(SPRING_QUICK)
        .on_press(|s: &mut AppState| s.toggle_settings())
        .child(icon_chevron_left(16.0, FG))
        .child(text("Back").font(type_body()).color(FG));

    let sidebar = col()
        .key("settings-sidebar")
        .w(scaled(232.0))
        .h_full()
        .py(24.0)
        .pl(0.0)
        .pr(20.0)
        .gap(14.0)
        .border_sides([0.0, 1.0, 0.0, 0.0], float_line())
        .child(row().child(back_btn))
        .child(
            row().h(scaled(42.0)).px(12.0).center_y().child(
                text("Settings")
                    .key("settings-sidebar-title")
                    .font(type_page())
                    .color(FG),
            ),
        )
        .child(
            row()
                .w_full()
                .h(36.0)
                .px(12.0)
                .gap(10.0)
                .center_y()
                .radius(RADIUS_PILL)
                .fill(glass_control_fill())
                .border_gradient(1.0, glass_rim_top(), glass_rim_bottom())
                .child(icon_search(15.0, fg_muted()))
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
        .child(
            col()
                .w_full()
                .gap(3.0)
                .children(SettingsSection::ALL.into_iter().map(|sec| {
                    let active = s.pc.settings_section == sec && q.is_empty();
                    let mut item = row()
                        .key(sec.key())
                        .role("tab")
                        .label(nav_label(sec))
                        .focusable(true)
                        .hit()
                        .press_on_down()
                        .press_scale(PRESS_SCALE)
                        .w_full()
                        .min_h(36.0)
                        .py(6.0)
                        .px(12.0)
                        .gap(10.0)
                        .center_y()
                        .radius(RADIUS_SMALL)
                        .fill(if active { selected_fill() } else { TRANSPARENT })
                        .hover(hover_fill())
                        .press(pressed_fill())
                        .motion(SPRING_QUICK)
                        .on_press(move |s: &mut AppState| {
                            s.pc.settings_section = sec;
                            s.pc.settings_query.text.clear();
                            s.pc.settings_query.sync_invariants();
                        })
                        .child(nav_icon(sec, active))
                        .child(
                            text(nav_label(sec))
                                .font(if active { type_section() } else { type_body() })
                                .color(if active { FG } else { fg_muted() }),
                        );
                    if active {
                        item = item.border_gradient(1.0, glass_rim_top(), glass_rim_bottom());
                    }
                    item
                })),
        );

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
        .key("settings-scroll")
        .grow(1.0)
        .min_w(0.0)
        .max_w(scaled(952.0))
        .h_full()
        .scroll(ScrollSpec {
            stick_bottom: false,
        })
        .child(
            col()
                .key(format!("settings-pane-{:?}", s.pc.settings_section))
                .transition(Transition::opacity())
                .w_full()
                .pt(24.0)
                .pb(48.0)
                .child(
                    row()
                        .key("settings-page-title")
                        .h(scaled(42.0))
                        .px(18.0)
                        .center_y()
                        .child(
                            text(title)
                                .key("settings-page-title-text")
                                .font(type_page())
                                .color(FG),
                        ),
                )
                .child(col().w_full().pt(20.0).child(body)),
        );

    col()
        .key("room-settings")
        .w_full()
        .h_full()
        .pt(52.0)
        .px(16.0)
        .fill(BG)
        .items_center()
        .child(
            row()
                .key("settings-shell")
                .w_full()
                .max_w(scaled(1220.0))
                .h_full()
                .items_start()
                .gap(36.0)
                .child(sidebar)
                .child(right_col),
        )
}

#[cfg(test)]
mod tests {
    use crate::shell;
    use crate::state::{AppState, Room};
    use rune::motion_gate::Run;
    use rune::render::KIND_BACKDROP_BLUR;
    use rune::{Env, Runtime};

    #[test]
    fn settings_gridlines_gutter_and_blur_cap() {
        let mut rt = Runtime::new(
            AppState::default(),
            shell::view,
            (1920.0, 1080.0),
            Env::new(true, 1.0),
        );
        rt.update(|s, _| s.switch_room(Room::Settings));
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
            "Settings exceeded MAX_BACKDROP_BLURS=4: {blurs}"
        );

        let (shell_rect, _) = rt.presented("settings-shell").expect("settings-shell");
        let (sidebar_rect, _) = rt.presented("settings-sidebar").expect("settings-sidebar");
        let (scroll_rect, _) = rt.presented("settings-scroll").expect("settings-scroll");

        assert!(
            (shell_rect[2] - 1220.0).abs() <= 1.0,
            "expected centered 1220px settings shell, got {}",
            shell_rect[2]
        );
        let gutter = scroll_rect[0] - (sidebar_rect[0] + sidebar_rect[2]);
        assert!(
            (gutter - 36.0).abs() <= 1.0,
            "expected 36px sidebar-to-content gutter, got {gutter}"
        );

        let (title_rect, _) = rt
            .presented("settings-page-title-text")
            .expect("settings-page-title-text");
        let (sec_rect, _) = rt
            .presented("set-sec-head-Backups")
            .expect("set-sec-head-Backups");
        let (row1_rect, _) = rt
            .presented("set-label-Text size")
            .expect("set-label-Text size");
        let (row2_rect, _) = rt
            .presented("set-label-Last backup")
            .expect("set-label-Last backup");

        assert!(
            (title_rect[0] - row1_rect[0]).abs() <= 1.0,
            "page title x={} missed row label x={}",
            title_rect[0],
            row1_rect[0]
        );
        assert!(
            (sec_rect[0] - row1_rect[0]).abs() <= 1.0,
            "section head x={} missed row label x={}",
            sec_rect[0],
            row1_rect[0]
        );
        assert!(
            (row2_rect[0] - row1_rect[0]).abs() <= 1.0,
            "backup row label x={} missed primary row label x={}",
            row2_rect[0],
            row1_rect[0]
        );
    }

    #[test]
    fn settings_section_switch_motion_gate() {
        for reduce_motion in [false, true] {
            let mut state = AppState::default();
            state.settings.reduce_motion = reduce_motion;
            state.switch_room(Room::Settings);
            let mut rt = Runtime::new(state, shell::view, (1920.0, 1080.0), Env::new(true, 1.0));
            rt.set_system_reduced_motion(reduce_motion);
            let mut run = Run::new(rt, 120.0);
            run.idle(0.5);
            run.step();
            run.act("switch to Library", |rt| Run::tap(rt, "set-sec-account"));
            run.play(0.5);
            run.act("switch to About", |rt| Run::tap(rt, "set-sec-about"));
            run.play(0.5);
            let findings = run.finish();
            assert!(
                findings.is_empty(),
                "motion_gate findings (reduce_motion={reduce_motion}): {findings:?}"
            );
        }
    }
}
