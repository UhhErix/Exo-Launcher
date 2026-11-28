use crate::discord::avatar_box;
use crate::discord_chat_state::{
    ChatDrawer, InboxTab, SearchChip, SearchFilterKey, SearchSort, discord_deeplink_target,
};
use crate::discord_md;
use crate::state::AppState;
use exo_kit::{
    BG, FG, PRESS_SCALE, PRIMARY_FG, RADIUS_CARD, RADIUS_PANEL, RADIUS_PILL, RADIUS_SMALL,
    SPRING_QUICK, bad, fg_faint, fg_muted, glass_button, glass_control_fill, glass_rim,
    glass_rim_bottom, glass_rim_top, good, hairline, hover_fill, icon_bookmark, icon_check,
    icon_close, icon_filter, icon_hash, icon_pin, icon_search, icon_trash, subtle_fill, type_label,
    type_section, type_small,
};
use exo_rpc::{DiscordMessage, DiscordPlaceRef};
use rune::{Color, Cursor, El, ScrollSpec, col, edit_access, el, row, text, text_input};

fn tiny_btn(key: String, label: &'static str, danger: bool, icon: El) -> El {
    el().key(key)
        .role("button")
        .label(label)
        .cursor(Cursor::Pointer)
        .focusable(true)
        .hit()
        .press_on_down()
        .press_scale(PRESS_SCALE)
        .size(24.0, 24.0)
        .radius(6.0)
        .fill(Color::TRANSPARENT)
        .hover(if danger {
            bad().alpha(0.22)
        } else {
            hover_fill()
        })
        .center()
        .motion(SPRING_QUICK)
        .child(icon)
}

fn seg_tab(key: &str, label: &str, active: bool) -> El {
    el().key(key)
        .role("tab")
        .label(label)
        .cursor(Cursor::Pointer)
        .focusable(true)
        .hit()
        .press_on_down()
        .press_scale(PRESS_SCALE)
        .h(26.0)
        .px(10.0)
        .radius(RADIUS_PILL)
        .fill(if active { FG } else { subtle_fill() })
        .hover(if active { FG } else { hover_fill() })
        .center()
        .motion(SPRING_QUICK)
        .child(
            text(label)
                .font(type_label())
                .color(if active { PRIMARY_FG } else { fg_muted() }),
        )
}

type RemoveAction = (&'static str, Box<dyn Fn(&mut AppState) + 'static>);

fn result_message_card(
    s: &AppState,
    m: &DiscordMessage,
    place: Option<&DiscordPlaceRef>,
    scale: f32,
    on_remove: Option<RemoveAction>,
) -> El {
    let av = m.author.avatar_url();
    let jump_gid = place
        .and_then(|p| p.guild_id.clone())
        .or_else(|| s.discord.active_guild_id.clone());
    let jump_cid = m.channel_id.clone();
    let jump_mid = m.id.clone();

    let mut head = row()
        .w_full()
        .gap(8.0)
        .center_y()
        .child(avatar_box(
            s,
            av.as_deref(),
            m.display_name(),
            22.0,
            11.0,
            None,
            false,
            scale,
        ))
        .child(
            text(m.display_name().to_string())
                .font(type_small())
                .color(FG)
                .grow(1.0)
                .truncate(),
        )
        .child(text(m.time_short()).font(type_label()).color(fg_faint()))
        .child(
            el().key(format!("dc-res-jump-{}", m.id))
                .role("button")
                .label_static("Jump to message")
                .focusable(true)
                .px(8.0)
                .h(22.0)
                .radius(RADIUS_PILL)
                .fill(subtle_fill())
                .hover(hover_fill())
                .center()
                .cursor(Cursor::Pointer)
                .hit()
                .press_on_down()
                .on_press(move |s: &mut AppState| {
                    s.jump_to_discord_message(jump_gid.as_deref(), &jump_cid, &jump_mid);
                })
                .child(text("Jump").font(type_label()).color(FG)),
        );

    if let Some((rm_label, rm_cb)) = on_remove {
        head = head.child(
            tiny_btn(
                format!("dc-res-rm-{}", m.id),
                rm_label,
                true,
                icon_close(11.0, fg_muted()),
            )
            .on_press(move |s: &mut AppState| rm_cb(s)),
        );
    }

    let mut card = col()
        .key(format!("dc-drawer-msg-{}", m.id))
        .w_full()
        .px(10.0)
        .py(8.0)
        .gap(6.0)
        .radius(RADIUS_CARD)
        .fill(BG)
        .border(1.0, hairline());

    if let Some(p) = place {
        let place_text = match p.guild.as_deref() {
            Some(g) => format!("{g} · #{}", p.name),
            None => format!("#{}", p.name),
        };
        card = card.child(text(place_text).font(type_label()).color(fg_faint()));
    }

    card = card.child(head).child(discord_md::render_markdown(
        &m.content,
        &m.mentions,
        &m.id,
        true,
    ));

    if !m.attachments.is_empty() {
        card = card.child(
            text(format!(
                "{} attachment{}",
                m.attachments.len(),
                if m.attachments.len() == 1 { "" } else { "s" }
            ))
            .font(type_label())
            .color(fg_faint()),
        );
    }

    card
}

pub fn pins_drawer(s: &AppState, scale: f32) -> El {
    let mut list = col().w_full().gap(8.0).pb(76.0);
    if s.discord.chat.pins_loading {
        list = list.child(text("Loading pins…").font(type_small()).color(fg_muted()));
    } else if s.discord.chat.pins.is_empty() {
        list = list.child(
            text("No pinned messages in this channel.")
                .font(type_small())
                .color(fg_muted())
                .w_full()
                .wrap(),
        );
    } else {
        for m in &s.discord.chat.pins {
            let cid = m.channel_id.clone();
            let mid = m.id.clone();
            list = list.child(result_message_card(
                s,
                m,
                None,
                scale,
                Some((
                    "Unpin",
                    Box::new(move |s: &mut AppState| s.toggle_discord_pin(&cid, &mid, true)),
                )),
            ));
        }
    }

    col()
        .key("dc-pins-drawer")
        .w(320.0)
        .h_full()
        .px(10.0)
        .py(10.0)
        .gap(10.0)
        .radius(RADIUS_PANEL)
        .fill(BG)
        .border(1.0, glass_rim())
        .child(
            row()
                .w_full()
                .justify_between()
                .center_y()
                .child(
                    row().gap(6.0).center_y().child(icon_pin(13.0, FG)).child(
                        text(format!("Pinned · {}", s.discord.chat.pins.len()))
                            .font(type_section())
                            .color(FG),
                    ),
                )
                .child(
                    tiny_btn(
                        "dc-drawer-close".into(),
                        "Close",
                        false,
                        icon_close(13.0, fg_muted()),
                    )
                    .on_press(|s: &mut AppState| s.discord.chat.drawer = ChatDrawer::None),
                ),
        )
        .child(
            col()
                .grow(1.0)
                .w_full()
                .min_h(0.0)
                .scroll(ScrollSpec {
                    stick_bottom: false,
                })
                .child(list),
        )
}

pub fn search_drawer(s: &AppState, scale: f32) -> El {
    let mut header_bar = col().w_full().gap(8.0);
    header_bar = header_bar.child(
        row()
            .w_full()
            .justify_between()
            .center_y()
            .child(
                row()
                    .gap(6.0)
                    .center_y()
                    .child(icon_search(13.0, FG))
                    .child(text("Search").font(type_section()).color(FG)),
            )
            .child(
                row()
                    .gap(4.0)
                    .child(
                        tiny_btn(
                            "dc-search-filter-toggle".into(),
                            "Search filters",
                            false,
                            icon_filter(
                                13.0,
                                if s.discord.chat.search.show_filters {
                                    good()
                                } else {
                                    fg_muted()
                                },
                            ),
                        )
                        .on_press(|s: &mut AppState| {
                            s.discord.chat.search.show_filters = !s.discord.chat.search.show_filters
                        }),
                    )
                    .child(
                        tiny_btn(
                            "dc-drawer-close".into(),
                            "Close",
                            false,
                            icon_close(13.0, fg_muted()),
                        )
                        .on_press(|s: &mut AppState| s.discord.chat.drawer = ChatDrawer::None),
                    ),
            ),
    );

    header_bar = header_bar.child(
        row()
            .w_full()
            .h(34.0)
            .px(10.0)
            .gap(6.0)
            .center_y()
            .radius(RADIUS_SMALL)
            .fill(glass_control_fill())
            .border(1.0, glass_rim())
            .child(
                text_input(&s.discord.chat.search_input, type_small())
                    .key("dc-chat-search-input")
                    .label("Search messages")
                    .grow(1.0)
                    .single_line()
                    .color(FG)
                    .placeholder("Search or type has:image, pinned:true…", fg_muted())
                    .on_edit(edit_access(|s: &mut AppState| {
                        &mut s.discord.chat.search_input
                    }))
                    .on_submit(|s: &mut AppState, _| {
                        s.discord.chat.search.offset = 0;
                        s.run_discord_search();
                    }),
            ),
    );

    if !s.discord.chat.search.chips.is_empty() {
        let mut chip_row = row().w_full().gap(4.0).flex_wrap();
        for (idx, chip) in s.discord.chat.search.chips.iter().enumerate() {
            let neg = if chip.negated { "-" } else { "" };
            chip_row = chip_row.child(
                row()
                    .key(format!("dc-schip-{idx}"))
                    .h(24.0)
                    .px(8.0)
                    .gap(4.0)
                    .center_y()
                    .radius(RADIUS_PILL)
                    .fill(subtle_fill())
                    .border(1.0, glass_rim())
                    .child(
                        text(format!("{neg}{}: {}", chip.key.as_str(), chip.label))
                            .font(type_label())
                            .color(FG),
                    )
                    .child(
                        el().key(format!("dc-schip-rm-{idx}"))
                            .role("button")
                            .label_static("Remove filter")
                            .focusable(true)
                            .size(16.0, 16.0)
                            .radius(8.0)
                            .hover(hover_fill())
                            .center()
                            .cursor(Cursor::Pointer)
                            .hit()
                            .press_on_down()
                            .on_press(move |s: &mut AppState| s.remove_search_chip(idx))
                            .child(icon_close(10.0, fg_muted())),
                    ),
            );
        }
        header_bar = header_bar.child(chip_row);
    }

    if s.discord.chat.search.show_filters {
        let mut quick = col()
            .w_full()
            .padding(8.0)
            .gap(6.0)
            .radius(RADIUS_CARD)
            .fill(BG)
            .border_gradient(1.0, glass_rim_top(), glass_rim_bottom())
            .child(text("Quick filters").font(type_label()).color(fg_muted()));

        let mut has_row = row().w_full().gap(4.0).flex_wrap();
        for kind in ["link", "image", "file", "video", "embed", "poll", "sticker"] {
            let k_s = kind.to_string();
            has_row = has_row.child(
                seg_tab(&format!("dc-sf-has-{kind}"), &format!("has:{kind}"), false).on_press(
                    move |s: &mut AppState| {
                        s.add_search_chip(SearchChip {
                            key: SearchFilterKey::Has,
                            label: k_s.clone(),
                            value: k_s.clone(),
                            negated: false,
                            range_ms: None,
                        });
                    },
                ),
            );
        }
        quick = quick.child(has_row).child(
            row()
                .w_full()
                .gap(4.0)
                .flex_wrap()
                .child(seg_tab("dc-sf-pinned", "pinned:true", false).on_press(
                    |s: &mut AppState| {
                        s.add_search_chip(SearchChip {
                            key: SearchFilterKey::Pinned,
                            label: "true".into(),
                            value: "true".into(),
                            negated: false,
                            range_ms: None,
                        });
                    },
                ))
                .child(seg_tab("dc-sf-bot", "authorType:bot", false).on_press(
                    |s: &mut AppState| {
                        s.add_search_chip(SearchChip {
                            key: SearchFilterKey::AuthorType,
                            label: "bot".into(),
                            value: "bot".into(),
                            negated: false,
                            range_ms: None,
                        });
                    },
                ))
                .child(
                    seg_tab("dc-sf-from", "from:", false).on_press(|s: &mut AppState| {
                        s.discord.chat.search_input.text.push_str(" from:");
                        s.discord.chat.search.show_filters = false;
                    }),
                )
                .child(seg_tab("dc-sf-before", "before:today", false).on_press(
                    |s: &mut AppState| {
                        s.discord.chat.search_input.text.push_str(" before:today");
                        s.run_discord_search();
                    },
                )),
        );
        header_bar = header_bar.child(quick);
    }

    header_bar = header_bar.child(
        row()
            .w_full()
            .gap(4.0)
            .child(
                seg_tab(
                    "dc-ssort-new",
                    "Newest",
                    s.discord.chat.search.sort == SearchSort::Newest,
                )
                .on_press(|s: &mut AppState| s.set_search_sort(SearchSort::Newest)),
            )
            .child(
                seg_tab(
                    "dc-ssort-old",
                    "Oldest",
                    s.discord.chat.search.sort == SearchSort::Oldest,
                )
                .on_press(|s: &mut AppState| s.set_search_sort(SearchSort::Oldest)),
            )
            .child(
                seg_tab(
                    "dc-ssort-rel",
                    "Relevant",
                    s.discord.chat.search.sort == SearchSort::Relevance,
                )
                .on_press(|s: &mut AppState| s.set_search_sort(SearchSort::Relevance)),
            ),
    );

    let mut list = col().w_full().gap(8.0).pb(76.0);
    if let Some(ref err) = s.discord.chat.search.error {
        list = list.child(
            text(err.clone())
                .font(type_small())
                .color(bad())
                .w_full()
                .wrap(),
        );
    }
    if s.discord.chat.search_loading {
        list = list.child(text("Searching…").font(type_small()).color(fg_muted()));
    } else if s.discord.chat.search_results.is_empty() {
        let msg = if s.discord.chat.search_input.text.trim().is_empty()
            && s.discord.chat.search.chips.is_empty()
        {
            "Type words or filters (from:, in:, has:, before:, pinned:) and press Enter."
        } else {
            "No matching messages found."
        };
        list = list.child(
            text(msg)
                .font(type_small())
                .color(fg_muted())
                .w_full()
                .wrap(),
        );
    } else {
        list = list.child(
            text(format!("{} results", s.discord.chat.search_total))
                .font(type_label())
                .color(fg_muted()),
        );
        for m in &s.discord.chat.search_results {
            let place = s
                .discord
                .chat
                .search
                .places
                .get(&m.channel_id)
                .and_then(|o| o.as_ref());
            list = list.child(result_message_card(s, m, place, scale, None));
        }
        let offset = s.discord.chat.search.offset;
        let total = s.discord.chat.search_total;
        if offset > 0 || offset + 25 < total {
            let mut pager = row().w_full().justify_between().center_y().pt(4.0);
            if offset > 0 {
                let prev = offset.saturating_sub(25);
                pager = pager.child(
                    glass_button("dc-search-prev", "Previous", None)
                        .on_press(move |s: &mut AppState| s.set_search_offset(prev)),
                );
            } else {
                pager = pager.child(el());
            }
            pager = pager.child(
                text(format!("Page {}", offset / 25 + 1))
                    .font(type_label())
                    .color(fg_faint()),
            );
            if offset + 25 < total {
                let next = offset + 25;
                pager = pager.child(
                    glass_button("dc-search-next", "Next", None)
                        .on_press(move |s: &mut AppState| s.set_search_offset(next)),
                );
            } else {
                pager = pager.child(el());
            }
            list = list.child(pager);
        }
    }

    col()
        .key("dc-search-drawer")
        .w(340.0)
        .h_full()
        .px(10.0)
        .py(10.0)
        .gap(10.0)
        .radius(RADIUS_PANEL)
        .fill(BG)
        .border(1.0, glass_rim())
        .child(header_bar)
        .child(
            col()
                .grow(1.0)
                .w_full()
                .min_h(0.0)
                .scroll(ScrollSpec {
                    stick_bottom: false,
                })
                .child(list),
        )
}

pub fn inbox_panel(s: &AppState, sc: f32, scale: f32) -> El {
    let tab = s.discord.chat.inbox.tab;
    let tabs_row = row()
        .w_full()
        .gap(4.0)
        .flex_wrap()
        .child(
            seg_tab("dc-inb-fy", "For you", tab == InboxTab::ForYou)
                .on_press(|s: &mut AppState| s.select_inbox_tab(InboxTab::ForYou)),
        )
        .child(
            seg_tab("dc-inb-un", "Unreads", tab == InboxTab::Unreads)
                .on_press(|s: &mut AppState| s.select_inbox_tab(InboxTab::Unreads)),
        )
        .child(
            seg_tab("dc-inb-mn", "Mentions", tab == InboxTab::Mentions)
                .on_press(|s: &mut AppState| s.select_inbox_tab(InboxTab::Mentions)),
        )
        .child(
            seg_tab("dc-inb-bm", "Bookmarks", tab == InboxTab::Bookmarks)
                .on_press(|s: &mut AppState| s.select_inbox_tab(InboxTab::Bookmarks)),
        )
        .child(
            seg_tab("dc-inb-rm", "Reminders", tab == InboxTab::Reminders)
                .on_press(|s: &mut AppState| s.select_inbox_tab(InboxTab::Reminders)),
        );

    let body = match tab {
        InboxTab::ForYou => inbox_for_you_list(s, scale),
        InboxTab::Unreads => inbox_unreads_list(s, scale),
        InboxTab::Mentions => inbox_mentions_list(s, scale),
        InboxTab::Bookmarks => inbox_saved_list(s, false, scale),
        InboxTab::Reminders => inbox_saved_list(s, true, scale),
    };

    col()
        .key("dc-inbox-panel")
        .w(360.0 * sc)
        .h_full()
        .px(12.0)
        .py(12.0)
        .gap(10.0)
        .radius(RADIUS_PANEL)
        .fill(BG)
        .border(1.0, glass_rim())
        .child(
            row()
                .w_full()
                .justify_between()
                .center_y()
                .child(text("Inbox").font(type_section()).color(FG))
                .child(
                    tiny_btn(
                        "dc-inbox-close".into(),
                        "Close inbox",
                        false,
                        icon_close(13.0, fg_muted()),
                    )
                    .on_press(|s: &mut AppState| s.discord.inbox_open = false),
                ),
        )
        .child(tabs_row)
        .child(body)
}

fn inbox_for_you_list(s: &AppState, scale: f32) -> El {
    let mut list = col().w_full().gap(8.0).pb(76.0);
    if let Some(ref err) = s.discord.chat.inbox.for_you_error {
        list = list.child(
            text(err.clone())
                .font(type_small())
                .color(bad())
                .w_full()
                .wrap(),
        );
    }
    if s.discord.chat.inbox.for_you_loading && s.discord.chat.inbox.for_you.is_empty() {
        list = list.child(
            text("Loading For You…")
                .font(type_small())
                .color(fg_muted()),
        );
    } else if s.discord.chat.inbox.for_you.is_empty() {
        list = list.child(
            text("Nothing new for you.")
                .font(type_small())
                .color(fg_muted()),
        );
    } else {
        for item in &s.discord.chat.inbox.for_you {
            let pic = item
                .icon_url
                .clone()
                .or_else(|| item.other_user.as_ref().and_then(|u| u.avatar_url()));
            let name = item
                .other_user
                .as_ref()
                .map(|u| u.global_name.as_deref().unwrap_or(&u.username))
                .unwrap_or("Discord");
            let deeplink = item.deeplink.clone();
            let mut card = row()
                .key(format!("dc-fy-{}", item.id))
                .w_full()
                .padding(10.0)
                .gap(10.0)
                .radius(RADIUS_CARD)
                .fill(BG)
                .border(1.0, hairline())
                .hover(hover_fill())
                .child(avatar_box(
                    s,
                    pic.as_deref(),
                    name,
                    30.0,
                    15.0,
                    None,
                    false,
                    scale,
                ))
                .child(
                    col()
                        .grow(1.0)
                        .min_w(0.0)
                        .gap(4.0)
                        .child(discord_md::render_markdown(
                            item.body.as_deref().unwrap_or("Notification"),
                            &[],
                            &item.id,
                            true,
                        )),
                );
            if item.acked == Some(false) {
                card = card.child(el().size(8.0, 8.0).radius(4.0).fill(good()));
            }
            if let Some(link) = deeplink
                && let Some((gid, cid, mid)) = discord_deeplink_target(&link)
            {
                card = card
                    .role("button")
                    .label(item.body.clone().unwrap_or_else(|| "Notification".into()))
                    .focusable(true)
                    .cursor(Cursor::Pointer)
                    .hit()
                    .press_on_down()
                    .on_press(move |s: &mut AppState| {
                        s.discord.inbox_open = false;
                        if let Some(ref m) = mid {
                            s.jump_to_discord_message(gid.as_deref(), &cid, m);
                        } else if let Some(ref g) = gid {
                            s.open_discord_guild(g);
                            s.open_discord_channel(&cid);
                        } else {
                            s.open_discord_dm(&cid);
                        }
                    });
            }
            list = list.child(card);
        }
        if s.discord.chat.inbox.for_you_has_more
            && let Some(last_id) = s.discord.chat.inbox.for_you.last().map(|i| i.id.clone())
        {
            list = list.child(
                glass_button("dc-fy-more", "Load more", None)
                    .on_press(move |s: &mut AppState| s.load_discord_for_you(Some(&last_id))),
            );
        }
    }
    col()
        .grow(1.0)
        .w_full()
        .min_h(0.0)
        .scroll(ScrollSpec {
            stick_bottom: false,
        })
        .child(list)
}

fn inbox_unreads_list(s: &AppState, scale: f32) -> El {
    let mut list = col().w_full().gap(10.0).pb(76.0);
    if s.discord.unreads.is_empty() {
        list = list.child(
            text("You're all caught up.")
                .font(type_small())
                .color(fg_muted()),
        );
    } else {
        let mut grouped: Vec<(String, String, Vec<&exo_rpc::DiscordUnreadChannel>)> = Vec::new();
        for ch in &s.discord.unreads {
            if let Some(g) = grouped.iter_mut().find(|(gid, _, _)| gid == &ch.guild_id) {
                g.2.push(ch);
            } else {
                grouped.push((ch.guild_id.clone(), ch.guild.clone(), vec![ch]));
            }
        }
        for (gid, gname, channels) in grouped {
            let gid_read = gid.clone();
            let mut sec = col().w_full().gap(6.0).child(
                row()
                    .w_full()
                    .justify_between()
                    .center_y()
                    .child(text(gname).font(type_small()).color(FG))
                    .child(
                        tiny_btn(
                            format!("dc-un-gread-{gid}"),
                            "Mark server read",
                            false,
                            icon_check(13.0, fg_muted()),
                        )
                        .on_press(move |s: &mut AppState| s.mark_unread_server_read(&gid_read)),
                    ),
            );
            for ch in channels {
                let cid_open = ch.channel_id.clone();
                let gid_open = ch.guild_id.clone();
                let cid_read = ch.channel_id.clone();
                let mut card = col()
                    .key(format!("dc-un-ch-{}", ch.channel_id))
                    .w_full()
                    .padding(8.0)
                    .gap(6.0)
                    .radius(RADIUS_CARD)
                    .fill(BG)
                    .border(1.0, hairline())
                    .child(
                        row()
                            .w_full()
                            .justify_between()
                            .center_y()
                            .child(
                                row()
                                    .key(format!("dc-un-chopen-{}", ch.channel_id))
                                    .role("button")
                                    .label(ch.name.clone())
                                    .focusable(true)
                                    .gap(5.0)
                                    .center_y()
                                    .cursor(Cursor::Pointer)
                                    .hit()
                                    .press_on_down()
                                    .on_press(move |s: &mut AppState| {
                                        s.discord.inbox_open = false;
                                        s.open_discord_guild(&gid_open);
                                        s.open_discord_channel(&cid_open);
                                    })
                                    .child(icon_hash(13.0, fg_muted()))
                                    .child(text(ch.name.clone()).font(type_small()).color(FG)),
                            )
                            .child(
                                tiny_btn(
                                    format!("dc-un-cread-{}", ch.channel_id),
                                    "Mark channel read",
                                    false,
                                    icon_check(12.0, fg_muted()),
                                )
                                .on_press(
                                    move |s: &mut AppState| s.mark_unread_channel_read(&cid_read),
                                ),
                            ),
                    );
                if let Some(msgs) = s.discord.chat.inbox.unread_previews.get(&ch.channel_id) {
                    for m in msgs {
                        let av = m.author.avatar_url();
                        let jg = ch.guild_id.clone();
                        let jc = ch.channel_id.clone();
                        let jm = m.id.clone();
                        card = card.child(
                            row()
                                .key(format!("dc-un-m-{}", m.id))
                                .role("button")
                                .label(m.display_name().to_string())
                                .focusable(true)
                                .w_full()
                                .gap(6.0)
                                .center_y()
                                .px(4.0)
                                .py(3.0)
                                .radius(6.0)
                                .hover(hover_fill())
                                .cursor(Cursor::Pointer)
                                .hit()
                                .press_on_down()
                                .on_press(move |s: &mut AppState| {
                                    s.discord.inbox_open = false;
                                    s.jump_to_discord_message(Some(&jg), &jc, &jm);
                                })
                                .child(avatar_box(
                                    s,
                                    av.as_deref(),
                                    m.display_name(),
                                    20.0,
                                    10.0,
                                    None,
                                    false,
                                    scale,
                                ))
                                .child(
                                    text(format!(
                                        "{}: {}",
                                        m.display_name(),
                                        if m.content.is_empty() {
                                            "Attachment"
                                        } else {
                                            m.content.as_str()
                                        }
                                    ))
                                    .font(type_label())
                                    .color(fg_muted())
                                    .grow(1.0)
                                    .truncate(),
                                ),
                        );
                    }
                }
                sec = sec.child(card);
            }
            list = list.child(sec);
        }
    }
    col()
        .grow(1.0)
        .w_full()
        .min_h(0.0)
        .scroll(ScrollSpec {
            stick_bottom: false,
        })
        .child(list)
}

fn inbox_mentions_list(s: &AppState, scale: f32) -> El {
    let mut filter_bar = row().w_full().gap(4.0).flex_wrap();
    if s.discord.active_guild_id.is_some() {
        filter_bar = filter_bar.child(
            seg_tab(
                "dc-mn-server",
                "This server",
                s.discord.chat.inbox.mentions_this_server,
            )
            .on_press(|s: &mut AppState| {
                s.discord.chat.inbox.mentions_this_server =
                    !s.discord.chat.inbox.mentions_this_server;
                s.load_discord_mentions(None);
            }),
        );
    }
    filter_bar = filter_bar
        .child(
            seg_tab(
                "dc-mn-ev",
                "@everyone",
                s.discord.chat.inbox.mentions_everyone,
            )
            .on_press(|s: &mut AppState| {
                s.discord.chat.inbox.mentions_everyone = !s.discord.chat.inbox.mentions_everyone;
                s.load_discord_mentions(None);
            }),
        )
        .child(
            seg_tab("dc-mn-roles", "Roles", s.discord.chat.inbox.mentions_roles).on_press(
                |s: &mut AppState| {
                    s.discord.chat.inbox.mentions_roles = !s.discord.chat.inbox.mentions_roles;
                    s.load_discord_mentions(None);
                },
            ),
        );

    let mut list = col().w_full().gap(8.0).pb(76.0);
    if let Some(ref err) = s.discord.chat.inbox.mentions_error {
        list = list.child(
            text(err.clone())
                .font(type_small())
                .color(bad())
                .w_full()
                .wrap(),
        );
    }
    if s.discord.chat.inbox.mentions_loading && s.discord.chat.inbox.mentions.is_empty() {
        list = list.child(
            text("Loading mentions…")
                .font(type_small())
                .color(fg_muted()),
        );
    } else if s.discord.chat.inbox.mentions.is_empty() {
        list = list.child(
            text("You're all caught up.")
                .font(type_small())
                .color(fg_muted()),
        );
    } else {
        for m in &s.discord.chat.inbox.mentions {
            let place = s
                .discord
                .chat
                .inbox
                .mentions_places
                .get(&m.channel_id)
                .and_then(|o| o.as_ref());
            let mid = m.id.clone();
            list = list.child(result_message_card(
                s,
                m,
                place,
                scale,
                Some((
                    "Dismiss mention",
                    Box::new(move |s: &mut AppState| s.dismiss_discord_mention(&mid)),
                )),
            ));
        }
    }

    col()
        .grow(1.0)
        .w_full()
        .min_h(0.0)
        .gap(8.0)
        .child(filter_bar)
        .child(
            col()
                .grow(1.0)
                .w_full()
                .min_h(0.0)
                .scroll(ScrollSpec {
                    stick_bottom: false,
                })
                .child(list),
        )
}

fn inbox_saved_list(s: &AppState, reminders: bool, scale: f32) -> El {
    let items = if reminders {
        &s.discord.chat.inbox.reminders
    } else {
        &s.discord.chat.inbox.bookmarks
    };
    let mut list = col().w_full().gap(8.0).pb(76.0);
    if let Some(ref err) = s.discord.chat.inbox.saved_error {
        list = list.child(
            text(err.clone())
                .font(type_small())
                .color(bad())
                .w_full()
                .wrap(),
        );
    }
    if s.discord.chat.inbox.saved_loading && items.is_empty() {
        list = list.child(
            text(if reminders {
                "Loading reminders…"
            } else {
                "Loading saved messages…"
            })
            .font(type_small())
            .color(fg_muted()),
        );
    } else if items.is_empty() {
        list = list.child(
            text(if reminders {
                "Set a reminder from a message's menu, and it shows here."
            } else {
                "Bookmark a message from its menu to find it here."
            })
            .font(type_small())
            .color(fg_muted())
            .w_full()
            .wrap(),
        );
    } else {
        for item in items {
            let Some(ref m) = item.message else {
                continue;
            };
            let place = s
                .discord
                .chat
                .inbox
                .saved_places
                .get(&m.channel_id)
                .and_then(|o| o.as_ref());
            let cid = item.save_data.channel_id.clone();
            let mid = item.save_data.message_id.clone();
            let rm_label = if reminders {
                "Mark complete"
            } else {
                "Remove bookmark"
            };
            let mut block = col().w_full().gap(4.0);
            if let Some(ref due) = item.save_data.due_at {
                block = block.child(
                    row()
                        .gap(5.0)
                        .center_y()
                        .child(icon_bookmark(11.0, good()))
                        .child(text(format!("Due {due}")).font(type_label()).color(good())),
                );
            }
            block = block.child(result_message_card(
                s,
                m,
                place,
                scale,
                Some((
                    rm_label,
                    Box::new(move |s: &mut AppState| s.unsave_discord_message(&cid, &mid)),
                )),
            ));
            list = list.child(block);
        }
    }
    let _ = icon_trash;
    col()
        .grow(1.0)
        .w_full()
        .min_h(0.0)
        .scroll(ScrollSpec {
            stick_bottom: false,
        })
        .child(list)
}
