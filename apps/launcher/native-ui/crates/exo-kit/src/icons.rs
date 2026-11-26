// Keyline Icons v3.0.0 Rounded (MIT) — https://keylineicons.com
// Source commit: bff62150b550b24ca5d5e0e38206592091a4ca53

use rune::{Color, El, Icon, Shape, canvas, icon};

pub fn exo_mark(size: f32, color: Color) -> El {
    let slant = 1.75;
    let bar = |x: f32, y: f32, w: f32, h: f32| -> Vec<[f32; 2]> {
        vec![
            [x + slant, y],
            [x + w + slant, y],
            [x + w, y + h],
            [x, y + h],
        ]
    };
    let shape = Shape::new()
        .view_box(64.0, 64.0)
        .fill(bar(16.8, 13.0, 36.0, 9.0), color)
        .fill(bar(14.0, 27.5, 24.0, 9.0), color)
        .fill(bar(11.2, 42.0, 36.0, 9.0), color);
    canvas(shape).size(size, size)
}

pub fn exo_app_icon(size: f32) -> El {
    let r = size * 0.3;
    let full = || rune::el().pin(Some(0.0), Some(0.0), Some(0.0), Some(0.0));
    rune::el()
        .size(size, size)
        .radius(r)
        .continuous_corners()
        .clip_rounded(r)
        .linear_gradient(
            std::f32::consts::PI,
            [
                (0.0, crate::app_icon_top()),
                (0.45, crate::app_icon_mid()),
                (1.0, crate::app_icon_bottom()),
            ],
        )
        .border_gradient(1.0, Color::WHITE.alpha(0.47), Color::WHITE.alpha(0.16))
        .child(full().radial_gradient(
            [0.5, -0.1],
            size * 0.75,
            [(0.0, Color::WHITE.alpha(0.12)), (1.0, Color::TRANSPARENT)],
        ))
        .child(full().center().child(exo_mark(size, Color::WHITE)))
}

const ICON_MICROPHONE_SM: Icon = Icon {
    name: "kl-mic-165",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.3" stroke-linecap="round" stroke-linejoin="round"><path d="M9 5C9 3.343146 10.343146 2 12 2L12 2C13.656854 2 15 3.343146 15 5L15 11C15 12.656854 13.656854 14 12 14L12 14C10.343146 14 9 12.656854 9 11ZM19 11C19 14.865993 15.865993 18 12 18C8.134007 18 5 14.865993 5 11M12 18V22M8 22H16"/></g>"#,
};
const ICON_MICROPHONE_LG: Icon = Icon {
    name: "kl-mic-185",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M9 5C9 3.343146 10.343146 2 12 2L12 2C13.656854 2 15 3.343146 15 5L15 11C15 12.656854 13.656854 14 12 14L12 14C10.343146 14 9 12.656854 9 11ZM19 11C19 14.865993 15.865993 18 12 18C8.134007 18 5 14.865993 5 11M12 18V22M8 22H16"/></g>"#,
};

pub fn icon_microphone(size: f32, color: Color) -> El {
    let glyph = if size < 24.0 {
        &ICON_MICROPHONE_SM
    } else {
        &ICON_MICROPHONE_LG
    };
    icon(glyph, size).color(color)
}

const ICON_GRID_SM: Icon = Icon {
    name: "kl-grid-2x2-165",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.3" stroke-linecap="round" stroke-linejoin="round"><path d="M5.5 4H8.5C9.3284 4 10 4.6716 10 5.5V8.5C10 9.3284 9.3284 10 8.5 10H5.5C4.6716 10 4 9.3284 4 8.5V5.5C4 4.6716 4.6716 4 5.5 4ZM15.5 4H18.5C19.3284 4 20 4.6716 20 5.5V8.5C20 9.3284 19.3284 10 18.5 10H15.5C14.6716 10 14 9.3284 14 8.5V5.5C14 4.6716 14.6716 4 15.5 4ZM5.5 14H8.5C9.3284 14 10 14.6716 10 15.5V18.5C10 19.3284 9.3284 20 8.5 20H5.5C4.6716 20 4 19.3284 4 18.5V15.5C4 14.6716 4.6716 14 5.5 14ZM15.5 14H18.5C19.3284 14 20 14.6716 20 15.5V18.5C20 19.3284 19.3284 20 18.5 20H15.5C14.6716 20 14 19.3284 14 18.5V15.5C14 14.6716 14.6716 14 15.5 14Z"/></g>"#,
};
const ICON_GRID_LG: Icon = Icon {
    name: "kl-grid-2x2-185",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M5.5 4H8.5C9.3284 4 10 4.6716 10 5.5V8.5C10 9.3284 9.3284 10 8.5 10H5.5C4.6716 10 4 9.3284 4 8.5V5.5C4 4.6716 4.6716 4 5.5 4ZM15.5 4H18.5C19.3284 4 20 4.6716 20 5.5V8.5C20 9.3284 19.3284 10 18.5 10H15.5C14.6716 10 14 9.3284 14 8.5V5.5C14 4.6716 14.6716 4 15.5 4ZM5.5 14H8.5C9.3284 14 10 14.6716 10 15.5V18.5C10 19.3284 9.3284 20 8.5 20H5.5C4.6716 20 4 19.3284 4 18.5V15.5C4 14.6716 4.6716 14 5.5 14ZM15.5 14H18.5C19.3284 14 20 14.6716 20 15.5V18.5C20 19.3284 19.3284 20 18.5 20H15.5C14.6716 20 14 19.3284 14 18.5V15.5C14 14.6716 14.6716 14 15.5 14Z"/></g>"#,
};

pub fn icon_grid(size: f32, color: Color) -> El {
    let glyph = if size < 24.0 {
        &ICON_GRID_SM
    } else {
        &ICON_GRID_LG
    };
    icon(glyph, size).color(color)
}

const ICON_SEARCH_SM: Icon = Icon {
    name: "kl-search-165",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.3" stroke-linecap="round" stroke-linejoin="round"><path d="M17 10C17 13.866 13.866 17 10 17C6.13401 17 3 13.866 3 10C3 6.13401 6.13401 3 10 3C13.866 3 17 6.13401 17 10ZM15 15L21 21"/></g>"#,
};
const ICON_SEARCH_LG: Icon = Icon {
    name: "kl-search-185",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M17 10C17 13.866 13.866 17 10 17C6.13401 17 3 13.866 3 10C3 6.13401 6.13401 3 10 3C13.866 3 17 6.13401 17 10ZM15 15L21 21"/></g>"#,
};

pub fn icon_search(size: f32, color: Color) -> El {
    let glyph = if size < 24.0 {
        &ICON_SEARCH_SM
    } else {
        &ICON_SEARCH_LG
    };
    icon(glyph, size).color(color)
}

const ICON_PLAY_SOLID: Icon = Icon {
    name: "kl-play-solid",
    view: 24.0,
    body: r#"<g fill="currentColor" stroke="none"><path d="M5 6C5 4.8436 5.6646 3.7902 6.7083 3.2923C7.752 2.7944 8.989 2.9407 9.8877 3.6684L18.2585 10.4456C18.7275 10.8253 19 11.3965 19 12C19 12.6035 18.7275 13.1747 18.2585 13.5544L9.8877 20.3316C8.989 21.0593 7.752 21.2056 6.7083 20.7077C5.6646 20.2098 5 19.1564 5 18Z" fill="currentColor"/></g>"#,
};

pub fn icon_play(size: f32, color: Color) -> El {
    icon(&ICON_PLAY_SOLID, size).color(color)
}

const ICON_STOP_SOLID: Icon = Icon {
    name: "kl-square-solid",
    view: 24.0,
    body: r#"<g fill="currentColor" stroke="none"><path d="M2 6C2 3.79086 3.79086 2 6 2H18C20.2091 2 22 3.79086 22 6V18C22 20.2091 20.2091 22 18 22H6C3.79086 22 2 20.2091 2 18V6Z" fill="currentColor"/></g>"#,
};

pub fn icon_stop(size: f32, color: Color) -> El {
    icon(&ICON_STOP_SOLID, size).color(color)
}

const ICON_REFRESH_SM: Icon = Icon {
    name: "kl-refresh-cw-165",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.3" stroke-linecap="round" stroke-linejoin="round"><path d="M3.2117 13.9407C3.071 13.3034 3 12.6527 3 12C3 7.0294 7.0294 3 12 3C14.4831 3 16.8559 4.0259 18.5568 5.8349M19.7143 3L19.2707 6.105C19.2317 6.3784 18.9784 6.5683 18.705 6.5293L15.6 6.0857M20.7883 10.0593C20.929 10.6966 21 11.3473 21 12C21 16.9706 16.9706 21 12 21C9.5169 21 7.1441 19.9741 5.4432 18.1651M4.2857 21L4.7293 17.895C4.7683 17.6216 5.0216 17.4317 5.295 17.4707L8.4 17.9143"/></g>"#,
};
const ICON_REFRESH_LG: Icon = Icon {
    name: "kl-refresh-cw-185",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M3.2117 13.9407C3.071 13.3034 3 12.6527 3 12C3 7.0294 7.0294 3 12 3C14.4831 3 16.8559 4.0259 18.5568 5.8349M19.7143 3L19.2707 6.105C19.2317 6.3784 18.9784 6.5683 18.705 6.5293L15.6 6.0857M20.7883 10.0593C20.929 10.6966 21 11.3473 21 12C21 16.9706 16.9706 21 12 21C9.5169 21 7.1441 19.9741 5.4432 18.1651M4.2857 21L4.7293 17.895C4.7683 17.6216 5.0216 17.4317 5.295 17.4707L8.4 17.9143"/></g>"#,
};

pub fn icon_refresh(size: f32, color: Color) -> El {
    let glyph = if size < 24.0 {
        &ICON_REFRESH_SM
    } else {
        &ICON_REFRESH_LG
    };
    icon(glyph, size).color(color)
}

const ICON_STORE_SM: Icon = Icon {
    name: "kl-store-165",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.3" stroke-linecap="round" stroke-linejoin="round"><path d="M5.5 8.5H18.5C19.6046 8.5 20.5 9.3954 20.5 10.5L19.7 18.2C19.55 19.8 18.2 21 16.6 21H7.4C5.8 21 4.45 19.8 4.3 18.2L3.5 10.5C3.5 9.3954 4.3954 8.5 5.5 8.5ZM8.5 8.5V6.5C8.5 4.567 10.067 3 12 3C13.933 3 15.5 4.567 15.5 6.5V8.5"/></g>"#,
};
const ICON_STORE_LG: Icon = Icon {
    name: "kl-store-185",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M5.5 8.5H18.5C19.6046 8.5 20.5 9.3954 20.5 10.5L19.7 18.2C19.55 19.8 18.2 21 16.6 21H7.4C5.8 21 4.45 19.8 4.3 18.2L3.5 10.5C3.5 9.3954 4.3954 8.5 5.5 8.5ZM8.5 8.5V6.5C8.5 4.567 10.067 3 12 3C13.933 3 15.5 4.567 15.5 6.5V8.5"/></g>"#,
};

pub fn icon_store(size: f32, color: Color) -> El {
    let glyph = if size < 24.0 {
        &ICON_STORE_SM
    } else {
        &ICON_STORE_LG
    };
    icon(glyph, size).color(color)
}

const ICON_CHAT_SM: Icon = Icon {
    name: "kl-message-lines-165",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.3" stroke-linecap="round" stroke-linejoin="round"><path d="M5.2 16.5C3.8 15.2 3 13.4 3 11.5C3 7.3579 7.0294 4 12 4C16.9706 4 21 7.3579 21 11.5C21 15.6421 16.9706 19 12 19C10.9 19 9.85 18.83 8.88 18.52L4.8 20.2L5.2 16.5Z"/></g>"#,
};
const ICON_CHAT_LG: Icon = Icon {
    name: "kl-message-lines-185",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M5.2 16.5C3.8 15.2 3 13.4 3 11.5C3 7.3579 7.0294 4 12 4C16.9706 4 21 7.3579 21 11.5C21 15.6421 16.9706 19 12 19C10.9 19 9.85 18.83 8.88 18.52L4.8 20.2L5.2 16.5Z"/></g>"#,
};

pub fn icon_chat(size: f32, color: Color) -> El {
    let glyph = if size < 24.0 {
        &ICON_CHAT_SM
    } else {
        &ICON_CHAT_LG
    };
    icon(glyph, size).color(color)
}

const ICON_MUSIC_SM: Icon = Icon {
    name: "kl-music-note-165",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.3" stroke-linecap="round" stroke-linejoin="round"><path d="M9 18C9 19.6569 7.6569 21 6 21C4.3431 21 3 19.6569 3 18C3 16.3431 4.3431 15 6 15C7.6569 15 9 16.3431 9 18ZM21 16C21 17.6569 19.6569 19 18 19C16.3431 19 15 17.6569 15 16C15 14.3431 16.3431 13 18 13C19.6569 13 21 14.3431 21 16ZM9 18V5L21 3V16"/></g>"#,
};
const ICON_MUSIC_LG: Icon = Icon {
    name: "kl-music-note-185",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M9 18C9 19.6569 7.6569 21 6 21C4.3431 21 3 19.6569 3 18C3 16.3431 4.3431 15 6 15C7.6569 15 9 16.3431 9 18ZM21 16C21 17.6569 19.6569 19 18 19C16.3431 19 15 17.6569 15 16C15 14.3431 16.3431 13 18 13C19.6569 13 21 14.3431 21 16ZM9 18V5L21 3V16"/></g>"#,
};

pub fn icon_music(size: f32, color: Color) -> El {
    let glyph = if size < 24.0 {
        &ICON_MUSIC_SM
    } else {
        &ICON_MUSIC_LG
    };
    icon(glyph, size).color(color)
}

const ICON_CPU_SM: Icon = Icon {
    name: "kl-cpu-165",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.3" stroke-linecap="round" stroke-linejoin="round"><path d="M7 4L17 4C18.6569 4 20 5.3431 20 7L20 17C20 18.6569 18.6569 20 17 20L7 20C5.3431 20 4 18.6569 4 17L4 7C4 5.3431 5.3431 4 7 4ZM9 8L15 8C15.5523 8 16 8.4477 16 9L16 15C16 15.5523 15.5523 16 15 16L9 16C8.4477 16 8 15.5523 8 15L8 9C8 8.4477 8.4477 8 9 8ZM9 4L9 2M9 20L9 22M4 9L2 9M20 9L22 9M15 4L15 2M15 20L15 22M4 15L2 15M20 15L22 15" fill="none"/></g>"#,
};
const ICON_CPU_LG: Icon = Icon {
    name: "kl-cpu-185",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M7 4L17 4C18.6569 4 20 5.3431 20 7L20 17C20 18.6569 18.6569 20 17 20L7 20C5.3431 20 4 18.6569 4 17L4 7C4 5.3431 5.3431 4 7 4ZM9 8L15 8C15.5523 8 16 8.4477 16 9L16 15C16 15.5523 15.5523 16 15 16L9 16C8.4477 16 8 15.5523 8 15L8 9C8 8.4477 8.4477 8 9 8ZM9 4L9 2M9 20L9 22M4 9L2 9M20 9L22 9M15 4L15 2M15 20L15 22M4 15L2 15M20 15L22 15" fill="none"/></g>"#,
};

pub fn icon_cpu(size: f32, color: Color) -> El {
    let glyph = if size < 24.0 {
        &ICON_CPU_SM
    } else {
        &ICON_CPU_LG
    };
    icon(glyph, size).color(color)
}

const ICON_SETTINGS_SM: Icon = Icon {
    name: "kl-settings-165",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.3" stroke-linecap="round" stroke-linejoin="round"><path d="M9.8826 3.31L9.6447 5.26C9.5876 5.73 9.3203 6.14 8.9222 6.37L8.7712 6.46C8.3732 6.7 7.8905 6.73 7.467 6.55L5.6993 5.78C5.0174 5.49 4.228 5.76 3.8579 6.41L3.1967 7.59C2.8267 8.24 2.9918 9.07 3.5819 9.53L5.1117 10.71C5.4781 11 5.6935 11.44 5.6935 11.91V12.09C5.6935 12.56 5.4781 13 5.1117 13.29L3.5819 14.47C2.9918 14.93 2.8267 15.76 3.1967 16.41L3.8579 17.59C4.228 18.24 5.0174 18.51 5.6993 18.22L7.467 17.45C7.8905 17.27 8.3732 17.3 8.7712 17.54L8.9222 17.63C9.3203 17.86 9.5876 18.27 9.6447 18.74L9.8826 20.69C9.9744 21.44 10.5987 22 11.3388 22H12.6612C13.4013 22 14.0256 21.44 14.1173 20.69L14.3554 18.74C14.4124 18.27 14.6798 17.86 15.0778 17.63L15.2287 17.54C15.6268 17.3 16.1095 17.27 16.5329 17.45L18.3007 18.22C18.9826 18.51 19.772 18.24 20.1421 17.59L20.8033 16.41C21.1733 15.76 21.0082 14.93 20.4181 14.47L18.8883 13.29C18.5219 13 18.3066 12.56 18.3066 12.09V11.91C18.3066 11.44 18.5219 11 18.8883 10.71L20.4181 9.53C21.0082 9.07 21.1733 8.24 20.8033 7.59L20.1421 6.41C19.772 5.76 18.9826 5.49 18.3007 5.78L16.5329 6.55C16.1095 6.73 15.6268 6.7 15.2287 6.46L15.0778 6.37C14.6798 6.14 14.4124 5.73 14.3554 5.26L14.1173 3.31C14.0256 2.56 13.4013 2 12.6612 2H11.3388C10.5987 2 9.9744 2.56 9.8826 3.31ZM12 9.5C13.3807 9.5 14.5 10.6193 14.5 12C14.5 13.3807 13.3807 14.5 12 14.5C10.6193 14.5 9.5 13.3807 9.5 12C9.5 10.6193 10.6193 9.5 12 9.5Z"/></g>"#,
};
const ICON_SETTINGS_LG: Icon = Icon {
    name: "kl-settings-185",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M9.8826 3.31L9.6447 5.26C9.5876 5.73 9.3203 6.14 8.9222 6.37L8.7712 6.46C8.3732 6.7 7.8905 6.73 7.467 6.55L5.6993 5.78C5.0174 5.49 4.228 5.76 3.8579 6.41L3.1967 7.59C2.8267 8.24 2.9918 9.07 3.5819 9.53L5.1117 10.71C5.4781 11 5.6935 11.44 5.6935 11.91V12.09C5.6935 12.56 5.4781 13 5.1117 13.29L3.5819 14.47C2.9918 14.93 2.8267 15.76 3.1967 16.41L3.8579 17.59C4.228 18.24 5.0174 18.51 5.6993 18.22L7.467 17.45C7.8905 17.27 8.3732 17.3 8.7712 17.54L8.9222 17.63C9.3203 17.86 9.5876 18.27 9.6447 18.74L9.8826 20.69C9.9744 21.44 10.5987 22 11.3388 22H12.6612C13.4013 22 14.0256 21.44 14.1173 20.69L14.3554 18.74C14.4124 18.27 14.6798 17.86 15.0778 17.63L15.2287 17.54C15.6268 17.3 16.1095 17.27 16.5329 17.45L18.3007 18.22C18.9826 18.51 19.772 18.24 20.1421 17.59L20.8033 16.41C21.1733 15.76 21.0082 14.93 20.4181 14.47L18.8883 13.29C18.5219 13 18.3066 12.56 18.3066 12.09V11.91C18.3066 11.44 18.5219 11 18.8883 10.71L20.4181 9.53C21.0082 9.07 21.1733 8.24 20.8033 7.59L20.1421 6.41C19.772 5.76 18.9826 5.49 18.3007 5.78L16.5329 6.55C16.1095 6.73 15.6268 6.7 15.2287 6.46L15.0778 6.37C14.6798 6.14 14.4124 5.73 14.3554 5.26L14.1173 3.31C14.0256 2.56 13.4013 2 12.6612 2H11.3388C10.5987 2 9.9744 2.56 9.8826 3.31ZM12 9.5C13.3807 9.5 14.5 10.6193 14.5 12C14.5 13.3807 13.3807 14.5 12 14.5C10.6193 14.5 9.5 13.3807 9.5 12C9.5 10.6193 10.6193 9.5 12 9.5Z"/></g>"#,
};

pub fn icon_settings(size: f32, color: Color) -> El {
    let glyph = if size < 24.0 {
        &ICON_SETTINGS_SM
    } else {
        &ICON_SETTINGS_LG
    };
    icon(glyph, size).color(color)
}

const ICON_CLOSE_SM: Icon = Icon {
    name: "kl-x-165",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.3" stroke-linecap="round" stroke-linejoin="round"><path d="M7 7L17 17M17 7L7 17"/></g>"#,
};
const ICON_CLOSE_LG: Icon = Icon {
    name: "kl-x-185",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M7 7L17 17M17 7L7 17"/></g>"#,
};

pub fn icon_close(size: f32, color: Color) -> El {
    let glyph = if size < 24.0 {
        &ICON_CLOSE_SM
    } else {
        &ICON_CLOSE_LG
    };
    icon(glyph, size).color(color)
}

const ICON_BACK_SM: Icon = Icon {
    name: "kl-arrow-left-165",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.3" stroke-linecap="round" stroke-linejoin="round"><path d="M19 12H5.94129M11.8771 5L5.17385 11.5875C4.94205 11.8153 4.94205 12.1847 5.17385 12.4125L11.8771 19"/></g>"#,
};
const ICON_BACK_LG: Icon = Icon {
    name: "kl-arrow-left-185",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M19 12H5.94129M11.8771 5L5.17385 11.5875C4.94205 11.8153 4.94205 12.1847 5.17385 12.4125L11.8771 19"/></g>"#,
};

pub fn icon_back(size: f32, color: Color) -> El {
    let glyph = if size < 24.0 {
        &ICON_BACK_SM
    } else {
        &ICON_BACK_LG
    };
    icon(glyph, size).color(color)
}

const ICON_FOLDER_SM: Icon = Icon {
    name: "kl-folder-165",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.3" stroke-linecap="round" stroke-linejoin="round"><path d="M3 7C3 5.3431 4.3431 4 6 4L8.6716 4C9.202 4 9.7107 4.2107 10.0858 4.5858L11.4142 5.9142C11.7893 6.2893 12.298 6.5 12.8284 6.5L18 6.5C19.6569 6.5 21 7.8431 21 9.5L21 17C21 18.6569 19.6569 20 18 20L6 20C4.3431 20 3 18.6569 3 17Z"/></g>"#,
};
const ICON_FOLDER_LG: Icon = Icon {
    name: "kl-folder-185",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M3 7C3 5.3431 4.3431 4 6 4L8.6716 4C9.202 4 9.7107 4.2107 10.0858 4.5858L11.4142 5.9142C11.7893 6.2893 12.298 6.5 12.8284 6.5L18 6.5C19.6569 6.5 21 7.8431 21 9.5L21 17C21 18.6569 19.6569 20 18 20L6 20C4.3431 20 3 18.6569 3 17Z"/></g>"#,
};

pub fn icon_folder(size: f32, color: Color) -> El {
    let glyph = if size < 24.0 {
        &ICON_FOLDER_SM
    } else {
        &ICON_FOLDER_LG
    };
    icon(glyph, size).color(color)
}

const ICON_KEY_SM: Icon = Icon {
    name: "kl-key-165",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.3" stroke-linecap="round" stroke-linejoin="round"><path d="M10 12C10 14.2091 8.2091 16 6 16C3.7909 16 2 14.2091 2 12C2 9.7909 3.7909 8 6 8C8.2091 8 10 9.7909 10 12ZM10 12L22 12M17 12L17 16M21 12L21 16" fill="none"/></g>"#,
};
const ICON_KEY_LG: Icon = Icon {
    name: "kl-key-185",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M10 12C10 14.2091 8.2091 16 6 16C3.7909 16 2 14.2091 2 12C2 9.7909 3.7909 8 6 8C8.2091 8 10 9.7909 10 12ZM10 12L22 12M17 12L17 16M21 12L21 16" fill="none"/></g>"#,
};

pub fn icon_key(size: f32, color: Color) -> El {
    let glyph = if size < 24.0 {
        &ICON_KEY_SM
    } else {
        &ICON_KEY_LG
    };
    icon(glyph, size).color(color)
}

const ICON_DOWNLOAD_SM: Icon = Icon {
    name: "kl-download-165",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.3" stroke-linecap="round" stroke-linejoin="round"><path d="M12 3V14M8 10L12 14L16 10M4 18V19C4 20.1046 4.89543 21 6 21H18C19.1046 21 20 20.1046 20 19V18"/></g>"#,
};
const ICON_DOWNLOAD_LG: Icon = Icon {
    name: "kl-download-185",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M12 3V14M8 10L12 14L16 10M4 18V19C4 20.1046 4.89543 21 6 21H18C19.1046 21 20 20.1046 20 19V18"/></g>"#,
};

pub fn icon_download(size: f32, color: Color) -> El {
    let glyph = if size < 24.0 {
        &ICON_DOWNLOAD_SM
    } else {
        &ICON_DOWNLOAD_LG
    };
    icon(glyph, size).color(color)
}

const ICON_WRENCH_SM: Icon = Icon {
    name: "kl-wrench-165",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.3" stroke-linecap="round" stroke-linejoin="round"><path d="M18.682 8.8536L20.2678 7.2678C20.4553 7.0802 20.7097 6.9749 20.9749 6.9749C21.4902 6.9749 21.9211 7.3664 21.9703 7.8794C21.9901 8.0857 22 8.2928 22 8.5C22 12.0899 19.0899 15 15.5 15C11.9101 15 9 12.0899 9 8.5C9 4.9101 11.9101 2 15.5 2C15.7072 2 15.9143 2.0099 16.1206 2.0297C16.6336 2.0789 17.0251 2.5098 17.0251 3.0251C17.0251 3.2903 16.9198 3.5447 16.7322 3.7322L15.1464 5.318C14.6776 5.7869 14.4142 6.4227 14.4142 7.0858C14.4142 8.4665 15.5335 9.5858 16.9142 9.5858C17.5773 9.5858 18.2131 9.3224 18.682 8.8536ZM15.3955 14.9992C15.3688 14.9987 15.342 14.9985 15.3152 14.9985C13.9891 14.9985 12.7173 15.5253 11.7797 16.463L7.1213 21.1213C6.5587 21.6839 5.7956 22 5 22C3.3431 22 2 20.6569 2 19C2 18.2044 2.3161 17.4413 2.8787 16.8787L7.537 12.2203C8.4747 11.2827 9.0015 10.0109 9.0015 8.6848C9.0015 8.658 9.0013 8.6312 9.0008 8.6045"/></g>"#,
};
const ICON_WRENCH_LG: Icon = Icon {
    name: "kl-wrench-185",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M18.682 8.8536L20.2678 7.2678C20.4553 7.0802 20.7097 6.9749 20.9749 6.9749C21.4902 6.9749 21.9211 7.3664 21.9703 7.8794C21.9901 8.0857 22 8.2928 22 8.5C22 12.0899 19.0899 15 15.5 15C11.9101 15 9 12.0899 9 8.5C9 4.9101 11.9101 2 15.5 2C15.7072 2 15.9143 2.0099 16.1206 2.0297C16.6336 2.0789 17.0251 2.5098 17.0251 3.0251C17.0251 3.2903 16.9198 3.5447 16.7322 3.7322L15.1464 5.318C14.6776 5.7869 14.4142 6.4227 14.4142 7.0858C14.4142 8.4665 15.5335 9.5858 16.9142 9.5858C17.5773 9.5858 18.2131 9.3224 18.682 8.8536ZM15.3955 14.9992C15.3688 14.9987 15.342 14.9985 15.3152 14.9985C13.9891 14.9985 12.7173 15.5253 11.7797 16.463L7.1213 21.1213C6.5587 21.6839 5.7956 22 5 22C3.3431 22 2 20.6569 2 19C2 18.2044 2.3161 17.4413 2.8787 16.8787L7.537 12.2203C8.4747 11.2827 9.0015 10.0109 9.0015 8.6848C9.0015 8.658 9.0013 8.6312 9.0008 8.6045"/></g>"#,
};

pub fn icon_wrench(size: f32, color: Color) -> El {
    let glyph = if size < 24.0 {
        &ICON_WRENCH_SM
    } else {
        &ICON_WRENCH_LG
    };
    icon(glyph, size).color(color)
}

const ICON_TRASH_SM: Icon = Icon {
    name: "kl-bin-165",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.3" stroke-linecap="round" stroke-linejoin="round"><path d="M10 2H14M4 7H20M6 7H18V19C18 20.6569 16.6569 22 15 22H9C7.34315 22 6 20.6569 6 19V7Z"/></g>"#,
};
const ICON_TRASH_LG: Icon = Icon {
    name: "kl-bin-185",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M10 2H14M4 7H20M6 7H18V19C18 20.6569 16.6569 22 15 22H9C7.34315 22 6 20.6569 6 19V7Z"/></g>"#,
};

pub fn icon_trash(size: f32, color: Color) -> El {
    let glyph = if size < 24.0 {
        &ICON_TRASH_SM
    } else {
        &ICON_TRASH_LG
    };
    icon(glyph, size).color(color)
}

const ICON_SLIDERS_SM: Icon = Icon {
    name: "kl-sliders-vertical-165",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.3" stroke-linecap="round" stroke-linejoin="round"><path d="M5 3L5 12M5 21L5 16M3 16L7 16M12 3L12 10M12 21L12 14M10 10L14 10M19 3L19 10M19 21L19 14M17 14L21 14" fill="none"/></g>"#,
};
const ICON_SLIDERS_LG: Icon = Icon {
    name: "kl-sliders-vertical-185",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M5 3L5 12M5 21L5 16M3 16L7 16M12 3L12 10M12 21L12 14M10 10L14 10M19 3L19 10M19 21L19 14M17 14L21 14" fill="none"/></g>"#,
};

pub fn icon_sliders(size: f32, color: Color) -> El {
    let glyph = if size < 24.0 {
        &ICON_SLIDERS_SM
    } else {
        &ICON_SLIDERS_LG
    };
    icon(glyph, size).color(color)
}

const ICON_EXTERNAL_SM: Icon = Icon {
    name: "kl-square-arrow-out-up-right-165",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.3" stroke-linecap="round" stroke-linejoin="round"><path d="M9 7L6 7C4.3431 7 3 8.3431 3 10L3 18C3 19.6569 4.3431 21 6 21L14 21C15.6569 21 17 19.6569 17 18L17 15M13 11L20.5 3.5M13 3L20.5 3C20.7761 3 21 3.2239 21 3.5L21 11" fill="none"/></g>"#,
};
const ICON_EXTERNAL_LG: Icon = Icon {
    name: "kl-square-arrow-out-up-right-185",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M9 7L6 7C4.3431 7 3 8.3431 3 10L3 18C3 19.6569 4.3431 21 6 21L14 21C15.6569 21 17 19.6569 17 18L17 15M13 11L20.5 3.5M13 3L20.5 3C20.7761 3 21 3.2239 21 3.5L21 11" fill="none"/></g>"#,
};

pub fn icon_external(size: f32, color: Color) -> El {
    let glyph = if size < 24.0 {
        &ICON_EXTERNAL_SM
    } else {
        &ICON_EXTERNAL_LG
    };
    icon(glyph, size).color(color)
}

const ICON_SPARKLES_SM: Icon = Icon {
    name: "kl-sparkles-165",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.3" stroke-linecap="round" stroke-linejoin="round"><path d="M15.4651 9.1794L16.7409 12.4141C16.8933 12.8006 17.1994 13.1067 17.5859 13.2591L20.8206 14.5349C21.0117 14.6102 21.1372 14.7947 21.1372 15C21.1372 15.2053 21.0117 15.3898 20.8206 15.4651L17.5859 16.7409C17.1994 16.8933 16.8933 17.1994 16.7409 17.5859L15.4651 20.8206C15.3898 21.0117 15.2053 21.1372 15 21.1372C14.7947 21.1372 14.6102 21.0117 14.5349 20.8206L13.2591 17.5859C13.1067 17.1994 12.8006 16.8933 12.4141 16.7409L9.1794 15.4651C8.9883 15.3898 8.8628 15.2053 8.8628 15C8.8628 14.7947 8.9883 14.6102 9.1794 14.5349L12.4141 13.2591C12.8006 13.1067 13.1067 12.8006 13.2591 12.4141L14.5349 9.1794C14.6102 8.9883 14.7947 8.8628 15 8.8628C15.2053 8.8628 15.3898 8.9883 15.4651 9.1794ZM6.7151 3.1794L7.2131 4.4419C7.3655 4.8285 7.6715 5.1345 8.0581 5.2869L9.3206 5.7849C9.5117 5.8602 9.6372 6.0447 9.6372 6.25C9.6372 6.4553 9.5117 6.6398 9.3206 6.7151L8.0581 7.2131C7.6715 7.3655 7.3655 7.6715 7.2131 8.0581L6.7151 9.3206C6.6398 9.5117 6.4553 9.6372 6.25 9.6372C6.0447 9.6372 5.8602 9.5117 5.7849 9.3206L5.2869 8.0581C5.1345 7.6715 4.8285 7.3655 4.4419 7.2131L3.1794 6.7151C2.9883 6.6398 2.8628 6.4553 2.8628 6.25C2.8628 6.0447 2.9883 5.8602 3.1794 5.7849L4.4419 5.2869C4.8285 5.1345 5.1345 4.8285 5.2869 4.4419L5.7849 3.1794C5.8602 2.9883 6.0447 2.8628 6.25 2.8628C6.4553 2.8628 6.6398 2.9883 6.7151 3.1794Z" fill="none"/></g>"#,
};
const ICON_SPARKLES_LG: Icon = Icon {
    name: "kl-sparkles-185",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M15.4651 9.1794L16.7409 12.4141C16.8933 12.8006 17.1994 13.1067 17.5859 13.2591L20.8206 14.5349C21.0117 14.6102 21.1372 14.7947 21.1372 15C21.1372 15.2053 21.0117 15.3898 20.8206 15.4651L17.5859 16.7409C17.1994 16.8933 16.8933 17.1994 16.7409 17.5859L15.4651 20.8206C15.3898 21.0117 15.2053 21.1372 15 21.1372C14.7947 21.1372 14.6102 21.0117 14.5349 20.8206L13.2591 17.5859C13.1067 17.1994 12.8006 16.8933 12.4141 16.7409L9.1794 15.4651C8.9883 15.3898 8.8628 15.2053 8.8628 15C8.8628 14.7947 8.9883 14.6102 9.1794 14.5349L12.4141 13.2591C12.8006 13.1067 13.1067 12.8006 13.2591 12.4141L14.5349 9.1794C14.6102 8.9883 14.7947 8.8628 15 8.8628C15.2053 8.8628 15.3898 8.9883 15.4651 9.1794ZM6.7151 3.1794L7.2131 4.4419C7.3655 4.8285 7.6715 5.1345 8.0581 5.2869L9.3206 5.7849C9.5117 5.8602 9.6372 6.0447 9.6372 6.25C9.6372 6.4553 9.5117 6.6398 9.3206 6.7151L8.0581 7.2131C7.6715 7.3655 7.3655 7.6715 7.2131 8.0581L6.7151 9.3206C6.6398 9.5117 6.4553 9.6372 6.25 9.6372C6.0447 9.6372 5.8602 9.5117 5.7849 9.3206L5.2869 8.0581C5.1345 7.6715 4.8285 7.3655 4.4419 7.2131L3.1794 6.7151C2.9883 6.6398 2.8628 6.4553 2.8628 6.25C2.8628 6.0447 2.9883 5.8602 3.1794 5.7849L4.4419 5.2869C4.8285 5.1345 5.1345 4.8285 5.2869 4.4419L5.7849 3.1794C5.8602 2.9883 6.0447 2.8628 6.25 2.8628C6.4553 2.8628 6.6398 2.9883 6.7151 3.1794Z" fill="none"/></g>"#,
};

pub fn icon_sparkles(size: f32, color: Color) -> El {
    let glyph = if size < 24.0 {
        &ICON_SPARKLES_SM
    } else {
        &ICON_SPARKLES_LG
    };
    icon(glyph, size).color(color)
}

const ICON_GAMEPAD_SM: Icon = Icon {
    name: "kl-gamepad-165",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.3" stroke-linecap="round" stroke-linejoin="round"><path d="M2.0274 16.1864L3.1639 7.5039C3.426 5.5022 5.2078 4 7.3203 4L16.6797 4C18.7922 4 20.5741 5.5022 20.8361 7.5039L21.9726 16.1864C22.2376 18.2114 20.5842 20 18.4472 20C17.069 20 15.8151 19.2389 15.2301 18.0472L14.6927 16.9524C14.3276 16.2085 13.5449 15.7333 12.6845 15.7333L11.3155 15.7333C10.4551 15.7333 9.6724 16.2085 9.3073 16.9524L8.7698 18.0472C8.1849 19.2389 6.931 20 5.5529 20C3.4158 20 1.7624 18.2114 2.0274 16.1864ZM7 10L11 10M9 8L9 12" fill="none"/><path d="M16.6464 8.1464C16.8417 7.9512 17.1583 7.9512 17.3536 8.1464L17.8536 8.6464C18.0488 8.8417 18.0488 9.1583 17.8536 9.3536L17.3536 9.8536C17.1583 10.0488 16.8417 10.0488 16.6464 9.8536L16.1464 9.3536C15.9512 9.1583 15.9512 8.8417 16.1464 8.6464ZM14.3964 11.1464C14.5917 10.9512 14.9083 10.9512 15.1036 11.1464L15.6036 11.6464C15.7988 11.8417 15.7988 12.1583 15.6036 12.3536L15.1036 12.8536C14.9083 13.0488 14.5917 13.0488 14.3964 12.8536L13.8964 12.3536C13.7012 12.1583 13.7012 11.8417 13.8964 11.6464Z" fill="currentColor" fill-rule="evenodd" clip-rule="evenodd" stroke="none"/></g>"#,
};
const ICON_GAMEPAD_LG: Icon = Icon {
    name: "kl-gamepad-185",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M2.0274 16.1864L3.1639 7.5039C3.426 5.5022 5.2078 4 7.3203 4L16.6797 4C18.7922 4 20.5741 5.5022 20.8361 7.5039L21.9726 16.1864C22.2376 18.2114 20.5842 20 18.4472 20C17.069 20 15.8151 19.2389 15.2301 18.0472L14.6927 16.9524C14.3276 16.2085 13.5449 15.7333 12.6845 15.7333L11.3155 15.7333C10.4551 15.7333 9.6724 16.2085 9.3073 16.9524L8.7698 18.0472C8.1849 19.2389 6.931 20 5.5529 20C3.4158 20 1.7624 18.2114 2.0274 16.1864ZM7 10L11 10M9 8L9 12" fill="none"/><path d="M16.6464 8.1464C16.8417 7.9512 17.1583 7.9512 17.3536 8.1464L17.8536 8.6464C18.0488 8.8417 18.0488 9.1583 17.8536 9.3536L17.3536 9.8536C17.1583 10.0488 16.8417 10.0488 16.6464 9.8536L16.1464 9.3536C15.9512 9.1583 15.9512 8.8417 16.1464 8.6464ZM14.3964 11.1464C14.5917 10.9512 14.9083 10.9512 15.1036 11.1464L15.6036 11.6464C15.7988 11.8417 15.7988 12.1583 15.6036 12.3536L15.1036 12.8536C14.9083 13.0488 14.5917 13.0488 14.3964 12.8536L13.8964 12.3536C13.7012 12.1583 13.7012 11.8417 13.8964 11.6464Z" fill="currentColor" fill-rule="evenodd" clip-rule="evenodd" stroke="none"/></g>"#,
};

pub fn icon_gamepad(size: f32, color: Color) -> El {
    let glyph = if size < 24.0 {
        &ICON_GAMEPAD_SM
    } else {
        &ICON_GAMEPAD_LG
    };
    icon(glyph, size).color(color)
}

const ICON_TROPHY_SM: Icon = Icon {
    name: "kl-trophy-165",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.3" stroke-linecap="round" stroke-linejoin="round"><path d="M9 2H15C16.65685 2 18 3.34315 18 5V9C18 12.31371 15.31371 15 12 15C8.68629 15 6 12.31371 6 9V5C6 3.34315 7.34315 2 9 2ZM6 5H4C2.89543 5 2 5.89543 2 7V9C2 10.10457 2.89543 11 4 11H6M18 5H20C21.10457 5 22 5.89543 22 7V9C22 10.10457 21.10457 11 20 11H18M12 15V19M9 19H15C16.65685 19 18 20.34315 18 22H6C6 20.34315 7.34315 19 9 19Z"/></g>"#,
};
const ICON_TROPHY_LG: Icon = Icon {
    name: "kl-trophy-185",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M9 2H15C16.65685 2 18 3.34315 18 5V9C18 12.31371 15.31371 15 12 15C8.68629 15 6 12.31371 6 9V5C6 3.34315 7.34315 2 9 2ZM6 5H4C2.89543 5 2 5.89543 2 7V9C2 10.10457 2.89543 11 4 11H6M18 5H20C21.10457 5 22 5.89543 22 7V9C22 10.10457 21.10457 11 20 11H18M12 15V19M9 19H15C16.65685 19 18 20.34315 18 22H6C6 20.34315 7.34315 19 9 19Z"/></g>"#,
};

pub fn icon_trophy(size: f32, color: Color) -> El {
    let glyph = if size < 24.0 {
        &ICON_TROPHY_SM
    } else {
        &ICON_TROPHY_LG
    };
    icon(glyph, size).color(color)
}

const ICON_SHIELD_SM: Icon = Icon {
    name: "kl-shield-165",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.3" stroke-linecap="round" stroke-linejoin="round"><path d="M5.18251 5.16845C7.83034 4.85425 9.82952 3.53222 11.0242 2.37315C11.537 1.87562 12.463 1.87562 12.9758 2.37315C14.1705 3.53222 16.1697 4.85425 18.8175 5.16845C19.4672 5.24554 20 5.75056 20 6.38285V12.5708C20 14.3452 19.7111 16.1513 18.612 17.5729C17.4097 19.1277 15.5033 20.8703 12.8298 21.8525C12.2945 22.0492 11.7055 22.0492 11.1702 21.8525C8.49668 20.8703 6.59026 19.1277 5.38804 17.5729C4.28885 16.1513 4 14.3452 4 12.5708V6.38285C4 5.75056 4.53284 5.24554 5.18251 5.16845Z"/></g>"#,
};
const ICON_SHIELD_LG: Icon = Icon {
    name: "kl-shield-185",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M5.18251 5.16845C7.83034 4.85425 9.82952 3.53222 11.0242 2.37315C11.537 1.87562 12.463 1.87562 12.9758 2.37315C14.1705 3.53222 16.1697 4.85425 18.8175 5.16845C19.4672 5.24554 20 5.75056 20 6.38285V12.5708C20 14.3452 19.7111 16.1513 18.612 17.5729C17.4097 19.1277 15.5033 20.8703 12.8298 21.8525C12.2945 22.0492 11.7055 22.0492 11.1702 21.8525C8.49668 20.8703 6.59026 19.1277 5.38804 17.5729C4.28885 16.1513 4 14.3452 4 12.5708V6.38285C4 5.75056 4.53284 5.24554 5.18251 5.16845Z"/></g>"#,
};

pub fn icon_shield(size: f32, color: Color) -> El {
    let glyph = if size < 24.0 {
        &ICON_SHIELD_SM
    } else {
        &ICON_SHIELD_LG
    };
    icon(glyph, size).color(color)
}

const ICON_HARD_DRIVE_SM: Icon = Icon {
    name: "kl-hard-drive-165",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.3" stroke-linecap="round" stroke-linejoin="round"><path d="M5 11L19 11C20.6569 11 22 12.3431 22 14L22 17C22 18.6569 20.6569 20 19 20L5 20C3.3431 20 2 18.6569 2 17L2 14C2 12.3431 3.3431 11 5 11ZM2.4954 12.3486L4.1448 6.8506C4.6524 5.1587 6.2097 4 7.9761 4L16.0239 4C17.7903 4 19.3476 5.1587 19.8552 6.8506L21.5046 12.3486"/><path d="M19 15.5C19 16.3284 18.3284 17 17.5 17C16.6716 17 16 16.3284 16 15.5C16 14.6716 16.6716 14 17.5 14C18.3284 14 19 14.6716 19 15.5Z" fill="currentColor" stroke="none"/></g>"#,
};
const ICON_HARD_DRIVE_LG: Icon = Icon {
    name: "kl-hard-drive-185",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M5 11L19 11C20.6569 11 22 12.3431 22 14L22 17C22 18.6569 20.6569 20 19 20L5 20C3.3431 20 2 18.6569 2 17L2 14C2 12.3431 3.3431 11 5 11ZM2.4954 12.3486L4.1448 6.8506C4.6524 5.1587 6.2097 4 7.9761 4L16.0239 4C17.7903 4 19.3476 5.1587 19.8552 6.8506L21.5046 12.3486"/><path d="M19 15.5C19 16.3284 18.3284 17 17.5 17C16.6716 17 16 16.3284 16 15.5C16 14.6716 16.6716 14 17.5 14C18.3284 14 19 14.6716 19 15.5Z" fill="currentColor" stroke="none"/></g>"#,
};

pub fn icon_hard_drive(size: f32, color: Color) -> El {
    let glyph = if size < 24.0 {
        &ICON_HARD_DRIVE_SM
    } else {
        &ICON_HARD_DRIVE_LG
    };
    icon(glyph, size).color(color)
}

const ICON_KEYBOARD_SM: Icon = Icon {
    name: "kl-keyboard-165",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.3" stroke-linecap="round" stroke-linejoin="round"><path d="M5 4L19 4C20.6569 4 22 5.3431 22 7L22 17C22 18.6569 20.6569 20 19 20L5 20C3.3431 20 2 18.6569 2 17L2 7C2 5.3431 3.3431 4 5 4ZM8 16L16 16" fill="none"/><path d="M5.5 7L6.5 7C6.7761 7 7 7.2239 7 7.5L7 8.5C7 8.7761 6.7761 9 6.5 9L5.5 9C5.2239 9 5 8.7761 5 8.5L5 7.5C5 7.2239 5.2239 7 5.5 7ZM9.5 7L10.5 7C10.7761 7 11 7.2239 11 7.5L11 8.5C11 8.7761 10.7761 9 10.5 9L9.5 9C9.2239 9 9 8.7761 9 8.5L9 7.5C9 7.2239 9.2239 7 9.5 7ZM13.5 7L14.5 7C14.7761 7 15 7.2239 15 7.5L15 8.5C15 8.7761 14.7761 9 14.5 9L13.5 9C13.2239 9 13 8.7761 13 8.5L13 7.5C13 7.2239 13.2239 7 13.5 7ZM17.5 7L18.5 7C18.7761 7 19 7.2239 19 7.5L19 8.5C19 8.7761 18.7761 9 18.5 9L17.5 9C17.2239 9 17 8.7761 17 8.5L17 7.5C17 7.2239 17.2239 7 17.5 7ZM5.5 11L6.5 11C6.7761 11 7 11.2239 7 11.5L7 12.5C7 12.7761 6.7761 13 6.5 13L5.5 13C5.2239 13 5 12.7761 5 12.5L5 11.5C5 11.2239 5.2239 11 5.5 11ZM9.5 11L10.5 11C10.7761 11 11 11.2239 11 11.5L11 12.5C11 12.7761 10.7761 13 10.5 13L9.5 13C9.2239 13 9 12.7761 9 12.5L9 11.5C9 11.2239 9.2239 11 9.5 11ZM13.5 11L14.5 11C14.7761 11 15 11.2239 15 11.5L15 12.5C15 12.7761 14.7761 13 14.5 13L13.5 13C13.2239 13 13 12.7761 13 12.5L13 11.5C13 11.2239 13.2239 11 13.5 11ZM17.5 11L18.5 11C18.7761 11 19 11.2239 19 11.5L19 12.5C19 12.7761 18.7761 13 18.5 13L17.5 13C17.2239 13 17 12.7761 17 12.5L17 11.5C17 11.2239 17.2239 11 17.5 11Z" fill="currentColor" stroke="none"/></g>"#,
};
const ICON_KEYBOARD_LG: Icon = Icon {
    name: "kl-keyboard-185",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M5 4L19 4C20.6569 4 22 5.3431 22 7L22 17C22 18.6569 20.6569 20 19 20L5 20C3.3431 20 2 18.6569 2 17L2 7C2 5.3431 3.3431 4 5 4ZM8 16L16 16" fill="none"/><path d="M5.5 7L6.5 7C6.7761 7 7 7.2239 7 7.5L7 8.5C7 8.7761 6.7761 9 6.5 9L5.5 9C5.2239 9 5 8.7761 5 8.5L5 7.5C5 7.2239 5.2239 7 5.5 7ZM9.5 7L10.5 7C10.7761 7 11 7.2239 11 7.5L11 8.5C11 8.7761 10.7761 9 10.5 9L9.5 9C9.2239 9 9 8.7761 9 8.5L9 7.5C9 7.2239 9.2239 7 9.5 7ZM13.5 7L14.5 7C14.7761 7 15 7.2239 15 7.5L15 8.5C15 8.7761 14.7761 9 14.5 9L13.5 9C13.2239 9 13 8.7761 13 8.5L13 7.5C13 7.2239 13.2239 7 13.5 7ZM17.5 7L18.5 7C18.7761 7 19 7.2239 19 7.5L19 8.5C19 8.7761 18.7761 9 18.5 9L17.5 9C17.2239 9 17 8.7761 17 8.5L17 7.5C17 7.2239 17.2239 7 17.5 7ZM5.5 11L6.5 11C6.7761 11 7 11.2239 7 11.5L7 12.5C7 12.7761 6.7761 13 6.5 13L5.5 13C5.2239 13 5 12.7761 5 12.5L5 11.5C5 11.2239 5.2239 11 5.5 11ZM9.5 11L10.5 11C10.7761 11 11 11.2239 11 11.5L11 12.5C11 12.7761 10.7761 13 10.5 13L9.5 13C9.2239 13 9 12.7761 9 12.5L9 11.5C9 11.2239 9.2239 11 9.5 11ZM13.5 11L14.5 11C14.7761 11 15 11.2239 15 11.5L15 12.5C15 12.7761 14.7761 13 14.5 13L13.5 13C13.2239 13 13 12.7761 13 12.5L13 11.5C13 11.2239 13.2239 11 13.5 11ZM17.5 11L18.5 11C18.7761 11 19 11.2239 19 11.5L19 12.5C19 12.7761 18.7761 13 18.5 13L17.5 13C17.2239 13 17 12.7761 17 12.5L17 11.5C17 11.2239 17.2239 11 17.5 11Z" fill="currentColor" stroke="none"/></g>"#,
};

pub fn icon_keyboard(size: f32, color: Color) -> El {
    let glyph = if size < 24.0 {
        &ICON_KEYBOARD_SM
    } else {
        &ICON_KEYBOARD_LG
    };
    icon(glyph, size).color(color)
}

const ICON_VOLUME_SM: Icon = Icon {
    name: "kl-volume-165",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.3" stroke-linecap="round" stroke-linejoin="round"><path d="M2 10C2 9.447715 2.447715 9 3 9L5.477658 9C5.804909 9 6.111453 8.839879 6.298408 8.571288L9.17925 4.432477C9.738582 3.628903 11 4.024692 11 5.003764L11 18.996236C11 19.975308 9.738582 20.371097 9.17925 19.567523L6.298408 15.428712C6.111453 15.160121 5.804909 15 5.477658 15L3 15C2.447715 15 2 14.552285 2 14ZM15.80444 6.909091C17.205764 8.231561 18 10.073179 18 12C18 13.926821 17.205764 15.768439 15.80444 17.090909M18.549834 4C20.751915 6.078168 22 8.972139 22 12C22 15.027861 20.751915 17.921832 18.549834 20"/></g>"#,
};
const ICON_VOLUME_LG: Icon = Icon {
    name: "kl-volume-185",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M2 10C2 9.447715 2.447715 9 3 9L5.477658 9C5.804909 9 6.111453 8.839879 6.298408 8.571288L9.17925 4.432477C9.738582 3.628903 11 4.024692 11 5.003764L11 18.996236C11 19.975308 9.738582 20.371097 9.17925 19.567523L6.298408 15.428712C6.111453 15.160121 5.804909 15 5.477658 15L3 15C2.447715 15 2 14.552285 2 14ZM15.80444 6.909091C17.205764 8.231561 18 10.073179 18 12C18 13.926821 17.205764 15.768439 15.80444 17.090909M18.549834 4C20.751915 6.078168 22 8.972139 22 12C22 15.027861 20.751915 17.921832 18.549834 20"/></g>"#,
};

pub fn icon_volume(size: f32, color: Color) -> El {
    let glyph = if size < 24.0 {
        &ICON_VOLUME_SM
    } else {
        &ICON_VOLUME_LG
    };
    icon(glyph, size).color(color)
}

const ICON_VOLUME_OFF_SM: Icon = Icon {
    name: "kl-volume-off-165",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.3" stroke-linecap="round" stroke-linejoin="round"><path d="M11 11L11 18.9962C11 19.9753 9.7386 20.3711 9.1793 19.5675L6.2984 15.4287C6.1115 15.1601 5.8049 15 5.4777 15L3 15C2.4477 15 2 14.5523 2 14L2 10C2 9.4477 2.4477 9 3 9L5.4777 9C5.8049 9 6.1115 8.8399 6.2984 8.5713L7.2312 7.2312M15.8044 6.9091C17.2058 8.2316 18 10.0732 18 12C18 12.112 17.9973 12.2238 17.992 12.3351M18.5498 4C20.7519 6.0782 22 8.9721 22 12C22 13.2746 21.7788 14.5254 21.3585 15.7016M2 2L22 22"/></g>"#,
};
const ICON_VOLUME_OFF_LG: Icon = Icon {
    name: "kl-volume-off-185",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M11 11L11 18.9962C11 19.9753 9.7386 20.3711 9.1793 19.5675L6.2984 15.4287C6.1115 15.1601 5.8049 15 5.4777 15L3 15C2.4477 15 2 14.5523 2 14L2 10C2 9.4477 2.4477 9 3 9L5.4777 9C5.8049 9 6.1115 8.8399 6.2984 8.5713L7.2312 7.2312M15.8044 6.9091C17.2058 8.2316 18 10.0732 18 12C18 12.112 17.9973 12.2238 17.992 12.3351M18.5498 4C20.7519 6.0782 22 8.9721 22 12C22 13.2746 21.7788 14.5254 21.3585 15.7016M2 2L22 22"/></g>"#,
};

pub fn icon_volume_off(size: f32, color: Color) -> El {
    let glyph = if size < 24.0 {
        &ICON_VOLUME_OFF_SM
    } else {
        &ICON_VOLUME_OFF_LG
    };
    icon(glyph, size).color(color)
}

const ICON_ZAP_SM: Icon = Icon {
    name: "kl-zap-165",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.3" stroke-linecap="round" stroke-linejoin="round"><path d="M12.9611 3.1513L3.1443 13.1497C3.0525 13.2432 3.001 13.369 3.001 13.5C3.001 13.7761 3.2249 14 3.501 14L9.8656 14C10.4178 14 10.8656 14.4477 10.8656 15C10.8656 15.0419 10.8629 15.0839 10.8577 15.1255L10.1861 20.4357C10.1834 20.4565 10.1821 20.4774 10.1821 20.4984C10.1821 20.7745 10.406 20.9984 10.6821 20.9984C10.8163 20.9984 10.9449 20.9445 11.0389 20.8487L20.8557 10.8503C20.9475 10.7568 20.999 10.631 20.999 10.5C20.999 10.2239 20.7751 10 20.499 10L14.1344 10C13.5822 10 13.1344 9.5523 13.1344 9C13.1344 8.9581 13.1371 8.9161 13.1423 8.8745L13.8139 3.5643C13.8166 3.5435 13.8179 3.5226 13.8179 3.5016C13.8179 3.2255 13.594 3.0016 13.3179 3.0016C13.1837 3.0016 13.0551 3.0555 12.9611 3.1513Z" fill="none"/></g>"#,
};
const ICON_ZAP_LG: Icon = Icon {
    name: "kl-zap-185",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M12.9611 3.1513L3.1443 13.1497C3.0525 13.2432 3.001 13.369 3.001 13.5C3.001 13.7761 3.2249 14 3.501 14L9.8656 14C10.4178 14 10.8656 14.4477 10.8656 15C10.8656 15.0419 10.8629 15.0839 10.8577 15.1255L10.1861 20.4357C10.1834 20.4565 10.1821 20.4774 10.1821 20.4984C10.1821 20.7745 10.406 20.9984 10.6821 20.9984C10.8163 20.9984 10.9449 20.9445 11.0389 20.8487L20.8557 10.8503C20.9475 10.7568 20.999 10.631 20.999 10.5C20.999 10.2239 20.7751 10 20.499 10L14.1344 10C13.5822 10 13.1344 9.5523 13.1344 9C13.1344 8.9581 13.1371 8.9161 13.1423 8.8745L13.8139 3.5643C13.8166 3.5435 13.8179 3.5226 13.8179 3.5016C13.8179 3.2255 13.594 3.0016 13.3179 3.0016C13.1837 3.0016 13.0551 3.0555 12.9611 3.1513Z" fill="none"/></g>"#,
};

pub fn icon_zap(size: f32, color: Color) -> El {
    let glyph = if size < 24.0 {
        &ICON_ZAP_SM
    } else {
        &ICON_ZAP_LG
    };
    icon(glyph, size).color(color)
}

const ICON_MONITOR_SM: Icon = Icon {
    name: "kl-monitor-165",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.3" stroke-linecap="round" stroke-linejoin="round"><path d="M5 4H19C20.65685 4 22 5.34315 22 7V13C22 14.65685 20.65685 16 19 16H5C3.34315 16 2 14.65685 2 13V7C2 5.34315 3.34315 4 5 4ZM12 16V20M8 20H16"/></g>"#,
};
const ICON_MONITOR_LG: Icon = Icon {
    name: "kl-monitor-185",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M5 4H19C20.65685 4 22 5.34315 22 7V13C22 14.65685 20.65685 16 19 16H5C3.34315 16 2 14.65685 2 13V7C2 5.34315 3.34315 4 5 4ZM12 16V20M8 20H16"/></g>"#,
};

pub fn icon_monitor(size: f32, color: Color) -> El {
    let glyph = if size < 24.0 {
        &ICON_MONITOR_SM
    } else {
        &ICON_MONITOR_LG
    };
    icon(glyph, size).color(color)
}

const ICON_CHECK_SM: Icon = Icon {
    name: "kl-check-165",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.3" stroke-linecap="round" stroke-linejoin="round"><path d="M5 12L9.66667 17L19 7"/></g>"#,
};
const ICON_CHECK_LG: Icon = Icon {
    name: "kl-check-185",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M5 12L9.66667 17L19 7"/></g>"#,
};

pub fn icon_check(size: f32, color: Color) -> El {
    let glyph = if size < 24.0 {
        &ICON_CHECK_SM
    } else {
        &ICON_CHECK_LG
    };
    icon(glyph, size).color(color)
}

const ICON_USER_SM: Icon = Icon {
    name: "kl-user-165",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.3" stroke-linecap="round" stroke-linejoin="round"><path d="M11 14H13C16.3137 14 19 16.6863 19 20C19 20.5523 18.5523 21 18 21H6C5.44772 21 5 20.5523 5 20C5 16.6863 7.68629 14 11 14ZM12 4C13.6569 4 15 5.34315 15 7C15 8.65685 13.6569 10 12 10C10.3431 10 9 8.65685 9 7C9 5.34315 10.3431 4 12 4Z"/></g>"#,
};
const ICON_USER_LG: Icon = Icon {
    name: "kl-user-185",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M11 14H13C16.3137 14 19 16.6863 19 20C19 20.5523 18.5523 21 18 21H6C5.44772 21 5 20.5523 5 20C5 16.6863 7.68629 14 11 14ZM12 4C13.6569 4 15 5.34315 15 7C15 8.65685 13.6569 10 12 10C10.3431 10 9 8.65685 9 7C9 5.34315 10.3431 4 12 4Z"/></g>"#,
};

pub fn icon_user(size: f32, color: Color) -> El {
    let glyph = if size < 24.0 {
        &ICON_USER_SM
    } else {
        &ICON_USER_LG
    };
    icon(glyph, size).color(color)
}

const ICON_APP_WINDOW_SM: Icon = Icon {
    name: "kl-app-window-165",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.3" stroke-linecap="round" stroke-linejoin="round"><path d="M6 3L18 3C19.6569 3 21 4.3431 21 6L21 18C21 19.6569 19.6569 21 18 21L6 21C4.3431 21 3 19.6569 3 18L3 6C3 4.3431 4.3431 3 6 3Z"/><path d="M8 7C8 7.5523 7.5523 8 7 8C6.4477 8 6 7.5523 6 7C6 6.4477 6.4477 6 7 6C7.5523 6 8 6.4477 8 7ZM12 7C12 7.5523 11.5523 8 11 8C10.4477 8 10 7.5523 10 7C10 6.4477 10.4477 6 11 6C11.5523 6 12 6.4477 12 7ZM16 7C16 7.5523 15.5523 8 15 8C14.4477 8 14 7.5523 14 7C14 6.4477 14.4477 6 15 6C15.5523 6 16 6.4477 16 7Z" fill="currentColor" stroke="none"/></g>"#,
};
const ICON_APP_WINDOW_LG: Icon = Icon {
    name: "kl-app-window-185",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M6 3L18 3C19.6569 3 21 4.3431 21 6L21 18C21 19.6569 19.6569 21 18 21L6 21C4.3431 21 3 19.6569 3 18L3 6C3 4.3431 4.3431 3 6 3Z"/><path d="M8 7C8 7.5523 7.5523 8 7 8C6.4477 8 6 7.5523 6 7C6 6.4477 6.4477 6 7 6C7.5523 6 8 6.4477 8 7ZM12 7C12 7.5523 11.5523 8 11 8C10.4477 8 10 7.5523 10 7C10 6.4477 10.4477 6 11 6C11.5523 6 12 6.4477 12 7ZM16 7C16 7.5523 15.5523 8 15 8C14.4477 8 14 7.5523 14 7C14 6.4477 14.4477 6 15 6C15.5523 6 16 6.4477 16 7Z" fill="currentColor" stroke="none"/></g>"#,
};

pub fn icon_app_window(size: f32, color: Color) -> El {
    let glyph = if size < 24.0 {
        &ICON_APP_WINDOW_SM
    } else {
        &ICON_APP_WINDOW_LG
    };
    icon(glyph, size).color(color)
}

const ICON_PAUSE_SM: Icon = Icon {
    name: "kl-pause-165",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.3" stroke-linecap="round" stroke-linejoin="round"><path d="M5 5C5 4.447715 5.447715 4 6 4L8 4C8.552285 4 9 4.447715 9 5L9 19C9 19.552285 8.552285 20 8 20L6 20C5.447715 20 5 19.552285 5 19ZM15 5C15 4.447715 15.447715 4 16 4L18 4C18.552285 4 19 4.447715 19 5L19 19C19 19.552285 18.552285 20 18 20L16 20C15.447715 20 15 19.552285 15 19Z"/></g>"#,
};
const ICON_PAUSE_LG: Icon = Icon {
    name: "kl-pause-185",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M5 5C5 4.447715 5.447715 4 6 4L8 4C8.552285 4 9 4.447715 9 5L9 19C9 19.552285 8.552285 20 8 20L6 20C5.447715 20 5 19.552285 5 19ZM15 5C15 4.447715 15.447715 4 16 4L18 4C18.552285 4 19 4.447715 19 5L19 19C19 19.552285 18.552285 20 18 20L16 20C15.447715 20 15 19.552285 15 19Z"/></g>"#,
};

pub fn icon_pause(size: f32, color: Color) -> El {
    let glyph = if size < 24.0 {
        &ICON_PAUSE_SM
    } else {
        &ICON_PAUSE_LG
    };
    icon(glyph, size).color(color)
}

const ICON_SKIP_BACK_SM: Icon = Icon {
    name: "kl-skip-back-165",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.3" stroke-linecap="round" stroke-linejoin="round"><path d="M19 16.9941C19 18.728898 16.942447 19.641025 15.656974 18.476086L9.328487 12.740993C8.890504 12.344078 8.890504 11.655922 9.328487 11.259007L15.656974 5.523914C16.942447 4.358975 19 5.271102 19 7.0059ZM5 5V19"/></g>"#,
};
const ICON_SKIP_BACK_LG: Icon = Icon {
    name: "kl-skip-back-185",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M19 16.9941C19 18.728898 16.942447 19.641025 15.656974 18.476086L9.328487 12.740993C8.890504 12.344078 8.890504 11.655922 9.328487 11.259007L15.656974 5.523914C16.942447 4.358975 19 5.271102 19 7.0059ZM5 5V19"/></g>"#,
};

pub fn icon_skip_back(size: f32, color: Color) -> El {
    let glyph = if size < 24.0 {
        &ICON_SKIP_BACK_SM
    } else {
        &ICON_SKIP_BACK_LG
    };
    icon(glyph, size).color(color)
}

const ICON_SKIP_FORWARD_SM: Icon = Icon {
    name: "kl-skip-forward-165",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.3" stroke-linecap="round" stroke-linejoin="round"><path d="M5 7.0059C5 5.271102 7.057553 4.358975 8.343026 5.523914L14.671513 11.259007C15.109496 11.655922 15.109496 12.344078 14.671513 12.740993L8.343026 18.476086C7.057553 19.641025 5 18.728898 5 16.9941ZM19 5V19"/></g>"#,
};
const ICON_SKIP_FORWARD_LG: Icon = Icon {
    name: "kl-skip-forward-185",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M5 7.0059C5 5.271102 7.057553 4.358975 8.343026 5.523914L14.671513 11.259007C15.109496 11.655922 15.109496 12.344078 14.671513 12.740993L8.343026 18.476086C7.057553 19.641025 5 18.728898 5 16.9941ZM19 5V19"/></g>"#,
};

pub fn icon_skip_forward(size: f32, color: Color) -> El {
    let glyph = if size < 24.0 {
        &ICON_SKIP_FORWARD_SM
    } else {
        &ICON_SKIP_FORWARD_LG
    };
    icon(glyph, size).color(color)
}

const ICON_FILM_SM: Icon = Icon {
    name: "kl-film-165",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.3" stroke-linecap="round" stroke-linejoin="round"><path d="M6 3L18 3C19.6569 3 21 4.3431 21 6L21 18C21 19.6569 19.6569 21 18 21L6 21C4.3431 21 3 19.6569 3 18L3 6C3 4.3431 4.3431 3 6 3ZM8 3L8 21M16 3L16 21M3 12L21 12M3 7.5L8 7.5M16 7.5L21 7.5M3 16.5L8 16.5M16 16.5L21 16.5" fill="none"/></g>"#,
};
const ICON_FILM_LG: Icon = Icon {
    name: "kl-film-185",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M6 3L18 3C19.6569 3 21 4.3431 21 6L21 18C21 19.6569 19.6569 21 18 21L6 21C4.3431 21 3 19.6569 3 18L3 6C3 4.3431 4.3431 3 6 3ZM8 3L8 21M16 3L16 21M3 12L21 12M3 7.5L8 7.5M16 7.5L21 7.5M3 16.5L8 16.5M16 16.5L21 16.5" fill="none"/></g>"#,
};

pub fn icon_film(size: f32, color: Color) -> El {
    let glyph = if size < 24.0 {
        &ICON_FILM_SM
    } else {
        &ICON_FILM_LG
    };
    icon(glyph, size).color(color)
}

const ICON_CAMERA_SM: Icon = Icon {
    name: "kl-camera-165",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.3" stroke-linecap="round" stroke-linejoin="round"><path d="M2 9C2 7.3431 3.3431 6 5 6L7.5 6L8.7 4.4C8.8889 4.1482 9.1852 4 9.5 4L14.5 4C14.8148 4 15.1111 4.1482 15.3 4.4L16.5 6L19 6C20.6569 6 22 7.3431 22 9L22 17C22 18.6569 20.6569 20 19 20L5 20C3.3431 20 2 18.6569 2 17ZM15 13C15 14.6569 13.6569 16 12 16C10.3431 16 9 14.6569 9 13C9 11.3431 10.3431 10 12 10C13.6569 10 15 11.3431 15 13Z"/></g>"#,
};
const ICON_CAMERA_LG: Icon = Icon {
    name: "kl-camera-185",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M2 9C2 7.3431 3.3431 6 5 6L7.5 6L8.7 4.4C8.8889 4.1482 9.1852 4 9.5 4L14.5 4C14.8148 4 15.1111 4.1482 15.3 4.4L16.5 6L19 6C20.6569 6 22 7.3431 22 9L22 17C22 18.6569 20.6569 20 19 20L5 20C3.3431 20 2 18.6569 2 17ZM15 13C15 14.6569 13.6569 16 12 16C10.3431 16 9 14.6569 9 13C9 11.3431 10.3431 10 12 10C13.6569 10 15 11.3431 15 13Z"/></g>"#,
};

pub fn icon_camera(size: f32, color: Color) -> El {
    let glyph = if size < 24.0 {
        &ICON_CAMERA_SM
    } else {
        &ICON_CAMERA_LG
    };
    icon(glyph, size).color(color)
}

const ICON_STAR_SM: Icon = Icon {
    name: "kl-star-165",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.3" stroke-linecap="round" stroke-linejoin="round"><path d="M11.5386 2.8073C11.6164 2.6212 11.7983 2.5 12 2.5C12.2017 2.5 12.3836 2.6212 12.4614 2.8073L14.87 8.5744C14.9422 8.7473 15.1049 8.8653 15.2916 8.8802L21.5398 9.379C21.7413 9.3951 21.9133 9.531 21.9756 9.7233C22.0379 9.9157 21.9783 10.1266 21.8245 10.2578L17.0671 14.3162C16.924 14.4383 16.8615 14.6302 16.9053 14.8131L18.3593 20.8835C18.4064 21.0798 18.3307 21.2851 18.1675 21.404C18.0044 21.5229 17.7857 21.532 17.6132 21.4271L12.2598 18.1708C12.1002 18.0737 11.8998 18.0737 11.7402 18.1708L6.3868 21.4271C6.2143 21.532 5.9956 21.5229 5.8325 21.404C5.6693 21.2851 5.5936 21.0798 5.6407 20.8835L7.0947 14.8131C7.1385 14.6302 7.076 14.4383 6.9329 14.3162L2.1755 10.2578C2.0217 10.1266 1.962 9.9157 2.0244 9.7233C2.0867 9.531 2.2587 9.3951 2.4602 9.379L8.7084 8.8802C8.8951 8.8653 9.0578 8.7473 9.13 8.5744Z"/></g>"#,
};
const ICON_STAR_LG: Icon = Icon {
    name: "kl-star-185",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M11.5386 2.8073C11.6164 2.6212 11.7983 2.5 12 2.5C12.2017 2.5 12.3836 2.6212 12.4614 2.8073L14.87 8.5744C14.9422 8.7473 15.1049 8.8653 15.2916 8.8802L21.5398 9.379C21.7413 9.3951 21.9133 9.531 21.9756 9.7233C22.0379 9.9157 21.9783 10.1266 21.8245 10.2578L17.0671 14.3162C16.924 14.4383 16.8615 14.6302 16.9053 14.8131L18.3593 20.8835C18.4064 21.0798 18.3307 21.2851 18.1675 21.404C18.0044 21.5229 17.7857 21.532 17.6132 21.4271L12.2598 18.1708C12.1002 18.0737 11.8998 18.0737 11.7402 18.1708L6.3868 21.4271C6.2143 21.532 5.9956 21.5229 5.8325 21.404C5.6693 21.2851 5.5936 21.0798 5.6407 20.8835L7.0947 14.8131C7.1385 14.6302 7.076 14.4383 6.9329 14.3162L2.1755 10.2578C2.0217 10.1266 1.962 9.9157 2.0244 9.7233C2.0867 9.531 2.2587 9.3951 2.4602 9.379L8.7084 8.8802C8.8951 8.8653 9.0578 8.7473 9.13 8.5744Z"/></g>"#,
};

pub fn icon_star(size: f32, color: Color) -> El {
    let glyph = if size < 24.0 {
        &ICON_STAR_SM
    } else {
        &ICON_STAR_LG
    };
    icon(glyph, size).color(color)
}

const ICON_SCISSORS_SM: Icon = Icon {
    name: "kl-scissors-165",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.3" stroke-linecap="round" stroke-linejoin="round"><path d="M9 18C9 19.6569 7.6569 21 6 21C4.3431 21 3 19.6569 3 18C3 16.3431 4.3431 15 6 15C7.6569 15 9 16.3431 9 18ZM21 18C21 19.6569 19.6569 21 18 21C16.3431 21 15 19.6569 15 18C15 16.3431 16.3431 15 18 15C19.6569 15 21 16.3431 21 18ZM8.1213 15.8787L17 3M15.8787 15.8787L12 10.2526M9.5708 6.729L7 3" fill="none"/></g>"#,
};
const ICON_SCISSORS_LG: Icon = Icon {
    name: "kl-scissors-185",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M9 18C9 19.6569 7.6569 21 6 21C4.3431 21 3 19.6569 3 18C3 16.3431 4.3431 15 6 15C7.6569 15 9 16.3431 9 18ZM21 18C21 19.6569 19.6569 21 18 21C16.3431 21 15 19.6569 15 18C15 16.3431 16.3431 15 18 15C19.6569 15 21 16.3431 21 18ZM8.1213 15.8787L17 3M15.8787 15.8787L12 10.2526M9.5708 6.729L7 3" fill="none"/></g>"#,
};

pub fn icon_scissors(size: f32, color: Color) -> El {
    let glyph = if size < 24.0 {
        &ICON_SCISSORS_SM
    } else {
        &ICON_SCISSORS_LG
    };
    icon(glyph, size).color(color)
}

const ICON_HASH_SM: Icon = Icon {
    name: "kl-hash-165",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.3" stroke-linecap="round" stroke-linejoin="round"><path d="M3 8.5L21 8.5M3 15.5L21 15.5M7.5 21L9.5 3M14.5 21L16.5 3"/></g>"#,
};
const ICON_HASH_LG: Icon = Icon {
    name: "kl-hash-185",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M3 8.5L21 8.5M3 15.5L21 15.5M7.5 21L9.5 3M14.5 21L16.5 3"/></g>"#,
};

pub fn icon_hash(size: f32, color: Color) -> El {
    let glyph = if size < 24.0 {
        &ICON_HASH_SM
    } else {
        &ICON_HASH_LG
    };
    icon(glyph, size).color(color)
}

const ICON_MIC_SM: Icon = Icon {
    name: "kl-mic-165",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.3" stroke-linecap="round" stroke-linejoin="round"><path d="M9 5C9 3.343146 10.343146 2 12 2L12 2C13.656854 2 15 3.343146 15 5L15 11C15 12.656854 13.656854 14 12 14L12 14C10.343146 14 9 12.656854 9 11ZM19 11C19 14.865993 15.865993 18 12 18C8.134007 18 5 14.865993 5 11M12 18V22M8 22H16"/></g>"#,
};
const ICON_MIC_LG: Icon = Icon {
    name: "kl-mic-185",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M9 5C9 3.343146 10.343146 2 12 2L12 2C13.656854 2 15 3.343146 15 5L15 11C15 12.656854 13.656854 14 12 14L12 14C10.343146 14 9 12.656854 9 11ZM19 11C19 14.865993 15.865993 18 12 18C8.134007 18 5 14.865993 5 11M12 18V22M8 22H16"/></g>"#,
};

pub fn icon_mic(size: f32, color: Color) -> El {
    let glyph = if size < 24.0 {
        &ICON_MIC_SM
    } else {
        &ICON_MIC_LG
    };
    icon(glyph, size).color(color)
}

const ICON_MIC_OFF_SM: Icon = Icon {
    name: "kl-mic-off-165",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.3" stroke-linecap="round" stroke-linejoin="round"><path d="M9.3157 3.6589C9.8081 2.6753 10.8252 2 12 2L12 2C13.6569 2 15 3.3431 15 5L15 9.3431M13.5619 13.5619C13.1069 13.8398 12.5722 14 12 14L12 14C10.3431 14 9 12.6569 9 11L9 9M16.4246 16.4246C15.2187 17.4094 13.6783 18 12 18C8.134 18 5 14.866 5 11M12 18L12 22M8 22L16 22M2 2L22 22"/></g>"#,
};
const ICON_MIC_OFF_LG: Icon = Icon {
    name: "kl-mic-off-185",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M9.3157 3.6589C9.8081 2.6753 10.8252 2 12 2L12 2C13.6569 2 15 3.3431 15 5L15 9.3431M13.5619 13.5619C13.1069 13.8398 12.5722 14 12 14L12 14C10.3431 14 9 12.6569 9 11L9 9M16.4246 16.4246C15.2187 17.4094 13.6783 18 12 18C8.134 18 5 14.866 5 11M12 18L12 22M8 22L16 22M2 2L22 22"/></g>"#,
};

pub fn icon_mic_off(size: f32, color: Color) -> El {
    let glyph = if size < 24.0 {
        &ICON_MIC_OFF_SM
    } else {
        &ICON_MIC_OFF_LG
    };
    icon(glyph, size).color(color)
}

const ICON_HEADPHONES_SM: Icon = Icon {
    name: "kl-headphones-165",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.3" stroke-linecap="round" stroke-linejoin="round"><path d="M3 12C3 10.895431 3.895431 10 5 10L5 10C6.104569 10 7 10.895431 7 12L7 19C7 20.104569 6.104569 21 5 21L5 21C3.895431 21 3 20.104569 3 19ZM17 12C17 10.895431 17.895431 10 19 10L19 10C20.104569 10 21 10.895431 21 12L21 19C21 20.104569 20.104569 21 19 21L19 21C17.895431 21 17 20.104569 17 19ZM3 12C3 7.029437 7.029437 3 12 3C16.970563 3 21 7.029437 21 12"/></g>"#,
};
const ICON_HEADPHONES_LG: Icon = Icon {
    name: "kl-headphones-185",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M3 12C3 10.895431 3.895431 10 5 10L5 10C6.104569 10 7 10.895431 7 12L7 19C7 20.104569 6.104569 21 5 21L5 21C3.895431 21 3 20.104569 3 19ZM17 12C17 10.895431 17.895431 10 19 10L19 10C20.104569 10 21 10.895431 21 12L21 19C21 20.104569 20.104569 21 19 21L19 21C17.895431 21 17 20.104569 17 19ZM3 12C3 7.029437 7.029437 3 12 3C16.970563 3 21 7.029437 21 12"/></g>"#,
};

pub fn icon_headphones(size: f32, color: Color) -> El {
    let glyph = if size < 24.0 {
        &ICON_HEADPHONES_SM
    } else {
        &ICON_HEADPHONES_LG
    };
    icon(glyph, size).color(color)
}

const ICON_HEADPHONES_OFF_SM: Icon = Icon {
    name: "kl-headphones-off-165",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.3" stroke-linecap="round" stroke-linejoin="round"><path d="M3 12C3 10.8954 3.8954 10 5 10L5 10C6.1046 10 7 10.8954 7 12L7 19C7 20.1046 6.1046 21 5 21L5 21C3.8954 21 3 20.1046 3 19L3 12ZM17.0834 11.4266C17.3299 10.6015 18.0947 10 19 10L19 10C20.1046 10 21 10.8954 21 12L21 15.3431M20.4142 20.4142C20.0523 20.7761 19.5523 21 19 21L19 21C17.8954 21 17 20.1046 17 19L17 17M3 12C3 9.5147 4.0074 7.2647 5.636 5.636M9.1256 3.4688C10.0282 3.1648 10.9949 3 12 3C16.9706 3 21 7.0294 21 12M2 2L22 22"/></g>"#,
};
const ICON_HEADPHONES_OFF_LG: Icon = Icon {
    name: "kl-headphones-off-185",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M3 12C3 10.8954 3.8954 10 5 10L5 10C6.1046 10 7 10.8954 7 12L7 19C7 20.1046 6.1046 21 5 21L5 21C3.8954 21 3 20.1046 3 19L3 12ZM17.0834 11.4266C17.3299 10.6015 18.0947 10 19 10L19 10C20.1046 10 21 10.8954 21 12L21 15.3431M20.4142 20.4142C20.0523 20.7761 19.5523 21 19 21L19 21C17.8954 21 17 20.1046 17 19L17 17M3 12C3 9.5147 4.0074 7.2647 5.636 5.636M9.1256 3.4688C10.0282 3.1648 10.9949 3 12 3C16.9706 3 21 7.0294 21 12M2 2L22 22"/></g>"#,
};

pub fn icon_headphones_off(size: f32, color: Color) -> El {
    let glyph = if size < 24.0 {
        &ICON_HEADPHONES_OFF_SM
    } else {
        &ICON_HEADPHONES_OFF_LG
    };
    icon(glyph, size).color(color)
}

const ICON_USERS_SM: Icon = Icon {
    name: "kl-users-165",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.3" stroke-linecap="round" stroke-linejoin="round"><path d="M8 14H10C13.3137 14 16 16.6863 16 20C16 20.5523 15.5523 21 15 21H3C2.44772 21 2 20.5523 2 20C2 16.6863 4.68629 14 8 14ZM9 4C10.6569 4 12 5.34315 12 7C12 8.65685 10.6569 10 9 10C7.34315 10 6 8.65685 6 7C6 5.34315 7.34315 4 9 4ZM16 4C17.6569 4 19 5.34315 19 7C19 8.65685 17.6569 10 16 10M18.3333 14.4713C20.554 15.409 22 17.589 22 20C22 20.5523 21.5523 21 21 21"/></g>"#,
};
const ICON_USERS_LG: Icon = Icon {
    name: "kl-users-185",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M8 14H10C13.3137 14 16 16.6863 16 20C16 20.5523 15.5523 21 15 21H3C2.44772 21 2 20.5523 2 20C2 16.6863 4.68629 14 8 14ZM9 4C10.6569 4 12 5.34315 12 7C12 8.65685 10.6569 10 9 10C7.34315 10 6 8.65685 6 7C6 5.34315 7.34315 4 9 4ZM16 4C17.6569 4 19 5.34315 19 7C19 8.65685 17.6569 10 16 10M18.3333 14.4713C20.554 15.409 22 17.589 22 20C22 20.5523 21.5523 21 21 21"/></g>"#,
};

pub fn icon_users(size: f32, color: Color) -> El {
    let glyph = if size < 24.0 {
        &ICON_USERS_SM
    } else {
        &ICON_USERS_LG
    };
    icon(glyph, size).color(color)
}

const ICON_USER_PLUS_SM: Icon = Icon {
    name: "kl-user-plus-165",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.3" stroke-linecap="round" stroke-linejoin="round"><path d="M19 4V10M16 7H22M8 14H10C13.3137 14 16 16.6863 16 20C16 20.5523 15.5523 21 15 21H3C2.44772 21 2 20.5523 2 20C2 16.6863 4.68629 14 8 14ZM9 4C10.6569 4 12 5.34315 12 7C12 8.65685 10.6569 10 9 10C7.34315 10 6 8.65685 6 7C6 5.34315 7.34315 4 9 4Z"/></g>"#,
};
const ICON_USER_PLUS_LG: Icon = Icon {
    name: "kl-user-plus-185",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M19 4V10M16 7H22M8 14H10C13.3137 14 16 16.6863 16 20C16 20.5523 15.5523 21 15 21H3C2.44772 21 2 20.5523 2 20C2 16.6863 4.68629 14 8 14ZM9 4C10.6569 4 12 5.34315 12 7C12 8.65685 10.6569 10 9 10C7.34315 10 6 8.65685 6 7C6 5.34315 7.34315 4 9 4Z"/></g>"#,
};

pub fn icon_user_plus(size: f32, color: Color) -> El {
    let glyph = if size < 24.0 {
        &ICON_USER_PLUS_SM
    } else {
        &ICON_USER_PLUS_LG
    };
    icon(glyph, size).color(color)
}

const ICON_PHONE_SM: Icon = Icon {
    name: "kl-phone-165",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.3" stroke-linecap="round" stroke-linejoin="round"><path d="M2 4C2 2.8954 2.8954 2 4 2L7 2C8.1046 2 9 2.8954 9 4L9 7C9 7.6295 8.7036 8.2223 8.2 8.6L7.7388 8.9459C7.487 9.1348 7.3388 9.4312 7.3388 9.7459C7.3388 9.8992 7.374 10.0504 7.4417 10.1879C8.8053 12.9552 11.0448 15.1947 13.8121 16.5583C13.9496 16.626 14.1008 16.6612 14.2541 16.6612C14.5688 16.6612 14.8652 16.513 15.0541 16.2612L15.4 15.8C15.7777 15.2964 16.3705 15 17 15L20 15C21.1046 15 22 15.8954 22 17L22 20C22 21.1046 21.1046 22 20 22C10.0589 22 2 13.9411 2 4Z" fill="none"/></g>"#,
};
const ICON_PHONE_LG: Icon = Icon {
    name: "kl-phone-185",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M2 4C2 2.8954 2.8954 2 4 2L7 2C8.1046 2 9 2.8954 9 4L9 7C9 7.6295 8.7036 8.2223 8.2 8.6L7.7388 8.9459C7.487 9.1348 7.3388 9.4312 7.3388 9.7459C7.3388 9.8992 7.374 10.0504 7.4417 10.1879C8.8053 12.9552 11.0448 15.1947 13.8121 16.5583C13.9496 16.626 14.1008 16.6612 14.2541 16.6612C14.5688 16.6612 14.8652 16.513 15.0541 16.2612L15.4 15.8C15.7777 15.2964 16.3705 15 17 15L20 15C21.1046 15 22 15.8954 22 17L22 20C22 21.1046 21.1046 22 20 22C10.0589 22 2 13.9411 2 4Z" fill="none"/></g>"#,
};

pub fn icon_phone(size: f32, color: Color) -> El {
    let glyph = if size < 24.0 {
        &ICON_PHONE_SM
    } else {
        &ICON_PHONE_LG
    };
    icon(glyph, size).color(color)
}

const ICON_PHONE_OFF_SM: Icon = Icon {
    name: "kl-phone-off-165",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.3" stroke-linecap="round" stroke-linejoin="round"><path d="M4.7619 13.5812C2.9574 10.7113 2 7.3901 2 4C2 2.8954 2.8954 2 4 2L7 2C8.1046 2 9 2.8954 9 4L9 7C9 7.6295 8.7036 8.2223 8.2 8.6L7.7388 8.9459C7.487 9.1348 7.3388 9.4312 7.3388 9.7459C7.3388 9.8992 7.374 10.0504 7.4417 10.1879C7.5198 10.3463 7.6008 10.5031 7.6847 10.6584M10.1005 13.8995C11.1839 14.9829 12.4377 15.8811 13.8121 16.5583C13.9496 16.626 14.1008 16.6612 14.2541 16.6612C14.5688 16.6612 14.8652 16.513 15.0541 16.2612L15.4 15.8C15.7777 15.2964 16.3705 15 17 15L20 15C21.1046 15 22 15.8954 22 17L22 20C22 21.1046 21.1046 22 20 22C15.2261 22 10.6477 20.1036 7.2721 16.7279M22 2L2 22" fill="none"/></g>"#,
};
const ICON_PHONE_OFF_LG: Icon = Icon {
    name: "kl-phone-off-185",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M4.7619 13.5812C2.9574 10.7113 2 7.3901 2 4C2 2.8954 2.8954 2 4 2L7 2C8.1046 2 9 2.8954 9 4L9 7C9 7.6295 8.7036 8.2223 8.2 8.6L7.7388 8.9459C7.487 9.1348 7.3388 9.4312 7.3388 9.7459C7.3388 9.8992 7.374 10.0504 7.4417 10.1879C7.5198 10.3463 7.6008 10.5031 7.6847 10.6584M10.1005 13.8995C11.1839 14.9829 12.4377 15.8811 13.8121 16.5583C13.9496 16.626 14.1008 16.6612 14.2541 16.6612C14.5688 16.6612 14.8652 16.513 15.0541 16.2612L15.4 15.8C15.7777 15.2964 16.3705 15 17 15L20 15C21.1046 15 22 15.8954 22 17L22 20C22 21.1046 21.1046 22 20 22C15.2261 22 10.6477 20.1036 7.2721 16.7279M22 2L2 22" fill="none"/></g>"#,
};

pub fn icon_phone_off(size: f32, color: Color) -> El {
    let glyph = if size < 24.0 {
        &ICON_PHONE_OFF_SM
    } else {
        &ICON_PHONE_OFF_LG
    };
    icon(glyph, size).color(color)
}

const ICON_LOCK_SM: Icon = Icon {
    name: "kl-lock-165",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.3" stroke-linecap="round" stroke-linejoin="round"><path d="M7.5 10V6.5C7.5 4.01472 9.51472 2 12 2C14.4853 2 16.5 4.01472 16.5 6.5V10M4 13V19C4 20.6569 5.34315 22 7 22H17C18.6569 22 20 20.6569 20 19V13C20 11.3431 18.6569 10 17 10H7C5.34315 10 4 11.3431 4 13Z"/><path d="M13.5 16C13.5 16.8284 12.8284 17.5 12 17.5C11.1716 17.5 10.5 16.8284 10.5 16C10.5 15.1716 11.1716 14.5 12 14.5C12.8284 14.5 13.5 15.1716 13.5 16Z" fill="currentColor" stroke="none"/></g>"#,
};
const ICON_LOCK_LG: Icon = Icon {
    name: "kl-lock-185",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M7.5 10V6.5C7.5 4.01472 9.51472 2 12 2C14.4853 2 16.5 4.01472 16.5 6.5V10M4 13V19C4 20.6569 5.34315 22 7 22H17C18.6569 22 20 20.6569 20 19V13C20 11.3431 18.6569 10 17 10H7C5.34315 10 4 11.3431 4 13Z"/><path d="M13.5 16C13.5 16.8284 12.8284 17.5 12 17.5C11.1716 17.5 10.5 16.8284 10.5 16C10.5 15.1716 11.1716 14.5 12 14.5C12.8284 14.5 13.5 15.1716 13.5 16Z" fill="currentColor" stroke="none"/></g>"#,
};

pub fn icon_lock(size: f32, color: Color) -> El {
    let glyph = if size < 24.0 {
        &ICON_LOCK_SM
    } else {
        &ICON_LOCK_LG
    };
    icon(glyph, size).color(color)
}

const ICON_HOME_SM: Icon = Icon {
    name: "kl-house-165",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.3" stroke-linecap="round" stroke-linejoin="round"><path d="M2 10.8473L10.7072 3.4703C11.4532 2.8383 12.5468 2.8383 13.2928 3.4703L22 10.8473M4 9.1528L4 19C4 20.1046 4.8954 21 6 21L18 21C19.1046 21 20 20.1046 20 19L20 9.1528M9 21L9 18C9 16.3431 10.3431 15 12 15C13.6569 15 15 16.3431 15 18L15 21"/></g>"#,
};
const ICON_HOME_LG: Icon = Icon {
    name: "kl-house-185",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M2 10.8473L10.7072 3.4703C11.4532 2.8383 12.5468 2.8383 13.2928 3.4703L22 10.8473M4 9.1528L4 19C4 20.1046 4.8954 21 6 21L18 21C19.1046 21 20 20.1046 20 19L20 9.1528M9 21L9 18C9 16.3431 10.3431 15 12 15C13.6569 15 15 16.3431 15 18L15 21"/></g>"#,
};

pub fn icon_home(size: f32, color: Color) -> El {
    let glyph = if size < 24.0 {
        &ICON_HOME_SM
    } else {
        &ICON_HOME_LG
    };
    icon(glyph, size).color(color)
}

const ICON_SERVER_SM: Icon = Icon {
    name: "kl-server-165",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.3" stroke-linecap="round" stroke-linejoin="round"><path d="M6 2L18 2C19.1046 2 20 2.8954 20 4L20 8C20 9.1046 19.1046 10 18 10L6 10C4.8954 10 4 9.1046 4 8L4 4C4 2.8954 4.8954 2 6 2ZM6 14L18 14C19.1046 14 20 14.8954 20 16L20 20C20 21.1046 19.1046 22 18 22L6 22C4.8954 22 4 21.1046 4 20L4 16C4 14.8954 4.8954 14 6 14Z" fill="none"/><path d="M9 6C9 6.5523 8.5523 7 8 7C7.4477 7 7 6.5523 7 6C7 5.4477 7.4477 5 8 5C8.5523 5 9 5.4477 9 6ZM13 6C13 6.5523 12.5523 7 12 7C11.4477 7 11 6.5523 11 6C11 5.4477 11.4477 5 12 5C12.5523 5 13 5.4477 13 6ZM9 18C9 18.5523 8.5523 19 8 19C7.4477 19 7 18.5523 7 18C7 17.4477 7.4477 17 8 17C8.5523 17 9 17.4477 9 18ZM13 18C13 18.5523 12.5523 19 12 19C11.4477 19 11 18.5523 11 18C11 17.4477 11.4477 17 12 17C12.5523 17 13 17.4477 13 18Z" fill="currentColor" stroke="none"/></g>"#,
};
const ICON_SERVER_LG: Icon = Icon {
    name: "kl-server-185",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M6 2L18 2C19.1046 2 20 2.8954 20 4L20 8C20 9.1046 19.1046 10 18 10L6 10C4.8954 10 4 9.1046 4 8L4 4C4 2.8954 4.8954 2 6 2ZM6 14L18 14C19.1046 14 20 14.8954 20 16L20 20C20 21.1046 19.1046 22 18 22L6 22C4.8954 22 4 21.1046 4 20L4 16C4 14.8954 4.8954 14 6 14Z" fill="none"/><path d="M9 6C9 6.5523 8.5523 7 8 7C7.4477 7 7 6.5523 7 6C7 5.4477 7.4477 5 8 5C8.5523 5 9 5.4477 9 6ZM13 6C13 6.5523 12.5523 7 12 7C11.4477 7 11 6.5523 11 6C11 5.4477 11.4477 5 12 5C12.5523 5 13 5.4477 13 6ZM9 18C9 18.5523 8.5523 19 8 19C7.4477 19 7 18.5523 7 18C7 17.4477 7.4477 17 8 17C8.5523 17 9 17.4477 9 18ZM13 18C13 18.5523 12.5523 19 12 19C11.4477 19 11 18.5523 11 18C11 17.4477 11.4477 17 12 17C12.5523 17 13 17.4477 13 18Z" fill="currentColor" stroke="none"/></g>"#,
};

pub fn icon_server(size: f32, color: Color) -> El {
    let glyph = if size < 24.0 {
        &ICON_SERVER_SM
    } else {
        &ICON_SERVER_LG
    };
    icon(glyph, size).color(color)
}

const ICON_BELL_OFF_SM: Icon = Icon {
    name: "kl-bell-off-165",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.3" stroke-linecap="round" stroke-linejoin="round"><path d="M9.3908 3.7339C10.1504 3.2684 11.0438 3 12 3C14.7614 3 17 5.2386 17 8C17 9.7086 17.2336 10.9501 17.541 11.8842M16 16L6 16C5.4477 16 5 15.5523 5 15C5 14 7 13 7 8C7 7.687 7.0288 7.3808 7.0838 7.0838M10.2679 20C10.6252 20.6188 11.2855 21 12 21C12.7145 21 13.3748 20.6188 13.7321 20M2 2L22 22"/></g>"#,
};
const ICON_BELL_OFF_LG: Icon = Icon {
    name: "kl-bell-off-185",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M9.3908 3.7339C10.1504 3.2684 11.0438 3 12 3C14.7614 3 17 5.2386 17 8C17 9.7086 17.2336 10.9501 17.541 11.8842M16 16L6 16C5.4477 16 5 15.5523 5 15C5 14 7 13 7 8C7 7.687 7.0288 7.3808 7.0838 7.0838M10.2679 20C10.6252 20.6188 11.2855 21 12 21C12.7145 21 13.3748 20.6188 13.7321 20M2 2L22 22"/></g>"#,
};

pub fn icon_bell_off(size: f32, color: Color) -> El {
    let glyph = if size < 24.0 {
        &ICON_BELL_OFF_SM
    } else {
        &ICON_BELL_OFF_LG
    };
    icon(glyph, size).color(color)
}

const ICON_CHEVRON_DOWN_SM: Icon = Icon {
    name: "kl-chevron-down-165",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.3" stroke-linecap="round" stroke-linejoin="round"><path d="M6 9L12 15L18 9"/></g>"#,
};
const ICON_CHEVRON_DOWN_LG: Icon = Icon {
    name: "kl-chevron-down-185",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M6 9L12 15L18 9"/></g>"#,
};

pub fn icon_chevron_down(size: f32, color: Color) -> El {
    let glyph = if size < 24.0 {
        &ICON_CHEVRON_DOWN_SM
    } else {
        &ICON_CHEVRON_DOWN_LG
    };
    icon(glyph, size).color(color)
}

const ICON_CHEVRON_RIGHT_SM: Icon = Icon {
    name: "kl-chevron-right-165",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.3" stroke-linecap="round" stroke-linejoin="round"><path d="M9 6L15 12L9 18"/></g>"#,
};
const ICON_CHEVRON_RIGHT_LG: Icon = Icon {
    name: "kl-chevron-right-185",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M9 6L15 12L9 18"/></g>"#,
};

pub fn icon_chevron_right(size: f32, color: Color) -> El {
    let glyph = if size < 24.0 {
        &ICON_CHEVRON_RIGHT_SM
    } else {
        &ICON_CHEVRON_RIGHT_LG
    };
    icon(glyph, size).color(color)
}

const ICON_SEND_SM: Icon = Icon {
    name: "kl-send-165",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.3" stroke-linecap="round" stroke-linejoin="round"><path d="M2.7164 7.7054L22 2L16.2946 21.2836C16.0554 22.0918 14.985 22.2598 14.5098 21.5637L9.9303 14.8559C9.7195 14.5471 9.4529 14.2805 9.1441 14.0697L2.4363 9.4902C1.7402 9.015 1.9082 7.9446 2.7164 7.7054ZM9.6113 14.3887L22 2" fill="none"/></g>"#,
};
const ICON_SEND_LG: Icon = Icon {
    name: "kl-send-185",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M2.7164 7.7054L22 2L16.2946 21.2836C16.0554 22.0918 14.985 22.2598 14.5098 21.5637L9.9303 14.8559C9.7195 14.5471 9.4529 14.2805 9.1441 14.0697L2.4363 9.4902C1.7402 9.015 1.9082 7.9446 2.7164 7.7054ZM9.6113 14.3887L22 2" fill="none"/></g>"#,
};

pub fn icon_send(size: f32, color: Color) -> El {
    let glyph = if size < 24.0 {
        &ICON_SEND_SM
    } else {
        &ICON_SEND_LG
    };
    icon(glyph, size).color(color)
}

const ICON_REPLY_SM: Icon = Icon {
    name: "kl-reply-165",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.3" stroke-linecap="round" stroke-linejoin="round"><path d="M8 4L2 10L8 16M2 10L18 10C20.2091 10 22 11.7909 22 14L22 20"/></g>"#,
};
const ICON_REPLY_LG: Icon = Icon {
    name: "kl-reply-185",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M8 4L2 10L8 16M2 10L18 10C20.2091 10 22 11.7909 22 14L22 20"/></g>"#,
};

pub fn icon_reply(size: f32, color: Color) -> El {
    let glyph = if size < 24.0 {
        &ICON_REPLY_SM
    } else {
        &ICON_REPLY_LG
    };
    icon(glyph, size).color(color)
}

const ICON_PIN_SM: Icon = Icon {
    name: "kl-pin-165",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.3" stroke-linecap="round" stroke-linejoin="round"><path d="M13.7336 3.293C13.9211 3.1054 14.1754 3.0001 14.4407 3.0001C14.7059 3.0001 14.9602 3.1054 15.1478 3.293L20.707 8.8522C20.8946 9.0398 20.9999 9.2941 20.9999 9.5593C20.9999 9.8246 20.8946 10.0789 20.707 10.2664L19.3319 11.6416C19.1443 11.8292 18.89 11.9345 18.6248 11.9345C18.3595 11.9345 18.1052 11.8292 17.9176 11.6416L16.8814 10.6054L14.5233 12.9635C14.3358 13.151 14.2304 13.4053 14.2304 13.6706C14.2304 13.7692 14.245 13.8672 14.2737 13.9616C14.8295 15.7886 15.1207 17.6858 15.1385 19.5954C15.1386 19.5985 15.1386 19.6017 15.1386 19.6048C15.1386 19.87 15.0332 20.1243 14.8457 20.3119L14.4504 20.7072C14.2629 20.8947 14.0085 21.0001 13.7433 21.0001C13.4781 21.0001 13.2237 20.8947 13.0362 20.7072L3.2928 10.9638C3.1053 10.7763 2.9999 10.5219 2.9999 10.2567C2.9999 9.9915 3.1053 9.7371 3.2928 9.5496L3.6881 9.1543C3.8757 8.9668 4.13 8.8614 4.3952 8.8614C4.3983 8.8614 4.4015 8.8614 4.4046 8.8615C6.3142 8.8793 8.2114 9.1705 10.0384 9.7263C10.1328 9.755 10.2308 9.7696 10.3294 9.7696C10.5947 9.7696 10.849 9.6642 11.0365 9.4767L13.3946 7.1186L12.3584 6.0824C12.1708 5.8948 12.0655 5.6405 12.0655 5.3752C12.0655 5.11 12.1708 4.8557 12.3584 4.6681L13.7336 3.293ZM8.1645 15.8355L3.9804 20.0196" fill="none"/></g>"#,
};
const ICON_PIN_LG: Icon = Icon {
    name: "kl-pin-185",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M13.7336 3.293C13.9211 3.1054 14.1754 3.0001 14.4407 3.0001C14.7059 3.0001 14.9602 3.1054 15.1478 3.293L20.707 8.8522C20.8946 9.0398 20.9999 9.2941 20.9999 9.5593C20.9999 9.8246 20.8946 10.0789 20.707 10.2664L19.3319 11.6416C19.1443 11.8292 18.89 11.9345 18.6248 11.9345C18.3595 11.9345 18.1052 11.8292 17.9176 11.6416L16.8814 10.6054L14.5233 12.9635C14.3358 13.151 14.2304 13.4053 14.2304 13.6706C14.2304 13.7692 14.245 13.8672 14.2737 13.9616C14.8295 15.7886 15.1207 17.6858 15.1385 19.5954C15.1386 19.5985 15.1386 19.6017 15.1386 19.6048C15.1386 19.87 15.0332 20.1243 14.8457 20.3119L14.4504 20.7072C14.2629 20.8947 14.0085 21.0001 13.7433 21.0001C13.4781 21.0001 13.2237 20.8947 13.0362 20.7072L3.2928 10.9638C3.1053 10.7763 2.9999 10.5219 2.9999 10.2567C2.9999 9.9915 3.1053 9.7371 3.2928 9.5496L3.6881 9.1543C3.8757 8.9668 4.13 8.8614 4.3952 8.8614C4.3983 8.8614 4.4015 8.8614 4.4046 8.8615C6.3142 8.8793 8.2114 9.1705 10.0384 9.7263C10.1328 9.755 10.2308 9.7696 10.3294 9.7696C10.5947 9.7696 10.849 9.6642 11.0365 9.4767L13.3946 7.1186L12.3584 6.0824C12.1708 5.8948 12.0655 5.6405 12.0655 5.3752C12.0655 5.11 12.1708 4.8557 12.3584 4.6681L13.7336 3.293ZM8.1645 15.8355L3.9804 20.0196" fill="none"/></g>"#,
};

pub fn icon_pin(size: f32, color: Color) -> El {
    let glyph = if size < 24.0 {
        &ICON_PIN_SM
    } else {
        &ICON_PIN_LG
    };
    icon(glyph, size).color(color)
}

const ICON_SMILE_SM: Icon = Icon {
    name: "kl-face-smile-165",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.3" stroke-linecap="round" stroke-linejoin="round"><path d="M22 12C22 17.5228 17.5228 22 12 22C6.4772 22 2 17.5228 2 12C2 6.4772 6.4772 2 12 2C17.5228 2 22 6.4772 22 12ZM9 10L9 11M15 10L15 11M9 15C10 16 11 16.5 12 16.5C13 16.5 14 16 15 15" fill="none"/></g>"#,
};
const ICON_SMILE_LG: Icon = Icon {
    name: "kl-face-smile-185",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M22 12C22 17.5228 17.5228 22 12 22C6.4772 22 2 17.5228 2 12C2 6.4772 6.4772 2 12 2C17.5228 2 22 6.4772 22 12ZM9 10L9 11M15 10L15 11M9 15C10 16 11 16.5 12 16.5C13 16.5 14 16 15 15" fill="none"/></g>"#,
};

pub fn icon_smile(size: f32, color: Color) -> El {
    let glyph = if size < 24.0 {
        &ICON_SMILE_SM
    } else {
        &ICON_SMILE_LG
    };
    icon(glyph, size).color(color)
}

const ICON_PAPERCLIP_SM: Icon = Icon {
    name: "kl-paperclip-165",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.3" stroke-linecap="round" stroke-linejoin="round"><path d="M15.6716 6.8536L8.0126 14.5125C7.3292 15.1959 7.3292 16.304 8.0126 16.9874C8.696 17.6708 9.8041 17.6708 10.4875 16.9874L18.5 8.9749C19.8668 7.608 19.8668 5.392 18.5 4.0251C17.1332 2.6583 14.9171 2.6583 13.5503 4.0251L5.5377 12.0377C3.4875 14.0879 3.4874 17.4119 5.5377 19.4623C7.5881 21.5126 10.9121 21.5125 12.9623 19.4623L20 12.4246"/></g>"#,
};
const ICON_PAPERCLIP_LG: Icon = Icon {
    name: "kl-paperclip-185",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M15.6716 6.8536L8.0126 14.5125C7.3292 15.1959 7.3292 16.304 8.0126 16.9874C8.696 17.6708 9.8041 17.6708 10.4875 16.9874L18.5 8.9749C19.8668 7.608 19.8668 5.392 18.5 4.0251C17.1332 2.6583 14.9171 2.6583 13.5503 4.0251L5.5377 12.0377C3.4875 14.0879 3.4874 17.4119 5.5377 19.4623C7.5881 21.5126 10.9121 21.5125 12.9623 19.4623L20 12.4246"/></g>"#,
};

pub fn icon_paperclip(size: f32, color: Color) -> El {
    let glyph = if size < 24.0 {
        &ICON_PAPERCLIP_SM
    } else {
        &ICON_PAPERCLIP_LG
    };
    icon(glyph, size).color(color)
}

const ICON_EDIT_SM: Icon = Icon {
    name: "kl-pen-165",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.3" stroke-linecap="round" stroke-linejoin="round"><path d="M21.0237 7.6904L9.8567 18.8573L2 22L5.1427 14.1433L16.3096 2.9763C16.9348 2.3512 17.7826 2 18.6667 2C19.5507 2 20.3986 2.3512 21.0237 2.9763C21.6488 3.6014 22 4.4493 22 5.3333C22 6.2174 21.6488 7.0652 21.0237 7.6904ZM15.0638 4.2222L19.7778 8.9362" fill="none"/></g>"#,
};
const ICON_EDIT_LG: Icon = Icon {
    name: "kl-pen-185",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M21.0237 7.6904L9.8567 18.8573L2 22L5.1427 14.1433L16.3096 2.9763C16.9348 2.3512 17.7826 2 18.6667 2C19.5507 2 20.3986 2.3512 21.0237 2.9763C21.6488 3.6014 22 4.4493 22 5.3333C22 6.2174 21.6488 7.0652 21.0237 7.6904ZM15.0638 4.2222L19.7778 8.9362" fill="none"/></g>"#,
};

pub fn icon_edit(size: f32, color: Color) -> El {
    let glyph = if size < 24.0 {
        &ICON_EDIT_SM
    } else {
        &ICON_EDIT_LG
    };
    icon(glyph, size).color(color)
}

const ICON_PLUS_SM: Icon = Icon {
    name: "kl-plus-165",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.3" stroke-linecap="round" stroke-linejoin="round"><path d="M5 12H19M11.995 19.005V5.005"/></g>"#,
};
const ICON_PLUS_LG: Icon = Icon {
    name: "kl-plus-185",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M5 12H19M11.995 19.005V5.005"/></g>"#,
};

pub fn icon_plus(size: f32, color: Color) -> El {
    let glyph = if size < 24.0 {
        &ICON_PLUS_SM
    } else {
        &ICON_PLUS_LG
    };
    icon(glyph, size).color(color)
}

const ICON_SCREEN_SHARE_SM: Icon = Icon {
    name: "kl-monitor-2-arrow-up-165",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.3" stroke-linecap="round" stroke-linejoin="round"><path d="M5 3L19 3C20.6569 3 22 4.3431 22 6L22 14C22 15.6569 20.6569 17 19 17L5 17C3.3431 17 2 15.6569 2 14L2 6C2 4.3431 3.3431 3 5 3ZM12 17L12 21M8 21L16 21M12 13L12 7M9 10L12 7L15 10" fill="none"/></g>"#,
};
const ICON_SCREEN_SHARE_LG: Icon = Icon {
    name: "kl-monitor-2-arrow-up-185",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M5 3L19 3C20.6569 3 22 4.3431 22 6L22 14C22 15.6569 20.6569 17 19 17L5 17C3.3431 17 2 15.6569 2 14L2 6C2 4.3431 3.3431 3 5 3ZM12 17L12 21M8 21L16 21M12 13L12 7M9 10L12 7L15 10" fill="none"/></g>"#,
};

pub fn icon_screen_share(size: f32, color: Color) -> El {
    let glyph = if size < 24.0 {
        &ICON_SCREEN_SHARE_SM
    } else {
        &ICON_SCREEN_SHARE_LG
    };
    icon(glyph, size).color(color)
}

const ICON_VIDEO_SM: Icon = Icon {
    name: "kl-video-165",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.3" stroke-linecap="round" stroke-linejoin="round"><path d="M5 12L13 12C14.6569 12 16 13.3431 16 15L16 18C16 19.6569 14.6569 21 13 21L5 21C3.3431 21 2 19.6569 2 18L2 15C2 13.3431 3.3431 12 5 12ZM16 15L20.5528 12.7236C21.2177 12.3912 22 12.8747 22 13.618L22 19.382C22 20.1253 21.2177 20.6088 20.5528 20.2764L16 18M8 5.5C8 6.8807 6.8807 8 5.5 8C4.1193 8 3 6.8807 3 5.5C3 4.1193 4.1193 3 5.5 3C6.8807 3 8 4.1193 8 5.5ZM17 5.5C17 6.8807 15.8807 8 14.5 8C13.1193 8 12 6.8807 12 5.5C12 4.1193 13.1193 3 14.5 3C15.8807 3 17 4.1193 17 5.5Z"/></g>"#,
};
const ICON_VIDEO_LG: Icon = Icon {
    name: "kl-video-185",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M5 12L13 12C14.6569 12 16 13.3431 16 15L16 18C16 19.6569 14.6569 21 13 21L5 21C3.3431 21 2 19.6569 2 18L2 15C2 13.3431 3.3431 12 5 12ZM16 15L20.5528 12.7236C21.2177 12.3912 22 12.8747 22 13.618L22 19.382C22 20.1253 21.2177 20.6088 20.5528 20.2764L16 18M8 5.5C8 6.8807 6.8807 8 5.5 8C4.1193 8 3 6.8807 3 5.5C3 4.1193 4.1193 3 5.5 3C6.8807 3 8 4.1193 8 5.5ZM17 5.5C17 6.8807 15.8807 8 14.5 8C13.1193 8 12 6.8807 12 5.5C12 4.1193 13.1193 3 14.5 3C15.8807 3 17 4.1193 17 5.5Z"/></g>"#,
};

pub fn icon_video(size: f32, color: Color) -> El {
    let glyph = if size < 24.0 {
        &ICON_VIDEO_SM
    } else {
        &ICON_VIDEO_LG
    };
    icon(glyph, size).color(color)
}

const ICON_HAND_SM: Icon = Icon {
    name: "kl-hand-open-165",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.3" stroke-linecap="round" stroke-linejoin="round"><path d="M11.0991 8.384L10.6566 4.586C10.485 3.7192 9.6065 3.1464 8.6906 3.3042C7.771 3.4625 7.1611 4.2971 7.3283 5.1682L8.434 13.6532C8.469 13.9211 8.1257 14.0793 7.924 13.8883L5.4898 11.5822C4.9857 11.1046 4.1909 11.0423 3.6108 11.4349C2.987 11.8571 2.8147 12.6647 3.2165 13.2832L4.9181 15.9026C8.5408 22.1944 12.7674 22.1944 15.1826 21.9082C19.6629 21.1366 20.9836 16.7211 20.998 13.49C21.001 13.3014 21.0001 9.7027 20.9988 7.027C20.9984 6.2092 20.2972 5.5474 19.4323 5.5474C18.5672 5.5474 17.8658 6.2106 17.8658 7.0287L17.8658 7.7302M11.0991 8.384L11.2579 10.4687M11.0991 8.384L10.8717 3.6524C10.8311 2.8082 11.5 2.0831 12.3901 2.0066C13.3264 1.9261 14.1488 2.5916 14.2093 3.4789L14.3364 5.3415M17.9984 10.937L17.8658 7.7302L17.7633 5.1763C17.7274 4.2835 16.9351 3.5861 15.9908 3.6162C15.0234 3.6471 14.2746 4.4281 14.3364 5.3415M14.3364 5.3415L14.6988 10.6534" fill="none"/></g>"#,
};
const ICON_HAND_LG: Icon = Icon {
    name: "kl-hand-open-185",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M11.0991 8.384L10.6566 4.586C10.485 3.7192 9.6065 3.1464 8.6906 3.3042C7.771 3.4625 7.1611 4.2971 7.3283 5.1682L8.434 13.6532C8.469 13.9211 8.1257 14.0793 7.924 13.8883L5.4898 11.5822C4.9857 11.1046 4.1909 11.0423 3.6108 11.4349C2.987 11.8571 2.8147 12.6647 3.2165 13.2832L4.9181 15.9026C8.5408 22.1944 12.7674 22.1944 15.1826 21.9082C19.6629 21.1366 20.9836 16.7211 20.998 13.49C21.001 13.3014 21.0001 9.7027 20.9988 7.027C20.9984 6.2092 20.2972 5.5474 19.4323 5.5474C18.5672 5.5474 17.8658 6.2106 17.8658 7.0287L17.8658 7.7302M11.0991 8.384L11.2579 10.4687M11.0991 8.384L10.8717 3.6524C10.8311 2.8082 11.5 2.0831 12.3901 2.0066C13.3264 1.9261 14.1488 2.5916 14.2093 3.4789L14.3364 5.3415M17.9984 10.937L17.8658 7.7302L17.7633 5.1763C17.7274 4.2835 16.9351 3.5861 15.9908 3.6162C15.0234 3.6471 14.2746 4.4281 14.3364 5.3415M14.3364 5.3415L14.6988 10.6534" fill="none"/></g>"#,
};

pub fn icon_hand(size: f32, color: Color) -> El {
    let glyph = if size < 24.0 {
        &ICON_HAND_SM
    } else {
        &ICON_HAND_LG
    };
    icon(glyph, size).color(color)
}

const ICON_MAXIMIZE_SM: Icon = Icon {
    name: "kl-maximize-2-165",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.3" stroke-linecap="round" stroke-linejoin="round"><path d="M3 9L3 3.5C3 3.2239 3.2239 3 3.5 3L9 3M9 9L3.6 3.6M21 9L21 3.5C21 3.2239 20.7761 3 20.5 3L15 3M15 9L20.4 3.6M21 15L21 20.5C21 20.7761 20.7761 21 20.5 21L15 21M15 15L20.4 20.4M3 15L3 20.5C3 20.7761 3.2239 21 3.5 21L9 21M9 15L3.6 20.4"/></g>"#,
};
const ICON_MAXIMIZE_LG: Icon = Icon {
    name: "kl-maximize-2-185",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M3 9L3 3.5C3 3.2239 3.2239 3 3.5 3L9 3M9 9L3.6 3.6M21 9L21 3.5C21 3.2239 20.7761 3 20.5 3L15 3M15 9L20.4 3.6M21 15L21 20.5C21 20.7761 20.7761 21 20.5 21L15 21M15 15L20.4 20.4M3 15L3 20.5C3 20.7761 3.2239 21 3.5 21L9 21M9 15L3.6 20.4"/></g>"#,
};

pub fn icon_maximize(size: f32, color: Color) -> El {
    let glyph = if size < 24.0 {
        &ICON_MAXIMIZE_SM
    } else {
        &ICON_MAXIMIZE_LG
    };
    icon(glyph, size).color(color)
}

const ICON_LIBRARY_SM: Icon = Icon {
    name: "kl-library-big-165",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.45" stroke-linecap="round" stroke-linejoin="round"><path d="M5 4.5V19.5M10 4.5V19.5M14.5 5L19.5 19"/></g>"#,
};
const ICON_LIBRARY_LG: Icon = Icon {
    name: "kl-library-big-185",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round"><path d="M5 4.5V19.5M10 4.5V19.5M14.5 5L19.5 19"/></g>"#,
};

pub fn icon_library(size: f32, color: Color) -> El {
    let glyph = if size < 24.0 {
        &ICON_LIBRARY_SM
    } else {
        &ICON_LIBRARY_LG
    };
    icon(glyph, size).color(color)
}

const ICON_MOUSE_SM: Icon = Icon {
    name: "kl-mouse-165",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.3" stroke-linecap="round" stroke-linejoin="round"><path d="M12 3C15.3137 3 18 5.6863 18 9L18 15C18 18.3137 15.3137 21 12 21C8.6863 21 6 18.3137 6 15L6 9C6 5.6863 8.6863 3 12 3ZM12 7L12 8"/></g>"#,
};
const ICON_MOUSE_LG: Icon = Icon {
    name: "kl-mouse-185",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M12 3C15.3137 3 18 5.6863 18 9L18 15C18 18.3137 15.3137 21 12 21C8.6863 21 6 18.3137 6 15L6 9C6 5.6863 8.6863 3 12 3ZM12 7L12 8"/></g>"#,
};

pub fn icon_mouse(size: f32, color: Color) -> El {
    let glyph = if size < 24.0 {
        &ICON_MOUSE_SM
    } else {
        &ICON_MOUSE_LG
    };
    icon(glyph, size).color(color)
}

const ICON_MORE_HORIZ_SM: Icon = Icon {
    name: "kl-more-horizontal-165",
    view: 24.0,
    body: r#"<g fill="currentColor" stroke="none"><path d="M7.1 12C7.1 12.88 6.38 13.6 5.5 13.6C4.62 13.6 3.9 12.88 3.9 12C3.9 11.12 4.62 10.4 5.5 10.4C6.38 10.4 7.1 11.12 7.1 12ZM13.6 12C13.6 12.88 12.88 13.6 12 13.6C11.12 13.6 10.4 12.88 10.4 12C10.4 11.12 11.12 10.4 12 10.4C12.88 10.4 13.6 11.12 13.6 12ZM20.1 12C20.1 12.88 19.38 13.6 18.5 13.6C17.62 13.6 16.9 12.88 16.9 12C16.9 11.12 17.62 10.4 18.5 10.4C19.38 10.4 20.1 11.12 20.1 12Z"/></g>"#,
};
const ICON_MORE_HORIZ_LG: Icon = Icon {
    name: "kl-more-horizontal-185",
    view: 24.0,
    body: r#"<g fill="currentColor" stroke="none"><path d="M7.1 12C7.1 12.88 6.38 13.6 5.5 13.6C4.62 13.6 3.9 12.88 3.9 12C3.9 11.12 4.62 10.4 5.5 10.4C6.38 10.4 7.1 11.12 7.1 12ZM13.6 12C13.6 12.88 12.88 13.6 12 13.6C11.12 13.6 10.4 12.88 10.4 12C10.4 11.12 11.12 10.4 12 10.4C12.88 10.4 13.6 11.12 13.6 12ZM20.1 12C20.1 12.88 19.38 13.6 18.5 13.6C17.62 13.6 16.9 12.88 16.9 12C16.9 11.12 17.62 10.4 18.5 10.4C19.38 10.4 20.1 11.12 20.1 12Z"/></g>"#,
};

pub fn icon_more_horiz(size: f32, color: Color) -> El {
    let glyph = if size < 24.0 {
        &ICON_MORE_HORIZ_SM
    } else {
        &ICON_MORE_HORIZ_LG
    };
    icon(glyph, size).color(color)
}

const ICON_FILTER_SM: Icon = Icon {
    name: "kl-filter-165",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.3" stroke-linecap="round" stroke-linejoin="round"><path d="M3.2903 4.7025C2.6666 4.0707 3.1142 3 4.002 3L19.998 3C20.8858 3 21.3334 4.0707 20.7097 4.7025L14.2883 11.2079C14.1036 11.3951 14 11.6474 14 11.9104L14 18.4997C14 18.8146 13.8516 19.1112 13.5996 19.3L11.5996 20.7985C10.9403 21.2925 10 20.822 10 19.9982L10 11.9104C10 11.6474 9.8964 11.3951 9.7117 11.2079L3.2903 4.7025C2.6666 4.0707 3.1142 3 4.002 3Z" fill="none"/></g>"#,
};
const ICON_FILTER_LG: Icon = Icon {
    name: "kl-filter-185",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M3.2903 4.7025C2.6666 4.0707 3.1142 3 4.002 3L19.998 3C20.8858 3 21.3334 4.0707 20.7097 4.7025L14.2883 11.2079C14.1036 11.3951 14 11.6474 14 11.9104L14 18.4997C14 18.8146 13.8516 19.1112 13.5996 19.3L11.5996 20.7985C10.9403 21.2925 10 20.822 10 19.9982L10 11.9104C10 11.6474 9.8964 11.3951 9.7117 11.2079L3.2903 4.7025C2.6666 4.0707 3.1142 3 4.002 3Z" fill="none"/></g>"#,
};

pub fn icon_filter(size: f32, color: Color) -> El {
    let glyph = if size < 24.0 {
        &ICON_FILTER_SM
    } else {
        &ICON_FILTER_LG
    };
    icon(glyph, size).color(color)
}

const ICON_CHEVRON_LEFT_SM: Icon = Icon {
    name: "kl-chevron-left-165",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.3" stroke-linecap="round" stroke-linejoin="round"><path d="M15 6L9 12L15 18"/></g>"#,
};
const ICON_CHEVRON_LEFT_LG: Icon = Icon {
    name: "kl-chevron-left-185",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M15 6L9 12L15 18"/></g>"#,
};

pub fn icon_chevron_left(size: f32, color: Color) -> El {
    let glyph = if size < 24.0 {
        &ICON_CHEVRON_LEFT_SM
    } else {
        &ICON_CHEVRON_LEFT_LG
    };
    icon(glyph, size).color(color)
}

const ICON_REPLAY_SM: Icon = Icon {
    name: "kl-replay-165",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.3" stroke-linecap="round" stroke-linejoin="round"><path d="M20.7883 10.0593C20.929 10.6966 21 11.3473 21 12C21 16.9706 16.9706 21 12 21C7.0294 21 3 16.9706 3 12C3 7.0294 7.0294 3 12 3C14.4831 3 16.8559 4.0259 18.5568 5.8349M19.7143 3L19.2707 6.105C19.2317 6.3784 18.9784 6.5683 18.705 6.5293L15.6 6.0857M8.962734 9.105541C8.962734 8.320105 9.826684 7.841262 10.492732 8.257542L15.802266 11.576001C16.1156 11.771835 16.1156 12.228165 15.802266 12.423999L10.492732 15.742458C9.826684 16.158738 8.962734 15.679895 8.962734 14.894459Z"/></g>"#,
};
const ICON_REPLAY_LG: Icon = Icon {
    name: "kl-replay-185",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M20.7883 10.0593C20.929 10.6966 21 11.3473 21 12C21 16.9706 16.9706 21 12 21C7.0294 21 3 16.9706 3 12C3 7.0294 7.0294 3 12 3C14.4831 3 16.8559 4.0259 18.5568 5.8349M19.7143 3L19.2707 6.105C19.2317 6.3784 18.9784 6.5683 18.705 6.5293L15.6 6.0857M8.962734 9.105541C8.962734 8.320105 9.826684 7.841262 10.492732 8.257542L15.802266 11.576001C16.1156 11.771835 16.1156 12.228165 15.802266 12.423999L10.492732 15.742458C9.826684 16.158738 8.962734 15.679895 8.962734 14.894459Z"/></g>"#,
};

pub fn icon_replay(size: f32, color: Color) -> El {
    let glyph = if size < 24.0 {
        &ICON_REPLAY_SM
    } else {
        &ICON_REPLAY_LG
    };
    icon(glyph, size).color(color)
}

const ICON_RECORD_DOT_SOLID: Icon = Icon {
    name: "kl-circle-record-solid",
    view: 24.0,
    body: r#"<g fill="currentColor" stroke="none"><path d="M23 12C23 18.0751 18.0751 23 12 23C5.9249 23 1 18.0751 1 12C1 5.9249 5.9249 1 12 1C18.0751 1 23 5.9249 23 12ZM17 12C17 14.7614 14.7614 17 12 17C9.2386 17 7 14.7614 7 12C7 9.2386 9.2386 7 12 7C14.7614 7 17 9.2386 17 12Z" fill="currentColor" fill-rule="evenodd" clip-rule="evenodd"/></g>"#,
};

pub fn icon_record_dot(size: f32, color: Color) -> El {
    icon(&ICON_RECORD_DOT_SOLID, size).color(color)
}

const ICON_BOOKMARK_SM: Icon = Icon {
    name: "kl-bookmark-165",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.3" stroke-linecap="round" stroke-linejoin="round"><path d="M4 5C4 3.3431 5.3431 2 7 2L17 2C18.6569 2 20 3.3431 20 5L20 20.9983C20 21.7895 19.1248 22.2673 18.4592 21.8395L12.8111 18.8514C12.317 18.5338 11.683 18.5338 11.1889 18.8514L5.5408 21.8395C4.8752 22.2673 4 21.7895 4 20.9983Z"/></g>"#,
};
const ICON_BOOKMARK_LG: Icon = Icon {
    name: "kl-bookmark-185",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M4 5C4 3.3431 5.3431 2 7 2L17 2C18.6569 2 20 3.3431 20 5L20 20.9983C20 21.7895 19.1248 22.2673 18.4592 21.8395L12.8111 18.8514C12.317 18.5338 11.683 18.5338 11.1889 18.8514L5.5408 21.8395C4.8752 22.2673 4 21.7895 4 20.9983Z"/></g>"#,
};

pub fn icon_bookmark(size: f32, color: Color) -> El {
    let glyph = if size < 24.0 {
        &ICON_BOOKMARK_SM
    } else {
        &ICON_BOOKMARK_LG
    };
    icon(glyph, size).color(color)
}

const ICON_MINIMIZE_SM: Icon = Icon {
    name: "kl-minus-165",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.25" stroke-linecap="round" stroke-linejoin="round"><path d="M6 12H18"/></g>"#,
};
const ICON_MINIMIZE_LG: Icon = Icon {
    name: "kl-minus-185",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.45" stroke-linecap="round" stroke-linejoin="round"><path d="M6 12H18"/></g>"#,
};

pub fn icon_minimize(size: f32, color: Color) -> El {
    let glyph = if size < 24.0 {
        &ICON_MINIMIZE_SM
    } else {
        &ICON_MINIMIZE_LG
    };
    icon(glyph, size).color(color)
}

const ICON_WIN_MAXIMIZE_SM: Icon = Icon {
    name: "kl-square-165",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.25" stroke-linecap="round" stroke-linejoin="round"><path d="M7.5 5.5H16.5C17.6046 5.5 18.5 6.3954 18.5 7.5V16.5C18.5 17.6046 17.6046 18.5 16.5 18.5H7.5C6.3954 18.5 5.5 17.6046 5.5 16.5V7.5C5.5 6.3954 6.3954 5.5 7.5 5.5Z"/></g>"#,
};
const ICON_WIN_MAXIMIZE_LG: Icon = Icon {
    name: "kl-square-185",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.45" stroke-linecap="round" stroke-linejoin="round"><path d="M7.5 5.5H16.5C17.6046 5.5 18.5 6.3954 18.5 7.5V16.5C18.5 17.6046 17.6046 18.5 16.5 18.5H7.5C6.3954 18.5 5.5 17.6046 5.5 16.5V7.5C5.5 6.3954 6.3954 5.5 7.5 5.5Z"/></g>"#,
};

pub fn icon_win_maximize(size: f32, color: Color) -> El {
    let glyph = if size < 24.0 {
        &ICON_WIN_MAXIMIZE_SM
    } else {
        &ICON_WIN_MAXIMIZE_LG
    };
    icon(glyph, size).color(color)
}

const ICON_WIN_RESTORE_SM: Icon = Icon {
    name: "kl-win-restore-125",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.25" stroke-linecap="round" stroke-linejoin="round"><path d="M8.5 7.5V6.5C8.5 5.3954 9.3954 4.5 10.5 4.5H17.5C18.6046 4.5 19.5 5.3954 19.5 6.5V13.5C19.5 14.6046 18.6046 15.5 17.5 15.5H16.5M6.5 8.5H13.5C14.6046 8.5 15.5 9.3954 15.5 10.5V17.5C15.5 18.6046 14.6046 19.5 13.5 19.5H6.5C5.3954 19.5 4.5 18.6046 4.5 17.5V10.5C4.5 9.3954 5.3954 8.5 6.5 8.5Z"/></g>"#,
};
const ICON_WIN_RESTORE_LG: Icon = Icon {
    name: "kl-win-restore-145",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.45" stroke-linecap="round" stroke-linejoin="round"><path d="M8.5 7.5V6.5C8.5 5.3954 9.3954 4.5 10.5 4.5H17.5C18.6046 4.5 19.5 5.3954 19.5 6.5V13.5C19.5 14.6046 18.6046 15.5 17.5 15.5H16.5M6.5 8.5H13.5C14.6046 8.5 15.5 9.3954 15.5 10.5V17.5C15.5 18.6046 14.6046 19.5 13.5 19.5H6.5C5.3954 19.5 4.5 18.6046 4.5 17.5V10.5C4.5 9.3954 5.3954 8.5 6.5 8.5Z"/></g>"#,
};

pub fn icon_win_restore(size: f32, color: Color) -> El {
    let glyph = if size < 24.0 {
        &ICON_WIN_RESTORE_SM
    } else {
        &ICON_WIN_RESTORE_LG
    };
    icon(glyph, size).color(color)
}

const ICON_INFO_SM: Icon = Icon {
    name: "kl-info-165",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.3" stroke-linecap="round" stroke-linejoin="round"><path d="M12 2C17.5228 2 22 6.47715 22 12C22 17.5228 17.5228 22 12 22C6.47715 22 2 17.5228 2 12C2 6.47715 6.47715 2 12 2Z"/><path d="M12 12V16"/><path d="M13 8C13 8.5523 12.5523 9 12 9C11.4477 9 11 8.5523 11 8C11 7.4477 11.4477 7 12 7C12.5523 7 13 7.4477 13 8Z" fill="currentColor" stroke="none"/></g>"#,
};
const ICON_INFO_LG: Icon = Icon {
    name: "kl-info-185",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M12 2C17.5228 2 22 6.47715 22 12C22 17.5228 17.5228 22 12 22C6.47715 22 2 17.5228 2 12C2 6.47715 6.47715 2 12 2Z"/><path d="M12 12V16"/><path d="M13 8C13 8.5523 12.5523 9 12 9C11.4477 9 11 8.5523 11 8C11 7.4477 11.4477 7 12 7C12.5523 7 13 7.4477 13 8Z" fill="currentColor" stroke="none"/></g>"#,
};

pub fn icon_info(size: f32, color: Color) -> El {
    let glyph = if size < 24.0 {
        &ICON_INFO_SM
    } else {
        &ICON_INFO_LG
    };
    icon(glyph, size).color(color)
}

const ICON_COPY_SM: Icon = Icon {
    name: "kl-copy-165",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.3" stroke-linecap="round" stroke-linejoin="round"><path d="M15.8284 4C15.4046 2.8015 14.2714 2 13 2H5C3.34315 2 2 3.34315 2 5V13C2 14.2714 2.8015 15.4046 4 15.8284M11 8H19C20.65684 8 22 9.34316 22 11V19C22 20.65684 20.65684 22 19 22H11C9.34316 22 8 20.65684 8 19V11C8 9.34316 9.34316 8 11 8Z"/></g>"#,
};
const ICON_COPY_LG: Icon = Icon {
    name: "kl-copy-185",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M15.8284 4C15.4046 2.8015 14.2714 2 13 2H5C3.34315 2 2 3.34315 2 5V13C2 14.2714 2.8015 15.4046 4 15.8284M11 8H19C20.65684 8 22 9.34316 22 11V19C22 20.65684 20.65684 22 19 22H11C9.34316 22 8 20.65684 8 19V11C8 9.34316 9.34316 8 11 8Z"/></g>"#,
};

pub fn icon_copy(size: f32, color: Color) -> El {
    let glyph = if size < 24.0 {
        &ICON_COPY_SM
    } else {
        &ICON_COPY_LG
    };
    icon(glyph, size).color(color)
}

const ICON_LINK_SM: Icon = Icon {
    name: "kl-link-165",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.3" stroke-linecap="round" stroke-linejoin="round"><path d="M8.1883 18.4383C7.5084 19.1181 6.5864 19.5 5.625 19.5C4.6636 19.5 3.7416 19.1181 3.0617 18.4383C2.3819 17.7584 2 16.8364 2 15.875C2 14.9136 2.3819 13.9916 3.0617 13.3117L8.1867 8.1867C8.8666 7.5069 9.7886 7.125 10.75 7.125C11.7114 7.125 12.6334 7.5069 13.3133 8.1867C13.9931 8.8666 14.375 9.7886 14.375 10.75C14.375 11.7114 13.9931 12.6334 13.3133 13.3133M15.8117 5.5617C16.4916 4.8819 17.4136 4.5 18.375 4.5C19.3364 4.5 20.2584 4.8819 20.9383 5.5617C21.6181 6.2416 22 7.1636 22 8.125C22 9.0864 21.6181 10.0084 20.9383 10.6883L15.8133 15.8133C15.1334 16.4931 14.2114 16.875 13.25 16.875C12.2886 16.875 11.3666 16.4931 10.6867 15.8133C10.0069 15.1334 9.625 14.2114 9.625 13.25C9.625 12.2886 10.0069 11.3666 10.6867 10.6867" fill="none"/></g>"#,
};
const ICON_LINK_LG: Icon = Icon {
    name: "kl-link-185",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M8.1883 18.4383C7.5084 19.1181 6.5864 19.5 5.625 19.5C4.6636 19.5 3.7416 19.1181 3.0617 18.4383C2.3819 17.7584 2 16.8364 2 15.875C2 14.9136 2.3819 13.9916 3.0617 13.3117L8.1867 8.1867C8.8666 7.5069 9.7886 7.125 10.75 7.125C11.7114 7.125 12.6334 7.5069 13.3133 8.1867C13.9931 8.8666 14.375 9.7886 14.375 10.75C14.375 11.7114 13.9931 12.6334 13.3133 13.3133M15.8117 5.5617C16.4916 4.8819 17.4136 4.5 18.375 4.5C19.3364 4.5 20.2584 4.8819 20.9383 5.5617C21.6181 6.2416 22 7.1636 22 8.125C22 9.0864 21.6181 10.0084 20.9383 10.6883L15.8133 15.8133C15.1334 16.4931 14.2114 16.875 13.25 16.875C12.2886 16.875 11.3666 16.4931 10.6867 15.8133C10.0069 15.1334 9.625 14.2114 9.625 13.25C9.625 12.2886 10.0069 11.3666 10.6867 10.6867" fill="none"/></g>"#,
};

pub fn icon_link(size: f32, color: Color) -> El {
    let glyph = if size < 24.0 {
        &ICON_LINK_SM
    } else {
        &ICON_LINK_LG
    };
    icon(glyph, size).color(color)
}

const ICON_FLAG_SM: Icon = Icon {
    name: "kl-flag-165",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.3" stroke-linecap="round" stroke-linejoin="round"><path d="M4 13.3739C4 13.7715 4.2356 14.1313 4.6 14.2904C5.6724 14.7584 6.8299 15 8 15C9.3952 15 10.7689 14.6566 12 14C13.2311 13.3434 14.6048 13 16 13C16.9123 13 17.8186 13.1469 18.6842 13.435C18.786 13.4688 18.8927 13.4861 19 13.4861C19.5523 13.4861 20 13.0384 20 12.4861L20 3.6261C20 3.2285 19.7644 2.8687 19.4 2.7096C18.3276 2.2416 17.1701 2 16 2C14.6048 2 13.2311 2.3434 12 3C10.7689 3.6566 9.3952 4 8 4C6.6048 4 5.2311 3.6566 4 3L4 22"/></g>"#,
};
const ICON_FLAG_LG: Icon = Icon {
    name: "kl-flag-185",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M4 13.3739C4 13.7715 4.2356 14.1313 4.6 14.2904C5.6724 14.7584 6.8299 15 8 15C9.3952 15 10.7689 14.6566 12 14C13.2311 13.3434 14.6048 13 16 13C16.9123 13 17.8186 13.1469 18.6842 13.435C18.786 13.4688 18.8927 13.4861 19 13.4861C19.5523 13.4861 20 13.0384 20 12.4861L20 3.6261C20 3.2285 19.7644 2.8687 19.4 2.7096C18.3276 2.2416 17.1701 2 16 2C14.6048 2 13.2311 2.3434 12 3C10.7689 3.6566 9.3952 4 8 4C6.6048 4 5.2311 3.6566 4 3L4 22"/></g>"#,
};

pub fn icon_flag(size: f32, color: Color) -> El {
    let glyph = if size < 24.0 {
        &ICON_FLAG_SM
    } else {
        &ICON_FLAG_LG
    };
    icon(glyph, size).color(color)
}

const ICON_FORWARD_SM: Icon = Icon {
    name: "kl-forward-165",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.3" stroke-linecap="round" stroke-linejoin="round"><path d="M16 4L22 10L16 16M22 10L6 10C3.7909 10 2 11.7909 2 14L2 20"/></g>"#,
};
const ICON_FORWARD_LG: Icon = Icon {
    name: "kl-forward-185",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M16 4L22 10L16 16M22 10L6 10C3.7909 10 2 11.7909 2 14L2 20"/></g>"#,
};

pub fn icon_forward(size: f32, color: Color) -> El {
    let glyph = if size < 24.0 {
        &ICON_FORWARD_SM
    } else {
        &ICON_FORWARD_LG
    };
    icon(glyph, size).color(color)
}

const ICON_CHEVRON_UP_SM: Icon = Icon {
    name: "kl-chevron-up-165",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.3" stroke-linecap="round" stroke-linejoin="round"><path d="M6 15L12 9L18 15"/></g>"#,
};
const ICON_CHEVRON_UP_LG: Icon = Icon {
    name: "kl-chevron-up-185",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M6 15L12 9L18 15"/></g>"#,
};

pub fn icon_chevron_up(size: f32, color: Color) -> El {
    let glyph = if size < 24.0 {
        &ICON_CHEVRON_UP_SM
    } else {
        &ICON_CHEVRON_UP_LG
    };
    icon(glyph, size).color(color)
}

const ICON_ALERT_SM: Icon = Icon {
    name: "kl-circle-alert-165",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.3" stroke-linecap="round" stroke-linejoin="round"><path d="M22 12C22 17.52285 17.52285 22 12 22C6.47715 22 2 17.52285 2 12C2 6.47715 6.47715 2 12 2C17.52285 2 22 6.47715 22 12Z"/><path d="M12 7V13"/><path d="M13 17C13 17.55228 12.55228 18 12 18C11.44772 18 11 17.55228 11 17C11 16.44772 11.44772 16 12 16C12.55228 16 13 16.44772 13 17Z" fill="currentColor" stroke="none"/></g>"#,
};
const ICON_ALERT_LG: Icon = Icon {
    name: "kl-circle-alert-185",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M22 12C22 17.52285 17.52285 22 12 22C6.47715 22 2 17.52285 2 12C2 6.47715 6.47715 2 12 2C17.52285 2 22 6.47715 22 12Z"/><path d="M12 7V13"/><path d="M13 17C13 17.55228 12.55228 18 12 18C11.44772 18 11 17.55228 11 17C11 16.44772 11.44772 16 12 16C12.55228 16 13 16.44772 13 17Z" fill="currentColor" stroke="none"/></g>"#,
};

pub fn icon_alert(size: f32, color: Color) -> El {
    let glyph = if size < 24.0 {
        &ICON_ALERT_SM
    } else {
        &ICON_ALERT_LG
    };
    icon(glyph, size).color(color)
}

const ICON_BELL_SM: Icon = Icon {
    name: "kl-bell-165",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.3" stroke-linecap="round" stroke-linejoin="round"><path d="M12 3C14.7614 3 17 5.23858 17 8C17 13 19 14 19 15C19 15.5523 18.5523 16 18 16H6C5.44772 16 5 15.5523 5 15C5 14 7 13 7 8C7 5.23858 9.23858 3 12 3ZM10.2679 20C10.6252 20.6188 11.2855 21 12 21C12.7145 21 13.3748 20.6188 13.7321 20"/></g>"#,
};
const ICON_BELL_LG: Icon = Icon {
    name: "kl-bell-185",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M12 3C14.7614 3 17 5.23858 17 8C17 13 19 14 19 15C19 15.5523 18.5523 16 18 16H6C5.44772 16 5 15.5523 5 15C5 14 7 13 7 8C7 5.23858 9.23858 3 12 3ZM10.2679 20C10.6252 20.6188 11.2855 21 12 21C12.7145 21 13.3748 20.6188 13.7321 20"/></g>"#,
};

pub fn icon_bell(size: f32, color: Color) -> El {
    let glyph = if size < 24.0 {
        &ICON_BELL_SM
    } else {
        &ICON_BELL_LG
    };
    icon(glyph, size).color(color)
}

const ICON_GIFT_SM: Icon = Icon {
    name: "kl-gift-165",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.3" stroke-linecap="round" stroke-linejoin="round"><path d="M12 7H3C2.44771 7 2 7.44771 2 8V10C2 10.5523 2.44771 11 3 11H21C21.5523 11 22 10.5523 22 10V8C22 7.44771 21.5523 7 21 7H12ZM12 7C9 7 7 6 7 4.5C7 3.11929 8.11929 2 9.5 2C11.5 2 12 5 12 7ZM12 7C15 7 17 6 17 4.5C17 3.11929 15.8807 2 14.5 2C12.5 2 12 5 12 7ZM4 11V19C4 20.6569 5.34315 22 7 22H17C18.6569 22 20 20.6569 20 19V11H4Z"/></g>"#,
};
const ICON_GIFT_LG: Icon = Icon {
    name: "kl-gift-185",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M12 7H3C2.44771 7 2 7.44771 2 8V10C2 10.5523 2.44771 11 3 11H21C21.5523 11 22 10.5523 22 10V8C22 7.44771 21.5523 7 21 7H12ZM12 7C9 7 7 6 7 4.5C7 3.11929 8.11929 2 9.5 2C11.5 2 12 5 12 7ZM12 7C15 7 17 6 17 4.5C17 3.11929 15.8807 2 14.5 2C12.5 2 12 5 12 7ZM4 11V19C4 20.6569 5.34315 22 7 22H17C18.6569 22 20 20.6569 20 19V11H4Z"/></g>"#,
};

pub fn icon_gift(size: f32, color: Color) -> El {
    let glyph = if size < 24.0 {
        &ICON_GIFT_SM
    } else {
        &ICON_GIFT_LG
    };
    icon(glyph, size).color(color)
}

const ICON_VOLUME_LOW_SM: Icon = Icon {
    name: "kl-volume-low-165",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.3" stroke-linecap="round" stroke-linejoin="round"><path d="M2 10C2 9.447715 2.447715 9 3 9L5.477658 9C5.804909 9 6.111453 8.839879 6.298408 8.571288L9.17925 4.432477C9.738582 3.628903 11 4.024692 11 5.003764L11 18.996236C11 19.975308 9.738582 20.371097 9.17925 19.567523L6.298408 15.428712C6.111453 15.160121 5.804909 15 5.477658 15L3 15C2.447715 15 2 14.552285 2 14ZM15.80444 6.909091C17.205764 8.231561 18 10.073179 18 12C18 13.926821 17.205764 15.768439 15.80444 17.090909"/></g>"#,
};
const ICON_VOLUME_LOW_LG: Icon = Icon {
    name: "kl-volume-low-185",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M2 10C2 9.447715 2.447715 9 3 9L5.477658 9C5.804909 9 6.111453 8.839879 6.298408 8.571288L9.17925 4.432477C9.738582 3.628903 11 4.024692 11 5.003764L11 18.996236C11 19.975308 9.738582 20.371097 9.17925 19.567523L6.298408 15.428712C6.111453 15.160121 5.804909 15 5.477658 15L3 15C2.447715 15 2 14.552285 2 14ZM15.80444 6.909091C17.205764 8.231561 18 10.073179 18 12C18 13.926821 17.205764 15.768439 15.80444 17.090909"/></g>"#,
};

pub fn icon_volume_low(size: f32, color: Color) -> El {
    let glyph = if size < 24.0 {
        &ICON_VOLUME_LOW_SM
    } else {
        &ICON_VOLUME_LOW_LG
    };
    icon(glyph, size).color(color)
}

const ICON_FOLDER_PLUS_SM: Icon = Icon {
    name: "kl-folder-plus-165",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.3" stroke-linecap="round" stroke-linejoin="round"><path d="M10 20L6 20C4.3431 20 3 18.6569 3 17L3 7C3 5.3431 4.3431 4 6 4L8.6716 4C9.202 4 9.7107 4.2107 10.0858 4.5858L11.4142 5.9142C11.7893 6.2893 12.298 6.5 12.8284 6.5L18 6.5C19.6569 6.5 21 7.8431 21 9.5L21 10M18 14V20M15 17H21"/></g>"#,
};
const ICON_FOLDER_PLUS_LG: Icon = Icon {
    name: "kl-folder-plus-185",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M10 20L6 20C4.3431 20 3 18.6569 3 17L3 7C3 5.3431 4.3431 4 6 4L8.6716 4C9.202 4 9.7107 4.2107 10.0858 4.5858L11.4142 5.9142C11.7893 6.2893 12.298 6.5 12.8284 6.5L18 6.5C19.6569 6.5 21 7.8431 21 9.5L21 10M18 14V20M15 17H21"/></g>"#,
};

pub fn icon_folder_plus(size: f32, color: Color) -> El {
    let glyph = if size < 24.0 {
        &ICON_FOLDER_PLUS_SM
    } else {
        &ICON_FOLDER_PLUS_LG
    };
    icon(glyph, size).color(color)
}

const ICON_NOTES_SM: Icon = Icon {
    name: "kl-notebook-pen-165",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.3" stroke-linecap="round" stroke-linejoin="round"><path d="M20 12.1317L20 19C20 20.6569 18.6569 22 17 22L7 22C5.3431 22 4 20.6569 4 19L4 5C4 3.3431 5.3431 2 7 2L13.8683 2M2 7L6 7M2 12L6 12M2 17L6 17M11.5 12.5L15.6248 10.8501L21.4874 4.9874C22.1709 4.304 22.1709 3.196 21.4874 2.5126C20.804 1.8291 19.696 1.8291 19.0126 2.5126L13.1499 8.3752Z" fill="none"/></g>"#,
};
const ICON_NOTES_LG: Icon = Icon {
    name: "kl-notebook-pen-185",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M20 12.1317L20 19C20 20.6569 18.6569 22 17 22L7 22C5.3431 22 4 20.6569 4 19L4 5C4 3.3431 5.3431 2 7 2L13.8683 2M2 7L6 7M2 12L6 12M2 17L6 17M11.5 12.5L15.6248 10.8501L21.4874 4.9874C22.1709 4.304 22.1709 3.196 21.4874 2.5126C20.804 1.8291 19.696 1.8291 19.0126 2.5126L13.1499 8.3752Z" fill="none"/></g>"#,
};

pub fn icon_notes(size: f32, color: Color) -> El {
    let glyph = if size < 24.0 {
        &ICON_NOTES_SM
    } else {
        &ICON_NOTES_LG
    };
    icon(glyph, size).color(color)
}

const ICON_EYE_SM: Icon = Icon {
    name: "kl-eye-165",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.3" stroke-linecap="round" stroke-linejoin="round"><path d="M2 12C3.0523 7.3229 7.206 4 12 4C16.794 4 20.9477 7.3229 22 12C20.9477 16.6771 16.794 20 12 20C7.206 20 3.0523 16.6771 2 12ZM15 12C15 13.6569 13.6569 15 12 15C10.3431 15 9 13.6569 9 12C9 10.3431 10.3431 9 12 9C13.6569 9 15 10.3431 15 12Z"/></g>"#,
};
const ICON_EYE_LG: Icon = Icon {
    name: "kl-eye-185",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M2 12C3.0523 7.3229 7.206 4 12 4C16.794 4 20.9477 7.3229 22 12C20.9477 16.6771 16.794 20 12 20C7.206 20 3.0523 16.6771 2 12ZM15 12C15 13.6569 13.6569 15 12 15C10.3431 15 9 13.6569 9 12C9 10.3431 10.3431 9 12 9C13.6569 9 15 10.3431 15 12Z"/></g>"#,
};

pub fn icon_eye(size: f32, color: Color) -> El {
    let glyph = if size < 24.0 {
        &ICON_EYE_SM
    } else {
        &ICON_EYE_LG
    };
    icon(glyph, size).color(color)
}

const ICON_EYE_OFF_SM: Icon = Icon {
    name: "kl-eye-off-165",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.3" stroke-linecap="round" stroke-linejoin="round"><path d="M18.035 18.035C16.3283 19.2786 14.2337 20 12 20C7.206 20 3.0523 16.6771 2 12C2.562 9.5021 4.0086 7.3904 5.965 5.965M9.8779 4.2211C10.565 4.076 11.2752 4 12 4C16.794 4 20.9477 7.3229 22 12C21.7487 13.1169 21.3206 14.1566 20.7491 15.0922M14.1213 14.1213C13.5785 14.6642 12.8285 15 12 15C10.3431 15 9 13.6569 9 12C9 11.1716 9.3358 10.4216 9.8787 9.8787M2 2L22 22"/></g>"#,
};
const ICON_EYE_OFF_LG: Icon = Icon {
    name: "kl-eye-off-185",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M18.035 18.035C16.3283 19.2786 14.2337 20 12 20C7.206 20 3.0523 16.6771 2 12C2.562 9.5021 4.0086 7.3904 5.965 5.965M9.8779 4.2211C10.565 4.076 11.2752 4 12 4C16.794 4 20.9477 7.3229 22 12C21.7487 13.1169 21.3206 14.1566 20.7491 15.0922M14.1213 14.1213C13.5785 14.6642 12.8285 15 12 15C10.3431 15 9 13.6569 9 12C9 11.1716 9.3358 10.4216 9.8787 9.8787M2 2L22 22"/></g>"#,
};

pub fn icon_eye_off(size: f32, color: Color) -> El {
    let glyph = if size < 24.0 {
        &ICON_EYE_OFF_SM
    } else {
        &ICON_EYE_OFF_LG
    };
    icon(glyph, size).color(color)
}

const ICON_IMAGE_UPLOAD_SM: Icon = Icon {
    name: "kl-image-arrow-up-165",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.3" stroke-linecap="round" stroke-linejoin="round"><path d="M11 3L6 3C4.3431 3 3 4.3431 3 6L3 18C3 19.6569 4.3431 21 6 21L18 21C19.6569 21 21 19.6569 21 18L21 13M3 18L7.9393 13.0607C8.5251 12.4749 9.4749 12.4749 10.0607 13.0607L12.0801 15.0801C12.6079 15.6079 13.4436 15.6673 14.0408 15.2194L15.9592 13.7806C16.5564 13.3327 17.3921 13.3921 17.9199 13.9199L21 17M18 9V3M15 6L18 3L21 6"/><path d="M9.5 7.5C9.5 8.3284 8.8284 9 8 9C7.1716 9 6.5 8.3284 6.5 7.5C6.5 6.6716 7.1716 6 8 6C8.8284 6 9.5 6.6716 9.5 7.5Z" fill="currentColor" stroke="none"/></g>"#,
};
const ICON_IMAGE_UPLOAD_LG: Icon = Icon {
    name: "kl-image-arrow-up-185",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M11 3L6 3C4.3431 3 3 4.3431 3 6L3 18C3 19.6569 4.3431 21 6 21L18 21C19.6569 21 21 19.6569 21 18L21 13M3 18L7.9393 13.0607C8.5251 12.4749 9.4749 12.4749 10.0607 13.0607L12.0801 15.0801C12.6079 15.6079 13.4436 15.6673 14.0408 15.2194L15.9592 13.7806C16.5564 13.3327 17.3921 13.3921 17.9199 13.9199L21 17M18 9V3M15 6L18 3L21 6"/><path d="M9.5 7.5C9.5 8.3284 8.8284 9 8 9C7.1716 9 6.5 8.3284 6.5 7.5C6.5 6.6716 7.1716 6 8 6C8.8284 6 9.5 6.6716 9.5 7.5Z" fill="currentColor" stroke="none"/></g>"#,
};

pub fn icon_image_upload(size: f32, color: Color) -> El {
    let glyph = if size < 24.0 {
        &ICON_IMAGE_UPLOAD_SM
    } else {
        &ICON_IMAGE_UPLOAD_LG
    };
    icon(glyph, size).color(color)
}

const ICON_ZOOM_IN_SM: Icon = Icon {
    name: "kl-search-plus-165",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.3" stroke-linecap="round" stroke-linejoin="round"><path d="M17 10C17 13.866 13.866 17 10 17C6.134 17 3 13.866 3 10C3 6.134 6.134 3 10 3C13.866 3 17 6.134 17 10ZM15 15L21 21M7 10L13 10M10 7L10 13"/></g>"#,
};
const ICON_ZOOM_IN_LG: Icon = Icon {
    name: "kl-search-plus-185",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M17 10C17 13.866 13.866 17 10 17C6.134 17 3 13.866 3 10C3 6.134 6.134 3 10 3C13.866 3 17 6.134 17 10ZM15 15L21 21M7 10L13 10M10 7L10 13"/></g>"#,
};

pub fn icon_zoom_in(size: f32, color: Color) -> El {
    let glyph = if size < 24.0 {
        &ICON_ZOOM_IN_SM
    } else {
        &ICON_ZOOM_IN_LG
    };
    icon(glyph, size).color(color)
}

const ICON_ZOOM_OUT_SM: Icon = Icon {
    name: "kl-search-minus-165",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.3" stroke-linecap="round" stroke-linejoin="round"><path d="M17 10C17 13.866 13.866 17 10 17C6.134 17 3 13.866 3 10C3 6.134 6.134 3 10 3C13.866 3 17 6.134 17 10ZM15 15L21 21M7 10L13 10"/></g>"#,
};
const ICON_ZOOM_OUT_LG: Icon = Icon {
    name: "kl-search-minus-185",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M17 10C17 13.866 13.866 17 10 17C6.134 17 3 13.866 3 10C3 6.134 6.134 3 10 3C13.866 3 17 6.134 17 10ZM15 15L21 21M7 10L13 10"/></g>"#,
};

pub fn icon_zoom_out(size: f32, color: Color) -> El {
    let glyph = if size < 24.0 {
        &ICON_ZOOM_OUT_SM
    } else {
        &ICON_ZOOM_OUT_LG
    };
    icon(glyph, size).color(color)
}

const ICON_SELECT_MANY_SM: Icon = Icon {
    name: "kl-square-check-165",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.3" stroke-linecap="round" stroke-linejoin="round"><path d="M6 3H18C19.6569 3 21 4.34315 21 6V18C21 19.6569 19.6569 21 18 21H6C4.34315 21 3 19.6569 3 18V6C3 4.34315 4.34315 3 6 3Z"/><path d="M8 12L10.6667 15L16 9"/></g>"#,
};
const ICON_SELECT_MANY_LG: Icon = Icon {
    name: "kl-square-check-185",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M6 3H18C19.6569 3 21 4.34315 21 6V18C21 19.6569 19.6569 21 18 21H6C4.34315 21 3 19.6569 3 18V6C3 4.34315 4.34315 3 6 3Z"/><path d="M8 12L10.6667 15L16 9"/></g>"#,
};

pub fn icon_select_many(size: f32, color: Color) -> El {
    let glyph = if size < 24.0 {
        &ICON_SELECT_MANY_SM
    } else {
        &ICON_SELECT_MANY_LG
    };
    icon(glyph, size).color(color)
}

const ICON_UPLOAD_SM: Icon = Icon {
    name: "kl-upload-165",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.3" stroke-linecap="round" stroke-linejoin="round"><path d="M12 14V3M8 7L12 3L16 7M4 18V19C4 20.1046 4.89543 21 6 21H18C19.1046 21 20 20.1046 20 19V18"/></g>"#,
};
const ICON_UPLOAD_LG: Icon = Icon {
    name: "kl-upload-185",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M12 14V3M8 7L12 3L16 7M4 18V19C4 20.1046 4.89543 21 6 21H18C19.1046 21 20 20.1046 20 19V18"/></g>"#,
};

pub fn icon_upload(size: f32, color: Color) -> El {
    let glyph = if size < 24.0 {
        &ICON_UPLOAD_SM
    } else {
        &ICON_UPLOAD_LG
    };
    icon(glyph, size).color(color)
}

const ICON_ARROW_RIGHT_SM: Icon = Icon {
    name: "kl-arrow-right-165",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.3" stroke-linecap="round" stroke-linejoin="round"><path d="M5 12H18.0587M12.1229 5L18.8261 11.5875C19.058 11.8153 19.058 12.1847 18.8261 12.4125L12.1229 19"/></g>"#,
};
const ICON_ARROW_RIGHT_LG: Icon = Icon {
    name: "kl-arrow-right-185",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M5 12H18.0587M12.1229 5L18.8261 11.5875C19.058 11.8153 19.058 12.1847 18.8261 12.4125L12.1229 19"/></g>"#,
};

pub fn icon_arrow_right(size: f32, color: Color) -> El {
    let glyph = if size < 24.0 {
        &ICON_ARROW_RIGHT_SM
    } else {
        &ICON_ARROW_RIGHT_LG
    };
    icon(glyph, size).color(color)
}

const ICON_ARROW_UP_SM: Icon = Icon {
    name: "kl-arrow-up-165",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.3" stroke-linecap="round" stroke-linejoin="round"><path d="M5 11.8771L11.5875 5.17385C11.8153 4.94205 12.1847 4.94205 12.4125 5.17385L19 11.8771M12 19V5.94129"/></g>"#,
};
const ICON_ARROW_UP_LG: Icon = Icon {
    name: "kl-arrow-up-185",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M5 11.8771L11.5875 5.17385C11.8153 4.94205 12.1847 4.94205 12.4125 5.17385L19 11.8771M12 19V5.94129"/></g>"#,
};

pub fn icon_arrow_up(size: f32, color: Color) -> El {
    let glyph = if size < 24.0 {
        &ICON_ARROW_UP_SM
    } else {
        &ICON_ARROW_UP_LG
    };
    icon(glyph, size).color(color)
}

const ICON_ROTATE_CCW_SM: Icon = Icon {
    name: "kl-rotate-ccw-165",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.3" stroke-linecap="round" stroke-linejoin="round"><path d="M3.2117 10.0593C3.071 10.6966 3 11.3473 3 12C3 16.9706 7.0294 21 12 21C16.9706 21 21 16.9706 21 12C21 7.0294 16.9706 3 12 3C9.5169 3 7.1441 4.0259 5.4432 5.8349M4.2857 3L4.7293 6.105C4.7683 6.3784 5.0216 6.5683 5.295 6.5293L8.4 6.0857"/></g>"#,
};
const ICON_ROTATE_CCW_LG: Icon = Icon {
    name: "kl-rotate-ccw-185",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M3.2117 10.0593C3.071 10.6966 3 11.3473 3 12C3 16.9706 7.0294 21 12 21C16.9706 21 21 16.9706 21 12C21 7.0294 16.9706 3 12 3C9.5169 3 7.1441 4.0259 5.4432 5.8349M4.2857 3L4.7293 6.105C4.7683 6.3784 5.0216 6.5683 5.295 6.5293L8.4 6.0857"/></g>"#,
};

pub fn icon_rotate_ccw(size: f32, color: Color) -> El {
    let glyph = if size < 24.0 {
        &ICON_ROTATE_CCW_SM
    } else {
        &ICON_ROTATE_CCW_LG
    };
    icon(glyph, size).color(color)
}

const ICON_BOOKMARK_FILLED_SOLID: Icon = Icon {
    name: "kl-bookmark-solid",
    view: 24.0,
    body: r#"<g fill="currentColor" stroke="none"><path d="M3 5C3 2.7909 4.7909 1 7 1L17 1C19.2091 1 21 2.7909 21 5L21 20.9983C21 22.5807 19.2495 23.532 17.9185 22.6807L12.2704 19.6926C12.1057 19.5867 11.8943 19.5867 11.7296 19.6926L6.0815 22.6807C4.7505 23.532 3 22.5807 3 20.9983Z" fill="currentColor"/></g>"#,
};

pub fn icon_bookmark_filled(size: f32, color: Color) -> El {
    icon(&ICON_BOOKMARK_FILLED_SOLID, size).color(color)
}

const ICON_LIST_MUSIC_SM: Icon = Icon {
    name: "kl-list-music-165",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.3" stroke-linecap="round" stroke-linejoin="round"><path d="M22 15C22 16.6569 20.6569 18 19 18C17.3431 18 16 16.6569 16 15C16 13.3431 17.3431 12 19 12C20.6569 12 22 13.3431 22 15ZM2 6H16M2 12H10M2 18H10M22 15V6"/></g>"#,
};
const ICON_LIST_MUSIC_LG: Icon = Icon {
    name: "kl-list-music-185",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M22 15C22 16.6569 20.6569 18 19 18C17.3431 18 16 16.6569 16 15C16 13.3431 17.3431 12 19 12C20.6569 12 22 13.3431 22 15ZM2 6H16M2 12H10M2 18H10M22 15V6"/></g>"#,
};

pub fn icon_list_music(size: f32, color: Color) -> El {
    let glyph = if size < 24.0 {
        &ICON_LIST_MUSIC_SM
    } else {
        &ICON_LIST_MUSIC_LG
    };
    icon(glyph, size).color(color)
}

const ICON_RADIO_SM: Icon = Icon {
    name: "kl-radio-165",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.3" stroke-linecap="round" stroke-linejoin="round"><path d="M5 8L19 8C20.6569 8 22 9.3431 22 11L22 17C22 18.6569 20.6569 20 19 20L5 20C3.3431 20 2 18.6569 2 17L2 11C2 9.3431 3.3431 8 5 8ZM6 8L16 4M10 14C10 15.1046 9.1046 16 8 16C6.8954 16 6 15.1046 6 14C6 12.8954 6.8954 12 8 12C9.1046 12 10 12.8954 10 14ZM14 12L18 12M14 16L18 16"/></g>"#,
};
const ICON_RADIO_LG: Icon = Icon {
    name: "kl-radio-185",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M5 8L19 8C20.6569 8 22 9.3431 22 11L22 17C22 18.6569 20.6569 20 19 20L5 20C3.3431 20 2 18.6569 2 17L2 11C2 9.3431 3.3431 8 5 8ZM6 8L16 4M10 14C10 15.1046 9.1046 16 8 16C6.8954 16 6 15.1046 6 14C6 12.8954 6.8954 12 8 12C9.1046 12 10 12.8954 10 14ZM14 12L18 12M14 16L18 16"/></g>"#,
};

pub fn icon_radio(size: f32, color: Color) -> El {
    let glyph = if size < 24.0 {
        &ICON_RADIO_SM
    } else {
        &ICON_RADIO_LG
    };
    icon(glyph, size).color(color)
}

const ICON_COMPASS_SM: Icon = Icon {
    name: "kl-compass-165",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.3" stroke-linecap="round" stroke-linejoin="round"><path d="M22 12C22 17.5228 17.5228 22 12 22C6.4772 22 2 17.5228 2 12C2 6.4772 6.4772 2 12 2C17.5228 2 22 6.4772 22 12ZM15.102633 7.632456C15.884391 7.37187 16.62813 8.115609 16.367544 8.897367L14.816228 13.551317C14.617158 14.148527 14.148527 14.617158 13.551317 14.816228L8.897367 16.367544C8.115609 16.62813 7.37187 15.884391 7.632456 15.102633L9.183772 10.448683C9.382842 9.851473 9.851473 9.382842 10.448683 9.183772Z"/></g>"#,
};
const ICON_COMPASS_LG: Icon = Icon {
    name: "kl-compass-185",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M22 12C22 17.5228 17.5228 22 12 22C6.4772 22 2 17.5228 2 12C2 6.4772 6.4772 2 12 2C17.5228 2 22 6.4772 22 12ZM15.102633 7.632456C15.884391 7.37187 16.62813 8.115609 16.367544 8.897367L14.816228 13.551317C14.617158 14.148527 14.148527 14.617158 13.551317 14.816228L8.897367 16.367544C8.115609 16.62813 7.37187 15.884391 7.632456 15.102633L9.183772 10.448683C9.382842 9.851473 9.851473 9.382842 10.448683 9.183772Z"/></g>"#,
};

pub fn icon_compass(size: f32, color: Color) -> El {
    let glyph = if size < 24.0 {
        &ICON_COMPASS_SM
    } else {
        &ICON_COMPASS_LG
    };
    icon(glyph, size).color(color)
}

const ICON_SHUFFLE_SM: Icon = Icon {
    name: "kl-shuffle-165",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.3" stroke-linecap="round" stroke-linejoin="round"><path d="M3 6H6C7.5 6 8.9 6.8 9.6 8.1L10.2387 9.1379M13.7613 14.8621L14.4 15.9C15.1 17.2 16.5 18 18 18H21M3 18H6C7.5 18 8.9 17.2 9.6 15.9L14.4 8.1C15.1 6.8 16.5 6 18 6H21M18 3L21 6L18 9M18 15L21 18L18 21"/></g>"#,
};
const ICON_SHUFFLE_LG: Icon = Icon {
    name: "kl-shuffle-185",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M3 6H6C7.5 6 8.9 6.8 9.6 8.1L10.2387 9.1379M13.7613 14.8621L14.4 15.9C15.1 17.2 16.5 18 18 18H21M3 18H6C7.5 18 8.9 17.2 9.6 15.9L14.4 8.1C15.1 6.8 16.5 6 18 6H21M18 3L21 6L18 9M18 15L21 18L18 21"/></g>"#,
};

pub fn icon_shuffle(size: f32, color: Color) -> El {
    let glyph = if size < 24.0 {
        &ICON_SHUFFLE_SM
    } else {
        &ICON_SHUFFLE_LG
    };
    icon(glyph, size).color(color)
}

const ICON_REPEAT_SM: Icon = Icon {
    name: "kl-repeat-165",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.3" stroke-linecap="round" stroke-linejoin="round"><path d="M3 12V9C3 7.34315 4.34315 6 6 6H21M18 3L21 6L18 9M21 12V15C21 16.65685 19.65685 18 18 18H3M6 21L3 18L6 15"/></g>"#,
};
const ICON_REPEAT_LG: Icon = Icon {
    name: "kl-repeat-185",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M3 12V9C3 7.34315 4.34315 6 6 6H21M18 3L21 6L18 9M21 12V15C21 16.65685 19.65685 18 18 18H3M6 21L3 18L6 15"/></g>"#,
};

pub fn icon_repeat(size: f32, color: Color) -> El {
    let glyph = if size < 24.0 {
        &ICON_REPEAT_SM
    } else {
        &ICON_REPEAT_LG
    };
    icon(glyph, size).color(color)
}

const ICON_DISC_SM: Icon = Icon {
    name: "kl-disc-165",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.3" stroke-linecap="round" stroke-linejoin="round"><path d="M14.0521 6.3618C15.3777 6.8443 16.4908 7.7784 17.1962 9M22 12C22 17.5228 17.5228 22 12 22C6.4772 22 2 17.5228 2 12C2 6.4772 6.4772 2 12 2C17.5228 2 22 6.4772 22 12ZM14 12C14 13.1046 13.1046 14 12 14C10.8954 14 10 13.1046 10 12C10 10.8954 10.8954 10 12 10C13.1046 10 14 10.8954 14 12Z" fill="none"/></g>"#,
};
const ICON_DISC_LG: Icon = Icon {
    name: "kl-disc-185",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M14.0521 6.3618C15.3777 6.8443 16.4908 7.7784 17.1962 9M22 12C22 17.5228 17.5228 22 12 22C6.4772 22 2 17.5228 2 12C2 6.4772 6.4772 2 12 2C17.5228 2 22 6.4772 22 12ZM14 12C14 13.1046 13.1046 14 12 14C10.8954 14 10 13.1046 10 12C10 10.8954 10.8954 10 12 10C13.1046 10 14 10.8954 14 12Z" fill="none"/></g>"#,
};

pub fn icon_disc(size: f32, color: Color) -> El {
    let glyph = if size < 24.0 {
        &ICON_DISC_SM
    } else {
        &ICON_DISC_LG
    };
    icon(glyph, size).color(color)
}

const ICON_QUEUE_SM: Icon = Icon {
    name: "kl-list-ordered-165",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.3" stroke-linecap="round" stroke-linejoin="round"><path d="M3.5 4.5L5 3L5 8M3 8H7M3 16.3C3 15.5 3.84 15 4.89 15C6.05 15 7 15.7 7 16.7C7 18.6 3 19.9 3 21H7M11 5H21M11 12H21M11 19H21" fill="none"/></g>"#,
};
const ICON_QUEUE_LG: Icon = Icon {
    name: "kl-list-ordered-185",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M3.5 4.5L5 3L5 8M3 8H7M3 16.3C3 15.5 3.84 15 4.89 15C6.05 15 7 15.7 7 16.7C7 18.6 3 19.9 3 21H7M11 5H21M11 12H21M11 19H21" fill="none"/></g>"#,
};

pub fn icon_queue(size: f32, color: Color) -> El {
    let glyph = if size < 24.0 {
        &ICON_QUEUE_SM
    } else {
        &ICON_QUEUE_LG
    };
    icon(glyph, size).color(color)
}

const ICON_HEART_SOLID: Icon = Icon {
    name: "kl-heart-solid",
    view: 24.0,
    body: r#"<g fill="currentColor" stroke="none"><path d="M16 2C19.8256 2 23 4.9823 23 8.75C23 11.6443 21.418 13.9747 19.4219 15.9922C17.4582 17.9769 14.8378 19.8917 12.6494 21.7607C12.4624 21.9204 12.2312 22 12 22C11.9422 22 11.8843 21.9953 11.8271 21.9854C11.6558 21.9554 11.4908 21.8804 11.3506 21.7607C9.1622 19.8917 6.5418 17.9769 4.5781 15.9922C2.582 13.9747 1 11.6443 1 8.75C1 4.9823 4.1744 2 8 2C9.4851 2 10.828 2.4608 12 3.2539C13.172 2.4608 14.5149 2 16 2Z" fill="currentColor"/></g>"#,
};

pub fn icon_heart(size: f32, color: Color) -> El {
    icon(&ICON_HEART_SOLID, size).color(color)
}

const ICON_GALLERY_SM: Icon = Icon {
    name: "kl-images-165",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.3" stroke-linecap="round" stroke-linejoin="round"><path d="M3 8V18C3 19.66 4.3431 21 6 21H16M21 14.14L18.6044 11.93C18.1939 11.55 17.5439 11.51 17.0794 11.83L15.5873 12.86C15.1228 13.18 14.4728 13.14 14.0623 12.76L12.4917 11.31C12.036 10.9 11.2973 10.9 10.8417 11.31L7.5 14.39M10 3H18C19.6569 3 21 4.34 21 6V14C21 15.66 19.6569 17 18 17H10C8.3431 17 7 15.66 7 14V6C7 4.34 8.3431 3 10 3Z"/><path d="M12 7C12 7.55 11.5523 8 11 8C10.4477 8 10 7.55 10 7C10 6.45 10.4477 6 11 6C11.5523 6 12 6.45 12 7Z" fill="currentColor" fill-rule="evenodd" clip-rule="evenodd" stroke="none"/></g>"#,
};
const ICON_GALLERY_LG: Icon = Icon {
    name: "kl-images-185",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M3 8V18C3 19.66 4.3431 21 6 21H16M21 14.14L18.6044 11.93C18.1939 11.55 17.5439 11.51 17.0794 11.83L15.5873 12.86C15.1228 13.18 14.4728 13.14 14.0623 12.76L12.4917 11.31C12.036 10.9 11.2973 10.9 10.8417 11.31L7.5 14.39M10 3H18C19.6569 3 21 4.34 21 6V14C21 15.66 19.6569 17 18 17H10C8.3431 17 7 15.66 7 14V6C7 4.34 8.3431 3 10 3Z"/><path d="M12 7C12 7.55 11.5523 8 11 8C10.4477 8 10 7.55 10 7C10 6.45 10.4477 6 11 6C11.5523 6 12 6.45 12 7Z" fill="currentColor" fill-rule="evenodd" clip-rule="evenodd" stroke="none"/></g>"#,
};

pub fn icon_gallery(size: f32, color: Color) -> El {
    let glyph = if size < 24.0 {
        &ICON_GALLERY_SM
    } else {
        &ICON_GALLERY_LG
    };
    icon(glyph, size).color(color)
}

const ICON_FOUR_POINT_SPARKLE_SM: Icon = Icon {
    name: "kl-sparkle-165",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.3" stroke-linecap="round" stroke-linejoin="round"><path d="M12.4651 3.3166L14.5506 8.6043C14.7031 8.9909 15.0091 9.2969 15.3957 9.4494L20.6834 11.5349C20.8745 11.6102 21 11.7947 21 12C21 12.2053 20.8745 12.3898 20.6834 12.4651L15.3957 14.5506C15.0091 14.7031 14.7031 15.0091 14.5506 15.3957L12.4651 20.6834C12.3898 20.8745 12.2053 21 12 21C11.7947 21 11.6102 20.8745 11.5349 20.6834L9.4494 15.3957C9.2969 15.0091 8.9909 14.7031 8.6043 14.5506L3.3166 12.4651C3.1255 12.3898 3 12.2053 3 12C3 11.7947 3.1255 11.6102 3.3166 11.5349L8.6043 9.4494C8.9909 9.2969 9.2969 8.9909 9.4494 8.6043L11.5349 3.3166C11.6102 3.1255 11.7947 3 12 3C12.2053 3 12.3898 3.1255 12.4651 3.3166Z" fill="none"/></g>"#,
};
const ICON_FOUR_POINT_SPARKLE_LG: Icon = Icon {
    name: "kl-sparkle-185",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M12.4651 3.3166L14.5506 8.6043C14.7031 8.9909 15.0091 9.2969 15.3957 9.4494L20.6834 11.5349C20.8745 11.6102 21 11.7947 21 12C21 12.2053 20.8745 12.3898 20.6834 12.4651L15.3957 14.5506C15.0091 14.7031 14.7031 15.0091 14.5506 15.3957L12.4651 20.6834C12.3898 20.8745 12.2053 21 12 21C11.7947 21 11.6102 20.8745 11.5349 20.6834L9.4494 15.3957C9.2969 15.0091 8.9909 14.7031 8.6043 14.5506L3.3166 12.4651C3.1255 12.3898 3 12.2053 3 12C3 11.7947 3.1255 11.6102 3.3166 11.5349L8.6043 9.4494C8.9909 9.2969 9.2969 8.9909 9.4494 8.6043L11.5349 3.3166C11.6102 3.1255 11.7947 3 12 3C12.2053 3 12.3898 3.1255 12.4651 3.3166Z" fill="none"/></g>"#,
};

pub fn icon_four_point_sparkle(size: f32, color: Color) -> El {
    let glyph = if size < 24.0 {
        &ICON_FOUR_POINT_SPARKLE_SM
    } else {
        &ICON_FOUR_POINT_SPARKLE_LG
    };
    icon(glyph, size).color(color)
}

const ICON_LIBRARY_FILLED_SOLID: Icon = Icon {
    name: "kl-library-big-solid",
    view: 24.0,
    body: r#"<g fill="currentColor" stroke="none"><path d="M3.5 5C3.5 4.17 4.17 3.5 5 3.5C5.83 3.5 6.5 4.17 6.5 5V19C6.5 19.83 5.83 20.5 5 20.5C4.17 20.5 3.5 19.83 3.5 19ZM9 5C9 4.17 9.67 3.5 10.5 3.5C11.33 3.5 12 4.17 12 5V19C12 19.83 11.33 20.5 10.5 20.5C9.67 20.5 9 19.83 9 19ZM14.6 5.4C14.33 4.62 14.74 3.77 15.52 3.5C16.3 3.23 17.15 3.64 17.42 4.42L21.4 18.6C21.67 19.38 21.26 20.23 20.48 20.5C19.7 20.77 18.85 20.36 18.58 19.58Z"/></g>"#,
};

pub fn icon_library_filled(size: f32, color: Color) -> El {
    icon(&ICON_LIBRARY_FILLED_SOLID, size).color(color)
}

const ICON_STORE_FILLED_SOLID: Icon = Icon {
    name: "kl-store-solid",
    view: 24.0,
    body: r#"<g fill="currentColor" stroke="none"><path d="M5.5 8.5H18.5C19.6046 8.5 20.5 9.3954 20.5 10.5L19.7 18.2C19.55 19.8 18.2 21 16.6 21H7.4C5.8 21 4.45 19.8 4.3 18.2L3.5 10.5C3.5 9.3954 4.3954 8.5 5.5 8.5Z"/><path d="M8.5 8.5V6.5C8.5 4.567 10.067 3 12 3C13.933 3 15.5 4.567 15.5 6.5V8.5" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round"/></g>"#,
};

pub fn icon_store_filled(size: f32, color: Color) -> El {
    icon(&ICON_STORE_FILLED_SOLID, size).color(color)
}

const ICON_CHAT_FILLED_SOLID: Icon = Icon {
    name: "kl-message-lines-solid",
    view: 24.0,
    body: r#"<g fill="currentColor" stroke="none"><path d="M5.2 16.5C3.8 15.2 3 13.4 3 11.5C3 7.3579 7.0294 4 12 4C16.9706 4 21 7.3579 21 11.5C21 15.6421 16.9706 19 12 19C10.9 19 9.85 18.83 8.88 18.52L4.8 20.2L5.2 16.5Z"/></g>"#,
};

pub fn icon_chat_filled(size: f32, color: Color) -> El {
    icon(&ICON_CHAT_FILLED_SOLID, size).color(color)
}

const ICON_MUSIC_FILLED_SOLID: Icon = Icon {
    name: "kl-music-note-solid",
    view: 24.0,
    body: r#"<g fill="currentColor" stroke="none"><path d="M10 18C10 20.2091 8.2091 22 6 22C3.7909 22 2 20.2091 2 18C2 15.7909 3.7909 14 6 14C8.2091 14 10 15.7909 10 18ZM22 16C22 18.2091 20.2091 20 18 20C15.7909 20 14 18.2091 14 16C14 13.7909 15.7909 12 18 12C20.2091 12 22 13.7909 22 16Z"/><path d="M9 18V5L21 3V16" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"/></g>"#,
};

pub fn icon_music_filled(size: f32, color: Color) -> El {
    icon(&ICON_MUSIC_FILLED_SOLID, size).color(color)
}

const ICON_MOUSE_FILLED_SOLID: Icon = Icon {
    name: "kl-mouse-solid",
    view: 24.0,
    body: r#"<g fill="currentColor" stroke="none"><path d="M12 2C15.866 2 19 5.134 19 9L19 15C19 18.866 15.866 22 12 22C8.134 22 5 18.866 5 15L5 9C5 5.134 8.134 2 12 2ZM13 7C13 6.4477 12.5523 6 12 6C11.4477 6 11 6.4477 11 7L11 8C11 8.5523 11.4477 9 12 9C12.5523 9 13 8.5523 13 8L13 7Z" fill="currentColor" fill-rule="evenodd" clip-rule="evenodd"/></g>"#,
};

pub fn icon_mouse_filled(size: f32, color: Color) -> El {
    icon(&ICON_MOUSE_FILLED_SOLID, size).color(color)
}

const ICON_SPARKLES_FILLED_SOLID: Icon = Icon {
    name: "kl-sparkles-solid",
    view: 24.0,
    body: r#"<g fill="currentColor" stroke="none"><path d="M15.4651 9.1794L16.7409 12.4141C16.8933 12.8006 17.1994 13.1067 17.5859 13.2591L20.8206 14.5349C21.0117 14.6102 21.1372 14.7947 21.1372 15C21.1372 15.2053 21.0117 15.3898 20.8206 15.4651L17.5859 16.7409C17.1994 16.8933 16.8933 17.1994 16.7409 17.5859L15.4651 20.8206C15.3898 21.0117 15.2053 21.1372 15 21.1372C14.7947 21.1372 14.6102 21.0117 14.5349 20.8206L13.2591 17.5859C13.1067 17.1994 12.8006 16.8933 12.4141 16.7409L9.1794 15.4651C8.9883 15.3898 8.8628 15.2053 8.8628 15C8.8628 14.7947 8.9883 14.6102 9.1794 14.5349L12.4141 13.2591C12.8006 13.1067 13.1067 12.8006 13.2591 12.4141L14.5349 9.1794C14.6102 8.9883 14.7947 8.8628 15 8.8628C15.2053 8.8628 15.3898 8.9883 15.4651 9.1794ZM6.7151 3.1794L7.2131 4.4419C7.3655 4.8285 7.6715 5.1345 8.0581 5.2869L9.3206 5.7849C9.5117 5.8602 9.6372 6.0447 9.6372 6.25C9.6372 6.4553 9.5117 6.6398 9.3206 6.7151L8.0581 7.2131C7.6715 7.3655 7.3655 7.6715 7.2131 8.0581L6.7151 9.3206C6.6398 9.5117 6.4553 9.6372 6.25 9.6372C6.0447 9.6372 5.8602 9.5117 5.7849 9.3206L5.2869 8.0581C5.1345 7.6715 4.8285 7.3655 4.4419 7.2131L3.1794 6.7151C2.9883 6.6398 2.8628 6.4553 2.8628 6.25C2.8628 6.0447 2.9883 5.8602 3.1794 5.7849L4.4419 5.2869C4.8285 5.1345 5.1345 4.8285 5.2869 4.4419L5.7849 3.1794C5.8602 2.9883 6.0447 2.8628 6.25 2.8628C6.4553 2.8628 6.6398 2.9883 6.7151 3.1794Z"/></g>"#,
};

pub fn icon_sparkles_filled(size: f32, color: Color) -> El {
    icon(&ICON_SPARKLES_FILLED_SOLID, size).color(color)
}

const ICON_GRID_FILLED_SOLID: Icon = Icon {
    name: "kl-grid-2x2-solid",
    view: 24.0,
    body: r#"<g fill="currentColor" stroke="none"><path d="M5.5 3.5H8.5C9.6046 3.5 10.5 4.3954 10.5 5.5V8.5C10.5 9.6046 9.6046 10.5 8.5 10.5H5.5C4.3954 10.5 3.5 9.6046 3.5 8.5V5.5C3.5 4.3954 4.3954 3.5 5.5 3.5ZM15.5 3.5H18.5C19.6046 3.5 20.5 4.3954 20.5 5.5V8.5C20.5 9.6046 19.6046 10.5 18.5 10.5H15.5C14.3954 10.5 13.5 9.6046 13.5 8.5V5.5C13.5 4.3954 14.3954 3.5 15.5 3.5ZM5.5 13.5H8.5C9.6046 13.5 10.5 14.3954 10.5 15.5V18.5C10.5 19.6046 9.6046 20.5 8.5 20.5H5.5C4.3954 20.5 3.5 19.6046 3.5 18.5V15.5C3.5 14.3954 4.3954 13.5 5.5 13.5ZM15.5 13.5H18.5C19.6046 13.5 20.5 14.3954 20.5 15.5V18.5C20.5 19.6046 19.6046 20.5 18.5 20.5H15.5C14.3954 20.5 13.5 19.6046 13.5 18.5V15.5C13.5 14.3954 14.3954 13.5 15.5 13.5Z"/></g>"#,
};

pub fn icon_grid_filled(size: f32, color: Color) -> El {
    icon(&ICON_GRID_FILLED_SOLID, size).color(color)
}

const ICON_SETTINGS_FILLED_SOLID: Icon = Icon {
    name: "kl-settings-solid",
    view: 24.0,
    body: r#"<g fill="currentColor" stroke="none"><path d="M12.6613 1C13.9227 1 14.9584 1.96 15.1096 3.19L15.3439 5.12C15.3448 5.12 15.3463 5.13 15.3478 5.14L15.3713 5.24C15.3734 5.25 15.3757 5.26 15.3791 5.26C15.4196 5.37 15.4927 5.45 15.5822 5.51L15.5851 5.51L15.7375 5.6C15.7434 5.6 15.7498 5.61 15.756 5.61L15.8107 5.63C15.8178 5.64 15.8258 5.64 15.8332 5.64C15.933 5.67 16.0406 5.67 16.1359 5.63L17.9035 4.86C19.0639 4.36 20.3958 4.83 21.012 5.92L21.673 7.09C22.245 8.11 22.046 9.39 21.205 10.17L21.022 10.33L19.5099 11.5L19.4982 11.5C19.4884 11.51 19.4807 11.52 19.4767 11.53L19.4201 11.58C19.3492 11.67 19.3058 11.88 19.3058 12C19.3058 12.17 19.3836 12.4 19.5002 12.49L21.031 13.68L21.206 13.83L21.351 13.98L21.487 14.14C22.084 14.93 22.175 16.02 21.673 16.91L21.012 18.08C20.3958 19.17 19.0648 19.64 17.9045 19.14L16.1354 18.37C16.0067 18.32 15.8589 18.33 15.7365 18.4L15.5929 18.48C15.5908 18.48 15.5883 18.49 15.5861 18.49L15.5754 18.49C15.4547 18.57 15.3663 18.7 15.3469 18.86L15.1096 20.81C14.9584 22.04 13.9227 23 12.6613 23H11.339C10.0773 23 9.0401 22.04 8.8888 20.81L8.6525 18.86L8.6496 18.85L8.6437 18.82L8.6359 18.79L8.6115 18.72C8.6089 18.71 8.6062 18.7 8.6027 18.7C8.5623 18.61 8.5 18.54 8.424 18.5C8.4212 18.49 8.4183 18.49 8.4152 18.49L8.4084 18.48L8.2619 18.4C8.1703 18.35 8.0056 18.34 7.9069 18.35L6.0959 19.14C5.14 19.55 3.8883 19.12 3.1958 18.41L2.9821 18.07L2.3254 16.91C1.7152 15.82 1.9819 14.44 2.9689 13.68L4.4992 12.49C4.5053 12.49 4.5113 12.48 4.5168 12.48L4.5637 12.43C4.5693 12.43 4.5753 12.42 4.5803 12.42C4.6503 12.33 4.6935 12.12 4.6935 12C4.6935 11.88 4.6504 11.67 4.5803 11.58L4.5637 11.57L4.5168 11.52C4.5113 11.52 4.5053 11.51 4.4992 11.51L2.9689 10.32C1.9819 9.56 1.7152 8.18 2.3254 7.09L2.9819 5.93L3.1115 5.73C3.7711 4.78 5.0111 4.4 6.0959 4.86L7.8566 5.62C7.864 5.63 7.8723 5.63 7.8801 5.63L7.9435 5.65C7.9513 5.65 7.96 5.65 7.9679 5.66C8.0671 5.67 8.2468 5.61 8.3378 5.56C8.462 5.48 8.6315 5.3 8.6515 5.14L8.8888 3.19C9.0401 1.96 10.0773 1 11.339 1H12.6613ZM12.09 8.5C10.1574 8.5 8.4992 10.07 8.4992 12C8.4992 13.93 10.1574 15.5 12.09 15.5C13.9389 15.4 15.5002 13.87 15.5002 12C15.5002 10.13 13.9389 8.6 12.09 8.5Z" fill="currentColor" fill-rule="evenodd" clip-rule="evenodd"/></g>"#,
};

pub fn icon_settings_filled(size: f32, color: Color) -> El {
    icon(&ICON_SETTINGS_FILLED_SOLID, size).color(color)
}

const ICON_STAR_FILLED_SOLID: Icon = Icon {
    name: "kl-star-solid",
    view: 24.0,
    body: r#"<g fill="currentColor" stroke="none"><path d="M10.6159 2.4219C10.8491 1.8636 11.3949 1.5 12 1.5C12.6051 1.5 13.1509 1.8636 13.3841 2.4219L15.6752 7.9076L21.6194 8.3822C22.224 8.4305 22.74 8.8381 22.927 9.4151C23.1139 9.9921 22.9349 10.625 22.4735 11.0186L17.9492 14.8782L19.3318 20.6505C19.4729 21.2395 19.2459 21.8556 18.7564 22.2122C18.267 22.5689 17.611 22.5962 17.0936 22.2815L12 19.1832L6.9064 22.2815C6.389 22.5962 5.733 22.5689 5.2436 22.2122C4.7541 21.8556 4.5271 21.2395 4.6682 20.6505L6.0508 14.8782L1.5265 11.0186C1.0651 10.625 0.8861 9.9921 1.073 9.4151C1.26 8.8381 1.776 8.4305 2.3806 8.3822L8.3248 7.9076Z" fill="currentColor"/></g>"#,
};

pub fn icon_star_filled(size: f32, color: Color) -> El {
    icon(&ICON_STAR_FILLED_SOLID, size).color(color)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn all_keyline_icons_rasterize_nonzero_coverage() {
        let all: &[&Icon] = &[
            &ICON_MICROPHONE_SM,
            &ICON_MICROPHONE_LG,
            &ICON_GRID_SM,
            &ICON_GRID_LG,
            &ICON_SEARCH_SM,
            &ICON_SEARCH_LG,
            &ICON_PLAY_SOLID,
            &ICON_STOP_SOLID,
            &ICON_REFRESH_SM,
            &ICON_REFRESH_LG,
            &ICON_STORE_SM,
            &ICON_STORE_LG,
            &ICON_CHAT_SM,
            &ICON_CHAT_LG,
            &ICON_MUSIC_SM,
            &ICON_MUSIC_LG,
            &ICON_CPU_SM,
            &ICON_CPU_LG,
            &ICON_SETTINGS_SM,
            &ICON_SETTINGS_LG,
            &ICON_CLOSE_SM,
            &ICON_CLOSE_LG,
            &ICON_BACK_SM,
            &ICON_BACK_LG,
            &ICON_FOLDER_SM,
            &ICON_FOLDER_LG,
            &ICON_KEY_SM,
            &ICON_KEY_LG,
            &ICON_DOWNLOAD_SM,
            &ICON_DOWNLOAD_LG,
            &ICON_WRENCH_SM,
            &ICON_WRENCH_LG,
            &ICON_TRASH_SM,
            &ICON_TRASH_LG,
            &ICON_SLIDERS_SM,
            &ICON_SLIDERS_LG,
            &ICON_EXTERNAL_SM,
            &ICON_EXTERNAL_LG,
            &ICON_SPARKLES_SM,
            &ICON_SPARKLES_LG,
            &ICON_GAMEPAD_SM,
            &ICON_GAMEPAD_LG,
            &ICON_TROPHY_SM,
            &ICON_TROPHY_LG,
            &ICON_SHIELD_SM,
            &ICON_SHIELD_LG,
            &ICON_HARD_DRIVE_SM,
            &ICON_HARD_DRIVE_LG,
            &ICON_KEYBOARD_SM,
            &ICON_KEYBOARD_LG,
            &ICON_VOLUME_SM,
            &ICON_VOLUME_LG,
            &ICON_VOLUME_OFF_SM,
            &ICON_VOLUME_OFF_LG,
            &ICON_ZAP_SM,
            &ICON_ZAP_LG,
            &ICON_MONITOR_SM,
            &ICON_MONITOR_LG,
            &ICON_CHECK_SM,
            &ICON_CHECK_LG,
            &ICON_USER_SM,
            &ICON_USER_LG,
            &ICON_APP_WINDOW_SM,
            &ICON_APP_WINDOW_LG,
            &ICON_PAUSE_SM,
            &ICON_PAUSE_LG,
            &ICON_SKIP_BACK_SM,
            &ICON_SKIP_BACK_LG,
            &ICON_SKIP_FORWARD_SM,
            &ICON_SKIP_FORWARD_LG,
            &ICON_FILM_SM,
            &ICON_FILM_LG,
            &ICON_CAMERA_SM,
            &ICON_CAMERA_LG,
            &ICON_STAR_SM,
            &ICON_STAR_LG,
            &ICON_SCISSORS_SM,
            &ICON_SCISSORS_LG,
            &ICON_HASH_SM,
            &ICON_HASH_LG,
            &ICON_MIC_SM,
            &ICON_MIC_LG,
            &ICON_MIC_OFF_SM,
            &ICON_MIC_OFF_LG,
            &ICON_HEADPHONES_SM,
            &ICON_HEADPHONES_LG,
            &ICON_HEADPHONES_OFF_SM,
            &ICON_HEADPHONES_OFF_LG,
            &ICON_USERS_SM,
            &ICON_USERS_LG,
            &ICON_USER_PLUS_SM,
            &ICON_USER_PLUS_LG,
            &ICON_PHONE_SM,
            &ICON_PHONE_LG,
            &ICON_PHONE_OFF_SM,
            &ICON_PHONE_OFF_LG,
            &ICON_LOCK_SM,
            &ICON_LOCK_LG,
            &ICON_HOME_SM,
            &ICON_HOME_LG,
            &ICON_SERVER_SM,
            &ICON_SERVER_LG,
            &ICON_BELL_OFF_SM,
            &ICON_BELL_OFF_LG,
            &ICON_CHEVRON_DOWN_SM,
            &ICON_CHEVRON_DOWN_LG,
            &ICON_CHEVRON_RIGHT_SM,
            &ICON_CHEVRON_RIGHT_LG,
            &ICON_SEND_SM,
            &ICON_SEND_LG,
            &ICON_REPLY_SM,
            &ICON_REPLY_LG,
            &ICON_PIN_SM,
            &ICON_PIN_LG,
            &ICON_SMILE_SM,
            &ICON_SMILE_LG,
            &ICON_PAPERCLIP_SM,
            &ICON_PAPERCLIP_LG,
            &ICON_EDIT_SM,
            &ICON_EDIT_LG,
            &ICON_PLUS_SM,
            &ICON_PLUS_LG,
            &ICON_SCREEN_SHARE_SM,
            &ICON_SCREEN_SHARE_LG,
            &ICON_VIDEO_SM,
            &ICON_VIDEO_LG,
            &ICON_HAND_SM,
            &ICON_HAND_LG,
            &ICON_MAXIMIZE_SM,
            &ICON_MAXIMIZE_LG,
            &ICON_LIBRARY_SM,
            &ICON_LIBRARY_LG,
            &ICON_MOUSE_SM,
            &ICON_MOUSE_LG,
            &ICON_MORE_HORIZ_SM,
            &ICON_MORE_HORIZ_LG,
            &ICON_FILTER_SM,
            &ICON_FILTER_LG,
            &ICON_CHEVRON_LEFT_SM,
            &ICON_CHEVRON_LEFT_LG,
            &ICON_REPLAY_SM,
            &ICON_REPLAY_LG,
            &ICON_RECORD_DOT_SOLID,
            &ICON_BOOKMARK_SM,
            &ICON_BOOKMARK_LG,
            &ICON_MINIMIZE_SM,
            &ICON_MINIMIZE_LG,
            &ICON_WIN_MAXIMIZE_SM,
            &ICON_WIN_MAXIMIZE_LG,
            &ICON_WIN_RESTORE_SM,
            &ICON_WIN_RESTORE_LG,
            &ICON_INFO_SM,
            &ICON_INFO_LG,
            &ICON_COPY_SM,
            &ICON_COPY_LG,
            &ICON_LINK_SM,
            &ICON_LINK_LG,
            &ICON_FLAG_SM,
            &ICON_FLAG_LG,
            &ICON_FORWARD_SM,
            &ICON_FORWARD_LG,
            &ICON_CHEVRON_UP_SM,
            &ICON_CHEVRON_UP_LG,
            &ICON_ALERT_SM,
            &ICON_ALERT_LG,
            &ICON_BELL_SM,
            &ICON_BELL_LG,
            &ICON_GIFT_SM,
            &ICON_GIFT_LG,
            &ICON_VOLUME_LOW_SM,
            &ICON_VOLUME_LOW_LG,
            &ICON_FOLDER_PLUS_SM,
            &ICON_FOLDER_PLUS_LG,
            &ICON_NOTES_SM,
            &ICON_NOTES_LG,
            &ICON_EYE_SM,
            &ICON_EYE_LG,
            &ICON_EYE_OFF_SM,
            &ICON_EYE_OFF_LG,
            &ICON_IMAGE_UPLOAD_SM,
            &ICON_IMAGE_UPLOAD_LG,
            &ICON_ZOOM_IN_SM,
            &ICON_ZOOM_IN_LG,
            &ICON_ZOOM_OUT_SM,
            &ICON_ZOOM_OUT_LG,
            &ICON_SELECT_MANY_SM,
            &ICON_SELECT_MANY_LG,
            &ICON_UPLOAD_SM,
            &ICON_UPLOAD_LG,
            &ICON_ARROW_RIGHT_SM,
            &ICON_ARROW_RIGHT_LG,
            &ICON_ARROW_UP_SM,
            &ICON_ARROW_UP_LG,
            &ICON_ROTATE_CCW_SM,
            &ICON_ROTATE_CCW_LG,
            &ICON_BOOKMARK_FILLED_SOLID,
            &ICON_LIST_MUSIC_SM,
            &ICON_LIST_MUSIC_LG,
            &ICON_RADIO_SM,
            &ICON_RADIO_LG,
            &ICON_COMPASS_SM,
            &ICON_COMPASS_LG,
            &ICON_SHUFFLE_SM,
            &ICON_SHUFFLE_LG,
            &ICON_REPEAT_SM,
            &ICON_REPEAT_LG,
            &ICON_DISC_SM,
            &ICON_DISC_LG,
            &ICON_QUEUE_SM,
            &ICON_QUEUE_LG,
            &ICON_HEART_SOLID,
            &ICON_GALLERY_SM,
            &ICON_GALLERY_LG,
            &ICON_FOUR_POINT_SPARKLE_SM,
            &ICON_FOUR_POINT_SPARKLE_LG,
            &ICON_LIBRARY_FILLED_SOLID,
            &ICON_STORE_FILLED_SOLID,
            &ICON_CHAT_FILLED_SOLID,
            &ICON_MUSIC_FILLED_SOLID,
            &ICON_MOUSE_FILLED_SOLID,
            &ICON_SPARKLES_FILLED_SOLID,
            &ICON_GRID_FILLED_SOLID,
            &ICON_SETTINGS_FILLED_SOLID,
            &ICON_STAR_FILLED_SOLID,
        ];
        for glyph in all {
            let cov = rune::text::icon_coverage(glyph, 24);
            let lit = cov.iter().filter(|&&a| a > 0).count();
            assert!(lit > 0, "icon {} rendered empty coverage", glyph.name);
        }
    }
}
