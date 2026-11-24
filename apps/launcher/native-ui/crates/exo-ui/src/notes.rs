use crate::devices_audio::device_btn;
use crate::state::{AppState, HostStatus};
use exo_kit::{
    BG, BadgeTone, FG, GLASS_PANEL_SIGMA, PRIMARY_FG, RADIUS_PANEL, RADIUS_PILL, RADIUS_SHEET,
    RADIUS_SMALL, SPRING_QUICK, bad, fg_faint, fg_muted, filter_pill, float_line,
    glass_control_fill, glass_panel_fill, glass_rim, good, hairline, hover_fill, icon_close,
    icon_pin, icon_plus, icon_search, primary_button, scaled, status_badge, type_body,
    type_control, type_page, type_section, type_small, type_title,
};
use exo_rpc::{
    ActionResult, METHOD_NOTES_ACT, METHOD_NOTES_LIST, METHOD_NOTES_OPEN, METHOD_NOTES_PENDING,
    METHOD_NOTES_PHOTO, METHOD_NOTES_READ, METHOD_NOTES_SAVE, METHOD_NOTES_SAVE_AS,
    METHOD_NOTES_SESSION, METHOD_NOTES_SPELL, METHOD_NOTES_TEXT_FILES, NoteInfo, NotesActReply,
    NotesListReply, NotesOpenEvent, NotesOpenReply, NotesPendingReply, NotesPhotoReply,
    NotesReadReply, NotesSaveAsReply, NotesSaveReply, NotesSpellReply, NotesTextFilesReply,
};
use rune::{
    Color, Cursor, El, Key, KeyEvent, ScrollSpec, SurfaceFit, TextEdit, TextStyle, Tx, col,
    edit_access, el, row, text, text_input,
};
use serde_json::json;
use std::collections::HashMap;
use std::path::Path;

const FRESH_PREFIX: &str = "new:";
const SAVE_DEBOUNCE_SECS: f64 = 0.45;
const MAX_PHOTO_BYTES: u64 = 25 * 1024 * 1024;

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum NoteFormat {
    Bold,
    Italic,
    Strike,
    Heading,
    List,
    Checklist,
    Code,
    Link,
}

#[derive(Debug, Clone)]
pub struct NoteDoc {
    pub key: String,
    pub info: NoteInfo,
    pub edit: TextEdit,
    pub sent: String,
    pub loaded: bool,
    pub reading: bool,
    pub problem: Option<String>,
    pub fresh: bool,
    pub preview: bool,
    pub dirty_since: Option<f64>,
}

impl NoteDoc {
    pub fn fresh(counter: u64) -> Self {
        let key = format!("{FRESH_PREFIX}{counter}");
        Self {
            key: key.clone(),
            info: NoteInfo {
                id: key,
                title: "New note".into(),
                snippet: String::new(),
                kind: "text".into(),
                name: String::new(),
                edited: String::new(),
                size: 0,
                pinned: false,
                external: false,
                unsaved: false,
                game_id: None,
                encoding: "utf-8".into(),
                eol: "crlf".into(),
                spelling: true,
            },
            edit: TextEdit::default(),
            sent: String::new(),
            loaded: true,
            reading: false,
            problem: None,
            fresh: true,
            preview: false,
            dirty_since: None,
        }
    }

    pub fn from_info(info: NoteInfo) -> Self {
        Self {
            key: info.id.clone(),
            info,
            edit: TextEdit::default(),
            sent: String::new(),
            loaded: false,
            reading: false,
            problem: None,
            fresh: false,
            preview: false,
            dirty_since: None,
        }
    }

    pub fn text(&self) -> &str {
        self.edit.text.as_str()
    }
}

#[derive(Debug, Clone, Default)]
pub struct NotesFindState {
    pub replace: bool,
    pub query: TextEdit,
    pub with: TextEdit,
    pub match_case: bool,
    pub whole_word: bool,
    pub current: usize,
}

#[derive(Debug, Clone)]
pub struct SpellCheckState {
    pub word: String,
    pub start: usize,
    pub end: usize,
    pub suggestions: Vec<String>,
}

#[derive(Debug, Clone, Default)]
pub struct NotesState {
    pub open: bool,
    pub notes: Option<Vec<NoteInfo>>,
    pub photos_base: Option<String>,
    pub tabs: Vec<String>,
    pub active: Option<String>,
    pub docs: HashMap<String, NoteDoc>,
    pub fresh_seq: u64,
    pub search_query: TextEdit,
    pub search_results: Option<Vec<NoteInfo>>,
    pub last_search_sent: String,
    pub find: Option<NotesFindState>,
    pub renaming_key: Option<String>,
    pub rename_edit: TextEdit,
    pub closing_prompt: Option<String>,
    pub spell_popup: Option<SpellCheckState>,
    pub text_files: Option<(bool, bool)>,
    pub more_menu_open: bool,
    pub note_menu_open: bool,
    pub status: Option<String>,
    pub failed: Option<String>,
    pub drop_hovered: bool,
    pub pending_open_id: Option<String>,
    pub pending_fresh: bool,
    pub session_loaded: bool,
}

impl NotesState {
    pub fn demo() -> Self {
        let info = NoteInfo {
            id: "demo-note-1".into(),
            title: "Raid callouts".into(),
            snippet: "Phase 2 stack left behind pillar".into(),
            kind: "markdown".into(),
            name: "Raid callouts.md".into(),
            edited: "2026-11-22T18:10:00Z".into(),
            size: 120,
            pinned: true,
            external: false,
            unsaved: false,
            game_id: None,
            encoding: "utf-8".into(),
            eol: "crlf".into(),
            spelling: true,
        };
        let body = "# Raid callouts\n\n- [ ] Check buffs\n- [x] Phase 2 stack left behind pillar\n";
        let mut doc = NoteDoc::from_info(info.clone());
        doc.edit.text = body.to_string();
        doc.edit.sync_invariants();
        doc.sent = body.to_string();
        doc.loaded = true;
        let mut docs = HashMap::new();
        docs.insert(info.id.clone(), doc);
        Self {
            notes: Some(vec![info.clone()]),
            tabs: vec![info.id.clone()],
            active: Some(info.id),
            docs,
            session_loaded: true,
            text_files: Some((false, false)),
            ..Self::default()
        }
    }
}

pub fn plain_line(line: &str) -> String {
    let mut s = line.trim().trim_start_matches(['#', '>']).trim();
    if let Some(rest) = s
        .strip_prefix("- [ ] ")
        .or_else(|| s.strip_prefix("- [x] "))
        .or_else(|| s.strip_prefix("- [X] "))
    {
        s = rest;
    } else if let Some(rest) = s
        .strip_prefix("- ")
        .or_else(|| s.strip_prefix("* "))
        .or_else(|| s.strip_prefix("+ "))
    {
        s = rest;
    }
    s.replace("**", "")
        .replace("__", "")
        .replace("~~", "")
        .replace('`', "")
        .trim()
        .to_string()
}

pub fn tab_name(doc: &NoteDoc) -> String {
    if doc.info.external {
        return doc.info.name.clone();
    }
    if !doc.loaded {
        return doc.info.title.clone();
    }
    for line in doc.text().split('\n') {
        let first = plain_line(line);
        if !first.is_empty() {
            return first.chars().take(40).collect();
        }
    }
    "New note".into()
}

pub fn word_count(text: &str) -> usize {
    text.split_whitespace().count()
}

pub fn caret_line_col(text: &str, caret_byte: usize) -> (usize, usize) {
    let clamped = caret_byte.min(text.len());
    let mut safe = clamped;
    while safe > 0 && !text.is_char_boundary(safe) {
        safe -= 1;
    }
    let prefix = &text[..safe];
    let line = prefix.bytes().filter(|&b| b == b'\n').count() + 1;
    let line_start = prefix.rfind('\n').map(|i| i + 1).unwrap_or(0);
    let col = prefix[line_start..].chars().count() + 1;
    (line, col)
}

pub fn encoding_label(enc: &str) -> &'static str {
    match enc {
        "utf-8-bom" => "UTF-8 with BOM",
        "utf-16le" => "UTF-16 LE",
        "utf-16be" => "UTF-16 BE",
        "ansi" => "ANSI",
        _ => "UTF-8",
    }
}

pub fn base_name(name: &str) -> &str {
    match name.rfind('.') {
        Some(dot) if dot > 0 => &name[..dot],
        _ => name,
    }
}

pub fn word_at_caret(text: &str, caret_byte: usize) -> Option<(usize, usize)> {
    let mut at = caret_byte.min(text.len());
    while at > 0 && !text.is_char_boundary(at) {
        at -= 1;
    }
    let is_word = |c: char| c.is_alphanumeric() || c == '\'' || c == '’';
    let mut start = at;
    while start > 0 {
        let prev_char = text[..start].chars().next_back()?;
        if is_word(prev_char) {
            start -= prev_char.len_utf8();
        } else {
            break;
        }
    }
    let mut end = at;
    while end < text.len() {
        let next_char = text[end..].chars().next()?;
        if is_word(next_char) {
            end += next_char.len_utf8();
        } else {
            break;
        }
    }
    while start < end && matches!(text[start..].chars().next(), Some('\'' | '’')) {
        start += text[start..].chars().next().unwrap().len_utf8();
    }
    while end > start && matches!(text[..end].chars().next_back(), Some('\'' | '’')) {
        end -= text[..end].chars().next_back().unwrap().len_utf8();
    }
    (end > start).then_some((start, end))
}

pub fn find_matches(
    text: &str,
    query: &str,
    match_case: bool,
    whole_word: bool,
) -> Vec<(usize, usize)> {
    if query.is_empty() {
        return Vec::new();
    }
    let mut ranges = Vec::new();
    let is_word_char = |c: char| c.is_alphanumeric() || c == '_';
    if match_case {
        let mut from = 0;
        while let Some(pos) = text[from..].find(query) {
            let start = from + pos;
            let end = start + query.len();
            let ok = if whole_word {
                let left_ok = text[..start]
                    .chars()
                    .next_back()
                    .is_none_or(|c| !is_word_char(c));
                let right_ok = text[end..].chars().next().is_none_or(|c| !is_word_char(c));
                left_ok && right_ok
            } else {
                true
            };
            if ok {
                ranges.push((start, end));
                if ranges.len() >= 5000 {
                    break;
                }
            }
            from = end.max(start + 1);
            while from < text.len() && !text.is_char_boundary(from) {
                from += 1;
            }
        }
    } else {
        let q_lower = query.to_lowercase();
        let q_chars: Vec<char> = q_lower.chars().collect();
        let t_chars: Vec<(usize, char)> = text.char_indices().collect();
        if q_chars.is_empty() || t_chars.len() < q_chars.len() {
            return ranges;
        }
        let mut i = 0;
        while i + q_chars.len() <= t_chars.len() {
            let mut matched = true;
            for (k, &qc) in q_chars.iter().enumerate() {
                let tc = t_chars[i + k]
                    .1
                    .to_lowercase()
                    .next()
                    .unwrap_or(t_chars[i + k].1);
                if tc != qc {
                    matched = false;
                    break;
                }
            }
            if matched {
                let start = t_chars[i].0;
                let last = t_chars[i + q_chars.len() - 1];
                let end = last.0 + last.1.len_utf8();
                let ok = if whole_word {
                    let left_ok = i == 0 || !is_word_char(t_chars[i - 1].1);
                    let right_ok = i + q_chars.len() >= t_chars.len()
                        || !is_word_char(t_chars[i + q_chars.len()].1);
                    left_ok && right_ok
                } else {
                    true
                };
                if ok {
                    ranges.push((start, end));
                    if ranges.len() >= 5000 {
                        break;
                    }
                    i += q_chars.len();
                    continue;
                }
            }
            i += 1;
        }
    }
    ranges
}

pub fn apply_format(edit: &mut TextEdit, kind: NoteFormat) {
    let text = edit.text.clone();
    let (start, end) = edit.range();
    let wrap = match kind {
        NoteFormat::Bold => Some("**"),
        NoteFormat::Italic => Some("*"),
        NoteFormat::Strike => Some("~~"),
        NoteFormat::Code => Some("`"),
        _ => None,
    };
    if let Some(w) = wrap {
        let wl = w.len();
        if start >= wl
            && end + wl <= text.len()
            && &text[start - wl..start] == w
            && &text[end..end + wl] == w
        {
            let inner = text[start..end].to_string();
            edit.set_selection(start - wl, end + wl);
            edit.paste(&inner);
            edit.set_selection(start - wl, end - wl);
        } else {
            let inner = text[start..end].to_string();
            let next = format!("{w}{inner}{w}");
            edit.set_selection(start, end);
            edit.paste(&next);
            edit.set_selection(start + wl, end + wl);
        }
        return;
    }
    if kind == NoteFormat::Link {
        let label = if end > start {
            &text[start..end]
        } else {
            "link"
        };
        let next = format!("[{label}](https://)");
        let sel_start = start + label.len() + 3;
        let sel_end = sel_start + 8;
        edit.set_selection(start, end);
        edit.paste(&next);
        edit.set_selection(sel_start, sel_end);
        return;
    }

    let from = text[..start].rfind('\n').map(|i| i + 1).unwrap_or(0);
    let to = text[end..]
        .find('\n')
        .map(|i| end + i)
        .unwrap_or(text.len());
    let slice = &text[from..to];
    let lines: Vec<&str> = slice.split('\n').collect();
    let mark = match kind {
        NoteFormat::Heading => "# ",
        NoteFormat::List => "- ",
        NoteFormat::Checklist => "- [ ] ",
        _ => "",
    };
    fn strip_prefix(line: &str) -> &str {
        if let Some(r) = line
            .strip_prefix("- [ ] ")
            .or_else(|| line.strip_prefix("- [x] "))
            .or_else(|| line.strip_prefix("- [X] "))
        {
            return r;
        }
        if let Some(r) = line
            .strip_prefix("- ")
            .or_else(|| line.strip_prefix("* "))
            .or_else(|| line.strip_prefix("+ "))
        {
            return r;
        }
        let hashes = line.bytes().take_while(|&b| b == b'#').count();
        if (1..=6).contains(&hashes) && line.as_bytes().get(hashes) == Some(&b' ') {
            return &line[hashes + 1..];
        }
        line
    }
    let all_marked = lines.iter().all(|line| match kind {
        NoteFormat::Heading => {
            let h = line.bytes().take_while(|&b| b == b'#').count();
            (1..=6).contains(&h) && line.as_bytes().get(h) == Some(&b' ')
        }
        NoteFormat::Checklist => {
            line.starts_with("- [ ] ") || line.starts_with("- [x] ") || line.starts_with("- [X] ")
        }
        NoteFormat::List => {
            (line.starts_with("- ") || line.starts_with("* ") || line.starts_with("+ "))
                && !line.starts_with("- [ ] ")
                && !line.starts_with("- [x] ")
                && !line.starts_with("- [X] ")
        }
        _ => false,
    });
    let next: String = lines
        .iter()
        .map(|line| {
            let stripped = strip_prefix(line);
            if all_marked {
                stripped.to_string()
            } else {
                format!("{mark}{stripped}")
            }
        })
        .collect::<Vec<_>>()
        .join("\n");
    edit.set_selection(from, to);
    edit.paste(&next);
    edit.set_selection(from, from + next.len());
}

pub fn insert_own_line(edit: &mut TextEdit, line: &str) {
    let text = edit.text.as_str();
    let (start, end) = edit.range();
    let need_lead = start > 0 && text.as_bytes().get(start - 1) != Some(&b'\n');
    let need_trail = end < text.len() && text.as_bytes().get(end) != Some(&b'\n');
    let mut insert = String::new();
    if need_lead {
        insert.push('\n');
    }
    insert.push_str(line);
    if need_trail {
        insert.push('\n');
    }
    let after = start + insert.len();
    edit.set_selection(start, end);
    edit.paste(&insert);
    edit.set_caret(after);
}

pub fn toggle_checklist_line(text: &str, line_idx: usize) -> String {
    let mut lines: Vec<String> = text.split('\n').map(String::from).collect();
    if let Some(line) = lines.get_mut(line_idx) {
        if let Some(pos) = line.find("[ ]") {
            line.replace_range(pos..pos + 3, "[x]");
        } else if let Some(pos) = line.find("[x]").or_else(|| line.find("[X]")) {
            line.replace_range(pos..pos + 3, "[ ]");
        }
    }
    lines.join("\n")
}

fn is_image_path(path: &Path) -> bool {
    path.extension()
        .and_then(|e| e.to_str())
        .map(|e| e.to_ascii_lowercase())
        .is_some_and(|e| matches!(e.as_str(), "png" | "jpg" | "jpeg" | "gif" | "webp" | "bmp"))
}

fn sort_notes(list: &mut [NoteInfo]) {
    list.sort_by(|a, b| {
        b.pinned
            .cmp(&a.pinned)
            .then_with(|| b.edited.cmp(&a.edited))
    });
}

impl AppState {
    pub fn open_notes(&mut self, id: Option<String>, fresh: bool) {
        self.media.captures_open = true;
        self.media.captures_settings_open = false;
        self.media.open_capture_id = None;
        self.media.snip_editor = None;
        self.media.clip_editor_open = false;
        self.media.notes.open = true;
        self.media.notes.pending_open_id = id;
        self.media.notes.pending_fresh = fresh;
        self.media.notes.status = None;
        self.media.notes.more_menu_open = false;
        self.media.notes.note_menu_open = false;
        self.load_notes_list(None);
        self.notes_text_files("state");
    }

    pub fn close_notes_to_captures(&mut self) {
        self.flush_all_dirty_notes();
        self.media.notes.open = false;
        self.media.notes.find = None;
        self.media.notes.spell_popup = None;
        self.media.notes.more_menu_open = false;
        self.media.notes.note_menu_open = false;
        self.load_captures();
    }

    pub fn check_pending_notes_on_startup(&mut self) {
        if self.status == HostStatus::Standalone {
            return;
        }
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            rpc.call::<_, NotesPendingReply>(METHOD_NOTES_PENDING, &json!({}), move |res| {
                if let Ok(reply) = res
                    && let Some(id) = reply.id.filter(|s| !s.is_empty())
                {
                    handle.update(move |s, _| s.open_notes(Some(id), false));
                }
            });
        }
    }

    pub fn apply_notes_open(&mut self, ev: NotesOpenEvent) {
        if let Some(id) = ev.id.filter(|s| !s.is_empty()) {
            self.open_notes(Some(id), false);
        } else if let Some(msg) = ev.message.filter(|s| !s.is_empty()) {
            self.notice = Some(msg);
        }
    }

    pub fn load_notes_list(&mut self, query: Option<String>) {
        if self.status == HostStatus::Standalone {
            if self.media.notes.pending_fresh {
                self.media.notes.pending_fresh = false;
                self.notes_new_note();
            } else if let Some(id) = self.media.notes.pending_open_id.take()
                && let Some(info) = self
                    .media
                    .notes
                    .notes
                    .as_ref()
                    .and_then(|l| l.iter().find(|n| n.id == id).cloned())
            {
                self.notes_show_doc(info);
            }
            if let Some(q) = query {
                let needle = q.trim().to_lowercase();
                if needle.is_empty() {
                    self.media.notes.search_results = None;
                } else {
                    let hits = self
                        .media
                        .notes
                        .notes
                        .as_deref()
                        .unwrap_or(&[])
                        .iter()
                        .filter(|n| {
                            n.title.to_lowercase().contains(&needle)
                                || n.snippet.to_lowercase().contains(&needle)
                        })
                        .cloned()
                        .collect();
                    self.media.notes.search_results = Some(hits);
                }
            }
            return;
        }

        let is_search = query.as_deref().is_some_and(|q| !q.trim().is_empty());
        let q_val = query
            .as_deref()
            .map(str::trim)
            .filter(|s| !s.is_empty())
            .map(String::from);
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            rpc.call::<_, NotesListReply>(
                METHOD_NOTES_LIST,
                &json!({ "query": q_val }),
                move |res| {
                    handle.update(move |s, _| {
                        if is_search {
                            match res {
                                Ok(reply) if reply.ok => {
                                    s.media.notes.search_results = Some(reply.notes);
                                }
                                _ => {
                                    s.media.notes.search_results = Some(Vec::new());
                                }
                            }
                            return;
                        }
                        match res {
                            Ok(reply) if reply.ok => {
                                s.media.notes.failed = None;
                                s.media.notes.photos_base = reply.photos;
                                let mut list = reply.notes;
                                sort_notes(&mut list);
                                if !s.media.notes.session_loaded {
                                    let mut open_tabs: Vec<String> = reply
                                        .tabs
                                        .into_iter()
                                        .filter(|id| list.iter().any(|n| n.id == *id))
                                        .collect();
                                    let mut front = reply
                                        .active
                                        .filter(|id| open_tabs.contains(id))
                                        .or_else(|| open_tabs.first().cloned());
                                    for id in &open_tabs {
                                        if let Some(info) = list.iter().find(|n| n.id == *id) {
                                            s.media.notes.docs.entry(id.clone()).or_insert_with(
                                                || NoteDoc::from_info(info.clone()),
                                            );
                                        }
                                    }
                                    if let Some(req_id) = s.media.notes.pending_open_id.take()
                                        && let Some(info) = list.iter().find(|n| n.id == req_id)
                                    {
                                        if !open_tabs.contains(&info.id) {
                                            open_tabs.push(info.id.clone());
                                            s.media
                                                .notes
                                                .docs
                                                .entry(info.id.clone())
                                                .or_insert_with(|| {
                                                    NoteDoc::from_info(info.clone())
                                                });
                                        }
                                        front = Some(info.id.clone());
                                    }
                                    s.media.notes.tabs = open_tabs;
                                    s.media.notes.active = front;
                                    s.media.notes.session_loaded = true;
                                } else if let Some(req_id) = s.media.notes.pending_open_id.take()
                                    && let Some(info) =
                                        list.iter().find(|n| n.id == req_id).cloned()
                                {
                                    s.notes_show_doc(info);
                                }
                                s.media.notes.notes = Some(list);
                                if s.media.notes.pending_fresh {
                                    s.media.notes.pending_fresh = false;
                                    s.notes_new_note();
                                }
                                s.notes_ensure_active_loaded();
                            }
                            Ok(reply) => {
                                s.media.notes.failed = Some(reply.message.unwrap_or_else(|| {
                                    "Exo couldn’t read the Notes folder.".into()
                                }));
                            }
                            Err(_) => {
                                s.media.notes.failed =
                                    Some("Exo couldn’t read the Notes folder.".into());
                            }
                        }
                    });
                },
            );
        }
        #[cfg(not(windows))]
        let _ = (is_search, q_val);
    }

    pub fn notes_upsert_info(&mut self, info: NoteInfo, replace_id: Option<&str>) {
        let list = self.media.notes.notes.get_or_insert_with(Vec::new);
        list.retain(|n| n.id != info.id && replace_id.is_none_or(|r| n.id != r));
        list.insert(0, info.clone());
        sort_notes(list);
        if let Some(ref mut found) = self.media.notes.search_results {
            for item in found.iter_mut() {
                if item.id == info.id {
                    *item = info.clone();
                }
            }
        }
    }

    pub fn notes_show_doc(&mut self, info: NoteInfo) {
        let id = info.id.clone();
        self.media
            .notes
            .docs
            .entry(id.clone())
            .or_insert_with(|| NoteDoc::from_info(info));
        if !self.media.notes.tabs.contains(&id) {
            self.media.notes.tabs.push(id.clone());
        }
        self.media.notes.active = Some(id);
        self.notes_ensure_active_loaded();
        self.notes_save_session();
    }

    pub fn notes_new_note(&mut self) {
        self.media.notes.fresh_seq = self.media.notes.fresh_seq.wrapping_add(1);
        let doc = NoteDoc::fresh(self.media.notes.fresh_seq);
        let key = doc.key.clone();
        self.media.notes.docs.insert(key.clone(), doc);
        self.media.notes.tabs.push(key.clone());
        self.media.notes.active = Some(key);
        self.notes_save_session();
    }

    pub fn notes_ensure_active_loaded(&mut self) {
        let Some(key) = self.media.notes.active.clone() else {
            return;
        };
        let Some(doc) = self.media.notes.docs.get_mut(&key) else {
            return;
        };
        if doc.loaded || doc.reading || doc.fresh {
            return;
        }
        let id = doc.info.id.clone();
        doc.reading = true;
        if self.status == HostStatus::Standalone {
            doc.reading = false;
            doc.loaded = true;
            return;
        }
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            let key_cb = key.clone();
            rpc.call::<_, NotesReadReply>(METHOD_NOTES_READ, &json!({ "id": id }), move |res| {
                handle.update(move |s, _| {
                    if let Some(doc) = s.media.notes.docs.get_mut(&key_cb) {
                        doc.reading = false;
                        match res {
                            Ok(reply) if reply.ok => {
                                if let Some(txt) = reply.text {
                                    doc.edit.text = txt.clone();
                                    doc.edit.sync_invariants();
                                    doc.sent = txt;
                                }
                                if let Some(info) = reply.note {
                                    doc.info = info;
                                }
                                doc.loaded = true;
                                doc.problem = None;
                            }
                            Ok(reply) => {
                                doc.problem = Some(
                                    reply
                                        .message
                                        .unwrap_or_else(|| "Exo couldn’t open this note.".into()),
                                );
                            }
                            Err(_) => {
                                doc.problem = Some("Exo couldn’t open this note.".into());
                            }
                        }
                    }
                });
            });
        }
        #[cfg(not(windows))]
        let _ = id;
    }

    pub fn notes_save_session(&mut self) {
        if !self.media.notes.session_loaded || self.status == HostStatus::Standalone {
            return;
        }
        let kept: Vec<String> = self
            .media
            .notes
            .tabs
            .iter()
            .filter(|k| !k.starts_with(FRESH_PREFIX))
            .cloned()
            .collect();
        let active = self
            .media
            .notes
            .active
            .clone()
            .filter(|k| !k.starts_with(FRESH_PREFIX));
        #[cfg(windows)]
        if let Some(rpc) = self.rpc.clone() {
            rpc.call_raw(
                METHOD_NOTES_SESSION,
                &json!({ "tabs": kept, "active": active }),
                |_| {},
            );
        }
        #[cfg(not(windows))]
        let _ = (kept, active);
    }

    pub fn notes_flush_doc(&mut self, key: &str, to_file: bool) {
        let Some(doc) = self.media.notes.docs.get_mut(key) else {
            return;
        };
        doc.dirty_since = None;
        if !doc.loaded {
            return;
        }
        let text = doc.text().to_string();
        if !to_file && text == doc.sent {
            return;
        }
        if doc.fresh && text.trim().is_empty() {
            return;
        }
        let is_fresh = doc.fresh;
        let is_md = doc.info.kind == "markdown";
        let id_arg = if is_fresh {
            None
        } else {
            Some(doc.info.id.clone())
        };
        let is_ext = doc.info.external;
        let ext_name = doc.info.name.clone();

        if self.status == HostStatus::Standalone {
            doc.sent = text;
            if is_fresh {
                doc.fresh = false;
                doc.info.title = tab_name(doc);
            }
            if to_file && is_ext {
                doc.info.unsaved = false;
                self.media.notes.status = Some(format!("Saved {ext_name}."));
            }
            return;
        }

        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            let key_owned = key.to_string();
            let text_sent = text.clone();
            let payload = if is_fresh {
                json!({ "id": serde_json::Value::Null, "text": text, "markdown": is_md })
            } else {
                json!({ "id": id_arg, "text": text, "toFile": to_file })
            };
            rpc.call::<_, NotesSaveReply>(METHOD_NOTES_SAVE, &payload, move |res| {
                handle.update(move |s, _| match res {
                    Ok(reply) if reply.ok && reply.note.is_some() => {
                        let info = reply.note.unwrap();
                        if is_fresh {
                            if let Some(mut fresh_doc) = s.media.notes.docs.remove(&key_owned) {
                                fresh_doc.key = info.id.clone();
                                fresh_doc.info = info.clone();
                                fresh_doc.sent = text_sent;
                                fresh_doc.fresh = false;
                                fresh_doc.loaded = true;
                                fresh_doc.problem = None;
                                s.media.notes.docs.insert(info.id.clone(), fresh_doc);
                            }
                            for tab in &mut s.media.notes.tabs {
                                if *tab == key_owned {
                                    *tab = info.id.clone();
                                }
                            }
                            if s.media.notes.active.as_deref() == Some(key_owned.as_str()) {
                                s.media.notes.active = Some(info.id.clone());
                            }
                            s.notes_upsert_info(info, Some(&key_owned));
                            s.notes_save_session();
                        } else {
                            if let Some(d) = s.media.notes.docs.get_mut(&key_owned) {
                                d.sent = text_sent;
                                d.info = info.clone();
                                d.problem = None;
                            }
                            s.notes_upsert_info(info, None);
                            if to_file && is_ext {
                                s.media.notes.status = Some(format!("Saved {ext_name}."));
                            }
                        }
                    }
                    Ok(reply) => {
                        if let Some(d) = s.media.notes.docs.get_mut(&key_owned) {
                            d.problem = Some(
                                reply
                                    .message
                                    .unwrap_or_else(|| "Exo couldn’t save the note.".into()),
                            );
                        }
                    }
                    Err(_) => {
                        if let Some(d) = s.media.notes.docs.get_mut(&key_owned) {
                            d.problem = Some("Exo couldn’t save the note.".into());
                        }
                    }
                });
            });
        }
        #[cfg(not(windows))]
        let _ = (id_arg, is_md, is_ext, ext_name);
    }

    pub fn flush_all_dirty_notes(&mut self) {
        let dirty_keys: Vec<String> = self
            .media
            .notes
            .docs
            .iter()
            .filter(|(_, d)| d.loaded && d.text() != d.sent)
            .map(|(k, _)| k.clone())
            .collect();
        for k in dirty_keys {
            self.notes_flush_doc(&k, false);
        }
    }

    pub fn tick_notes_autosave(&mut self, now: f64) {
        if !self.media.notes.open {
            return;
        }
        let due: Vec<String> = self
            .media
            .notes
            .docs
            .iter()
            .filter(|(_, d)| d.dirty_since.is_some_and(|t| now - t >= SAVE_DEBOUNCE_SECS))
            .map(|(k, _)| k.clone())
            .collect();
        for k in due {
            self.notes_flush_doc(&k, false);
        }
    }

    pub fn notes_drop_tab(&mut self, key: &str) {
        let pos = self
            .media
            .notes
            .tabs
            .iter()
            .position(|k| k == key)
            .unwrap_or(0);
        self.media.notes.tabs.retain(|k| k != key);
        self.media.notes.docs.remove(key);
        if self.media.notes.active.as_deref() == Some(key) {
            let next = if self.media.notes.tabs.is_empty() {
                None
            } else {
                let idx = pos.min(self.media.notes.tabs.len() - 1);
                Some(self.media.notes.tabs[idx].clone())
            };
            self.media.notes.active = next;
            self.notes_ensure_active_loaded();
        }
        self.notes_save_session();
    }

    pub fn notes_close_tab(&mut self, key: &str) {
        let meta = self.media.notes.docs.get(key).map(|d| {
            (
                d.fresh,
                d.info.external,
                d.info.unsaved || (d.info.external && d.text() != d.sent),
                d.loaded && d.text().trim().is_empty(),
                d.info.id.clone(),
            )
        });
        if let Some((fresh, external, unsaved, empty_own, id)) = meta {
            self.notes_flush_doc(key, false);
            if external && unsaved {
                self.media.notes.closing_prompt = Some(key.to_string());
                return;
            }
            if !fresh && !external && empty_own {
                self.notes_act_raw(&id, "delete", None, false);
                if let Some(ref mut list) = self.media.notes.notes {
                    list.retain(|n| n.id != id);
                }
            }
        }
        self.notes_drop_tab(key);
    }

    pub fn notes_open_file(&mut self) {
        if self.status == HostStatus::Standalone {
            return;
        }
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            rpc.call::<_, NotesOpenReply>(METHOD_NOTES_OPEN, &json!({}), move |res| {
                handle.update(move |s, _| match res {
                    Ok(reply) if reply.cancelled => {}
                    Ok(reply) if reply.ok && reply.id.is_some() => {
                        s.media.notes.pending_open_id = reply.id;
                        s.load_notes_list(None);
                    }
                    Ok(reply) => {
                        s.media.notes.status = Some(
                            reply
                                .message
                                .unwrap_or_else(|| "Exo couldn’t open the file.".into()),
                        );
                    }
                    Err(_) => {
                        s.media.notes.status = Some("Exo couldn’t open the file.".into());
                    }
                });
            });
        }
    }

    pub fn notes_save_as(&mut self, key: &str) {
        self.notes_flush_doc(key, false);
        let Some(doc) = self.media.notes.docs.get(key) else {
            return;
        };
        if doc.fresh {
            return;
        }
        let id = doc.info.id.clone();
        let text = doc.text().to_string();
        let is_ext = doc.info.external;
        let key_owned = key.to_string();
        if self.status == HostStatus::Standalone {
            return;
        }
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            rpc.call::<_, NotesSaveAsReply>(
                METHOD_NOTES_SAVE_AS,
                &json!({ "id": id, "text": text }),
                move |res| {
                    handle.update(move |s, _| match res {
                        Ok(reply) if reply.cancelled => {}
                        Ok(reply) if reply.ok && reply.id.is_some() => {
                            let new_id = reply.id.unwrap();
                            if is_ext {
                                s.notes_drop_tab(&key_owned);
                            }
                            s.media.notes.pending_open_id = Some(new_id);
                            s.media.notes.status = Some("Saved file.".into());
                            s.load_notes_list(None);
                        }
                        Ok(reply) => {
                            s.media.notes.status = Some(
                                reply
                                    .message
                                    .unwrap_or_else(|| "Exo couldn’t save the file.".into()),
                            );
                        }
                        Err(_) => {
                            s.media.notes.status = Some("Exo couldn’t save the file.".into());
                        }
                    });
                },
            );
        }
        #[cfg(not(windows))]
        let _ = (id, text, is_ext, key_owned);
    }

    pub fn notes_act(&mut self, key: &str, action: &'static str, name: Option<String>) {
        self.media.notes.status = None;
        self.media.notes.note_menu_open = false;
        let is_fresh = self.media.notes.docs.get(key).is_some_and(|d| d.fresh);
        if is_fresh {
            if matches!(action, "markdown" | "text")
                && let Some(d) = self.media.notes.docs.get_mut(key)
            {
                d.info.kind = action.into();
            }
            return;
        }
        self.notes_flush_doc(key, false);
        let id = self
            .media
            .notes
            .docs
            .get(key)
            .map(|d| d.info.id.clone())
            .unwrap_or_else(|| key.to_string());
        if self.status == HostStatus::Standalone {
            if action == "delete" {
                if let Some(ref mut list) = self.media.notes.notes {
                    list.retain(|n| n.id != id);
                }
                self.notes_drop_tab(key);
            } else if let Some(d) = self.media.notes.docs.get_mut(key) {
                match action {
                    "pin" => d.info.pinned = true,
                    "unpin" => d.info.pinned = false,
                    "markdown" => d.info.kind = "markdown".into(),
                    "text" => d.info.kind = "text".into(),
                    "rename" => {
                        if let Some(n) = name {
                            d.info.name = n.clone();
                            d.info.title = n;
                        }
                    }
                    _ => {}
                }
                let updated = d.info.clone();
                self.notes_upsert_info(updated, None);
            }
            return;
        }
        self.notes_act_raw(&id, action, name, true);
    }

    fn notes_act_raw(
        &mut self,
        id: &str,
        action: &'static str,
        name: Option<String>,
        update_ui: bool,
    ) {
        let id_owned = id.to_string();
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            rpc.call::<_, NotesActReply>(
                METHOD_NOTES_ACT,
                &json!({ "id": id_owned, "action": action, "name": name }),
                move |res| {
                    if !update_ui {
                        return;
                    }
                    handle.update(move |s, _| match res {
                        Ok(reply) if reply.ok => {
                            if action == "delete" {
                                if let Some(ref mut list) = s.media.notes.notes {
                                    list.retain(|n| n.id != id_owned);
                                }
                                s.notes_drop_tab(&id_owned);
                            } else if let Some(info) = reply.note {
                                if let Some(d) = s.media.notes.docs.get_mut(&id_owned) {
                                    d.info = info.clone();
                                }
                                s.notes_upsert_info(info, None);
                            }
                        }
                        Ok(reply) => {
                            s.media.notes.status = Some(
                                reply
                                    .message
                                    .unwrap_or_else(|| "Exo couldn’t update the note.".into()),
                            );
                        }
                        Err(_) => {
                            s.media.notes.status = Some("Exo couldn’t update the note.".into());
                        }
                    });
                },
            );
        }
        #[cfg(not(windows))]
        let _ = (id_owned, action, name, update_ui);
    }

    pub fn notes_open_folder(&mut self) {
        self.media.notes.more_menu_open = false;
        if self.status == HostStatus::Standalone {
            return;
        }
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            rpc.call::<_, NotesActReply>(
                METHOD_NOTES_ACT,
                &json!({ "id": "", "action": "folder", "name": serde_json::Value::Null }),
                move |res| {
                    if let Ok(r) = res
                        && !r.ok
                    {
                        handle.update(move |s, _| {
                            s.media.notes.status =
                                Some(r.message.unwrap_or_else(|| {
                                    "Exo couldn’t open the Notes folder.".into()
                                }));
                        });
                    }
                },
            );
        }
    }

    pub fn notes_text_files(&mut self, action: &'static str) {
        self.media.notes.more_menu_open = false;
        if self.status == HostStatus::Standalone {
            return;
        }
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            rpc.call::<_, NotesTextFilesReply>(
                METHOD_NOTES_TEXT_FILES,
                &json!({ "action": action }),
                move |res| {
                    handle.update(move |s, _| match res {
                        Ok(reply) => {
                            s.media.notes.text_files = Some((reply.offered, reply.is_default));
                            if let Some(msg) = reply.message {
                                s.media.notes.status = Some(msg);
                            }
                        }
                        Err(_) => {
                            if action != "state" {
                                s.media.notes.status = Some(
                                    "Exo couldn’t change how text files open. Try again.".into(),
                                );
                            }
                        }
                    });
                },
            );
        }
        #[cfg(not(windows))]
        let _ = action;
    }

    pub fn notes_add_photo(&mut self, key: &str, data_b64: Option<String>) {
        let Some(doc) = self.media.notes.docs.get(key) else {
            return;
        };
        if doc.info.external {
            return;
        }
        self.media.notes.status = None;
        if self.status == HostStatus::Standalone {
            if let Some(d) = self.media.notes.docs.get_mut(key) {
                d.info.kind = "markdown".into();
                insert_own_line(&mut d.edit, "![](Photos/demo.png)");
            }
            return;
        }
        let key_owned = key.to_string();
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            rpc.call::<_, NotesPhotoReply>(
                METHOD_NOTES_PHOTO,
                &json!({ "data": data_b64 }),
                move |res| {
                    handle.update(move |s, _| match res {
                        Ok(reply) if reply.cancelled => {}
                        Ok(reply) if reply.ok && reply.src.is_some() => {
                            let src = reply.src.unwrap();
                            let need_md = s
                                .media
                                .notes
                                .docs
                                .get(&key_owned)
                                .is_some_and(|d| d.info.kind != "markdown");
                            if need_md {
                                s.notes_act(&key_owned, "markdown", None);
                            }
                            if let Some(d) = s.media.notes.docs.get_mut(&key_owned) {
                                insert_own_line(&mut d.edit, &format!("![]({src})"));
                                d.dirty_since = Some(0.0);
                            }
                            s.notes_flush_doc(&key_owned, false);
                        }
                        Ok(reply) => {
                            s.media.notes.status = Some(
                                reply
                                    .message
                                    .unwrap_or_else(|| "Exo couldn’t add the picture.".into()),
                            );
                        }
                        Err(_) => {
                            s.media.notes.status = Some("Exo couldn’t add the picture.".into());
                        }
                    });
                },
            );
        }
        #[cfg(not(windows))]
        let _ = (key_owned, data_b64);
    }

    pub fn notes_handle_file_drop(&mut self, paths: &[std::path::PathBuf]) {
        self.media.notes.drop_hovered = false;
        let Some(active) = self.media.notes.active.clone() else {
            return;
        };
        let Some(img_path) = paths.iter().find(|p| is_image_path(p)) else {
            return;
        };
        match std::fs::metadata(img_path) {
            Ok(meta) if meta.len() > MAX_PHOTO_BYTES => {
                self.media.notes.status = Some("That picture is over 25 MB.".into());
                return;
            }
            Ok(_) => {}
            Err(_) => {
                self.media.notes.status = Some("The picture could not be read.".into());
                return;
            }
        }
        match std::fs::read(img_path) {
            Ok(bytes) => {
                let b64 = exo_rpc::base64::encode(&bytes);
                self.notes_add_photo(&active, Some(b64));
            }
            Err(_) => {
                self.media.notes.status = Some("The picture could not be read.".into());
            }
        }
    }

    pub fn notes_check_spell_at_caret(&mut self) {
        let Some(active) = self.media.notes.active.clone() else {
            return;
        };
        let Some(doc) = self.media.notes.docs.get(&active) else {
            return;
        };
        if !doc.info.spelling || doc.preview {
            return;
        }
        let (caret, _) = doc.edit.range();
        let Some((start, end)) = word_at_caret(doc.text(), caret) else {
            return;
        };
        let word = doc.text()[start..end].to_string();
        if self.status == HostStatus::Standalone {
            return;
        }
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            let w_cb = word.clone();
            rpc.call::<_, NotesSpellReply>(
                METHOD_NOTES_SPELL,
                &json!({ "word": word, "add": false }),
                move |res| {
                    if let Ok(reply) = res
                        && reply.wrong
                    {
                        handle.update(move |s, _| {
                            s.media.notes.spell_popup = Some(SpellCheckState {
                                word: w_cb,
                                start,
                                end,
                                suggestions: reply.suggestions,
                            });
                        });
                    }
                },
            );
        }
    }

    pub fn notes_add_word_to_dict(&mut self, word: String) {
        self.media.notes.spell_popup = None;
        if self.status == HostStatus::Standalone {
            return;
        }
        #[cfg(windows)]
        if let Some(rpc) = self.rpc.clone() {
            rpc.call_raw(
                METHOD_NOTES_SPELL,
                &json!({ "word": word, "add": true }),
                |_| {},
            );
        }
        #[cfg(not(windows))]
        let _ = word;
    }

    pub fn notes_open_find(&mut self, replace: bool) {
        let selected = self
            .media
            .notes
            .active
            .as_deref()
            .and_then(|k| self.media.notes.docs.get(k))
            .and_then(|d| {
                let s = d.edit.selected();
                (!s.is_empty() && s.len() < 200 && !s.contains('\n')).then(|| s.to_string())
            });
        let mut find = self.media.notes.find.take().unwrap_or_default();
        find.replace = replace;
        if let Some(sel) = selected {
            find.query.text = sel;
            find.query.sync_invariants();
        }
        find.current = 0;
        self.media.notes.find = Some(find);
    }

    pub fn notes_find_step(&mut self, by: isize) {
        let Some(active) = self.media.notes.active.clone() else {
            return;
        };
        let (ranges, next_idx) = {
            let Some(doc) = self.media.notes.docs.get(&active) else {
                return;
            };
            let Some(ref mut find) = self.media.notes.find else {
                return;
            };
            let ranges = find_matches(
                doc.text(),
                find.query.text.as_str(),
                find.match_case,
                find.whole_word,
            );
            if ranges.is_empty() {
                return;
            }
            let count = ranges.len() as isize;
            let cur = (find.current.min(ranges.len() - 1)) as isize;
            let next = ((cur + by).rem_euclid(count)) as usize;
            find.current = next;
            (ranges, next)
        };
        if let Some(&(a, b)) = ranges.get(next_idx)
            && let Some(doc) = self.media.notes.docs.get_mut(&active)
        {
            doc.edit.set_selection(a, b);
        }
    }

    pub fn notes_replace_one(&mut self, now: f64) {
        let Some(active) = self.media.notes.active.clone() else {
            return;
        };
        let Some((range, replacement)) = ({
            let doc = self.media.notes.docs.get(&active);
            let find = self.media.notes.find.as_ref();
            match (doc, find) {
                (Some(d), Some(f)) => {
                    let ranges =
                        find_matches(d.text(), f.query.text.as_str(), f.match_case, f.whole_word);
                    let idx = if ranges.is_empty() {
                        0
                    } else {
                        f.current.min(ranges.len() - 1)
                    };
                    ranges
                        .get(idx)
                        .copied()
                        .map(|r| (r, f.with.text.to_string()))
                }
                _ => None,
            }
        }) else {
            return;
        };
        if let Some(doc) = self.media.notes.docs.get_mut(&active) {
            doc.edit.set_selection(range.0, range.1);
            doc.edit.paste(&replacement);
            doc.dirty_since = Some(now);
        }
    }

    pub fn notes_replace_all(&mut self, now: f64) {
        let Some(active) = self.media.notes.active.clone() else {
            return;
        };
        let Some((ranges, replacement)) = ({
            let doc = self.media.notes.docs.get(&active);
            let find = self.media.notes.find.as_ref();
            match (doc, find) {
                (Some(d), Some(f)) => {
                    let ranges =
                        find_matches(d.text(), f.query.text.as_str(), f.match_case, f.whole_word);
                    (!ranges.is_empty()).then(|| (ranges, f.with.text.to_string()))
                }
                _ => None,
            }
        }) else {
            return;
        };
        let count = ranges.len();
        if let Some(doc) = self.media.notes.docs.get_mut(&active) {
            let src = doc.text().to_string();
            let mut out = String::with_capacity(src.len());
            let mut at = 0;
            for (s, e) in ranges {
                out.push_str(&src[at..s]);
                out.push_str(&replacement);
                at = e;
            }
            out.push_str(&src[at..]);
            doc.edit.set_selection(0, src.len());
            doc.edit.paste(&out);
            doc.edit.set_caret(0);
            doc.dirty_since = Some(now);
        }
        self.media.notes.status = Some(format!("Replaced {count}."));
    }

    pub fn handle_notes_key(&mut self, ev: &KeyEvent, tx: &mut Tx) -> bool {
        if !self.media.captures_open || !self.media.notes.open {
            return false;
        }
        if ev.key == Key::Escape {
            if self.media.notes.spell_popup.is_some() {
                self.media.notes.spell_popup = None;
                return true;
            }
            if self.media.notes.closing_prompt.is_some() {
                self.media.notes.closing_prompt = None;
                return true;
            }
            if self.media.notes.renaming_key.is_some() {
                self.media.notes.renaming_key = None;
                return true;
            }
            if self.media.notes.find.is_some() {
                self.media.notes.find = None;
                return true;
            }
            return false;
        }
        if ev.mods.ctrl && !ev.mods.alt && !ev.mods.logo {
            if ev.key == Key::Tab {
                let len = self.media.notes.tabs.len();
                if len >= 2 {
                    let cur = self
                        .media
                        .notes
                        .active
                        .as_deref()
                        .and_then(|a| self.media.notes.tabs.iter().position(|k| k == a))
                        .unwrap_or(0);
                    let next = if ev.mods.shift {
                        (cur + len - 1) % len
                    } else {
                        (cur + 1) % len
                    };
                    self.media.notes.active = Some(self.media.notes.tabs[next].clone());
                    self.notes_ensure_active_loaded();
                    return true;
                }
            }
            if ev.is_command('n') {
                self.notes_new_note();
                return true;
            }
            if ev.is_command('o') {
                self.notes_open_file();
                return true;
            }
            if ev.is_command('s') {
                if let Some(active) = self.media.notes.active.clone() {
                    if ev.mods.shift {
                        self.notes_save_as(&active);
                    } else {
                        let is_ext = self
                            .media
                            .notes
                            .docs
                            .get(&active)
                            .is_some_and(|d| d.info.external);
                        self.notes_flush_doc(&active, is_ext);
                    }
                }
                return true;
            }
            if ev.is_command('w') {
                if let Some(active) = self.media.notes.active.clone() {
                    self.notes_close_tab(&active);
                }
                return true;
            }
            if ev.is_command('f') {
                self.notes_open_find(false);
                return true;
            }
            if ev.is_command('h') {
                self.notes_open_find(true);
                return true;
            }
            if ev.is_command('b') || ev.is_command('i') {
                let fmt = if ev.is_command('b') {
                    NoteFormat::Bold
                } else {
                    NoteFormat::Italic
                };
                if let Some(active) = self.media.notes.active.clone()
                    && let Some(doc) = self.media.notes.docs.get_mut(&active)
                    && doc.info.kind == "markdown"
                    && !doc.preview
                {
                    apply_format(&mut doc.edit, fmt);
                    doc.dirty_since = Some(tx.clock());
                    return true;
                }
            }
        }
        false
    }
}

fn render_markdown_preview(s: &AppState, doc: &NoteDoc, scale: f32) -> El {
    let lines: Vec<&str> = doc.text().split('\n').collect();
    let mut blocks: Vec<El> = Vec::new();
    let mut i = 0;
    let active_key = doc.key.clone();

    while i < lines.len() {
        let line = lines[i];
        let trimmed = line.trim();
        if trimmed.is_empty() {
            i += 1;
            continue;
        }
        if trimmed.starts_with("```") || trimmed.starts_with("~~~") {
            let fence = &trimmed[..3];
            let mut body = Vec::new();
            i += 1;
            while i < lines.len() && !lines[i].trim_start().starts_with(fence) {
                body.push(lines[i]);
                i += 1;
            }
            i += 1;
            blocks.push(
                el().w_full()
                    .padding(12.0)
                    .radius(RADIUS_SMALL)
                    .fill(glass_control_fill())
                    .border(1.0, hairline())
                    .child(
                        text(body.join("\n"))
                            .font(TextStyle::new(scaled(12.0), 400.0))
                            .color(FG),
                    ),
            );
            continue;
        }
        let hashes = trimmed.bytes().take_while(|&b| b == b'#').count();
        if (1..=6).contains(&hashes) && trimmed.as_bytes().get(hashes) == Some(&b' ') {
            let heading = trimmed[hashes + 1..].trim_end_matches('#').trim();
            let size = match hashes {
                1 => scaled(22.0),
                2 => scaled(18.0),
                3 => scaled(15.0),
                _ => scaled(13.0),
            };
            blocks.push(
                text(heading.to_string())
                    .font(TextStyle::new(size, 650.0))
                    .color(FG),
            );
            i += 1;
            continue;
        }
        if trimmed.len() >= 3
            && (trimmed.chars().all(|c| c == '-' || c == ' ')
                || trimmed.chars().all(|c| c == '*' || c == ' ')
                || trimmed.chars().all(|c| c == '_' || c == ' '))
        {
            blocks.push(el().w_full().h(1.0).my(6.0).fill(hairline()));
            i += 1;
            continue;
        }
        if trimmed.starts_with('>') {
            let mut quote_lines = Vec::new();
            while i < lines.len() && lines[i].trim_start().starts_with('>') {
                quote_lines.push(
                    lines[i]
                        .trim_start()
                        .trim_start_matches('>')
                        .trim_start()
                        .to_string(),
                );
                i += 1;
            }
            blocks.push(
                el().w_full()
                    .pl(12.0)
                    .py(4.0)
                    .border_sides([0.0, 0.0, 0.0, 2.0], float_line())
                    .child(
                        text(quote_lines.join("\n"))
                            .font(type_body())
                            .color(fg_muted()),
                    ),
            );
            continue;
        }
        if trimmed.starts_with("- [ ] ")
            || trimmed.starts_with("- [x] ")
            || trimmed.starts_with("- [X] ")
        {
            let checked = !trimmed.starts_with("- [ ] ");
            let label = trimmed[6..].to_string();
            let line_idx = i;
            let k = active_key.clone();
            blocks.push(
                row()
                    .key(format!("note-task-{i}"))
                    .gap(8.0)
                    .center_y()
                    .cursor(Cursor::Pointer)
                    .hit()
                    .on_press_tx(move |s: &mut AppState, tx| {
                        if let Some(d) = s.media.notes.docs.get_mut(&k) {
                            let next = toggle_checklist_line(d.text(), line_idx);
                            d.edit.text = next;
                            d.edit.sync_invariants();
                            d.dirty_since = Some(tx.clock());
                        }
                    })
                    .child(
                        el().size(16.0, 16.0)
                            .radius(4.0)
                            .fill(if checked {
                                good()
                            } else {
                                glass_control_fill()
                            })
                            .border(1.0, glass_rim())
                            .center()
                            .children(checked.then(|| exo_kit::icon_check(10.0, PRIMARY_FG))),
                    )
                    .child(text(label).font(type_body()).color(if checked {
                        fg_muted()
                    } else {
                        FG
                    })),
            );
            i += 1;
            continue;
        }
        if let Some(rest) = trimmed
            .strip_prefix("- ")
            .or_else(|| trimmed.strip_prefix("* "))
            .or_else(|| trimmed.strip_prefix("+ "))
        {
            blocks.push(
                row()
                    .gap(8.0)
                    .pl(8.0)
                    .child(text("•").font(type_body()).color(fg_muted()))
                    .child(text(plain_line(rest)).font(type_body()).color(FG)),
            );
            i += 1;
            continue;
        }
        if let Some(img_inner) = trimmed.strip_prefix("![").and_then(|s| s.strip_suffix(')'))
            && let Some((_, src)) = img_inner.split_once("](")
            && !doc.info.external
            && let Some(base) = s.media.notes.photos_base.as_deref()
            && let Some(file) = src.strip_prefix("Photos/")
        {
            let url = format!("{base}{file}");
            if let Some(photo) = s.capture_thumb_image(&url, scale) {
                let lb_url = url.clone();
                blocks.push(
                    el().key(format!("note-photo-{i}"))
                        .role("button")
                        .cursor(Cursor::Pointer)
                        .hit()
                        .w_full()
                        .max_w(540.0)
                        .h(300.0)
                        .radius(RADIUS_SMALL)
                        .clip_rounded(RADIUS_SMALL)
                        .pixels_fit(photo.pixels(), SurfaceFit::Fit)
                        .on_press(move |s: &mut AppState| {
                            s.media.lightbox_urls = vec![lb_url.clone()];
                            s.media.lightbox.open(0);
                        }),
                );
            } else {
                blocks.push(
                    text(format!("[Image: {file}]"))
                        .font(type_small())
                        .color(fg_muted()),
                );
            }
            i += 1;
            continue;
        }
        blocks.push(text(plain_line(trimmed)).font(type_body()).color(FG));
        i += 1;
    }

    col()
        .key("note-preview-scroll")
        .w_full()
        .grow(1.0)
        .min_h(0.0)
        .padding(24.0)
        .gap(10.0)
        .scroll(ScrollSpec {
            stick_bottom: false,
        })
        .children(blocks)
}

pub fn view(s: &AppState, scale: f32) -> El {
    let notes = &s.media.notes;
    let active_doc = notes.active.as_deref().and_then(|k| notes.docs.get(k));

    let more_items = {
        let mut items = vec![("folder", "Show the Notes folder")];
        match notes.text_files {
            Some((true, is_default)) => {
                if !is_default {
                    items.push(("settings", "Choose Exo in Default apps"));
                }
                items.push(("withdraw", "Stop opening text files with Exo"));
            }
            _ => {
                items.push(("offer", "Open text files with Exo"));
            }
        }
        items
    };

    let head = row()
        .w_full()
        .px(20.0)
        .pt(18.0)
        .pb(12.0)
        .justify_between()
        .center_y()
        .border_sides([0.0, 0.0, 1.0, 0.0], hairline())
        .child(text("Notes").font(type_page()).color(FG))
        .child(
            row()
                .gap(8.0)
                .center_y()
                .child(
                    primary_button("notes-new", "New note", Some(icon_plus(14.0, PRIMARY_FG)))
                        .on_press(|s: &mut AppState| s.notes_new_note()),
                )
                .child(
                    device_btn("notes-open", "Open", false)
                        .on_press(|s: &mut AppState| s.notes_open_file()),
                )
                .child(
                    device_btn("notes-more", "⋯", false).on_press(|s: &mut AppState| {
                        s.media.notes.more_menu_open = !s.media.notes.more_menu_open;
                        s.media.notes.note_menu_open = false;
                    }),
                )
                .child(
                    device_btn("notes-back-captures", "Captures", false)
                        .on_press(|s: &mut AppState| s.close_notes_to_captures()),
                )
                .child(
                    el().key("notes-close")
                        .role("button")
                        .label("Close Captures")
                        .cursor(Cursor::Pointer)
                        .focusable(true)
                        .hit()
                        .press_on_down()
                        .size(32.0, 32.0)
                        .radius(RADIUS_PILL)
                        .fill(glass_control_fill())
                        .border(1.0, glass_rim())
                        .hover(hover_fill())
                        .center()
                        .on_press(|s: &mut AppState| {
                            s.flush_all_dirty_notes();
                            s.close_captures();
                        })
                        .child(icon_close(16.0, FG)),
                ),
        );

    let listed = notes
        .search_results
        .as_deref()
        .or(notes.notes.as_deref())
        .unwrap_or(&[]);
    let searched = notes.search_results.is_some();

    let mut sidebar = col()
        .w(260.0)
        .h_full()
        .padding(12.0)
        .gap(8.0)
        .border_sides([0.0, 1.0, 0.0, 0.0], hairline())
        .child(
            row()
                .w_full()
                .h(34.0)
                .px(10.0)
                .gap(6.0)
                .center_y()
                .radius(RADIUS_PILL)
                .fill(glass_control_fill())
                .border(1.0, glass_rim())
                .cursor(Cursor::Text)
                .child(icon_search(13.0, fg_muted()))
                .child(
                    text_input(&notes.search_query, type_body())
                        .key("notes-search-input")
                        .label("Search notes")
                        .single_line()
                        .grow(1.0)
                        .min_w(0.0)
                        .color(FG)
                        .placeholder("Search notes", fg_faint())
                        .on_edit(edit_access(|s: &mut AppState| {
                            &mut s.media.notes.search_query
                        }))
                        .on_input_change(|s: &mut AppState, _| {
                            let q = s.media.notes.search_query.text.to_string();
                            if q != s.media.notes.last_search_sent {
                                s.media.notes.last_search_sent = q.clone();
                                if q.trim().is_empty() {
                                    s.media.notes.search_results = None;
                                } else {
                                    s.load_notes_list(Some(q));
                                }
                            }
                        }),
                ),
        );

    if let Some(ref fail) = notes.failed {
        sidebar = sidebar
            .child(text(fail.clone()).font(type_small()).color(bad()))
            .child(
                device_btn("notes-retry", "Try again", false)
                    .on_press(|s: &mut AppState| s.load_notes_list(None)),
            );
    } else if notes.notes.is_none() {
        sidebar = sidebar.child(
            text("Reading your notes…")
                .font(type_small())
                .color(fg_muted()),
        );
    } else if searched && listed.is_empty() {
        sidebar = sidebar.child(text("No notes match").font(type_small()).color(fg_muted()));
    }

    let mut rows_col = col()
        .key("notes-list-scroll")
        .w_full()
        .grow(1.0)
        .min_h(0.0)
        .gap(2.0)
        .scroll(ScrollSpec {
            stick_bottom: false,
        });

    for note in listed {
        let on = notes.active.as_deref() == Some(note.id.as_str());
        let title = if note.external {
            note.name.clone()
        } else {
            note.title.clone()
        };
        let game = note
            .game_id
            .as_deref()
            .and_then(|gid| s.games.iter().find(|g| g.id == gid))
            .map(|g| g.title.as_str());
        let mut sub_parts = Vec::new();
        if !note.edited.is_empty() {
            sub_parts.push(note.edited.get(..10).unwrap_or(&note.edited));
        }
        if let Some(g) = game {
            sub_parts.push(g);
        }
        if !note.snippet.is_empty() {
            sub_parts.push(note.snippet.as_str());
        }
        let info_clone = note.clone();
        rows_col = rows_col.child(
            col()
                .key(format!("note-row-{}", note.id))
                .role("button")
                .focusable(true)
                .hit()
                .press_on_down()
                .cursor(Cursor::Pointer)
                .w_full()
                .px(10.0)
                .py(8.0)
                .gap(2.0)
                .radius(RADIUS_SMALL)
                .fill(if on { hover_fill() } else { Color::TRANSPARENT })
                .hover(hover_fill())
                .on_press(move |s: &mut AppState| s.notes_show_doc(info_clone.clone()))
                .child(
                    row()
                        .gap(6.0)
                        .center_y()
                        .children(note.pinned.then(|| icon_pin(11.0, fg_muted())))
                        .child(text(title).font(type_body()).color(FG).truncate()),
                )
                .child(
                    text(sub_parts.join(" · "))
                        .font(type_small())
                        .color(fg_muted())
                        .truncate(),
                ),
        );
    }
    sidebar = sidebar.child(rows_col);

    let tab_bar = row()
        .w_full()
        .min_h(38.0)
        .px(10.0)
        .py(4.0)
        .gap(4.0)
        .center_y()
        .border_sides([0.0, 0.0, 1.0, 0.0], hairline())
        .children(notes.tabs.iter().filter_map(|key| {
            let doc = notes.docs.get(key)?;
            let is_active = notes.active.as_deref() == Some(key.as_str());
            let name = tab_name(doc);
            let k_pick = key.clone();
            let k_close = key.clone();
            let renaming_this = notes.renaming_key.as_deref() == Some(key.as_str());
            let tab_el = if renaming_this {
                let k_ren = key.clone();
                let k_ren_key = key.clone();
                row()
                    .key(format!("note-tab-{key}"))
                    .h(30.0)
                    .px(8.0)
                    .center_y()
                    .radius(RADIUS_SMALL)
                    .fill(hover_fill())
                    .child(
                        text_input(&notes.rename_edit, type_small())
                            .key("note-rename-field")
                            .label("Note name")
                            .single_line()
                            .w(140.0)
                            .color(FG)
                            .on_edit(edit_access(|s: &mut AppState| {
                                &mut s.media.notes.rename_edit
                            }))
                            .on_submit(move |s: &mut AppState, _| {
                                let new_name = s.media.notes.rename_edit.text.trim().to_string();
                                s.media.notes.renaming_key = None;
                                if !new_name.is_empty() {
                                    s.notes_act(&k_ren, "rename", Some(new_name));
                                }
                            })
                            .on_input_key(move |s: &mut AppState, ev, _| {
                                if ev.key == Key::Escape {
                                    s.media.notes.renaming_key = None;
                                    return true;
                                }
                                let _ = &k_ren_key;
                                false
                            }),
                    )
            } else {
                row()
                    .key(format!("note-tab-{key}"))
                    .h(30.0)
                    .px(10.0)
                    .gap(6.0)
                    .center_y()
                    .radius(RADIUS_SMALL)
                    .fill(if is_active {
                        hover_fill()
                    } else {
                        Color::TRANSPARENT
                    })
                    .hover(hover_fill())
                    .cursor(Cursor::Pointer)
                    .hit()
                    .on_press(move |s: &mut AppState| {
                        s.media.notes.active = Some(k_pick.clone());
                        s.notes_ensure_active_loaded();
                        s.notes_save_session();
                    })
                    .child(text(name).font(type_small()).color(FG))
                    .children(
                        doc.info
                            .unsaved
                            .then(|| el().size(6.0, 6.0).radius(3.0).fill(FG)),
                    )
                    .child(
                        el().key(format!("note-tab-close-{key}"))
                            .role("button")
                            .cursor(Cursor::Pointer)
                            .hit()
                            .size(18.0, 18.0)
                            .radius(RADIUS_PILL)
                            .hover(glass_control_fill())
                            .center()
                            .on_press(move |s: &mut AppState| s.notes_close_tab(&k_close))
                            .child(icon_close(10.0, fg_muted())),
                    )
            };
            Some(tab_el)
        }));

    let mut main_area = col()
        .grow(1.0)
        .min_w(0.0)
        .h_full()
        .children((!notes.tabs.is_empty()).then_some(tab_bar));

    if let Some(doc) = active_doc {
        let is_md = doc.info.kind == "markdown";
        let own = !doc.info.external;
        let doc_key = doc.key.clone();

        let mut toolbar = row()
            .w_full()
            .px(14.0)
            .py(8.0)
            .gap(6.0)
            .center_y()
            .border_sides([0.0, 0.0, 1.0, 0.0], hairline());

        if !doc.preview && (is_md || own) {
            if is_md {
                for (fmt, key, lbl) in [
                    (NoteFormat::Bold, "nf-b", "B"),
                    (NoteFormat::Italic, "nf-i", "I"),
                    (NoteFormat::Strike, "nf-s", "S"),
                    (NoteFormat::Heading, "nf-h", "H"),
                    (NoteFormat::List, "nf-ul", "•"),
                    (NoteFormat::Checklist, "nf-chk", "☑"),
                    (NoteFormat::Code, "nf-code", "</>"),
                    (NoteFormat::Link, "nf-link", "Link"),
                ] {
                    let k = doc_key.clone();
                    toolbar = toolbar.child(device_btn(key, lbl, false).on_press_tx(
                        move |s: &mut AppState, tx| {
                            if let Some(d) = s.media.notes.docs.get_mut(&k) {
                                apply_format(&mut d.edit, fmt);
                                d.dirty_since = Some(tx.clock());
                            }
                        },
                    ));
                }
            } else {
                let k = doc_key.clone();
                toolbar = toolbar.child(device_btn("nf-link", "Link", false).on_press_tx(
                    move |s: &mut AppState, tx| {
                        s.notes_act(&k, "markdown", None);
                        if let Some(d) = s.media.notes.docs.get_mut(&k) {
                            apply_format(&mut d.edit, NoteFormat::Link);
                            d.dirty_since = Some(tx.clock());
                        }
                    },
                ));
            }
            if own {
                let k = doc_key.clone();
                toolbar = toolbar.child(
                    device_btn("nf-photo", "Photo", false)
                        .on_press(move |s: &mut AppState| s.notes_add_photo(&k, None)),
                );
            }
        }

        if is_md {
            let k1 = doc_key.clone();
            let k2 = doc_key.clone();
            toolbar = toolbar
                .child(
                    filter_pill("note-mode-edit", "Edit", !doc.preview).on_press(
                        move |s: &mut AppState| {
                            if let Some(d) = s.media.notes.docs.get_mut(&k1) {
                                d.preview = false;
                            }
                        },
                    ),
                )
                .child(
                    filter_pill("note-mode-preview", "Preview", doc.preview).on_press(
                        move |s: &mut AppState| {
                            s.notes_flush_doc(&k2, false);
                            s.media.notes.find = None;
                            if let Some(d) = s.media.notes.docs.get_mut(&k2) {
                                d.preview = true;
                            }
                        },
                    ),
                );
        }

        toolbar = toolbar.child(el().grow(1.0));

        if own {
            let k1 = doc_key.clone();
            let k2 = doc_key.clone();
            toolbar = toolbar
                .child(
                    filter_pill("note-kind-text", "Text", !is_md)
                        .on_press(move |s: &mut AppState| s.notes_act(&k1, "text", None)),
                )
                .child(
                    filter_pill("note-kind-md", "Markdown", is_md)
                        .on_press(move |s: &mut AppState| s.notes_act(&k2, "markdown", None)),
                );
        }

        if !doc.preview {
            toolbar = toolbar.child(device_btn("note-find-btn", "Find", false).on_press(
                |s: &mut AppState| {
                    if s.media.notes.find.is_some() {
                        s.media.notes.find = None;
                    } else {
                        s.notes_open_find(false);
                    }
                },
            ));
            if doc.info.spelling {
                toolbar = toolbar.child(
                    device_btn("note-spell-btn", "Spell", false)
                        .on_press(|s: &mut AppState| s.notes_check_spell_at_caret()),
                );
            }
        }

        toolbar = toolbar.child(device_btn("note-more-btn", "⋯", false).on_press(
            |s: &mut AppState| {
                s.media.notes.note_menu_open = !s.media.notes.note_menu_open;
                s.media.notes.more_menu_open = false;
            },
        ));

        main_area = main_area.child(toolbar);

        if let Some(ref find) = notes.find
            && !doc.preview
        {
            let ranges = find_matches(
                doc.text(),
                find.query.text.as_str(),
                find.match_case,
                find.whole_word,
            );
            let count = ranges.len();
            let cur_display = if find.query.text.is_empty() {
                String::new()
            } else if count == 0 {
                "No matches".into()
            } else {
                format!("{} of {count}", find.current.min(count - 1) + 1)
            };

            let mut find_box = col()
                .w_full()
                .px(14.0)
                .py(8.0)
                .gap(8.0)
                .border_sides([0.0, 0.0, 1.0, 0.0], hairline())
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
                                .gap(6.0)
                                .center_y()
                                .radius(RADIUS_PILL)
                                .fill(glass_control_fill())
                                .border(1.0, glass_rim())
                                .child(
                                    text_input(&find.query, type_small())
                                        .key("note-find-query")
                                        .label("Find")
                                        .single_line()
                                        .grow(1.0)
                                        .color(FG)
                                        .placeholder("Find", fg_faint())
                                        .on_edit(edit_access(|s: &mut AppState| {
                                            &mut s.media.notes.find.as_mut().unwrap().query
                                        }))
                                        .on_input_change(|s: &mut AppState, _| {
                                            if let Some(ref mut f) = s.media.notes.find {
                                                f.current = 0;
                                            }
                                        })
                                        .on_submit(|s: &mut AppState, _| s.notes_find_step(1)),
                                )
                                .child(text(cur_display).font(type_small()).color(fg_muted())),
                        )
                        .child(
                            filter_pill("note-find-case", "Aa", find.match_case).on_press(
                                |s: &mut AppState| {
                                    if let Some(ref mut f) = s.media.notes.find {
                                        f.match_case = !f.match_case;
                                        f.current = 0;
                                    }
                                },
                            ),
                        )
                        .child(
                            filter_pill("note-find-word", "Word", find.whole_word).on_press(
                                |s: &mut AppState| {
                                    if let Some(ref mut f) = s.media.notes.find {
                                        f.whole_word = !f.whole_word;
                                        f.current = 0;
                                    }
                                },
                            ),
                        )
                        .child(
                            device_btn("note-find-prev", "↑", count == 0)
                                .on_press(|s: &mut AppState| s.notes_find_step(-1)),
                        )
                        .child(
                            device_btn("note-find-next", "↓", count == 0)
                                .on_press(|s: &mut AppState| s.notes_find_step(1)),
                        )
                        .child(
                            filter_pill("note-find-rep-toggle", "Replace", find.replace).on_press(
                                |s: &mut AppState| {
                                    if let Some(ref mut f) = s.media.notes.find {
                                        f.replace = !f.replace;
                                    }
                                },
                            ),
                        )
                        .child(device_btn("note-find-close", "✕", false).on_press(
                            |s: &mut AppState| {
                                s.media.notes.find = None;
                            },
                        )),
                );

            if find.replace {
                find_box = find_box.child(
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
                                .radius(RADIUS_PILL)
                                .fill(glass_control_fill())
                                .border(1.0, glass_rim())
                                .child(
                                    text_input(&find.with, type_small())
                                        .key("note-replace-with")
                                        .label("Replace with")
                                        .single_line()
                                        .grow(1.0)
                                        .color(FG)
                                        .placeholder("Replace with", fg_faint())
                                        .on_edit(edit_access(|s: &mut AppState| {
                                            &mut s.media.notes.find.as_mut().unwrap().with
                                        }))
                                        .on_submit(|s: &mut AppState, tx| {
                                            s.notes_replace_one(tx.clock())
                                        }),
                                ),
                        )
                        .child(
                            device_btn("note-rep-one", "Replace", count == 0).on_press_tx(
                                |s: &mut AppState, tx| s.notes_replace_one(tx.clock()),
                            ),
                        )
                        .child(
                            device_btn("note-rep-all", "Replace all", count == 0).on_press_tx(
                                |s: &mut AppState, tx| s.notes_replace_all(tx.clock()),
                            ),
                        ),
                );
            }
            main_area = main_area.child(find_box);
        }

        let body = if !doc.loaded {
            el().grow(1.0).w_full().center().child(
                text(
                    doc.problem
                        .clone()
                        .unwrap_or_else(|| "Opening note…".into()),
                )
                .font(type_body())
                .color(if doc.problem.is_some() {
                    bad()
                } else {
                    fg_muted()
                }),
            )
        } else if doc.preview {
            render_markdown_preview(s, doc, scale)
        } else {
            let k_edit = doc_key.clone();
            let k_chg = doc_key.clone();
            let k_key = doc_key.clone();
            let editor = text_input(&doc.edit, type_body())
                .key(format!("note-editor-{}", doc.key))
                .label(tab_name(doc))
                .w_full()
                .grow(1.0)
                .min_h(320.0)
                .color(FG)
                .placeholder(if doc.fresh { "New note" } else { "" }, fg_faint())
                .spellcheck(doc.info.spelling)
                .on_edit(edit_access(move |s: &mut AppState| {
                    &mut s.media.notes.docs.get_mut(&k_edit).unwrap().edit
                }))
                .on_input_change(move |s: &mut AppState, tx| {
                    if let Some(d) = s.media.notes.docs.get_mut(&k_chg) {
                        d.dirty_since = Some(tx.clock());
                    }
                })
                .on_input_key(move |s: &mut AppState, ev, tx| {
                    let _ = &k_key;
                    s.handle_notes_key(ev, tx)
                });

            col()
                .key("note-editor-scroll")
                .w_full()
                .grow(1.0)
                .min_h(0.0)
                .padding(20.0)
                .border(
                    if notes.drop_hovered { 2.0 } else { 0.0 },
                    if notes.drop_hovered {
                        good()
                    } else {
                        Color::TRANSPARENT
                    },
                )
                .scroll(ScrollSpec {
                    stick_bottom: false,
                })
                .on_drop_hover(|s: &mut AppState, hovered, _| {
                    s.media.notes.drop_hovered = hovered;
                })
                .on_drop_files(|s: &mut AppState, paths, _| {
                    s.notes_handle_file_drop(paths);
                })
                .child(editor)
        };

        let (caret_pos, _) = doc.edit.range();
        let (ln, col_num) = caret_line_col(doc.text(), caret_pos);
        let words = word_count(doc.text());
        let chars = doc.text().chars().count();

        let status_bar = row()
            .w_full()
            .px(16.0)
            .py(8.0)
            .gap(12.0)
            .center_y()
            .border_sides([1.0, 0.0, 0.0, 0.0], hairline())
            .children(
                doc.problem
                    .as_ref()
                    .filter(|_| doc.loaded)
                    .map(|p| text(p.clone()).font(type_small()).color(bad())),
            )
            .children(
                notes
                    .status
                    .as_ref()
                    .map(|st| text(st.clone()).font(type_small()).color(fg_muted())),
            )
            .child(el().grow(1.0))
            .children(
                (doc.info.external && doc.info.unsaved)
                    .then(|| text("Unsaved").font(type_small()).color(FG)),
            )
            .children((doc.loaded && !doc.preview).then(|| {
                text(format!("Ln {ln}, Col {col_num}"))
                    .font(type_small())
                    .color(fg_muted())
            }))
            .children(doc.loaded.then(|| {
                text(if words == 1 {
                    "1 word".into()
                } else {
                    format!("{words} words")
                })
                .font(type_small())
                .color(fg_muted())
            }))
            .children(doc.loaded.then(|| {
                text(if chars == 1 {
                    "1 character".into()
                } else {
                    format!("{chars} characters")
                })
                .font(type_small())
                .color(fg_muted())
            }))
            .child(
                text(encoding_label(&doc.info.encoding))
                    .font(type_small())
                    .color(fg_muted()),
            )
            .child(
                text(doc.info.eol.to_uppercase())
                    .font(type_small())
                    .color(fg_muted()),
            );

        main_area = main_area.child(body).child(status_bar);
    } else {
        let any_notes = notes.notes.as_ref().is_some_and(|l| !l.is_empty());
        main_area = main_area.child(
            col()
                .grow(1.0)
                .w_full()
                .center()
                .gap(16.0)
                .child(
                    text(if any_notes {
                        "No note open"
                    } else {
                        "No notes yet"
                    })
                    .font(type_title())
                    .color(FG),
                )
                .child(
                    row()
                        .gap(12.0)
                        .child(
                            device_btn("notes-empty-new", "+  New note  Ctrl + N", false)
                                .on_press(|s: &mut AppState| s.notes_new_note()),
                        )
                        .child(
                            device_btn("notes-empty-open", "Open a file  Ctrl + O", false)
                                .on_press(|s: &mut AppState| s.notes_open_file()),
                        ),
                ),
        );
    }

    let mut overlays = Vec::new();
    if notes.more_menu_open {
        let menu = col()
            .key("notes-more-popover")
            .pin(Some(60.0), None, None, Some(120.0))
            .w(240.0)
            .padding(6.0)
            .gap(2.0)
            .radius(RADIUS_PANEL)
            .fill(glass_panel_fill())
            .backdrop_blur(GLASS_PANEL_SIGMA)
            .border(1.0, glass_rim())
            .children(more_items.into_iter().map(|(id, label)| {
                row()
                    .key(format!("notes-more-{id}"))
                    .role("menuitem")
                    .focusable(true)
                    .hit()
                    .press_on_down()
                    .cursor(Cursor::Pointer)
                    .w_full()
                    .px(10.0)
                    .py(8.0)
                    .radius(RADIUS_SMALL)
                    .hover(hover_fill())
                    .on_press(move |s: &mut AppState| {
                        if id == "folder" {
                            s.notes_open_folder();
                        } else {
                            s.notes_text_files(id);
                        }
                    })
                    .child(text(label).font(type_control()).color(FG))
            }));
        overlays.push(menu);
    }

    if notes.note_menu_open
        && let Some(doc) = active_doc
    {
        let k = doc.key.clone();
        let saved = !doc.fresh;
        let pinned = doc.info.pinned;
        let ext = doc.info.external;
        let mut entries: Vec<(&'static str, &'static str, bool)> = vec![(
            if pinned { "unpin" } else { "pin" },
            if pinned { "Unpin" } else { "Pin" },
            false,
        )];
        if saved {
            entries.push(("rename", "Rename", false));
        }
        if ext {
            entries.push(("save", "Save", false));
        }
        if saved {
            entries.push(("saveAs", "Save as", false));
            entries.push(("reveal", "Show in folder", false));
        }
        if saved && !ext {
            entries.push(("delete", "Delete", true));
        }
        let doc_name = doc.info.name.clone();
        let menu = col()
            .key("notes-doc-menu")
            .pin(Some(142.0), None, None, Some(20.0))
            .w(200.0)
            .padding(6.0)
            .gap(2.0)
            .radius(RADIUS_PANEL)
            .fill(glass_panel_fill())
            .backdrop_blur(GLASS_PANEL_SIGMA)
            .border(1.0, glass_rim())
            .children(entries.into_iter().map(|(id, label, danger)| {
                let k_act = k.clone();
                let nm = doc_name.clone();
                row()
                    .key(format!("note-act-{id}"))
                    .role("menuitem")
                    .focusable(true)
                    .hit()
                    .press_on_down()
                    .cursor(Cursor::Pointer)
                    .w_full()
                    .px(10.0)
                    .py(8.0)
                    .radius(RADIUS_SMALL)
                    .hover(hover_fill())
                    .on_press(move |s: &mut AppState| {
                        s.media.notes.note_menu_open = false;
                        match id {
                            "rename" => {
                                let mut re = TextEdit::default();
                                re.text = base_name(&nm).to_string();
                                re.sync_invariants();
                                s.media.notes.rename_edit = re;
                                s.media.notes.renaming_key = Some(k_act.clone());
                            }
                            "save" => s.notes_flush_doc(&k_act, true),
                            "saveAs" => s.notes_save_as(&k_act),
                            other => s.notes_act(&k_act, other, None),
                        }
                    })
                    .child(
                        text(label)
                            .font(type_control())
                            .color(if danger { bad() } else { FG }),
                    )
            }));
        overlays.push(menu);
    }

    if let Some(ref spell) = notes.spell_popup {
        let mut items = Vec::new();
        for sugg in &spell.suggestions {
            let start = spell.start;
            let end = spell.end;
            let rep = sugg.clone();
            items.push(
                row()
                    .key(format!("spell-sugg-{sugg}"))
                    .role("menuitem")
                    .focusable(true)
                    .hit()
                    .press_on_down()
                    .cursor(Cursor::Pointer)
                    .w_full()
                    .px(10.0)
                    .py(8.0)
                    .radius(RADIUS_SMALL)
                    .hover(hover_fill())
                    .on_press_tx(move |s: &mut AppState, tx| {
                        if let Some(active) = s.media.notes.active.clone()
                            && let Some(d) = s.media.notes.docs.get_mut(&active)
                        {
                            d.edit.set_selection(start, end);
                            d.edit.paste(&rep);
                            d.dirty_since = Some(tx.clock());
                        }
                        s.media.notes.spell_popup = None;
                    })
                    .child(text(sugg.clone()).font(type_control()).color(FG)),
            );
        }
        if spell.suggestions.is_empty() {
            items.push(
                el().px(10.0)
                    .py(6.0)
                    .child(text("No suggestions").font(type_small()).color(fg_muted())),
            );
        }
        let add_w = spell.word.clone();
        items.push(
            row()
                .key("spell-add-dict")
                .role("menuitem")
                .focusable(true)
                .hit()
                .press_on_down()
                .cursor(Cursor::Pointer)
                .w_full()
                .px(10.0)
                .py(8.0)
                .radius(RADIUS_SMALL)
                .hover(hover_fill())
                .on_press(move |s: &mut AppState| s.notes_add_word_to_dict(add_w.clone()))
                .child(text("Add to dictionary").font(type_control()).color(FG)),
        );
        overlays.push(
            col()
                .key("notes-spell-popup")
                .pin(Some(148.0), None, None, Some(60.0))
                .w(220.0)
                .padding(6.0)
                .gap(2.0)
                .radius(RADIUS_PANEL)
                .fill(glass_panel_fill())
                .backdrop_blur(GLASS_PANEL_SIGMA)
                .border(1.0, glass_rim())
                .child(
                    el().px(10.0).py(4.0).child(
                        text(format!("Spelling of {}", spell.word))
                            .font(type_small())
                            .color(fg_muted()),
                    ),
                )
                .children(items),
        );
    }

    if let Some(ref key) = notes.closing_prompt {
        let name = notes
            .docs
            .get(key)
            .map(|d| d.info.name.clone())
            .unwrap_or_else(|| "this file".into());
        let k_save = key.clone();
        let k_disc = key.clone();
        overlays.push(
            el().key("notes-close-prompt-scrim")
                .pin(Some(0.0), Some(0.0), Some(0.0), Some(0.0))
                .fill(exo_kit::scrim_fill())
                .center()
                .child(
                    col()
                        .w(380.0)
                        .padding(20.0)
                        .gap(14.0)
                        .radius(RADIUS_SHEET)
                        .fill(glass_panel_fill())
                        .backdrop_blur(GLASS_PANEL_SIGMA)
                        .border(1.0, glass_rim())
                        .child(
                            text(format!("Save changes to {name}?"))
                                .font(type_section())
                                .color(FG),
                        )
                        .child(
                            row()
                                .w_full()
                                .justify_end()
                                .gap(8.0)
                                .child(device_btn("notes-close-cancel", "Cancel", false).on_press(
                                    |s: &mut AppState| s.media.notes.closing_prompt = None,
                                ))
                                .child(
                                    exo_kit::danger_button(
                                        "notes-close-discard",
                                        "Close without saving",
                                        None,
                                    )
                                    .on_press(
                                        move |s: &mut AppState| {
                                            s.media.notes.closing_prompt = None;
                                            s.notes_act(&k_disc, "discard", None);
                                            s.notes_drop_tab(&k_disc);
                                        },
                                    ),
                                )
                                .child(
                                    primary_button("notes-close-save", "Save and close", None)
                                        .on_press(move |s: &mut AppState| {
                                            s.media.notes.closing_prompt = None;
                                            s.notes_flush_doc(&k_save, true);
                                            s.notes_drop_tab(&k_save);
                                        }),
                                ),
                        ),
                ),
        );
    }

    let _ = (
        ActionResult::default,
        BG,
        BadgeTone::Neutral,
        SPRING_QUICK,
        status_badge,
    );

    el().key("notes-view-root")
        .w_full()
        .h_full()
        .child(
            col().w_full().h_full().child(head).child(
                row()
                    .grow(1.0)
                    .w_full()
                    .min_h(0.0)
                    .child(sidebar)
                    .child(main_area),
            ),
        )
        .children(overlays)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn plain_line_strips_markdown_prefixes_and_inline_marks() {
        assert_eq!(plain_line("## **Hello** `world`"), "Hello world");
        assert_eq!(plain_line("- [x] ~~Done~~ item"), "Done item");
        assert_eq!(plain_line("> Quoted note"), "Quoted note");
    }

    #[test]
    fn word_count_and_caret_line_col_are_accurate() {
        let txt = "First line\nSecond line here";
        assert_eq!(word_count(txt), 5);
        assert_eq!(caret_line_col(txt, 0), (1, 1));
        assert_eq!(caret_line_col(txt, 11), (2, 1));
        assert_eq!(caret_line_col(txt, 17), (2, 7));
    }

    #[test]
    fn find_matches_respects_case_and_whole_word() {
        let txt = "Cat caterpillar cat CAT";
        assert_eq!(find_matches(txt, "cat", false, false).len(), 4);
        assert_eq!(find_matches(txt, "cat", true, false).len(), 2);
        assert_eq!(find_matches(txt, "cat", false, true).len(), 3);
        assert_eq!(find_matches(txt, "cat", true, true).len(), 1);
    }

    #[test]
    fn apply_format_toggles_bold_and_checklist() {
        let mut ed = TextEdit::new("hello world");
        ed.set_selection(0, 5);
        apply_format(&mut ed, NoteFormat::Bold);
        assert_eq!(ed.text.as_str(), "**hello** world");
        apply_format(&mut ed, NoteFormat::Bold);
        assert_eq!(ed.text.as_str(), "hello world");

        ed.set_selection(0, 5);
        apply_format(&mut ed, NoteFormat::Checklist);
        assert_eq!(ed.text.as_str(), "- [ ] hello world");
        apply_format(&mut ed, NoteFormat::Checklist);
        assert_eq!(ed.text.as_str(), "hello world");
    }

    #[test]
    fn toggle_checklist_line_flips_target_line_only() {
        let src = "- [ ] one\n- [x] two";
        assert_eq!(toggle_checklist_line(src, 0), "- [x] one\n- [x] two");
        assert_eq!(toggle_checklist_line(src, 1), "- [ ] one\n- [ ] two");
    }
}
