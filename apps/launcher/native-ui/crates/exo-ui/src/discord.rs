use crate::discord_state::{DiscordHomeTab, DiscordPlace};
use crate::state::{AppState, Room};
use exo_kit::{
    BG, FG, GAP, PRESS_SCALE, PRIMARY_FG, RADIUS_CARD, RADIUS_PANEL, RADIUS_PILL, RADIUS_SMALL,
    SPRING_QUICK, bad, fg_faint, fg_muted, filter_pill, glass_button, glass_control_fill,
    glass_rim, good, hairline, hex, hover_fill, icon_back, icon_bell_off, icon_chat,
    icon_chevron_down, icon_chevron_right, icon_hash, icon_headphones, icon_headphones_off,
    icon_home, icon_lock, icon_mic, icon_mic_off, icon_phone, icon_phone_off, icon_play,
    icon_refresh, icon_search, icon_server, icon_settings, icon_user_plus, icon_users, icon_volume,
    pressed_fill, primary_button, qr_code_view, srgb, subtle_fill, type_body, type_control,
    type_label, type_page, type_section, type_small, type_title, warn,
};
use exo_rpc::{
    DiscordChannel, DiscordDm, DiscordFriend, DiscordGuild, DiscordMemberItem, DiscordVoiceMember,
};
use rune::{Color, El, ScrollSpec, SurfaceFit, col, edit_access, el, row, text, text_input};
use std::collections::BTreeMap;

fn status_color(status: &str) -> Color {
    match status {
        "online" => good(),
        "idle" => warn(),
        "dnd" => bad(),
        _ => hex(0x6e6e73, 1.0),
    }
}

fn status_label(status: &str) -> &'static str {
    match status {
        "online" => "Online",
        "idle" => "Idle",
        "dnd" => "Do Not Disturb",
        "invisible" => "Invisible",
        _ => "Offline",
    }
}

fn initials(name: &str) -> String {
    let mut out = String::new();
    for word in name.split_whitespace().take(2) {
        if let Some(ch) = word.chars().next() {
            out.push(ch.to_ascii_uppercase());
        }
    }
    if out.is_empty() {
        "D".into()
    } else {
        out
    }
}

fn monogram_fill(seed: &str) -> Color {
    let mut h = 2166136261u32;
    for b in seed.bytes() {
        h ^= b as u32;
        h = h.wrapping_mul(16777619);
    }
    let palette = [
        0x1d232b, 0x221f29, 0x1a2622, 0x27221b, 0x241d22, 0x1c2529,
    ];
    hex(palette[(h as usize) % palette.len()], 1.0)
}

fn lift_role_color(raw: Option<&str>, offline: bool) -> Color {
    if offline {
        return fg_faint();
    }
    let Some(h) = raw.and_then(|s| s.strip_prefix('#')) else {
        return FG;
    };
    if h.len() != 6 {
        return FG;
    }
    let Ok(v) = u32::from_str_radix(h, 16) else {
        return FG;
    };
    let mut r = ((v >> 16) & 0xff) as f32 / 255.0;
    let mut g = ((v >> 8) & 0xff) as f32 / 255.0;
    let mut b = (v & 0xff) as f32 / 255.0;
    let lum = 0.2126 * r + 0.7152 * g + 0.0722 * b;
    if lum < 0.38 {
        let boost = ((0.38 - lum) / (1.0 - lum).max(0.01)).clamp(0.0, 0.78);
        r += (1.0 - r) * boost;
        g += (1.0 - g) * boost;
        b += (1.0 - b) * boost;
    }
    srgb(
        (r * 255.0).round() as u8,
        (g * 255.0).round() as u8,
        (b * 255.0).round() as u8,
        1.0,
    )
}

fn avatar_box(
    s: &AppState,
    url: Option<&str>,
    name: &str,
    size: f32,
    radius: f32,
    status: Option<&str>,
    speaking: bool,
    scale: f32,
) -> El {
    let mut inner = el()
        .size(size, size)
        .radius(radius)
        .clip_rounded(radius)
        .fill(monogram_fill(name))
        .center();
    if let Some(img) = url.and_then(|u| s.remote_image(u, scale)) {
        inner = inner.pixels_fit(img.pixels(), SurfaceFit::Fill);
    } else {
        let style = if size >= 40.0 {
            type_section()
        } else if size >= 28.0 {
            type_control()
        } else {
            type_label()
        };
        inner = inner.child(text(initials(name)).font(style).color(FG));
    }
    if speaking {
        inner = inner.border(2.0, good());
    } else {
        inner = inner.border(1.0, hairline());
    }
    let mut wrap = el().size(size, size).child(inner);
    if let Some(st) = status {
        let dot_sz = (size * 0.30).clamp(8.0, 12.0);
        wrap = wrap.child(
            el().pin(None, None, Some(-1.0), Some(-1.0))
                .size(dot_sz, dot_sz)
                .radius(dot_sz * 0.5)
                .fill(status_color(st))
                .border(2.0, BG),
        );
    }
    wrap
}

fn badge_pill(count: i32) -> El {
    el().h(18.0)
        .px(6.0)
        .radius(RADIUS_PILL)
        .fill(FG)
        .center()
        .child(text(count.to_string()).font(type_label()).color(PRIMARY_FG))
}

fn unread_dot() -> El {
    el().size(7.0, 7.0).radius(3.5).fill(FG)
}

fn pane_surface() -> Color {
    hex(0x0b0b0d, 1.0)
}

fn card_surface() -> Color {
    hex(0x0e0e11, 1.0)
}

fn icon_btn(key: String, label: &'static str, active: bool, danger: bool, icon: El) -> El {
    let fill = if danger {
        bad().alpha(0.20)
    } else if active {
        hover_fill()
    } else {
        subtle_fill()
    };
    el().key(key)
        .role("button")
        .label(label)
        .focusable(true)
        .hit()
        .press_on_down()
        .press_scale(PRESS_SCALE)
        .size(30.0, 30.0)
        .radius(RADIUS_SMALL)
        .fill(fill)
        .hover(if danger { bad().alpha(0.32) } else { hover_fill() })
        .press(pressed_fill())
        .center()
        .motion(SPRING_QUICK)
        .child(icon)
}

fn signed_out_view(s: &AppState) -> El {
    let state = s.discord.status.state.as_str();
    let mut card = col()
        .key("dc-signin-card")
        .w(440.0)
        .px(28.0)
        .py(28.0)
        .gap(16.0)
        .radius(RADIUS_PANEL)
        .fill(pane_surface())
        .border(1.0, glass_rim())
        .center()
        .child(
            el().size(48.0, 48.0)
                .radius(14.0)
                .fill(subtle_fill())
                .border(1.0, glass_rim())
                .center()
                .child(icon_chat(22.0, FG)),
        )
        .child(text("Discord in Exo").font(type_page()).color(FG))
        .child(
            text("Native client built in Exo's AMOLED shape — friends' games first, voice rooms, DMs, and servers.")
                .font(type_body())
                .color(fg_muted())
                .w_full()
                .wrap(),
        );

    if let Some(ref step) = s.discord.signin {
        match step.state.as_str() {
            "waiting" => {
                if let Some(ref url) = step.url {
                    card = card
                        .child(qr_code_view(url, 208.0))
                        .child(
                            text("Scan with the Discord app on your phone")
                                .font(type_control())
                                .color(FG),
                        );
                } else {
                    card = card.child(
                        text(
                            step.message
                                .clone()
                                .unwrap_or_else(|| "Generating sign-in code…".into()),
                        )
                        .font(type_body())
                        .color(fg_muted()),
                    );
                }
                card = card.child(
                    glass_button("dc-signin-cancel", "Cancel", None)
                        .on_press(|s: &mut AppState| s.cancel_discord_signin()),
                );
            }
            "scanned" => {
                let who = step.user.as_deref().unwrap_or("your account");
                card = card
                    .child(
                        row()
                            .px(14.0)
                            .py(10.0)
                            .gap(8.0)
                            .radius(RADIUS_CARD)
                            .fill(good().alpha(0.14))
                            .border(1.0, good().alpha(0.35))
                            .center_y()
                            .child(el().size(8.0, 8.0).radius(4.0).fill(good()))
                            .child(
                                text(format!("Confirm sign-in on your phone for {who}"))
                                    .font(type_control())
                                    .color(FG)
                                    .wrap(),
                            ),
                    )
                    .child(
                        glass_button("dc-signin-cancel", "Cancel", None)
                            .on_press(|s: &mut AppState| s.cancel_discord_signin()),
                    );
            }
            _ => {
                let msg = step
                    .message
                    .clone()
                    .unwrap_or_else(|| "Sign-in did not complete.".into());
                card = card
                    .child(text(msg).font(type_body()).color(bad()).w_full().wrap())
                    .child(
                        primary_button("dc-signin-retry", "Try again", None)
                            .on_press(|s: &mut AppState| s.start_discord_signin()),
                    );
            }
        }
    } else if state == "connecting" || (s.discord.status.signed_in && state != "error") {
        card = card
            .child(
                row()
                    .px(14.0)
                    .py(10.0)
                    .gap(8.0)
                    .radius(RADIUS_PILL)
                    .fill(subtle_fill())
                    .center_y()
                    .child(el().size(8.0, 8.0).radius(4.0).fill(warn()))
                    .child(text("Connecting to Discord…").font(type_control()).color(FG)),
            )
            .child(
                glass_button("dc-reconnect", "Refresh status", Some(icon_refresh(14.0, FG)))
                    .on_press(|s: &mut AppState| s.load_discord_room()),
            );
    } else {
        if let Some(ref err) = s.discord.status.message {
            card = card.child(text(err.clone()).font(type_small()).color(bad()).w_full().wrap());
        }
        let mut actions = row().gap(10.0).center_y().child(
            primary_button("dc-signin-start", "Sign in with QR", None)
                .on_press(|s: &mut AppState| s.start_discord_signin()),
        );
        if state == "error" || state == "off" {
            actions = actions.child(
                glass_button("dc-connect-btn", "Reconnect", Some(icon_refresh(14.0, FG)))
                    .on_press(|s: &mut AppState| s.load_discord_room()),
            );
        }
        card = card.child(actions);

        if !s.discord.accounts.is_empty() {
            let mut acc_list = col().w_full().gap(6.0).pt(6.0).child(
                text("Saved accounts")
                    .font(type_small())
                    .color(fg_muted()),
            );
            for acc in &s.discord.accounts {
                let aid = acc.id.clone();
                acc_list = acc_list.child(
                    row()
                        .key(format!("dc-saved-acc-{}", acc.id))
                        .w_full()
                        .h(40.0)
                        .px(12.0)
                        .gap(10.0)
                        .center_y()
                        .radius(RADIUS_SMALL)
                        .fill(subtle_fill())
                        .hover(hover_fill())
                        .hit()
                        .press_on_down()
                        .press_scale(PRESS_SCALE)
                        .on_press(move |s: &mut AppState| s.switch_discord_account(&aid))
                        .child(text(acc.name.clone()).font(type_control()).color(FG).grow(1.0))
                        .child(text("Switch").font(type_small()).color(fg_muted())),
                );
            }
            card = card.child(acc_list);
        }
    }

    col()
        .key("dc-signed-out")
        .w_full()
        .h_full()
        .pt(64.0)
        .center()
        .child(card)
}

fn place_band(s: &AppState) -> El {
    let dm_unread = s.discord.unread_dm_count();
    let dm_mentions = s.discord.total_dm_mentions();
    let guild_mentions = s.discord.total_guild_mentions();

    let mut places = row().gap(6.0).center_y();
    for place in DiscordPlace::ALL {
        let active = s.discord.place == place;
        let label = match place {
            DiscordPlace::Home => place.label().to_string(),
            DiscordPlace::Messages => {
                if dm_mentions > 0 {
                    format!("Messages · {dm_mentions}")
                } else if dm_unread > 0 {
                    format!("Messages · {dm_unread}")
                } else {
                    "Messages".into()
                }
            }
            DiscordPlace::Servers => {
                if guild_mentions > 0 {
                    format!("Servers · {guild_mentions}")
                } else {
                    "Servers".into()
                }
            }
        };
        let ic = match place {
            DiscordPlace::Home => icon_home(13.0, if active { PRIMARY_FG } else { fg_muted() }),
            DiscordPlace::Messages => icon_chat(13.0, if active { PRIMARY_FG } else { fg_muted() }),
            DiscordPlace::Servers => icon_server(13.0, if active { PRIMARY_FG } else { fg_muted() }),
        };
        places = places.child(
            row()
                .key(place.key())
                .role("tab")
                .label(label.clone())
                .focusable(true)
                .hit()
                .press_on_down()
                .press_scale(PRESS_SCALE)
                .h(32.0)
                .px(12.0)
                .gap(6.0)
                .center_y()
                .radius(RADIUS_PILL)
                .fill(if active { FG } else { subtle_fill() })
                .hover(if active { FG.alpha(0.92) } else { hover_fill() })
                .press(if active { FG.alpha(0.84) } else { pressed_fill() })
                .motion(SPRING_QUICK)
                .on_press(move |s: &mut AppState| s.open_discord_place(place))
                .child(ic)
                .child(
                    text(label)
                        .font(type_control())
                        .color(if active { PRIMARY_FG } else { FG }),
                ),
        );
    }

    let mut context_controls = row().gap(8.0).center_y();
    match s.discord.place {
        DiscordPlace::Home => {
            context_controls = context_controls
                .child(
                    filter_pill(
                        "dc-home-now",
                        "Now".into(),
                        s.discord.home_tab == DiscordHomeTab::Now,
                    )
                    .on_press(|s: &mut AppState| s.discord.home_tab = DiscordHomeTab::Now),
                )
                .child(
                    filter_pill(
                        "dc-home-friends",
                        format!(
                            "Friends · {}",
                            s.discord
                                .friends
                                .iter()
                                .filter(|f| f.kind == "friend")
                                .count()
                        ),
                        s.discord.home_tab == DiscordHomeTab::Friends,
                    )
                    .on_press(|s: &mut AppState| s.discord.home_tab = DiscordHomeTab::Friends),
                )
                .child(
                    glass_button(
                        "dc-add-friend-toggle",
                        "Add friend",
                        Some(icon_user_plus(13.0, FG)),
                    )
                    .on_press(|s: &mut AppState| {
                        s.discord.home_tab = DiscordHomeTab::Friends;
                        s.discord.add_friend_open = !s.discord.add_friend_open;
                    }),
                );
        }
        DiscordPlace::Messages => {
            context_controls = context_controls.child(
                glass_button(
                    "dc-toggle-members",
                    if s.discord.show_members {
                        "Hide info"
                    } else {
                        "Show info"
                    },
                    Some(icon_users(13.0, FG)),
                )
                .on_press(|s: &mut AppState| s.discord.show_members = !s.discord.show_members),
            );
        }
        DiscordPlace::Servers => {
            if let Some(g) = s.discord.active_guild() {
                let gid = g.id.clone();
                context_controls = context_controls
                    .child(
                        glass_button("dc-all-servers", "All servers", Some(icon_back(13.0, FG)))
                            .on_press(|s: &mut AppState| s.close_discord_guild()),
                    )
                    .child(
                        glass_button("dc-mark-read", "Mark read", None)
                            .on_press(move |s: &mut AppState| s.mark_discord_guild_read(&gid)),
                    )
                    .child(
                        glass_button(
                            "dc-toggle-members",
                            if s.discord.show_members {
                                "Hide members"
                            } else {
                                "Members"
                            },
                            Some(icon_users(13.0, FG)),
                        )
                        .on_press(|s: &mut AppState| {
                            s.discord.show_members = !s.discord.show_members
                        }),
                    );
            }
        }
    }

    let search = row()
        .w(220.0)
        .h(32.0)
        .px(10.0)
        .gap(6.0)
        .center_y()
        .radius(RADIUS_PILL)
        .fill(glass_control_fill())
        .border(1.0, glass_rim())
        .child(icon_search(13.0, fg_muted()))
        .child(
            text_input(&s.discord.search_query, type_small())
                .key("dc-search-input")
                .label("Filter Discord")
                .grow(1.0)
                .single_line()
                .color(FG)
                .placeholder("Filter…", fg_muted())
                .on_edit(edit_access(|s: &mut AppState| &mut s.discord.search_query)),
        );

    row()
        .key("dc-place-band")
        .w_full()
        .h(44.0)
        .px(GAP)
        .justify_between()
        .center_y()
        .child(row().gap(12.0).center_y().child(places).child(context_controls))
        .child(search)
}

fn voice_dock(s: &AppState) -> Option<El> {
    if s.discord.voice.state == "off" || s.discord.voice.state.is_empty() {
        return None;
    }
    let connected = s.discord.voice.state == "connected";
    let status_txt = if connected {
        if let Some(rtt) = s.discord.voice.rtt_ms {
            format!("Voice connected · {rtt} ms")
        } else {
            "Voice connected".into()
        }
    } else {
        "Connecting to voice…".into()
    };
    let label = s.discord.voice_channel_label();
    Some(
        row()
            .key("dc-voice-dock")
            .w_full()
            .px(10.0)
            .py(8.0)
            .gap(8.0)
            .center_y()
            .radius(RADIUS_CARD)
            .fill(if connected {
                good().alpha(0.11)
            } else {
                warn().alpha(0.11)
            })
            .border(
                1.0,
                if connected {
                    good().alpha(0.32)
                } else {
                    warn().alpha(0.32)
                },
            )
            .child(
                col()
                    .grow(1.0)
                    .min_w(0.0)
                    .gap(2.0)
                    .child(
                        row()
                            .gap(6.0)
                            .center_y()
                            .child(
                                el().size(7.0, 7.0)
                                    .radius(3.5)
                                    .fill(if connected { good() } else { warn() }),
                            )
                            .child(
                                text(status_txt)
                                    .font(type_small())
                                    .color(if connected { good() } else { warn() })
                                    .truncate(),
                            )
                            .child(if s.discord.voice.e2ee {
                                el().px(5.0)
                                    .py(1.0)
                                    .radius(4.0)
                                    .fill(subtle_fill())
                                    .child(text("E2EE").font(type_label()).color(fg_muted()))
                            } else {
                                el()
                            }),
                    )
                    .child(text(label).font(type_small()).color(FG).truncate()),
            )
            .child(
                icon_btn(
                    "dc-voice-leave".into(),
                    "Disconnect",
                    false,
                    true,
                    icon_phone_off(14.0, bad()),
                )
                .on_press(|s: &mut AppState| s.leave_discord_voice()),
            ),
    )
}

fn me_panel(s: &AppState, scale: f32) -> El {
    let me = s.discord.home.me.as_ref();
    let name = me.map(|m| m.name.as_str()).unwrap_or("Discord");
    let st = me.and_then(|m| m.status.as_deref()).unwrap_or("online");
    let muted = s.discord.voice.muted;
    let deaf = s.discord.voice.deafened;

    let mut stack = col().w_full().gap(6.0);

    if let Some(dock) = voice_dock(s) {
        stack = stack.child(dock);
    }

    if s.discord.status_menu_open {
        let mut menu = col()
            .key("dc-status-menu")
            .w_full()
            .px(6.0).py(6.0)
            .gap(2.0)
            .radius(RADIUS_CARD)
            .fill(card_surface())
            .border(1.0, glass_rim());
        for (code, title) in [
            ("online", "Online"),
            ("idle", "Idle"),
            ("dnd", "Do Not Disturb"),
            ("invisible", "Invisible"),
        ] {
            let active = st == code;
            menu = menu.child(
                row()
                    .key(format!("dc-set-status-{code}"))
                    .w_full()
                    .h(30.0)
                    .px(8.0)
                    .gap(8.0)
                    .center_y()
                    .radius(RADIUS_SMALL)
                    .fill(if active { hover_fill() } else { Color::TRANSPARENT })
                    .hover(hover_fill())
                    .hit()
                    .press_on_down()
                    .on_press(move |s: &mut AppState| s.set_discord_status(code))
                    .child(el().size(8.0, 8.0).radius(4.0).fill(status_color(code)))
                    .child(text(title).font(type_small()).color(FG).grow(1.0)),
            );
        }
        if s.discord.accounts.len() > 1 {
            menu = menu.child(el().w_full().h(1.0).fill(hairline()));
            for acc in &s.discord.accounts {
                if acc.current {
                    continue;
                }
                let aid = acc.id.clone();
                menu = menu.child(
                    row()
                        .key(format!("dc-switch-acc-{}", acc.id))
                        .w_full()
                        .h(30.0)
                        .px(8.0)
                        .gap(8.0)
                        .center_y()
                        .radius(RADIUS_SMALL)
                        .hover(hover_fill())
                        .hit()
                        .press_on_down()
                        .on_press(move |s: &mut AppState| s.switch_discord_account(&aid))
                        .child(
                            text(format!("Switch to {}", acc.name))
                                .font(type_small())
                                .color(fg_muted())
                                .truncate(),
                        ),
                );
            }
        }
        menu = menu.child(el().w_full().h(1.0).fill(hairline())).child(
            row()
                .key("dc-signout-row")
                .w_full()
                .h(30.0)
                .px(8.0)
                .center_y()
                .radius(RADIUS_SMALL)
                .hover(bad().alpha(0.16))
                .hit()
                .press_on_down()
                .on_press(|s: &mut AppState| s.sign_out_discord())
                .child(text("Sign out").font(type_small()).color(bad())),
        );
        stack = stack.child(menu);
    }

    let identity = row()
        .key("dc-me-trigger")
        .grow(1.0)
        .min_w(0.0)
        .gap(8.0)
        .center_y()
        .px(4.0).py(4.0)
        .radius(RADIUS_SMALL)
        .hover(hover_fill())
        .hit()
        .press_on_down()
        .on_press(|s: &mut AppState| s.discord.status_menu_open = !s.discord.status_menu_open)
        .child(avatar_box(
            s,
            me.and_then(|m| m.avatar.as_deref()),
            name,
            32.0,
            16.0,
            Some(st),
            me.is_some_and(|m| s.discord.speaking.contains(&m.id)),
            scale,
        ))
        .child(
            col()
                .grow(1.0)
                .min_w(0.0)
                .gap(1.0)
                .child(text(name.to_string()).font(type_control()).color(FG).truncate())
                .child(
                    text(status_label(st))
                        .font(type_label())
                        .color(fg_muted())
                        .truncate(),
                ),
        );

    let bar = row()
        .key("dc-me-panel")
        .w_full()
        .px(6.0)
        .py(6.0)
        .gap(4.0)
        .center_y()
        .radius(RADIUS_CARD)
        .fill(card_surface())
        .border(1.0, glass_rim())
        .child(identity)
        .child(
            icon_btn(
                "dc-me-mute".into(),
                if muted { "Unmute" } else { "Mute" },
                muted,
                muted,
                if muted {
                    icon_mic_off(14.0, bad())
                } else {
                    icon_mic(14.0, FG)
                },
            )
            .on_press(|s: &mut AppState| s.toggle_discord_mute()),
        )
        .child(
            icon_btn(
                "dc-me-deafen".into(),
                if deaf { "Undeafen" } else { "Deafen" },
                deaf,
                deaf,
                if deaf {
                    icon_headphones_off(14.0, bad())
                } else {
                    icon_headphones(14.0, FG)
                },
            )
            .on_press(|s: &mut AppState| s.toggle_discord_deafen()),
        )
        .child(
            icon_btn(
                "dc-me-settings".into(),
                "Settings",
                false,
                false,
                icon_settings(14.0, fg_muted()),
            )
            .on_press(|s: &mut AppState| s.switch_room(Room::Settings)),
        );

    stack.child(bar)
}

fn active_voice_rooms(s: &AppState) -> Vec<(Option<String>, String, String, String, Vec<DiscordVoiceMember>)> {
    let mut rooms = Vec::new();
    for dm in &s.discord.home.dms {
        if let Some(ref call) = dm.call
            && !call.is_empty()
        {
            rooms.push((
                None,
                dm.id.clone(),
                format!("Call · {}", dm.name),
                "Direct message".into(),
                call.clone(),
            ));
        }
    }
    for g in &s.discord.home.guilds {
        if g.muted {
            continue;
        }
        if let Some(chans) = s.discord.channels_by_guild.get(&g.id) {
            for ch in &chans.channels {
                if ch.kind == "voice"
                    && !ch.afk
                    && let Some(ref members) = ch.members
                    && !members.is_empty()
                {
                    rooms.push((
                        Some(g.id.clone()),
                        ch.id.clone(),
                        ch.name.clone(),
                        g.name.clone(),
                        members.clone(),
                    ));
                }
            }
        }
    }
    rooms
}

fn dm_row(s: &AppState, dm: &DiscordDm, active: bool, scale: f32) -> El {
    let dm_id = dm.id.clone();
    let sub = dm
        .subtitle
        .clone()
        .or_else(|| {
            if dm.group {
                let n = dm.members.as_ref().map(|m| m.len() + 1).unwrap_or(2);
                Some(format!("{n} members"))
            } else {
                None
            }
        })
        .unwrap_or_default();
    let name_color = if dm.muted {
        fg_faint()
    } else if dm.unread || active {
        FG
    } else {
        fg_muted()
    };
    let in_call = dm.call.as_ref().is_some_and(|c| !c.is_empty());

    let mut r = row()
        .key(format!("dc-dm-{}", dm.id))
        .w_full()
        .h(52.0)
        .px(10.0)
        .gap(10.0)
        .center_y()
        .radius(RADIUS_SMALL)
        .fill(if active { hover_fill() } else { Color::TRANSPARENT })
        .hover(hover_fill())
        .press(pressed_fill())
        .hit()
        .press_on_down()
        .press_scale(0.99)
        .on_press(move |s: &mut AppState| s.open_discord_dm(&dm_id))
        .child(avatar_box(
            s,
            dm.avatar.as_deref(),
            &dm.name,
            34.0,
            if dm.group { 10.0 } else { 17.0 },
            dm.status.as_deref(),
            false,
            scale,
        ))
        .child(
            col()
                .grow(1.0)
                .min_w(0.0)
                .gap(2.0)
                .child(text(dm.name.clone()).font(type_control()).color(name_color).truncate())
                .child(if !sub.is_empty() {
                    text(sub).font(type_label()).color(fg_faint()).truncate()
                } else {
                    el()
                }),
        );

    if in_call {
        r = r.child(icon_phone(13.0, good()));
    }
    if dm.muted {
        r = r.child(icon_bell_off(12.0, fg_faint()));
    } else if dm.mentions > 0 {
        r = r.child(badge_pill(dm.mentions));
    } else if dm.unread {
        r = r.child(unread_dot());
    }
    r
}

fn home_left_rail(s: &AppState, scale: f32) -> El {
    let voice_rooms = active_voice_rooms(s);
    let mut body = col().w_full().gap(12.0).pb(8.0);

    if !voice_rooms.is_empty() {
        let mut vc_col = col().w_full().gap(6.0).child(
            text("Active voice")
                .font(type_small())
                .color(fg_muted())
                .px(6.0),
        );
        for (gid, cid, cname, gname, members) in voice_rooms.iter().take(4) {
            let gid_c = gid.clone();
            let cid_c = cid.clone();
            let is_joined = s.discord.voice.channel_id.as_deref() == Some(cid.as_str())
                && s.discord.voice.state != "off";
            vc_col = vc_col.child(
                row()
                    .key(format!("dc-rail-vc-{cid}"))
                    .w_full()
                    .px(10.0)
                    .py(8.0)
                    .gap(8.0)
                    .center_y()
                    .radius(RADIUS_SMALL)
                    .fill(if is_joined {
                        good().alpha(0.14)
                    } else {
                        subtle_fill()
                    })
                    .hover(hover_fill())
                    .hit()
                    .press_on_down()
                    .on_press(move |s: &mut AppState| {
                        if is_joined {
                            s.leave_discord_voice();
                        } else {
                            s.join_discord_voice(gid_c.as_deref(), &cid_c);
                        }
                    })
                    .child(icon_volume(14.0, if is_joined { good() } else { FG }))
                    .child(
                        col()
                            .grow(1.0)
                            .min_w(0.0)
                            .gap(1.0)
                            .child(text(cname.clone()).font(type_control()).color(FG).truncate())
                            .child(
                                text(format!("{gname} · {}", members.len()))
                                    .font(type_label())
                                    .color(fg_muted())
                                    .truncate(),
                            ),
                    )
                    .child(
                        text(if is_joined { "In call" } else { "Join" })
                            .font(type_label())
                            .color(if is_joined { good() } else { FG }),
                    ),
            );
        }
        body = body.child(vc_col);
    }

    let mut dm_col = col().w_full().gap(2.0).child(
        row()
            .w_full()
            .px(6.0)
            .justify_between()
            .center_y()
            .child(text("Conversations").font(type_small()).color(fg_muted()))
            .child(
                text(format!("{}", s.discord.home.dms.len()))
                    .font(type_label())
                    .color(fg_faint()),
            ),
    );
    for dm in s.discord.home.dms.iter().take(8) {
        dm_col = dm_col.child(dm_row(s, dm, false, scale));
    }
    body = body.child(dm_col);

    col()
        .key("dc-left-rail")
        .w(268.0)
        .h_full()
        .px(8.0).py(8.0)
        .gap(8.0)
        .radius(RADIUS_PANEL)
        .fill(pane_surface())
        .border(1.0, glass_rim())
        .child(
            col()
                .grow(1.0)
                .w_full()
                .min_h(0.0)
                .scroll(ScrollSpec { stick_bottom: false })
                .child(body),
        )
        .child(me_panel(s, scale))
}

fn messages_left_rail(s: &AppState, scale: f32) -> El {
    let q = s.discord.search_query.text.trim().to_lowercase();
    let mut list = col().w_full().gap(2.0).pb(8.0);
    let mut shown = 0usize;
    for dm in &s.discord.home.dms {
        if !q.is_empty() && !dm.name.to_lowercase().contains(&q) {
            continue;
        }
        let active = s.discord.active_dm_id.as_deref() == Some(dm.id.as_str());
        list = list.child(dm_row(s, dm, active, scale));
        shown += 1;
    }
    if shown == 0 {
        list = list.child(
            text("No matching conversations")
                .font(type_small())
                .color(fg_muted())
                .px(12.0).py(12.0),
        );
    }

    col()
        .key("dc-messages-rail")
        .w(268.0)
        .h_full()
        .px(8.0).py(8.0)
        .gap(8.0)
        .radius(RADIUS_PANEL)
        .fill(pane_surface())
        .border(1.0, glass_rim())
        .child(
            row()
                .w_full()
                .px(6.0)
                .py(4.0)
                .justify_between()
                .center_y()
                .child(text("Messages").font(type_section()).color(FG))
                .child(
                    text(format!("{} chats", s.discord.home.dms.len()))
                        .font(type_label())
                        .color(fg_muted()),
                ),
        )
        .child(
            col()
                .grow(1.0)
                .w_full()
                .min_h(0.0)
                .scroll(ScrollSpec { stick_bottom: false })
                .child(list),
        )
        .child(me_panel(s, scale))
}

fn guild_channel_row(
    s: &AppState,
    guild_id: &str,
    ch: &DiscordChannel,
    active: bool,
    scale: f32,
) -> El {
    let cid = ch.id.clone();
    let gid = guild_id.to_string();
    let is_voice = ch.kind == "voice";
    let in_this_voice = is_voice
        && s.discord.voice.channel_id.as_deref() == Some(ch.id.as_str())
        && s.discord.voice.state != "off";
    let name_color = if in_this_voice {
        good()
    } else if ch.muted {
        fg_faint()
    } else if active || ch.unread {
        FG
    } else {
        fg_muted()
    };

    let icon = if is_voice {
        icon_volume(14.0, if in_this_voice { good() } else { fg_muted() })
    } else if ch.locked {
        icon_lock(13.0, fg_muted())
    } else {
        icon_hash(14.0, if active || ch.unread { FG } else { fg_muted() })
    };

    let mut head = row()
        .key(format!("dc-ch-{}", ch.id))
        .w_full()
        .h(34.0)
        .px(8.0)
        .gap(8.0)
        .center_y()
        .radius(RADIUS_SMALL)
        .fill(if active { hover_fill() } else { Color::TRANSPARENT })
        .hover(hover_fill())
        .press(pressed_fill())
        .hit()
        .press_on_down()
        .on_press(move |s: &mut AppState| {
            s.open_discord_channel(&cid);
            if is_voice && !in_this_voice {
                s.join_discord_voice(Some(&gid), &cid);
            }
        })
        .child(icon)
        .child(text(ch.name.clone()).font(type_control()).color(name_color).grow(1.0).truncate());

    if is_voice {
        let count = ch.members.as_ref().map(|m| m.len()).unwrap_or(0);
        if let Some(lim) = ch.user_limit
            && lim > 0
        {
            head = head.child(
                text(format!("{count}/{lim}"))
                    .font(type_label())
                    .color(fg_faint()),
            );
        } else if count > 0 {
            head = head.child(text(count.to_string()).font(type_label()).color(good()));
        }
    } else if ch.mentions > 0 {
        head = head.child(badge_pill(ch.mentions));
    } else if ch.unread && !ch.muted {
        head = head.child(unread_dot());
    }

    let mut block = col().w_full().gap(2.0).child(head);
    if is_voice
        && let Some(ref members) = ch.members
        && !members.is_empty()
    {
        for m in members {
            let speaking = s.discord.speaking.contains(&m.id);
            let mut mrow = row()
                .key(format!("dc-vc-mem-{}-{}", ch.id, m.id))
                .w_full()
                .h(26.0)
                .pl(28.0)
                .pr(8.0)
                .gap(7.0)
                .center_y()
                .child(avatar_box(
                    s,
                    m.avatar.as_deref(),
                    &m.name,
                    20.0,
                    10.0,
                    None,
                    speaking,
                    scale,
                ))
                .child(
                    text(m.name.clone())
                        .font(type_small())
                        .color(if speaking { FG } else { fg_muted() })
                        .grow(1.0)
                        .truncate(),
                );
            if m.stream {
                mrow = mrow.child(
                    el().px(5.0)
                        .py(1.0)
                        .radius(4.0)
                        .fill(bad().alpha(0.22))
                        .child(text("LIVE").font(type_label()).color(bad())),
                );
            }
            if m.deaf || m.server_deaf {
                mrow = mrow.child(icon_headphones_off(11.0, fg_faint()));
            } else if m.mute || m.server_mute {
                mrow = mrow.child(icon_mic_off(11.0, fg_faint()));
            }
            block = block.child(mrow);
        }
    }
    block
}

fn server_left_rail(s: &AppState, guild: &DiscordGuild, scale: f32) -> El {
    let mut strip = row()
        .w_full()
        .gap(6.0)
        .pb(4.0).flex_wrap();
    for g in s.discord.home.guilds.iter().take(6) {
        let gid = g.id.clone();
        let active = g.id == guild.id;
        strip = strip.child(
            el().key(format!("dc-strip-{}", g.id))
                .role("button")
                .label(g.name.clone())
                .focusable(true)
                .hit()
                .press_on_down()
                .press_scale(PRESS_SCALE)
                .size(34.0, 34.0)
                .radius(10.0)
                .border(
                    if active { 1.5 } else { 1.0 },
                    if active { FG } else { hairline() },
                )
                .on_press(move |s: &mut AppState| s.open_discord_guild(&gid))
                .child(avatar_box(
                    s,
                    g.icon.as_deref(),
                    &g.name,
                    32.0,
                    9.0,
                    None,
                    false,
                    scale,
                )),
        );
    }

    let q = s.discord.search_query.text.trim().to_lowercase();
    let mut tree = col().w_full().gap(2.0).pb(8.0);
    if let Some(chans) = s.discord.channels_by_guild.get(&guild.id) {
        let mut current_cat_collapsed = false;
        for ch in &chans.channels {
            if ch.resource {
                continue;
            }
            if ch.kind == "category" {
                let cid = ch.id.clone();
                let collapsed = s.discord.collapsed_categories.contains(&ch.id);
                current_cat_collapsed = collapsed;
                tree = tree.child(
                    row()
                        .key(format!("dc-cat-{}", ch.id))
                        .w_full()
                        .h(28.0)
                        .pt(6.0)
                        .px(6.0)
                        .gap(4.0)
                        .center_y()
                        .hover(hover_fill())
                        .radius(RADIUS_SMALL)
                        .hit()
                        .press_on_down()
                        .on_press(move |s: &mut AppState| s.toggle_discord_category(&cid))
                        .child(if collapsed {
                            icon_chevron_right(11.0, fg_faint())
                        } else {
                            icon_chevron_down(11.0, fg_faint())
                        })
                        .child(
                            text(ch.name.clone())
                                .font(type_label())
                                .color(fg_faint())
                                .truncate(),
                        ),
                );
                continue;
            }
            if !q.is_empty() && !ch.name.to_lowercase().contains(&q) {
                continue;
            }
            let active = s.discord.active_channel_id.as_deref() == Some(ch.id.as_str());
            let keep_visible = active
                || ch.unread
                || ch.mentions > 0
                || ch.members.as_ref().is_some_and(|m| !m.is_empty());
            if current_cat_collapsed && !keep_visible && q.is_empty() {
                continue;
            }
            tree = tree.child(guild_channel_row(s, &guild.id, ch, active, scale));
        }
    } else {
        tree = tree.child(
            text("Loading channels…")
                .font(type_small())
                .color(fg_muted())
                .px(12.0).py(12.0),
        );
    }

    col()
        .key("dc-server-rail")
        .w(268.0)
        .h_full()
        .px(8.0).py(8.0)
        .gap(8.0)
        .radius(RADIUS_PANEL)
        .fill(pane_surface())
        .border(1.0, glass_rim())
        .child(strip)
        .child(
            row()
                .w_full()
                .px(6.0)
                .py(2.0)
                .gap(8.0)
                .center_y()
                .child(avatar_box(
                    s,
                    guild.icon.as_deref(),
                    &guild.name,
                    26.0,
                    8.0,
                    None,
                    false,
                    scale,
                ))
                .child(
                    text(guild.name.clone())
                        .font(type_section())
                        .color(FG)
                        .grow(1.0)
                        .truncate(),
                ),
        )
        .child(el().w_full().h(1.0).fill(hairline()))
        .child(
            col()
                .grow(1.0)
                .w_full()
                .min_h(0.0)
                .scroll(ScrollSpec { stick_bottom: false })
                .child(tree),
        )
        .child(me_panel(s, scale))
}

fn home_now_center(s: &AppState, scale: f32) -> El {
    let q = s.discord.search_query.text.trim().to_lowercase();
    let online_friends: Vec<&DiscordFriend> = s
        .discord
        .friends
        .iter()
        .filter(|f| f.kind == "friend" && f.status != "offline")
        .filter(|f| {
            q.is_empty()
                || f.name.to_lowercase().contains(&q)
                || f.activity
                    .as_deref()
                    .is_some_and(|a| a.to_lowercase().contains(&q))
        })
        .collect();

    let mut by_game: BTreeMap<String, Vec<&DiscordFriend>> = BTreeMap::new();
    let mut hanging_out: Vec<&DiscordFriend> = Vec::new();
    for f in &online_friends {
        let is_playing = f.activity_type == Some(0)
            || f.rich.as_ref().is_some_and(|r| r.activity_type == 0);
        if is_playing
            && let Some(game_name) = f
                .rich
                .as_ref()
                .map(|r| r.name.as_str())
                .or(f.activity.as_deref())
            && !game_name.is_empty()
        {
            by_game.entry(game_name.to_string()).or_default().push(f);
        } else {
            hanging_out.push(f);
        }
    }

    let mut content = col().w_full().gap(16.0).pb(24.0);

    if !by_game.is_empty() {
        let mut game_cards = row().w_full().gap(12.0).flex_wrap();
        for (game_title, players) in &by_game {
            let matched = s
                .games
                .iter()
                .find(|g| g.title.eq_ignore_ascii_case(game_title));
            let via_exo = players
                .iter()
                .any(|p| p.rich.as_ref().is_some_and(|r| r.through_exo));
            let details = players
                .iter()
                .find_map(|p| p.rich.as_ref().and_then(|r| r.details.clone()));

            let mut faces = row().w_full().gap(10.0).flex_wrap();
            for p in players {
                let uid = p.id.clone();
                faces = faces.child(
                    row()
                        .key(format!("dc-now-player-{}", p.id))
                        .px(8.0)
                        .py(6.0)
                        .gap(8.0)
                        .center_y()
                        .radius(RADIUS_CARD)
                        .fill(subtle_fill())
                        .hover(hover_fill())
                        .hit()
                        .press_on_down()
                        .press_scale(PRESS_SCALE)
                        .on_press(move |s: &mut AppState| s.open_dm_with_friend(&uid))
                        .child(avatar_box(
                            s,
                            p.avatar.as_deref(),
                            &p.name,
                            34.0,
                            17.0,
                            Some(&p.status),
                            false,
                            scale,
                        ))
                        .child(
                            col()
                                .gap(1.0)
                                .child(text(p.name.clone()).font(type_control()).color(FG))
                                .child(
                                    text(if p.rich.as_ref().is_some_and(|r| r.through_exo) {
                                        "On Exo"
                                    } else {
                                        "Message"
                                    })
                                    .font(type_label())
                                    .color(fg_muted()),
                                ),
                        ),
                );
            }

            let mut head = row()
                .w_full()
                .justify_between()
                .center_y()
                .gap(10.0)
                .child(
                    col()
                        .grow(1.0)
                        .min_w(0.0)
                        .gap(3.0)
                        .child(
                            row()
                                .gap(8.0)
                                .center_y()
                                .child(el().size(8.0, 8.0).radius(4.0).fill(good()))
                                .child(
                                    text(format!("Playing {game_title}"))
                                        .font(type_section())
                                        .color(FG)
                                        .truncate(),
                                )
                                .child(if via_exo {
                                    el().px(6.0)
                                        .py(2.0)
                                        .radius(RADIUS_PILL)
                                        .fill(good().alpha(0.16))
                                        .child(text("Exo").font(type_label()).color(good()))
                                } else {
                                    el()
                                }),
                        )
                        .child(if let Some(d) = details {
                            text(d).font(type_small()).color(fg_muted()).truncate()
                        } else {
                            text(format!(
                                "{} {} playing",
                                players.len(),
                                if players.len() == 1 { "friend" } else { "friends" }
                            ))
                            .font(type_small())
                            .color(fg_muted())
                        }),
                );

            if let Some(g) = matched {
                let gid = g.id.clone();
                let installed = g.installed;
                head = head.child(
                    primary_button(
                        if installed { "dc-now-play" } else { "dc-now-open" },
                        if installed { "Play" } else { "View" },
                        Some(icon_play(12.0, PRIMARY_FG)),
                    )
                    .on_press(move |s: &mut AppState| {
                        if installed {
                            s.launch_game(&gid);
                        } else {
                            s.switch_room(Room::Library);
                            s.open_game_page(&gid);
                        }
                    }),
                );
            }

            game_cards = game_cards.child(
                col()
                    .key(format!("dc-game-card-{game_title}"))
                    .grow(1.0)
                    .min_w(340.0)
                    .px(16.0).py(16.0)
                    .gap(12.0)
                    .radius(RADIUS_PANEL)
                    .fill(card_surface())
                    .border(1.0, glass_rim())
                    .child(head)
                    .child(faces),
            );
        }
        content = content
            .child(text("In game now").font(type_title()).color(FG))
            .child(game_cards);
    }

    let voice_rooms = active_voice_rooms(s);
    if !voice_rooms.is_empty() {
        let mut vc_row = row().w_full().gap(12.0).flex_wrap();
        for (gid, cid, cname, gname, members) in &voice_rooms {
            let gid_c = gid.clone();
            let cid_c = cid.clone();
            let is_joined = s.discord.voice.channel_id.as_deref() == Some(cid.as_str())
                && s.discord.voice.state != "off";
            let mut faces = row().gap(8.0).flex_wrap();
            for m in members.iter().take(6) {
                faces = faces.child(
                    row()
                        .px(8.0)
                        .py(5.0)
                        .gap(6.0)
                        .center_y()
                        .radius(RADIUS_PILL)
                        .fill(subtle_fill())
                        .child(avatar_box(
                            s,
                            m.avatar.as_deref(),
                            &m.name,
                            22.0,
                            11.0,
                            None,
                            s.discord.speaking.contains(&m.id),
                            scale,
                        ))
                        .child(text(m.name.clone()).font(type_small()).color(FG)),
                );
            }
            vc_row = vc_row.child(
                col()
                    .key(format!("dc-now-vc-{cid}"))
                    .grow(1.0)
                    .min_w(300.0)
                    .px(16.0).py(16.0)
                    .gap(12.0)
                    .radius(RADIUS_PANEL)
                    .fill(card_surface())
                    .border(
                        1.0,
                        if is_joined {
                            good().alpha(0.4)
                        } else {
                            glass_rim()
                        },
                    )
                    .child(
                        row()
                            .w_full()
                            .justify_between()
                            .center_y()
                            .child(
                                col()
                                    .gap(2.0)
                                    .child(text(cname.clone()).font(type_section()).color(FG))
                                    .child(text(gname.clone()).font(type_small()).color(fg_muted())),
                            )
                            .child(
                                if is_joined {
                                    glass_button(
                                        "dc-now-vc-leave",
                                        "Leave",
                                        Some(icon_phone_off(13.0, bad())),
                                    )
                                } else {
                                    primary_button(
                                        "dc-now-vc-join",
                                        "Join",
                                        Some(icon_phone(13.0, PRIMARY_FG)),
                                    )
                                }
                                .on_press(move |s: &mut AppState| {
                                    if is_joined {
                                        s.leave_discord_voice();
                                    } else {
                                        s.join_discord_voice(gid_c.as_deref(), &cid_c);
                                    }
                                }),
                            ),
                    )
                    .child(faces),
            );
        }
        content = content
            .child(text("Voice rooms").font(type_title()).color(FG))
            .child(vc_row);
    }

    if !hanging_out.is_empty() {
        let mut grid = row().w_full().gap(10.0).flex_wrap();
        for f in &hanging_out {
            let uid = f.id.clone();
            let sub = f
                .activity
                .clone()
                .unwrap_or_else(|| status_label(&f.status).into());
            grid = grid.child(
                row()
                    .key(format!("dc-hang-{}", f.id))
                    .w(240.0)
                    .px(10.0).py(10.0)
                    .gap(10.0)
                    .center_y()
                    .radius(RADIUS_CARD)
                    .fill(card_surface())
                    .border(1.0, hairline())
                    .hover(hover_fill())
                    .hit()
                    .press_on_down()
                    .press_scale(PRESS_SCALE)
                    .on_press(move |s: &mut AppState| s.open_dm_with_friend(&uid))
                    .child(avatar_box(
                        s,
                        f.avatar.as_deref(),
                        &f.name,
                        38.0,
                        19.0,
                        Some(&f.status),
                        false,
                        scale,
                    ))
                    .child(
                        col()
                            .grow(1.0)
                            .min_w(0.0)
                            .gap(2.0)
                            .child(text(f.name.clone()).font(type_control()).color(FG).truncate())
                            .child(text(sub).font(type_label()).color(fg_muted()).truncate()),
                    ),
            );
        }
        content = content
            .child(text("Hanging out").font(type_title()).color(FG))
            .child(grid);
    }

    if by_game.is_empty() && voice_rooms.is_empty() && hanging_out.is_empty() {
        content = content.child(
            col()
                .w_full()
                .py(80.0)
                .gap(8.0)
                .center()
                .child(text("Quiet right now").font(type_title()).color(FG))
                .child(
                    text("Friends playing games or sitting in voice channels will appear here automatically.")
                        .font(type_body())
                        .color(fg_muted()),
                ),
        );
    }

    content
}

fn home_friends_center(s: &AppState, scale: f32) -> El {
    let q = s.discord.search_query.text.trim().to_lowercase();
    let mut content = col().w_full().gap(14.0).pb(24.0);

    if s.discord.add_friend_open {
        content = content.child(
            row()
                .key("dc-add-friend-bar")
                .w_full()
                .px(12.0).py(12.0)
                .gap(10.0)
                .center_y()
                .radius(RADIUS_CARD)
                .fill(card_surface())
                .border(1.0, glass_rim())
                .child(
                    text_input(&s.discord.add_friend_input, type_body())
                        .key("dc-add-friend-input")
                        .label("Discord username")
                        .grow(1.0)
                        .single_line()
                        .color(FG)
                        .placeholder("Enter a Discord username…", fg_muted())
                        .on_edit(edit_access(|s: &mut AppState| &mut s.discord.add_friend_input))
                        .on_submit(|s: &mut AppState, _| s.submit_add_discord_friend()),
                )
                .child(
                    primary_button("dc-add-friend-send", "Send request", None)
                        .on_press(|s: &mut AppState| s.submit_add_discord_friend()),
                ),
        );
    }

    let pending: Vec<&DiscordFriend> = s
        .discord
        .friends
        .iter()
        .filter(|f| f.kind == "incoming" || f.kind == "outgoing")
        .collect();
    if !pending.is_empty() {
        let mut list = col().w_full().gap(6.0);
        for f in pending {
            let uid_a = f.id.clone();
            let uid_r = f.id.clone();
            let incoming = f.kind == "incoming";
            let mut r = row()
                .key(format!("dc-req-{}", f.id))
                .w_full()
                .px(12.0)
                .py(10.0)
                .gap(10.0)
                .center_y()
                .radius(RADIUS_CARD)
                .fill(card_surface())
                .border(1.0, glass_rim())
                .child(avatar_box(
                    s,
                    f.avatar.as_deref(),
                    &f.name,
                    36.0,
                    18.0,
                    Some(&f.status),
                    false,
                    scale,
                ))
                .child(
                    col()
                        .grow(1.0)
                        .min_w(0.0)
                        .gap(2.0)
                        .child(text(f.name.clone()).font(type_control()).color(FG).truncate())
                        .child(
                            text(if incoming {
                                "Incoming friend request"
                            } else {
                                "Outgoing request"
                            })
                            .font(type_small())
                            .color(fg_muted()),
                        ),
                );
            if incoming {
                r = r.child(
                    primary_button("dc-req-accept", "Accept", None)
                        .on_press(move |s: &mut AppState| s.accept_discord_friend(&uid_a)),
                );
            }
            r = r.child(
                glass_button(
                    "dc-req-ignore",
                    if incoming { "Ignore" } else { "Cancel" },
                    None,
                )
                .on_press(move |s: &mut AppState| s.remove_discord_friend(&uid_r)),
            );
            list = list.child(r);
        }
        content = content
            .child(text("Friend requests").font(type_section()).color(FG))
            .child(list);
    }

    let friends: Vec<&DiscordFriend> = s
        .discord
        .friends
        .iter()
        .filter(|f| f.kind == "friend")
        .filter(|f| {
            q.is_empty()
                || f.name.to_lowercase().contains(&q)
                || f.username
                    .as_deref()
                    .is_some_and(|u| u.to_lowercase().contains(&q))
        })
        .collect();
    let mut flist = col().w_full().gap(4.0);
    for f in &friends {
        let uid = f.id.clone();
        let sub = f
            .activity
            .clone()
            .unwrap_or_else(|| status_label(&f.status).into());
        flist = flist.child(
            row()
                .key(format!("dc-friend-{}", f.id))
                .w_full()
                .px(12.0)
                .py(9.0)
                .gap(10.0)
                .center_y()
                .radius(RADIUS_CARD)
                .fill(card_surface())
                .border(1.0, hairline())
                .hover(hover_fill())
                .child(avatar_box(
                    s,
                    f.avatar.as_deref(),
                    &f.name,
                    36.0,
                    18.0,
                    Some(&f.status),
                    false,
                    scale,
                ))
                .child(
                    col()
                        .grow(1.0)
                        .min_w(0.0)
                        .gap(2.0)
                        .child(text(f.name.clone()).font(type_control()).color(FG).truncate())
                        .child(text(sub).font(type_small()).color(fg_muted()).truncate()),
                )
                .child(
                    glass_button("dc-friend-msg", "Message", Some(icon_chat(13.0, FG)))
                        .on_press(move |s: &mut AppState| s.open_dm_with_friend(&uid)),
                ),
        );
    }
    content = content
        .child(
            text(format!("All friends · {}", friends.len()))
                .font(type_section())
                .color(FG),
        )
        .child(flist);
    content
}

fn servers_directory(s: &AppState, scale: f32) -> El {
    let q = s.discord.search_query.text.trim().to_lowercase();
    let mut root = col().w_full().gap(18.0).pb(24.0);

    let tile_of = |g: &DiscordGuild| -> El {
        let gid = g.id.clone();
        let wash = if let Some(img) = g.icon.as_deref().and_then(|u| s.remote_image(u, scale)) {
            el().pin(Some(0.0), Some(0.0), Some(0.0), Some(0.0))
                .pixels_fit(img.pixels(), SurfaceFit::Fill)
                .blur(20.0)
                .opacity(0.45)
        } else {
            el().pin(Some(0.0), Some(0.0), Some(0.0), Some(0.0))
                .fill(monogram_fill(&g.name))
        };

        let mut header_badges = row()
            .pin(None, Some(8.0), Some(8.0), None)
            .gap(6.0)
            .center_y();
        if g.in_voice > 0 {
            header_badges = header_badges.child(
                row()
                    .h(20.0)
                    .px(7.0)
                    .gap(4.0)
                    .center_y()
                    .radius(RADIUS_PILL)
                    .fill(BG.alpha(0.75))
                    .border(1.0, good().alpha(0.5))
                    .child(icon_volume(11.0, good()))
                    .child(text(g.in_voice.to_string()).font(type_label()).color(good())),
            );
        }
        if g.mentions > 0 {
            header_badges = header_badges.child(badge_pill(g.mentions));
        }

        col()
            .key(format!("dc-guild-tile-{}", g.id))
            .role("button")
            .label(g.name.clone())
            .focusable(true)
            .hit()
            .press_on_down()
            .press_scale(PRESS_SCALE)
            .w(232.0)
            .radius(RADIUS_CARD)
            .clip_rounded(RADIUS_CARD)
            .fill(card_surface())
            .border(1.0, glass_rim())
            .hover(hover_fill())
            .motion(SPRING_QUICK)
            .on_press(move |s: &mut AppState| s.open_discord_guild(&gid))
            .child(
                el().w_full()
                    .h(62.0)
                    .clip_rounded(0.0)
                    .child(wash)
                    .child(header_badges),
            )
            .child(
                row()
                    .w_full()
                    .px(12.0)
                    .py(12.0)
                    .gap(10.0)
                    .center_y()
                    .child(avatar_box(
                        s,
                        g.icon.as_deref(),
                        &g.name,
                        38.0,
                        11.0,
                        None,
                        false,
                        scale,
                    ))
                    .child(
                        col()
                            .grow(1.0)
                            .min_w(0.0)
                            .gap(2.0)
                            .child(
                                text(g.name.clone())
                                    .font(type_control())
                                    .color(if g.muted { fg_muted() } else { FG })
                                    .truncate(),
                            )
                            .child(
                                text(if g.in_voice > 0 {
                                    format!("{} in voice", g.in_voice)
                                } else if g.unread && !g.muted {
                                    "Unread messages".into()
                                } else if g.muted {
                                    "Muted".into()
                                } else {
                                    "Server".into()
                                })
                                .font(type_label())
                                .color(if g.in_voice > 0 { good() } else { fg_faint() }),
                            ),
                    )
                    .child(if g.unread && !g.muted {
                        unread_dot()
                    } else {
                        el()
                    }),
            )
    };

    if s.discord.home.rail.is_empty() || !q.is_empty() {
        let mut grid = row().w_full().gap(12.0).flex_wrap();
        for g in &s.discord.home.guilds {
            if !q.is_empty() && !g.name.to_lowercase().contains(&q) {
                continue;
            }
            grid = grid.child(tile_of(g));
        }
        root = root
            .child(
                text(format!("Servers · {}", s.discord.home.guilds.len()))
                    .font(type_title())
                    .color(FG),
            )
            .child(grid);
        return root;
    }

    let mut standalone = Vec::new();
    for item in &s.discord.home.rail {
        if let Some(ref fid) = item.id {
            let folder_guilds: Vec<&DiscordGuild> = item
                .guild_ids
                .iter()
                .filter_map(|id| s.discord.home.guilds.iter().find(|g| g.id == *id))
                .collect();
            if folder_guilds.is_empty() {
                continue;
            }
            if !standalone.is_empty() {
                let mut grid = row().w_full().gap(12.0).flex_wrap();
                for g in standalone.drain(..) {
                    grid = grid.child(tile_of(g));
                }
                root = root.child(grid);
            }
            let collapsed = s.discord.collapsed_folders.contains(fid);
            let fid_click = fid.clone();
            let title = item
                .name
                .clone()
                .filter(|n| !n.is_empty())
                .unwrap_or_else(|| "Folder".into());
            let mut section = col().w_full().gap(10.0).child(
                row()
                    .key(format!("dc-folder-{fid}"))
                    .px(8.0)
                    .py(5.0)
                    .gap(6.0)
                    .center_y()
                    .radius(RADIUS_SMALL)
                    .hover(hover_fill())
                    .hit()
                    .press_on_down()
                    .on_press(move |s: &mut AppState| s.toggle_discord_folder(&fid_click))
                    .child(if collapsed {
                        icon_chevron_right(13.0, fg_muted())
                    } else {
                        icon_chevron_down(13.0, fg_muted())
                    })
                    .child(text(title).font(type_section()).color(FG))
                    .child(
                        text(format!("{} servers", folder_guilds.len()))
                            .font(type_small())
                            .color(fg_muted()),
                    ),
            );
            if !collapsed {
                let mut fgrid = row().w_full().gap(12.0).flex_wrap();
                for g in folder_guilds {
                    fgrid = fgrid.child(tile_of(g));
                }
                section = section.child(fgrid);
            }
            root = root.child(section);
        } else {
            for id in &item.guild_ids {
                if let Some(g) = s.discord.home.guilds.iter().find(|g| g.id == *id) {
                    standalone.push(g);
                }
            }
        }
    }
    if !standalone.is_empty() {
        let mut grid = row().w_full().gap(12.0).flex_wrap();
        for g in standalone {
            grid = grid.child(tile_of(g));
        }
        root = root.child(grid);
    }
    root
}

fn channel_or_dm_stage(s: &AppState, scale: f32) -> El {
    match s.discord.place {
        DiscordPlace::Messages => {
            let Some(dm) = s.discord.active_dm() else {
                return col()
                    .grow(1.0)
                    .h_full()
                    .center()
                    .child(text("Select a conversation").font(type_section()).color(fg_muted()));
            };
            let dm_id = dm.id.clone();
            let in_call = s.discord.voice.channel_id.as_deref() == Some(dm.id.as_str())
                && s.discord.voice.state != "off";

            let header = row()
                .w_full()
                .h(52.0)
                .px(16.0)
                .gap(10.0)
                .justify_between()
                .center_y()
                .border(1.0, hairline())
                .radius(RADIUS_CARD)
                .fill(pane_surface())
                .child(
                    row()
                        .grow(1.0)
                        .min_w(0.0)
                        .gap(10.0)
                        .center_y()
                        .child(avatar_box(
                            s,
                            dm.avatar.as_deref(),
                            &dm.name,
                            30.0,
                            if dm.group { 9.0 } else { 15.0 },
                            dm.status.as_deref(),
                            false,
                            scale,
                        ))
                        .child(
                            col()
                                .grow(1.0)
                                .min_w(0.0)
                                .gap(1.0)
                                .child(text(dm.name.clone()).font(type_section()).color(FG).truncate())
                                .child(
                                    text(
                                        dm.subtitle
                                            .clone()
                                            .or_else(|| dm.username.clone().map(|u| format!("@{u}")))
                                            .unwrap_or_else(|| "Direct message".into()),
                                    )
                                    .font(type_label())
                                    .color(fg_muted())
                                    .truncate(),
                                ),
                        ),
                )
                .child(
                    if in_call {
                        glass_button("dc-dm-leave", "End call", Some(icon_phone_off(13.0, bad())))
                    } else {
                        glass_button("dc-dm-call", "Voice call", Some(icon_phone(13.0, FG)))
                    }
                    .on_press(move |s: &mut AppState| {
                        if in_call {
                            s.leave_discord_voice();
                        } else {
                            s.join_discord_voice(None, &dm_id);
                        }
                    }),
                );

            let body = col()
                .grow(1.0)
                .w_full()
                .px(24.0).py(24.0)
                .gap(12.0)
                .justify_end()
                .child(avatar_box(
                    s,
                    dm.avatar.as_deref(),
                    &dm.name,
                    64.0,
                    if dm.group { 18.0 } else { 32.0 },
                    dm.status.as_deref(),
                    false,
                    scale,
                ))
                .child(text(dm.name.clone()).font(type_page()).color(FG))
                .child(
                    text(
                        dm.line
                            .clone()
                            .unwrap_or_else(|| "Conversation ready for Phase 5b live timeline & composer.".into()),
                    )
                    .font(type_body())
                    .color(fg_muted())
                    .w_full()
                    .wrap(),
                );

            col()
                .key("dc-dm-stage")
                .grow(1.0)
                .min_w(0.0)
                .h_full()
                .gap(GAP)
                .child(header)
                .child(
                    col()
                        .grow(1.0)
                        .w_full()
                        .min_h(0.0)
                        .radius(RADIUS_PANEL)
                        .fill(pane_surface())
                        .border(1.0, hairline())
                        .child(body),
                )
        }
        DiscordPlace::Servers => {
            let Some(ch) = s.discord.active_channel() else {
                return col()
                    .grow(1.0)
                    .h_full()
                    .center()
                    .child(text("Pick a channel").font(type_section()).color(fg_muted()));
            };
            let gid = s.discord.active_guild_id.clone();
            let cid = ch.id.clone();
            let is_voice = ch.kind == "voice";
            let in_this_voice = is_voice
                && s.discord.voice.channel_id.as_deref() == Some(ch.id.as_str())
                && s.discord.voice.state != "off";

            let mut header = row()
                .w_full()
                .h(52.0)
                .px(16.0)
                .gap(10.0)
                .justify_between()
                .center_y()
                .radius(RADIUS_CARD)
                .fill(pane_surface())
                .border(1.0, hairline())
                .child(
                    row()
                        .grow(1.0)
                        .min_w(0.0)
                        .gap(8.0)
                        .center_y()
                        .child(if is_voice {
                            icon_volume(16.0, if in_this_voice { good() } else { fg_muted() })
                        } else {
                            icon_hash(16.0, fg_muted())
                        })
                        .child(text(ch.name.clone()).font(type_section()).color(FG))
                        .child(if let Some(ref topic) = ch.topic {
                            text(format!("· {topic}"))
                                .font(type_small())
                                .color(fg_muted())
                                .grow(1.0)
                                .truncate()
                        } else if let Some(ref st) = ch.status {
                            text(format!("· {st}"))
                                .font(type_small())
                                .color(fg_muted())
                                .grow(1.0)
                                .truncate()
                        } else {
                            el()
                        }),
                );

            if is_voice {
                let gid_v = gid.clone();
                let cid_v = cid.clone();
                header = header.child(
                    if in_this_voice {
                        glass_button("dc-stage-leave", "Disconnect", Some(icon_phone_off(13.0, bad())))
                    } else {
                        primary_button("dc-stage-join", "Join voice", Some(icon_phone(13.0, PRIMARY_FG)))
                    }
                    .on_press(move |s: &mut AppState| {
                        if in_this_voice {
                            s.leave_discord_voice();
                        } else {
                            s.join_discord_voice(gid_v.as_deref(), &cid_v);
                        }
                    }),
                );
            }

            let stage_body = if is_voice {
                let mut tiles = row().w_full().gap(14.0).flex_wrap().justify_center();
                if let Some(ref members) = ch.members {
                    for m in members {
                        let speaking = s.discord.speaking.contains(&m.id);
                        tiles = tiles.child(
                            col()
                                .key(format!("dc-stage-tile-{}", m.id))
                                .w(172.0)
                                .h(148.0)
                                .px(16.0).py(16.0)
                                .gap(10.0)
                                .center()
                                .radius(RADIUS_PANEL)
                                .fill(card_surface())
                                .border(
                                    if speaking { 2.0 } else { 1.0 },
                                    if speaking { good() } else { glass_rim() },
                                )
                                .child(avatar_box(
                                    s,
                                    m.avatar.as_deref(),
                                    &m.name,
                                    58.0,
                                    29.0,
                                    None,
                                    speaking,
                                    scale,
                                ))
                                .child(
                                    row()
                                        .gap(6.0)
                                        .center_y()
                                        .child(text(m.name.clone()).font(type_control()).color(FG).truncate())
                                        .child(if m.stream {
                                            el().px(5.0)
                                                .py(1.0)
                                                .radius(4.0)
                                                .fill(bad().alpha(0.2))
                                                .child(text("LIVE").font(type_label()).color(bad()))
                                        } else {
                                            el()
                                        }),
                                ),
                        );
                    }
                }
                col()
                    .grow(1.0)
                    .w_full()
                    .px(24.0).py(24.0)
                    .gap(16.0)
                    .center()
                    .child(tiles)
            } else {
                col()
                    .grow(1.0)
                    .w_full()
                    .px(24.0).py(24.0)
                    .gap(10.0)
                    .justify_end()
                    .child(
                        el().size(48.0, 48.0)
                            .radius(14.0)
                            .fill(subtle_fill())
                            .border(1.0, glass_rim())
                            .center()
                            .child(icon_hash(22.0, FG)),
                    )
                    .child(text(format!("Welcome to #{}", ch.name)).font(type_page()).color(FG))
                    .child(
                        text(
                            ch.topic
                                .clone()
                                .unwrap_or_else(|| "Channel timeline, markdown, embeds, and composer connect in Phase 5b.".into()),
                        )
                        .font(type_body())
                        .color(fg_muted())
                        .w_full()
                        .wrap(),
                    )
            };

            col()
                .key("dc-channel-stage")
                .grow(1.0)
                .min_w(0.0)
                .h_full()
                .gap(GAP)
                .child(header)
                .child(
                    col()
                        .grow(1.0)
                        .w_full()
                        .min_h(0.0)
                        .radius(RADIUS_PANEL)
                        .fill(pane_surface())
                        .border(1.0, hairline())
                        .child(stage_body),
                )
        }
        DiscordPlace::Home => el(),
    }
}

fn right_members_panel(s: &AppState, scale: f32) -> Option<El> {
    if !s.discord.show_members {
        return None;
    }
    match s.discord.place {
        DiscordPlace::Servers => {
            let gid = s.discord.active_guild_id.as_deref()?;
            let mut list = col().w_full().gap(2.0).pb(76.0);
            let header_text = if let Some(ml) = s.discord.members_by_guild.get(gid) {
                for (idx, item) in ml.items.iter().enumerate() {
                    let Some(it) = item else { continue };
                    match it {
                        DiscordMemberItem::Group { group, count } => {
                            list = list.child(
                                text(format!("{group} — {count}"))
                                    .font(type_label())
                                    .color(fg_faint())
                                    .pt(if idx == 0 { 4.0 } else { 12.0 })
                                    .px(8.0)
                                    .pb(4.0),
                            );
                        }
                        DiscordMemberItem::Member(m) => {
                            let offline = m.status == "offline";
                            let uid = m.id.clone();
                            let name_color = lift_role_color(m.color.as_deref(), offline);
                            let mut name_row = row().gap(5.0).center_y().child(
                                text(m.name.clone())
                                    .font(type_control())
                                    .color(name_color)
                                    .truncate(),
                            );
                            if m.bot {
                                name_row = name_row.child(
                                    el().px(4.0)
                                        .py(1.0)
                                        .radius(4.0)
                                        .fill(subtle_fill())
                                        .child(text("APP").font(type_label()).color(fg_muted())),
                                );
                            }
                            let sub = if m.in_voice.is_some() {
                                Some("In voice".to_string())
                            } else {
                                m.activity.clone()
                            };
                            list = list.child(
                                row()
                                    .key(format!("dc-mem-{}", m.id))
                                    .w_full()
                                    .h(44.0)
                                    .px(8.0)
                                    .gap(9.0)
                                    .center_y()
                                    .radius(RADIUS_SMALL)
                                    .hover(hover_fill())
                                    .opacity(if offline { 0.55 } else { 1.0 })
                                    .hit()
                                    .press_on_down()
                                    .on_press(move |s: &mut AppState| s.open_dm_with_friend(&uid))
                                    .child(avatar_box(
                                        s,
                                        m.avatar.as_deref(),
                                        &m.name,
                                        30.0,
                                        15.0,
                                        if offline { None } else { Some(&m.status) },
                                        s.discord.speaking.contains(&m.id),
                                        scale,
                                    ))
                                    .child(
                                        col()
                                            .grow(1.0)
                                            .min_w(0.0)
                                            .gap(1.0)
                                            .child(name_row)
                                            .child(if let Some(sub_txt) = sub {
                                                text(sub_txt)
                                                    .font(type_label())
                                                    .color(if m.in_voice.is_some() {
                                                        good()
                                                    } else {
                                                        fg_faint()
                                                    })
                                                    .truncate()
                                            } else {
                                                el()
                                            }),
                                    ),
                            );
                        }
                    }
                }
                format!("Members · {} online", ml.online)
            } else {
                list = list.child(
                    text("Loading members…")
                        .font(type_small())
                        .color(fg_muted())
                        .px(10.0).py(10.0),
                );
                "Members".into()
            };

            Some(
                col()
                    .key("dc-members-panel")
                    .w(244.0)
                    .h_full()
                    .px(8.0).py(8.0)
                    .gap(6.0)
                    .radius(RADIUS_PANEL)
                    .fill(pane_surface())
                    .border(1.0, glass_rim())
                    .child(text(header_text).font(type_small()).color(fg_muted()).px(8.0).py(4.0))
                    .child(
                        col()
                            .grow(1.0)
                            .w_full()
                            .min_h(0.0)
                            .scroll(ScrollSpec { stick_bottom: false })
                            .child(list),
                    ),
            )
        }
        DiscordPlace::Messages => {
            let dm = s.discord.active_dm()?;
            let mut panel = col()
                .key("dc-dm-info")
                .w(244.0)
                .h_full()
                .px(14.0).py(14.0)
                .gap(12.0)
                .radius(RADIUS_PANEL)
                .fill(pane_surface())
                .border(1.0, glass_rim());
            if dm.group {
                let members = dm.members.as_deref().unwrap_or(&[]);
                panel = panel.child(
                    text(format!("Members · {}", members.len() + 1))
                        .font(type_small())
                        .color(fg_muted()),
                );
                let mut list = col().w_full().gap(4.0);
                for m in members {
                    let uid = m.id.clone();
                    list = list.child(
                        row()
                            .key(format!("dc-grp-mem-{}", m.id))
                            .w_full()
                            .h(40.0)
                            .px(6.0)
                            .gap(8.0)
                            .center_y()
                            .radius(RADIUS_SMALL)
                            .hover(hover_fill())
                            .hit()
                            .press_on_down()
                            .on_press(move |s: &mut AppState| s.open_dm_with_friend(&uid))
                            .child(avatar_box(
                                s,
                                m.avatar.as_deref(),
                                &m.name,
                                28.0,
                                14.0,
                                Some(&m.status),
                                false,
                                scale,
                            ))
                            .child(text(m.name.clone()).font(type_control()).color(FG).truncate()),
                    );
                }
                panel = panel.child(list);
            } else {
                panel = panel
                    .child(avatar_box(
                        s,
                        dm.avatar.as_deref(),
                        &dm.name,
                        60.0,
                        30.0,
                        dm.status.as_deref(),
                        false,
                        scale,
                    ))
                    .child(
                        col()
                            .gap(2.0)
                            .child(text(dm.name.clone()).font(type_title()).color(FG))
                            .child(
                                text(
                                    dm.username
                                        .clone()
                                        .map(|u| format!("@{u}"))
                                        .unwrap_or_default(),
                                )
                                .font(type_small())
                                .color(fg_muted()),
                            ),
                    )
                    .child(el().w_full().h(1.0).fill(hairline()))
                    .child(
                        col()
                            .gap(4.0)
                            .child(text("Status").font(type_label()).color(fg_faint()))
                            .child(
                                text(status_label(dm.status.as_deref().unwrap_or("offline")))
                                    .font(type_small())
                                    .color(FG),
                            ),
                    );
                if let Some(ref sub) = dm.subtitle {
                    panel = panel.child(
                        col()
                            .gap(4.0)
                            .child(text("Activity").font(type_label()).color(fg_faint()))
                            .child(text(sub.clone()).font(type_small()).color(FG).w_full().wrap()),
                    );
                }
            }
            Some(panel)
        }
        DiscordPlace::Home => None,
    }
}

pub fn view(s: &AppState, scale: f32) -> El {
    let signed_in = s.discord.status.state == "ready" && s.discord.home.me.is_some();
    if !signed_in {
        return signed_out_view(s);
    }

    let body = match s.discord.place {
        DiscordPlace::Home => {
            let center = col()
                .key("dc-home-center")
                .grow(1.0)
                .min_w(0.0)
                .h_full()
                .px(8.0)
                .scroll(ScrollSpec { stick_bottom: false })
                .child(match s.discord.home_tab {
                    DiscordHomeTab::Now => home_now_center(s, scale),
                    DiscordHomeTab::Friends => home_friends_center(s, scale),
                });
            row()
                .grow(1.0)
                .w_full()
                .min_h(0.0)
                .px(GAP)
                .pb(GAP)
                .gap(GAP)
                .child(home_left_rail(s, scale))
                .child(center)
        }
        DiscordPlace::Messages => {
            let mut r = row()
                .grow(1.0)
                .w_full()
                .min_h(0.0)
                .px(GAP)
                .pb(GAP)
                .gap(GAP)
                .child(messages_left_rail(s, scale))
                .child(channel_or_dm_stage(s, scale));
            if let Some(panel) = right_members_panel(s, scale) {
                r = r.child(panel);
            }
            r
        }
        DiscordPlace::Servers => {
            if let Some(guild) = s.discord.active_guild() {
                let mut r = row()
                    .grow(1.0)
                    .w_full()
                    .min_h(0.0)
                    .px(GAP)
                    .pb(GAP)
                    .gap(GAP)
                    .child(server_left_rail(s, guild, scale))
                    .child(channel_or_dm_stage(s, scale));
                if let Some(panel) = right_members_panel(s, scale) {
                    r = r.child(panel);
                }
                r
            } else {
                let grid_scroll = col()
                    .key("dc-servers-scroll")
                    .grow(1.0)
                    .min_w(0.0)
                    .h_full()
                    .px(8.0)
                    .scroll(ScrollSpec { stick_bottom: false })
                    .child(servers_directory(s, scale));
                row()
                    .grow(1.0)
                    .w_full()
                    .min_h(0.0)
                    .px(GAP)
                    .pb(GAP)
                    .gap(GAP)
                    .child(home_left_rail(s, scale))
                    .child(grid_scroll)
            }
        }
    };

    col()
        .key("discord-room")
        .w_full()
        .h_full()
        .pt(54.0)
        .gap(4.0)
        .child(place_band(s))
        .child(body)
}
