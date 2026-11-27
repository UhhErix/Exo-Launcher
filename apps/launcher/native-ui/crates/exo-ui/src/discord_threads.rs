use crate::discord::avatar_box;
use crate::discord_chat_state::ProfileTab;
use crate::discord_md;
use crate::state::AppState;
use exo_kit::{
    BG, ExoGlass, FG, PRESS_SCALE, PRIMARY_FG, RADIUS_CARD, RADIUS_PANEL, RADIUS_PILL,
    RADIUS_SMALL, SPRING_QUICK, apply_glass, bad, danger_button, fg_faint, fg_muted, glass_button,
    glass_button_opt, glass_control_fill, glass_rim, good, hairline, hex, hover_fill, icon_check,
    icon_close, icon_external, icon_hash, icon_plus, icon_search, icon_trash, icon_user_plus,
    icon_users, modal_card_transition, primary_button, scrim_fill, subtle_fill, switch_pill,
    type_body, type_control, type_label, type_section, type_small,
};
use exo_rpc::{DiscordMessage, DiscordPoll};
use rune::{
    Color, Cursor, El, ScrollSpec, SurfaceFit, TextEdit, col, edit_access, el, row, text,
    text_input,
};
use std::collections::HashMap;

const POLL_DURATIONS: &[(u32, &str)] = &[
    (1, "1 hour"),
    (4, "4 hours"),
    (8, "8 hours"),
    (24, "24 hours"),
    (72, "3 days"),
    (168, "1 week"),
    (336, "2 weeks"),
];

const INVITE_EXPIRES: &[(u32, &str)] = &[
    (1800, "30 minutes"),
    (3600, "1 hour"),
    (21600, "6 hours"),
    (43200, "12 hours"),
    (86400, "1 day"),
    (604_800, "7 days"),
    (0, "Never"),
];

const INVITE_USES: &[(u32, &str)] = &[
    (0, "No limit"),
    (1, "1 use"),
    (5, "5 uses"),
    (10, "10 uses"),
    (25, "25 uses"),
    (50, "50 uses"),
    (100, "100 uses"),
];

const REPORT_REASONS: &[&str] = &[
    "Spam or unwanted content",
    "Harassment or bullying",
    "Hate speech or discrimination",
    "Scam, phishing, or malware",
    "Explicit or unsafe media",
    "Impersonation or other violation",
];

fn modal_wrap(key: &'static str, card: El, on_close: impl Fn(&mut AppState) + 'static) -> El {
    let card = apply_glass(card, ExoGlass::Sheet)
        .modal(true, on_close)
        .modal_scrim(scrim_fill());
    el().key(key)
        .pin(Some(0.0), Some(0.0), Some(0.0), Some(0.0))
        .center()
        .child(card)
}

fn choice_pill(key: String, label: &str, active: bool) -> El {
    el().key(key)
        .cursor(Cursor::Pointer)
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

pub fn create_dm_overlay(s: &AppState, scale: f32) -> Option<El> {
    let dlg = s.discord.chat.create_dm.as_ref()?;
    let q = dlg.query.text.trim().to_lowercase();
    let left = 9usize.saturating_sub(dlg.picked.len());
    let friends: Vec<_> = s
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
        .take(40)
        .collect();

    let mut list = col().w_full().gap(4.0);
    if friends.is_empty() {
        list = list.child(
            text("No matching friends.")
                .font(type_small())
                .color(fg_muted()),
        );
    } else {
        for f in friends {
            let picked = dlg.picked.iter().any(|id| id == &f.id);
            let disabled = !picked && left == 0;
            let fid = f.id.clone();
            let mut frow = row()
                .key(format!("dc-cdm-f-{}", f.id))
                .w_full()
                .h(38.0)
                .px(8.0)
                .gap(10.0)
                .center_y()
                .radius(RADIUS_SMALL)
                .fill(if picked {
                    subtle_fill()
                } else {
                    Color::TRANSPARENT
                })
                .opacity(if disabled { 0.45 } else { 1.0 })
                .hover(if disabled {
                    Color::TRANSPARENT
                } else {
                    hover_fill()
                })
                .cursor(if disabled {
                    Cursor::Default
                } else {
                    Cursor::Pointer
                })
                .hit()
                .press_on_down()
                .child(avatar_box(
                    s,
                    f.avatar.as_deref(),
                    &f.name,
                    26.0,
                    13.0,
                    Some(f.status.as_str()),
                    false,
                    scale,
                ))
                .child(
                    text(f.name.clone())
                        .font(type_small())
                        .color(FG)
                        .grow(1.0)
                        .truncate(),
                );
            if picked {
                frow = frow.child(icon_check(14.0, good()));
            }
            if !disabled {
                frow = frow.on_press(move |s: &mut AppState| s.toggle_create_dm_friend(&fid));
            }
            list = list.child(frow);
        }
    }

    let mut card = col()
        .key("dc-create-dm-card")
        .w(420.0)
        .padding(18.0)
        .gap(12.0)
        .radius(RADIUS_PANEL)
        .transition(modal_card_transition())
        .child(
            row()
                .w_full()
                .justify_between()
                .center_y()
                .child(
                    row()
                        .gap(8.0)
                        .center_y()
                        .child(icon_users(16.0, FG))
                        .child(text("Select friends").font(type_section()).color(FG)),
                )
                .child(
                    el().key("dc-cdm-close")
                        .size(26.0, 26.0)
                        .radius(13.0)
                        .hover(hover_fill())
                        .center()
                        .cursor(Cursor::Pointer)
                        .hit()
                        .press_on_down()
                        .on_press(|s: &mut AppState| s.discord.chat.create_dm = None)
                        .child(icon_close(13.0, fg_muted())),
                ),
        )
        .child(
            text(if left == 0 {
                "This group has a 10 member limit.".to_string()
            } else {
                format!(
                    "You can add {left} more friend{}.",
                    if left == 1 { "" } else { "s" }
                )
            })
            .font(type_label())
            .color(fg_muted()),
        )
        .child(
            row()
                .w_full()
                .h(34.0)
                .px(10.0)
                .gap(8.0)
                .center_y()
                .radius(RADIUS_SMALL)
                .fill(glass_control_fill())
                .border(1.0, glass_rim())
                .child(icon_search(13.0, fg_muted()))
                .child(
                    text_input(&dlg.query, type_small())
                        .key("dc-cdm-find")
                        .label("Find a friend")
                        .grow(1.0)
                        .single_line()
                        .color(FG)
                        .placeholder("Find a friend", fg_muted())
                        .on_edit(edit_access(|s: &mut AppState| {
                            match s.discord.chat.create_dm.as_mut() {
                                Some(dlg) => &mut dlg.query,
                                None => &mut s.ui_discard_edit,
                            }
                        })),
                ),
        )
        .child(
            col()
                .w_full()
                .h(240.0)
                .scroll(ScrollSpec {
                    stick_bottom: false,
                })
                .child(list),
        );

    if let Some(ref err) = dlg.error {
        card = card.child(text(err.clone()).font(type_small()).color(bad()));
    }

    let btn_label = if dlg.busy {
        "Creating…"
    } else if dlg.picked.len() > 1 {
        "Create group DM"
    } else {
        "Create DM"
    };
    card = card.child(
        row()
            .w_full()
            .justify_end()
            .gap(8.0)
            .child(
                glass_button_opt("dc-cdm-cancel", "Cancel", None, false)
                    .on_press(|s: &mut AppState| s.discord.chat.create_dm = None),
            )
            .child(
                primary_button("dc-cdm-submit", btn_label, None)
                    .on_press(|s: &mut AppState| s.submit_create_dm()),
            ),
    );

    Some(modal_wrap("dc-create-dm-scrim", card, |s| {
        s.discord.chat.create_dm = None
    }))
}

pub fn poll_creator_overlay(s: &AppState) -> Option<El> {
    let pc = s.discord.chat.poll_creator.as_ref()?;
    let mut answers_col = col().w_full().gap(6.0);
    for (i, ans) in pc.answers.iter().enumerate() {
        let mut arow = row().w_full().gap(6.0).center_y().child(
            row()
                .grow(1.0)
                .h(34.0)
                .px(10.0)
                .center_y()
                .radius(RADIUS_SMALL)
                .fill(glass_control_fill())
                .border(1.0, glass_rim())
                .child(
                    text_input(ans, type_small())
                        .key(format!("dc-poll-ans-{i}"))
                        .label(format!("Answer {}", i + 1))
                        .grow(1.0)
                        .single_line()
                        .color(FG)
                        .placeholder(format!("Answer {}", i + 1), fg_faint())
                        .on_edit(edit_access(move |s: &mut AppState| {
                            match s
                                .discord
                                .chat
                                .poll_creator
                                .as_mut()
                                .and_then(|p| p.answers.get_mut(i))
                            {
                                Some(ans) => ans,
                                None => &mut s.ui_discard_edit,
                            }
                        })),
                ),
        );
        if pc.answers.len() > 2 {
            arow = arow.child(
                el().key(format!("dc-poll-ans-rm-{i}"))
                    .size(28.0, 28.0)
                    .radius(14.0)
                    .hover(bad().alpha(0.22))
                    .center()
                    .cursor(Cursor::Pointer)
                    .hit()
                    .press_on_down()
                    .on_press(move |s: &mut AppState| {
                        if let Some(ref mut p) = s.discord.chat.poll_creator
                            && p.answers.len() > 2
                            && i < p.answers.len()
                        {
                            p.answers.remove(i);
                        }
                    })
                    .child(icon_trash(13.0, bad())),
            );
        }
        answers_col = answers_col.child(arow);
    }

    if pc.answers.len() < 10 {
        answers_col = answers_col.child(
            glass_button_opt(
                "dc-poll-add-ans",
                "Add another answer",
                Some(icon_plus(12.0, FG)),
                false,
            )
            .on_press(|s: &mut AppState| {
                if let Some(ref mut p) = s.discord.chat.poll_creator
                    && p.answers.len() < 10
                {
                    p.answers.push(TextEdit::default());
                }
            }),
        );
    }

    let mut dur_row = row().w_full().gap(4.0).flex_wrap();
    for &(hrs, lbl) in POLL_DURATIONS {
        dur_row = dur_row.child(
            choice_pill(format!("dc-poll-dur-{hrs}"), lbl, pc.hours == hrs).on_press(
                move |s: &mut AppState| {
                    if let Some(ref mut p) = s.discord.chat.poll_creator {
                        p.hours = hrs;
                    }
                },
            ),
        );
    }

    let mut card = col()
        .key("dc-poll-creator-card")
        .w(480.0)
        .padding(20.0)
        .gap(12.0)
        .radius(RADIUS_PANEL)
        .transition(modal_card_transition())
        .child(
            row()
                .w_full()
                .justify_between()
                .center_y()
                .child(text("Create a poll").font(type_section()).color(FG))
                .child(
                    el().key("dc-poll-close")
                        .size(26.0, 26.0)
                        .radius(13.0)
                        .hover(hover_fill())
                        .center()
                        .cursor(Cursor::Pointer)
                        .hit()
                        .press_on_down()
                        .on_press(|s: &mut AppState| s.discord.chat.poll_creator = None)
                        .child(icon_close(13.0, fg_muted())),
                ),
        )
        .child(
            col()
                .w_full()
                .gap(4.0)
                .child(text("Question").font(type_small()).color(fg_muted()))
                .child(
                    row()
                        .w_full()
                        .h(36.0)
                        .px(10.0)
                        .center_y()
                        .radius(RADIUS_SMALL)
                        .fill(glass_control_fill())
                        .border(1.0, glass_rim())
                        .child(
                            text_input(&pc.question, type_body())
                                .key("dc-poll-question")
                                .label("Question")
                                .grow(1.0)
                                .single_line()
                                .color(FG)
                                .placeholder("What question do you want to ask?", fg_faint())
                                .on_edit(edit_access(|s: &mut AppState| {
                                    match s.discord.chat.poll_creator.as_mut() {
                                        Some(p) => &mut p.question,
                                        None => &mut s.ui_discard_edit,
                                    }
                                })),
                        ),
                ),
        )
        .child(
            col()
                .w_full()
                .gap(6.0)
                .child(text("Answers").font(type_small()).color(fg_muted()))
                .child(answers_col),
        )
        .child(
            col()
                .w_full()
                .gap(6.0)
                .child(text("Duration").font(type_small()).color(fg_muted()))
                .child(dur_row),
        )
        .child(
            row()
                .w_full()
                .justify_between()
                .center_y()
                .child(text("Allow multiple answers").font(type_small()).color(FG))
                .child(
                    switch_pill(
                        "dc-poll-multi",
                        "Allow multiple answers",
                        pc.allow_multiselect,
                    )
                    .on_press(|s: &mut AppState| {
                        if let Some(ref mut p) = s.discord.chat.poll_creator {
                            p.allow_multiselect = !p.allow_multiselect;
                        }
                    }),
                ),
        );

    if let Some(ref err) = pc.error {
        card = card.child(text(err.clone()).font(type_small()).color(bad()));
    }

    card = card.child(
        row()
            .w_full()
            .justify_end()
            .gap(8.0)
            .child(
                glass_button_opt("dc-poll-cancel", "Cancel", None, false)
                    .on_press(|s: &mut AppState| s.discord.chat.poll_creator = None),
            )
            .child(
                primary_button(
                    "dc-poll-post",
                    if pc.busy { "Posting…" } else { "Post" },
                    None,
                )
                .on_press(|s: &mut AppState| s.submit_poll_creator()),
            ),
    );

    Some(modal_wrap("dc-poll-creator-scrim", card, |s| {
        s.discord.chat.poll_creator = None
    }))
}

pub fn poll_card(s: &AppState, msg: &DiscordMessage, poll: &DiscordPoll) -> El {
    let counts: HashMap<i32, (i32, bool)> = poll
        .results
        .as_ref()
        .map(|r| {
            r.answer_counts
                .iter()
                .map(|c| (c.id, (c.count, c.me_voted)))
                .collect()
        })
        .unwrap_or_default();
    let total: i32 = counts.values().map(|(c, _)| *c).sum();
    let voted = counts.values().any(|(_, me)| *me);
    let closed = poll.results.as_ref().is_some_and(|r| r.is_finalized);
    let showing = voted || closed;
    let me_id = s.discord.home.me.as_ref().map(|m| m.id.as_str());
    let is_author = me_id == Some(msg.author.id.as_str());
    let picks = s.discord.chat.poll_picks.get(&msg.id);

    let mut card = col()
        .key(format!("dc-poll-{}", msg.id))
        .w(440.0)
        .max_w(480.0)
        .padding(14.0)
        .gap(10.0)
        .radius(RADIUS_CARD)
        .fill(BG)
        .border(1.0, glass_rim())
        .child(
            text(poll.question.text.clone().unwrap_or_else(|| "Poll".into()))
                .font(type_section())
                .color(FG)
                .w_full()
                .wrap(),
        );

    if !showing && poll.allow_multiselect {
        card = card.child(
            text("Multiple answers")
                .font(type_label())
                .color(fg_faint()),
        );
    }

    let mut answers_col = col().w_full().gap(6.0);
    for ans in &poll.answers {
        let label = ans.poll_media.text.clone().unwrap_or_default();
        let emoji_prefix = ans
            .poll_media
            .emoji
            .as_ref()
            .and_then(|e| e.name.as_deref())
            .map(|n| format!("{n} "))
            .unwrap_or_default();
        if showing {
            let (cnt, mine) = counts.get(&ans.answer_id).copied().unwrap_or((0, false));
            let pct = if total > 0 {
                ((cnt as f32 / total as f32) * 100.0).round() as i32
            } else {
                0
            };
            let fill_col = if mine {
                good().alpha(0.18)
            } else {
                subtle_fill()
            };
            let border_col = if mine { good().alpha(0.5) } else { hairline() };
            let mut r = row()
                .w_full()
                .h(36.0)
                .px(10.0)
                .gap(8.0)
                .center_y()
                .radius(RADIUS_SMALL)
                .fill(fill_col)
                .border(1.0, border_col)
                .child(
                    text(format!("{emoji_prefix}{label}"))
                        .font(type_small())
                        .color(FG)
                        .grow(1.0)
                        .truncate(),
                );
            if mine {
                r = r.child(icon_check(13.0, good()));
            }
            r = r.child(
                text(format!("{cnt} · {pct}%"))
                    .font(type_label())
                    .color(fg_muted()),
            );
            answers_col = answers_col.child(r);
        } else {
            let picked = picks.is_some_and(|p| p.contains(&ans.answer_id));
            let mid_c = msg.id.clone();
            let aid = ans.answer_id;
            let multi = poll.allow_multiselect;
            let mut btn = row()
                .key(format!("dc-poll-opt-{}-{}", msg.id, ans.answer_id))
                .w_full()
                .h(36.0)
                .px(10.0)
                .gap(8.0)
                .center_y()
                .radius(RADIUS_SMALL)
                .fill(if picked {
                    good().alpha(0.16)
                } else {
                    subtle_fill()
                })
                .border(
                    1.0,
                    if picked {
                        good().alpha(0.55)
                    } else {
                        hairline()
                    },
                )
                .hover(hover_fill())
                .cursor(Cursor::Pointer)
                .hit()
                .press_on_down()
                .on_press(move |s: &mut AppState| s.toggle_poll_pick(&mid_c, aid, multi))
                .child(
                    text(format!("{emoji_prefix}{label}"))
                        .font(type_small())
                        .color(FG)
                        .grow(1.0)
                        .truncate(),
                );
            if picked {
                btn = btn.child(icon_check(13.0, good()));
            }
            answers_col = answers_col.child(btn);
        }
    }
    card = card.child(answers_col);

    let mut actions = row().gap(6.0).center_y();
    if !closed && voted {
        let cid = msg.channel_id.clone();
        let mid = msg.id.clone();
        actions = actions.child(
            glass_button("dc-poll-unvote", "Remove vote", None)
                .key(format!("dc-poll-unvote-{}", msg.id))
                .on_press(move |s: &mut AppState| s.submit_poll_vote(&cid, &mid, Vec::new())),
        );
    }
    if !closed && is_author {
        let cid = msg.channel_id.clone();
        let mid = msg.id.clone();
        actions = actions.child(
            glass_button("dc-poll-end", "End poll", None)
                .key(format!("dc-poll-end-{}", msg.id))
                .on_press(move |s: &mut AppState| s.end_discord_poll(&cid, &mid)),
        );
    }
    if !showing {
        let cid = msg.channel_id.clone();
        let mid = msg.id.clone();
        let chosen: Vec<i32> = picks
            .map(|p| p.iter().copied().collect())
            .unwrap_or_default();
        if !chosen.is_empty() {
            actions = actions.child(
                primary_button("dc-poll-vote", "Vote", None)
                    .key(format!("dc-poll-vote-{}", msg.id))
                    .on_press(move |s: &mut AppState| {
                        s.submit_poll_vote(&cid, &mid, chosen.clone())
                    }),
            );
        }
    }

    let status = if closed {
        format!(
            "{total} {} · Poll closed",
            if total == 1 { "vote" } else { "votes" }
        )
    } else {
        format!("{total} {}", if total == 1 { "vote" } else { "votes" })
    };

    card.child(
        row()
            .w_full()
            .justify_between()
            .center_y()
            .child(text(status).font(type_label()).color(fg_faint()))
            .child(actions),
    )
}

pub fn invite_dialog_overlay(s: &AppState, scale: f32) -> Option<El> {
    let dlg = s.discord.chat.invite_dialog.as_ref()?;
    if dlg.editing {
        let mut exp_row = row().w_full().gap(4.0).flex_wrap();
        for &(secs, lbl) in INVITE_EXPIRES {
            exp_row = exp_row.child(
                choice_pill(format!("dc-inv-exp-{secs}"), lbl, dlg.max_age == secs).on_press(
                    move |s: &mut AppState| {
                        if let Some(ref mut d) = s.discord.chat.invite_dialog {
                            d.max_age = secs;
                        }
                    },
                ),
            );
        }
        let mut uses_row = row().w_full().gap(4.0).flex_wrap();
        for &(u, lbl) in INVITE_USES {
            uses_row = uses_row.child(
                choice_pill(format!("dc-inv-use-{u}"), lbl, dlg.max_uses == u).on_press(
                    move |s: &mut AppState| {
                        if let Some(ref mut d) = s.discord.chat.invite_dialog {
                            d.max_uses = u;
                        }
                    },
                ),
            );
        }
        let card = col()
            .key("dc-invite-edit-card")
            .w(440.0)
            .padding(20.0)
            .gap(12.0)
            .radius(RADIUS_PANEL)
            .transition(modal_card_transition())
            .child(
                text("Server invite link settings")
                    .font(type_section())
                    .color(FG),
            )
            .child(
                col()
                    .w_full()
                    .gap(6.0)
                    .child(text("Expire after").font(type_small()).color(fg_muted()))
                    .child(exp_row),
            )
            .child(
                col()
                    .w_full()
                    .gap(6.0)
                    .child(
                        text("Max number of uses")
                            .font(type_small())
                            .color(fg_muted()),
                    )
                    .child(uses_row),
            )
            .child(
                row()
                    .w_full()
                    .justify_between()
                    .center_y()
                    .child(
                        text("Grant temporary membership")
                            .font(type_small())
                            .color(FG),
                    )
                    .child(
                        switch_pill("dc-inv-temp", "Grant temporary membership", dlg.temporary)
                            .on_press(|s: &mut AppState| {
                                if let Some(ref mut d) = s.discord.chat.invite_dialog {
                                    d.temporary = !d.temporary;
                                }
                            }),
                    ),
            )
            .child(
                row()
                    .w_full()
                    .justify_end()
                    .gap(8.0)
                    .child(
                        glass_button_opt("dc-inv-edit-back", "Cancel", None, false).on_press(
                            |s: &mut AppState| {
                                if let Some(ref mut d) = s.discord.chat.invite_dialog {
                                    d.editing = false;
                                }
                            },
                        ),
                    )
                    .child(
                        primary_button("dc-inv-edit-gen", "Generate a new link", None)
                            .on_press(|s: &mut AppState| s.generate_invite_link()),
                    ),
            );
        return Some(modal_wrap("dc-invite-scrim", card, |s| {
            s.discord.chat.invite_dialog = None
        }));
    }

    let q = dlg.query.text.trim().to_lowercase();
    let friends: Vec<_> = s
        .discord
        .friends
        .iter()
        .filter(|f| f.kind == "friend")
        .filter(|f| q.is_empty() || f.name.to_lowercase().contains(&q))
        .take(30)
        .collect();

    let mut flist = col().w_full().gap(4.0);
    if friends.is_empty() {
        flist = flist.child(
            text("No friends to invite.")
                .font(type_small())
                .color(fg_muted()),
        );
    } else {
        for f in friends {
            let sent = dlg.sent_to.contains(&f.id);
            let fid = f.id.clone();
            flist = flist.child(
                row()
                    .key(format!("dc-inv-f-{}", f.id))
                    .w_full()
                    .h(38.0)
                    .px(6.0)
                    .gap(8.0)
                    .center_y()
                    .radius(RADIUS_SMALL)
                    .hover(hover_fill())
                    .child(avatar_box(
                        s,
                        f.avatar.as_deref(),
                        &f.name,
                        26.0,
                        13.0,
                        None,
                        false,
                        scale,
                    ))
                    .child(
                        text(f.name.clone())
                            .font(type_small())
                            .color(FG)
                            .grow(1.0)
                            .truncate(),
                    )
                    .child(
                        glass_button_opt(
                            "dc-inv-send",
                            if sent { "Sent" } else { "Invite" },
                            None,
                            false,
                        )
                        .key(format!("dc-inv-send-{}", f.id))
                        .on_press(move |s: &mut AppState| {
                            if !sent {
                                s.send_invite_to_friend(&fid);
                            }
                        }),
                    ),
            );
        }
    }

    let link_text = dlg
        .url
        .clone()
        .unwrap_or_else(|| "Making a link…".to_string());
    let copy_url = dlg.url.clone();

    let mut card = col()
        .key("dc-invite-card")
        .w(460.0)
        .padding(20.0)
        .gap(12.0)
        .radius(RADIUS_PANEL)
        .transition(modal_card_transition())
        .child(
            row()
                .w_full()
                .justify_between()
                .center_y()
                .child(
                    text(format!("Invite friends to {}", dlg.guild_name))
                        .font(type_section())
                        .color(FG),
                )
                .child(
                    el().key("dc-inv-close")
                        .size(26.0, 26.0)
                        .radius(13.0)
                        .hover(hover_fill())
                        .center()
                        .cursor(Cursor::Pointer)
                        .hit()
                        .press_on_down()
                        .on_press(|s: &mut AppState| s.discord.chat.invite_dialog = None)
                        .child(icon_close(13.0, fg_muted())),
                ),
        );

    if let Some(ref ch) = dlg.channel_name {
        card = card.child(
            text(format!("Recipients will land in #{ch}"))
                .font(type_label())
                .color(fg_muted()),
        );
    }

    card = card
        .child(
            row()
                .w_full()
                .h(34.0)
                .px(10.0)
                .gap(8.0)
                .center_y()
                .radius(RADIUS_SMALL)
                .fill(glass_control_fill())
                .border(1.0, glass_rim())
                .child(icon_search(13.0, fg_muted()))
                .child(
                    text_input(&dlg.query, type_small())
                        .key("dc-inv-search")
                        .label("Search for friends")
                        .grow(1.0)
                        .single_line()
                        .color(FG)
                        .placeholder("Search for friends", fg_muted())
                        .on_edit(edit_access(|s: &mut AppState| {
                            match s.discord.chat.invite_dialog.as_mut() {
                                Some(dlg) => &mut dlg.query,
                                None => &mut s.ui_discard_edit,
                            }
                        })),
                ),
        )
        .child(
            col()
                .w_full()
                .h(180.0)
                .scroll(ScrollSpec {
                    stick_bottom: false,
                })
                .child(flist),
        )
        .child(
            col()
                .w_full()
                .gap(6.0)
                .child(
                    text("Or, send a server invite link to a friend")
                        .font(type_label())
                        .color(fg_muted()),
                )
                .child(
                    row()
                        .w_full()
                        .gap(8.0)
                        .center_y()
                        .child(
                            row()
                                .grow(1.0)
                                .h(34.0)
                                .px(10.0)
                                .center_y()
                                .radius(RADIUS_SMALL)
                                .fill(subtle_fill())
                                .border(1.0, hairline())
                                .child(text(link_text).font(type_small()).color(FG).truncate()),
                        )
                        .child(
                            primary_button(
                                "dc-inv-copy",
                                if dlg.copied { "Copied" } else { "Copy" },
                                None,
                            )
                            .on_press_tx(
                                move |s: &mut AppState, tx| {
                                    if let Some(ref u) = copy_url {
                                        tx.copy(u.clone());
                                        if let Some(ref mut d) = s.discord.chat.invite_dialog {
                                            d.copied = true;
                                        }
                                        s.notice = Some("Copied invite link".into());
                                    }
                                },
                            ),
                        ),
                ),
        );

    if let Some(ref err) = dlg.error {
        card = card.child(text(err.clone()).font(type_small()).color(bad()));
    }

    card = card.child(
        row()
            .w_full()
            .justify_between()
            .center_y()
            .child(
                text(if dlg.max_age == 0 {
                    "Your invite link will never expire."
                } else {
                    "Your invite link expires after its set duration."
                })
                .font(type_label())
                .color(fg_faint()),
            )
            .child(
                el().key("dc-inv-edit-link")
                    .cursor(Cursor::Pointer)
                    .hit()
                    .press_on_down()
                    .on_press(|s: &mut AppState| {
                        if let Some(ref mut d) = s.discord.chat.invite_dialog {
                            d.editing = true;
                        }
                    })
                    .child(text("Edit invite link").font(type_label()).color(good())),
            ),
    );

    Some(modal_wrap("dc-invite-scrim", card, |s| {
        s.discord.chat.invite_dialog = None
    }))
}

struct ForwardTarget {
    id: String,
    name: String,
    place: Option<String>,
    avatar: Option<String>,
    is_dm: bool,
}

pub fn forward_dialog_overlay(s: &AppState, scale: f32) -> Option<El> {
    let dlg = s.discord.chat.forward_dialog.as_ref()?;
    let q = dlg.query.text.trim().to_lowercase();
    let mut targets: Vec<ForwardTarget> = Vec::new();
    for dm in &s.discord.home.dms {
        targets.push(ForwardTarget {
            id: dm.id.clone(),
            name: dm.name.clone(),
            place: None,
            avatar: dm.avatar.clone(),
            is_dm: true,
        });
    }
    for guild in &s.discord.home.guilds {
        if let Some(chans) = s.discord.channels_by_guild.get(&guild.id) {
            for ch in &chans.channels {
                if ch.kind == "text" && ch.can_send != Some(false) {
                    targets.push(ForwardTarget {
                        id: ch.id.clone(),
                        name: ch.name.clone(),
                        place: Some(guild.name.clone()),
                        avatar: guild.icon.clone(),
                        is_dm: false,
                    });
                }
            }
        }
    }
    let shown: Vec<_> = targets
        .into_iter()
        .filter(|t| {
            q.is_empty()
                || t.name.to_lowercase().contains(&q)
                || t.place
                    .as_deref()
                    .is_some_and(|p| p.to_lowercase().contains(&q))
        })
        .take(50)
        .collect();

    let mut list = col().w_full().gap(4.0);
    for t in shown {
        let on = dlg.picked.iter().any(|x| x == &t.id);
        let id_c = t.id.clone();
        let mut r = row()
            .key(format!("dc-fwd-t-{}", t.id))
            .w_full()
            .h(36.0)
            .px(8.0)
            .gap(8.0)
            .center_y()
            .radius(RADIUS_SMALL)
            .fill(if on {
                subtle_fill()
            } else {
                Color::TRANSPARENT
            })
            .hover(hover_fill())
            .cursor(Cursor::Pointer)
            .hit()
            .press_on_down()
            .on_press(move |s: &mut AppState| s.toggle_forward_target(&id_c));
        if t.is_dm {
            r = r.child(avatar_box(
                s,
                t.avatar.as_deref(),
                &t.name,
                24.0,
                12.0,
                None,
                false,
                scale,
            ));
        } else {
            r = r.child(icon_hash(14.0, fg_muted()));
        }
        r = r
            .child(
                text(t.name)
                    .font(type_small())
                    .color(FG)
                    .grow(1.0)
                    .truncate(),
            )
            .child(
                text(t.place.unwrap_or_default())
                    .font(type_label())
                    .color(fg_faint()),
            );
        if on {
            r = r.child(icon_check(14.0, good()));
        }
        list = list.child(r);
    }

    let mut card = col()
        .key("dc-forward-card")
        .w(440.0)
        .padding(18.0)
        .gap(12.0)
        .radius(RADIUS_PANEL)
        .transition(modal_card_transition())
        .child(
            row()
                .w_full()
                .justify_between()
                .center_y()
                .child(text("Forward to").font(type_section()).color(FG))
                .child(
                    el().key("dc-fwd-close")
                        .size(26.0, 26.0)
                        .radius(13.0)
                        .hover(hover_fill())
                        .center()
                        .cursor(Cursor::Pointer)
                        .hit()
                        .press_on_down()
                        .on_press(|s: &mut AppState| s.discord.chat.forward_dialog = None)
                        .child(icon_close(13.0, fg_muted())),
                ),
        )
        .child(
            row()
                .w_full()
                .h(34.0)
                .px(10.0)
                .gap(8.0)
                .center_y()
                .radius(RADIUS_SMALL)
                .fill(glass_control_fill())
                .border(1.0, glass_rim())
                .child(icon_search(13.0, fg_muted()))
                .child(
                    text_input(&dlg.query, type_small())
                        .key("dc-fwd-search")
                        .label("Search conversations")
                        .grow(1.0)
                        .single_line()
                        .color(FG)
                        .placeholder("Search", fg_muted())
                        .on_edit(edit_access(|s: &mut AppState| {
                            match s.discord.chat.forward_dialog.as_mut() {
                                Some(dlg) => &mut dlg.query,
                                None => &mut s.ui_discard_edit,
                            }
                        })),
                ),
        )
        .child(
            col()
                .w_full()
                .h(220.0)
                .scroll(ScrollSpec {
                    stick_bottom: false,
                })
                .child(list),
        )
        .child(
            row()
                .w_full()
                .h(34.0)
                .px(10.0)
                .center_y()
                .radius(RADIUS_SMALL)
                .fill(glass_control_fill())
                .border(1.0, glass_rim())
                .child(
                    text_input(&dlg.note, type_small())
                        .key("dc-fwd-note")
                        .label("Optional message")
                        .grow(1.0)
                        .single_line()
                        .color(FG)
                        .placeholder("Add an optional message", fg_faint())
                        .on_edit(edit_access(|s: &mut AppState| {
                            match s.discord.chat.forward_dialog.as_mut() {
                                Some(dlg) => &mut dlg.note,
                                None => &mut s.ui_discard_edit,
                            }
                        }))
                        .on_submit(|s: &mut AppState, _| s.submit_forward_dialog()),
                ),
        );

    if let Some(ref err) = dlg.error {
        card = card.child(text(err.clone()).font(type_small()).color(bad()));
    }

    let send_label: &'static str = if dlg.busy { "Sending…" } else { "Send" };

    card = card.child(
        row()
            .w_full()
            .justify_end()
            .gap(8.0)
            .child(
                glass_button_opt("dc-fwd-cancel", "Cancel", None, false)
                    .on_press(|s: &mut AppState| s.discord.chat.forward_dialog = None),
            )
            .child(
                primary_button("dc-fwd-submit", send_label, None)
                    .on_press(|s: &mut AppState| s.submit_forward_dialog()),
            ),
    );

    Some(modal_wrap("dc-forward-scrim", card, |s| {
        s.discord.chat.forward_dialog = None
    }))
}

pub fn new_thread_dialog_overlay(s: &AppState) -> Option<El> {
    let dlg = s.discord.chat.new_thread_dialog.as_ref()?;
    let mut card = col()
        .key("dc-new-thread-card")
        .w(400.0)
        .padding(18.0)
        .gap(12.0)
        .radius(RADIUS_PANEL)
        .transition(modal_card_transition())
        .child(text("Create thread").font(type_section()).color(FG))
        .child(
            row()
                .w_full()
                .h(36.0)
                .px(10.0)
                .center_y()
                .radius(RADIUS_SMALL)
                .fill(glass_control_fill())
                .border(1.0, glass_rim())
                .child(
                    text_input(&dlg.name, type_body())
                        .key("dc-new-thread-name")
                        .label("Thread name")
                        .grow(1.0)
                        .single_line()
                        .color(FG)
                        .placeholder("Thread name", fg_faint())
                        .on_edit(edit_access(|s: &mut AppState| {
                            match s.discord.chat.new_thread_dialog.as_mut() {
                                Some(dlg) => &mut dlg.name,
                                None => &mut s.ui_discard_edit,
                            }
                        }))
                        .on_submit(|s: &mut AppState, _| s.submit_new_thread_dialog()),
                ),
        );

    if let Some(ref err) = dlg.error {
        card = card.child(text(err.clone()).font(type_small()).color(bad()));
    }

    card = card.child(
        row()
            .w_full()
            .justify_end()
            .gap(8.0)
            .child(
                glass_button_opt("dc-nt-cancel", "Cancel", None, false)
                    .on_press(|s: &mut AppState| s.discord.chat.new_thread_dialog = None),
            )
            .child(
                primary_button(
                    "dc-nt-create",
                    if dlg.busy { "Creating…" } else { "Create" },
                    None,
                )
                .on_press(|s: &mut AppState| s.submit_new_thread_dialog()),
            ),
    );

    Some(modal_wrap("dc-new-thread-scrim", card, |s| {
        s.discord.chat.new_thread_dialog = None
    }))
}

pub fn report_dialog_overlay(s: &AppState) -> Option<El> {
    let dlg = s.discord.chat.report_dialog.as_ref()?;
    let mut card = col()
        .key("dc-report-card")
        .w(440.0)
        .padding(20.0)
        .gap(12.0)
        .radius(RADIUS_PANEL)
        .transition(modal_card_transition())
        .child(
            row()
                .w_full()
                .justify_between()
                .center_y()
                .child(text("Report message").font(type_section()).color(FG))
                .child(
                    el().key("dc-rep-close")
                        .size(26.0, 26.0)
                        .radius(13.0)
                        .hover(hover_fill())
                        .center()
                        .cursor(Cursor::Pointer)
                        .hit()
                        .press_on_down()
                        .on_press(|s: &mut AppState| s.discord.chat.report_dialog = None)
                        .child(icon_close(13.0, fg_muted())),
                ),
        )
        .child(
            col()
                .w_full()
                .padding(10.0)
                .gap(4.0)
                .radius(RADIUS_SMALL)
                .fill(subtle_fill())
                .child(text(dlg.author_name.clone()).font(type_small()).color(FG))
                .child(
                    text(dlg.preview.clone())
                        .font(type_label())
                        .color(fg_muted())
                        .w_full()
                        .wrap(),
                ),
        );

    if dlg.done {
        card = card
            .child(
                text("Report sent to Discord.")
                    .font(type_body())
                    .color(good()),
            )
            .child(
                row().w_full().justify_end().child(
                    primary_button("dc-rep-done", "Done", None)
                        .on_press(|s: &mut AppState| s.discord.chat.report_dialog = None),
                ),
            );
    } else {
        let mut reasons = col().w_full().gap(4.0);
        for &reason in REPORT_REASONS {
            let active = dlg.selected_reason == reason;
            let r_s = reason.to_string();
            reasons = reasons.child(
                row()
                    .key(format!("dc-rep-r-{reason}"))
                    .w_full()
                    .h(34.0)
                    .px(10.0)
                    .justify_between()
                    .center_y()
                    .radius(RADIUS_SMALL)
                    .fill(if active {
                        subtle_fill()
                    } else {
                        Color::TRANSPARENT
                    })
                    .border(1.0, if active { glass_rim() } else { hairline() })
                    .hover(hover_fill())
                    .cursor(Cursor::Pointer)
                    .hit()
                    .press_on_down()
                    .on_press(move |s: &mut AppState| {
                        if let Some(ref mut d) = s.discord.chat.report_dialog {
                            d.selected_reason = r_s.clone();
                        }
                    })
                    .child(text(reason).font(type_small()).color(FG))
                    .child(if active {
                        icon_check(13.0, good())
                    } else {
                        el()
                    }),
            );
        }
        card = card.child(reasons);
        if let Some(ref err) = dlg.error {
            card = card.child(text(err.clone()).font(type_small()).color(bad()));
        }
        card = card.child(
            row()
                .w_full()
                .justify_end()
                .gap(8.0)
                .child(
                    glass_button_opt("dc-rep-cancel", "Cancel", None, false)
                        .on_press(|s: &mut AppState| s.discord.chat.report_dialog = None),
                )
                .child(
                    danger_button(
                        "dc-rep-send",
                        if dlg.busy {
                            "Submitting…"
                        } else {
                            "Submit report"
                        },
                        None,
                    )
                    .on_press(|s: &mut AppState| s.submit_report_message()),
                ),
        );
    }

    Some(modal_wrap("dc-report-scrim", card, |s| {
        s.discord.chat.report_dialog = None
    }))
}

pub fn modify_attachment_overlay(s: &AppState) -> Option<El> {
    let draft = s.discord.chat.modifying_attachment.as_ref()?;
    let mut card = col()
        .key("dc-mod-att-card")
        .w(420.0)
        .padding(18.0)
        .gap(12.0)
        .radius(RADIUS_PANEL)
        .transition(modal_card_transition())
        .child(text("Modify attachment").font(type_section()).color(FG))
        .child(
            col()
                .w_full()
                .gap(4.0)
                .child(text("Filename").font(type_small()).color(fg_muted()))
                .child(
                    row()
                        .w_full()
                        .h(34.0)
                        .px(10.0)
                        .center_y()
                        .radius(RADIUS_SMALL)
                        .fill(glass_control_fill())
                        .border(1.0, glass_rim())
                        .child(
                            text_input(&draft.name, type_small())
                                .key("dc-mod-att-name")
                                .label("Filename")
                                .grow(1.0)
                                .single_line()
                                .color(FG)
                                .on_edit(edit_access(|s: &mut AppState| {
                                    match s.discord.chat.modifying_attachment.as_mut() {
                                        Some(draft) => &mut draft.name,
                                        None => &mut s.ui_discard_edit,
                                    }
                                })),
                        ),
                ),
        );

    if draft.is_media {
        card = card.child(
            col()
                .w_full()
                .gap(4.0)
                .child(
                    text("Description (alt text)")
                        .font(type_small())
                        .color(fg_muted()),
                )
                .child(
                    el().w_full()
                        .px(10.0)
                        .py(8.0)
                        .radius(RADIUS_SMALL)
                        .fill(glass_control_fill())
                        .border(1.0, glass_rim())
                        .child(rune::auto_grow(
                            text_input(&draft.description, type_small())
                                .key("dc-mod-att-desc")
                                .label("Description")
                                .w_full()
                                .color(FG)
                                .placeholder("Add a description", fg_faint())
                                .on_edit(edit_access(|s: &mut AppState| {
                                    match s.discord.chat.modifying_attachment.as_mut() {
                                        Some(draft) => &mut draft.description,
                                        None => &mut s.ui_discard_edit,
                                    }
                                })),
                            2,
                            4,
                        )),
                ),
        );
    }

    card = card
        .child(
            row()
                .w_full()
                .justify_between()
                .center_y()
                .child(text("Mark as spoiler").font(type_small()).color(FG))
                .child(
                    switch_pill("dc-mod-att-spoiler", "Mark as spoiler", draft.spoiler).on_press(
                        |s: &mut AppState| {
                            if let Some(ref mut d) = s.discord.chat.modifying_attachment {
                                d.spoiler = !d.spoiler;
                            }
                        },
                    ),
                ),
        )
        .child(
            row()
                .w_full()
                .justify_end()
                .gap(8.0)
                .child(
                    glass_button_opt("dc-mod-att-cancel", "Cancel", None, false)
                        .on_press(|s: &mut AppState| s.discord.chat.modifying_attachment = None),
                )
                .child(
                    primary_button("dc-mod-att-save", "Save", None)
                        .on_press(|s: &mut AppState| s.save_modify_attachment()),
                ),
        );

    Some(modal_wrap("dc-mod-att-scrim", card, |s| {
        s.discord.chat.modifying_attachment = None
    }))
}

pub fn profile_dialog_overlay(s: &AppState, scale: f32) -> Option<El> {
    let dlg = s.discord.chat.profile_dialog.as_ref()?;
    let me_id = s.discord.home.me.as_ref().map(|m| m.id.as_str());
    let is_me = me_id == Some(dlg.user_id.as_str());

    let display_name = dlg
        .profile
        .as_ref()
        .and_then(|p| {
            p.guild_member
                .as_ref()
                .and_then(|gm| gm.nick.clone())
                .or_else(|| p.user.global_name.clone())
                .filter(|n| !n.is_empty())
        })
        .unwrap_or_else(|| dlg.fallback_name.clone());
    let username = dlg
        .profile
        .as_ref()
        .map(|p| p.user.username.clone())
        .filter(|u| !u.is_empty())
        .unwrap_or_else(|| dlg.fallback_username.clone());
    let avatar_url = dlg
        .profile
        .as_ref()
        .and_then(|p| {
            p.user.avatar.as_deref().map(|hash| {
                format!(
                    "https://cdn.discordapp.com/avatars/{}/{hash}.webp?size=160&quality=lossless",
                    p.user.id
                )
            })
        })
        .or_else(|| dlg.fallback_avatar.clone());
    let pronouns = dlg.profile.as_ref().and_then(|p| {
        p.guild_member_profile
            .as_ref()
            .and_then(|g| g.pronouns.clone())
            .or_else(|| p.user_profile.as_ref().and_then(|u| u.pronouns.clone()))
            .filter(|s| !s.is_empty())
    });
    let bio = dlg.profile.as_ref().and_then(|p| {
        p.guild_member_profile
            .as_ref()
            .and_then(|g| g.bio.clone())
            .or_else(|| p.guild_member.as_ref().and_then(|g| g.bio.clone()))
            .or_else(|| p.user_profile.as_ref().and_then(|u| u.bio.clone()))
            .or_else(|| p.user.bio.clone())
            .filter(|s| !s.is_empty())
    });
    let banner_color = dlg
        .profile
        .as_ref()
        .and_then(|p| {
            p.user_profile
                .as_ref()
                .and_then(|u| u.accent_color)
                .or(p.user.accent_color)
        })
        .map(|c| hex(c, 1.0))
        .unwrap_or_else(subtle_fill);

    let status_dot = dlg
        .extras
        .as_ref()
        .and_then(|e| e.presence.as_ref())
        .map(|p| p.status.as_str());

    let width = if dlg.full { 560.0 } else { 360.0 };
    let banner_h = if dlg.full { 112.0 } else { 72.0 };
    let av_size = if dlg.full { 80.0 } else { 64.0 };

    let mut banner = el().w_full().h(banner_h).fill(banner_color);
    if let Some(ref prof) = dlg.profile
        && let Some(ref hash) = prof.user.banner
    {
        let burl = format!(
            "https://cdn.discordapp.com/banners/{}/{hash}.webp?size=600",
            prof.user.id
        );
        if let Some(img) = s.remote_image_sized(&burl, width, scale) {
            banner = banner.pixels_fit(img.pixels(), SurfaceFit::Fill);
        }
    }

    let uid_msg = dlg.user_id.clone();
    let uid_add = dlg.user_id.clone();
    let rel = dlg
        .extras
        .as_ref()
        .and_then(|e| e.relationship)
        .unwrap_or(0);
    let mut actions = row().gap(6.0).center_y();
    if !is_me {
        actions = actions.child(primary_button("dc-pf-msg-btn", "Message", None).on_press(
            move |s: &mut AppState| {
                s.discord.chat.profile_dialog = None;
                s.open_dm_with_friend(&uid_msg);
            },
        ));
        if rel == 0 {
            actions = actions.child(
                glass_button_opt(
                    "dc-pf-add-btn",
                    "Add friend",
                    Some(icon_user_plus(13.0, FG)),
                    false,
                )
                .on_press(move |s: &mut AppState| s.accept_discord_friend(&uid_add)),
            );
        } else if rel == 3 {
            actions = actions.child(
                glass_button_opt(
                    "dc-pf-acc-btn",
                    "Accept",
                    Some(icon_check(13.0, good())),
                    false,
                )
                .on_press(move |s: &mut AppState| s.accept_discord_friend(&uid_add)),
            );
        }
    }
    if !dlg.full {
        actions = actions.child(
            glass_button_opt("dc-pf-full-btn", "Full profile", None, false).on_press(
                |s: &mut AppState| {
                    let uid = s
                        .discord
                        .chat
                        .profile_dialog
                        .as_ref()
                        .map(|d| d.user_id.clone());
                    if let Some(ref mut d) = s.discord.chat.profile_dialog {
                        d.full = true;
                    }
                    if let Some(u) = uid {
                        s.fetch_mutual_friends(&u);
                    }
                },
            ),
        );
    }

    let mut body = col().w_full().px(16.0).pb(16.0).gap(12.0);
    body = body.child(
        row()
            .w_full()
            .justify_between()
            .center_y()
            .child(avatar_box(
                s,
                avatar_url.as_deref(),
                &display_name,
                av_size,
                av_size * 0.5,
                status_dot,
                false,
                scale,
            ))
            .child(actions),
    );

    let sub_line = match pronouns {
        Some(pr) => format!("{username} · {pr}"),
        None => username,
    };
    body = body.child(
        col()
            .w_full()
            .gap(2.0)
            .child(text(display_name).font(type_section()).color(FG))
            .child(text(sub_line).font(type_small()).color(fg_muted())),
    );

    if let Some(ref prof) = dlg.profile {
        let badges: Vec<_> = prof.badges.iter().chain(prof.guild_badges.iter()).collect();
        if !badges.is_empty() {
            let mut brow = row().w_full().gap(6.0).flex_wrap();
            for b in badges {
                let icon_url = format!("https://cdn.discordapp.com/badge-icons/{}.png", b.icon);
                let mut chip = row()
                    .px(6.0)
                    .h(24.0)
                    .gap(5.0)
                    .center_y()
                    .radius(RADIUS_PILL)
                    .fill(subtle_fill())
                    .border(1.0, hairline());
                if let Some(img) = s.remote_image_sized(&icon_url, 16.0, scale) {
                    chip = chip.child(
                        el().size(16.0, 16.0)
                            .pixels_fit(img.pixels(), SurfaceFit::Fit),
                    );
                }
                chip = chip.child(
                    text(b.description.clone())
                        .font(type_label())
                        .color(fg_muted()),
                );
                brow = brow.child(chip);
            }
            body = body.child(brow);
        }
    }

    if !is_me && let Some(ref prof) = dlg.profile {
        let mf = prof.mutual_friends_count.unwrap_or(0);
        let ms = prof.mutual_guilds.len();
        if dlg.full {
            body = body.child(
                row()
                    .w_full()
                    .gap(6.0)
                    .child(
                        choice_pill(
                            "dc-pft-about".into(),
                            "About me",
                            dlg.tab == ProfileTab::About,
                        )
                        .on_press(|s: &mut AppState| {
                            if let Some(ref mut d) = s.discord.chat.profile_dialog {
                                d.tab = ProfileTab::About;
                            }
                        }),
                    )
                    .child(
                        choice_pill(
                            "dc-pft-friends".into(),
                            &format!("Mutual friends ({mf})"),
                            dlg.tab == ProfileTab::Friends,
                        )
                        .on_press(|s: &mut AppState| {
                            let uid = s
                                .discord
                                .chat
                                .profile_dialog
                                .as_ref()
                                .map(|d| d.user_id.clone());
                            if let Some(ref mut d) = s.discord.chat.profile_dialog {
                                d.tab = ProfileTab::Friends;
                            }
                            if let Some(u) = uid {
                                s.fetch_mutual_friends(&u);
                            }
                        }),
                    )
                    .child(
                        choice_pill(
                            "dc-pft-servers".into(),
                            &format!("Mutual servers ({ms})"),
                            dlg.tab == ProfileTab::Servers,
                        )
                        .on_press(|s: &mut AppState| {
                            if let Some(ref mut d) = s.discord.chat.profile_dialog {
                                d.tab = ProfileTab::Servers;
                            }
                        }),
                    ),
            );
        } else if mf > 0 || ms > 0 {
            body = body.child(
                row().gap(8.0).child(
                    el().cursor(Cursor::Pointer)
                        .hit()
                        .press_on_down()
                        .on_press(|s: &mut AppState| {
                            let uid = s
                                .discord
                                .chat
                                .profile_dialog
                                .as_ref()
                                .map(|d| d.user_id.clone());
                            if let Some(ref mut d) = s.discord.chat.profile_dialog {
                                d.full = true;
                                d.tab = ProfileTab::Friends;
                            }
                            if let Some(u) = uid {
                                s.fetch_mutual_friends(&u);
                            }
                        })
                        .child(
                            text(format!(
                                "{mf} mutual friend{} · {ms} mutual server{}",
                                if mf == 1 { "" } else { "s" },
                                if ms == 1 { "" } else { "s" }
                            ))
                            .font(type_label())
                            .color(fg_muted()),
                        ),
                ),
            );
        }
    }

    match dlg.tab {
        ProfileTab::About => {
            if let Some(voice) = dlg.extras.as_ref().and_then(|e| e.voice.as_ref()) {
                let gid_v = voice.guild_id.clone();
                let cid_v = voice.channel_id.clone();
                body = body.child(
                    row()
                        .w_full()
                        .padding(10.0)
                        .justify_between()
                        .center_y()
                        .radius(RADIUS_SMALL)
                        .fill(subtle_fill())
                        .border(1.0, hairline())
                        .child(
                            col()
                                .child(text(voice.channel.clone()).font(type_small()).color(FG))
                                .child(
                                    text(voice.guild.clone())
                                        .font(type_label())
                                        .color(fg_faint()),
                                ),
                        )
                        .child(
                            glass_button_opt("dc-pf-join-vc", "Join voice", None, false).on_press(
                                move |s: &mut AppState| {
                                    s.discord.chat.profile_dialog = None;
                                    s.join_discord_voice(Some(&gid_v), &cid_v);
                                },
                            ),
                        ),
                );
            }
            if let Some(bio_s) = bio {
                body = body.child(
                    col()
                        .w_full()
                        .gap(4.0)
                        .child(text("About me").font(type_label()).color(fg_muted()))
                        .child(discord_md::render_markdown(&bio_s, &[], &dlg.user_id, true)),
                );
            }
            body = body.child(profile_roles_section(s));
            if let Some(ref prof) = dlg.profile
                && !prof.connected_accounts.is_empty()
            {
                let mut conns = row().w_full().gap(6.0).flex_wrap();
                for acc in &prof.connected_accounts {
                    conns = conns.child(
                        row()
                            .px(8.0)
                            .h(26.0)
                            .gap(6.0)
                            .center_y()
                            .radius(RADIUS_SMALL)
                            .fill(subtle_fill())
                            .border(1.0, hairline())
                            .child(text(acc.name.clone()).font(type_label()).color(FG))
                            .child(if acc.verified {
                                icon_check(11.0, good())
                            } else {
                                icon_external(11.0, fg_faint())
                            }),
                    );
                }
                body = body.child(
                    col()
                        .w_full()
                        .gap(4.0)
                        .child(text("Connections").font(type_label()).color(fg_muted()))
                        .child(conns),
                );
            }
            if !is_me {
                let dirty = dlg.note.text != dlg.saved_note;
                let mut note_sec = col()
                    .w_full()
                    .gap(4.0)
                    .child(text("Note").font(type_label()).color(fg_muted()))
                    .child(
                        row()
                            .w_full()
                            .gap(6.0)
                            .center_y()
                            .child(
                                row()
                                    .grow(1.0)
                                    .h(32.0)
                                    .px(10.0)
                                    .center_y()
                                    .radius(RADIUS_SMALL)
                                    .fill(glass_control_fill())
                                    .border(1.0, glass_rim())
                                    .child(
                                        text_input(&dlg.note, type_small())
                                            .key("dc-pf-note-in")
                                            .label("Note")
                                            .grow(1.0)
                                            .single_line()
                                            .color(FG)
                                            .placeholder("Click to add a note", fg_faint())
                                            .on_edit(edit_access(|s: &mut AppState| {
                                                match s.discord.chat.profile_dialog.as_mut() {
                                                    Some(dlg) => &mut dlg.note,
                                                    None => &mut s.ui_discard_edit,
                                                }
                                            }))
                                            .on_submit(|s: &mut AppState, _| s.save_profile_note()),
                                    ),
                            )
                            .child(if dirty {
                                primary_button("dc-pf-note-save", "Save", None)
                                    .on_press(|s: &mut AppState| s.save_profile_note())
                            } else {
                                el()
                            }),
                    );
                if let Some(ref ne) = dlg.note_error {
                    note_sec = note_sec.child(text(ne.clone()).font(type_label()).color(bad()));
                }
                body = body.child(note_sec);
            }
        }
        ProfileTab::Friends => {
            let mut flist = col().w_full().gap(6.0);
            match dlg.mutual_friends.as_ref() {
                None => {
                    flist = flist.child(
                        text("Loading mutual friends…")
                            .font(type_small())
                            .color(fg_muted()),
                    );
                }
                Some(list) if list.is_empty() => {
                    flist = flist.child(
                        text("No friends in common")
                            .font(type_small())
                            .color(fg_muted()),
                    );
                }
                Some(list) => {
                    for f in list {
                        let name = f
                            .global_name
                            .clone()
                            .filter(|g| !g.is_empty())
                            .unwrap_or_else(|| f.username.clone());
                        let av = f.avatar_url();
                        let fid = f.id.clone();
                        let fname = name.clone();
                        let funame = f.username.clone();
                        let fav = av.clone();
                        flist = flist.child(
                            row()
                                .key(format!("dc-pf-mf-{}", f.id))
                                .w_full()
                                .h(40.0)
                                .px(8.0)
                                .gap(10.0)
                                .center_y()
                                .radius(RADIUS_SMALL)
                                .hover(hover_fill())
                                .cursor(Cursor::Pointer)
                                .hit()
                                .press_on_down()
                                .on_press(move |s: &mut AppState| {
                                    s.open_user_profile_dialog(
                                        crate::discord_chat_state::ProfileOpenRequest {
                                            user_id: &fid,
                                            guild_id: None,
                                            fallback_name: &fname,
                                            fallback_username: &funame,
                                            fallback_avatar: fav.as_deref(),
                                            full: true,
                                            tab: ProfileTab::About,
                                        },
                                    );
                                })
                                .child(avatar_box(
                                    s,
                                    av.as_deref(),
                                    &name,
                                    28.0,
                                    14.0,
                                    None,
                                    false,
                                    scale,
                                ))
                                .child(text(name).font(type_small()).color(FG)),
                        );
                    }
                }
            }
            body = body.child(flist);
        }
        ProfileTab::Servers => {
            let mut slist = col().w_full().gap(6.0);
            let mutuals = dlg
                .profile
                .as_ref()
                .map(|p| p.mutual_guilds.as_slice())
                .unwrap_or(&[]);
            let mut count = 0;
            for mg in mutuals {
                if let Some(g) = s.discord.home.guilds.iter().find(|g| g.id == mg.id) {
                    count += 1;
                    let gid = g.id.clone();
                    slist = slist.child(
                        row()
                            .key(format!("dc-pf-ms-{}", g.id))
                            .w_full()
                            .h(40.0)
                            .px(8.0)
                            .gap(10.0)
                            .center_y()
                            .radius(RADIUS_SMALL)
                            .hover(hover_fill())
                            .cursor(Cursor::Pointer)
                            .hit()
                            .press_on_down()
                            .on_press(move |s: &mut AppState| {
                                s.discord.chat.profile_dialog = None;
                                s.open_discord_guild(&gid);
                            })
                            .child(avatar_box(
                                s,
                                g.icon.as_deref(),
                                &g.name,
                                28.0,
                                8.0,
                                None,
                                false,
                                scale,
                            ))
                            .child(
                                col()
                                    .child(text(g.name.clone()).font(type_small()).color(FG))
                                    .child(
                                        text(mg.nick.clone().unwrap_or_default())
                                            .font(type_label())
                                            .color(fg_faint()),
                                    ),
                            ),
                    );
                }
            }
            if count == 0 {
                slist = slist.child(
                    text("No servers in common")
                        .font(type_small())
                        .color(fg_muted()),
                );
            }
            body = body.child(slist);
        }
    }

    if let Some(ref err) = dlg.error {
        body = body.child(text(err.clone()).font(type_small()).color(bad()));
    } else if dlg.loading && dlg.profile.is_none() {
        body = body.child(
            text("Loading profile…")
                .font(type_small())
                .color(fg_muted()),
        );
    }

    let card = col()
        .key("dc-profile-card")
        .w(width)
        .h(if dlg.full { 580.0 } else { 460.0 })
        .radius(RADIUS_PANEL)
        .clip_rounded(RADIUS_PANEL)
        .transition(modal_card_transition())
        .child(banner)
        .child(
            col()
                .grow(1.0)
                .w_full()
                .min_h(0.0)
                .scroll(ScrollSpec {
                    stick_bottom: false,
                })
                .child(body),
        );

    Some(modal_wrap("dc-profile-scrim", card, |s| {
        s.discord.chat.profile_dialog = None
    }))
}

fn profile_roles_section(s: &AppState) -> El {
    let Some(dlg) = s.discord.chat.profile_dialog.as_ref() else {
        return el();
    };
    let editable =
        dlg.guild_id.is_some() && dlg.role_person.as_ref().is_some_and(|rp| rp.can.roles);

    let roles: Vec<(String, String, Option<String>, bool)> = if let Some(ref rp) = dlg.role_person {
        rp.choices
            .iter()
            .filter(|c| rp.roles.iter().any(|r| r == &c.id))
            .map(|c| {
                (
                    c.id.clone(),
                    c.name.clone(),
                    c.color.clone(),
                    editable && c.assignable,
                )
            })
            .collect()
    } else {
        dlg.extras
            .as_ref()
            .map(|e| {
                e.roles
                    .iter()
                    .map(|r| (r.id.clone(), r.name.clone(), r.color.clone(), false))
                    .collect()
            })
            .unwrap_or_default()
    };

    if roles.is_empty() && !editable {
        return el();
    }

    let mut chips = row().w_full().gap(5.0).flex_wrap().center_y();
    for (rid, name, color_hex, removable) in roles {
        let dot_color = color_hex
            .as_deref()
            .and_then(parse_hex_color)
            .unwrap_or_else(fg_muted);
        let mut chip = row()
            .key(format!("dc-pf-role-{rid}"))
            .h(24.0)
            .px(8.0)
            .gap(5.0)
            .center_y()
            .radius(RADIUS_PILL)
            .fill(subtle_fill())
            .border(1.0, hairline())
            .child(el().size(8.0, 8.0).radius(4.0).fill(dot_color))
            .child(text(name).font(type_label()).color(FG));
        if removable {
            let rid_rm = rid.clone();
            chip = chip.child(
                el().key(format!("dc-pf-role-rm-{rid}"))
                    .size(16.0, 16.0)
                    .radius(8.0)
                    .hover(bad().alpha(0.22))
                    .center()
                    .cursor(Cursor::Pointer)
                    .hit()
                    .press_on_down()
                    .on_press(move |s: &mut AppState| s.change_profile_role(&rid_rm, false))
                    .child(icon_close(10.0, fg_muted())),
            );
        }
        chips = chips.child(chip);
    }

    if editable {
        chips = chips.child(
            el().key("dc-pf-role-add")
                .size(24.0, 24.0)
                .radius(12.0)
                .fill(subtle_fill())
                .border(1.0, glass_rim())
                .hover(hover_fill())
                .center()
                .cursor(Cursor::Pointer)
                .hit()
                .press_on_down()
                .on_press(|s: &mut AppState| {
                    if let Some(ref mut d) = s.discord.chat.profile_dialog {
                        d.role_picker_open = !d.role_picker_open;
                        d.role_query = TextEdit::default();
                    }
                })
                .child(icon_plus(12.0, FG)),
        );
    }

    let mut sec = col()
        .w_full()
        .gap(6.0)
        .child(text("Roles").font(type_label()).color(fg_muted()))
        .child(chips);

    if dlg.role_picker_open
        && let Some(ref rp) = dlg.role_person
    {
        let q = dlg.role_query.text.trim().to_lowercase();
        let mut opts = col().w_full().gap(2.0);
        for choice in rp
            .choices
            .iter()
            .filter(|c| {
                c.assignable
                    && !rp.roles.iter().any(|r| r == &c.id)
                    && (q.is_empty() || c.name.to_lowercase().contains(&q))
            })
            .take(10)
        {
            let rid = choice.id.clone();
            let dot = choice
                .color
                .as_deref()
                .and_then(parse_hex_color)
                .unwrap_or_else(fg_muted);
            opts = opts.child(
                row()
                    .key(format!("dc-pf-ropt-{}", choice.id))
                    .w_full()
                    .h(28.0)
                    .px(8.0)
                    .gap(6.0)
                    .center_y()
                    .radius(6.0)
                    .hover(hover_fill())
                    .cursor(Cursor::Pointer)
                    .hit()
                    .press_on_down()
                    .on_press(move |s: &mut AppState| s.change_profile_role(&rid, true))
                    .child(el().size(8.0, 8.0).radius(4.0).fill(dot))
                    .child(text(choice.name.clone()).font(type_small()).color(FG)),
            );
        }
        sec = sec.child(
            col()
                .w_full()
                .padding(8.0)
                .gap(6.0)
                .radius(RADIUS_SMALL)
                .fill(subtle_fill())
                .border(1.0, glass_rim())
                .child(
                    text_input(&dlg.role_query, type_small())
                        .key("dc-pf-role-q")
                        .label("Find a role")
                        .w_full()
                        .single_line()
                        .color(FG)
                        .placeholder("Find a role", fg_faint())
                        .on_edit(edit_access(|s: &mut AppState| {
                            match s.discord.chat.profile_dialog.as_mut() {
                                Some(dlg) => &mut dlg.role_query,
                                None => &mut s.ui_discard_edit,
                            }
                        })),
                )
                .child(opts),
        );
    }

    if let Some(ref err) = dlg.role_error {
        sec = sec.child(text(err.clone()).font(type_label()).color(bad()));
    }

    sec
}

pub fn parse_hex_color(raw: &str) -> Option<Color> {
    let hex_str = raw.trim().trim_start_matches('#');
    let val = u32::from_str_radix(hex_str, 16).ok()?;
    Some(hex(val, 1.0))
}

pub fn _unused_imports_guard() {
    let _ = type_control;
}
