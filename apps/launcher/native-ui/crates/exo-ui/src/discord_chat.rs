use crate::discord::avatar_box;
use crate::discord_chat_state::{
    ChatDrawer, ComponentPress, ExpressionTab, FileTextEntry, GiftCardEntry, ImageMenuState,
    InviteCardEntry, LightboxSlide, PendingAttachment, ProfileOpenRequest, ProfileTab,
    extract_gift_codes, extract_invite_codes, extract_spotify_jam_url, is_text_preview_filename,
};
use crate::discord_md;
use crate::discord_pickers;
use crate::discord_search;
use crate::discord_threads;
use crate::state::{AppState, ContextMenuTarget};
use exo_kit::{
    BG, FG, PRESS_SCALE, PRIMARY_FG, RADIUS_CARD, RADIUS_PANEL, RADIUS_PILL, RADIUS_SMALL,
    SPRING_BASE, SPRING_FLIGHT, SPRING_QUICK, bad, fg_faint, fg_muted, glass_button, glass_rim,
    glass_rim_bottom, glass_rim_top, good, hairline, hex, hover_fill, icon_arrow_up, icon_bell_off,
    icon_bookmark, icon_bookmark_filled, icon_check, icon_close, icon_copy, icon_download,
    icon_edit, icon_external, icon_hash, icon_more_horiz, icon_paperclip, icon_phone, icon_pin, icon_plus, icon_reply,
    icon_search, icon_smile, icon_sparkles, icon_trash, popover_transition_from, pressed_fill,
    primary_button, scrim_fill, subtle_fill, type_body, type_control, type_label, type_page,
    type_section, type_small,
};
use exo_rpc::{DiscordAppComponent, DiscordAttachment, DiscordEmbed, DiscordMessage};
use rune::{
    Color, Cursor, El, GrowthSpec, LightboxItem, ScrollSpec, Spring, SpringPair, SurfaceFit,
    Transition, Tx, auto_grow, col, edit_access, el, lightbox, row, text, text_input,
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
        tx.motion(SPRING_FLIGHT);
        tx.launch("dc-composer-input", format!("dc-msg-body-{local_id}"));
    }
}

fn pane_surface() -> Color {
    BG
}

fn card_surface() -> Color {
    BG
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
        .press(pressed_fill())
        .center()
        .motion(SPRING_QUICK)
        .child(icon)
}

fn channel_name_map(s: &AppState) -> std::collections::HashMap<String, String> {
    let mut map = std::collections::HashMap::new();
    for chans in s.discord.channels_by_guild.values() {
        for ch in &chans.channels {
            map.insert(ch.id.clone(), ch.name.clone());
        }
    }
    map
}

fn embed_card(
    s: &AppState,
    embed: &DiscordEmbed,
    channels: &std::collections::HashMap<String, String>,
    msg_id: &str,
    scale: f32,
) -> El {
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
        let mut title_el = text(title.clone())
            .font(type_section())
            .color(if embed.url.is_some() { good() } else { FG })
            .w_full()
            .wrap();
        if let Some(ref url) = embed.url {
            let u = url.clone();
            title_el = el()
                .w_full()
                .cursor(Cursor::Pointer)
                .hit()
                .press_on_down()
                .on_press(move |s: &mut AppState| s.open_external_url(&u))
                .child(title_el);
        }
        inner = inner.child(title_el);
    }
    if let Some(ref desc) = embed.description
        && !desc.is_empty()
    {
        inner = inner.child(discord_md::render_markdown_with_channels(
            desc,
            &[],
            channels,
            msg_id,
            true,
        ));
    }
    if !embed.fields.is_empty() {
        let mut frow = row().w_full().gap(12.0).flex_wrap();
        for f in &embed.fields {
            let mut fc = col()
                .gap(2.0)
                .child(text(f.name.clone()).font(type_label()).color(fg_muted()))
                .child(discord_md::render_markdown_with_channels(
                    &f.value,
                    &[],
                    channels,
                    msg_id,
                    true,
                ));
            if f.inline {
                fc = fc.min_w(130.0).grow(1.0);
            } else {
                fc = fc.w_full();
            }
            frow = frow.child(fc);
        }
        inner = inner.child(frow);
    }
    let is_gifv = embed.kind.as_deref() == Some("gifv");
    if is_gifv {
        let video_gif = embed
            .video
            .as_ref()
            .and_then(|v| v.url.as_deref().or(v.proxy_url.as_deref()))
            .map(exo_rpc::remote_art::normalize_gif_image_url)
            .filter(|u| {
                let lower = u.split('?').next().unwrap_or(u).to_ascii_lowercase();
                !lower.ends_with(".mp4") && !lower.ends_with(".webm")
            });
        let img_gif = embed
            .image
            .as_ref()
            .and_then(|m| m.proxy_url.as_deref().or(m.url.as_deref()))
            .map(exo_rpc::remote_art::normalize_gif_image_url);
        let thumb_gif = embed
            .thumbnail
            .as_ref()
            .and_then(|m| m.proxy_url.as_deref().or(m.url.as_deref()))
            .map(exo_rpc::remote_art::normalize_gif_image_url);
        let gif_url = video_gif.or(img_gif).or(thumb_gif);
        let (gw, gh) = embed
            .video
            .as_ref()
            .or(embed.thumbnail.as_ref())
            .or(embed.image.as_ref())
            .and_then(|m| match (m.width, m.height) {
                (Some(w), Some(h)) if w > 0 && h > 0 => {
                    let aspect = (h as f32 / w as f32).clamp(0.42, 1.45);
                    let dw = (w as f32).clamp(180.0, 340.0);
                    Some((dw, (dw * aspect).clamp(110.0, 280.0)))
                }
                _ => None,
            })
            .unwrap_or((300.0, 200.0));
        if let Some(url) = gif_url {
            let orig_c = embed
                .url
                .clone()
                .or_else(|| embed.thumbnail.as_ref().and_then(|m| m.url.clone()))
                .unwrap_or_else(|| url.clone());
            let lb_src = url.clone();
            let lb_orig = orig_c.clone();
            let lb_cap = embed.title.clone().unwrap_or_else(|| "GIF".into());
            let mut tile = el()
                .w(gw)
                .h(gh)
                .radius(RADIUS_SMALL)
                .continuous_corners()
                .clip_rounded(RADIUS_SMALL)
                .fill(Color::WHITE.alpha(0.03))
                .border(1.0, Color::WHITE.alpha(0.06))
                .cursor(Cursor::Pointer)
                .hit()
                .on_press(move |s: &mut AppState| {
                    if let Some(cid) = s.active_chat_channel_id() {
                        s.open_channel_image_lightbox(&cid, &lb_src, &lb_orig, &lb_cap, &lb_cap);
                    } else {
                        s.open_discord_lightbox(
                            vec![LightboxSlide {
                                src: lb_src.clone(),
                                original_url: lb_orig.clone(),
                                caption: lb_cap.clone(),
                                alt: lb_cap.clone(),
                            }],
                            0,
                        );
                    }
                })
                .on_secondary_press(move |s: &mut AppState, p, _| {
                    s.discord.chat.links.image_menu = Some(ImageMenuState {
                        original_url: orig_c.clone(),
                        x: p.x,
                        y: p.y,
                    });
                });
            if let Some(img) = s.remote_image_sized(&url, gw.max(320.0), scale) {
                tile = tile.pixels_fit(img.pixels(), SurfaceFit::Fill);
            } else if let Some(thumb) = embed
                .thumbnail
                .as_ref()
                .and_then(|m| m.proxy_url.as_deref().or(m.url.as_deref()))
                .and_then(|u| s.remote_image_sized(u, gw.max(320.0), scale))
            {
                tile = tile.pixels_fit(thumb.pixels(), SurfaceFit::Fill);
            }
            return tile;
        }
    }

    if let Some(url) = embed
        .image
        .as_ref()
        .and_then(|m| m.proxy_url.as_deref().or(m.url.as_deref()))
        .map(exo_rpc::remote_art::normalize_gif_image_url)
        && let Some(img) = s.remote_image_sized(&url, 360.0, scale)
    {
        let orig_c = embed
            .image
            .as_ref()
            .and_then(|m| m.url.clone())
            .unwrap_or_else(|| url.clone());
        let lb_src = url.clone();
        let lb_orig = orig_c.clone();
        let lb_cap = embed.title.clone().unwrap_or_default();
        inner = inner.child(
            el().w(360.0)
                .h(202.0)
                .radius(RADIUS_SMALL)
                .clip_rounded(RADIUS_SMALL)
                .cursor(Cursor::Pointer)
                .hit()
                .on_press(move |s: &mut AppState| {
                    if let Some(cid) = s.active_chat_channel_id() {
                        s.open_channel_image_lightbox(&cid, &lb_src, &lb_orig, &lb_cap, &lb_cap);
                    } else {
                        s.open_discord_lightbox(
                            vec![LightboxSlide {
                                src: lb_src.clone(),
                                original_url: lb_orig.clone(),
                                caption: lb_cap.clone(),
                                alt: lb_cap.clone(),
                            }],
                            0,
                        );
                    }
                })
                .on_secondary_press(move |s: &mut AppState, p, _| {
                    s.discord.chat.links.image_menu = Some(ImageMenuState {
                        original_url: orig_c.clone(),
                        x: p.x,
                        y: p.y,
                    });
                })
                .pixels_fit(img.pixels(), SurfaceFit::Fit),
        );
    } else if let Some(url) = embed
        .thumbnail
        .as_ref()
        .and_then(|m| m.proxy_url.as_deref().or(m.url.as_deref()))
        .map(exo_rpc::remote_art::normalize_gif_image_url)
        && let Some(img) = s.remote_image_sized(&url, 240.0, scale)
    {
        let orig_c = embed
            .thumbnail
            .as_ref()
            .and_then(|m| m.url.clone())
            .unwrap_or_else(|| url.clone());
        let lb_src = url.clone();
        let lb_orig = orig_c.clone();
        let lb_cap = embed.title.clone().unwrap_or_default();
        let has_copy = embed.title.is_some() || embed.description.is_some();
        let (tw, th) = if has_copy {
            (112.0, 112.0)
        } else {
            (300.0, 190.0)
        };
        inner = inner.child(
            el().w(tw)
                .h(th)
                .radius(RADIUS_SMALL)
                .clip_rounded(RADIUS_SMALL)
                .cursor(Cursor::Pointer)
                .hit()
                .on_press(move |s: &mut AppState| {
                    if let Some(cid) = s.active_chat_channel_id() {
                        s.open_channel_image_lightbox(&cid, &lb_src, &lb_orig, &lb_cap, &lb_cap);
                    } else {
                        s.open_discord_lightbox(
                            vec![LightboxSlide {
                                src: lb_src.clone(),
                                original_url: lb_orig.clone(),
                                caption: lb_cap.clone(),
                                alt: lb_cap.clone(),
                            }],
                            0,
                        );
                    }
                })
                .on_secondary_press(move |s: &mut AppState, p, _| {
                    s.discord.chat.links.image_menu = Some(ImageMenuState {
                        original_url: orig_c.clone(),
                        x: p.x,
                        y: p.y,
                    });
                })
                .pixels_fit(img.pixels(), SurfaceFit::Fit),
        );
    }
    if let Some(ref f) = embed.footer
        && let Some(footer_txt) = f.text.as_deref().filter(|t| !t.is_empty())
    {
        let mut frow = row().gap(6.0).center_y();
        if let Some(icon_url) = f.proxy_icon_url.as_deref().or(f.icon_url.as_deref())
            && let Some(img) = s.remote_image_sized(icon_url, 24.0, scale)
        {
            frow = frow.child(
                el().size(16.0, 16.0)
                    .radius(4.0)
                    .clip_rounded(4.0)
                    .pixels_fit(img.pixels(), SurfaceFit::Fit),
            );
        }
        frow = frow.child(
            text(footer_txt.to_string())
                .font(type_label())
                .color(fg_faint()),
        );
        inner = inner.child(frow);
    }

    row()
        .w(480.0)
        .max_w(540.0)
        .px(12.0)
        .py(8.0)
        .gap(10.0)
        .radius(RADIUS_SMALL)
        .fill(Color::WHITE.alpha(0.025))
        .child(el().w(3.0).radius(2.0).fill(accent))
        .child(inner)
}

fn render_attachment(s: &AppState, att: &DiscordAttachment, scale: f32) -> El {
    let is_spoiler = (att.filename.starts_with("SPOILER_") || (att.flags & 8) != 0)
        && !s.discord.chat.links.revealed_media.contains(&att.id);

    if att.is_image() {
        let url = att.best_image_url().to_string();
        let orig_url = att.url.clone();
        let att_id = att.id.clone();
        let alt_open = s.discord.chat.links.alt_shown.contains(&att.id);
        let mut box_w = 340.0f32;
        let mut box_h = 220.0f32;
        if let (Some(w), Some(h)) = (att.width, att.height)
            && w > 0
            && h > 0
        {
            let ratio = (w as f32) / (h as f32);
            box_w = (w as f32).min(420.0);
            box_h = (box_w / ratio).clamp(80.0, 320.0);
        }

        if is_spoiler {
            let reveal_id = att.id.clone();
            return el()
                .key(format!("dc-att-sp-{}", att.id))
                .w(box_w)
                .h(box_h)
                .radius(RADIUS_CARD)
                .fill(subtle_fill())
                .border(1.0, glass_rim())
                .center()
                .cursor(Cursor::Pointer)
                .hit()
                .press_on_down()
                .on_press(move |s: &mut AppState| {
                    s.discord
                        .chat
                        .links
                        .revealed_media
                        .insert(reveal_id.clone());
                })
                .child(
                    el().px(14.0)
                        .py(6.0)
                        .radius(RADIUS_PILL)
                        .fill(BG)
                        .border(1.0, glass_rim())
                        .child(text("Spoiler · Click to show").font(type_small()).color(FG)),
                );
        }

        let orig_menu = orig_url.clone();
        let lb_src = url.clone();
        let lb_orig = orig_url.clone();
        let lb_cap = att.filename.clone();
        let lb_alt = att.description.clone().unwrap_or_default();
        let mut frame = el()
            .key(format!("dc-att-img-{}", att.id))
            .w(box_w)
            .h(box_h)
            .radius(RADIUS_CARD)
            .clip_rounded(RADIUS_CARD)
            .fill(subtle_fill())
            .border(1.0, hairline())
            .cursor(Cursor::Pointer)
            .hit()
            .on_press(move |s: &mut AppState| {
                if let Some(cid) = s.active_chat_channel_id() {
                    s.open_channel_image_lightbox(&cid, &lb_src, &lb_orig, &lb_cap, &lb_alt);
                } else {
                    s.open_discord_lightbox(
                        vec![LightboxSlide {
                            src: lb_src.clone(),
                            original_url: lb_orig.clone(),
                            caption: lb_cap.clone(),
                            alt: lb_alt.clone(),
                        }],
                        0,
                    );
                }
            })
            .on_secondary_press(move |s: &mut AppState, p, _| {
                s.discord.chat.links.image_menu = Some(ImageMenuState {
                    original_url: orig_menu.clone(),
                    x: p.x,
                    y: p.y,
                });
            });
        if let Some(img) = s.remote_image_sized(&url, box_w, scale) {
            frame = frame.pixels_fit(img.pixels(), SurfaceFit::Fit);
        } else {
            frame = frame.center().child(
                text(att.filename.clone())
                    .font(type_small())
                    .color(fg_muted()),
            );
        }
        if let Some(ref desc) = att.description
            && !desc.is_empty()
        {
            let aid_alt = att_id.clone();
            frame = frame.child(
                el().key(format!("dc-att-alt-{}", att.id))
                    .pin(Some(8.0), None, None, Some(8.0))
                    .px(6.0)
                    .py(2.0)
                    .radius(4.0)
                    .fill(BG.alpha(0.82))
                    .border(1.0, glass_rim())
                    .cursor(Cursor::Pointer)
                    .hit()
                    .press_on_down()
                    .on_press(move |s: &mut AppState| {
                        if !s.discord.chat.links.alt_shown.remove(&aid_alt) {
                            s.discord.chat.links.alt_shown.insert(aid_alt.clone());
                        }
                    })
                    .child(text("ALT").font(type_label()).color(FG)),
            );
            if alt_open {
                return col().gap(4.0).child(frame).child(
                    el().w(box_w)
                        .px(10.0)
                        .py(6.0)
                        .radius(RADIUS_SMALL)
                        .fill(subtle_fill())
                        .border(1.0, hairline())
                        .child(
                            text(desc.clone())
                                .font(type_label())
                                .color(fg_muted())
                                .w_full()
                                .wrap(),
                        ),
                );
            }
        }
        return frame;
    }

    if is_text_preview_filename(&att.filename, att.content_type.as_deref()) {
        let mut card = col()
            .key(format!("dc-att-txt-{}", att.id))
            .w(460.0)
            .max_w(520.0)
            .radius(RADIUS_CARD)
            .fill(card_surface())
            .border(1.0, hairline());
        match s.discord.chat.links.file_texts.get(&att.url) {
            Some(FileTextEntry::Loaded {
                text: body_txt,
                left,
                expanded,
            }) => {
                let lines: Vec<&str> = body_txt.lines().collect();
                let long = lines.len() > 6 || *left > 0;
                let preview = if *expanded {
                    if *left > 0 {
                        format!("{body_txt}\n… {} more", format_bytes(*left))
                    } else {
                        body_txt.clone()
                    }
                } else {
                    lines.iter().take(6).copied().collect::<Vec<_>>().join("\n")
                };
                card = card.child(
                    el().w_full()
                        .px(12.0)
                        .py(8.0)
                        .fill(hex(0x0c0c10, 1.0))
                        .child(
                            text(preview)
                                .font(type_label())
                                .color(hex(0xe4e4eb, 1.0))
                                .w_full()
                                .wrap(),
                        ),
                );
                if long {
                    let u = att.url.clone();
                    card = card.child(
                        el().key(format!("dc-att-exp-{}", att.id))
                            .w_full()
                            .px(12.0)
                            .py(6.0)
                            .hover(hover_fill())
                            .cursor(Cursor::Pointer)
                            .hit()
                            .press_on_down()
                            .on_press(move |s: &mut AppState| s.toggle_file_text_expand(&u))
                            .child(
                                text(if *expanded {
                                    format!("Collapse ({} lines)", lines.len())
                                } else {
                                    format!(
                                        "Expand ({}{} lines)",
                                        lines.len(),
                                        if *left > 0 { "+" } else { "" }
                                    )
                                })
                                .font(type_label())
                                .color(good()),
                            ),
                    );
                }
            }
            Some(FileTextEntry::Loading) => {
                card = card.child(
                    el().px(12.0).py(8.0).child(
                        text("Loading preview…")
                            .font(type_label())
                            .color(fg_faint()),
                    ),
                );
            }
            _ => {}
        }
        let open_u = att.url.clone();
        return card.child(
            row()
                .w_full()
                .px(12.0)
                .py(8.0)
                .gap(8.0)
                .center_y()
                .child(icon_paperclip(14.0, fg_muted()))
                .child(
                    col()
                        .grow(1.0)
                        .min_w(0.0)
                        .child(
                            text(att.filename.clone())
                                .font(type_small())
                                .color(FG)
                                .truncate(),
                        )
                        .child(
                            text(format_bytes(att.size))
                                .font(type_label())
                                .color(fg_faint()),
                        ),
                )
                .child(
                    tiny_action(
                        format!("dc-att-dl-{}", att.id),
                        "Open file",
                        false,
                        icon_download(14.0, fg_muted()),
                    )
                    .on_press(move |s: &mut AppState| s.open_external_url(&open_u)),
                ),
        );
    }

    let open_u = att.url.clone();
    row()
        .key(format!("dc-att-file-{}", att.id))
        .px(12.0)
        .py(8.0)
        .gap(8.0)
        .center_y()
        .radius(RADIUS_SMALL)
        .fill(card_surface())
        .border(1.0, hairline())
        .hover(hover_fill())
        .cursor(Cursor::Pointer)
        .hit()
        .press_on_down()
        .on_press(move |s: &mut AppState| s.open_external_url(&open_u))
        .child(icon_paperclip(14.0, fg_muted()))
        .child(text(att.filename.clone()).font(type_small()).color(FG))
        .child(
            text(format_bytes(att.size))
                .font(type_label())
                .color(fg_faint()),
        )
        .child(icon_external(12.0, fg_faint()))
}

fn render_message_components(s: &AppState, msg: &DiscordMessage) -> El {
    let mut wrap = col().w_full().gap(6.0).pt(4.0);
    for (i, comp) in msg.components.iter().enumerate() {
        wrap = wrap.child(render_component_node(s, msg, comp, i));
    }
    if let Some(ref err) = s.discord.chat.apps.component_error {
        wrap = wrap.child(text(err.clone()).font(type_label()).color(bad()));
    }
    wrap
}

fn render_component_node(
    s: &AppState,
    msg: &DiscordMessage,
    comp: &DiscordAppComponent,
    idx: usize,
) -> El {
    match comp.kind {
        1 => {
            let mut r = row().w_full().gap(6.0).flex_wrap().center_y();
            for (ci, child) in comp.components.iter().enumerate() {
                r = r.child(render_component_node(s, msg, child, idx * 31 + ci));
            }
            r
        }
        2 => {
            let style = comp.style.unwrap_or(2);
            let is_link = style == 5;
            let label = comp.label.clone().unwrap_or_default();
            let emoji = comp
                .emoji
                .as_ref()
                .and_then(|e| e.name.as_deref())
                .map(|n| format!("{n} "))
                .unwrap_or_default();
            let busy = comp
                .custom_id
                .as_deref()
                .is_some_and(|id| s.discord.chat.apps.component_busy.as_deref() == Some(id));
            let fill = match style {
                1 => FG,
                3 => good().alpha(0.22),
                4 => bad().alpha(0.22),
                _ => subtle_fill(),
            };
            let fg_c = match style {
                1 => PRIMARY_FG,
                3 => good(),
                4 => bad(),
                _ => FG,
            };
            let mut btn = row()
                .key(format!("dc-comp-btn-{}-{idx}", msg.id))
                .h(30.0)
                .px(12.0)
                .gap(5.0)
                .center_y()
                .radius(RADIUS_SMALL)
                .fill(fill)
                .border(1.0, glass_rim())
                .opacity(if comp.disabled { 0.45 } else { 1.0 })
                .cursor(if comp.disabled {
                    Cursor::Default
                } else {
                    Cursor::Pointer
                })
                .hit()
                .press_on_down()
                .child(
                    text(if busy {
                        "Working…".to_string()
                    } else {
                        format!("{emoji}{label}")
                    })
                    .font(type_small())
                    .color(fg_c),
                );
            if is_link {
                btn = btn.child(icon_external(12.0, fg_c));
                if let Some(ref url) = comp.url {
                    let u = url.clone();
                    btn = btn.on_press(move |s: &mut AppState| s.open_external_url(&u));
                }
            } else if !comp.disabled
                && let Some(ref custom_id) = comp.custom_id
            {
                let cid = msg.channel_id.clone();
                let mid = msg.id.clone();
                let app_id = msg
                    .application_id
                    .clone()
                    .unwrap_or_else(|| msg.author.id.clone());
                let flags = msg.flags;
                let ctype = comp.kind;
                let cust = custom_id.clone();
                btn = btn.on_press(move |s: &mut AppState| {
                    s.press_message_component(ComponentPress {
                        channel_id: &cid,
                        message_id: &mid,
                        application_id: &app_id,
                        message_flags: flags,
                        component_type: ctype,
                        custom_id: &cust,
                        values: None,
                    });
                });
            }
            btn
        }
        3 | 5 | 6 | 7 | 8 => {
            let select_id = format!("{}:{idx}", msg.id);
            let is_open = s.discord.chat.apps.open_select_id.as_deref() == Some(select_id.as_str());
            let placeholder = comp
                .placeholder
                .clone()
                .unwrap_or_else(|| "Select an option".into());
            let sid_toggle = select_id.clone();
            let mut box_col = col().w(300.0).gap(4.0).child(
                row()
                    .key(format!("dc-comp-sel-{select_id}"))
                    .w_full()
                    .h(34.0)
                    .px(10.0)
                    .justify_between()
                    .center_y()
                    .radius(RADIUS_SMALL)
                    .fill(subtle_fill())
                    .border(1.0, glass_rim())
                    .hover(hover_fill())
                    .cursor(Cursor::Pointer)
                    .hit()
                    .press_on_down()
                    .on_press(move |s: &mut AppState| {
                        if s.discord.chat.apps.open_select_id.as_deref()
                            == Some(sid_toggle.as_str())
                        {
                            s.discord.chat.apps.open_select_id = None;
                        } else {
                            s.discord.chat.apps.open_select_id = Some(sid_toggle.clone());
                        }
                    })
                    .child(text(placeholder).font(type_small()).color(fg_muted()))
                    .child(
                        text(if is_open { "▲" } else { "▼" })
                            .font(type_label())
                            .color(fg_faint()),
                    ),
            );
            if is_open && !comp.options.is_empty() {
                let mut opts = col()
                    .w_full()
                    .padding(4.0)
                    .gap(2.0)
                    .radius(RADIUS_SMALL)
                    .fill(BG)
                    .border(1.0, glass_rim());
                for (oi, opt) in comp.options.iter().enumerate() {
                    let cid = msg.channel_id.clone();
                    let mid = msg.id.clone();
                    let app_id = msg
                        .application_id
                        .clone()
                        .unwrap_or_else(|| msg.author.id.clone());
                    let flags = msg.flags;
                    let ctype = comp.kind;
                    let cust = comp.custom_id.clone().unwrap_or_default();
                    let val = opt.value.clone();
                    opts = opts.child(
                        col()
                            .key(format!("dc-comp-sopt-{select_id}-{oi}"))
                            .w_full()
                            .px(8.0)
                            .py(6.0)
                            .radius(6.0)
                            .hover(hover_fill())
                            .cursor(Cursor::Pointer)
                            .hit()
                            .press_on_down()
                            .on_press(move |s: &mut AppState| {
                                s.press_message_component(ComponentPress {
                                    channel_id: &cid,
                                    message_id: &mid,
                                    application_id: &app_id,
                                    message_flags: flags,
                                    component_type: ctype,
                                    custom_id: &cust,
                                    values: Some(vec![val.clone()]),
                                });
                            })
                            .child(text(opt.label.clone()).font(type_small()).color(FG))
                            .child(if let Some(ref d) = opt.description {
                                text(d.clone()).font(type_label()).color(fg_faint())
                            } else {
                                el()
                            }),
                    );
                }
                box_col = box_col.child(opts);
            }
            box_col
        }
        9 => {
            let mut inner = col().grow(1.0).min_w(0.0).gap(4.0);
            for (ci, child) in comp.components.iter().enumerate() {
                inner = inner.child(render_component_node(s, msg, child, idx * 31 + ci));
            }
            let mut sec = row().w_full().gap(10.0).center_y().child(inner);
            if let Some(ref acc) = comp.accessory {
                sec = sec.child(render_component_node(s, msg, acc, idx * 31 + 99));
            }
            sec
        }
        10 => discord_md::render_markdown(
            comp.content.as_deref().unwrap_or(""),
            &msg.mentions,
            &msg.id,
            true,
        ),
        14 => el().w_full().h(1.0).fill(hairline()),
        17 => {
            let accent = comp
                .accent_color
                .filter(|&c| c != 0)
                .map(|c| hex(c, 1.0))
                .unwrap_or_else(glass_rim);
            let mut inner = col().grow(1.0).min_w(0.0).gap(6.0);
            for (ci, child) in comp.components.iter().enumerate() {
                inner = inner.child(render_component_node(s, msg, child, idx * 31 + ci));
            }
            row()
                .w(460.0)
                .max_w(520.0)
                .padding(12.0)
                .gap(10.0)
                .radius(RADIUS_CARD)
                .fill(BG)
                .border(1.0, hairline())
                .child(el().w(3.0).radius(2.0).fill(accent))
                .child(inner)
        }
        _ => el(),
    }
}

fn render_link_cards(s: &AppState, msg: &DiscordMessage, is_mine: bool, scale: f32) -> Vec<El> {
    let mut out = Vec::new();
    if let Some(jam_url) = extract_spotify_jam_url(&msg.content)
        && s.discord
            .chat
            .links
            .jams
            .get(&jam_url)
            .copied()
            .unwrap_or(false)
    {
        let u = jam_url.clone();
        out.push(
            col()
                .w(380.0)
                .padding(12.0)
                .gap(8.0)
                .radius(RADIUS_CARD)
                .fill(card_surface())
                .border(1.0, glass_rim())
                .child(
                    text("Invite to a Spotify Jam")
                        .font(type_label())
                        .color(fg_muted()),
                )
                .child(
                    row()
                        .w_full()
                        .justify_between()
                        .center_y()
                        .child(
                            col()
                                .child(text("Spotify Jam").font(type_section()).color(FG))
                                .child(
                                    text("Listen together").font(type_small()).color(fg_muted()),
                                ),
                        )
                        .child(
                            primary_button("dc-jam", "Join", None)
                                .key(format!("dc-jam-{}", msg.id))
                                .on_press(move |s: &mut AppState| s.open_external_url(&u)),
                        ),
                ),
        );
    }

    for code in extract_invite_codes(&msg.content) {
        match s.discord.chat.links.invites.get(&code) {
            Some(InviteCardEntry::Loaded(inv)) => {
                let title = inv.name.clone().unwrap_or_else(|| "Discord server".into());
                let joined = inv.joined;
                let code_c = code.clone();
                let gid_c = inv.guild_id.clone();
                let cid_c = inv.channel_id.clone();
                let subtitle = match (inv.online, inv.members) {
                    (Some(o), Some(m)) => format!("{o} Online · {m} Members"),
                    (None, Some(m)) => format!("{m} Members"),
                    _ => "Server invite".into(),
                };
                out.push(
                    col()
                        .key(format!("dc-invcard-{}-{code}", msg.id))
                        .w(400.0)
                        .padding(12.0)
                        .gap(8.0)
                        .radius(RADIUS_CARD)
                        .fill(card_surface())
                        .border(1.0, glass_rim())
                        .child(
                            text(if is_mine {
                                "You sent an invite to join a server"
                            } else {
                                "You've been invited to join a server"
                            })
                            .font(type_label())
                            .color(fg_muted()),
                        )
                        .child(
                            row()
                                .w_full()
                                .gap(10.0)
                                .center_y()
                                .child(avatar_box(
                                    s,
                                    inv.icon.as_deref(),
                                    &title,
                                    40.0,
                                    10.0,
                                    None,
                                    false,
                                    scale,
                                ))
                                .child(
                                    col()
                                        .grow(1.0)
                                        .min_w(0.0)
                                        .child(
                                            text(title).font(type_control()).color(FG).truncate(),
                                        )
                                        .child(text(subtitle).font(type_label()).color(fg_muted())),
                                )
                                .child(if joined {
                                    glass_button("dc-inv-go", "Joined", None)
                                        .key(format!("dc-inv-go-{code}"))
                                        .on_press(move |s: &mut AppState| {
                                            if let Some(ref g) = gid_c {
                                                s.open_discord_guild(g);
                                                if let Some(ref c) = cid_c {
                                                    s.open_discord_channel(c);
                                                }
                                            }
                                        })
                                } else {
                                    primary_button("dc-inv-join", "Join", None)
                                        .key(format!("dc-inv-join-{code}"))
                                        .on_press(move |s: &mut AppState| {
                                            s.join_invite_code(&code_c)
                                        })
                                }),
                        ),
                );
            }
            Some(InviteCardEntry::Invalid) => {
                out.push(
                    col()
                        .w(400.0)
                        .padding(12.0)
                        .gap(4.0)
                        .radius(RADIUS_CARD)
                        .fill(card_surface())
                        .border(1.0, hairline())
                        .child(
                            text("Invalid invite")
                                .font(type_control())
                                .color(fg_muted()),
                        )
                        .child(
                            text(if is_mine {
                                "Try sending a new invite"
                            } else {
                                "Ask for a new invite"
                            })
                            .font(type_label())
                            .color(fg_faint()),
                        ),
                );
            }
            _ => {}
        }
    }

    for code in extract_gift_codes(&msg.content) {
        match s.discord.chat.links.gifts.get(&code) {
            Some(GiftCardEntry::Loaded(gift)) => {
                let claimed = gift.redeemed;
                let code_c = code.clone();
                let cid_c = msg.channel_id.clone();
                out.push(
                    col()
                        .key(format!("dc-gift-{}-{code}", msg.id))
                        .w(400.0)
                        .padding(12.0)
                        .gap(8.0)
                        .radius(RADIUS_CARD)
                        .fill(card_surface())
                        .border(1.0, glass_rim())
                        .child(
                            text(match gift.gifter.as_deref() {
                                Some(g) => format!("{g} sent a gift"),
                                None if is_mine => "You sent a gift".into(),
                                None => "You received a gift".into(),
                            })
                            .font(type_label())
                            .color(fg_muted()),
                        )
                        .child(
                            row()
                                .w_full()
                                .gap(10.0)
                                .center_y()
                                .child(icon_sparkles(22.0, good()))
                                .child(
                                    col()
                                        .grow(1.0)
                                        .min_w(0.0)
                                        .child(
                                            text(gift.name.clone())
                                                .font(type_control())
                                                .color(FG)
                                                .truncate(),
                                        )
                                        .child(
                                            text(if claimed {
                                                "Claimed"
                                            } else {
                                                "Ready to claim"
                                            })
                                            .font(type_label())
                                            .color(fg_muted()),
                                        ),
                                )
                                .child(if claimed {
                                    glass_button("dc-gift-done", "Claimed", None)
                                        .key(format!("dc-gift-done-{code}"))
                                } else {
                                    primary_button("dc-gift-claim", "Accept", None)
                                        .key(format!("dc-gift-claim-{code}"))
                                        .on_press(move |s: &mut AppState| {
                                            s.redeem_gift_code(&code_c, Some(&cid_c))
                                        })
                                }),
                        ),
                );
            }
            Some(GiftCardEntry::Invalid) => {
                out.push(
                    col()
                        .w(400.0)
                        .padding(12.0)
                        .gap(4.0)
                        .radius(RADIUS_CARD)
                        .fill(card_surface())
                        .border(1.0, hairline())
                        .child(
                            text("Gift expired or invalid")
                                .font(type_control())
                                .color(fg_muted()),
                        )
                        .child(
                            text("This gift code can no longer be claimed.")
                                .font(type_label())
                                .color(fg_faint()),
                        ),
                );
            }
            _ => {}
        }
    }

    out
}

fn system_message_row(s: &AppState, msg: &DiscordMessage) -> Option<El> {
    let my_id = s.discord.home.me.as_ref().map(|m| m.id.as_str());
    let name = msg.display_name().to_string();
    match msg.msg_type {
        3 => {
            let ended = msg
                .call
                .as_ref()
                .and_then(|c| c.ended_timestamp.as_deref())
                .is_some();
            let missed = ended
                && my_id.is_some_and(|me| {
                    msg.author.id != me
                        && !msg
                            .call
                            .as_ref()
                            .is_some_and(|c| c.participants.iter().any(|p| p == me))
                });
            let line = if missed {
                format!("You missed a call from {name}.")
            } else {
                format!("{name} started a call.")
            };
            Some(
                row()
                    .key(format!("dc-msg-row-{}", msg.id))
                    .w_full()
                    .px(16.0)
                    .py(6.0)
                    .gap(8.0)
                    .center_y()
                    .child(icon_phone(14.0, if missed { bad() } else { good() }))
                    .child(text(line).font(type_small()).color(fg_muted()))
                    .child(text(msg.time_short()).font(type_label()).color(fg_faint())),
            )
        }
        18 => {
            let thread_name = msg
                .thread
                .as_ref()
                .map(|t| t.name.clone())
                .filter(|n| !n.is_empty())
                .unwrap_or_else(|| msg.content.clone());
            let mut r = row()
                .key(format!("dc-msg-row-{}", msg.id))
                .w_full()
                .px(16.0)
                .py(6.0)
                .gap(8.0)
                .center_y()
                .child(icon_hash(14.0, fg_muted()))
                .child(
                    text(format!("{name} started a thread: {thread_name}"))
                        .font(type_small())
                        .color(fg_muted()),
                )
                .child(text(msg.time_short()).font(type_label()).color(fg_faint()));
            if let Some(ref th) = msg.thread {
                let tid = th.id.clone();
                r = r
                    .cursor(Cursor::Pointer)
                    .hit()
                    .press_on_down()
                    .on_press(move |s: &mut AppState| s.open_discord_channel(&tid));
            }
            Some(r)
        }
        46 => {
            let fields = msg
                .embeds
                .first()
                .map(|e| e.fields.as_slice())
                .unwrap_or(&[]);
            let question = fields
                .iter()
                .find(|f| f.name == "poll_question_text")
                .map(|f| f.value.as_str())
                .unwrap_or("A poll");
            let winner = fields
                .iter()
                .find(|f| f.name == "victor_answer_text")
                .map(|f| f.value.as_str());
            let votes = fields
                .iter()
                .find(|f| f.name == "victor_answer_votes")
                .and_then(|f| f.value.parse::<u32>().ok())
                .unwrap_or(0);
            let total = fields
                .iter()
                .find(|f| f.name == "total_votes")
                .and_then(|f| f.value.parse::<u32>().ok())
                .unwrap_or(0);
            let summary = if let Some(w) = winner
                && votes > 0
            {
                format!(
                    "{name}'s poll \"{question}\" closed. {w} won with {votes} of {total} votes."
                )
            } else {
                format!("{name}'s poll \"{question}\" closed. There was no winner.")
            };
            Some(
                row()
                    .key(format!("dc-msg-row-{}", msg.id))
                    .w_full()
                    .px(16.0)
                    .py(6.0)
                    .gap(8.0)
                    .center_y()
                    .child(icon_check(14.0, good()))
                    .child(text(summary).font(type_small()).color(fg_muted()).wrap()),
            )
        }
        6 => Some(
            row()
                .key(format!("dc-msg-row-{}", msg.id))
                .w_full()
                .px(16.0)
                .py(6.0)
                .gap(8.0)
                .center_y()
                .child(icon_pin(13.0, fg_muted()))
                .child(
                    text(format!("{name} pinned a message to this channel."))
                        .font(type_small())
                        .color(fg_muted()),
                )
                .child(
                    text(msg.time_short_12h())
                        .font(type_label())
                        .color(fg_faint()),
                ),
        ),
        67 => Some(
            row()
                .key(format!("dc-msg-row-{}", msg.id))
                .w_full()
                .px(16.0)
                .py(6.0)
                .gap(8.0)
                .center_y()
                .child(icon_check(14.0, good()))
                .child(
                    text(format!("{name} accepted your friend request."))
                        .font(type_small())
                        .color(fg_muted()),
                )
                .child(
                    text(msg.time_short_12h())
                        .font(type_label())
                        .color(fg_faint()),
                ),
        ),
        _ => None,
    }
}

fn message_row(
    s: &AppState,
    channel_id: &str,
    msg: &DiscordMessage,
    prev: Option<&DiscordMessage>,
    scale: f32,
) -> El {
    if let Some(sys) = system_message_row(s, msg) {
        return sys;
    }

    let my_id = s.discord.home.me.as_ref().map(|m| m.id.as_str());
    let is_mine = my_id == Some(msg.author.id.as_str());
    let new_day = prev.is_none_or(|p| !msg.day_key().is_empty() && p.day_key() != msg.day_key());
    let grouped = !new_day
        && msg.referenced_message.is_none()
        && msg.interaction_metadata.is_none()
        && msg.interaction.is_none()
        && prev.is_some_and(|p| {
            p.msg_type == 0 && p.author.id == msg.author.id && within_seven_minutes(p, msg)
        });

    let is_editing = s
        .discord
        .chat
        .editing
        .as_ref()
        .is_some_and(|(id, _)| id == &msg.id);
    let show_emoji_bar = s.discord.chat.emoji_picker_for.as_deref() == Some(msg.id.as_str());
    let reveal_spoilers = s.discord.revealed_spoilers.contains(&msg.id);
    let highlighted = s.discord.chat.highlighted_message_id.as_deref() == Some(msg.id.as_str());

    let mut body = col().grow(1.0).min_w(0.0).gap(4.0);

    if let Some(ix) = msg
        .interaction_metadata
        .as_ref()
        .or(msg.interaction.as_ref())
    {
        let who = ix
            .user
            .as_ref()
            .map(|u| u.global_name.as_deref().unwrap_or(&u.username))
            .unwrap_or("Someone");
        let cmd_str = ix
            .name
            .as_deref()
            .map(|n| format!("used /{n}"))
            .unwrap_or_else(|| "used a command".into());
        body = body.child(
            row()
                .w_full()
                .gap(6.0)
                .center_y()
                .child(icon_sparkles(11.0, fg_faint()))
                .child(text(who.to_string()).font(type_label()).color(fg_muted()))
                .child(text(cmd_str).font(type_label()).color(fg_faint())),
        );
    }

    if let Some(ref r) = msg.referenced_message {
        let preview: String = if r.content.is_empty() {
            "Click to see attachment".into()
        } else {
            r.content.chars().take(80).collect()
        };
        let r_av = r.author.avatar_url();
        let jump_gid = s.discord.active_guild_id.clone();
        let jump_cid = channel_id.to_string();
        let jump_mid = r.id.clone();
        body = body.child(
            row()
                .w_full()
                .gap(6.0)
                .center_y()
                .cursor(Cursor::Pointer)
                .hit()
                .press_on_down()
                .on_press(move |s: &mut AppState| {
                    s.jump_to_discord_message(jump_gid.as_deref(), &jump_cid, &jump_mid)
                })
                .child(
                    el().w(14.0)
                        .h(10.0)
                        .mt(4.0)
                        .border_sides([1.5, 0.0, 0.0, 1.5], fg_faint())
                        .radius(4.0),
                )
                .child(avatar_box(
                    s,
                    r_av.as_deref(),
                    r.display_name(),
                    16.0,
                    8.0,
                    None,
                    false,
                    scale,
                ))
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
                    s.discord.chat.picker.open_for_reaction = None;
                } else {
                    s.open_expression_picker_for_reaction(&mid_e);
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
                s.discord.chat.reply_ping = true;
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
    {
        let cid_bm = cid_actions.clone();
        let mid_bm = mid_actions.clone();
        let bookmarked = s.discord.chat.inbox.bookmark_ids.contains(&msg.id);
        actions = actions.child(
            tiny_action(
                format!("dc-msg-bm-{}", msg.id),
                if bookmarked {
                    "Remove bookmark"
                } else {
                    "Bookmark"
                },
                false,
                if bookmarked {
                    icon_bookmark_filled(13.0, good())
                } else {
                    icon_bookmark(13.0, fg_faint())
                },
            )
            .on_press(move |s: &mut AppState| {
                if bookmarked {
                    s.unsave_discord_message(&cid_bm, &mid_bm);
                } else {
                    s.save_discord_message(&cid_bm, &mid_bm, None);
                }
            }),
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

    let role_color = s
        .discord
        .active_guild_id
        .as_deref()
        .and_then(|gid| s.discord.chat.links.guild_colors.get(gid))
        .and_then(|m| m.get(&msg.author.id))
        .and_then(|hex_s| discord_threads::parse_hex_color(hex_s));
    let name_color = if is_mine {
        fg_muted()
    } else {
        role_color.unwrap_or(FG)
    };

    if !grouped {
        let pf_uid = msg.author.id.clone();
        let pf_gid = s.discord.active_guild_id.clone();
        let pf_name = author_name.clone();
        let pf_uname = msg.author.username.clone();
        let pf_av = msg.author.avatar_url();
        let mut header_left = row().gap(7.0).center_y().child(
            el().cursor(Cursor::Pointer)
                .hit()
                .press_on_down()
                .on_press(move |s: &mut AppState| {
                    s.open_user_profile_dialog(ProfileOpenRequest {
                        user_id: &pf_uid,
                        guild_id: pf_gid.as_deref(),
                        fallback_name: &pf_name,
                        fallback_username: &pf_uname,
                        fallback_avatar: pf_av.as_deref(),
                        full: false,
                        tab: ProfileTab::About,
                    });
                })
                .child(
                    text(author_name.clone())
                        .font(type_control())
                        .color(name_color),
                ),
        );
        if msg.author.bot {
            header_left = header_left.child(
                row()
                    .px(5.0)
                    .py(1.0)
                    .gap(3.0)
                    .center_y()
                    .radius(4.0)
                    .fill(subtle_fill())
                    .child(icon_check(10.0, FG))
                    .child(text("App").font(type_label()).color(FG)),
            );
        }
        let ts = msg.time_short_12h();
        if !ts.is_empty() {
            header_left = header_left.child(text(ts).font(type_label()).color(fg_faint()));
        }
        if msg.edited_timestamp.is_some() {
            header_left = header_left.child(text("(edited)").font(type_label()).color(fg_faint()));
        }
        if (msg.flags & 4096) != 0 {
            header_left = header_left.child(icon_bell_off(11.0, fg_faint()));
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

    let ch_names = channel_name_map(s);
    if is_editing {
        if let Some((_, ref edit_buf)) = s.discord.chat.editing {
            body = body.child(
                col()
                    .w_full()
                    .gap(6.0)
                    .padding(8.0)
                    .radius(RADIUS_SMALL)
                    .fill(subtle_fill())
                    .border(1.0, glass_rim())
                    .child(auto_grow(
                        text_input(edit_buf, type_body())
                            .key(format!("dc-edit-in-{}", msg.id))
                            .label("Edit message")
                            .w_full()
                            .color(FG)
                            .on_edit(edit_access(|s: &mut AppState| {
                                match s.discord.chat.editing.as_mut() {
                                    Some(editing) => &mut editing.1,
                                    None => &mut s.ui_discard_edit,
                                }
                            }))
                            .on_submit(|s: &mut AppState, _| s.save_discord_edit()),
                        1,
                        6,
                    ))
                    .child(
                        row()
                            .gap(6.0)
                            .child(
                                primary_button("dc-edit-save", "Save", None)
                                    .key(format!("dc-edit-save-{}", msg.id))
                                    .on_press(|s: &mut AppState| s.save_discord_edit()),
                            )
                            .child(
                                glass_button("dc-edit-cancel", "Cancel", None)
                                    .key(format!("dc-edit-cancel-{}", msg.id))
                                    .on_press(|s: &mut AppState| s.cancel_edit_discord_message()),
                            ),
                    ),
            );
        }
    } else if (msg.flags & 128) != 0 {
        body = body.child(
            text(format!("{author_name} is thinking…"))
                .font(type_small())
                .color(fg_muted()),
        );
    } else if !msg.content.is_empty() {
        let md = el().key(format!("dc-msg-body-{}", msg.id)).w_full().child(
            discord_md::render_markdown_with_channels(
                &msg.content,
                &msg.mentions,
                &ch_names,
                &msg.id,
                reveal_spoilers,
            ),
        );
        if let Some(act) = actions.take() {
            body = body.child(
                row()
                    .w_full()
                    .gap(8.0)
                    .child(col().grow(1.0).min_w(0.0).child(md))
                    .child(act),
            );
        } else {
            body = body.child(md);
        }
    } else if let Some(act) = actions.take() {
        body = body.child(row().w_full().justify_end().child(act));
    }

    if let Some(ref fail) = msg.failed {
        body = body.child(text(fail.clone()).font(type_label()).color(bad()));
    }

    if !msg.attachments.is_empty() {
        let mut att_col = col().w_full().gap(6.0).pt(2.0);
        let img_indices: Vec<usize> = msg
            .attachments
            .iter()
            .enumerate()
            .filter(|(_, a)| a.is_image())
            .map(|(i, _)| i)
            .collect();
        if img_indices.len() >= 2 {
            let cid_strip = channel_id.to_string();
            let mid_strip = msg.id.clone();
            let indices_k = img_indices.clone();
            let indices_b = img_indices;
            let cid_k = cid_strip.clone();
            let mid_k = mid_strip.clone();
            att_col = att_col.child(
                row()
                    .key(format!("dc-att-xscroll-{}", msg.id))
                    .w_full()
                    .h(268.0)
                    .scroll_x()
                    .child(
                        row()
                            .key(format!("dc-att-strip-{}", msg.id))
                            .gap(8.0)
                            .virtual_cols(
                                indices_k.len(),
                                280.0,
                                move |s: &AppState, i: usize| {
                                    let att_idx = indices_k[i];
                                    s.discord
                                        .chat
                                        .messages_by_channel
                                        .get(&cid_k)
                                        .and_then(|v| v.iter().find(|m| m.id == mid_k))
                                        .and_then(|m| m.attachments.get(att_idx))
                                        .map(|a| Cow::Borrowed(a.id.as_str()))
                                        .unwrap_or(Cow::Borrowed("missing"))
                                },
                                move |s: &AppState, i: usize| {
                                    let att_idx = indices_b[i];
                                    s.discord
                                        .chat
                                        .messages_by_channel
                                        .get(&cid_strip)
                                        .and_then(|v| v.iter().find(|m| m.id == mid_strip))
                                        .and_then(|m| m.attachments.get(att_idx))
                                        .map(|a| render_attachment(s, a, scale))
                                        .unwrap_or_else(el)
                                },
                            ),
                    ),
            );
            for att in msg.attachments.iter().filter(|a| !a.is_image()) {
                att_col = att_col.child(render_attachment(s, att, scale));
            }
        } else {
            for att in &msg.attachments {
                att_col = att_col.child(render_attachment(s, att, scale));
            }
        }
        body = body.child(att_col);
    }

    if let Some(ref poll) = msg.poll {
        body = body.child(discord_threads::poll_card(s, msg, poll));
    }

    if !msg.sticker_items.is_empty() {
        let mut st_row = row().gap(8.0).flex_wrap();
        for st in &msg.sticker_items {
            let url = discord_pickers::sticker_cdn_url(&st.id, st.format_type, 160);
            let mut tile = col().gap(2.0).center();
            if let Some(img) = s.remote_image_sized(&url, 128.0, scale) {
                tile = tile.child(
                    el().size(128.0, 128.0)
                        .pixels_fit(img.pixels(), SurfaceFit::Fit),
                );
            } else {
                tile = tile.child(
                    el().size(96.0, 96.0)
                        .radius(RADIUS_SMALL)
                        .fill(subtle_fill())
                        .center()
                        .child(text(st.name.clone()).font(type_label()).color(fg_muted())),
                );
            }
            st_row = st_row.child(tile);
        }
        body = body.child(st_row);
    }

    if (msg.flags & 4) == 0 {
        for embed in &msg.embeds {
            body = body.child(embed_card(s, embed, &ch_names, &msg.id, scale));
        }
    }

    for card in render_link_cards(s, msg, is_mine, scale) {
        body = body.child(card);
    }

    if !msg.components.is_empty() && !is_editing {
        body = body.child(render_message_components(s, msg));
    }

    if let Some(snap) = msg.message_snapshots.first() {
        let mut fwd = col()
            .w(460.0)
            .max_w(520.0)
            .padding(10.0)
            .gap(4.0)
            .radius(RADIUS_CARD)
            .fill(subtle_fill())
            .border(1.0, hairline())
            .child(text("Forwarded").font(type_label()).color(fg_muted()));
        if !snap.message.content.is_empty() {
            fwd = fwd.child(discord_md::render_markdown(
                &snap.message.content,
                &[],
                &msg.id,
                true,
            ));
        }
        body = body.child(fwd);
    }

    if let Some(ref th) = msg.thread {
        let tid = th.id.clone();
        let cnt = th.message_count.unwrap_or(0);
        body = body.child(
            row()
                .key(format!("dc-thbox-{}", th.id))
                .px(10.0)
                .py(6.0)
                .gap(8.0)
                .center_y()
                .radius(RADIUS_SMALL)
                .fill(subtle_fill())
                .border(1.0, hairline())
                .hover(hover_fill())
                .cursor(Cursor::Pointer)
                .hit()
                .press_on_down()
                .on_press(move |s: &mut AppState| s.open_discord_channel(&tid))
                .child(icon_hash(13.0, fg_muted()))
                .child(text(th.name.clone()).font(type_small()).color(FG))
                .child(
                    text(format!(
                        "{cnt} {}",
                        if cnt == 1 { "Message" } else { "Messages" }
                    ))
                    .font(type_label())
                    .color(fg_faint()),
                ),
        );
    }

    if (msg.flags & 64) != 0 {
        let cid_dis = msg.channel_id.clone();
        let mid_dis = msg.id.clone();
        body = body.child(
            row()
                .gap(6.0)
                .center_y()
                .child(
                    text("Only you can see this ·")
                        .font(type_label())
                        .color(fg_faint()),
                )
                .child(
                    el().cursor(Cursor::Pointer)
                        .hit()
                        .press_on_down()
                        .on_press(move |s: &mut AppState| {
                            s.dismiss_ephemeral_message(&cid_dis, &mid_dis)
                        })
                        .child(text("Dismiss message").font(type_label()).color(good())),
                ),
        );
    }

    if !msg.reactions.is_empty() || show_emoji_bar {
        let mut rrow = row().w_full().gap(6.0).pt(2.0).flex_wrap().center_y();
        for r in &msg.reactions {
            let (normal, burst) = r
                .count_details
                .as_ref()
                .map(|d| (d.normal, d.burst))
                .unwrap_or((r.count, 0));
            for (is_burst, cnt, mine) in [(true, burst, r.me_burst), (false, normal, r.me)] {
                if cnt <= 0 {
                    continue;
                }
                let cid_rx = channel_id.to_string();
                let mid_rx = msg.id.clone();
                let key_rx = r.emoji.key();
                let cid_view = channel_id.to_string();
                let mid_view = msg.id.clone();
                let key_view = r.emoji.key();
                rrow = rrow.child(
                    row()
                        .key(format!("dc-rx-{}-{}-{is_burst}", msg.id, key_rx))
                        .h(26.0)
                        .px(8.0)
                        .gap(5.0)
                        .center_y()
                        .radius(RADIUS_PILL)
                        .fill(if mine {
                            good().alpha(0.16)
                        } else {
                            subtle_fill()
                        })
                        .border(1.0, if mine { good().alpha(0.45) } else { hairline() })
                        .hover(hover_fill())
                        .cursor(Cursor::Pointer)
                        .hit()
                        .press_on_down()
                        .press_scale(PRESS_SCALE)
                        .on_press(move |s: &mut AppState| {
                            s.toggle_discord_reaction_with_burst(
                                &cid_rx, &mid_rx, &key_rx, mine, is_burst,
                            )
                        })
                        .on_secondary_press(move |s: &mut AppState, _, _| {
                            s.open_reactors_dialog(&cid_view, &mid_view, &key_view, is_burst)
                        })
                        .child(text(r.emoji.display()).font(type_small()).color(FG))
                        .child(text(cnt.to_string()).font(type_label()).color(if mine {
                            good()
                        } else {
                            fg_muted()
                        })),
                );
            }
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
                .border_gradient(1.0, glass_rim_top(), glass_rim_bottom());
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
                        .cursor(Cursor::Pointer)
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

    let msg_ctx_target = ContextMenuTarget::DiscordMessage {
        channel_id: channel_id.to_string(),
        guild_id: s.discord.active_guild_id.clone(),
        message_id: msg.id.clone(),
        author_id: msg.author.id.clone(),
        author_name: msg.display_name().to_string(),
        content: msg.content.clone(),
        mine: is_mine,
        pinned: msg.pinned,
    };

    let row_fill = if highlighted {
        good().alpha(0.08)
    } else {
        Color::TRANSPARENT
    };

    let line = if grouped {
        row()
            .key(format!("dc-msg-row-{}", msg.id))
            .w_full()
            .pl(56.0)
            .pr(16.0)
            .py(2.0)
            .fill(row_fill)
            .opacity(if msg.pending { 0.6 } else { 1.0 })
            .hover_group()
            .hover_motion(Spring::NONE, SPRING_QUICK)
            .hit()
            .on_secondary_press(move |s: &mut AppState, p, _| {
                s.open_context_menu(msg_ctx_target.clone(), p.x, p.y);
            })
            .child(body)
    } else {
        let av_url = msg.author.avatar_url();
        let author_ctx = ContextMenuTarget::DiscordMember {
            guild_id: s.discord.active_guild_id.clone(),
            user_id: msg.author.id.clone(),
            name: msg.display_name().to_string(),
            username: Some(msg.author.username.clone()).filter(|u| !u.is_empty()),
            in_voice: false,
        };
        let pf_uid = msg.author.id.clone();
        let pf_gid = s.discord.active_guild_id.clone();
        let pf_name = msg.display_name().to_string();
        let pf_uname = msg.author.username.clone();
        let pf_av = av_url.clone();
        row()
            .key(format!("dc-msg-row-{}", msg.id))
            .w_full()
            .px(16.0)
            .pt(10.0)
            .pb(3.0)
            .gap(10.0)
            .fill(row_fill)
            .opacity(if msg.pending { 0.6 } else { 1.0 })
            .hover_group()
            .hover_motion(Spring::NONE, SPRING_QUICK)
            .hit()
            .on_secondary_press(move |s: &mut AppState, p, _| {
                s.open_context_menu(msg_ctx_target.clone(), p.x, p.y);
            })
            .child(
                el().cursor(Cursor::Pointer)
                    .hit()
                    .on_press(move |s: &mut AppState| {
                        s.open_user_profile_dialog(ProfileOpenRequest {
                            user_id: &pf_uid,
                            guild_id: pf_gid.as_deref(),
                            fallback_name: &pf_name,
                            fallback_username: &pf_uname,
                            fallback_avatar: pf_av.as_deref(),
                            full: false,
                            tab: ProfileTab::About,
                        });
                    })
                    .on_secondary_press(move |s: &mut AppState, p, _| {
                        s.open_context_menu(author_ctx.clone(), p.x, p.y);
                    })
                    .child(avatar_box(
                        s,
                        av_url.as_deref(),
                        msg.display_name(),
                        30.0,
                        15.0,
                        None,
                        false,
                        scale,
                    )),
            )
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
                    .child(text(msg.day_label()).font(type_label()).color(fg_faint()))
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
    let loading = s.discord.chat.loading_channels.contains(channel_id);
    let has_more = s
        .discord
        .chat
        .has_more_before
        .get(channel_id)
        .copied()
        .unwrap_or(false);

    let mut feed = col().w_full().grow(1.0).justify_end().pb(12.0);

    if has_more && let Some(oldest_id) = msgs.first().map(|m| m.id.clone()) {
        let cid_more = channel_id.to_string();
        feed = feed.child(
            el().w_full().py(10.0).center().child(
                glass_button(
                    "dc-load-older",
                    if loading {
                        "Loading older…"
                    } else {
                        "Load older messages"
                    },
                    None,
                )
                .on_press(move |s: &mut AppState| {
                    s.load_discord_messages(&cid_more, Some(&oldest_id))
                }),
            ),
        );
    } else if msgs.is_empty() {
        feed = feed.child(
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

    if !msgs.is_empty() {
        let cid_key = channel_id.to_string();
        let cid_build = channel_id.to_string();
        feed = feed.child(
            col()
                .key(format!("dc-vrows-{channel_id}"))
                .w_full()
                .growth(GrowthSpec::default())
                .selection_group(true)
                .virtual_rows(
                    msgs.len(),
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
                ),
        );
    } else if loading {
        feed = feed.child(
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
        .child(feed);

    let mut composer_wrap = col().w_full().px(14.0).pb(12.0).pt(6.0).gap(6.0);

    if let Some(ac) = discord_pickers::mention_autocomplete_popover(s, channel_id, scale) {
        composer_wrap = composer_wrap.child(ac);
    }
    if let Some(slash) = discord_pickers::slash_command_list_popover(s, channel_id) {
        composer_wrap = composer_wrap.child(slash);
    }

    if let Some((_, ref author)) = s.discord.chat.reply_to {
        let ping_on = s.discord.chat.reply_ping;
        composer_wrap = composer_wrap.child(
            row()
                .key("dc-reply-bar")
                .w_full()
                .px(12.0)
                .py(6.0)
                .justify_between()
                .center_y()
                .radius(RADIUS_SMALL)
                .fill(subtle_fill())
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
                    row()
                        .gap(6.0)
                        .center_y()
                        .child(
                            el().key("dc-reply-ping")
                                .px(8.0)
                                .h(22.0)
                                .radius(RADIUS_PILL)
                                .fill(if ping_on {
                                    good().alpha(0.18)
                                } else {
                                    card_surface()
                                })
                                .border(
                                    1.0,
                                    if ping_on {
                                        good().alpha(0.45)
                                    } else {
                                        hairline()
                                    },
                                )
                                .center()
                                .cursor(Cursor::Pointer)
                                .hit()
                                .press_on_down()
                                .on_press(|s: &mut AppState| {
                                    s.discord.chat.reply_ping = !s.discord.chat.reply_ping;
                                })
                                .child(
                                    text(if ping_on { "@ On" } else { "@ Off" })
                                        .font(type_label())
                                        .color(if ping_on { good() } else { fg_muted() }),
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
                ),
        );
    }

    if !s.discord.chat.pending_uploads.is_empty() {
        let count = s.discord.chat.pending_uploads.len();
        composer_wrap = composer_wrap.child(
            row()
                .key("dc-up-xscroll")
                .w_full()
                .h(36.0)
                .scroll_x()
                .child(row().key("dc-up-strip").gap(6.0).virtual_cols(
                    count,
                    220.0,
                    |s: &AppState, i: usize| {
                        s.discord
                            .chat
                            .pending_uploads
                            .get(i)
                            .map(|u| Cow::Borrowed(u.local_id.as_str()))
                            .unwrap_or(Cow::Borrowed("missing"))
                    },
                    |s: &AppState, i: usize| {
                        s.discord
                            .chat
                            .pending_uploads
                            .get(i)
                            .map(pending_upload_chip)
                            .unwrap_or_else(el)
                    },
                )),
        );
    }

    if can_send {
        let mut composer = row()
            .key("dc-composer")
            .w_full()
            .min_h(44.0)
            .px(10.0)
            .py(6.0)
            .gap(8.0)
            .center_y()
            .radius(RADIUS_CARD)
            .fill(card_surface());
        composer = if s.discord.chat.drop_hover {
            composer.border(1.0, good())
        } else {
            composer.border_gradient(1.0, glass_rim_top(), glass_rim_bottom())
        };

        let middle = if let Some(cmd_form) = discord_pickers::slash_command_form(s) {
            col().grow(1.0).min_w(0.0).child(cmd_form)
        } else {
            auto_grow(
                text_input(&s.discord.chat.composer, type_body())
                    .key("dc-composer-input")
                    .label("Message composer")
                    .cursor(Cursor::Text)
                    .grow(1.0)
                    .color(FG)
                    .placeholder(placeholder, fg_muted())
                    .on_edit(edit_access(|s: &mut AppState| {
                        s.notify_discord_typing();
                        &mut s.discord.chat.composer
                    }))
                    .on_submit(send_with_launch),
                1,
                8,
            )
            .grow(1.0)
            .min_w(0.0)
        };

        let composer = composer
            .child(
                tiny_action(
                    "dc-composer-plus".into(),
                    "Upload a file or create a poll",
                    false,
                    icon_plus(14.0, fg_muted()),
                )
                .on_press(|s: &mut AppState| {
                    s.discord.chat.plus_menu_open = !s.discord.chat.plus_menu_open;
                    s.discord.chat.picker.open_for_composer = false;
                }),
            )
            .child(middle)
            .child(
                tiny_action(
                    "dc-composer-emoji".into(),
                    "Emoji, GIFs, and stickers",
                    false,
                    icon_smile(15.0, fg_muted()),
                )
                .on_press(|s: &mut AppState| {
                    s.open_expression_picker_for_composer(ExpressionTab::Emoji);
                }),
            )
            .child({
                let ready = !s.discord.chat.composer.text.trim().is_empty()
                    || !s.discord.chat.pending_uploads.is_empty();
                el().key("dc-composer-send")
                    .role("button")
                    .label("Send message")
                    .cursor(Cursor::Pointer)
                    .focusable(true)
                    .hit()
                    .press_on_down()
                    .press_scale(PRESS_SCALE)
                    .size(30.0, 30.0)
                    .radius(RADIUS_PILL)
                    .fill(if ready { FG } else { Color::TRANSPARENT })
                    .border(1.0, if ready { FG } else { glass_rim() })
                    .hover(if ready { FG.alpha(0.92) } else { hover_fill() })
                    .press(if ready {
                        FG.alpha(0.82)
                    } else {
                        pressed_fill()
                    })
                    .center()
                    .motion(SPRING_QUICK)
                    .on_press_tx(send_with_launch)
                    .child(icon_arrow_up(
                        14.0,
                        if ready { PRIMARY_FG } else { fg_muted() },
                    ))
            });
        composer_wrap = composer_wrap.child(composer);

        let typing = s
            .discord
            .chat
            .apps
            .app_status
            .clone()
            .or_else(|| s.discord.chat.typing_label(channel_id));
        let len = s.discord.chat.composer.text.chars().count();
        let left = (s.discord.chat.char_limit as isize) - (len as isize);
        let show_count = left <= (s.discord.chat.char_limit / 10) as isize;
        if typing.is_some() || show_count || s.discord.chat.too_long_prompt {
            let mut foot = row().w_full().px(6.0).justify_between().center_y();
            foot = foot.child(
                text(typing.unwrap_or_default())
                    .font(type_label())
                    .color(fg_muted()),
            );
            let mut right_foot = row().gap(8.0).center_y();
            if s.discord.chat.too_long_prompt || left < 0 {
                right_foot = right_foot.child(
                    el().key("dc-upload-as-file-prompt")
                        .cursor(Cursor::Pointer)
                        .hit()
                        .press_on_down()
                        .on_press(|s: &mut AppState| s.upload_composer_text_as_file())
                        .child(text("Upload as file?").font(type_label()).color(good())),
                );
            }
            if show_count {
                right_foot = right_foot.child(
                    text(left.to_string())
                        .font(type_label())
                        .color(if left < 0 { bad() } else { fg_muted() }),
                );
            }
            composer_wrap = composer_wrap.child(foot.child(right_foot));
        }
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

    if s.discord.chat.plus_menu_open {
        let has_text = !s.discord.chat.composer.text.trim().is_empty();
        let mut pmenu = col()
            .key("dc-plus-menu")
            .role("menu")
            .focus_group()
            .pin(Some(14.0), None, None, Some(64.0))
            .w(230.0)
            .padding(6.0)
            .gap(2.0)
            .radius(RADIUS_CARD)
            .fill(BG)
            .border_gradient(1.0, glass_rim_top(), glass_rim_bottom())
            .transition(popover_transition_from(rune::Vec2::new(0.0, 1.0)))
            .child(
                row()
                    .key("dc-pm-drop-hint")
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
                    .on_press(|s: &mut AppState| {
                        s.discord.chat.plus_menu_open = false;
                        s.notice = Some("Drop files onto the chat to upload".into());
                    })
                    .child(icon_paperclip(14.0, FG))
                    .child(text("Upload a file").font(type_small()).color(FG)),
            );
        if has_text {
            pmenu = pmenu.child(
                row()
                    .key("dc-pm-as-file")
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
                    .on_press(|s: &mut AppState| s.upload_composer_text_as_file())
                    .child(icon_paperclip(14.0, FG))
                    .child(text("Upload message as file").font(type_small()).color(FG)),
            );
        }
        pmenu = pmenu.child(
            row()
                .key("dc-pm-poll")
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
                .on_press(|s: &mut AppState| s.open_poll_creator())
                .child(icon_sparkles(14.0, FG))
                .child(text("Create poll").font(type_small()).color(FG)),
        );
        stage = stage.child(pmenu);
    }

    if let Some(picker) = discord_pickers::expression_picker_popover(s, scale) {
        stage = stage.child(picker);
    }

    stage
}

fn pending_upload_chip(u: &PendingAttachment) -> El {
    let lid_mod = u.local_id.clone();
    let lid_sp = u.local_id.clone();
    let lid_rm = u.local_id.clone();
    let status_txt = if u.uploading {
        "Uploading…".into()
    } else if let Some(ref e) = u.error {
        e.clone()
    } else if u.spoiler {
        format!("Spoiler · {}", format_bytes(u.size))
    } else {
        format_bytes(u.size)
    };
    row()
        .key(format!("dc-up-{}", u.local_id))
        .h(32.0)
        .px(10.0)
        .gap(6.0)
        .center_y()
        .radius(RADIUS_SMALL)
        .fill(card_surface())
        .border(
            1.0,
            if u.error.is_some() {
                bad().alpha(0.5)
            } else if u.spoiler {
                good().alpha(0.45)
            } else {
                glass_rim()
            },
        )
        .child(icon_paperclip(12.0, fg_muted()))
        .child(text(u.name.clone()).font(type_small()).color(FG))
        .child(text(status_txt).font(type_label()).color(fg_faint()))
        .child(
            tiny_action(
                format!("dc-up-ed-{}", u.local_id),
                "Modify attachment",
                false,
                icon_edit(11.0, fg_muted()),
            )
            .on_press(move |s: &mut AppState| s.open_modify_attachment(&lid_mod)),
        )
        .child(
            el().key(format!("dc-up-sp-{}", u.local_id))
                .px(5.0)
                .h(20.0)
                .radius(4.0)
                .fill(if u.spoiler {
                    good().alpha(0.2)
                } else {
                    subtle_fill()
                })
                .center()
                .cursor(Cursor::Pointer)
                .hit()
                .press_on_down()
                .on_press(move |s: &mut AppState| s.toggle_attachment_spoiler(&lid_sp))
                .child(text("SP").font(type_label()).color(if u.spoiler {
                    good()
                } else {
                    fg_muted()
                })),
        )
        .child(
            tiny_action(
                format!("dc-up-rm-{}", u.local_id),
                "Remove attachment",
                false,
                icon_close(11.0, fg_muted()),
            )
            .on_press(move |s: &mut AppState| s.remove_pending_upload(&lid_rm)),
        )
}

pub fn chat_drawer_panel(s: &AppState, scale: f32) -> Option<El> {
    match s.discord.chat.drawer {
        ChatDrawer::None => None,
        ChatDrawer::Pins => Some(discord_search::pins_drawer(s, scale)),
        ChatDrawer::Search => Some(discord_search::search_drawer(s, scale)),
    }
}

pub fn chat_overlay_layers(s: &AppState, blur: bool, scale: f32) -> Vec<El> {
    let mut out = Vec::new();
    if let Some(el) = discord_threads::create_dm_overlay(s, blur, scale) {
        out.push(el);
    }
    if let Some(el) = discord_threads::poll_creator_overlay(s, blur) {
        out.push(el);
    }
    if let Some(el) = discord_threads::invite_dialog_overlay(s, blur, scale) {
        out.push(el);
    }
    if let Some(el) = discord_threads::forward_dialog_overlay(s, blur, scale) {
        out.push(el);
    }
    if let Some(el) = discord_threads::new_thread_dialog_overlay(s, blur) {
        out.push(el);
    }
    if let Some(el) = discord_threads::report_dialog_overlay(s, blur) {
        out.push(el);
    }
    if let Some(el) = discord_threads::modify_attachment_overlay(s, blur) {
        out.push(el);
    }
    if let Some(el) = discord_threads::profile_dialog_overlay(s, blur, scale) {
        out.push(el);
    }
    if let Some(el) = discord_pickers::reactions_dialog_overlay(s, blur, scale) {
        out.push(el);
    }
    if let Some(el) = discord_pickers::app_modal_overlay(s, blur) {
        out.push(el);
    }
    if s.discord.chat.links.lightbox.is_open() && !s.discord.chat.links.lightbox_slides.is_empty() {
        let slides = &s.discord.chat.links.lightbox_slides;
        let mut lb_items = Vec::new();
        let mut slide_urls = Vec::new();
        for sl in slides {
            if let Some(img) = s.remote_image_sized(&sl.src, 1400.0, scale) {
                let mut item = LightboxItem::new(img.pixels().clone());
                if !sl.caption.is_empty() {
                    item = item.caption(sl.caption.clone());
                }
                if !sl.alt.is_empty() {
                    item = item.alt(sl.alt.clone());
                }
                lb_items.push(item);
                slide_urls.push(sl.original_url.clone());
            }
        }
        if !lb_items.is_empty() {
            let active_idx = s
                .discord
                .chat
                .links
                .lightbox
                .current_index(lb_items.len())
                .unwrap_or(0);
            let active_orig = slide_urls.get(active_idx).cloned().unwrap_or_default();
            if let Some(lb_el) = lightbox(
                "dc-lightbox",
                lb_items,
                |st: &AppState| &st.discord.chat.links.lightbox,
                |st: &mut AppState| &mut st.discord.chat.links.lightbox,
            )
            .backdrop_color(scrim_fill())
            .surface_color(BG)
            .text_color(FG)
            .border_color(glass_rim())
            .show_counter(true)
            .show_zoom_controls(true)
            .show_nav_buttons(true)
            .on_close(|st: &mut AppState, _| {
                st.discord.chat.links.lightbox_slides.clear();
            })
            .build(s)
            {
                out.push(lb_el);
                if !active_orig.is_empty() {
                    let orig_copy = active_orig.clone();
                    let orig_save = active_orig.clone();
                    let orig_open = active_orig;
                    out.push(
                        row()
                            .key("dc-lightbox-actions")
                            .pin(Some(20.0), None, None, Some(20.0))
                            .padding(6.0)
                            .gap(6.0)
                            .center_y()
                            .radius(RADIUS_PILL)
                            .fill(BG)
                            .border(1.0, glass_rim())
                            .child(
                                glass_button("dc-lb-copy", "Copy image", Some(icon_copy(12.0, FG)))
                                    .on_press(move |st: &mut AppState| {
                                        st.discord_image_action("copy", &orig_copy);
                                    }),
                            )
                            .child(
                                glass_button(
                                    "dc-lb-save",
                                    "Save image",
                                    Some(icon_download(12.0, FG)),
                                )
                                .on_press(
                                    move |st: &mut AppState| {
                                        st.discord_image_action("save", &orig_save);
                                    },
                                ),
                            )
                            .child(
                                glass_button(
                                    "dc-lb-open",
                                    "Open original",
                                    Some(icon_external(12.0, FG)),
                                )
                                .on_press(
                                    move |st: &mut AppState| {
                                        st.open_external_url(&orig_open);
                                    },
                                ),
                            ),
                    );
                }
            }
        } else if let Some(active) = slides.get(
            s.discord
                .chat
                .links
                .lightbox
                .current_index(slides.len())
                .unwrap_or(0),
        ) {
            let orig_open = active.original_url.clone();
            out.push(
                el().key("dc-lightbox-loading")
                    .pin(Some(0.0), Some(0.0), Some(0.0), Some(0.0))
                    .fill(scrim_fill())
                    .center()
                    .hit()
                    .on_press(|st: &mut AppState| st.close_discord_lightbox())
                    .child(
                        col()
                            .padding(20.0)
                            .gap(12.0)
                            .center()
                            .radius(RADIUS_PANEL)
                            .fill(BG)
                            .border(1.0, glass_rim())
                            .hit()
                            .child(text("Loading image…").font(type_body()).color(FG))
                            .child(
                                row()
                                    .gap(8.0)
                                    .child(
                                        glass_button(
                                            "dc-lb-loading-open",
                                            "Open original",
                                            Some(icon_external(12.0, FG)),
                                        )
                                        .on_press(
                                            move |st: &mut AppState| {
                                                st.open_external_url(&orig_open);
                                            },
                                        ),
                                    )
                                    .child(
                                        glass_button("dc-lb-loading-close", "Close", None)
                                            .on_press(|st: &mut AppState| {
                                                st.close_discord_lightbox();
                                            }),
                                    ),
                            ),
                    ),
            );
        }
    }
    if let Some(ref im) = s.discord.chat.links.image_menu {
        let copy_src = im.original_url.clone();
        let save_src = im.original_url.clone();
        let copy_link = im.original_url.clone();
        let open_link = im.original_url.clone();
        let pt_key = format!(
            "dc-image-menu-pt-{}-{}",
            im.x.round() as i32,
            im.y.round() as i32
        );
        let menu = col()
            .key("dc-image-menu")
            .role("menu")
            .focus_group()
            .anchored_fit(pt_key.clone(), rune::Side::Bottom, rune::Align::Start, 1.0)
            .w(196.0)
            .padding(6.0)
            .gap(2.0)
            .radius(RADIUS_CARD)
            .continuous_corners()
            .clip_rounded(RADIUS_CARD)
            .liquid_glass(exo_kit::exo_glass(exo_kit::ExoGlass::Menu, false))
            .transition(popover_transition_from(rune::Vec2::new(0.0, 0.0)))
            .child(
                row()
                    .w_full()
                    .h(32.0)
                    .px(10.0)
                    .gap(8.0)
                    .center_y()
                    .radius(6.0)
                    .hover(hover_fill())
                    .cursor(Cursor::Pointer)
                    .hit()
                    .press_on_down()
                    .on_press(move |s: &mut AppState| s.discord_image_action("copy", &copy_src))
                    .child(icon_copy(13.0, FG))
                    .child(text("Copy image").font(type_small()).color(FG)),
            )
            .child(
                row()
                    .w_full()
                    .h(32.0)
                    .px(10.0)
                    .gap(8.0)
                    .center_y()
                    .radius(6.0)
                    .hover(hover_fill())
                    .cursor(Cursor::Pointer)
                    .hit()
                    .press_on_down()
                    .on_press(move |s: &mut AppState| s.discord_image_action("save", &save_src))
                    .child(icon_download(13.0, FG))
                    .child(text("Save image").font(type_small()).color(FG)),
            )
            .child(el().w_full().h(1.0).fill(hairline()))
            .child(
                row()
                    .w_full()
                    .h(32.0)
                    .px(10.0)
                    .gap(8.0)
                    .center_y()
                    .radius(6.0)
                    .hover(hover_fill())
                    .cursor(Cursor::Pointer)
                    .hit()
                    .press_on_down()
                    .on_press_tx(move |s: &mut AppState, tx| {
                        tx.copy(copy_link.clone());
                        s.discord.chat.links.image_menu = None;
                        s.notice = Some("Copied image link".into());
                    })
                    .child(icon_copy(13.0, FG))
                    .child(text("Copy link").font(type_small()).color(FG)),
            )
            .child(
                row()
                    .w_full()
                    .h(32.0)
                    .px(10.0)
                    .gap(8.0)
                    .center_y()
                    .radius(6.0)
                    .hover(hover_fill())
                    .cursor(Cursor::Pointer)
                    .hit()
                    .press_on_down()
                    .on_press(move |s: &mut AppState| {
                        s.discord.chat.links.image_menu = None;
                        s.open_external_url(&open_link);
                    })
                    .child(icon_external(13.0, FG))
                    .child(text("Open link").font(type_small()).color(FG)),
            );
        out.push(
            el().key("dc-image-menu-scrim")
                .pin(Some(0.0), Some(0.0), Some(0.0), Some(0.0))
                .hit()
                .on_press(|s: &mut AppState| s.discord.chat.links.image_menu = None)
                .child(el().key(pt_key).absolute(im.x, im.y).size(0.0, 0.0))
                .child(menu.hit().on_press(|_: &mut AppState| {})),
        );
    }
    out
}

pub fn header_tools(s: &AppState) -> El {
    let search_open = s.discord.chat.drawer == ChatDrawer::Search;
    let pins_open = s.discord.chat.drawer == ChatDrawer::Pins;
    row()
        .gap(4.0)
        .center_y()
        .child(
            el().key("dc-hdr-search")
                .role("button")
                .label("Search")
                .cursor(Cursor::Pointer)
                .focusable(true)
                .hit()
                .press_on_down()
                .press_scale(PRESS_SCALE)
                .size(32.0, 32.0)
                .radius(RADIUS_PILL)
                .fill(if search_open {
                    subtle_fill()
                } else {
                    Color::TRANSPARENT
                })
                .hover(hover_fill())
                .press(pressed_fill())
                .center()
                .motion(SPRING_QUICK)
                .on_press(|s: &mut AppState| s.toggle_discord_search_drawer())
                .child(icon_search(16.0, if search_open { FG } else { fg_muted() })),
        )
        .child(
            el().key("dc-hdr-pins")
                .role("button")
                .label("Pinned messages")
                .cursor(Cursor::Pointer)
                .focusable(true)
                .hit()
                .press_on_down()
                .press_scale(PRESS_SCALE)
                .size(32.0, 32.0)
                .radius(RADIUS_PILL)
                .fill(if pins_open {
                    subtle_fill()
                } else {
                    Color::TRANSPARENT
                })
                .hover(hover_fill())
                .press(pressed_fill())
                .center()
                .motion(SPRING_QUICK)
                .on_press(|s: &mut AppState| s.toggle_discord_pins_drawer())
                .child(icon_more_horiz(
                    16.0,
                    if pins_open { FG } else { fg_muted() },
                )),
        )
}

#[cfg(test)]
mod tests {
    use crate::shell;
    use crate::state::{AppState, Room};
    use exo_rpc::{DiscordMessage, DiscordUser};
    use rune::render::KIND_GLYPH;
    use rune::{Env, Key, KeyEvent, Mods, Runtime};

    #[test]
    fn discord_messages_are_selectable_and_copyable() {
        let mut rt = Runtime::new(
            AppState::default(),
            shell::view,
            (1280.0, 800.0),
            Env::new(true, 1.0),
        );
        let clipboard = rune::clipboard::memory();
        rt.use_clipboard(&clipboard);

        rt.update(|s, _| {
            s.switch_room(Room::Discord);
            s.open_discord_guild("g-exo");
            if let Some(list) = s.discord.chat.messages_by_channel.get_mut("ch-general") {
                list.push(DiscordMessage {
                    id: "msg-copy-test".into(),
                    channel_id: "ch-general".into(),
                    author: DiscordUser {
                        id: "u-copy".into(),
                        username: "copier".into(),
                        global_name: Some("Copier".into()),
                        ..Default::default()
                    },
                    content: "SelectableDiscordPhrase".into(),
                    timestamp: "2026-11-25T12:00:00.000Z".into(),
                    ..Default::default()
                });
            }
        });
        rt.frame(0.0);
        rt.settle_all();
        rt.frame(1.0 / 60.0);

        let (row_rect, _) = rt
            .presented("dc-msg-row-msg-copy-test")
            .expect("message row presented");

        // Locate glyphs rendered inside the bottom half (message body) of the row.
        let body_y_min = row_rect[1] + row_rect[3] * 0.45;
        let mut glyphs: Vec<(f32, f32)> = rt
            .instances
            .iter()
            .filter(|i| i.params[1] == KIND_GLYPH)
            .map(|i| (i.rect[0] + i.rect[2] * 0.5, i.rect[1] + i.rect[3] * 0.5))
            .filter(|&(x, y)| {
                x >= row_rect[0]
                    && x <= row_rect[0] + row_rect[2]
                    && y >= body_y_min
                    && y <= row_rect[1] + row_rect[3]
            })
            .collect();
        glyphs.sort_by(|a, b| a.0.total_cmp(&b.0));
        assert!(
            glyphs.len() >= 6,
            "expected rendered glyphs for selectable message body, found {}",
            glyphs.len()
        );
        let first = glyphs[0];
        let last = *glyphs.last().unwrap();

        rt.pointer_move(first.0 - 4.0, first.1);
        rt.pointer_down(first.0 - 4.0, first.1);
        rt.frame(2.0 / 60.0);
        rt.pointer_move(last.0 + 6.0, last.1);
        rt.frame(3.0 / 60.0);
        rt.pointer_up(last.0 + 6.0, last.1);
        rt.frame(4.0 / 60.0);

        assert!(
            rt.selected_text()
                .as_deref()
                .is_some_and(|t| t.contains("SelectableDiscordPhrase")),
            "expected drag across Discord message to highlight text, got {:?}",
            rt.selected_text()
        );

        rt.key(KeyEvent {
            key: Key::Command('c'),
            mods: Mods {
                cmd: true,
                ..Mods::default()
            },
        });
        rt.frame(5.0 / 60.0);

        let copied = clipboard.text().unwrap_or_default();
        assert!(
            copied.contains("SelectableDiscordPhrase"),
            "expected Ctrl+C after dragging across Discord message to copy text, got {copied:?}"
        );
    }
}
