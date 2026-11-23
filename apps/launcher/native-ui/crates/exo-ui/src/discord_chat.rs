use crate::discord::avatar_box;
use crate::discord_chat_state::ChatDrawer;
use crate::discord_md;
use crate::state::AppState;
use exo_kit::{
    FG, GAP, PRESS_SCALE, PRIMARY_FG, RADIUS_CARD, RADIUS_PANEL, RADIUS_PILL, RADIUS_SMALL,
    SPRING_BASE, SPRING_MOVE, SPRING_QUICK, bad, fg_faint, fg_muted, glass_button,
    glass_control_fill, glass_rim, good, hairline, hex, hover_fill, icon_close, icon_edit,
    icon_paperclip, icon_pin, icon_plus, icon_reply, icon_search, icon_send, icon_smile,
    icon_trash, pressed_fill, primary_button, subtle_fill, type_body, type_control, type_label,
    type_page, type_section, type_small,
};
use exo_rpc::{DiscordEmbed, DiscordMessage};
use rune::{
    Color, El, GrowthSpec, ScrollSpec, Spring, SpringPair, SurfaceFit, Transition, Tx, col,
    edit_access, el, row, text, text_input,
};
use std::borrow::Cow;

const QUICK_EMOJIS: &[&str] = &["👍", "🔥", "😂", "❤️", "👀", "🚀"];

fn inline_bar_transition(dy: f32) -> Transition {
    let enter = Transition::offset(0.0, dy).combined(Transition::opacity());
    Transition::asymmetric(enter, Transition::opacity())
        .springs(SpringPair::new(SPRING_BASE, SPRING_QUICK))
}

fn send_with_launch(s: &mut AppState, tx: &mut Tx) {
    if let Some(local_id) = s.send_discord_message() {
        tx.motion(SPRING_MOVE);
        tx.launch("dc-composer-input", format!("dc-msg-body-{local_id}"));
    }
}

fn pane_surface() -> Color {
    hex(0x0b0b0d, 1.0)
}

fn card_surface() -> Color {
    hex(0x0e0e11, 1.0)
}

fn format_bytes(bytes: u64) -> String {
    if bytes >= 1024 * 1024 {
        format!("{:.1} MB", bytes as f64 / (1024.0 * 1024.0))
    } else if bytes >= 1024 {
        format!("{} KB", bytes / 1024)
    } else {
        format!("{bytes} B")
    }
}

fn within_seven_minutes(a: &DiscordMessage, b: &DiscordMessage) -> bool {
    if let (Some(ta), Some(tb)) = (a.snowflake_ms(), b.snowflake_ms()) {
        return (tb - ta).abs() <= 7 * 60 * 1000;
    }
    a.day_key() == b.day_key() && a.time_short() == b.time_short()
}

fn tiny_action(key: String, label: &'static str, danger: bool, icon: El) -> El {
    el().key(key)
        .role("button")
        .label(label)
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
        .press(pressed_fill())
        .center()
        .motion(SPRING_QUICK)
        .child(icon)
}

fn embed_card(s: &AppState, embed: &DiscordEmbed, msg_id: &str, scale: f32) -> El {
    let accent = embed
        .color
        .filter(|&c| c != 0)
        .map(|c| hex(c, 1.0))
        .unwrap_or_else(glass_rim);

    let mut inner = col().grow(1.0).min_w(0.0).gap(6.0);
    if let Some(author) = embed
        .author
        .as_ref()
        .and_then(|a| a.name.as_deref())
        .or_else(|| embed.provider.as_ref().and_then(|p| p.name.as_deref()))
        .filter(|s| !s.is_empty())
    {
        inner = inner.child(
            text(author.to_string())
                .font(type_label())
                .color(fg_muted()),
        );
    }
    if let Some(ref title) = embed.title
        && !title.is_empty()
    {
        inner = inner.child(
            text(title.clone())
                .font(type_section())
                .color(FG)
                .w_full()
                .wrap(),
        );
    }
    if let Some(ref desc) = embed.description
        && !desc.is_empty()
    {
        inner = inner.child(discord_md::render_markdown(desc, &[], msg_id, true));
    }
    if !embed.fields.is_empty() {
        let mut frow = row().w_full().gap(12.0).flex_wrap();
        for f in &embed.fields {
            let mut fc = col()
                .gap(2.0)
                .child(text(f.name.clone()).font(type_label()).color(fg_muted()))
                .child(
                    text(f.value.clone())
                        .font(type_small())
                        .color(FG)
                        .w_full()
                        .wrap(),
                );
            if f.inline {
                fc = fc.min_w(130.0).grow(1.0);
            } else {
                fc = fc.w_full();
            }
            frow = frow.child(fc);
        }
        inner = inner.child(frow);
    }
    if let Some(url) = embed
        .image
        .as_ref()
        .and_then(|m| m.proxy_url.as_deref().or(m.url.as_deref()))
        && let Some(img) = s.remote_image_sized(url, 360.0, scale)
    {
        inner = inner.child(
            el().w(360.0)
                .h(202.0)
                .radius(RADIUS_SMALL)
                .clip_rounded(RADIUS_SMALL)
                .pixels_fit(img.pixels(), SurfaceFit::Fit),
        );
    }
    if let Some(footer) = embed
        .footer
        .as_ref()
        .and_then(|f| f.text.as_deref())
        .filter(|s| !s.is_empty())
    {
        inner = inner.child(
            text(footer.to_string())
                .font(type_label())
                .color(fg_faint()),
        );
    }

    row()
        .w(460.0)
        .max_w(520.0)
        .px(12.0)
        .py(10.0)
        .gap(10.0)
        .radius(RADIUS_CARD)
        .fill(card_surface())
        .border(1.0, hairline())
        .child(el().w(3.0).radius(2.0).fill(accent))
        .child(inner)
}

fn message_row(
    s: &AppState,
    channel_id: &str,
    msg: &DiscordMessage,
    prev: Option<&DiscordMessage>,
    scale: f32,
) -> El {
    let my_id = s.discord.home.me.as_ref().map(|m| m.id.as_str());
    let is_mine = my_id == Some(msg.author.id.as_str());
    let new_day = prev.is_none_or(|p| !msg.day_key().is_empty() && p.day_key() != msg.day_key());
    let grouped = !new_day
        && msg.referenced_message.is_none()
        && prev.is_some_and(|p| p.author.id == msg.author.id && within_seven_minutes(p, msg));

    let is_editing = s
        .discord
        .chat
        .editing
        .as_ref()
        .is_some_and(|(id, _)| id == &msg.id);
    let show_emoji_bar = s.discord.chat.emoji_picker_for.as_deref() == Some(msg.id.as_str());
    let reveal_spoilers = s.discord.revealed_spoilers.contains(&msg.id);

    let mut body = col().grow(1.0).min_w(0.0).gap(4.0);

    if let Some(ref r) = msg.referenced_message {
        let preview: String = if r.content.is_empty() {
            "Attachment".into()
        } else {
            r.content.chars().take(80).collect()
        };
        body = body.child(
            row()
                .w_full()
                .gap(6.0)
                .center_y()
                .child(icon_reply(11.0, fg_faint()))
                .child(
                    text(r.display_name().to_string())
                        .font(type_label())
                        .color(fg_muted()),
                )
                .child(
                    text(preview)
                        .font(type_label())
                        .color(fg_faint())
                        .grow(1.0)
                        .truncate(),
                ),
        );
    }

    let cid_actions = channel_id.to_string();
    let mid_actions = msg.id.clone();
    let author_name = msg.display_name().to_string();
    let msg_content_for_edit = msg.content.clone();
    let is_pinned = msg.pinned;

    let mut actions = row().gap(2.0).center_y();
    if !show_emoji_bar {
        actions = actions.group_opacity(0.0, 1.0);
    }
    {
        let mid_e = mid_actions.clone();
        actions = actions.child(
            tiny_action(
                format!("dc-msg-react-{}", msg.id),
                "React",
                false,
                icon_smile(13.0, fg_faint()),
            )
            .on_press(move |s: &mut AppState| {
                if s.discord.chat.emoji_picker_for.as_deref() == Some(mid_e.as_str()) {
                    s.discord.chat.emoji_picker_for = None;
                } else {
                    s.discord.chat.emoji_picker_for = Some(mid_e.clone());
                }
            }),
        );
    }
    {
        let mid_r = mid_actions.clone();
        let name_r = author_name.clone();
        actions = actions.child(
            tiny_action(
                format!("dc-msg-reply-{}", msg.id),
                "Reply",
                false,
                icon_reply(13.0, fg_faint()),
            )
            .on_press(move |s: &mut AppState| {
                s.discord.chat.reply_to = Some((mid_r.clone(), name_r.clone()));
            }),
        );
    }
    {
        let cid_p = cid_actions.clone();
        let mid_p = mid_actions.clone();
        actions = actions.child(
            tiny_action(
                format!("dc-msg-pin-{}", msg.id),
                if is_pinned { "Unpin" } else { "Pin" },
                false,
                icon_pin(13.0, if is_pinned { good() } else { fg_faint() }),
            )
            .on_press(move |s: &mut AppState| s.toggle_discord_pin(&cid_p, &mid_p, is_pinned)),
        );
    }
    if is_mine && !msg.pending {
        let mid_ed = mid_actions.clone();
        let content_ed = msg_content_for_edit.clone();
        actions = actions.child(
            tiny_action(
                format!("dc-msg-edit-{}", msg.id),
                "Edit",
                false,
                icon_edit(13.0, fg_faint()),
            )
            .on_press(move |s: &mut AppState| s.begin_edit_discord_message(&mid_ed, &content_ed)),
        );
        let cid_d = cid_actions.clone();
        let mid_d = mid_actions.clone();
        actions = actions.child(
            tiny_action(
                format!("dc-msg-del-{}", msg.id),
                "Delete",
                true,
                icon_trash(13.0, fg_faint()),
            )
            .on_press(move |s: &mut AppState| s.delete_discord_message(&cid_d, &mid_d)),
        );
    }

    let mut actions = Some(actions);

    if !grouped {
        let mut header_left = row().gap(7.0).center_y().child(
            text(author_name)
                .font(type_control())
                .color(if is_mine { fg_muted() } else { FG }),
        );
        if msg.author.bot {
            header_left = header_left.child(
                el().px(4.0)
                    .py(1.0)
                    .radius(4.0)
                    .fill(subtle_fill())
                    .child(text("APP").font(type_label()).color(fg_muted())),
            );
        }
        let ts = msg.time_short();
        if !ts.is_empty() {
            header_left = header_left.child(text(ts).font(type_label()).color(fg_faint()));
        }
        if msg.edited_timestamp.is_some() {
            header_left = header_left.child(text("(edited)").font(type_label()).color(fg_faint()));
        }
        if msg.pinned {
            header_left = header_left.child(
                row()
                    .gap(3.0)
                    .center_y()
                    .child(icon_pin(10.0, good()))
                    .child(text("Pinned").font(type_label()).color(good())),
            );
        }
        body = body.child(
            row()
                .w_full()
                .justify_between()
                .center_y()
                .child(header_left)
                .child(actions.take().unwrap_or_else(el)),
        );
    }

    if is_editing {
        if let Some((_, ref edit_buf)) = s.discord.chat.editing {
            body = body.child(
                col()
                    .w_full()
                    .gap(6.0)
                    .px(10.0)
                    .py(8.0)
                    .radius(RADIUS_SMALL)
                    .fill(card_surface())
                    .border(1.0, glass_rim())
                    .child(
                        text_input(edit_buf, type_body())
                            .key("dc-edit-input")
                            .label("Edit message")
                            .w_full()
                            .single_line()
                            .color(FG)
                            .on_edit(edit_access(|s: &mut AppState| {
                                &mut s.discord.chat.editing.as_mut().unwrap().1
                            }))
                            .on_submit(|s: &mut AppState, _| s.save_discord_edit()),
                    )
                    .child(
                        row()
                            .gap(8.0)
                            .child(
                                primary_button("dc-edit-save", "Save", None)
                                    .on_press(|s: &mut AppState| s.save_discord_edit()),
                            )
                            .child(
                                glass_button("dc-edit-cancel", "Cancel", None)
                                    .on_press(|s: &mut AppState| s.cancel_edit_discord_message()),
                            ),
                    ),
            );
        }
    } else if !msg.content.is_empty() {
        let md = el().key(format!("dc-msg-body-{}", msg.id)).w_full().child(
            discord_md::render_markdown(&msg.content, &msg.mentions, &msg.id, reveal_spoilers),
        );
        if grouped {
            body = body.child(
                row()
                    .w_full()
                    .justify_between()
                    .gap(8.0)
                    .child(col().grow(1.0).min_w(0.0).child(md))
                    .child(actions.take().unwrap_or_else(el)),
            );
        } else {
            body = body.child(md);
        }
    }

    if let Some(ref fail) = msg.failed {
        body = body.child(
            text(format!("Failed: {fail}"))
                .font(type_label())
                .color(bad()),
        );
    }

    if !msg.attachments.is_empty() {
        let mut att_col = col().w_full().gap(6.0).pt(2.0);
        for att in &msg.attachments {
            if att.is_image()
                && let Some(img) = s.remote_image_sized(att.best_image_url(), 640.0, scale)
            {
                let (w, h) = match (att.width, att.height) {
                    (Some(iw), Some(ih)) if iw > 0 && ih > 0 => {
                        let ratio = (iw as f32 / ih as f32).clamp(0.4, 2.4);
                        let tw = (iw as f32).min(380.0);
                        let th = (tw / ratio).min(280.0);
                        (th * ratio, th)
                    }
                    _ => (320.0, 200.0),
                };
                att_col = att_col.child(
                    el().size(w, h)
                        .radius(RADIUS_CARD)
                        .clip_rounded(RADIUS_CARD)
                        .border(1.0, hairline())
                        .pixels_fit(img.pixels(), SurfaceFit::Fill),
                );
            } else {
                att_col = att_col.child(
                    row()
                        .px(10.0)
                        .py(8.0)
                        .gap(8.0)
                        .center_y()
                        .radius(RADIUS_SMALL)
                        .fill(card_surface())
                        .border(1.0, hairline())
                        .child(icon_paperclip(13.0, fg_muted()))
                        .child(text(att.filename.clone()).font(type_small()).color(FG))
                        .child(
                            text(format_bytes(att.size))
                                .font(type_label())
                                .color(fg_faint()),
                        ),
                );
            }
        }
        body = body.child(att_col);
    }

    for embed in &msg.embeds {
        body = body.child(embed_card(s, embed, &msg.id, scale));
    }

    if !msg.reactions.is_empty() || show_emoji_bar {
        let mut rrow = row().w_full().gap(6.0).pt(2.0).flex_wrap().center_y();
        for r in &msg.reactions {
            let cid_rx = channel_id.to_string();
            let mid_rx = msg.id.clone();
            let key_rx = r.emoji.key();
            let me_rx = r.me;
            rrow = rrow.child(
                row()
                    .key(format!("dc-rx-{}-{}", msg.id, key_rx))
                    .h(26.0)
                    .px(8.0)
                    .gap(5.0)
                    .center_y()
                    .radius(RADIUS_PILL)
                    .fill(if me_rx {
                        good().alpha(0.16)
                    } else {
                        subtle_fill()
                    })
                    .border(
                        1.0,
                        if me_rx {
                            good().alpha(0.45)
                        } else {
                            hairline()
                        },
                    )
                    .hover(hover_fill())
                    .hit()
                    .press_on_down()
                    .press_scale(PRESS_SCALE)
                    .on_press(move |s: &mut AppState| {
                        s.toggle_discord_reaction(&cid_rx, &mid_rx, &key_rx, me_rx)
                    })
                    .child(text(r.emoji.display()).font(type_small()).color(FG))
                    .child(
                        text(r.count.to_string())
                            .font(type_label())
                            .color(if me_rx { good() } else { fg_muted() }),
                    ),
            );
        }
        if show_emoji_bar {
            let mut bar = row()
                .key(format!("dc-emoji-bar-{}", msg.id))
                .h(28.0)
                .px(6.0)
                .gap(2.0)
                .center_y()
                .radius(RADIUS_PILL)
                .fill(card_surface())
                .border(1.0, glass_rim());
            for &em in QUICK_EMOJIS {
                let cid_q = channel_id.to_string();
                let mid_q = msg.id.clone();
                let em_s = em.to_string();
                let already = msg.reactions.iter().any(|r| r.emoji.key() == em && r.me);
                bar = bar.child(
                    el().key(format!("dc-qrx-{}-{em}", msg.id))
                        .size(24.0, 24.0)
                        .radius(12.0)
                        .hover(hover_fill())
                        .center()
                        .hit()
                        .press_on_down()
                        .on_press(move |s: &mut AppState| {
                            s.toggle_discord_reaction(&cid_q, &mid_q, &em_s, already)
                        })
                        .child(text(em).font(type_small()).color(FG)),
                );
            }
            rrow = rrow.child(bar);
        }
        body = body.child(rrow);
    }

    let line = if grouped {
        row()
            .w_full()
            .pl(56.0)
            .pr(16.0)
            .py(2.0)
            .opacity(if msg.pending { 0.6 } else { 1.0 })
            .hover_group()
            .hover_motion(Spring::NONE, SPRING_QUICK)
            .child(body)
    } else {
        let av_url = msg.author.avatar_url();
        row()
            .w_full()
            .px(16.0)
            .pt(10.0)
            .pb(3.0)
            .gap(10.0)
            .opacity(if msg.pending { 0.6 } else { 1.0 })
            .hover_group()
            .hover_motion(Spring::NONE, SPRING_QUICK)
            .child(avatar_box(
                s,
                av_url.as_deref(),
                msg.display_name(),
                30.0,
                15.0,
                None,
                false,
                scale,
            ))
            .child(body)
    };

    if new_day && !msg.day_key().is_empty() {
        col()
            .w_full()
            .child(
                row()
                    .w_full()
                    .px(16.0)
                    .pt(12.0)
                    .pb(4.0)
                    .gap(10.0)
                    .center_y()
                    .child(el().grow(1.0).h(1.0).fill(hairline()))
                    .child(
                        text(msg.day_key().to_string())
                            .font(type_label())
                            .color(fg_faint()),
                    )
                    .child(el().grow(1.0).h(1.0).fill(hairline())),
            )
            .child(line)
    } else {
        line
    }
}

pub fn timeline_and_composer(
    s: &AppState,
    channel_id: &str,
    placeholder: String,
    welcome_title: String,
    welcome_sub: String,
    can_send: bool,
    scale: f32,
) -> El {
    let msgs = s
        .discord
        .chat
        .messages_by_channel
        .get(channel_id)
        .map(Vec::as_slice)
        .unwrap_or(&[]);
    let msg_count = msgs.len();
    let is_loading = s.discord.chat.loading_channels.contains(channel_id);
    let has_more = s
        .discord
        .chat
        .has_more_before
        .get(channel_id)
        .copied()
        .unwrap_or(false);
    let oldest_id = msgs.first().map(|m| m.id.clone());

    let mut scroll_inner = col().w_full().grow(1.0).justify_end().pb(12.0);
    if has_more && let Some(before_id) = oldest_id {
        let cid_older = channel_id.to_string();
        scroll_inner = scroll_inner.child(
            row().w_full().py(10.0).justify_center().child(
                glass_button(
                    "dc-load-older",
                    if is_loading {
                        "Loading…"
                    } else {
                        "Load older messages"
                    },
                    None,
                )
                .on_press(move |s: &mut AppState| {
                    s.load_discord_messages(&cid_older, Some(&before_id))
                }),
            ),
        );
    } else {
        scroll_inner = scroll_inner.child(
            col()
                .w_full()
                .px(16.0)
                .pt(22.0)
                .pb(12.0)
                .gap(4.0)
                .child(text(welcome_title).font(type_page()).color(FG))
                .child(
                    text(welcome_sub)
                        .font(type_body())
                        .color(fg_muted())
                        .w_full()
                        .wrap(),
                ),
        );
    }

    if msg_count > 0 {
        let cid_key = channel_id.to_string();
        let cid_build = channel_id.to_string();
        let rows_list = col()
            .key(format!("dc-vrows-{channel_id}"))
            .w_full()
            .growth(GrowthSpec::default())
            .virtual_rows(
                msg_count,
                68.0,
                move |s: &AppState, i: usize| {
                    s.discord
                        .chat
                        .messages_by_channel
                        .get(&cid_key)
                        .and_then(|v| v.get(i))
                        .map(|m| Cow::Borrowed(m.id.as_str()))
                        .unwrap_or(Cow::Borrowed("missing"))
                },
                move |s: &AppState, i: usize| {
                    let Some(list) = s.discord.chat.messages_by_channel.get(&cid_build) else {
                        return el();
                    };
                    let Some(msg) = list.get(i) else {
                        return el();
                    };
                    let prev = i.checked_sub(1).and_then(|p| list.get(p));
                    message_row(s, &cid_build, msg, prev, scale)
                },
            );
        scroll_inner = scroll_inner.child(rows_list);
    } else if is_loading {
        scroll_inner = scroll_inner.child(
            text("Loading messages…")
                .font(type_small())
                .color(fg_muted())
                .px(16.0)
                .py(12.0),
        );
    }

    let timeline = col()
        .key(format!("dc-timeline-{channel_id}"))
        .grow(1.0)
        .w_full()
        .min_h(0.0)
        .scroll(ScrollSpec { stick_bottom: true })
        .child(scroll_inner);

    let mut composer_wrap = col().w_full().px(12.0).pb(10.0).gap(6.0);

    if let Some(typing) = s.discord.chat.typing_label(channel_id) {
        composer_wrap =
            composer_wrap.child(text(typing).font(type_label()).color(fg_muted()).px(6.0));
    }

    if let Some((_, ref author)) = s.discord.chat.reply_to {
        composer_wrap = composer_wrap.child(
            row()
                .key("dc-reply-bar")
                .w_full()
                .h(30.0)
                .px(10.0)
                .gap(8.0)
                .justify_between()
                .center_y()
                .radius(RADIUS_SMALL)
                .fill(card_surface())
                .border(1.0, hairline())
                .transition(inline_bar_transition(6.0))
                .child(
                    row()
                        .gap(6.0)
                        .center_y()
                        .child(icon_reply(12.0, good()))
                        .child(
                            text(format!("Replying to {author}"))
                                .font(type_small())
                                .color(FG),
                        ),
                )
                .child(
                    tiny_action(
                        "dc-reply-cancel".into(),
                        "Cancel reply",
                        false,
                        icon_close(12.0, fg_muted()),
                    )
                    .on_press(|s: &mut AppState| s.discord.chat.reply_to = None),
                ),
        );
    }

    if !s.discord.chat.pending_uploads.is_empty() {
        let mut up_row = row().w_full().gap(6.0).flex_wrap();
        for u in &s.discord.chat.pending_uploads {
            let lid = u.local_id.clone();
            let status_txt = if u.uploading {
                "Uploading…".into()
            } else if let Some(ref e) = u.error {
                e.clone()
            } else {
                format_bytes(u.size)
            };
            up_row = up_row.child(
                row()
                    .key(format!("dc-up-{}", u.local_id))
                    .h(30.0)
                    .px(10.0)
                    .gap(6.0)
                    .center_y()
                    .radius(RADIUS_SMALL)
                    .fill(card_surface())
                    .border(
                        1.0,
                        if u.error.is_some() {
                            bad().alpha(0.5)
                        } else {
                            glass_rim()
                        },
                    )
                    .child(icon_paperclip(12.0, fg_muted()))
                    .child(text(u.name.clone()).font(type_small()).color(FG))
                    .child(text(status_txt).font(type_label()).color(fg_faint()))
                    .child(
                        tiny_action(
                            format!("dc-up-rm-{}", u.local_id),
                            "Remove attachment",
                            false,
                            icon_close(11.0, fg_muted()),
                        )
                        .on_press(move |s: &mut AppState| s.remove_pending_upload(&lid)),
                    ),
            );
        }
        composer_wrap = composer_wrap.child(up_row);
    }

    if can_send {
        let composer = row()
            .key("dc-composer")
            .w_full()
            .h(44.0)
            .px(10.0)
            .gap(8.0)
            .center_y()
            .radius(RADIUS_CARD)
            .fill(card_surface())
            .border(
                1.0,
                if s.discord.chat.drop_hover {
                    good()
                } else {
                    glass_rim()
                },
            )
            .child(
                tiny_action(
                    "dc-composer-emoji".into(),
                    "Insert emoji",
                    false,
                    icon_plus(14.0, fg_muted()),
                )
                .on_press(|s: &mut AppState| {
                    s.discord.chat.composer_emoji_open = !s.discord.chat.composer_emoji_open
                }),
            )
            .child(
                text_input(&s.discord.chat.composer, type_body())
                    .key("dc-composer-input")
                    .label("Message composer")
                    .grow(1.0)
                    .single_line()
                    .color(FG)
                    .placeholder(placeholder, fg_muted())
                    .on_edit(edit_access(|s: &mut AppState| {
                        s.notify_discord_typing();
                        &mut s.discord.chat.composer
                    }))
                    .on_submit(send_with_launch),
            )
            .child(
                el().key("dc-composer-send")
                    .role("button")
                    .label("Send message")
                    .focusable(true)
                    .hit()
                    .press_on_down()
                    .press_scale(PRESS_SCALE)
                    .size(30.0, 30.0)
                    .radius(RADIUS_PILL)
                    .fill(FG)
                    .hover(FG.alpha(0.92))
                    .press(FG.alpha(0.82))
                    .center()
                    .motion(SPRING_QUICK)
                    .on_press_tx(send_with_launch)
                    .child(icon_send(14.0, PRIMARY_FG)),
            );
        composer_wrap = composer_wrap.child(composer);
    } else {
        composer_wrap = composer_wrap.child(
            row()
                .w_full()
                .h(40.0)
                .px(14.0)
                .center_y()
                .radius(RADIUS_CARD)
                .fill(subtle_fill())
                .child(
                    text("You don't have permission to send messages in this channel.")
                        .font(type_small())
                        .color(fg_muted()),
                ),
        );
    }

    let mut stage = col()
        .grow(1.0)
        .w_full()
        .min_h(0.0)
        .radius(RADIUS_PANEL)
        .fill(pane_surface())
        .border(
            1.0,
            if s.discord.chat.drop_hover {
                good()
            } else {
                hairline()
            },
        )
        .on_drop_hover(|s: &mut AppState, over, _| s.discord.chat.drop_hover = over)
        .on_drop_files(|s: &mut AppState, files, _| s.attach_discord_files(files))
        .child(timeline)
        .child(composer_wrap);

    if s.discord.chat.composer_emoji_open {
        let mut ebar = row()
            .key("dc-composer-emoji-bar")
            .pin(Some(12.0), None, None, Some(60.0))
            .px(8.0)
            .py(4.0)
            .gap(4.0)
            .center_y()
            .radius(RADIUS_PILL)
            .fill(card_surface())
            .border(1.0, glass_rim())
            .transition(inline_bar_transition(6.0));
        for &em in QUICK_EMOJIS {
            let em_s = em.to_string();
            ebar = ebar.child(
                el().key(format!("dc-cem-{em}"))
                    .size(26.0, 26.0)
                    .radius(13.0)
                    .hover(hover_fill())
                    .center()
                    .hit()
                    .press_on_down()
                    .on_press(move |s: &mut AppState| {
                        s.discord.chat.composer.text.push_str(&em_s);
                        s.discord.chat.composer_emoji_open = false;
                    })
                    .child(text(em).font(type_body()).color(FG)),
            );
        }
        stage = stage.child(ebar);
    }

    stage
}

pub fn chat_drawer_panel(s: &AppState, scale: f32) -> Option<El> {
    match s.discord.chat.drawer {
        ChatDrawer::None => None,
        ChatDrawer::Pins => {
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
                    list = list.child(drawer_message_card(s, m, scale));
                }
            }
            Some(
                col()
                    .key("dc-pins-drawer")
                    .w(284.0)
                    .h_full()
                    .px(10.0)
                    .py(10.0)
                    .gap(10.0)
                    .radius(RADIUS_PANEL)
                    .fill(pane_surface())
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
                                tiny_action(
                                    "dc-drawer-close".into(),
                                    "Close",
                                    false,
                                    icon_close(13.0, fg_muted()),
                                )
                                .on_press(|s: &mut AppState| {
                                    s.discord.chat.drawer = ChatDrawer::None
                                }),
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
                    ),
            )
        }
        ChatDrawer::Search => {
            let mut list = col().w_full().gap(8.0).pb(76.0);
            if s.discord.chat.search_loading {
                list = list.child(text("Searching…").font(type_small()).color(fg_muted()));
            } else if s.discord.chat.search_results.is_empty() {
                let msg = if s.discord.chat.search_input.text.trim().is_empty() {
                    "Type words and press Enter to search this channel."
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
                    list = list.child(drawer_message_card(s, m, scale));
                }
            }
            Some(
                col()
                    .key("dc-search-drawer")
                    .w(284.0)
                    .h_full()
                    .px(10.0)
                    .py(10.0)
                    .gap(10.0)
                    .radius(RADIUS_PANEL)
                    .fill(pane_surface())
                    .border(1.0, glass_rim())
                    .child(
                        row()
                            .w_full()
                            .justify_between()
                            .center_y()
                            .child(
                                row()
                                    .gap(6.0)
                                    .center_y()
                                    .child(icon_search(13.0, FG))
                                    .child(text("Search channel").font(type_section()).color(FG)),
                            )
                            .child(
                                tiny_action(
                                    "dc-drawer-close".into(),
                                    "Close",
                                    false,
                                    icon_close(13.0, fg_muted()),
                                )
                                .on_press(|s: &mut AppState| {
                                    s.discord.chat.drawer = ChatDrawer::None
                                }),
                            ),
                    )
                    .child(
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
                                    .placeholder("Search messages…", fg_muted())
                                    .on_edit(edit_access(|s: &mut AppState| {
                                        &mut s.discord.chat.search_input
                                    }))
                                    .on_submit(|s: &mut AppState, _| s.run_discord_search()),
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
                    ),
            )
        }
    }
}

fn drawer_message_card(s: &AppState, m: &DiscordMessage, scale: f32) -> El {
    let av = m.author.avatar_url();
    col()
        .key(format!("dc-drawer-msg-{}", m.id))
        .w_full()
        .px(10.0)
        .py(8.0)
        .gap(6.0)
        .radius(RADIUS_CARD)
        .fill(card_surface())
        .border(1.0, hairline())
        .child(
            row()
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
                .child(text(m.time_short()).font(type_label()).color(fg_faint())),
        )
        .child(discord_md::render_markdown(
            &m.content,
            &m.mentions,
            &m.id,
            true,
        ))
}

pub fn header_tools(s: &AppState) -> El {
    row()
        .gap(GAP * 0.6)
        .center_y()
        .child(
            glass_button("dc-hdr-pins", "Pins", Some(icon_pin(13.0, FG)))
                .on_press(|s: &mut AppState| s.toggle_discord_pins_drawer()),
        )
        .child(
            glass_button(
                "dc-hdr-search",
                if s.discord.chat.drawer == ChatDrawer::Search {
                    "Searching"
                } else {
                    "Search"
                },
                Some(icon_search(13.0, FG)),
            )
            .on_press(|s: &mut AppState| s.toggle_discord_search_drawer()),
        )
}
