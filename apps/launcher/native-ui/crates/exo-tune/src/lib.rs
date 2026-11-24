use std::collections::{HashMap, HashSet};
use std::fmt;

pub const PACK_MAGIC: [u8; 4] = *b"EXT1";
pub const PACK_VERSION: u8 = 1;

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
#[repr(u8)]
pub enum PathRoot {
    Documents = 1,
    LocalAppData = 2,
    SteamUserData = 3,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
#[repr(u8)]
pub enum ConfigFormat {
    Xml = 1,
    Ini = 2,
    UnrealIni = 3,
    Json = 4,
    CyberpunkJson = 5,
    Scr = 6,
    Lua = 7,
    Vdf = 8,
    PearlAbyssXml = 9,
}

#[derive(Debug, Clone, PartialEq)]
pub enum Pred {
    Always,
    GpuStrong,
    GpuRtx,
    Fullscreen,
    VramGte(u16),
    VramLt(u16),
    VramGt(u16),
    HdrGt(u16),
    VendorEq(String),
    Not(Box<Pred>),
    And(Box<Pred>, Box<Pred>),
    Or(Box<Pred>, Box<Pred>),
}

#[derive(Debug, Clone, PartialEq)]
pub enum ValueExpr {
    Lit(String),
    Cond {
        pred: Pred,
        then_val: String,
        else_val: String,
    },
}

#[derive(Debug, Clone, PartialEq)]
pub struct SetRule {
    pub key: String,
    pub value: ValueExpr,
}

#[derive(Debug, Clone, PartialEq)]
pub struct RuleGroup {
    pub id: String,
    pub when: Pred,
    pub sets: Vec<SetRule>,
}

#[derive(Debug, Clone, PartialEq)]
pub struct GameRecipe {
    pub id: String,
    pub title: String,
    pub steam_app: Option<String>,
    pub program: String,
    pub processes: Vec<String>,
    pub root: PathRoot,
    pub rel_path: String,
    pub format: ConfigFormat,
    pub ini_section: Option<String>,
    pub groups: Vec<RuleGroup>,
    pub borderless: Vec<SetRule>,
    pub fullscreen: Vec<SetRule>,
}

#[derive(Debug, Clone)]
pub struct PcFacts {
    pub vendor: String,
    pub gpu_strong: bool,
    pub gpu_rtx: bool,
    pub vram_gb: u16,
    pub hdr_nits: u16,
    pub fullscreen: bool,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ResolvedChange {
    pub group: String,
    pub key: String,
    pub value: String,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct PatchedLine {
    pub key: String,
    pub old_value: String,
    pub new_value: String,
    pub line_index: usize,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum TuneError {
    Lex(usize, String),
    Parse(usize, String),
    Safety(String),
    Pack(String),
}

impl fmt::Display for TuneError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(f, "{self:?}")
    }
}

impl std::error::Error for TuneError {}

#[derive(Debug, Clone, PartialEq, Eq)]
enum Tok {
    Ident(String),
    Str(String),
    Int(u16),
    DollarIdent(String),
    LBrace,
    RBrace,
    LBracket,
    RBracket,
    LParen,
    RParen,
    Comma,
    Eq,
    EqEq,
    Ge,
    Gt,
    Lt,
    AndAnd,
    OrOr,
    Bang,
}

fn lex(src: &str) -> Result<Vec<(usize, Tok)>, TuneError> {
    let b = src.as_bytes();
    let mut i = 0usize;
    let mut line = 1usize;
    let mut toks = Vec::new();
    while i < b.len() {
        match b[i] {
            b' ' | b'\t' | b'\r' => i += 1,
            b'\n' => {
                line += 1;
                i += 1;
            }
            b'#' => {
                while i < b.len() && b[i] != b'\n' {
                    i += 1;
                }
            }
            b'{' => {
                toks.push((line, Tok::LBrace));
                i += 1;
            }
            b'}' => {
                toks.push((line, Tok::RBrace));
                i += 1;
            }
            b'[' => {
                toks.push((line, Tok::LBracket));
                i += 1;
            }
            b']' => {
                toks.push((line, Tok::RBracket));
                i += 1;
            }
            b'(' => {
                toks.push((line, Tok::LParen));
                i += 1;
            }
            b')' => {
                toks.push((line, Tok::RParen));
                i += 1;
            }
            b',' => {
                toks.push((line, Tok::Comma));
                i += 1;
            }
            b'!' => {
                toks.push((line, Tok::Bang));
                i += 1;
            }
            b'=' => {
                if i + 1 < b.len() && b[i + 1] == b'=' {
                    toks.push((line, Tok::EqEq));
                    i += 2;
                } else {
                    toks.push((line, Tok::Eq));
                    i += 1;
                }
            }
            b'>' => {
                if i + 1 < b.len() && b[i + 1] == b'=' {
                    toks.push((line, Tok::Ge));
                    i += 2;
                } else {
                    toks.push((line, Tok::Gt));
                    i += 1;
                }
            }
            b'<' => {
                toks.push((line, Tok::Lt));
                i += 1;
            }
            b'&' if i + 1 < b.len() && b[i + 1] == b'&' => {
                toks.push((line, Tok::AndAnd));
                i += 2;
            }
            b'|' if i + 1 < b.len() && b[i + 1] == b'|' => {
                toks.push((line, Tok::OrOr));
                i += 2;
            }
            b'"' => {
                i += 1;
                let mut s = String::new();
                while i < b.len() && b[i] != b'"' {
                    if b[i] == b'\\' && i + 1 < b.len() {
                        i += 1;
                        match b[i] {
                            b'"' => s.push('"'),
                            b'\\' => s.push('\\'),
                            b'n' => s.push('\n'),
                            other => s.push(other as char),
                        }
                        i += 1;
                    } else {
                        if b[i] == b'\n' {
                            return Err(TuneError::Lex(line, "unterminated string".into()));
                        }
                        s.push(b[i] as char);
                        i += 1;
                    }
                }
                if i >= b.len() {
                    return Err(TuneError::Lex(line, "unterminated string".into()));
                }
                i += 1;
                toks.push((line, Tok::Str(s)));
            }
            b'$' => {
                i += 1;
                let start = i;
                while i < b.len() && (b[i].is_ascii_alphanumeric() || b[i] == b'_') {
                    i += 1;
                }
                toks.push((line, Tok::DollarIdent(src[start..i].to_owned())));
            }
            c if c.is_ascii_digit() => {
                let start = i;
                while i < b.len() && b[i].is_ascii_digit() {
                    i += 1;
                }
                let v: u16 = src[start..i]
                    .parse()
                    .map_err(|_| TuneError::Lex(line, "bad integer".into()))?;
                toks.push((line, Tok::Int(v)));
            }
            c if c.is_ascii_alphabetic() || c == b'_' => {
                let start = i;
                while i < b.len()
                    && (b[i].is_ascii_alphanumeric() || b[i] == b'_' || b[i] == b'.')
                {
                    i += 1;
                }
                toks.push((line, Tok::Ident(src[start..i].to_owned())));
            }
            other => {
                return Err(TuneError::Lex(line, format!("unexpected byte 0x{other:02x}")));
            }
        }
    }
    Ok(toks)
}

struct Parser {
    toks: Vec<(usize, Tok)>,
    pos: usize,
}

impl Parser {
    fn peek(&self) -> Option<&Tok> {
        self.toks.get(self.pos).map(|(_, t)| t)
    }

    fn line(&self) -> usize {
        self.toks
            .get(self.pos)
            .or_else(|| self.toks.last())
            .map(|(l, _)| *l)
            .unwrap_or(1)
    }

    fn next(&mut self) -> Result<(usize, Tok), TuneError> {
        if self.pos >= self.toks.len() {
            return Err(TuneError::Parse(self.line(), "unexpected eof".into()));
        }
        let t = self.toks[self.pos].clone();
        self.pos += 1;
        Ok(t)
    }

    fn expect(&mut self, expected: Tok) -> Result<(), TuneError> {
        let (l, got) = self.next()?;
        if got != expected {
            return Err(TuneError::Parse(
                l,
                format!("expected {expected:?}, got {got:?}"),
            ));
        }
        Ok(())
    }

    fn expect_str(&mut self) -> Result<String, TuneError> {
        match self.next()? {
            (_, Tok::Str(s)) => Ok(s),
            (l, other) => Err(TuneError::Parse(l, format!("expected string, got {other:?}"))),
        }
    }

    fn expect_ident(&mut self) -> Result<String, TuneError> {
        match self.next()? {
            (_, Tok::Ident(s)) => Ok(s),
            (l, other) => Err(TuneError::Parse(l, format!("expected ident, got {other:?}"))),
        }
    }

    fn parse_pred(&mut self) -> Result<Pred, TuneError> {
        let mut lhs = self.parse_pred_and()?;
        while matches!(self.peek(), Some(Tok::OrOr)) {
            self.next()?;
            let rhs = self.parse_pred_and()?;
            lhs = Pred::Or(Box::new(lhs), Box::new(rhs));
        }
        Ok(lhs)
    }

    fn parse_pred_and(&mut self) -> Result<Pred, TuneError> {
        let mut lhs = self.parse_pred_atom()?;
        while matches!(self.peek(), Some(Tok::AndAnd)) {
            self.next()?;
            let rhs = self.parse_pred_atom()?;
            lhs = Pred::And(Box::new(lhs), Box::new(rhs));
        }
        Ok(lhs)
    }

    fn parse_pred_atom(&mut self) -> Result<Pred, TuneError> {
        if matches!(self.peek(), Some(Tok::Bang)) {
            self.next()?;
            return Ok(Pred::Not(Box::new(self.parse_pred_atom()?)));
        }
        if matches!(self.peek(), Some(Tok::LParen)) {
            self.next()?;
            let p = self.parse_pred()?;
            self.expect(Tok::RParen)?;
            return Ok(p);
        }
        let l = self.line();
        let name = self.expect_ident()?;
        match name.as_str() {
            "always" => Ok(Pred::Always),
            "gpu.strong" => Ok(Pred::GpuStrong),
            "gpu.rtx" => Ok(Pred::GpuRtx),
            "fullscreen" => Ok(Pred::Fullscreen),
            "vram_gb" => {
                let op = self.next()?.1;
                let val = match self.next()? {
                    (_, Tok::Int(v)) => v,
                    (ln, other) => return Err(TuneError::Parse(ln, format!("expected int, got {other:?}"))),
                };
                match op {
                    Tok::Ge => Ok(Pred::VramGte(val)),
                    Tok::Lt => Ok(Pred::VramLt(val)),
                    Tok::Gt => Ok(Pred::VramGt(val)),
                    other => Err(TuneError::Parse(l, format!("unsupported vram op {other:?}"))),
                }
            }
            "hdr_nits" => {
                self.expect(Tok::Gt)?;
                match self.next()? {
                    (_, Tok::Int(v)) => Ok(Pred::HdrGt(v)),
                    (ln, other) => Err(TuneError::Parse(ln, format!("expected int, got {other:?}"))),
                }
            }
            "gpu.vendor" => {
                self.expect(Tok::EqEq)?;
                Ok(Pred::VendorEq(self.expect_str()?.to_ascii_lowercase()))
            }
            other => Err(TuneError::Parse(l, format!("unknown predicate {other}"))),
        }
    }

    fn parse_set_rule(&mut self) -> Result<SetRule, TuneError> {
        let key = self.expect_str()?;
        self.expect(Tok::Eq)?;
        let value = match self.peek() {
            Some(Tok::Str(_)) => ValueExpr::Lit(self.expect_str()?),
            Some(Tok::Ident(k)) if k == "if" => {
                self.next()?;
                let pred = self.parse_pred()?;
                self.expect(Tok::LBrace)?;
                let then_val = self.expect_str()?;
                self.expect(Tok::RBrace)?;
                let kw = self.expect_ident()?;
                if kw != "else" {
                    return Err(TuneError::Parse(self.line(), format!("expected else, got {kw}")));
                }
                self.expect(Tok::LBrace)?;
                let else_val = self.expect_str()?;
                self.expect(Tok::RBrace)?;
                ValueExpr::Cond {
                    pred,
                    then_val,
                    else_val,
                }
            }
            other => {
                return Err(TuneError::Parse(
                    self.line(),
                    format!("expected value expression, got {other:?}"),
                ));
            }
        };
        Ok(SetRule { key, value })
    }

    fn parse_set_block(&mut self) -> Result<Vec<SetRule>, TuneError> {
        self.expect(Tok::LBrace)?;
        let mut sets = Vec::new();
        while !matches!(self.peek(), Some(Tok::RBrace)) {
            let kw = self.expect_ident()?;
            if kw != "set" {
                return Err(TuneError::Parse(self.line(), format!("expected set, got {kw}")));
            }
            sets.push(self.parse_set_rule()?);
        }
        self.expect(Tok::RBrace)?;
        Ok(sets)
    }

    fn parse_game(&mut self) -> Result<GameRecipe, TuneError> {
        let kw = self.expect_ident()?;
        if kw != "game" {
            return Err(TuneError::Parse(self.line(), format!("expected game, got {kw}")));
        }
        let id = self.expect_str()?;
        self.expect(Tok::LBrace)?;
        let mut title = String::new();
        let mut steam_app = None;
        let mut program = String::new();
        let mut processes = Vec::new();
        let mut root = None;
        let mut rel_path = String::new();
        let mut format = None;
        let mut ini_section = None;
        let mut groups = Vec::new();
        let mut borderless = Vec::new();
        let mut fullscreen = Vec::new();

        while !matches!(self.peek(), Some(Tok::RBrace)) {
            let l = self.line();
            let field = self.expect_ident()?;
            match field.as_str() {
                "title" => title = self.expect_str()?,
                "steam" => steam_app = Some(self.expect_str()?),
                "program" => program = self.expect_str()?,
                "section" => ini_section = Some(self.expect_str()?),
                "processes" => {
                    self.expect(Tok::LBracket)?;
                    while !matches!(self.peek(), Some(Tok::RBracket)) {
                        processes.push(self.expect_str()?);
                        if matches!(self.peek(), Some(Tok::Comma)) {
                            self.next()?;
                        }
                    }
                    self.expect(Tok::RBracket)?;
                }
                "file" => {
                    let root_name = match self.next()? {
                        (_, Tok::DollarIdent(r)) => r,
                        (ln, other) => {
                            return Err(TuneError::Safety(format!(
                                "line {ln}: file path must start with safe root ($documents/$localappdata/$steam_userdata), got {other:?}"
                            )));
                        }
                    };
                    root = Some(match root_name.as_str() {
                        "documents" => PathRoot::Documents,
                        "localappdata" => PathRoot::LocalAppData,
                        "steam_userdata" => PathRoot::SteamUserData,
                        other => {
                            return Err(TuneError::Safety(format!(
                                "disallowed path root ${other} (install folder and arbitrary roots are banned)"
                            )));
                        }
                    });
                    self.expect(Tok::LParen)?;
                    rel_path = self.expect_str()?;
                    self.expect(Tok::RParen)?;
                }
                "format" => {
                    let f = self.expect_ident()?;
                    format = Some(match f.as_str() {
                        "xml" => ConfigFormat::Xml,
                        "ini" => ConfigFormat::Ini,
                        "unreal_ini" => ConfigFormat::UnrealIni,
                        "json" => ConfigFormat::Json,
                        "cyberpunk_json" => ConfigFormat::CyberpunkJson,
                        "scr" => ConfigFormat::Scr,
                        "lua" => ConfigFormat::Lua,
                        "vdf" => ConfigFormat::Vdf,
                        "pearl_abyss_xml" => ConfigFormat::PearlAbyssXml,
                        other => return Err(TuneError::Parse(l, format!("unknown format {other}"))),
                    });
                }
                "group" => {
                    let gid = self.expect_str()?;
                    let when = if matches!(self.peek(), Some(Tok::Ident(w)) if w == "when") {
                        self.next()?;
                        self.parse_pred()?
                    } else {
                        Pred::Always
                    };
                    let sets = self.parse_set_block()?;
                    groups.push(RuleGroup { id: gid, when, sets });
                }
                "window" => {
                    self.expect(Tok::LBrace)?;
                    while !matches!(self.peek(), Some(Tok::RBrace)) {
                        let mode = self.expect_ident()?;
                        let sets = self.parse_set_block()?;
                        match mode.as_str() {
                            "borderless" => borderless = sets,
                            "fullscreen" => fullscreen = sets,
                            other => return Err(TuneError::Parse(self.line(), format!("unknown window mode {other}"))),
                        }
                    }
                    self.expect(Tok::RBrace)?;
                }
                other => return Err(TuneError::Parse(l, format!("unknown field {other}"))),
            }
        }
        self.expect(Tok::RBrace)?;
        Ok(GameRecipe {
            id,
            title,
            steam_app,
            program,
            processes,
            root: root.ok_or_else(|| TuneError::Parse(self.line(), "missing file".into()))?,
            rel_path,
            format: format.ok_or_else(|| TuneError::Parse(self.line(), "missing format".into()))?,
            ini_section,
            groups,
            borderless,
            fullscreen,
        })
    }
}

const BANNED_KEYS: &[&str] = &[
    "fps_max",
    "maxfps",
    "frameratelimit",
    "vsync",
    "busevsync",
    "verticalsync",
    "m_bvsync",
    "v_sync",
];

const BANNED_PROCESSES: &[&str] = &[
    "vgc",
    "vgk",
    "vgtray",
    "valorant",
    "league",
    "riotclient",
    "easyanticheat",
    "battleye",
    "_eac",
    "_be",
];

pub fn validate_recipes(recipes: &[GameRecipe]) -> Result<(), TuneError> {
    let mut ids = HashSet::new();
    for r in recipes {
        if !ids.insert(r.id.clone()) {
            return Err(TuneError::Safety(format!("duplicate game id {}", r.id)));
        }
        if r.rel_path.is_empty()
            || r.rel_path.contains("..")
            || r.rel_path.contains('\\')
            || r.rel_path.starts_with('/')
            || r.rel_path.contains(':')
        {
            return Err(TuneError::Safety(format!(
                "unsafe relative config path in {}: {}",
                r.id, r.rel_path
            )));
        }
        for p in &r.processes {
            let low = p.to_ascii_lowercase();
            if BANNED_PROCESSES.iter().any(|b| low.contains(b)) {
                return Err(TuneError::Safety(format!(
                    "disallowed anti-cheat or Riot process in {}: {p}",
                    r.id
                )));
            }
        }
        let all_sets = r
            .groups
            .iter()
            .flat_map(|g| g.sets.iter())
            .chain(r.borderless.iter())
            .chain(r.fullscreen.iter());
        for s in all_sets {
            let low = s.key.to_ascii_lowercase();
            if BANNED_KEYS.iter().any(|b| low == *b) {
                return Err(TuneError::Safety(format!(
                    "banned FPS-cap or V-Sync key in {}: {}",
                    r.id, s.key
                )));
            }
        }
    }
    Ok(())
}

pub fn parse_and_validate(src: &str) -> Result<Vec<GameRecipe>, TuneError> {
    let toks = lex(src)?;
    let mut p = Parser { toks, pos: 0 };
    let mut recipes = Vec::new();
    while p.peek().is_some() {
        recipes.push(p.parse_game()?);
    }
    validate_recipes(&recipes)?;
    Ok(recipes)
}

pub fn eval_pred(pred: &Pred, pc: &PcFacts) -> bool {
    match pred {
        Pred::Always => true,
        Pred::GpuStrong => pc.gpu_strong,
        Pred::GpuRtx => pc.gpu_rtx,
        Pred::Fullscreen => pc.fullscreen,
        Pred::VramGte(v) => pc.vram_gb >= *v,
        Pred::VramLt(v) => pc.vram_gb < *v,
        Pred::VramGt(v) => pc.vram_gb > *v,
        Pred::HdrGt(v) => pc.hdr_nits > *v,
        Pred::VendorEq(v) => pc.vendor.eq_ignore_ascii_case(v),
        Pred::Not(inner) => !eval_pred(inner, pc),
        Pred::And(a, b) => eval_pred(a, pc) && eval_pred(b, pc),
        Pred::Or(a, b) => eval_pred(a, pc) || eval_pred(b, pc),
    }
}

pub fn resolve_changes(recipe: &GameRecipe, pc: &PcFacts) -> Vec<ResolvedChange> {
    let mut out = Vec::new();
    for g in &recipe.groups {
        if !eval_pred(&g.when, pc) {
            continue;
        }
        for s in &g.sets {
            let val = match &s.value {
                ValueExpr::Lit(v) => v.clone(),
                ValueExpr::Cond {
                    pred,
                    then_val,
                    else_val,
                } => {
                    if eval_pred(pred, pc) {
                        then_val.clone()
                    } else {
                        else_val.clone()
                    }
                }
            };
            out.push(ResolvedChange {
                group: g.id.clone(),
                key: s.key.clone(),
                value: val,
            });
        }
    }
    let win = if pc.fullscreen {
        &recipe.fullscreen
    } else {
        &recipe.borderless
    };
    for s in win {
        if let ValueExpr::Lit(v) = &s.value {
            out.push(ResolvedChange {
                group: "window".into(),
                key: s.key.clone(),
                value: v.clone(),
            });
        }
    }
    out
}

fn put_str(buf: &mut Vec<u8>, s: &str) {
    buf.extend_from_slice(&(s.len() as u16).to_le_bytes());
    buf.extend_from_slice(s.as_bytes());
}

fn put_pred(buf: &mut Vec<u8>, pred: &Pred) {
    match pred {
        Pred::Always => buf.push(0),
        Pred::GpuStrong => buf.push(1),
        Pred::GpuRtx => buf.push(2),
        Pred::Fullscreen => buf.push(3),
        Pred::VramGte(v) => {
            buf.push(4);
            buf.extend_from_slice(&v.to_le_bytes());
        }
        Pred::VramLt(v) => {
            buf.push(5);
            buf.extend_from_slice(&v.to_le_bytes());
        }
        Pred::VramGt(v) => {
            buf.push(6);
            buf.extend_from_slice(&v.to_le_bytes());
        }
        Pred::HdrGt(v) => {
            buf.push(7);
            buf.extend_from_slice(&v.to_le_bytes());
        }
        Pred::VendorEq(v) => {
            buf.push(8);
            put_str(buf, v);
        }
        Pred::Not(a) => {
            buf.push(9);
            put_pred(buf, a);
        }
        Pred::And(a, b) => {
            buf.push(10);
            put_pred(buf, a);
            put_pred(buf, b);
        }
        Pred::Or(a, b) => {
            buf.push(11);
            put_pred(buf, a);
            put_pred(buf, b);
        }
    }
}

fn put_sets(buf: &mut Vec<u8>, sets: &[SetRule]) {
    buf.extend_from_slice(&(sets.len() as u16).to_le_bytes());
    for s in sets {
        put_str(buf, &s.key);
        match &s.value {
            ValueExpr::Lit(v) => {
                buf.push(0);
                put_str(buf, v);
            }
            ValueExpr::Cond {
                pred,
                then_val,
                else_val,
            } => {
                buf.push(1);
                put_pred(buf, pred);
                put_str(buf, then_val);
                put_str(buf, else_val);
            }
        }
    }
}

pub fn compile_pack(recipes: &[GameRecipe]) -> Vec<u8> {
    let mut out = Vec::with_capacity(4096);
    out.extend_from_slice(&PACK_MAGIC);
    out.push(PACK_VERSION);
    out.extend_from_slice(&(recipes.len() as u16).to_le_bytes());
    for r in recipes {
        put_str(&mut out, &r.id);
        put_str(&mut out, &r.title);
        match &r.steam_app {
            Some(s) => {
                out.push(1);
                put_str(&mut out, s);
            }
            None => out.push(0),
        }
        put_str(&mut out, &r.program);
        out.push(r.processes.len() as u8);
        for p in &r.processes {
            put_str(&mut out, p);
        }
        out.push(r.root as u8);
        put_str(&mut out, &r.rel_path);
        out.push(r.format as u8);
        match &r.ini_section {
            Some(s) => {
                out.push(1);
                put_str(&mut out, s);
            }
            None => out.push(0),
        }
        out.push(r.groups.len() as u8);
        for g in &r.groups {
            put_str(&mut out, &g.id);
            put_pred(&mut out, &g.when);
            put_sets(&mut out, &g.sets);
        }
        put_sets(&mut out, &r.borderless);
        put_sets(&mut out, &r.fullscreen);
    }
    out
}

struct BinReader<'a> {
    buf: &'a [u8],
    pos: usize,
}

impl<'a> BinReader<'a> {
    fn take(&mut self, n: usize) -> Result<&'a [u8], TuneError> {
        let end = self
            .pos
            .checked_add(n)
            .ok_or_else(|| TuneError::Pack("overflow".into()))?;
        if end > self.buf.len() {
            return Err(TuneError::Pack("truncated".into()));
        }
        let s = &self.buf[self.pos..end];
        self.pos = end;
        Ok(s)
    }

    fn u8(&mut self) -> Result<u8, TuneError> {
        Ok(self.take(1)?[0])
    }

    fn u16(&mut self) -> Result<u16, TuneError> {
        let b = self.take(2)?;
        Ok(u16::from_le_bytes([b[0], b[1]]))
    }

    fn str(&mut self) -> Result<String, TuneError> {
        let len = self.u16()? as usize;
        let s = std::str::from_utf8(self.take(len)?)
            .map_err(|_| TuneError::Pack("utf8".into()))?;
        Ok(s.to_owned())
    }

    fn pred(&mut self) -> Result<Pred, TuneError> {
        match self.u8()? {
            0 => Ok(Pred::Always),
            1 => Ok(Pred::GpuStrong),
            2 => Ok(Pred::GpuRtx),
            3 => Ok(Pred::Fullscreen),
            4 => Ok(Pred::VramGte(self.u16()?)),
            5 => Ok(Pred::VramLt(self.u16()?)),
            6 => Ok(Pred::VramGt(self.u16()?)),
            7 => Ok(Pred::HdrGt(self.u16()?)),
            8 => Ok(Pred::VendorEq(self.str()?)),
            9 => Ok(Pred::Not(Box::new(self.pred()?))),
            10 => Ok(Pred::And(Box::new(self.pred()?), Box::new(self.pred()?))),
            11 => Ok(Pred::Or(Box::new(self.pred()?), Box::new(self.pred()?))),
            t => Err(TuneError::Pack(format!("bad pred tag {t}"))),
        }
    }

    fn sets(&mut self) -> Result<Vec<SetRule>, TuneError> {
        let n = self.u16()? as usize;
        let mut out = Vec::with_capacity(n);
        for _ in 0..n {
            let key = self.str()?;
            let value = match self.u8()? {
                0 => ValueExpr::Lit(self.str()?),
                1 => ValueExpr::Cond {
                    pred: self.pred()?,
                    then_val: self.str()?,
                    else_val: self.str()?,
                },
                t => return Err(TuneError::Pack(format!("bad val tag {t}"))),
            };
            out.push(SetRule { key, value });
        }
        Ok(out)
    }
}

pub fn load_pack(bytes: &[u8]) -> Result<Vec<GameRecipe>, TuneError> {
    let mut r = BinReader { buf: bytes, pos: 0 };
    if r.take(4)? != PACK_MAGIC {
        return Err(TuneError::Pack("bad magic".into()));
    }
    if r.u8()? != PACK_VERSION {
        return Err(TuneError::Pack("bad version".into()));
    }
    let count = r.u16()? as usize;
    let mut out = Vec::with_capacity(count);
    for _ in 0..count {
        let id = r.str()?;
        let title = r.str()?;
        let steam_app = if r.u8()? == 1 { Some(r.str()?) } else { None };
        let program = r.str()?;
        let pn = r.u8()? as usize;
        let mut processes = Vec::with_capacity(pn);
        for _ in 0..pn {
            processes.push(r.str()?);
        }
        let root = match r.u8()? {
            1 => PathRoot::Documents,
            2 => PathRoot::LocalAppData,
            3 => PathRoot::SteamUserData,
            x => return Err(TuneError::Pack(format!("bad root {x}"))),
        };
        let rel_path = r.str()?;
        let format = match r.u8()? {
            1 => ConfigFormat::Xml,
            2 => ConfigFormat::Ini,
            3 => ConfigFormat::UnrealIni,
            4 => ConfigFormat::Json,
            5 => ConfigFormat::CyberpunkJson,
            6 => ConfigFormat::Scr,
            7 => ConfigFormat::Lua,
            8 => ConfigFormat::Vdf,
            9 => ConfigFormat::PearlAbyssXml,
            x => return Err(TuneError::Pack(format!("bad format {x}"))),
        };
        let ini_section = if r.u8()? == 1 { Some(r.str()?) } else { None };
        let gn = r.u8()? as usize;
        let mut groups = Vec::with_capacity(gn);
        for _ in 0..gn {
            groups.push(RuleGroup {
                id: r.str()?,
                when: r.pred()?,
                sets: r.sets()?,
            });
        }
        let borderless = r.sets()?;
        let fullscreen = r.sets()?;
        out.push(GameRecipe {
            id,
            title,
            steam_app,
            program,
            processes,
            root,
            rel_path,
            format,
            ini_section,
            groups,
            borderless,
            fullscreen,
        });
    }
    Ok(out)
}

fn find_value_span(format: ConfigFormat, line: &str) -> Option<(&str, usize, usize)> {
    let trimmed = line.trim();
    match format {
        ConfigFormat::Xml => {
            if !trimmed.starts_with('<') || trimmed.starts_with("</") || trimmed.starts_with("<?") {
                return None;
            }
            let open_pos = line.find('<')?;
            let after = &line[open_pos + 1..];
            let name_end = after.find(|c: char| c.is_ascii_whitespace() || c == '>' || c == '/')?;
            let key = &after[..name_end];
            if let Some(vpos) = line.find("value=\"") {
                let start = vpos + 7;
                let end = start + line[start..].find('"')?;
                return Some((key, start, end));
            }
            let gt = open_pos + 1 + after.find('>')?;
            let close_tag = format!("</{key}>");
            let end = line.find(&close_tag)?;
            if end > gt {
                return Some((key, gt + 1, end));
            }
            None
        }
        ConfigFormat::Ini | ConfigFormat::UnrealIni => {
            if trimmed.is_empty() || trimmed.starts_with('[') || trimmed.starts_with(';') || trimmed.starts_with('#') {
                return None;
            }
            let eq = line.find('=')?;
            let key = line[..eq].trim();
            let val_part = &line[eq + 1..];
            let lead = val_part.len() - val_part.trim_start().len();
            let start = eq + 1 + lead;
            let trimmed_val = val_part.trim();
            Some((key, start, start + trimmed_val.len()))
        }
        ConfigFormat::Vdf => {
            let q1 = line.find('"')?;
            let q2 = q1 + 1 + line[q1 + 1..].find('"')?;
            let key = &line[q1 + 1..q2];
            let q3 = q2 + 1 + line[q2 + 1..].find('"')?;
            let q4 = q3 + 1 + line[q3 + 1..].find('"')?;
            Some((key, q3 + 1, q4))
        }
        ConfigFormat::Scr => {
            let lp = line.find('(')?;
            let rp = line.rfind(')')?;
            let key = line[..lp].trim();
            let inner = &line[lp + 1..rp];
            if inner.starts_with('"') && inner.ends_with('"') && inner.len() >= 2 {
                Some((key, lp + 2, rp - 1))
            } else {
                Some((key, lp + 1, rp))
            }
        }
        ConfigFormat::Json | ConfigFormat::Lua => {
            let sep = if format == ConfigFormat::Json { ':' } else { '=' };
            let sp = line.find(sep)?;
            let raw_k = line[..sp].trim().trim_matches(|c| c == '"' || c == '[' || c == ']');
            if raw_k.is_empty() {
                return None;
            }
            let after = &line[sp + 1..];
            let lead = after.len() - after.trim_start().len();
            let mut start = sp + 1 + lead;
            let mut end = start + after.trim().trim_end_matches(',').len();
            if end > start + 1 && line.as_bytes()[start] == b'"' && line.as_bytes()[end - 1] == b'"' {
                start += 1;
                end -= 1;
            }
            Some((raw_k, start, end))
        }
        ConfigFormat::PearlAbyssXml => {
            if let Some(np) = line.find("Name=\"") {
                let ns = np + 6;
                let ne = ns + line[ns..].find('"')?;
                let key = &line[ns..ne];
                for attr in ["_select=\"", "_value=\""] {
                    if let Some(vp) = line.find(attr) {
                        let vs = vp + attr.len();
                        let ve = vs + line[vs..].find('"')?;
                        return Some((key, vs, ve));
                    }
                }
            }
            None
        }
        ConfigFormat::CyberpunkJson => None,
    }
}

pub fn patch_config(
    format: ConfigFormat,
    content: &str,
    changes: &[ResolvedChange],
) -> (String, Vec<PatchedLine>) {
    let want: HashMap<&str, &str> = changes
        .iter()
        .map(|c| (c.key.as_str(), c.value.as_str()))
        .collect();
    let newline = if content.contains("\r\n") { "\r\n" } else { "\n" };
    let mut lines: Vec<String> = content.split(newline).map(str::to_owned).collect();
    let mut patched = Vec::new();

    if format == ConfigFormat::CyberpunkJson {
        let mut current_opt = String::new();
        for (idx, line) in lines.iter_mut().enumerate() {
            if let Some((field, start, end)) = find_value_span(ConfigFormat::Json, line) {
                if field == "name" {
                    current_opt = line[start..end].to_owned();
                } else if field == "value" || field == "index" {
                    let key = if field == "index" {
                        format!("{current_opt}.index")
                    } else {
                        current_opt.clone()
                    };
                    if let Some(&target) = want.get(key.as_str()) {
                        let old = line[start..end].to_owned();
                        if old != target {
                            line.replace_range(start..end, target);
                            patched.push(PatchedLine {
                                key,
                                old_value: old,
                                new_value: target.to_owned(),
                                line_index: idx,
                            });
                        }
                    }
                }
            }
        }
    } else {
        for (idx, line) in lines.iter_mut().enumerate() {
            if let Some((key, start, end)) = find_value_span(format, line) {
                if let Some(&target) = want.get(key) {
                    let old = line[start..end].to_owned();
                    let k = key.to_owned();
                    if old != target {
                        line.replace_range(start..end, target);
                        patched.push(PatchedLine {
                            key: k,
                            old_value: old,
                            new_value: target.to_owned(),
                            line_index: idx,
                        });
                    }
                }
            }
        }
    }

    (lines.join(newline), patched)
}

pub fn rollback_config(format: ConfigFormat, patched_text: &str, log: &[PatchedLine]) -> String {
    let rev: Vec<ResolvedChange> = log
        .iter()
        .map(|p| ResolvedChange {
            group: "rollback".into(),
            key: p.key.clone(),
            value: p.old_value.clone(),
        })
        .collect();
    patch_config(format, patched_text, &rev).0
}

#[cfg(test)]
mod tests {
    use super::*;

    const SAMPLE: &str = r#"
game "rdr2" {
  title "Red Dead Redemption 2"
  steam "1174180"
  program "PlayRDR2.exe"
  processes ["RDR2", "PlayRDR2"]
  file $documents("Rockstar Games/Red Dead Redemption 2/Settings/system.xml")
  format xml
  group "graphics" {
    set "textureQuality" = "kSettingLevel_Ultra"
    set "shadowQuality" = if gpu.strong { "kSettingLevel_Ultra" } else { "kSettingLevel_High" }
  }
  window {
    borderless { set "windowed" = "2" }
    fullscreen { set "windowed" = "0" }
  }
}
"#;

    #[test]
    fn parse_compile_reload_and_eval() {
        let recipes = parse_and_validate(SAMPLE).unwrap();
        assert_eq!(recipes.len(), 1);
        let packed = compile_pack(&recipes);
        let reloaded = load_pack(&packed).unwrap();
        assert_eq!(reloaded, recipes);

        let pc = PcFacts {
            vendor: "nvidia".into(),
            gpu_strong: true,
            gpu_rtx: true,
            vram_gb: 12,
            hdr_nits: 600,
            fullscreen: false,
        };
        let changes = resolve_changes(&reloaded[0], &pc);
        assert!(changes.iter().any(|c| c.key == "shadowQuality" && c.value == "kSettingLevel_Ultra"));
        assert!(changes.iter().any(|c| c.key == "windowed" && c.value == "2"));
    }

    #[test]
    fn safety_rejects_install_root_vsync_fpscap_and_anticheat() {
        let bad_root = SAMPLE.replace("$documents(", "$install(");
        assert!(matches!(parse_and_validate(&bad_root), Err(TuneError::Safety(_))));

        let bad_vsync = SAMPLE.replace("\"textureQuality\"", "\"VSync\"");
        assert!(matches!(parse_and_validate(&bad_vsync), Err(TuneError::Safety(_))));

        let bad_ac = SAMPLE.replace("\"PlayRDR2\"", "\"vgtray\"");
        assert!(matches!(parse_and_validate(&bad_ac), Err(TuneError::Safety(_))));

        let bad_trav = SAMPLE.replace("Rockstar Games", "../Windows");
        assert!(matches!(parse_and_validate(&bad_trav), Err(TuneError::Safety(_))));
    }

    #[test]
    fn format_preserving_patch_and_exact_rollback() {
        let xml = "<?xml version=\"1.0\"?>\r\n<rage__fwuiSystemSettingsCollection>\r\n  <graphics>\r\n    <textureQuality>kSettingLevel_Medium</textureQuality>\r\n    <shadowQuality>kSettingLevel_Low</shadowQuality>\r\n    <untouched value=\"42\" />\r\n  </graphics>\r\n  <video>\r\n    <windowed value=\"1\" />\r\n  </video>\r\n</rage__fwuiSystemSettingsCollection>";
        let recipes = parse_and_validate(SAMPLE).unwrap();
        let pc = PcFacts {
            vendor: "nvidia".into(),
            gpu_strong: false,
            gpu_rtx: true,
            vram_gb: 8,
            hdr_nits: 0,
            fullscreen: false,
        };
        let changes = resolve_changes(&recipes[0], &pc);
        let (patched, log) = patch_config(ConfigFormat::Xml, xml, &changes);
        assert_eq!(log.len(), 3);
        assert!(patched.contains("<textureQuality>kSettingLevel_Ultra</textureQuality>"));
        assert!(patched.contains("<shadowQuality>kSettingLevel_High</shadowQuality>"));
        assert!(patched.contains("<windowed value=\"2\" />"));
        assert!(patched.contains("<untouched value=\"42\" />"));
        let restored = rollback_config(ConfigFormat::Xml, &patched, &log);
        assert_eq!(restored, xml);
    }
}
