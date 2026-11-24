use crate::state::AppState;
use exo_kit::{
    FG, RADIUS_SMALL, fg_faint, fg_muted, glass_rim, hex, subtle_fill, type_body, type_label,
    type_section, type_small, type_title,
};
use exo_rpc::DiscordUser;
use rune::{Color, El, Span, TextStyle, col, el, row, text};

#[derive(Debug, Clone)]
pub struct ParsedInline {
    pub plain: String,
    pub spans: Vec<Span>,
    pub strikes: Vec<(usize, usize)>,
    pub has_spoiler: bool,
}

fn code_bg() -> Color {
    hex(0x1b1b20, 1.0)
}

fn mention_bg() -> Color {
    hex(0x1f3344, 0.9)
}

fn mention_fg() -> Color {
    hex(0x7dd3fc, 1.0)
}

fn link_fg() -> Color {
    hex(0x64d2ff, 1.0)
}

fn spoiler_hidden_bg() -> Color {
    hex(0x28282f, 1.0)
}

pub fn parse_inline(input: &str, mentions: &[DiscordUser], reveal_spoilers: bool) -> ParsedInline {
    let mut plain = String::with_capacity(input.len());
    let mut spans = Vec::new();
    let mut strikes = Vec::new();
    let mut has_spoiler = false;
    let bytes = input.as_bytes();
    let len = bytes.len();
    let mut i = 0usize;

    while i < len {
        if bytes[i] == b'`'
            && let Some(end_rel) = input[i + 1..].find('`')
            && end_rel > 0
        {
            let inner = &input[i + 1..i + 1 + end_rel];
            let start = plain.len();
            plain.push(' ');
            plain.push_str(inner);
            plain.push(' ');
            let end = plain.len();
            let mut sp = Span::new(start, end).bg(code_bg(), 2.0);
            sp.mono = true;
            sp.size = Some(12.5);
            sp.color = Some(hex(0xe5e5ea, 1.0));
            spans.push(sp);
            i = i + 2 + end_rel;
            continue;
        }

        if input[i..].starts_with("||")
            && let Some(end_rel) = input[i + 2..].find("||")
            && end_rel > 0
        {
            has_spoiler = true;
            let inner = &input[i + 2..i + 2 + end_rel];
            let start = plain.len();
            if reveal_spoilers {
                plain.push_str(inner);
                let end = plain.len();
                let mut sp = Span::new(start, end).bg(subtle_fill(), 2.0);
                sp.color = Some(FG);
                spans.push(sp);
            } else {
                plain.push_str(" spoiler ");
                let end = plain.len();
                let mut sp = Span::new(start, end).bg(spoiler_hidden_bg(), 2.0);
                sp.color = Some(fg_faint());
                spans.push(sp);
            }
            i = i + 4 + end_rel;
            continue;
        }

        if input[i..].starts_with("**")
            && let Some(end_rel) = input[i + 2..].find("**")
            && end_rel > 0
        {
            let inner = &input[i + 2..i + 2 + end_rel];
            let start = plain.len();
            plain.push_str(inner);
            let end = plain.len();
            let mut sp = Span::new(start, end);
            sp.weight = Some(680.0);
            spans.push(sp);
            i = i + 4 + end_rel;
            continue;
        }

        if input[i..].starts_with("~~")
            && let Some(end_rel) = input[i + 2..].find("~~")
            && end_rel > 0
        {
            let inner = &input[i + 2..i + 2 + end_rel];
            let start = plain.len();
            plain.push_str(inner);
            let end = plain.len();
            strikes.push((start, end));
            let mut sp = Span::new(start, end);
            sp.color = Some(fg_muted());
            spans.push(sp);
            i = i + 4 + end_rel;
            continue;
        }

        if input[i..].starts_with("__")
            && let Some(end_rel) = input[i + 2..].find("__")
            && end_rel > 0
        {
            let inner = &input[i + 2..i + 2 + end_rel];
            let start = plain.len();
            plain.push_str(inner);
            let end = plain.len();
            let mut sp = Span::new(start, end);
            sp.weight = Some(550.0);
            sp.color = Some(FG);
            spans.push(sp);
            i = i + 4 + end_rel;
            continue;
        }

        if bytes[i] == b'*'
            && !input[i..].starts_with("**")
            && let Some(end_rel) = input[i + 1..].find('*')
            && end_rel > 0
        {
            let inner = &input[i + 1..i + 1 + end_rel];
            let start = plain.len();
            plain.push_str(inner);
            let end = plain.len();
            let mut sp = Span::new(start, end);
            sp.italic = true;
            spans.push(sp);
            i = i + 2 + end_rel;
            continue;
        }

        if bytes[i] == b'<'
            && let Some(close_rel) = input[i + 1..].find('>')
        {
            let token = &input[i + 1..i + 1 + close_rel];
            if let Some(uid) = token.strip_prefix("@!").or_else(|| token.strip_prefix('@'))
                && !uid.starts_with('&')
                && uid.chars().all(|c| c.is_ascii_digit())
            {
                let label = mentions
                    .iter()
                    .find(|u| u.id == uid)
                    .map(|u| {
                        u.global_name
                            .as_deref()
                            .filter(|s| !s.is_empty())
                            .unwrap_or(&u.username)
                            .to_string()
                    })
                    .unwrap_or_else(|| "user".into());
                let start = plain.len();
                plain.push_str(&format!(" @{label} "));
                let end = plain.len();
                let mut sp = Span::new(start, end).bg(mention_bg(), 2.0);
                sp.color = Some(mention_fg());
                sp.weight = Some(560.0);
                spans.push(sp);
                i = i + 2 + close_rel;
                continue;
            }
            if let Some(cid) = token.strip_prefix('#')
                && cid.chars().all(|c| c.is_ascii_digit())
            {
                let start = plain.len();
                plain.push_str(" #channel ");
                let end = plain.len();
                let mut sp = Span::new(start, end).bg(mention_bg(), 2.0);
                sp.color = Some(mention_fg());
                spans.push(sp);
                i = i + 2 + close_rel;
                continue;
            }
            if let Some(rest) = token.strip_prefix("a:").or_else(|| token.strip_prefix(':'))
                && let Some((ename, _)) = rest.split_once(':')
                && !ename.is_empty()
            {
                let start = plain.len();
                plain.push_str(&format!(":{ename}:"));
                let end = plain.len();
                let mut sp = Span::new(start, end).bg(subtle_fill(), 2.0);
                sp.color = Some(FG);
                spans.push(sp);
                i = i + 2 + close_rel;
                continue;
            }
            if let Some(rest) = token.strip_prefix("t:") {
                let ts_part = rest.split(':').next().unwrap_or("");
                if ts_part.parse::<i64>().is_ok() {
                    let start = plain.len();
                    plain.push_str(&format!(" {ts_part}s "));
                    let end = plain.len();
                    let mut sp = Span::new(start, end).bg(subtle_fill(), 2.0);
                    sp.color = Some(fg_muted());
                    spans.push(sp);
                    i = i + 2 + close_rel;
                    continue;
                }
            }
        }

        if input[i..].starts_with("@everyone") || input[i..].starts_with("@here") {
            let word = if input[i..].starts_with("@everyone") {
                "@everyone"
            } else {
                "@here"
            };
            let start = plain.len();
            plain.push_str(&format!(" {word} "));
            let end = plain.len();
            let mut sp = Span::new(start, end).bg(mention_bg(), 2.0);
            sp.color = Some(mention_fg());
            sp.weight = Some(600.0);
            spans.push(sp);
            i += word.len();
            continue;
        }

        if input[i..].starts_with("https://") || input[i..].starts_with("http://") {
            let end_rel = input[i..]
                .find(|c: char| c.is_whitespace() || matches!(c, ')' | ']' | '>'))
                .unwrap_or(len - i);
            let url = &input[i..i + end_rel];
            let start = plain.len();
            plain.push_str(url);
            let end = plain.len();
            let mut sp = Span::new(start, end);
            sp.color = Some(link_fg());
            spans.push(sp);
            i += end_rel;
            continue;
        }

        let ch = input[i..].chars().next().unwrap_or(' ');
        plain.push(ch);
        i += ch.len_utf8();
    }

    ParsedInline {
        plain,
        spans,
        strikes,
        has_spoiler,
    }
}

fn render_inline_text(
    line: &str,
    mentions: &[DiscordUser],
    style: TextStyle,
    color: Color,
    msg_id: &str,
    reveal_spoilers: bool,
) -> El {
    let parsed = parse_inline(line, mentions, reveal_spoilers);
    let mut t = text(parsed.plain)
        .font(style)
        .color(color)
        .w_full()
        .wrap()
        .span_radius(4.0);
    if !parsed.spans.is_empty() {
        t = t.spans(parsed.spans);
    }
    for (a, b) in parsed.strikes {
        t = t.strike(a, b, None);
    }
    if parsed.has_spoiler && !reveal_spoilers {
        let mid = msg_id.to_string();
        return el()
            .w_full()
            .hit()
            .press_on_down()
            .on_press(move |s: &mut AppState| {
                s.discord.revealed_spoilers.insert(mid.clone());
            })
            .child(t);
    }
    t
}

pub fn render_markdown(
    content: &str,
    mentions: &[DiscordUser],
    msg_id: &str,
    reveal_spoilers: bool,
) -> El {
    if content.is_empty() {
        return el();
    }
    let mut blocks = col().w_full().gap(6.0);
    let lines: Vec<&str> = content.lines().collect();
    let mut idx = 0usize;

    while idx < lines.len() {
        let line = lines[idx];
        if let Some(fence_rest) = line.trim_start().strip_prefix("```") {
            let lang = fence_rest.trim().to_string();
            idx += 1;
            let mut code_lines = Vec::new();
            while idx < lines.len() && !lines[idx].trim_start().starts_with("```") {
                code_lines.push(lines[idx]);
                idx += 1;
            }
            if idx < lines.len() {
                idx += 1;
            }
            let code_body = code_lines.join("\n");
            let mut slab = col()
                .w_full()
                .px(12.0)
                .py(10.0)
                .gap(6.0)
                .radius(RADIUS_SMALL)
                .fill(hex(0x0c0c10, 1.0))
                .border(1.0, glass_rim());
            if !lang.is_empty() {
                slab = slab.child(text(lang).font(type_label()).color(fg_faint()));
            }
            let mut mono_sp = Span::new(0, code_body.len());
            mono_sp.mono = true;
            mono_sp.size = Some(12.5);
            mono_sp.color = Some(hex(0xe4e4eb, 1.0));
            slab = slab.child(
                text(code_body)
                    .font(type_small())
                    .color(hex(0xe4e4eb, 1.0))
                    .spans(vec![mono_sp])
                    .w_full()
                    .wrap(),
            );
            blocks = blocks.child(slab);
            continue;
        }

        if line.starts_with("> ") || line == ">" {
            let mut quote_lines = Vec::new();
            while idx < lines.len() && (lines[idx].starts_with("> ") || lines[idx] == ">") {
                quote_lines.push(lines[idx].strip_prefix("> ").unwrap_or(""));
                idx += 1;
            }
            let joined = quote_lines.join("\n");
            blocks = blocks.child(
                row()
                    .w_full()
                    .gap(10.0)
                    .child(el().w(3.0).radius(2.0).fill(fg_faint()))
                    .child(col().grow(1.0).min_w(0.0).child(render_inline_text(
                        &joined,
                        mentions,
                        type_body(),
                        fg_muted(),
                        msg_id,
                        reveal_spoilers,
                    ))),
            );
            continue;
        }

        if let Some(h) = line.strip_prefix("### ") {
            blocks = blocks.child(render_inline_text(
                h,
                mentions,
                type_section(),
                FG,
                msg_id,
                reveal_spoilers,
            ));
            idx += 1;
            continue;
        }
        if let Some(h) = line.strip_prefix("## ") {
            blocks = blocks.child(render_inline_text(
                h,
                mentions,
                type_title(),
                FG,
                msg_id,
                reveal_spoilers,
            ));
            idx += 1;
            continue;
        }
        if let Some(h) = line.strip_prefix("# ") {
            blocks = blocks.child(render_inline_text(
                h,
                mentions,
                type_title(),
                FG,
                msg_id,
                reveal_spoilers,
            ));
            idx += 1;
            continue;
        }

        let mut para = Vec::new();
        while idx < lines.len() {
            let cur = lines[idx];
            if cur.trim_start().starts_with("```")
                || cur.starts_with("> ")
                || cur == ">"
                || cur.starts_with("# ")
                || cur.starts_with("## ")
                || cur.starts_with("### ")
            {
                break;
            }
            para.push(cur);
            idx += 1;
        }
        let joined = para.join("\n");
        if !joined.trim().is_empty() {
            blocks = blocks.child(render_inline_text(
                &joined,
                mentions,
                type_body(),
                FG,
                msg_id,
                reveal_spoilers,
            ));
        }
    }

    blocks
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn parses_bold_code_strike_spoiler_and_mentions() {
        let mentions = vec![DiscordUser {
            id: "200001".into(),
            username: "ada".into(),
            global_name: Some("Ada".into()),
            avatar: None,
            bot: false,
        }];
        let p = parse_inline(
            "Hey <@200001> **ready** for `cargo test` ~~old~~ ||secret||",
            &mentions,
            false,
        );
        assert!(p.plain.contains("@Ada"));
        assert!(p.plain.contains("ready"));
        assert!(p.plain.contains("cargo test"));
        assert!(p.plain.contains("spoiler"));
        assert!(p.has_spoiler);
        assert_eq!(p.strikes.len(), 1);
        assert!(p.spans.len() >= 4);

        let revealed = parse_inline("||secret||", &mentions, true);
        assert_eq!(revealed.plain, "secret");
    }
}
