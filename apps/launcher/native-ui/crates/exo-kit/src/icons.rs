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

const ICON_MICROPHONE_SM: Icon = Icon {
    name: "mic-15",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M12 19v3m7-12v2a7 7 0 0 1-14 0v-2"/><rect width="6" height="13" x="9" y="2" rx="3"/></g>"#,
};
const ICON_MICROPHONE_LG: Icon = Icon {
    name: "mic-175",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.75" stroke-linecap="round" stroke-linejoin="round"><path d="M12 19v3m7-12v2a7 7 0 0 1-14 0v-2"/><rect width="6" height="13" x="9" y="2" rx="3"/></g>"#,
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
    name: "layout-grid-15",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><rect width="7" height="7" x="3" y="3" rx="1"/><rect width="7" height="7" x="14" y="3" rx="1"/><rect width="7" height="7" x="14" y="14" rx="1"/><rect width="7" height="7" x="3" y="14" rx="1"/></g>"#,
};
const ICON_GRID_LG: Icon = Icon {
    name: "layout-grid-175",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.75" stroke-linecap="round" stroke-linejoin="round"><rect width="7" height="7" x="3" y="3" rx="1"/><rect width="7" height="7" x="14" y="3" rx="1"/><rect width="7" height="7" x="14" y="14" rx="1"/><rect width="7" height="7" x="3" y="14" rx="1"/></g>"#,
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
    name: "search-15",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="m21 21l-4.34-4.34"/><circle cx="11" cy="11" r="8"/></g>"#,
};
const ICON_SEARCH_LG: Icon = Icon {
    name: "search-175",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.75" stroke-linecap="round" stroke-linejoin="round"><path d="m21 21l-4.34-4.34"/><circle cx="11" cy="11" r="8"/></g>"#,
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
    name: "play-solid",
    view: 24.0,
    body: r#"<g fill="currentColor" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M5 5a2 2 0 0 1 3.008-1.728l11.997 6.998a2 2 0 0 1 .003 3.458l-12 7A2 2 0 0 1 5 19z"/></g>"#,
};

pub fn icon_play(size: f32, color: Color) -> El {
    icon(&ICON_PLAY_SOLID, size).color(color)
}

const ICON_STOP_SOLID: Icon = Icon {
    name: "square-solid",
    view: 24.0,
    body: r#"<g fill="currentColor" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><rect width="18" height="18" x="3" y="3" rx="2"/></g>"#,
};

pub fn icon_stop(size: f32, color: Color) -> El {
    icon(&ICON_STOP_SOLID, size).color(color)
}

const ICON_REFRESH_SM: Icon = Icon {
    name: "refresh-cw-15",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M3 12a9 9 0 0 1 9-9a9.75 9.75 0 0 1 6.74 2.74L21 8"/><path d="M21 3v5h-5m5 4a9 9 0 0 1-9 9a9.75 9.75 0 0 1-6.74-2.74L3 16"/><path d="M8 16H3v5"/></g>"#,
};
const ICON_REFRESH_LG: Icon = Icon {
    name: "refresh-cw-175",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.75" stroke-linecap="round" stroke-linejoin="round"><path d="M3 12a9 9 0 0 1 9-9a9.75 9.75 0 0 1 6.74 2.74L21 8"/><path d="M21 3v5h-5m5 4a9 9 0 0 1-9 9a9.75 9.75 0 0 1-6.74-2.74L3 16"/><path d="M8 16H3v5"/></g>"#,
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
    name: "store-15",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M15 21v-5a1 1 0 0 0-1-1h-4a1 1 0 0 0-1 1v5m8.774-10.69a1.12 1.12 0 0 0-1.549 0a2.5 2.5 0 0 1-3.451 0a1.12 1.12 0 0 0-1.548 0a2.5 2.5 0 0 1-3.452 0a1.12 1.12 0 0 0-1.549 0a2.5 2.5 0 0 1-3.77-3.248l2.889-4.184A2 2 0 0 1 7 2h10a2 2 0 0 1 1.653.873l2.895 4.192a2.5 2.5 0 0 1-3.774 3.244"/><path d="M4 10.95V19a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2v-8.05"/></g>"#,
};
const ICON_STORE_LG: Icon = Icon {
    name: "store-175",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.75" stroke-linecap="round" stroke-linejoin="round"><path d="M15 21v-5a1 1 0 0 0-1-1h-4a1 1 0 0 0-1 1v5m8.774-10.69a1.12 1.12 0 0 0-1.549 0a2.5 2.5 0 0 1-3.451 0a1.12 1.12 0 0 0-1.548 0a2.5 2.5 0 0 1-3.452 0a1.12 1.12 0 0 0-1.549 0a2.5 2.5 0 0 1-3.77-3.248l2.889-4.184A2 2 0 0 1 7 2h10a2 2 0 0 1 1.653.873l2.895 4.192a2.5 2.5 0 0 1-3.774 3.244"/><path d="M4 10.95V19a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2v-8.05"/></g>"#,
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
    name: "message-circle-15",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M2.992 16.342a2 2 0 0 1 .094 1.167l-1.065 3.29a1 1 0 0 0 1.236 1.168l3.413-.998a2 2 0 0 1 1.099.092a10 10 0 1 0-4.777-4.719"/></g>"#,
};
const ICON_CHAT_LG: Icon = Icon {
    name: "message-circle-175",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.75" stroke-linecap="round" stroke-linejoin="round"><path d="M2.992 16.342a2 2 0 0 1 .094 1.167l-1.065 3.29a1 1 0 0 0 1.236 1.168l3.413-.998a2 2 0 0 1 1.099.092a10 10 0 1 0-4.777-4.719"/></g>"#,
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
    name: "music-15",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M9 18V5l12-2v13"/><circle cx="6" cy="18" r="3"/><circle cx="18" cy="16" r="3"/></g>"#,
};
const ICON_MUSIC_LG: Icon = Icon {
    name: "music-175",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.75" stroke-linecap="round" stroke-linejoin="round"><path d="M9 18V5l12-2v13"/><circle cx="6" cy="18" r="3"/><circle cx="18" cy="16" r="3"/></g>"#,
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
    name: "cpu-15",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M12 20v2m0-20v2m5 16v2m0-20v2M2 12h2m-2 5h2M2 7h2m16 5h2m-2 5h2M20 7h2M7 20v2M7 2v2"/><rect width="16" height="16" x="4" y="4" rx="2"/><rect width="8" height="8" x="8" y="8" rx="1"/></g>"#,
};
const ICON_CPU_LG: Icon = Icon {
    name: "cpu-175",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.75" stroke-linecap="round" stroke-linejoin="round"><path d="M12 20v2m0-20v2m5 16v2m0-20v2M2 12h2m-2 5h2M2 7h2m16 5h2m-2 5h2M20 7h2M7 20v2M7 2v2"/><rect width="16" height="16" x="4" y="4" rx="2"/><rect width="8" height="8" x="8" y="8" rx="1"/></g>"#,
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
    name: "settings-15",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M9.671 4.136a2.34 2.34 0 0 1 4.659 0a2.34 2.34 0 0 0 3.319 1.915a2.34 2.34 0 0 1 2.33 4.033a2.34 2.34 0 0 0 0 3.831a2.34 2.34 0 0 1-2.33 4.033a2.34 2.34 0 0 0-3.319 1.915a2.34 2.34 0 0 1-4.659 0a2.34 2.34 0 0 0-3.32-1.915a2.34 2.34 0 0 1-2.33-4.033a2.34 2.34 0 0 0 0-3.831A2.34 2.34 0 0 1 6.35 6.051a2.34 2.34 0 0 0 3.319-1.915"/><circle cx="12" cy="12" r="3"/></g>"#,
};
const ICON_SETTINGS_LG: Icon = Icon {
    name: "settings-175",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.75" stroke-linecap="round" stroke-linejoin="round"><path d="M9.671 4.136a2.34 2.34 0 0 1 4.659 0a2.34 2.34 0 0 0 3.319 1.915a2.34 2.34 0 0 1 2.33 4.033a2.34 2.34 0 0 0 0 3.831a2.34 2.34 0 0 1-2.33 4.033a2.34 2.34 0 0 0-3.319 1.915a2.34 2.34 0 0 1-4.659 0a2.34 2.34 0 0 0-3.32-1.915a2.34 2.34 0 0 1-2.33-4.033a2.34 2.34 0 0 0 0-3.831A2.34 2.34 0 0 1 6.35 6.051a2.34 2.34 0 0 0 3.319-1.915"/><circle cx="12" cy="12" r="3"/></g>"#,
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
    name: "x-15",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M18 6L6 18M6 6l12 12"/></g>"#,
};
const ICON_CLOSE_LG: Icon = Icon {
    name: "x-175",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.75" stroke-linecap="round" stroke-linejoin="round"><path d="M18 6L6 18M6 6l12 12"/></g>"#,
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
    name: "arrow-left-15",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="m12 19l-7-7l7-7m7 7H5"/></g>"#,
};
const ICON_BACK_LG: Icon = Icon {
    name: "arrow-left-175",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.75" stroke-linecap="round" stroke-linejoin="round"><path d="m12 19l-7-7l7-7m7 7H5"/></g>"#,
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
    name: "folder-15",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M20 20a2 2 0 0 0 2-2V8a2 2 0 0 0-2-2h-7.9a2 2 0 0 1-1.69-.9L9.6 3.9A2 2 0 0 0 7.93 3H4a2 2 0 0 0-2 2v13a2 2 0 0 0 2 2Z"/></g>"#,
};
const ICON_FOLDER_LG: Icon = Icon {
    name: "folder-175",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.75" stroke-linecap="round" stroke-linejoin="round"><path d="M20 20a2 2 0 0 0 2-2V8a2 2 0 0 0-2-2h-7.9a2 2 0 0 1-1.69-.9L9.6 3.9A2 2 0 0 0 7.93 3H4a2 2 0 0 0-2 2v13a2 2 0 0 0 2 2Z"/></g>"#,
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
    name: "key-15",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="m2 21l9.6-9.6m-4.1 4.1l2.3 2.3a1 1 0 0 1 0 1.4l-2.1 2.1a1 1 0 0 1-1.4 0L4 19"/><circle cx="15.5" cy="7.5" r="5.5"/></g>"#,
};
const ICON_KEY_LG: Icon = Icon {
    name: "key-175",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.75" stroke-linecap="round" stroke-linejoin="round"><path d="m2 21l9.6-9.6m-4.1 4.1l2.3 2.3a1 1 0 0 1 0 1.4l-2.1 2.1a1 1 0 0 1-1.4 0L4 19"/><circle cx="15.5" cy="7.5" r="5.5"/></g>"#,
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
    name: "download-15",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M12 15V3m9 12v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4"/><path d="m7 10l5 5l5-5"/></g>"#,
};
const ICON_DOWNLOAD_LG: Icon = Icon {
    name: "download-175",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.75" stroke-linecap="round" stroke-linejoin="round"><path d="M12 15V3m9 12v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4"/><path d="m7 10l5 5l5-5"/></g>"#,
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
    name: "wrench-15",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M14.7 6.3a1 1 0 0 0 0 1.4l1.6 1.6a1 1 0 0 0 1.4 0l3.106-3.105c.32-.322.863-.22.983.218a6 6 0 0 1-8.259 7.057l-7.91 7.91a1 1 0 0 1-2.999-3l7.91-7.91a6 6 0 0 1 7.057-8.259c.438.12.54.662.219.984z"/></g>"#,
};
const ICON_WRENCH_LG: Icon = Icon {
    name: "wrench-175",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.75" stroke-linecap="round" stroke-linejoin="round"><path d="M14.7 6.3a1 1 0 0 0 0 1.4l1.6 1.6a1 1 0 0 0 1.4 0l3.106-3.105c.32-.322.863-.22.983.218a6 6 0 0 1-8.259 7.057l-7.91 7.91a1 1 0 0 1-2.999-3l7.91-7.91a6 6 0 0 1 7.057-8.259c.438.12.54.662.219.984z"/></g>"#,
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
    name: "trash-15",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M10 11v6m4-6v6m5-11v14a2 2 0 0 1-2 2H7a2 2 0 0 1-2-2V6M3 6h18M8 6V4a2 2 0 0 1 2-2h4a2 2 0 0 1 2 2v2"/></g>"#,
};
const ICON_TRASH_LG: Icon = Icon {
    name: "trash-175",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.75" stroke-linecap="round" stroke-linejoin="round"><path d="M10 11v6m4-6v6m5-11v14a2 2 0 0 1-2 2H7a2 2 0 0 1-2-2V6M3 6h18M8 6V4a2 2 0 0 1 2-2h4a2 2 0 0 1 2 2v2"/></g>"#,
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
    name: "sliders-vertical-15",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M10 8h4m-2 13v-9m0-4V3m5 13h4m-2-4V3m0 18v-5M3 14h4m-2-4V3m0 18v-7"/></g>"#,
};
const ICON_SLIDERS_LG: Icon = Icon {
    name: "sliders-vertical-175",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.75" stroke-linecap="round" stroke-linejoin="round"><path d="M10 8h4m-2 13v-9m0-4V3m5 13h4m-2-4V3m0 18v-5M3 14h4m-2-4V3m0 18v-7"/></g>"#,
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
    name: "external-link-15",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M15 3h6v6m-11 5L21 3m-3 10v6a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V8a2 2 0 0 1 2-2h6"/></g>"#,
};
const ICON_EXTERNAL_LG: Icon = Icon {
    name: "external-link-175",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.75" stroke-linecap="round" stroke-linejoin="round"><path d="M15 3h6v6m-11 5L21 3m-3 10v6a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V8a2 2 0 0 1 2-2h6"/></g>"#,
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
    name: "sparkles-15",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M11.017 2.814a1 1 0 0 1 1.966 0l1.051 5.558a2 2 0 0 0 1.594 1.594l5.558 1.051a1 1 0 0 1 0 1.966l-5.558 1.051a2 2 0 0 0-1.594 1.594l-1.051 5.558a1 1 0 0 1-1.966 0l-1.051-5.558a2 2 0 0 0-1.594-1.594l-5.558-1.051a1 1 0 0 1 0-1.966l5.558-1.051a2 2 0 0 0 1.594-1.594zM20 2v4m2-2h-4"/><circle cx="4" cy="20" r="2"/></g>"#,
};
const ICON_SPARKLES_LG: Icon = Icon {
    name: "sparkles-175",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.75" stroke-linecap="round" stroke-linejoin="round"><path d="M11.017 2.814a1 1 0 0 1 1.966 0l1.051 5.558a2 2 0 0 0 1.594 1.594l5.558 1.051a1 1 0 0 1 0 1.966l-5.558 1.051a2 2 0 0 0-1.594 1.594l-1.051 5.558a1 1 0 0 1-1.966 0l-1.051-5.558a2 2 0 0 0-1.594-1.594l-5.558-1.051a1 1 0 0 1 0-1.966l5.558-1.051a2 2 0 0 0 1.594-1.594zM20 2v4m2-2h-4"/><circle cx="4" cy="20" r="2"/></g>"#,
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
    name: "gamepad-2-15",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M6 11h4M8 9v4m7-1h.01M18 10h.01m-.69-5H6.68a4 4 0 0 0-3.978 3.59l-.017.152C2.604 9.416 2 14.456 2 16a3 3 0 0 0 3 3c1 0 1.5-.5 2-1l1.414-1.414A2 2 0 0 1 9.828 16h4.344a2 2 0 0 1 1.414.586L17 18c.5.5 1 1 2 1a3 3 0 0 0 3-3c0-1.545-.604-6.584-.685-7.258q-.01-.075-.017-.151A4 4 0 0 0 17.32 5"/></g>"#,
};
const ICON_GAMEPAD_LG: Icon = Icon {
    name: "gamepad-2-175",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.75" stroke-linecap="round" stroke-linejoin="round"><path d="M6 11h4M8 9v4m7-1h.01M18 10h.01m-.69-5H6.68a4 4 0 0 0-3.978 3.59l-.017.152C2.604 9.416 2 14.456 2 16a3 3 0 0 0 3 3c1 0 1.5-.5 2-1l1.414-1.414A2 2 0 0 1 9.828 16h4.344a2 2 0 0 1 1.414.586L17 18c.5.5 1 1 2 1a3 3 0 0 0 3-3c0-1.545-.604-6.584-.685-7.258q-.01-.075-.017-.151A4 4 0 0 0 17.32 5"/></g>"#,
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
    name: "trophy-15",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M10 14.66V17a1 1 0 0 1-1 1a2 2 0 0 0-2 2v2m7-7.34V17a1 1 0 0 0 1 1a2 2 0 0 1 2 2v2m.916-12H19.5A2.5 2.5 0 0 0 22 7.5V5a1 1 0 0 0-1-1h-3M4 22h16"/><path d="M6 9a6 6 0 0 0 12 0V3a1 1 0 0 0-1-1H7a1 1 0 0 0-1 1z"/><path d="M6.084 10H4.5A2.5 2.5 0 0 1 2 7.5V5a1 1 0 0 1 1-1h3"/></g>"#,
};
const ICON_TROPHY_LG: Icon = Icon {
    name: "trophy-175",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.75" stroke-linecap="round" stroke-linejoin="round"><path d="M10 14.66V17a1 1 0 0 1-1 1a2 2 0 0 0-2 2v2m7-7.34V17a1 1 0 0 0 1 1a2 2 0 0 1 2 2v2m.916-12H19.5A2.5 2.5 0 0 0 22 7.5V5a1 1 0 0 0-1-1h-3M4 22h16"/><path d="M6 9a6 6 0 0 0 12 0V3a1 1 0 0 0-1-1H7a1 1 0 0 0-1 1z"/><path d="M6.084 10H4.5A2.5 2.5 0 0 1 2 7.5V5a1 1 0 0 1 1-1h3"/></g>"#,
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
    name: "shield-15",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M20 13c0 5-3.5 7.5-7.66 8.95a1 1 0 0 1-.67-.01C7.5 20.5 4 18 4 13V6a1 1 0 0 1 1-1c2 0 4.5-1.2 6.24-2.72a1.17 1.17 0 0 1 1.52 0C14.51 3.81 17 5 19 5a1 1 0 0 1 1 1z"/></g>"#,
};
const ICON_SHIELD_LG: Icon = Icon {
    name: "shield-175",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.75" stroke-linecap="round" stroke-linejoin="round"><path d="M20 13c0 5-3.5 7.5-7.66 8.95a1 1 0 0 1-.67-.01C7.5 20.5 4 18 4 13V6a1 1 0 0 1 1-1c2 0 4.5-1.2 6.24-2.72a1.17 1.17 0 0 1 1.52 0C14.51 3.81 17 5 19 5a1 1 0 0 1 1 1z"/></g>"#,
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
    name: "hard-drive-15",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M10 16h.01m-7.798-4.423a2 2 0 0 0-.212.896V18a2 2 0 0 0 2 2h16a2 2 0 0 0 2-2v-5.527a2 2 0 0 0-.212-.896L18.55 5.11A2 2 0 0 0 16.76 4H7.24a2 2 0 0 0-1.79 1.11zm19.734.436H2.054M6 16h.01"/></g>"#,
};
const ICON_HARD_DRIVE_LG: Icon = Icon {
    name: "hard-drive-175",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.75" stroke-linecap="round" stroke-linejoin="round"><path d="M10 16h.01m-7.798-4.423a2 2 0 0 0-.212.896V18a2 2 0 0 0 2 2h16a2 2 0 0 0 2-2v-5.527a2 2 0 0 0-.212-.896L18.55 5.11A2 2 0 0 0 16.76 4H7.24a2 2 0 0 0-1.79 1.11zm19.734.436H2.054M6 16h.01"/></g>"#,
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
    name: "keyboard-15",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M10 8h.01M12 12h.01M14 8h.01M16 12h.01M18 8h.01M6 8h.01M7 16h10m-9-4h.01"/><rect width="20" height="16" x="2" y="4" rx="2"/></g>"#,
};
const ICON_KEYBOARD_LG: Icon = Icon {
    name: "keyboard-175",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.75" stroke-linecap="round" stroke-linejoin="round"><path d="M10 8h.01M12 12h.01M14 8h.01M16 12h.01M18 8h.01M6 8h.01M7 16h10m-9-4h.01"/><rect width="20" height="16" x="2" y="4" rx="2"/></g>"#,
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
    name: "volume-15",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M11 4.702a.705.705 0 0 0-1.203-.498L6.413 7.587A1.4 1.4 0 0 1 5.416 8H3a1 1 0 0 0-1 1v6a1 1 0 0 0 1 1h2.416a1.4 1.4 0 0 1 .997.413l3.383 3.384A.705.705 0 0 0 11 19.298z"/></g>"#,
};
const ICON_VOLUME_LG: Icon = Icon {
    name: "volume-175",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.75" stroke-linecap="round" stroke-linejoin="round"><path d="M11 4.702a.705.705 0 0 0-1.203-.498L6.413 7.587A1.4 1.4 0 0 1 5.416 8H3a1 1 0 0 0-1 1v6a1 1 0 0 0 1 1h2.416a1.4 1.4 0 0 1 .997.413l3.383 3.384A.705.705 0 0 0 11 19.298z"/></g>"#,
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
    name: "volume-off-15",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M16 9a5 5 0 0 1 .95 2.293m2.414-5.657a9 9 0 0 1 1.889 9.96M2 2l20 20M7 7l-.587.587A1.4 1.4 0 0 1 5.416 8H3a1 1 0 0 0-1 1v6a1 1 0 0 0 1 1h2.416a1.4 1.4 0 0 1 .997.413l3.383 3.384A.705.705 0 0 0 11 19.298V11M9.828 4.172A.686.686 0 0 1 11 4.657v.686"/></g>"#,
};
const ICON_VOLUME_OFF_LG: Icon = Icon {
    name: "volume-off-175",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.75" stroke-linecap="round" stroke-linejoin="round"><path d="M16 9a5 5 0 0 1 .95 2.293m2.414-5.657a9 9 0 0 1 1.889 9.96M2 2l20 20M7 7l-.587.587A1.4 1.4 0 0 1 5.416 8H3a1 1 0 0 0-1 1v6a1 1 0 0 0 1 1h2.416a1.4 1.4 0 0 1 .997.413l3.383 3.384A.705.705 0 0 0 11 19.298V11M9.828 4.172A.686.686 0 0 1 11 4.657v.686"/></g>"#,
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
    name: "zap-15",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M15.914 4a1.5 1.5 0 0 0-2.474-1.561l-9 9A1.5 1.5 0 0 0 5.5 14h4.002a.5.5 0 0 1 .471.666L8.086 20a1.5 1.5 0 0 0 2.475 1.56l9-9A1.5 1.5 0 0 0 18.5 10h-3.997a.5.5 0 0 1-.472-.667z"/></g>"#,
};
const ICON_ZAP_LG: Icon = Icon {
    name: "zap-175",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.75" stroke-linecap="round" stroke-linejoin="round"><path d="M15.914 4a1.5 1.5 0 0 0-2.474-1.561l-9 9A1.5 1.5 0 0 0 5.5 14h4.002a.5.5 0 0 1 .471.666L8.086 20a1.5 1.5 0 0 0 2.475 1.56l9-9A1.5 1.5 0 0 0 18.5 10h-3.997a.5.5 0 0 1-.472-.667z"/></g>"#,
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
    name: "monitor-15",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><rect width="20" height="14" x="2" y="3" rx="2"/><path d="M8 21h8m-4-4v4"/></g>"#,
};
const ICON_MONITOR_LG: Icon = Icon {
    name: "monitor-175",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.75" stroke-linecap="round" stroke-linejoin="round"><rect width="20" height="14" x="2" y="3" rx="2"/><path d="M8 21h8m-4-4v4"/></g>"#,
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
    name: "check-15",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M20 6L9 17l-5-5"/></g>"#,
};
const ICON_CHECK_LG: Icon = Icon {
    name: "check-175",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.75" stroke-linecap="round" stroke-linejoin="round"><path d="M20 6L9 17l-5-5"/></g>"#,
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
    name: "user-15",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M19 21v-2a4 4 0 0 0-4-4H9a4 4 0 0 0-4 4v2"/><circle cx="12" cy="7" r="4"/></g>"#,
};
const ICON_USER_LG: Icon = Icon {
    name: "user-175",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.75" stroke-linecap="round" stroke-linejoin="round"><path d="M19 21v-2a4 4 0 0 0-4-4H9a4 4 0 0 0-4 4v2"/><circle cx="12" cy="7" r="4"/></g>"#,
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
    name: "app-window-15",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><rect width="20" height="16" x="2" y="4" rx="2"/><path d="M10 4v4M2 8h20M6 4v4"/></g>"#,
};
const ICON_APP_WINDOW_LG: Icon = Icon {
    name: "app-window-175",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.75" stroke-linecap="round" stroke-linejoin="round"><rect width="20" height="16" x="2" y="4" rx="2"/><path d="M10 4v4M2 8h20M6 4v4"/></g>"#,
};

pub fn icon_app_window(size: f32, color: Color) -> El {
    let glyph = if size < 24.0 {
        &ICON_APP_WINDOW_SM
    } else {
        &ICON_APP_WINDOW_LG
    };
    icon(glyph, size).color(color)
}

const ICON_PAUSE_SOLID: Icon = Icon {
    name: "pause-solid",
    view: 24.0,
    body: r#"<g fill="currentColor" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><rect width="5" height="18" x="14" y="3" rx="1"/><rect width="5" height="18" x="5" y="3" rx="1"/></g>"#,
};

pub fn icon_pause(size: f32, color: Color) -> El {
    icon(&ICON_PAUSE_SOLID, size).color(color)
}

const ICON_SKIP_BACK_SOLID: Icon = Icon {
    name: "skip-back-solid",
    view: 24.0,
    body: r#"<g fill="currentColor" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M17.971 4.285A2 2 0 0 1 21 6v12a2 2 0 0 1-3.029 1.715l-9.997-5.998a2 2 0 0 1-.003-3.432zM3 20V4"/></g>"#,
};

pub fn icon_skip_back(size: f32, color: Color) -> El {
    icon(&ICON_SKIP_BACK_SOLID, size).color(color)
}

const ICON_SKIP_FORWARD_SOLID: Icon = Icon {
    name: "skip-forward-solid",
    view: 24.0,
    body: r#"<g fill="currentColor" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M21 4v16M6.029 4.285A2 2 0 0 0 3 6v12a2 2 0 0 0 3.029 1.715l9.997-5.998a2 2 0 0 0 .003-3.432z"/></g>"#,
};

pub fn icon_skip_forward(size: f32, color: Color) -> El {
    icon(&ICON_SKIP_FORWARD_SOLID, size).color(color)
}

const ICON_FILM_SM: Icon = Icon {
    name: "film-15",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><rect width="18" height="18" x="3" y="3" rx="2"/><path d="M7 3v18M3 7.5h4M3 12h18M3 16.5h4M17 3v18m0-13.5h4m-4 9h4"/></g>"#,
};
const ICON_FILM_LG: Icon = Icon {
    name: "film-175",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.75" stroke-linecap="round" stroke-linejoin="round"><rect width="18" height="18" x="3" y="3" rx="2"/><path d="M7 3v18M3 7.5h4M3 12h18M3 16.5h4M17 3v18m0-13.5h4m-4 9h4"/></g>"#,
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
    name: "camera-15",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M13.997 4a2 2 0 0 1 1.76 1.05l.486.9A2 2 0 0 0 18.003 7H20a2 2 0 0 1 2 2v9a2 2 0 0 1-2 2H4a2 2 0 0 1-2-2V9a2 2 0 0 1 2-2h1.997a2 2 0 0 0 1.759-1.048l.489-.904A2 2 0 0 1 10.004 4z"/><circle cx="12" cy="13" r="3"/></g>"#,
};
const ICON_CAMERA_LG: Icon = Icon {
    name: "camera-175",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.75" stroke-linecap="round" stroke-linejoin="round"><path d="M13.997 4a2 2 0 0 1 1.76 1.05l.486.9A2 2 0 0 0 18.003 7H20a2 2 0 0 1 2 2v9a2 2 0 0 1-2 2H4a2 2 0 0 1-2-2V9a2 2 0 0 1 2-2h1.997a2 2 0 0 0 1.759-1.048l.489-.904A2 2 0 0 1 10.004 4z"/><circle cx="12" cy="13" r="3"/></g>"#,
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
    name: "star-15",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M11.525 2.295a.53.53 0 0 1 .95 0l2.31 4.679a2.12 2.12 0 0 0 1.595 1.16l5.166.756a.53.53 0 0 1 .294.904l-3.736 3.638a2.12 2.12 0 0 0-.611 1.878l.882 5.14a.53.53 0 0 1-.771.56l-4.618-2.428a2.12 2.12 0 0 0-1.973 0L6.396 21.01a.53.53 0 0 1-.77-.56l.881-5.139a2.12 2.12 0 0 0-.611-1.879L2.16 9.795a.53.53 0 0 1 .294-.906l5.165-.755a2.12 2.12 0 0 0 1.597-1.16z"/></g>"#,
};
const ICON_STAR_LG: Icon = Icon {
    name: "star-175",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.75" stroke-linecap="round" stroke-linejoin="round"><path d="M11.525 2.295a.53.53 0 0 1 .95 0l2.31 4.679a2.12 2.12 0 0 0 1.595 1.16l5.166.756a.53.53 0 0 1 .294.904l-3.736 3.638a2.12 2.12 0 0 0-.611 1.878l.882 5.14a.53.53 0 0 1-.771.56l-4.618-2.428a2.12 2.12 0 0 0-1.973 0L6.396 21.01a.53.53 0 0 1-.77-.56l.881-5.139a2.12 2.12 0 0 0-.611-1.879L2.16 9.795a.53.53 0 0 1 .294-.906l5.165-.755a2.12 2.12 0 0 0 1.597-1.16z"/></g>"#,
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
    name: "scissors-15",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><circle cx="6" cy="6" r="3"/><path d="M8.12 8.12L12 12m8-8L8.12 15.88"/><circle cx="6" cy="18" r="3"/><path d="M14.8 14.8L20 20"/></g>"#,
};
const ICON_SCISSORS_LG: Icon = Icon {
    name: "scissors-175",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.75" stroke-linecap="round" stroke-linejoin="round"><circle cx="6" cy="6" r="3"/><path d="M8.12 8.12L12 12m8-8L8.12 15.88"/><circle cx="6" cy="18" r="3"/><path d="M14.8 14.8L20 20"/></g>"#,
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
    name: "hash-15",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M4 9h16M4 15h16M10 3L8 21m8-18l-2 18"/></g>"#,
};
const ICON_HASH_LG: Icon = Icon {
    name: "hash-175",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.75" stroke-linecap="round" stroke-linejoin="round"><path d="M4 9h16M4 15h16M10 3L8 21m8-18l-2 18"/></g>"#,
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
    name: "mic-15",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M12 19v3m7-12v2a7 7 0 0 1-14 0v-2"/><rect width="6" height="13" x="9" y="2" rx="3"/></g>"#,
};
const ICON_MIC_LG: Icon = Icon {
    name: "mic-175",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.75" stroke-linecap="round" stroke-linejoin="round"><path d="M12 19v3m7-12v2a7 7 0 0 1-14 0v-2"/><rect width="6" height="13" x="9" y="2" rx="3"/></g>"#,
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
    name: "mic-off-15",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M12 19v3m3-12.66V5a3 3 0 0 0-5.68-1.33m7.63 13.28A7 7 0 0 1 5 12v-2m13.89 3.23A7 7 0 0 0 19 12v-2M2 2l20 20"/><path d="M9 9v3a3 3 0 0 0 5.12 2.12"/></g>"#,
};
const ICON_MIC_OFF_LG: Icon = Icon {
    name: "mic-off-175",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.75" stroke-linecap="round" stroke-linejoin="round"><path d="M12 19v3m3-12.66V5a3 3 0 0 0-5.68-1.33m7.63 13.28A7 7 0 0 1 5 12v-2m13.89 3.23A7 7 0 0 0 19 12v-2M2 2l20 20"/><path d="M9 9v3a3 3 0 0 0 5.12 2.12"/></g>"#,
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
    name: "headphones-15",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M3 14h3a2 2 0 0 1 2 2v3a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-7a9 9 0 0 1 18 0v7a2 2 0 0 1-2 2h-1a2 2 0 0 1-2-2v-3a2 2 0 0 1 2-2h3"/></g>"#,
};
const ICON_HEADPHONES_LG: Icon = Icon {
    name: "headphones-175",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.75" stroke-linecap="round" stroke-linejoin="round"><path d="M3 14h3a2 2 0 0 1 2 2v3a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-7a9 9 0 0 1 18 0v7a2 2 0 0 1-2 2h-1a2 2 0 0 1-2-2v-3a2 2 0 0 1 2-2h3"/></g>"#,
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
    name: "headphone-off-15",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M21 14h-1.343M9.128 3.47A9 9 0 0 1 21 12v3.343M2 2l20 20m-1.586-1.586A2 2 0 0 1 19 21h-1a2 2 0 0 1-2-2v-3M3 14h3a2 2 0 0 1 2 2v3a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-7a9 9 0 0 1 2.636-6.364"/></g>"#,
};
const ICON_HEADPHONES_OFF_LG: Icon = Icon {
    name: "headphone-off-175",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.75" stroke-linecap="round" stroke-linejoin="round"><path d="M21 14h-1.343M9.128 3.47A9 9 0 0 1 21 12v3.343M2 2l20 20m-1.586-1.586A2 2 0 0 1 19 21h-1a2 2 0 0 1-2-2v-3M3 14h3a2 2 0 0 1 2 2v3a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-7a9 9 0 0 1 2.636-6.364"/></g>"#,
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
    name: "users-15",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M16 21v-2a4 4 0 0 0-4-4H6a4 4 0 0 0-4 4v2M16 3.128a4 4 0 0 1 0 7.744M22 21v-2a4 4 0 0 0-3-3.87"/><circle cx="9" cy="7" r="4"/></g>"#,
};
const ICON_USERS_LG: Icon = Icon {
    name: "users-175",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.75" stroke-linecap="round" stroke-linejoin="round"><path d="M16 21v-2a4 4 0 0 0-4-4H6a4 4 0 0 0-4 4v2M16 3.128a4 4 0 0 1 0 7.744M22 21v-2a4 4 0 0 0-3-3.87"/><circle cx="9" cy="7" r="4"/></g>"#,
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
    name: "user-plus-15",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M16 21v-2a4 4 0 0 0-4-4H6a4 4 0 0 0-4 4v2"/><circle cx="9" cy="7" r="4"/><path d="M19 8v6m3-3h-6"/></g>"#,
};
const ICON_USER_PLUS_LG: Icon = Icon {
    name: "user-plus-175",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.75" stroke-linecap="round" stroke-linejoin="round"><path d="M16 21v-2a4 4 0 0 0-4-4H6a4 4 0 0 0-4 4v2"/><circle cx="9" cy="7" r="4"/><path d="M19 8v6m3-3h-6"/></g>"#,
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
    name: "phone-15",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M13.832 16.568a1 1 0 0 0 1.213-.303l.355-.465A2 2 0 0 1 17 15h3a2 2 0 0 1 2 2v3a2 2 0 0 1-2 2A18 18 0 0 1 2 4a2 2 0 0 1 2-2h3a2 2 0 0 1 2 2v3a2 2 0 0 1-.8 1.6l-.468.351a1 1 0 0 0-.292 1.233a14 14 0 0 0 6.392 6.384"/></g>"#,
};
const ICON_PHONE_LG: Icon = Icon {
    name: "phone-175",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.75" stroke-linecap="round" stroke-linejoin="round"><path d="M13.832 16.568a1 1 0 0 0 1.213-.303l.355-.465A2 2 0 0 1 17 15h3a2 2 0 0 1 2 2v3a2 2 0 0 1-2 2A18 18 0 0 1 2 4a2 2 0 0 1 2-2h3a2 2 0 0 1 2 2v3a2 2 0 0 1-.8 1.6l-.468.351a1 1 0 0 0-.292 1.233a14 14 0 0 0 6.392 6.384"/></g>"#,
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
    name: "phone-off-15",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M10.1 13.9a14 14 0 0 0 3.732 2.668a1 1 0 0 0 1.213-.303l.355-.465A2 2 0 0 1 17 15h3a2 2 0 0 1 2 2v3a2 2 0 0 1-2 2a18 18 0 0 1-12.728-5.272M22 2L2 22m2.76-8.418A18 18 0 0 1 2 4a2 2 0 0 1 2-2h3a2 2 0 0 1 2 2v3a2 2 0 0 1-.8 1.6l-.468.351a1 1 0 0 0-.292 1.233a14 14 0 0 0 .244.473"/></g>"#,
};
const ICON_PHONE_OFF_LG: Icon = Icon {
    name: "phone-off-175",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.75" stroke-linecap="round" stroke-linejoin="round"><path d="M10.1 13.9a14 14 0 0 0 3.732 2.668a1 1 0 0 0 1.213-.303l.355-.465A2 2 0 0 1 17 15h3a2 2 0 0 1 2 2v3a2 2 0 0 1-2 2a18 18 0 0 1-12.728-5.272M22 2L2 22m2.76-8.418A18 18 0 0 1 2 4a2 2 0 0 1 2-2h3a2 2 0 0 1 2 2v3a2 2 0 0 1-.8 1.6l-.468.351a1 1 0 0 0-.292 1.233a14 14 0 0 0 .244.473"/></g>"#,
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
    name: "lock-15",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><rect width="18" height="11" x="3" y="11" rx="2" ry="2"/><path d="M7 11V7a5 5 0 0 1 10 0v4"/></g>"#,
};
const ICON_LOCK_LG: Icon = Icon {
    name: "lock-175",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.75" stroke-linecap="round" stroke-linejoin="round"><rect width="18" height="11" x="3" y="11" rx="2" ry="2"/><path d="M7 11V7a5 5 0 0 1 10 0v4"/></g>"#,
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
    name: "house-15",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M15 21v-8a1 1 0 0 0-1-1h-4a1 1 0 0 0-1 1v8"/><path d="M3 10a2 2 0 0 1 .709-1.528l7-6a2 2 0 0 1 2.582 0l7 6A2 2 0 0 1 21 10v9a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2z"/></g>"#,
};
const ICON_HOME_LG: Icon = Icon {
    name: "house-175",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.75" stroke-linecap="round" stroke-linejoin="round"><path d="M15 21v-8a1 1 0 0 0-1-1h-4a1 1 0 0 0-1 1v8"/><path d="M3 10a2 2 0 0 1 .709-1.528l7-6a2 2 0 0 1 2.582 0l7 6A2 2 0 0 1 21 10v9a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2z"/></g>"#,
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
    name: "server-15",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><rect width="20" height="8" x="2" y="2" rx="2" ry="2"/><rect width="20" height="8" x="2" y="14" rx="2" ry="2"/><path d="M6 6h.01M6 18h.01"/></g>"#,
};
const ICON_SERVER_LG: Icon = Icon {
    name: "server-175",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.75" stroke-linecap="round" stroke-linejoin="round"><rect width="20" height="8" x="2" y="2" rx="2" ry="2"/><rect width="20" height="8" x="2" y="14" rx="2" ry="2"/><path d="M6 6h.01M6 18h.01"/></g>"#,
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
    name: "bell-off-15",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M10.268 21a2 2 0 0 0 3.464 0M17 17H4a1 1 0 0 1-.74-1.673C4.59 13.956 6 12.499 6 8a6 6 0 0 1 .258-1.742M2 2l20 20M8.668 3.01A6 6 0 0 1 18 8c0 2.687.77 4.653 1.707 6.05"/></g>"#,
};
const ICON_BELL_OFF_LG: Icon = Icon {
    name: "bell-off-175",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.75" stroke-linecap="round" stroke-linejoin="round"><path d="M10.268 21a2 2 0 0 0 3.464 0M17 17H4a1 1 0 0 1-.74-1.673C4.59 13.956 6 12.499 6 8a6 6 0 0 1 .258-1.742M2 2l20 20M8.668 3.01A6 6 0 0 1 18 8c0 2.687.77 4.653 1.707 6.05"/></g>"#,
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
    name: "chevron-down-15",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="m6 9l6 6l6-6"/></g>"#,
};
const ICON_CHEVRON_DOWN_LG: Icon = Icon {
    name: "chevron-down-175",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.75" stroke-linecap="round" stroke-linejoin="round"><path d="m6 9l6 6l6-6"/></g>"#,
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
    name: "chevron-right-15",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="m9 18l6-6l-6-6"/></g>"#,
};
const ICON_CHEVRON_RIGHT_LG: Icon = Icon {
    name: "chevron-right-175",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.75" stroke-linecap="round" stroke-linejoin="round"><path d="m9 18l6-6l-6-6"/></g>"#,
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
    name: "send-15",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M14.536 21.686a.5.5 0 0 0 .937-.024l6.5-19a.496.496 0 0 0-.635-.635l-19 6.5a.5.5 0 0 0-.024.937l7.93 3.18a2 2 0 0 1 1.112 1.11zm7.318-19.539l-10.94 10.939"/></g>"#,
};
const ICON_SEND_LG: Icon = Icon {
    name: "send-175",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.75" stroke-linecap="round" stroke-linejoin="round"><path d="M14.536 21.686a.5.5 0 0 0 .937-.024l6.5-19a.496.496 0 0 0-.635-.635l-19 6.5a.5.5 0 0 0-.024.937l7.93 3.18a2 2 0 0 1 1.112 1.11zm7.318-19.539l-10.94 10.939"/></g>"#,
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
    name: "reply-15",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M20 18v-2a4 4 0 0 0-4-4H4"/><path d="m9 17l-5-5l5-5"/></g>"#,
};
const ICON_REPLY_LG: Icon = Icon {
    name: "reply-175",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.75" stroke-linecap="round" stroke-linejoin="round"><path d="M20 18v-2a4 4 0 0 0-4-4H4"/><path d="m9 17l-5-5l5-5"/></g>"#,
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
    name: "pin-15",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M12 17v5M9 10.76a2 2 0 0 1-1.11 1.79l-1.78.9A2 2 0 0 0 5 15.24V16a1 1 0 0 0 1 1h12a1 1 0 0 0 1-1v-.76a2 2 0 0 0-1.11-1.79l-1.78-.9A2 2 0 0 1 15 10.76V7a1 1 0 0 1 1-1a2 2 0 0 0 0-4H8a2 2 0 0 0 0 4a1 1 0 0 1 1 1z"/></g>"#,
};
const ICON_PIN_LG: Icon = Icon {
    name: "pin-175",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.75" stroke-linecap="round" stroke-linejoin="round"><path d="M12 17v5M9 10.76a2 2 0 0 1-1.11 1.79l-1.78.9A2 2 0 0 0 5 15.24V16a1 1 0 0 0 1 1h12a1 1 0 0 0 1-1v-.76a2 2 0 0 0-1.11-1.79l-1.78-.9A2 2 0 0 1 15 10.76V7a1 1 0 0 1 1-1a2 2 0 0 0 0-4H8a2 2 0 0 0 0 4a1 1 0 0 1 1 1z"/></g>"#,
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
    name: "smile-15",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><circle cx="12" cy="12" r="10"/><path d="M8 14s1.5 2 4 2s4-2 4-2M9 9h.01M15 9h.01"/></g>"#,
};
const ICON_SMILE_LG: Icon = Icon {
    name: "smile-175",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.75" stroke-linecap="round" stroke-linejoin="round"><circle cx="12" cy="12" r="10"/><path d="M8 14s1.5 2 4 2s4-2 4-2M9 9h.01M15 9h.01"/></g>"#,
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
    name: "paperclip-15",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="m16 6l-8.414 8.586a2 2 0 0 0 2.829 2.829l8.414-8.586a4 4 0 1 0-5.657-5.657l-8.379 8.551a6 6 0 1 0 8.485 8.485l8.379-8.551"/></g>"#,
};
const ICON_PAPERCLIP_LG: Icon = Icon {
    name: "paperclip-175",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.75" stroke-linecap="round" stroke-linejoin="round"><path d="m16 6l-8.414 8.586a2 2 0 0 0 2.829 2.829l8.414-8.586a4 4 0 1 0-5.657-5.657l-8.379 8.551a6 6 0 1 0 8.485 8.485l8.379-8.551"/></g>"#,
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
    name: "pencil-15",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M21.174 6.812a1 1 0 0 0-3.986-3.987L3.842 16.174a2 2 0 0 0-.5.83l-1.321 4.352a.5.5 0 0 0 .623.622l4.353-1.32a2 2 0 0 0 .83-.497zM15 5l4 4"/></g>"#,
};
const ICON_EDIT_LG: Icon = Icon {
    name: "pencil-175",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.75" stroke-linecap="round" stroke-linejoin="round"><path d="M21.174 6.812a1 1 0 0 0-3.986-3.987L3.842 16.174a2 2 0 0 0-.5.83l-1.321 4.352a.5.5 0 0 0 .623.622l4.353-1.32a2 2 0 0 0 .83-.497zM15 5l4 4"/></g>"#,
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
    name: "plus-15",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M5 12h14m-7-7v14"/></g>"#,
};
const ICON_PLUS_LG: Icon = Icon {
    name: "plus-175",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.75" stroke-linecap="round" stroke-linejoin="round"><path d="M5 12h14m-7-7v14"/></g>"#,
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
    name: "screen-share-15",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M13 3H4a2 2 0 0 0-2 2v10a2 2 0 0 0 2 2h16a2 2 0 0 0 2-2v-3M8 21h8m-4-4v4m5-13l5-5m-5 0h5v5"/></g>"#,
};
const ICON_SCREEN_SHARE_LG: Icon = Icon {
    name: "screen-share-175",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.75" stroke-linecap="round" stroke-linejoin="round"><path d="M13 3H4a2 2 0 0 0-2 2v10a2 2 0 0 0 2 2h16a2 2 0 0 0 2-2v-3M8 21h8m-4-4v4m5-13l5-5m-5 0h5v5"/></g>"#,
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
    name: "video-15",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="m16 13l5.223 3.482a.5.5 0 0 0 .777-.416V7.87a.5.5 0 0 0-.752-.432L16 10.5"/><rect width="14" height="12" x="2" y="6" rx="2"/></g>"#,
};
const ICON_VIDEO_LG: Icon = Icon {
    name: "video-175",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.75" stroke-linecap="round" stroke-linejoin="round"><path d="m16 13l5.223 3.482a.5.5 0 0 0 .777-.416V7.87a.5.5 0 0 0-.752-.432L16 10.5"/><rect width="14" height="12" x="2" y="6" rx="2"/></g>"#,
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
    name: "hand-15",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M18 11V6a2 2 0 0 0-2-2a2 2 0 0 0-2 2m0 4V4a2 2 0 0 0-2-2a2 2 0 0 0-2 2v2m0 4.5V6a2 2 0 0 0-2-2a2 2 0 0 0-2 2v8"/><path d="M18 8a2 2 0 1 1 4 0v6a8 8 0 0 1-8 8h-2c-2.8 0-4.5-.86-5.99-2.34l-3.6-3.6a2 2 0 0 1 2.83-2.82L7 15"/></g>"#,
};
const ICON_HAND_LG: Icon = Icon {
    name: "hand-175",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.75" stroke-linecap="round" stroke-linejoin="round"><path d="M18 11V6a2 2 0 0 0-2-2a2 2 0 0 0-2 2m0 4V4a2 2 0 0 0-2-2a2 2 0 0 0-2 2v2m0 4.5V6a2 2 0 0 0-2-2a2 2 0 0 0-2 2v8"/><path d="M18 8a2 2 0 1 1 4 0v6a8 8 0 0 1-8 8h-2c-2.8 0-4.5-.86-5.99-2.34l-3.6-3.6a2 2 0 0 1 2.83-2.82L7 15"/></g>"#,
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
    name: "maximize-15",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M8 3H5a2 2 0 0 0-2 2v3m18 0V5a2 2 0 0 0-2-2h-3M3 16v3a2 2 0 0 0 2 2h3m8 0h3a2 2 0 0 0 2-2v-3"/></g>"#,
};
const ICON_MAXIMIZE_LG: Icon = Icon {
    name: "maximize-175",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.75" stroke-linecap="round" stroke-linejoin="round"><path d="M8 3H5a2 2 0 0 0-2 2v3m18 0V5a2 2 0 0 0-2-2h-3M3 16v3a2 2 0 0 0 2 2h3m8 0h3a2 2 0 0 0 2-2v-3"/></g>"#,
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
    name: "library-15",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="m16 6l4 14M12 6v14M8 8v12M4 4v16"/></g>"#,
};
const ICON_LIBRARY_LG: Icon = Icon {
    name: "library-175",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.75" stroke-linecap="round" stroke-linejoin="round"><path d="m16 6l4 14M12 6v14M8 8v12M4 4v16"/></g>"#,
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
    name: "mouse-15",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><rect width="14" height="20" x="5" y="2" rx="7"/><path d="M12 6v4"/></g>"#,
};
const ICON_MOUSE_LG: Icon = Icon {
    name: "mouse-175",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.75" stroke-linecap="round" stroke-linejoin="round"><rect width="14" height="20" x="5" y="2" rx="7"/><path d="M12 6v4"/></g>"#,
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
    name: "ellipsis-15",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><circle cx="12" cy="12" r="1"/><circle cx="19" cy="12" r="1"/><circle cx="5" cy="12" r="1"/></g>"#,
};
const ICON_MORE_HORIZ_LG: Icon = Icon {
    name: "ellipsis-175",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.75" stroke-linecap="round" stroke-linejoin="round"><circle cx="12" cy="12" r="1"/><circle cx="19" cy="12" r="1"/><circle cx="5" cy="12" r="1"/></g>"#,
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
    name: "filter-15",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M22 3H2l8 9.46V19l4 2v-8.54z"/></g>"#,
};
const ICON_FILTER_LG: Icon = Icon {
    name: "filter-175",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.75" stroke-linecap="round" stroke-linejoin="round"><path d="M22 3H2l8 9.46V19l4 2v-8.54z"/></g>"#,
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
    name: "chevron-left-15",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="m15 18l-6-6l6-6"/></g>"#,
};
const ICON_CHEVRON_LEFT_LG: Icon = Icon {
    name: "chevron-left-175",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.75" stroke-linecap="round" stroke-linejoin="round"><path d="m15 18l-6-6l6-6"/></g>"#,
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
    name: "rotate-ccw-clock-15",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M3 12a9 9 0 1 0 9-9a9.75 9.75 0 0 0-6.74 2.74L3 8"/><path d="M3 3v5h5m4-1v5l4 2"/></g>"#,
};
const ICON_REPLAY_LG: Icon = Icon {
    name: "rotate-ccw-clock-175",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.75" stroke-linecap="round" stroke-linejoin="round"><path d="M3 12a9 9 0 1 0 9-9a9.75 9.75 0 0 0-6.74 2.74L3 8"/><path d="M3 3v5h5m4-1v5l4 2"/></g>"#,
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
    name: "circle-solid",
    view: 24.0,
    body: r#"<g fill="currentColor" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><circle cx="12" cy="12" r="10"/></g>"#,
};

pub fn icon_record_dot(size: f32, color: Color) -> El {
    icon(&ICON_RECORD_DOT_SOLID, size).color(color)
}

const ICON_BOOKMARK_SM: Icon = Icon {
    name: "bookmark-15",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M17 3a2 2 0 0 1 2 2v15a1 1 0 0 1-1.496.868l-4.512-2.578a2 2 0 0 0-1.984 0l-4.512 2.578A1 1 0 0 1 5 20V5a2 2 0 0 1 2-2z"/></g>"#,
};
const ICON_BOOKMARK_LG: Icon = Icon {
    name: "bookmark-175",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.75" stroke-linecap="round" stroke-linejoin="round"><path d="M17 3a2 2 0 0 1 2 2v15a1 1 0 0 1-1.496.868l-4.512-2.578a2 2 0 0 0-1.984 0l-4.512 2.578A1 1 0 0 1 5 20V5a2 2 0 0 1 2-2z"/></g>"#,
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
    name: "minimize-15",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M8 3v3a2 2 0 0 1-2 2H3m18 0h-3a2 2 0 0 1-2-2V3M3 16h3a2 2 0 0 1 2 2v3m8 0v-3a2 2 0 0 1 2-2h3"/></g>"#,
};
const ICON_MINIMIZE_LG: Icon = Icon {
    name: "minimize-175",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.75" stroke-linecap="round" stroke-linejoin="round"><path d="M8 3v3a2 2 0 0 1-2 2H3m18 0h-3a2 2 0 0 1-2-2V3M3 16h3a2 2 0 0 1 2 2v3m8 0v-3a2 2 0 0 1 2-2h3"/></g>"#,
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
    name: "maximize-2-15",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M15 3h6v6m0-6l-7 7M3 21l7-7m-1 7H3v-6"/></g>"#,
};
const ICON_WIN_MAXIMIZE_LG: Icon = Icon {
    name: "maximize-2-175",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.75" stroke-linecap="round" stroke-linejoin="round"><path d="M15 3h6v6m0-6l-7 7M3 21l7-7m-1 7H3v-6"/></g>"#,
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
    name: "minimize-2-15",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="m14 10l7-7m-1 7h-6V4M3 21l7-7m-6 0h6v6"/></g>"#,
};
const ICON_WIN_RESTORE_LG: Icon = Icon {
    name: "minimize-2-175",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.75" stroke-linecap="round" stroke-linejoin="round"><path d="m14 10l7-7m-1 7h-6V4M3 21l7-7m-6 0h6v6"/></g>"#,
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
    name: "info-15",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><circle cx="12" cy="12" r="10"/><path d="M12 16v-4m0-4h.01"/></g>"#,
};
const ICON_INFO_LG: Icon = Icon {
    name: "info-175",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.75" stroke-linecap="round" stroke-linejoin="round"><circle cx="12" cy="12" r="10"/><path d="M12 16v-4m0-4h.01"/></g>"#,
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
    name: "copy-15",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><rect width="14" height="14" x="8" y="8" rx="2" ry="2"/><path d="M4 16c-1.1 0-2-.9-2-2V4c0-1.1.9-2 2-2h10c1.1 0 2 .9 2 2"/></g>"#,
};
const ICON_COPY_LG: Icon = Icon {
    name: "copy-175",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.75" stroke-linecap="round" stroke-linejoin="round"><rect width="14" height="14" x="8" y="8" rx="2" ry="2"/><path d="M4 16c-1.1 0-2-.9-2-2V4c0-1.1.9-2 2-2h10c1.1 0 2 .9 2 2"/></g>"#,
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
    name: "link-15",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M10 13a5 5 0 0 0 7.54.54l3-3a5 5 0 0 0-7.07-7.07l-1.72 1.71"/><path d="M14 11a5 5 0 0 0-7.54-.54l-3 3a5 5 0 0 0 7.07 7.07l1.71-1.71"/></g>"#,
};
const ICON_LINK_LG: Icon = Icon {
    name: "link-175",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.75" stroke-linecap="round" stroke-linejoin="round"><path d="M10 13a5 5 0 0 0 7.54.54l3-3a5 5 0 0 0-7.07-7.07l-1.72 1.71"/><path d="M14 11a5 5 0 0 0-7.54-.54l-3 3a5 5 0 0 0 7.07 7.07l1.71-1.71"/></g>"#,
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
    name: "flag-15",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M4 22V4a1 1 0 0 1 .4-.8A6 6 0 0 1 8 2c3 0 5 2 7.333 2q2 0 3.067-.8A1 1 0 0 1 20 4v10a1 1 0 0 1-.4.8A6 6 0 0 1 16 16c-3 0-5-2-8-2a6 6 0 0 0-4 1.528"/></g>"#,
};
const ICON_FLAG_LG: Icon = Icon {
    name: "flag-175",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.75" stroke-linecap="round" stroke-linejoin="round"><path d="M4 22V4a1 1 0 0 1 .4-.8A6 6 0 0 1 8 2c3 0 5 2 7.333 2q2 0 3.067-.8A1 1 0 0 1 20 4v10a1 1 0 0 1-.4.8A6 6 0 0 1 16 16c-3 0-5-2-8-2a6 6 0 0 0-4 1.528"/></g>"#,
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
    name: "forward-15",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="m15 17l5-5l-5-5"/><path d="M4 18v-2a4 4 0 0 1 4-4h12"/></g>"#,
};
const ICON_FORWARD_LG: Icon = Icon {
    name: "forward-175",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.75" stroke-linecap="round" stroke-linejoin="round"><path d="m15 17l5-5l-5-5"/><path d="M4 18v-2a4 4 0 0 1 4-4h12"/></g>"#,
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
    name: "chevron-up-15",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="m18 15l-6-6l-6 6"/></g>"#,
};
const ICON_CHEVRON_UP_LG: Icon = Icon {
    name: "chevron-up-175",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.75" stroke-linecap="round" stroke-linejoin="round"><path d="m18 15l-6-6l-6 6"/></g>"#,
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
    name: "circle-exclamation-point-15",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><circle cx="12" cy="12" r="10"/><path d="M12 8v4m0 4h.01"/></g>"#,
};
const ICON_ALERT_LG: Icon = Icon {
    name: "circle-exclamation-point-175",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.75" stroke-linecap="round" stroke-linejoin="round"><circle cx="12" cy="12" r="10"/><path d="M12 8v4m0 4h.01"/></g>"#,
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
    name: "bell-15",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M10.268 21a2 2 0 0 0 3.464 0m-10.47-5.674A1 1 0 0 0 4 17h16a1 1 0 0 0 .74-1.673C19.41 13.956 18 12.499 18 8A6 6 0 0 0 6 8c0 4.499-1.411 5.956-2.738 7.326"/></g>"#,
};
const ICON_BELL_LG: Icon = Icon {
    name: "bell-175",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.75" stroke-linecap="round" stroke-linejoin="round"><path d="M10.268 21a2 2 0 0 0 3.464 0m-10.47-5.674A1 1 0 0 0 4 17h16a1 1 0 0 0 .74-1.673C19.41 13.956 18 12.499 18 8A6 6 0 0 0 6 8c0 4.499-1.411 5.956-2.738 7.326"/></g>"#,
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
    name: "gift-15",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M12 7v14m8-10v8a2 2 0 0 1-2 2H6a2 2 0 0 1-2-2v-8m3.5-4a1 1 0 0 1 0-5A4.8 8 0 0 1 12 7a4.8 8 0 0 1 4.5-5a1 1 0 0 1 0 5"/><rect width="18" height="4" x="3" y="7" rx="1"/></g>"#,
};
const ICON_GIFT_LG: Icon = Icon {
    name: "gift-175",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.75" stroke-linecap="round" stroke-linejoin="round"><path d="M12 7v14m8-10v8a2 2 0 0 1-2 2H6a2 2 0 0 1-2-2v-8m3.5-4a1 1 0 0 1 0-5A4.8 8 0 0 1 12 7a4.8 8 0 0 1 4.5-5a1 1 0 0 1 0 5"/><rect width="18" height="4" x="3" y="7" rx="1"/></g>"#,
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
    name: "volume-1-15",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M11 4.702a.705.705 0 0 0-1.203-.498L6.413 7.587A1.4 1.4 0 0 1 5.416 8H3a1 1 0 0 0-1 1v6a1 1 0 0 0 1 1h2.416a1.4 1.4 0 0 1 .997.413l3.383 3.384A.705.705 0 0 0 11 19.298zM16 9a5 5 0 0 1 0 6"/></g>"#,
};
const ICON_VOLUME_LOW_LG: Icon = Icon {
    name: "volume-1-175",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.75" stroke-linecap="round" stroke-linejoin="round"><path d="M11 4.702a.705.705 0 0 0-1.203-.498L6.413 7.587A1.4 1.4 0 0 1 5.416 8H3a1 1 0 0 0-1 1v6a1 1 0 0 0 1 1h2.416a1.4 1.4 0 0 1 .997.413l3.383 3.384A.705.705 0 0 0 11 19.298zM16 9a5 5 0 0 1 0 6"/></g>"#,
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
    name: "folder-plus-15",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M12 10v6m-3-3h6m5 7a2 2 0 0 0 2-2V8a2 2 0 0 0-2-2h-7.9a2 2 0 0 1-1.69-.9L9.6 3.9A2 2 0 0 0 7.93 3H4a2 2 0 0 0-2 2v13a2 2 0 0 0 2 2Z"/></g>"#,
};
const ICON_FOLDER_PLUS_LG: Icon = Icon {
    name: "folder-plus-175",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.75" stroke-linecap="round" stroke-linejoin="round"><path d="M12 10v6m-3-3h6m5 7a2 2 0 0 0 2-2V8a2 2 0 0 0-2-2h-7.9a2 2 0 0 1-1.69-.9L9.6 3.9A2 2 0 0 0 7.93 3H4a2 2 0 0 0-2 2v13a2 2 0 0 0 2 2Z"/></g>"#,
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
    name: "notebook-pen-15",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M13.4 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2v-7.4M2 6h4m-4 4h4m-4 4h4m-4 4h4"/><path d="M21.378 5.626a1 1 0 1 0-3.004-3.004l-5.01 5.012a2 2 0 0 0-.506.854l-.837 2.87a.5.5 0 0 0 .62.62l2.87-.837a2 2 0 0 0 .854-.506z"/></g>"#,
};
const ICON_NOTES_LG: Icon = Icon {
    name: "notebook-pen-175",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.75" stroke-linecap="round" stroke-linejoin="round"><path d="M13.4 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2v-7.4M2 6h4m-4 4h4m-4 4h4m-4 4h4"/><path d="M21.378 5.626a1 1 0 1 0-3.004-3.004l-5.01 5.012a2 2 0 0 0-.506.854l-.837 2.87a.5.5 0 0 0 .62.62l2.87-.837a2 2 0 0 0 .854-.506z"/></g>"#,
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
    name: "eye-15",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M2.062 12.348a1 1 0 0 1 0-.696a10.75 10.75 0 0 1 19.876 0a1 1 0 0 1 0 .696a10.75 10.75 0 0 1-19.876 0"/><circle cx="12" cy="12" r="3"/></g>"#,
};
const ICON_EYE_LG: Icon = Icon {
    name: "eye-175",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.75" stroke-linecap="round" stroke-linejoin="round"><path d="M2.062 12.348a1 1 0 0 1 0-.696a10.75 10.75 0 0 1 19.876 0a1 1 0 0 1 0 .696a10.75 10.75 0 0 1-19.876 0"/><circle cx="12" cy="12" r="3"/></g>"#,
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
    name: "eye-off-15",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M10.733 5.076a10.744 10.744 0 0 1 11.205 6.575a1 1 0 0 1 0 .696a10.8 10.8 0 0 1-1.444 2.49m-6.41-.679a3 3 0 0 1-4.242-4.242"/><path d="M17.479 17.499a10.75 10.75 0 0 1-15.417-5.151a1 1 0 0 1 0-.696a10.75 10.75 0 0 1 4.446-5.143M2 2l20 20"/></g>"#,
};
const ICON_EYE_OFF_LG: Icon = Icon {
    name: "eye-off-175",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.75" stroke-linecap="round" stroke-linejoin="round"><path d="M10.733 5.076a10.744 10.744 0 0 1 11.205 6.575a1 1 0 0 1 0 .696a10.8 10.8 0 0 1-1.444 2.49m-6.41-.679a3 3 0 0 1-4.242-4.242"/><path d="M17.479 17.499a10.75 10.75 0 0 1-15.417-5.151a1 1 0 0 1 0-.696a10.75 10.75 0 0 1 4.446-5.143M2 2l20 20"/></g>"#,
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
    name: "image-up-15",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M10.3 21H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h14a2 2 0 0 1 2 2v10l-3.1-3.1a2 2 0 0 0-2.814.014L6 21"/><path d="m14 19.5l3-3l3 3M17 22v-5.5"/><circle cx="9" cy="9" r="2"/></g>"#,
};
const ICON_IMAGE_UPLOAD_LG: Icon = Icon {
    name: "image-up-175",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.75" stroke-linecap="round" stroke-linejoin="round"><path d="M10.3 21H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h14a2 2 0 0 1 2 2v10l-3.1-3.1a2 2 0 0 0-2.814.014L6 21"/><path d="m14 19.5l3-3l3 3M17 22v-5.5"/><circle cx="9" cy="9" r="2"/></g>"#,
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
    name: "zoom-in-15",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><circle cx="11" cy="11" r="8"/><path d="m21 21l-4.35-4.35M11 8v6m-3-3h6"/></g>"#,
};
const ICON_ZOOM_IN_LG: Icon = Icon {
    name: "zoom-in-175",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.75" stroke-linecap="round" stroke-linejoin="round"><circle cx="11" cy="11" r="8"/><path d="m21 21l-4.35-4.35M11 8v6m-3-3h6"/></g>"#,
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
    name: "zoom-out-15",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><circle cx="11" cy="11" r="8"/><path d="m21 21l-4.35-4.35M8 11h6"/></g>"#,
};
const ICON_ZOOM_OUT_LG: Icon = Icon {
    name: "zoom-out-175",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.75" stroke-linecap="round" stroke-linejoin="round"><circle cx="11" cy="11" r="8"/><path d="m21 21l-4.35-4.35M8 11h6"/></g>"#,
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
    name: "square-check-big-15",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M21 10.656V19a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h12.344"/><path d="m9 11l3 3L22 4"/></g>"#,
};
const ICON_SELECT_MANY_LG: Icon = Icon {
    name: "square-check-big-175",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.75" stroke-linecap="round" stroke-linejoin="round"><path d="M21 10.656V19a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h12.344"/><path d="m9 11l3 3L22 4"/></g>"#,
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
    name: "upload-15",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M12 3v12m5-7l-5-5l-5 5m14 7v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4"/></g>"#,
};
const ICON_UPLOAD_LG: Icon = Icon {
    name: "upload-175",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.75" stroke-linecap="round" stroke-linejoin="round"><path d="M12 3v12m5-7l-5-5l-5 5m14 7v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4"/></g>"#,
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
    name: "arrow-right-15",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M5 12h14m-7-7l7 7l-7 7"/></g>"#,
};
const ICON_ARROW_RIGHT_LG: Icon = Icon {
    name: "arrow-right-175",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.75" stroke-linecap="round" stroke-linejoin="round"><path d="M5 12h14m-7-7l7 7l-7 7"/></g>"#,
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
    name: "arrow-up-15",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="m5 12l7-7l7 7m-7 7V5"/></g>"#,
};
const ICON_ARROW_UP_LG: Icon = Icon {
    name: "arrow-up-175",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.75" stroke-linecap="round" stroke-linejoin="round"><path d="m5 12l7-7l7 7m-7 7V5"/></g>"#,
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
    name: "rotate-ccw-15",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M3 12a9 9 0 1 0 9-9a9.75 9.75 0 0 0-6.74 2.74L3 8"/><path d="M3 3v5h5"/></g>"#,
};
const ICON_ROTATE_CCW_LG: Icon = Icon {
    name: "rotate-ccw-175",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.75" stroke-linecap="round" stroke-linejoin="round"><path d="M3 12a9 9 0 1 0 9-9a9.75 9.75 0 0 0-6.74 2.74L3 8"/><path d="M3 3v5h5"/></g>"#,
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
    name: "bookmark-solid",
    view: 24.0,
    body: r#"<g fill="currentColor" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M17 3a2 2 0 0 1 2 2v15a1 1 0 0 1-1.496.868l-4.512-2.578a2 2 0 0 0-1.984 0l-4.512 2.578A1 1 0 0 1 5 20V5a2 2 0 0 1 2-2z"/></g>"#,
};

pub fn icon_bookmark_filled(size: f32, color: Color) -> El {
    icon(&ICON_BOOKMARK_FILLED_SOLID, size).color(color)
}

const ICON_LIST_MUSIC_SM: Icon = Icon {
    name: "list-music-15",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M16 5H3m8 7H3m8 7H3m18-3V5"/><circle cx="18" cy="16" r="3"/></g>"#,
};
const ICON_LIST_MUSIC_LG: Icon = Icon {
    name: "list-music-175",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.75" stroke-linecap="round" stroke-linejoin="round"><path d="M16 5H3m8 7H3m8 7H3m18-3V5"/><circle cx="18" cy="16" r="3"/></g>"#,
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
    name: "radio-15",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M16.247 7.761a6 6 0 0 1 0 8.478m2.828-11.306a10 10 0 0 1 0 14.134m-14.15 0a10 10 0 0 1 0-14.134m2.828 11.306a6 6 0 0 1 0-8.478"/><circle cx="12" cy="12" r="2"/></g>"#,
};
const ICON_RADIO_LG: Icon = Icon {
    name: "radio-175",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.75" stroke-linecap="round" stroke-linejoin="round"><path d="M16.247 7.761a6 6 0 0 1 0 8.478m2.828-11.306a10 10 0 0 1 0 14.134m-14.15 0a10 10 0 0 1 0-14.134m2.828 11.306a6 6 0 0 1 0-8.478"/><circle cx="12" cy="12" r="2"/></g>"#,
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
    name: "compass-15",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><circle cx="12" cy="12" r="10"/><path d="m16.24 7.76l-1.804 5.411a2 2 0 0 1-1.265 1.265L7.76 16.24l1.804-5.411a2 2 0 0 1 1.265-1.265z"/></g>"#,
};
const ICON_COMPASS_LG: Icon = Icon {
    name: "compass-175",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.75" stroke-linecap="round" stroke-linejoin="round"><circle cx="12" cy="12" r="10"/><path d="m16.24 7.76l-1.804 5.411a2 2 0 0 1-1.265 1.265L7.76 16.24l1.804-5.411a2 2 0 0 1 1.265-1.265z"/></g>"#,
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
    name: "shuffle-15",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="m18 14l4 4l-4 4m0-20l4 4l-4 4"/><path d="M2 18h1.973a4 4 0 0 0 3.3-1.7l5.454-8.6a4 4 0 0 1 3.3-1.7H22M2 6h1.972a4 4 0 0 1 3.6 2.2M22 18h-6.041a4 4 0 0 1-3.3-1.8l-.359-.45"/></g>"#,
};
const ICON_SHUFFLE_LG: Icon = Icon {
    name: "shuffle-175",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.75" stroke-linecap="round" stroke-linejoin="round"><path d="m18 14l4 4l-4 4m0-20l4 4l-4 4"/><path d="M2 18h1.973a4 4 0 0 0 3.3-1.7l5.454-8.6a4 4 0 0 1 3.3-1.7H22M2 6h1.972a4 4 0 0 1 3.6 2.2M22 18h-6.041a4 4 0 0 1-3.3-1.8l-.359-.45"/></g>"#,
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
    name: "repeat-15",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="m17 2l4 4l-4 4"/><path d="M3 11v-1a4 4 0 0 1 4-4h14M7 22l-4-4l4-4"/><path d="M21 13v1a4 4 0 0 1-4 4H3"/></g>"#,
};
const ICON_REPEAT_LG: Icon = Icon {
    name: "repeat-175",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.75" stroke-linecap="round" stroke-linejoin="round"><path d="m17 2l4 4l-4 4"/><path d="M3 11v-1a4 4 0 0 1 4-4h14M7 22l-4-4l4-4"/><path d="M21 13v1a4 4 0 0 1-4 4H3"/></g>"#,
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
    name: "disc-3-15",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><circle cx="12" cy="12" r="10"/><path d="M6 12c0-1.7.7-3.2 1.8-4.2"/><circle cx="12" cy="12" r="2"/><path d="M18 12c0 1.7-.7 3.2-1.8 4.2"/></g>"#,
};
const ICON_DISC_LG: Icon = Icon {
    name: "disc-3-175",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.75" stroke-linecap="round" stroke-linejoin="round"><circle cx="12" cy="12" r="10"/><path d="M6 12c0-1.7.7-3.2 1.8-4.2"/><circle cx="12" cy="12" r="2"/><path d="M18 12c0 1.7-.7 3.2-1.8 4.2"/></g>"#,
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
    name: "list-ordered-15",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M11 5h10m-10 7h10m-10 7h10M4 4h1v5M4 9h2m.5 11H3.4c0-1 2.6-1.925 2.6-3.5a1.5 1.5 0 0 0-2.6-1.02"/></g>"#,
};
const ICON_QUEUE_LG: Icon = Icon {
    name: "list-ordered-175",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.75" stroke-linecap="round" stroke-linejoin="round"><path d="M11 5h10m-10 7h10m-10 7h10M4 4h1v5M4 9h2m.5 11H3.4c0-1 2.6-1.925 2.6-3.5a1.5 1.5 0 0 0-2.6-1.02"/></g>"#,
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
    name: "heart-solid",
    view: 24.0,
    body: r#"<g fill="currentColor" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M2 9.5a5.5 5.5 0 0 1 9.591-3.676a.56.56 0 0 0 .818 0A5.49 5.49 0 0 1 22 9.5c0 2.29-1.5 4-3 5.5l-5.492 5.313a2 2 0 0 1-3 .019L5 15c-1.5-1.5-3-3.2-3-5.5"/></g>"#,
};

pub fn icon_heart(size: f32, color: Color) -> El {
    icon(&ICON_HEART_SOLID, size).color(color)
}

const ICON_GALLERY_SM: Icon = Icon {
    name: "images-15",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="m22 11l-1.296-1.296a2.4 2.4 0 0 0-3.408 0L11 16"/><path d="M4 8a2 2 0 0 0-2 2v10a2 2 0 0 0 2 2h10a2 2 0 0 0 2-2"/><circle cx="13" cy="7" r="1" fill="currentColor"/><rect width="14" height="14" x="8" y="2" rx="2"/></g>"#,
};
const ICON_GALLERY_LG: Icon = Icon {
    name: "images-175",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.75" stroke-linecap="round" stroke-linejoin="round"><path d="m22 11l-1.296-1.296a2.4 2.4 0 0 0-3.408 0L11 16"/><path d="M4 8a2 2 0 0 0-2 2v10a2 2 0 0 0 2 2h10a2 2 0 0 0 2-2"/><circle cx="13" cy="7" r="1" fill="currentColor"/><rect width="14" height="14" x="8" y="2" rx="2"/></g>"#,
};

pub fn icon_gallery(size: f32, color: Color) -> El {
    let glyph = if size < 24.0 {
        &ICON_GALLERY_SM
    } else {
        &ICON_GALLERY_LG
    };
    icon(glyph, size).color(color)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn every_lucide_icon_rasterizes_non_empty_coverage() {
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
            &ICON_PAUSE_SOLID,
            &ICON_SKIP_BACK_SOLID,
            &ICON_SKIP_FORWARD_SOLID,
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
        ];
        for glyph in all {
            let cov = rune::text::icon_coverage(glyph, 24);
            let lit = cov.iter().filter(|&&a| a > 0).count();
            assert!(lit > 0, "icon {} rendered empty coverage", glyph.name);
        }
    }
}
