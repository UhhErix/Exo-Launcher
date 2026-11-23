use serde::Deserialize;
use std::sync::OnceLock;

const LAYOUTS_JSON: &str = include_str!("../../../../ui/contracts/keyboard-layouts.json");

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum FormFactor {
    Sixty,
    Eighty,
    Tkl,
    Full,
    Keypad3,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum LayoutCode {
    Ansi,
    Iso,
    Jis,
    AnsiSplit,
    IsoSplit,
    Keypad,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Default, Deserialize)]
#[serde(rename_all = "kebab-case")]
pub enum KeyKind {
    #[default]
    Alpha,
    Mod,
    Wasd,
    Space,
    Gap,
    LedBar,
    Analog,
}

#[derive(Debug, Clone, Deserialize)]
pub struct KeyCap {
    pub id: String,
    pub label: String,
    pub u: f32,
    #[serde(default)]
    pub kind: KeyKind,
    #[serde(default)]
    pub note: Option<String>,
}

#[derive(Debug, Clone)]
pub struct KeyboardLayoutSpec {
    pub form: FormFactor,
    pub layout_code: LayoutCode,
    pub main_rows: Vec<Vec<KeyCap>>,
    pub nav_rows: Vec<Vec<KeyCap>>,
    pub num_rows: Vec<Vec<KeyCap>>,
    pub has_led_bar: bool,
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct MainRow1 {
    standard: Vec<KeyCap>,
    jis: Vec<KeyCap>,
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct MainRowVariants {
    ansi: Vec<KeyCap>,
    iso: Vec<KeyCap>,
    jis: Vec<KeyCap>,
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct BottomRows {
    standard: Vec<KeyCap>,
    split: Vec<KeyCap>,
    jis: Vec<KeyCap>,
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct LayoutData {
    fn_row: Vec<KeyCap>,
    main_row1: MainRow1,
    main_row2: MainRowVariants,
    main_row3: MainRowVariants,
    main_row4: MainRowVariants,
    bottom_rows: BottomRows,
    tkl_nav_rows: Vec<Vec<KeyCap>>,
    w80_nav_rows: Vec<Vec<KeyCap>>,
    full_num_rows: Vec<Vec<KeyCap>>,
    uwu_rows: Vec<Vec<KeyCap>>,
}

fn layout_data() -> &'static LayoutData {
    static DATA: OnceLock<LayoutData> = OnceLock::new();
    DATA.get_or_init(|| serde_json::from_str(LAYOUTS_JSON).expect("valid keyboard-layouts.json"))
}

pub fn resolve_form_factor(form_factor: Option<&str>, model: Option<&str>) -> FormFactor {
    match form_factor.unwrap_or("") {
        "60" => return FormFactor::Sixty,
        "80" => return FormFactor::Eighty,
        "tkl" => return FormFactor::Tkl,
        "full" => return FormFactor::Full,
        "keypad3" => return FormFactor::Keypad3,
        _ => {}
    }
    let m = model.unwrap_or("").to_ascii_lowercase();
    if m.contains("uwu") {
        FormFactor::Keypad3
    } else if m.contains("two") || m.contains("full") || m.contains("100") {
        FormFactor::Full
    } else if m.contains("80") {
        FormFactor::Eighty
    } else if m.contains("one") || m.contains("tkl") {
        FormFactor::Tkl
    } else {
        FormFactor::Sixty
    }
}

pub fn resolve_layout_code(
    layout_code: Option<&str>,
    layout_label: Option<&str>,
    form: FormFactor,
) -> LayoutCode {
    if form == FormFactor::Keypad3 {
        return LayoutCode::Keypad;
    }
    match layout_code
        .unwrap_or("")
        .trim()
        .to_ascii_lowercase()
        .as_str()
    {
        "ansi" => return LayoutCode::Ansi,
        "iso" => return LayoutCode::Iso,
        "jis" => return LayoutCode::Jis,
        "ansi-split" => return LayoutCode::AnsiSplit,
        "iso-split" => return LayoutCode::IsoSplit,
        "keypad" => return LayoutCode::Keypad,
        _ => {}
    }
    let text = layout_label.unwrap_or("").to_ascii_lowercase();
    let split = text.contains("split");
    if text.contains("jis") {
        LayoutCode::Jis
    } else if text.contains("iso") {
        if split {
            LayoutCode::IsoSplit
        } else {
            LayoutCode::Iso
        }
    } else if split {
        LayoutCode::AnsiSplit
    } else {
        LayoutCode::Ansi
    }
}

fn build_main_rows(form: FormFactor, layout: LayoutCode) -> Vec<Vec<KeyCap>> {
    let data = layout_data();
    let is_iso = matches!(layout, LayoutCode::Iso | LayoutCode::IsoSplit);
    let is_jis = layout == LayoutCode::Jis;
    let is_split = matches!(layout, LayoutCode::AnsiSplit | LayoutCode::IsoSplit);

    let mut row1 = if is_jis {
        data.main_row1.jis.clone()
    } else {
        data.main_row1.standard.clone()
    };
    if form != FormFactor::Sixty
        && let Some(first) = row1.first_mut()
    {
        first.label = "~".into();
    }

    let (row2, row3, row4) = if is_jis {
        (
            data.main_row2.jis.clone(),
            data.main_row3.jis.clone(),
            data.main_row4.jis.clone(),
        )
    } else if is_iso {
        (
            data.main_row2.iso.clone(),
            data.main_row3.iso.clone(),
            data.main_row4.iso.clone(),
        )
    } else {
        (
            data.main_row2.ansi.clone(),
            data.main_row3.ansi.clone(),
            data.main_row4.ansi.clone(),
        )
    };

    let row5 = if is_jis {
        data.bottom_rows.jis.clone()
    } else if is_split {
        data.bottom_rows.split.clone()
    } else {
        data.bottom_rows.standard.clone()
    };

    vec![row1, row2, row3, row4, row5]
}

pub fn build_keyboard_layout(
    form_factor: Option<&str>,
    layout_code: Option<&str>,
    model: Option<&str>,
    layout_label: Option<&str>,
) -> KeyboardLayoutSpec {
    let data = layout_data();
    let form = resolve_form_factor(form_factor, model);
    let resolved = resolve_layout_code(layout_code, layout_label, form);

    if form == FormFactor::Keypad3 {
        return KeyboardLayoutSpec {
            form,
            layout_code: LayoutCode::Keypad,
            main_rows: data.uwu_rows.clone(),
            nav_rows: Vec::new(),
            num_rows: Vec::new(),
            has_led_bar: false,
        };
    }

    let base_main = build_main_rows(form, resolved);
    let mut main_rows = Vec::with_capacity(6);
    if form != FormFactor::Sixty {
        main_rows.push(data.fn_row.clone());
    }
    main_rows.extend(base_main);

    let nav_rows = match form {
        FormFactor::Sixty => Vec::new(),
        FormFactor::Eighty => data.w80_nav_rows.clone(),
        FormFactor::Tkl | FormFactor::Full => data.tkl_nav_rows.clone(),
        FormFactor::Keypad3 => Vec::new(),
    };

    let num_rows = if form == FormFactor::Full {
        data.full_num_rows.clone()
    } else {
        Vec::new()
    };

    KeyboardLayoutSpec {
        form,
        layout_code: resolved,
        main_rows,
        nav_rows,
        num_rows,
        has_led_bar: form == FormFactor::Eighty,
    }
}

pub fn find_key<'a>(spec: &'a KeyboardLayoutSpec, key_id: &str) -> Option<&'a KeyCap> {
    spec.main_rows
        .iter()
        .chain(spec.nav_rows.iter())
        .chain(spec.num_rows.iter())
        .flatten()
        .find(|k| k.id == key_id)
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::collections::HashSet;

    fn sum_u(row: &[KeyCap]) -> f32 {
        row.iter().map(|k| k.u).sum()
    }

    #[test]
    fn every_form_factor_and_layout_has_valid_geometry_and_unique_ids() {
        for form in ["60", "80", "tkl", "full"] {
            for layout in ["ansi", "iso", "jis", "ansi-split", "iso-split"] {
                let spec = build_keyboard_layout(Some(form), Some(layout), None, None);
                let expected_rows = if form == "60" { 5 } else { 6 };
                assert_eq!(spec.main_rows.len(), expected_rows, "{form}/{layout}");
                for row in &spec.main_rows {
                    assert!(
                        (sum_u(row) - 15.0).abs() < 0.01,
                        "{form}/{layout} main row sum"
                    );
                }
                for row in &spec.nav_rows {
                    assert!(
                        (sum_u(row) - 3.0).abs() < 0.01,
                        "{form}/{layout} nav row sum"
                    );
                }
                for row in &spec.num_rows {
                    assert!(
                        (sum_u(row) - 4.0).abs() < 0.01,
                        "{form}/{layout} num row sum"
                    );
                }
                let mut ids = HashSet::new();
                for cap in spec
                    .main_rows
                    .iter()
                    .chain(spec.nav_rows.iter())
                    .chain(spec.num_rows.iter())
                    .flatten()
                {
                    assert!(ids.insert(cap.id.as_str()), "duplicate key id {}", cap.id);
                }
            }
        }
    }

    #[test]
    fn split_spacebar_80he_led_bar_full_a1_a3_and_uwu_match_spec() {
        let split = build_keyboard_layout(Some("60"), Some("ansi-split"), None, None);
        let bottom = split.main_rows.last().expect("bottom row");
        let sl = bottom.iter().find(|k| k.id == "space-l").expect("space-l");
        let mid = bottom.iter().find(|k| k.id == "fn-mid").expect("fn-mid");
        let sr = bottom.iter().find(|k| k.id == "space-r").expect("space-r");
        assert!((sl.u - 2.75).abs() < 0.01);
        assert!((mid.u - 1.25).abs() < 0.01);
        assert!((sr.u - 2.25).abs() < 0.01);

        let w80 = build_keyboard_layout(Some("80"), Some("ansi"), None, None);
        assert!(w80.has_led_bar);
        assert!(find_key(&w80, "led-bar").is_some());
        assert!(find_key(&w80, "mode").is_some());

        let full = build_keyboard_layout(Some("full"), Some("ansi"), None, None);
        assert!(find_key(&full, "a1").is_some());
        assert!(find_key(&full, "a2").is_some());
        assert!(find_key(&full, "a3").is_some());
        assert!(find_key(&full, "mode").is_some());

        let uwu = build_keyboard_layout(Some("keypad3"), None, Some("Wooting UwU RGB"), None);
        assert_eq!(uwu.layout_code, LayoutCode::Keypad);
        assert_eq!(uwu.main_rows.len(), 2);
        assert!((sum_u(&uwu.main_rows[0]) - 4.0).abs() < 0.01);
        assert!((sum_u(&uwu.main_rows[1]) - 4.0).abs() < 0.01);
    }
}
