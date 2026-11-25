use crate::discord::avatar_box;
use crate::discord_chat_state::{
    ExpressionTab, GifTab, SlashCommandEntry, active_mention_token, flatten_slash_commands,
};
use crate::state::AppState;
use exo_kit::{
    BG, ExoGlass, FG, PRESS_SCALE, PRIMARY_FG, RADIUS_CARD, RADIUS_PANEL, RADIUS_PILL,
    RADIUS_SMALL, SPRING_BASE, SPRING_QUICK, apply_glass, bad, fg_faint, fg_muted, glass_button,
    glass_control_fill, glass_rim, glass_rim_bottom, glass_rim_top, good, hairline, hover_fill,
    icon_check, icon_close, icon_hash, icon_plus, icon_search, icon_star, icon_star_filled,
    icon_trash, modal_card_transition, pressed_fill, primary_button, scrim_fill, subtle_fill, switch_pill,
    type_body, type_control, type_label, type_section, type_small,
};
use exo_rpc::{DiscordMessage, DiscordReaction};
use rune::{
    Color, Cursor, El, ScrollSpec, SpringPair, SurfaceFit, TextEdit, Transition, col, edit_access,
    el, row, text, text_input,
};

fn picker_pop_transition() -> Transition {
    Transition::opacity().springs(SpringPair::new(SPRING_BASE, SPRING_BASE))
}

pub const UNICODE_EMOJI_GROUPS: &[(&str, &[(&str, &str)])] = &[
    (
        "Smileys & People",
        &[
            ("grinning", "😀"),
            ("joy", "😂"),
            ("rofl", "🤣"),
            ("smile", "😄"),
            ("wink", "😉"),
            ("blush", "😊"),
            ("heart_eyes", "😍"),
            ("sunglasses", "😎"),
            ("thinking", "🤔"),
            ("neutral_face", "😐"),
            ("rolling_eyes", "🙄"),
            ("sweat_smile", "😅"),
            ("sob", "😭"),
            ("scream", "😱"),
            ("rage", "😡"),
            ("skull", "💀"),
            ("clown", "🤡"),
            ("saluting_face", "🫡"),
            ("thumbsup", "👍"),
            ("thumbsdown", "👎"),
            ("clap", "👏"),
            ("pray", "🙏"),
            ("wave", "👋"),
            ("ok_hand", "👌"),
            ("point_up", "☝️"),
            ("eyes", "👀"),
            ("muscle", "💪"),
            ("brain", "🧠"),
        ],
    ),
    (
        "Nature & Animals",
        &[
            ("fire", "🔥"),
            ("sparkles", "✨"),
            ("star", "⭐"),
            ("zap", "⚡"),
            ("rainbow", "🌈"),
            ("sun", "☀️"),
            ("moon", "🌙"),
            ("snowflake", "❄️"),
            ("cat", "🐱"),
            ("dog", "🐶"),
            ("fox", "🦊"),
            ("owl", "🦉"),
            ("frog", "🐸"),
            ("penguin", "🐧"),
            ("butterfly", "🦋"),
            ("cherry_blossom", "🌸"),
        ],
    ),
    (
        "Food & Drink",
        &[
            ("pizza", "🍕"),
            ("hamburger", "🍔"),
            ("fries", "🍟"),
            ("taco", "🌮"),
            ("sushi", "🍣"),
            ("ramen", "🍜"),
            ("cake", "🎂"),
            ("cookie", "🍪"),
            ("coffee", "☕"),
            ("beer", "🍺"),
            ("champagne", "🍾"),
            ("boba", "🧋"),
        ],
    ),
    (
        "Activities & Objects",
        &[
            ("video_game", "🎮"),
            ("trophy", "🏆"),
            ("medal", "🏅"),
            ("soccer", "⚽"),
            ("basketball", "🏀"),
            ("tada", "🎉"),
            ("confetti_ball", "🎊"),
            ("rocket", "🚀"),
            ("headphones", "🎧"),
            ("microphone", "🎤"),
            ("musical_note", "🎵"),
            ("computer", "💻"),
            ("keyboard", "⌨️"),
            ("camera", "📷"),
            ("bulb", "💡"),
            ("lock", "🔒"),
        ],
    ),
    (
        "Symbols",
        &[
            ("heart", "❤️"),
            ("sparkling_heart", "💖"),
            ("broken_heart", "💔"),
            ("100", "💯"),
            ("white_check_mark", "✅"),
            ("x", "❌"),
            ("warning", "⚠️"),
            ("question", "❓"),
            ("exclamation", "❗"),
            ("bell", "🔔"),
            ("pin", "📌"),
            ("link", "🔗"),
        ],
    ),
];

pub fn unicode_char_by_name(name: &str) -> Option<&'static str> {
    for (_, list) in UNICODE_EMOJI_GROUPS {
        for &(n, ch) in *list {
            if n == name {
                return Some(ch);
            }
        }
    }
    None
}

pub fn emoji_cdn_url(id: &str, animated: bool, size: u32) -> String {
    let anim = if animated { "&animated=true" } else { "" };
    format!("https://cdn.discordapp.com/emojis/{id}.webp?size={size}&quality=lossless{anim}")
}

pub fn sticker_cdn_url(id: &str, format: i32, size: u32) -> String {
    let ext = if format == 4 { "gif" } else { "png" };
    format!("https://media.discordapp.net/stickers/{id}.{ext}?size={size}")
}

fn tab_pill(key: &str, label: &str, active: bool) -> El {
    el().key(key)
        .role("tab")
        .label(label)
        .cursor(Cursor::Pointer)
        .focusable(true)
        .hit()
        .press_on_down()
        .press_scale(PRESS_SCALE)
        .h(28.0)
        .px(12.0)
        .radius(RADIUS_PILL)
        .fill(if active { FG } else { Color::TRANSPARENT })
        .hover(if active { FG } else { hover_fill() })
        .center()
        .motion(SPRING_QUICK)
        .child(
            text(label)
                .font(type_small())
                .color(if active { PRIMARY_FG } else { fg_muted() }),
        )
}

pub fn expression_picker_popover(s: &AppState, scale: f32) -> Option<El> {
    let for_composer = s.discord.chat.picker.open_for_composer;
    let for_reaction = s.discord.chat.picker.open_for_reaction.clone();
    if !for_composer && for_reaction.is_none() {
        return None;
    }

    let tab = if for_reaction.is_some() {
        ExpressionTab::Emoji
    } else {
        s.discord.chat.picker.tab
    };

    let mut box_el = col()
        .key("dc-expr-picker")
        .pin(None, None, Some(12.0), Some(64.0))
        .w(420.0)
        .h(430.0)
        .padding(12.0)
        .gap(10.0)
        .radius(RADIUS_PANEL)
        .clip_rounded(RADIUS_PANEL)
        .fill(BG)
        .border_gradient(1.0, glass_rim_top(), glass_rim_bottom())
        .transition(picker_pop_transition());

    if for_composer {
        box_el = box_el.child(
            row()
                .w_full()
                .justify_between()
                .center_y()
                .child(
                    row()
                        .gap(6.0)
                        .child(
                            tab_pill("dc-exp-gif", "GIFs", tab == ExpressionTab::Gif).on_press(
                                |s: &mut AppState| s.switch_expression_tab(ExpressionTab::Gif),
                            ),
                        )
                        .child(
                            tab_pill("dc-exp-sticker", "Stickers", tab == ExpressionTab::Sticker)
                                .on_press(|s: &mut AppState| {
                                    s.switch_expression_tab(ExpressionTab::Sticker)
                                }),
                        )
                        .child(
                            tab_pill("dc-exp-emoji", "Emoji", tab == ExpressionTab::Emoji)
                                .on_press(|s: &mut AppState| {
                                    s.switch_expression_tab(ExpressionTab::Emoji)
                                }),
                        ),
                )
                .child(
                    el().key("dc-exp-close")
                        .size(26.0, 26.0)
                        .radius(13.0)
                        .hover(hover_fill())
                        .cursor(Cursor::Pointer)
                        .center()
                        .hit()
                        .press_on_down()
                        .on_press(|s: &mut AppState| {
                            s.discord.chat.picker.open_for_composer = false;
                            s.discord.chat.picker.open_for_reaction = None;
                            s.discord.chat.emoji_picker_for = None;
                        })
                        .child(icon_close(13.0, fg_muted())),
                ),
        );
    } else {
        box_el = box_el.child(
            row()
                .w_full()
                .justify_between()
                .center_y()
                .child(text("Add reaction").font(type_section()).color(FG))
                .child(
                    el().key("dc-exp-close")
                        .size(26.0, 26.0)
                        .radius(13.0)
                        .hover(hover_fill())
                        .cursor(Cursor::Pointer)
                        .center()
                        .hit()
                        .press_on_down()
                        .on_press(|s: &mut AppState| {
                            s.discord.chat.picker.open_for_reaction = None;
                            s.discord.chat.emoji_picker_for = None;
                        })
                        .child(icon_close(13.0, fg_muted())),
                ),
        );
    }

    let search_placeholder = match tab {
        ExpressionTab::Emoji => "Search emoji",
        ExpressionTab::Gif => "Search GIFs",
        ExpressionTab::Sticker => "Find stickers",
    };

    box_el = box_el.child(
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
                text_input(&s.discord.chat.picker.query, type_small())
                    .key("dc-exp-search")
                    .label(search_placeholder)
                    .grow(1.0)
                    .single_line()
                    .color(FG)
                    .placeholder(search_placeholder, fg_muted())
                    .on_edit(edit_access(|s: &mut AppState| {
                        &mut s.discord.chat.picker.query
                    }))
                    .on_submit(move |s: &mut AppState, _| {
                        if tab == ExpressionTab::Gif {
                            let q = s.discord.chat.picker.query.text.trim().to_string();
                            s.search_discord_gifs(if q.is_empty() { None } else { Some(&q) });
                        }
                    }),
            ),
    );

    match tab {
        ExpressionTab::Emoji => {
            box_el = box_el.child(emoji_tab_body(s, for_reaction.as_deref(), scale));
            if let Some(ref hovered) = s.discord.chat.picker.hovered_name {
                box_el = box_el.child(
                    row().w_full().h(24.0).px(4.0).center_y().child(
                        text(format!(":{hovered}:"))
                            .font(type_label())
                            .color(fg_muted()),
                    ),
                );
            }
        }
        ExpressionTab::Gif => {
            box_el = box_el.child(gif_tab_body(s, scale));
        }
        ExpressionTab::Sticker => {
            box_el = box_el.child(sticker_tab_body(s, scale));
        }
    }

    Some(box_el)
}

fn emoji_tab_body(s: &AppState, reacting_msg_id: Option<&str>, scale: f32) -> El {
    let q = s.discord.chat.picker.query.text.trim().to_lowercase();
    let active_cid = s.active_chat_channel_id().unwrap_or_default();
    let mut list = col().w_full().gap(10.0).pb(8.0);

    if q.is_empty() && !s.discord.chat.picker.emoji_favorites.is_empty() {
        let mut fav_grid = row().w_full().gap(4.0).flex_wrap();
        for key in &s.discord.chat.picker.emoji_favorites {
            if let Some(ch) = unicode_char_by_name(key) {
                fav_grid = fav_grid.child(unicode_emoji_tile(
                    key,
                    ch,
                    reacting_msg_id,
                    &active_cid,
                    true,
                ));
            }
        }
        list = list
            .child(text("Favorites").font(type_label()).color(fg_muted()))
            .child(fav_grid);
    }

    if let Some(ref data) = s.discord.chat.picker.emojis {
        for guild in &data.guilds {
            let matching: Vec<_> = guild
                .emojis
                .iter()
                .filter(|e| q.is_empty() || e.name.to_lowercase().contains(&q))
                .collect();
            if matching.is_empty() {
                continue;
            }
            let mut grid = row().w_full().gap(4.0).flex_wrap();
            for em in matching.into_iter().take(48) {
                let locked = !em.native;
                let url = emoji_cdn_url(&em.id, em.animated, 48);
                let em_key = em.id.clone();
                let em_name = em.name.clone();
                let em_text = format!(
                    "<{}:{}:{}>",
                    if em.animated { "a" } else { "" },
                    em.name,
                    em.id
                );
                let reaction_key = format!("{}:{}", em.name, em.id);
                let reacting = reacting_msg_id.map(str::to_string);
                let cid_c = active_cid.clone();
                let mut btn = el()
                    .key(format!("dc-cem-custom-{}", em.id))
                    .size(34.0, 34.0)
                    .radius(8.0)
                    .opacity(if locked { 0.35 } else { 1.0 })
                    .hover(if locked {
                        Color::TRANSPARENT
                    } else {
                        hover_fill()
                    })
                    .cursor(if locked {
                        Cursor::Default
                    } else {
                        Cursor::Pointer
                    })
                    .center()
                    .hit()
                    .press_on_down();
                if let Some(img) = s.remote_image_sized(&url, 26.0, scale) {
                    btn = btn.child(
                        el().size(26.0, 26.0)
                            .pixels_fit(img.pixels(), SurfaceFit::Fit),
                    );
                } else {
                    btn = btn.child(
                        text(format!(":{}", em.name.chars().take(3).collect::<String>()))
                            .font(type_label())
                            .color(fg_muted()),
                    );
                }
                if !locked {
                    let fav_k = em_key.clone();
                    btn = btn
                        .on_press(move |s: &mut AppState| {
                            s.note_emoji_use(&em_key, reacting.is_some());
                            if let Some(ref mid) = reacting {
                                s.toggle_discord_reaction(&cid_c, mid, &reaction_key, false);
                            } else {
                                s.discord.chat.composer.text.push_str(&em_text);
                                let end = s.discord.chat.composer.text.len();
                                s.discord.chat.composer.anchor = end;
                                s.discord.chat.composer.focus = end;
                                s.discord.chat.picker.open_for_composer = false;
                            }
                            s.discord.chat.picker.hovered_name = Some(em_name.clone());
                        })
                        .on_secondary_press(move |s: &mut AppState, _, _| {
                            s.toggle_emoji_favorite(&fav_k);
                        });
                }
                grid = grid.child(btn);
            }
            list = list
                .child(
                    text(guild.name.clone())
                        .font(type_label())
                        .color(fg_muted()),
                )
                .child(grid);
        }
    }

    for (group_name, emojis) in UNICODE_EMOJI_GROUPS {
        let matching: Vec<_> = emojis
            .iter()
            .filter(|(name, ch)| q.is_empty() || name.contains(&q) || ch.contains(&q))
            .collect();
        if matching.is_empty() {
            continue;
        }
        let mut grid = col().w_full().gap(4.0);
        for chunk in matching.chunks(10) {
            let mut r = row().w_full().gap(4.0);
            for &&(name, ch) in chunk {
                r = r.child(unicode_emoji_tile(
                    name,
                    ch,
                    reacting_msg_id,
                    &active_cid,
                    false,
                ));
            }
            grid = grid.child(r);
        }
        list = list
            .child(
                row()
                    .w_full()
                    .h(16.0)
                    .center_y()
                    .child(text(*group_name).font(type_label()).color(fg_muted())),
            )
            .child(grid);
    }

    col()
        .key("dc-exp-scroll")
        .grow(1.0)
        .w_full()
        .min_h(0.0)
        .scroll(ScrollSpec {
            stick_bottom: false,
        })
        .child(list)
}

fn unicode_emoji_tile(
    name: &str,
    ch: &str,
    reacting_msg_id: Option<&str>,
    active_cid: &str,
    is_fav_section: bool,
) -> El {
    let key_prefix = if is_fav_section { "fav" } else { "uni" };
    let name_s = name.to_string();
    let ch_s = ch.to_string();
    let fav_key = name.to_string();
    let reacting = reacting_msg_id.map(str::to_string);
    let cid_c = active_cid.to_string();
    el().key(format!("dc-uem-{key_prefix}-{name}"))
        .size(34.0, 34.0)
        .radius(8.0)
        .hover(hover_fill())
        .cursor(Cursor::Pointer)
        .center()
        .hit()
        .press_on_down()
        .on_press(move |s: &mut AppState| {
            s.note_emoji_use(&name_s, reacting.is_some());
            if let Some(ref mid) = reacting {
                s.toggle_discord_reaction(&cid_c, mid, &ch_s, false);
            } else {
                s.discord.chat.composer.text.push_str(&ch_s);
                let end = s.discord.chat.composer.text.len();
                s.discord.chat.composer.anchor = end;
                s.discord.chat.composer.focus = end;
                s.discord.chat.picker.open_for_composer = false;
            }
            s.discord.chat.picker.hovered_name = Some(name_s.clone());
        })
        .on_secondary_press(move |s: &mut AppState, _, _| {
            s.toggle_emoji_favorite(&fav_key);
        })
        .child(text(ch).font(type_body()).color(FG))
}

fn gif_tab_body(s: &AppState, scale: f32) -> El {
    let searching = !s.discord.chat.picker.query.text.trim().is_empty();
    let on_favorites = s.discord.chat.picker.gif_tab == GifTab::Favorites && !searching;
    let starred: std::collections::HashSet<&str> = s
        .discord
        .chat
        .picker
        .gif_favorites
        .iter()
        .map(|g| g.url.as_str())
        .collect();

    let mut root = col().grow(1.0).w_full().min_h(0.0).gap(8.0);
    root = root.child(
        row()
            .gap(6.0)
            .child(
                tab_pill(
                    "dc-gif-trending",
                    "Trending",
                    s.discord.chat.picker.gif_tab == GifTab::Trending,
                )
                .on_press(|s: &mut AppState| {
                    s.discord.chat.picker.gif_tab = GifTab::Trending;
                    s.discord.chat.picker.query = TextEdit::default();
                    s.search_discord_gifs(None);
                }),
            )
            .child(
                tab_pill(
                    "dc-gif-favs",
                    "Favorites",
                    s.discord.chat.picker.gif_tab == GifTab::Favorites,
                )
                .on_press(|s: &mut AppState| {
                    s.discord.chat.picker.gif_tab = GifTab::Favorites;
                    s.discord.chat.picker.query = TextEdit::default();
                    s.ensure_gif_favorites_loaded();
                }),
            ),
    );

    let items: Vec<(String, String, String, u32, u32)> = if on_favorites {
        s.discord
            .chat
            .picker
            .gif_favorites
            .iter()
            .map(|g| {
                (
                    g.url.clone(),
                    g.src.clone(),
                    "Favorite GIF".into(),
                    g.width,
                    g.height,
                )
            })
            .collect()
    } else {
        s.discord
            .chat
            .picker
            .gifs
            .iter()
            .map(|g| {
                let thumb = g
                    .preview
                    .clone()
                    .or_else(|| g.gif_src.clone())
                    .unwrap_or_else(|| g.src.clone());
                (
                    g.url.clone(),
                    thumb,
                    g.title.clone().unwrap_or_else(|| "GIF".into()),
                    g.width,
                    g.height,
                )
            })
            .collect()
    };

    let mut left_col = col().grow(1.0).min_w(0.0).gap(6.0);
    let mut right_col = col().grow(1.0).min_w(0.0).gap(6.0);
    for (idx, (url, thumb, title, w, h)) in items.iter().enumerate() {
        let is_star = starred.contains(url.as_str());
        let aspect = if *w > 0 && *h > 0 {
            ((*h as f32) / (*w as f32)).clamp(0.55, 1.5)
        } else {
            0.75
        };
        let card_h = (184.0 * aspect).round();
        let url_pick = url.clone();
        let url_star = url.clone();
        let src_star = thumb.clone();
        let gw = *w;
        let gh = *h;

        let mut card = el()
            .key(format!("dc-gif-{idx}"))
            .w_full()
            .h(card_h)
            .radius(RADIUS_SMALL)
            .clip_rounded(RADIUS_SMALL)
            .fill(subtle_fill())
            .border(1.0, hairline())
            .cursor(Cursor::Pointer)
            .hit()
            .press_on_down()
            .on_press(move |s: &mut AppState| s.send_direct_gif(&url_pick));

        if let Some(img) = s.remote_image_sized(thumb, 190.0, scale) {
            card = card.pixels_fit(img.pixels(), SurfaceFit::Fill);
        } else {
            card = card.center().child(
                text(title.clone())
                    .font(type_label())
                    .color(fg_muted())
                    .truncate(),
            );
        }

        card = card.child(
            el().key(format!("dc-gif-star-{idx}"))
                .pin(None, Some(6.0), Some(6.0), None)
                .size(26.0, 26.0)
                .radius(13.0)
                .fill(BG.alpha(0.75))
                .hover(hover_fill())
                .center()
                .cursor(Cursor::Pointer)
                .hit()
                .press_on_down()
                .on_press(move |s: &mut AppState| {
                    s.toggle_gif_favorite(&url_star, &src_star, gw, gh)
                })
                .child(if is_star {
                    icon_star_filled(13.0, good())
                } else {
                    icon_star(13.0, fg_muted())
                }),
        );

        if idx % 2 == 0 {
            left_col = left_col.child(card);
        } else {
            right_col = right_col.child(card);
        }
    }

    let body = if s.discord.chat.picker.gifs_loading && items.is_empty() {
        el().w_full()
            .py(24.0)
            .center()
            .child(text("Loading GIFs…").font(type_small()).color(fg_muted()))
    } else if items.is_empty() {
        let empty = if on_favorites {
            "Star a GIF to keep it here."
        } else if searching {
            "No GIFs found."
        } else {
            "Nothing trending right now."
        };
        el().w_full()
            .py(24.0)
            .center()
            .child(text(empty).font(type_small()).color(fg_muted()))
    } else {
        row().w_full().gap(6.0).child(left_col).child(right_col)
    };

    root.child(
        col()
            .grow(1.0)
            .w_full()
            .min_h(0.0)
            .scroll(ScrollSpec {
                stick_bottom: false,
            })
            .child(body),
    )
}

fn sticker_tab_body(s: &AppState, scale: f32) -> El {
    let q = s.discord.chat.picker.query.text.trim().to_lowercase();
    let mut list = col().w_full().gap(10.0).pb(8.0);
    let mut total_shown = 0usize;

    for group in &s.discord.chat.picker.stickers {
        let matching: Vec<_> = group
            .stickers
            .iter()
            .filter(|st| q.is_empty() || st.name.to_lowercase().contains(&q))
            .collect();
        if matching.is_empty() {
            continue;
        }
        total_shown += matching.len();
        let header = if group.usable {
            group.name.clone()
        } else {
            format!("{} · Nitro", group.name)
        };
        let mut grid = row().w_full().gap(6.0).flex_wrap();
        for st in matching {
            let locked = !group.usable;
            let st_id = st.id.clone();
            let fav_id = st.id.clone();
            let url = sticker_cdn_url(&st.id, st.format, 96);
            let mut tile = col()
                .key(format!("dc-st-{}", st.id))
                .w(88.0)
                .padding(6.0)
                .gap(4.0)
                .center()
                .radius(RADIUS_SMALL)
                .fill(subtle_fill())
                .opacity(if locked { 0.4 } else { 1.0 })
                .hover(if locked { subtle_fill() } else { hover_fill() })
                .cursor(if locked {
                    Cursor::Default
                } else {
                    Cursor::Pointer
                })
                .hit()
                .press_on_down();
            if let Some(img) = s.remote_image_sized(&url, 64.0, scale) {
                tile = tile.child(
                    el().size(64.0, 64.0)
                        .pixels_fit(img.pixels(), SurfaceFit::Fit),
                );
            } else {
                tile = tile.child(
                    el().size(64.0, 64.0)
                        .radius(8.0)
                        .fill(card_surface_fill())
                        .center()
                        .child(text("Sticker").font(type_label()).color(fg_faint())),
                );
            }
            tile = tile.child(
                text(st.name.clone())
                    .font(type_label())
                    .color(fg_muted())
                    .w_full()
                    .truncate(),
            );
            if !locked {
                tile = tile
                    .on_press(move |s: &mut AppState| s.send_discord_sticker(&st_id))
                    .on_secondary_press(move |s: &mut AppState, _, _| {
                        s.toggle_sticker_favorite(&fav_id)
                    });
            }
            grid = grid.child(tile);
        }
        list = list
            .child(text(header).font(type_label()).color(fg_muted()))
            .child(grid);
    }

    if s.discord.chat.picker.stickers_loading && total_shown == 0 {
        list = list.child(
            text("Loading stickers…")
                .font(type_small())
                .color(fg_muted()),
        );
    } else if total_shown == 0 {
        let msg = if q.is_empty() {
            "None of your servers has stickers."
        } else {
            "No stickers match."
        };
        list = list.child(text(msg).font(type_small()).color(fg_muted()));
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

fn card_surface_fill() -> Color {
    BG
}

pub fn mention_autocomplete_popover(s: &AppState, channel_id: &str, scale: f32) -> Option<El> {
    if s.discord.chat.apps.active_command.is_some() {
        return None;
    }
    let text_now = s.discord.chat.composer.text.as_str();
    let (_, sigil, raw_q) = active_mention_token(text_now)?;
    if s.discord.chat.autocomplete.dismissed_token.as_deref() == Some(text_now) {
        return None;
    }
    let q = raw_q.to_lowercase();
    let mut rows: Vec<El> = Vec::new();

    match sigil {
        '@' => {
            let mut seen = std::collections::HashSet::new();
            let mut users = Vec::new();
            if let Some(msgs) = s.discord.chat.messages_by_channel.get(channel_id) {
                for m in msgs.iter().rev() {
                    if seen.insert(m.author.id.clone()) {
                        users.push(m.author.clone());
                    }
                }
            }
            for u in &s.discord.chat.autocomplete.users {
                if seen.insert(u.id.clone()) {
                    users.push(u.clone());
                }
            }
            for u in users
                .into_iter()
                .filter(|u| {
                    q.is_empty()
                        || u.username.to_lowercase().contains(&q)
                        || u.global_name
                            .as_deref()
                            .is_some_and(|g| g.to_lowercase().contains(&q))
                })
                .take(7)
            {
                let display = u
                    .global_name
                    .clone()
                    .filter(|g| !g.is_empty())
                    .unwrap_or_else(|| u.username.clone());
                let insert = format!("@{display}");
                let raw = format!("<@{}>", u.id);
                let av = u.avatar_url();
                rows.push(
                    row()
                        .key(format!("dc-ac-u-{}", u.id))
                        .w_full()
                        .h(34.0)
                        .px(10.0)
                        .gap(8.0)
                        .center_y()
                        .radius(RADIUS_SMALL)
                        .hover(hover_fill())
                        .cursor(Cursor::Pointer)
                        .hit()
                        .press_on_down()
                        .on_press(move |s: &mut AppState| {
                            s.apply_autocomplete_selection(&insert, Some(&raw))
                        })
                        .child(avatar_box(
                            s,
                            av.as_deref(),
                            &display,
                            22.0,
                            11.0,
                            None,
                            false,
                            scale,
                        ))
                        .child(text(display).font(type_small()).color(FG))
                        .child(
                            text(u.username)
                                .font(type_label())
                                .color(fg_faint())
                                .grow(1.0)
                                .truncate(),
                        ),
                );
            }
            for role in s
                .discord
                .chat
                .autocomplete
                .roles
                .iter()
                .filter(|r| q.is_empty() || r.name.to_lowercase().contains(&q))
                .take(4)
            {
                let insert = format!("@{}", role.name);
                let raw = format!("<@&{}>", role.id);
                rows.push(
                    row()
                        .key(format!("dc-ac-r-{}", role.id))
                        .w_full()
                        .h(32.0)
                        .px(10.0)
                        .gap(8.0)
                        .center_y()
                        .radius(RADIUS_SMALL)
                        .hover(hover_fill())
                        .cursor(Cursor::Pointer)
                        .hit()
                        .press_on_down()
                        .on_press(move |s: &mut AppState| {
                            s.apply_autocomplete_selection(&insert, Some(&raw))
                        })
                        .child(text("@").font(type_small()).color(fg_muted()))
                        .child(text(role.name.clone()).font(type_small()).color(FG)),
                );
            }
            if s.discord.active_guild_id.is_some() {
                for (name, desc) in [
                    ("everyone", "Notify everyone who can view this channel"),
                    ("here", "Notify everyone online in this channel"),
                ] {
                    if name.starts_with(&q) {
                        let ins = format!("@{name}");
                        rows.push(
                            row()
                                .key(format!("dc-ac-g-{name}"))
                                .w_full()
                                .h(32.0)
                                .px(10.0)
                                .gap(8.0)
                                .center_y()
                                .radius(RADIUS_SMALL)
                                .hover(hover_fill())
                                .cursor(Cursor::Pointer)
                                .hit()
                                .press_on_down()
                                .on_press(move |s: &mut AppState| {
                                    s.apply_autocomplete_selection(&ins, None)
                                })
                                .child(text(format!("@{name}")).font(type_small()).color(FG))
                                .child(
                                    text(desc)
                                        .font(type_label())
                                        .color(fg_faint())
                                        .grow(1.0)
                                        .truncate(),
                                ),
                        );
                    }
                }
            }
        }
        '#' => {
            for ch in s
                .discord
                .chat
                .autocomplete
                .channels
                .iter()
                .filter(|c| q.is_empty() || c.name.to_lowercase().contains(&q))
                .take(8)
            {
                let insert = format!("#{}", ch.name);
                let raw = format!("<#{}>", ch.id);
                rows.push(
                    row()
                        .key(format!("dc-ac-c-{}", ch.id))
                        .w_full()
                        .h(32.0)
                        .px(10.0)
                        .gap(8.0)
                        .center_y()
                        .radius(RADIUS_SMALL)
                        .hover(hover_fill())
                        .cursor(Cursor::Pointer)
                        .hit()
                        .press_on_down()
                        .on_press(move |s: &mut AppState| {
                            s.apply_autocomplete_selection(&insert, Some(&raw))
                        })
                        .child(icon_hash(13.0, fg_muted()))
                        .child(text(ch.name.clone()).font(type_small()).color(FG))
                        .child(
                            text(ch.category.clone().unwrap_or_default())
                                .font(type_label())
                                .color(fg_faint())
                                .grow(1.0)
                                .truncate(),
                        ),
                );
            }
        }
        ':' => {
            if let Some(ref data) = s.discord.chat.picker.emojis {
                for g in &data.guilds {
                    for em in g
                        .emojis
                        .iter()
                        .filter(|e| e.native && e.name.to_lowercase().contains(&q))
                        .take(4)
                    {
                        let insert = format!(
                            "<{}:{}:{}>",
                            if em.animated { "a" } else { "" },
                            em.name,
                            em.id
                        );
                        rows.push(
                            row()
                                .key(format!("dc-ac-em-{}", em.id))
                                .w_full()
                                .h(32.0)
                                .px(10.0)
                                .gap(8.0)
                                .center_y()
                                .radius(RADIUS_SMALL)
                                .hover(hover_fill())
                                .cursor(Cursor::Pointer)
                                .hit()
                                .press_on_down()
                                .on_press(move |s: &mut AppState| {
                                    s.apply_autocomplete_selection(&insert, None)
                                })
                                .child(text(format!(":{}:", em.name)).font(type_small()).color(FG))
                                .child(text(g.name.clone()).font(type_label()).color(fg_faint())),
                        );
                    }
                }
            }
            for (_, group) in UNICODE_EMOJI_GROUPS {
                for &(name, ch) in *group {
                    if rows.len() >= 10 {
                        break;
                    }
                    if name.contains(&q) {
                        let ins = ch.to_string();
                        rows.push(
                            row()
                                .key(format!("dc-ac-u-{name}"))
                                .w_full()
                                .h(32.0)
                                .px(10.0)
                                .gap(8.0)
                                .center_y()
                                .radius(RADIUS_SMALL)
                                .hover(hover_fill())
                                .cursor(Cursor::Pointer)
                                .hit()
                                .press_on_down()
                                .on_press(move |s: &mut AppState| {
                                    s.apply_autocomplete_selection(&ins, None)
                                })
                                .child(text(ch).font(type_body()).color(FG))
                                .child(text(format!(":{name}:")).font(type_small()).color(FG)),
                        );
                    }
                }
            }
        }
        _ => {}
    }

    if rows.is_empty() {
        return None;
    }

    let mut panel = col()
        .key("dc-mention-ac")
        .w_full()
        .padding(6.0)
        .gap(2.0)
        .radius(RADIUS_CARD)
        .fill(BG)
        .border_gradient(1.0, glass_rim_top(), glass_rim_bottom())
        .transition(picker_pop_transition());
    for r in rows {
        panel = panel.child(r);
    }
    Some(panel)
}

pub fn slash_command_list_popover(s: &AppState, channel_id: &str) -> Option<El> {
    if s.discord.chat.apps.active_command.is_some() {
        return None;
    }
    let raw = s.discord.chat.composer.text.as_str();
    let rest = raw.strip_prefix('/')?;
    if rest.contains(' ') {
        return None;
    }
    let q = rest.to_lowercase();
    let loading = s.discord.chat.apps.loading_channel.as_deref() == Some(channel_id);
    let entries: Vec<SlashCommandEntry> = s
        .discord
        .chat
        .apps
        .commands_by_channel
        .get(channel_id)
        .map(|(cmds, apps)| flatten_slash_commands(cmds, apps))
        .unwrap_or_default()
        .into_iter()
        .filter(|e| {
            if q.is_empty() {
                return true;
            }
            let full = std::iter::once(e.command.name.as_str())
                .chain(e.path.iter().map(String::as_str))
                .collect::<Vec<_>>()
                .join(" ")
                .to_lowercase();
            full.contains(&q)
        })
        .take(12)
        .collect();

    let mut panel = col()
        .key("dc-slash-list")
        .w_full()
        .padding(6.0)
        .gap(2.0)
        .radius(RADIUS_CARD)
        .fill(BG)
        .border_gradient(1.0, glass_rim_top(), glass_rim_bottom())
        .transition(picker_pop_transition());

    if loading && entries.is_empty() {
        panel = panel.child(
            el().px(10.0).py(8.0).child(
                text("Loading commands…")
                    .font(type_small())
                    .color(fg_muted()),
            ),
        );
    } else if entries.is_empty() {
        panel = panel.child(
            el().px(10.0).py(8.0).child(
                text("No commands match")
                    .font(type_small())
                    .color(fg_muted()),
            ),
        );
    } else {
        for entry in entries {
            let full_name = std::iter::once(entry.command.name.as_str())
                .chain(entry.path.iter().map(String::as_str))
                .collect::<Vec<_>>()
                .join(" ");
            let app_name = entry
                .app
                .as_ref()
                .map(|a| a.name.clone())
                .unwrap_or_default();
            let desc = entry.description.clone();
            let key = format!("dc-cmd-{}-{}", entry.command.id, entry.path.join("_"));
            panel = panel.child(
                row()
                    .key(key)
                    .w_full()
                    .h(36.0)
                    .px(10.0)
                    .gap(8.0)
                    .center_y()
                    .radius(RADIUS_SMALL)
                    .hover(hover_fill())
                    .cursor(Cursor::Pointer)
                    .hit()
                    .press_on_down()
                    .on_press(move |s: &mut AppState| s.select_slash_command(entry.clone()))
                    .child(text(format!("/{full_name}")).font(type_control()).color(FG))
                    .child(
                        text(desc)
                            .font(type_small())
                            .color(fg_muted())
                            .grow(1.0)
                            .truncate(),
                    )
                    .child(text(app_name).font(type_label()).color(fg_faint())),
            );
        }
    }

    Some(panel)
}

pub fn slash_command_form(s: &AppState) -> Option<El> {
    let cmd = s.discord.chat.apps.active_command.as_ref()?;
    let full_name = std::iter::once(cmd.entry.command.name.as_str())
        .chain(cmd.entry.path.iter().map(String::as_str))
        .collect::<Vec<_>>()
        .join(" ");

    let mut form_row = row().w_full().gap(6.0).flex_wrap().center_y().child(
        el().px(8.0)
            .py(4.0)
            .radius(RADIUS_PILL)
            .fill(good().alpha(0.18))
            .border(1.0, good().alpha(0.4))
            .child(text(format!("/{full_name}")).font(type_small()).color(FG)),
    );

    for opt in &cmd.entry.options {
        if !cmd.used_options.contains(&opt.name) {
            continue;
        }
        let opt_name = opt.name.clone();
        if opt.kind == 5 {
            let cur = cmd.bool_values.get(&opt.name).copied();
            let on_true = opt_name.clone();
            let on_false = opt_name.clone();
            form_row = form_row.child(
                row()
                    .px(8.0)
                    .py(3.0)
                    .gap(4.0)
                    .center_y()
                    .radius(RADIUS_SMALL)
                    .fill(subtle_fill())
                    .border(1.0, hairline())
                    .child(
                        text(format!("{}:", opt.name))
                            .font(type_label())
                            .color(fg_muted()),
                    )
                    .child(
                        tab_pill(
                            &format!("dc-cmd-b-t-{}", opt.name),
                            "True",
                            cur == Some(true),
                        )
                        .on_press(move |s: &mut AppState| {
                            if let Some(ref mut c) = s.discord.chat.apps.active_command {
                                c.bool_values.insert(on_true.clone(), true);
                            }
                        }),
                    )
                    .child(
                        tab_pill(
                            &format!("dc-cmd-b-f-{}", opt.name),
                            "False",
                            cur == Some(false),
                        )
                        .on_press(move |s: &mut AppState| {
                            if let Some(ref mut c) = s.discord.chat.apps.active_command {
                                c.bool_values.insert(on_false.clone(), false);
                            }
                        }),
                    ),
            );
        } else {
            let empty = rune::TextEdit::default();
            let edit_ref = cmd.values.get(&opt.name).unwrap_or(&empty);
            let opt_k = opt.name.clone();
            let placeholder = opt
                .description
                .clone()
                .filter(|d| !d.is_empty())
                .unwrap_or_else(|| opt.name.clone());
            form_row = form_row.child(
                row()
                    .px(8.0)
                    .py(4.0)
                    .gap(6.0)
                    .center_y()
                    .radius(RADIUS_SMALL)
                    .fill(subtle_fill())
                    .border(1.0, hairline())
                    .child(
                        text(format!("{}:", opt.name))
                            .font(type_label())
                            .color(fg_muted()),
                    )
                    .child(
                        text_input(edit_ref, type_small())
                            .key(format!("dc-cmd-opt-{}", opt.name))
                            .label(opt.name.clone())
                            .w(140.0)
                            .single_line()
                            .color(FG)
                            .placeholder(placeholder, fg_faint())
                            .on_edit(edit_access(move |s: &mut AppState| {
                                match s.discord.chat.apps.active_command.as_mut() {
                                    Some(cmd) => cmd.values.entry(opt_k.clone()).or_default(),
                                    None => &mut s.ui_discard_edit,
                                }
                            }))
                            .on_submit(|s: &mut AppState, _| s.run_active_slash_command()),
                    ),
            );
        }
    }

    for opt in &cmd.entry.options {
        if cmd.used_options.contains(&opt.name) {
            continue;
        }
        let name_c = opt.name.clone();
        form_row = form_row.child(
            row()
                .key(format!("dc-cmd-add-{}", opt.name))
                .h(24.0)
                .px(8.0)
                .gap(4.0)
                .center_y()
                .radius(RADIUS_PILL)
                .fill(subtle_fill())
                .hover(hover_fill())
                .cursor(Cursor::Pointer)
                .hit()
                .press_on_down()
                .on_press(move |s: &mut AppState| {
                    if let Some(ref mut c) = s.discord.chat.apps.active_command {
                        c.used_options.push(name_c.clone());
                        c.focused_option = Some(name_c.clone());
                    }
                })
                .child(icon_plus(10.0, fg_muted()))
                .child(text(opt.name.clone()).font(type_label()).color(fg_muted())),
        );
    }

    let mut wrap = col().w_full().gap(6.0).child(
        row()
            .w_full()
            .gap(8.0)
            .center_y()
            .child(col().grow(1.0).min_w(0.0).child(form_row))
            .child(
                glass_button("dc-cmd-cancel", "Cancel", None).on_press(|s: &mut AppState| {
                    s.discord.chat.apps.active_command = None;
                }),
            )
            .child(
                primary_button(
                    "dc-cmd-run",
                    if cmd.busy { "Sending…" } else { "Run" },
                    None,
                )
                .on_press(|s: &mut AppState| s.run_active_slash_command()),
            ),
    );
    if let Some(ref err) = cmd.error {
        wrap = wrap.child(text(err.clone()).font(type_label()).color(bad()));
    }
    Some(wrap)
}

pub fn app_modal_overlay(s: &AppState, blur: bool) -> Option<El> {
    let active = s.discord.chat.apps.active_modal.as_ref()?;
    let mut fields_col = col().w_full().gap(10.0);

    for (idx, comp) in active.modal.components.iter().enumerate() {
        if comp.kind == 10 {
            if let Some(ref body) = comp.content {
                fields_col = fields_col.child(
                    text(body.clone())
                        .font(type_body())
                        .color(fg_muted())
                        .w_full()
                        .wrap(),
                );
            }
            continue;
        }
        let inners: Vec<(&exo_rpc::DiscordAppComponent, Option<&str>)> = if comp.kind == 1 {
            comp.components
                .iter()
                .map(|c| (c, c.label.as_deref()))
                .collect()
        } else if comp.kind == 18 {
            comp.component
                .as_deref()
                .into_iter()
                .map(|c| (c, comp.label.as_deref().or(c.label.as_deref())))
                .collect()
        } else {
            Vec::new()
        };
        for (inner_idx, (field, label)) in inners.into_iter().enumerate() {
            let cid = field
                .custom_id
                .clone()
                .unwrap_or_else(|| format!("field-{idx}-{inner_idx}"));
            let label_str = label.unwrap_or("Input").to_string();
            if field.kind == 23 {
                let checked = active.bool_values.get(&cid).copied().unwrap_or(false);
                let cid_t = cid.clone();
                fields_col = fields_col.child(
                    row()
                        .w_full()
                        .justify_between()
                        .center_y()
                        .child(text(label_str).font(type_small()).color(FG))
                        .child(switch_pill("dc-am-chk", "Toggle", checked).on_press(
                            move |s: &mut AppState| {
                                if let Some(ref mut m) = s.discord.chat.apps.active_modal {
                                    let cur = m.bool_values.get(&cid_t).copied().unwrap_or(false);
                                    m.bool_values.insert(cid_t.clone(), !cur);
                                }
                            },
                        )),
                );
            } else {
                let empty = rune::TextEdit::default();
                let edit_ref = active.text_values.get(&cid).unwrap_or(&empty);
                let cid_e = cid.clone();
                let placeholder = field.placeholder.clone().unwrap_or_default();
                let is_para = field.style == Some(2);
                let mut input = text_input(edit_ref, type_body())
                    .key(format!("dc-am-in-{cid}"))
                    .label(label_str.clone())
                    .w_full()
                    .color(FG)
                    .placeholder(placeholder, fg_faint())
                    .on_edit(edit_access(move |s: &mut AppState| {
                        match s.discord.chat.apps.active_modal.as_mut() {
                            Some(modal) => modal.text_values.entry(cid_e.clone()).or_default(),
                            None => &mut s.ui_discard_edit,
                        }
                    }));
                if !is_para {
                    input = input.single_line();
                } else {
                    input = rune::auto_grow(input, 3, 6);
                }
                fields_col = fields_col.child(
                    col()
                        .w_full()
                        .gap(4.0)
                        .child(text(label_str).font(type_small()).color(fg_muted()))
                        .child(
                            el().w_full()
                                .px(10.0)
                                .py(8.0)
                                .radius(RADIUS_SMALL)
                                .fill(glass_control_fill())
                                .border(1.0, glass_rim())
                                .child(input),
                        ),
                );
            }
        }
    }

    let mut card = col()
        .key("dc-app-modal-card")
        .w(460.0)
        .padding(20.0)
        .gap(14.0)
        .radius(RADIUS_PANEL)
        .fill(BG)
        .border_gradient(1.0, glass_rim_top(), glass_rim_bottom())
        .transition(modal_card_transition())
        .child(
            row()
                .w_full()
                .justify_between()
                .center_y()
                .child(
                    text(active.modal.title.clone())
                        .font(type_section())
                        .color(FG),
                )
                .child(
                    el().key("dc-am-close")
                        .size(28.0, 28.0)
                        .radius(14.0)
                        .hover(hover_fill())
                        .cursor(Cursor::Pointer)
                        .center()
                        .hit()
                        .press_on_down()
                        .on_press(|s: &mut AppState| s.discord.chat.apps.active_modal = None)
                        .child(icon_close(14.0, fg_muted())),
                ),
        )
        .child(fields_col);

    if let Some(ref err) = active.error {
        card = card.child(text(err.clone()).font(type_small()).color(bad()));
    }

    card = card.child(
        row()
            .w_full()
            .justify_end()
            .gap(8.0)
            .child(
                glass_button("dc-am-cancel", "Cancel", None)
                    .on_press(|s: &mut AppState| s.discord.chat.apps.active_modal = None),
            )
            .child(
                primary_button(
                    "dc-am-submit",
                    if active.busy {
                        "Submitting…"
                    } else {
                        "Submit"
                    },
                    None,
                )
                .on_press(|s: &mut AppState| s.submit_active_app_modal()),
            ),
    );

    let _ = blur;
    let card = apply_glass(card, ExoGlass::Sheet, false)
        .modal(true, |s: &mut AppState| {
            s.discord.chat.apps.active_modal = None
        })
        .modal_scrim(scrim_fill());
    let host = el()
        .key("dc-app-modal-scrim")
        .pin(Some(0.0), Some(0.0), Some(0.0), Some(0.0))
        .center()
        .child(card);
    Some(host)
}

pub fn reactions_dialog_overlay(s: &AppState, blur: bool, scale: f32) -> Option<El> {
    let dlg = s.discord.chat.reactors_dialog.as_ref()?;
    let msg: Option<&DiscordMessage> = s
        .discord
        .chat
        .messages_by_channel
        .get(&dlg.channel_id)
        .and_then(|list| list.iter().find(|m| m.id == dlg.message_id));

    let mut chips: Vec<(&DiscordReaction, bool, i32)> = Vec::new();
    if let Some(m) = msg {
        for r in &m.reactions {
            let (normal, burst) = r
                .count_details
                .as_ref()
                .map(|d| (d.normal, d.burst))
                .unwrap_or((r.count, 0));
            if burst > 0 {
                chips.push((r, true, burst));
            }
            if normal > 0 {
                chips.push((r, false, normal));
            }
        }
    }

    let mut left_tabs = col().w(110.0).gap(4.0);
    for (r, burst, count) in chips {
        let key = r.emoji.key();
        let active = dlg.selected_key == key && dlg.selected_burst == burst;
        let k_click = key.clone();
        left_tabs = left_tabs.child(
            row()
                .key(format!("dc-rtab-{key}-{burst}"))
                .w_full()
                .h(34.0)
                .px(10.0)
                .gap(6.0)
                .center_y()
                .radius(RADIUS_SMALL)
                .fill(if active {
                    subtle_fill()
                } else {
                    Color::TRANSPARENT
                })
                .border(
                    1.0,
                    if active {
                        glass_rim()
                    } else {
                        Color::TRANSPARENT
                    },
                )
                .hover(hover_fill())
                .cursor(Cursor::Pointer)
                .hit()
                .press_on_down()
                .on_press(move |s: &mut AppState| s.select_reactors_tab(&k_click, burst))
                .child(text(r.emoji.display()).font(type_small()).color(FG))
                .child(text(count.to_string()).font(type_label()).color(fg_muted())),
        );
    }

    let mut users_list = col().w_full().gap(6.0);
    if let Some(ref err) = dlg.error {
        users_list = users_list.child(text(err.clone()).font(type_small()).color(bad()));
    }
    if dlg.loading && dlg.users.is_empty() {
        users_list = users_list.child(
            text("Loading reactions…")
                .font(type_small())
                .color(fg_muted()),
        );
    } else if dlg.users.is_empty() {
        users_list = users_list.child(
            text("No reactions found.")
                .font(type_small())
                .color(fg_muted()),
        );
    } else {
        for user in &dlg.users {
            let name = user
                .global_name
                .clone()
                .filter(|g| !g.is_empty())
                .unwrap_or_else(|| user.username.clone());
            let av = user.avatar_url();
            let uid = user.id.clone();
            let mut urow = row()
                .key(format!("dc-reactor-{}", user.id))
                .w_full()
                .h(40.0)
                .px(8.0)
                .gap(10.0)
                .center_y()
                .radius(RADIUS_SMALL)
                .hover(hover_fill())
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
                .child(
                    col()
                        .grow(1.0)
                        .min_w(0.0)
                        .child(text(name).font(type_small()).color(FG).truncate())
                        .child(
                            text(user.username.clone())
                                .font(type_label())
                                .color(fg_faint())
                                .truncate(),
                        ),
                );
            if dlg.can_remove {
                urow = urow.child(
                    el().key(format!("dc-reactor-rm-{}", user.id))
                        .size(26.0, 26.0)
                        .radius(13.0)
                        .hover(bad().alpha(0.22))
                        .cursor(Cursor::Pointer)
                        .center()
                        .hit()
                        .press_on_down()
                        .on_press(move |s: &mut AppState| s.remove_user_reaction(&uid))
                        .child(icon_trash(13.0, bad())),
                );
            }
            users_list = users_list.child(urow);
        }
        if dlg.has_more
            && let Some(last_id) = dlg.users.last().map(|u| u.id.clone())
        {
            users_list = users_list.child(
                glass_button("dc-reactors-more", "Load more", None)
                    .on_press(move |s: &mut AppState| s.load_reactors_page(Some(&last_id))),
            );
        }
    }

    let card = col()
        .key("dc-reactors-card")
        .w(480.0)
        .h(380.0)
        .padding(16.0)
        .gap(12.0)
        .radius(RADIUS_PANEL)
        .fill(BG)
        .border_gradient(1.0, glass_rim_top(), glass_rim_bottom())
        .transition(modal_card_transition())
        .child(
            row()
                .w_full()
                .justify_between()
                .center_y()
                .child(text("Reactions").font(type_section()).color(FG))
                .child(
                    el().key("dc-reactors-close")
                        .size(26.0, 26.0)
                        .radius(13.0)
                        .hover(hover_fill())
                        .cursor(Cursor::Pointer)
                        .center()
                        .hit()
                        .press_on_down()
                        .on_press(|s: &mut AppState| s.discord.chat.reactors_dialog = None)
                        .child(icon_close(13.0, fg_muted())),
                ),
        )
        .child(
            row()
                .grow(1.0)
                .w_full()
                .min_h(0.0)
                .gap(12.0)
                .child(
                    col()
                        .h_full()
                        .scroll(ScrollSpec {
                            stick_bottom: false,
                        })
                        .child(left_tabs),
                )
                .child(el().w(1.0).h_full().fill(hairline()))
                .child(
                    col()
                        .grow(1.0)
                        .h_full()
                        .min_w(0.0)
                        .min_h(0.0)
                        .scroll(ScrollSpec {
                            stick_bottom: false,
                        })
                        .child(users_list),
                ),
        );

    let _ = blur;
    let card = apply_glass(card, ExoGlass::Sheet, false)
        .modal(true, |s: &mut AppState| {
            s.discord.chat.reactors_dialog = None
        })
        .modal_scrim(scrim_fill());
    let host = el()
        .key("dc-reactors-scrim")
        .pin(Some(0.0), Some(0.0), Some(0.0), Some(0.0))
        .center()
        .child(card);
    let _ = (icon_check, pressed_fill);
    Some(host)
}
