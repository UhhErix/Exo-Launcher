use crate::tokens::{
    glass_bar_tint, glass_panel_tint, glass_still_control_fill, glass_still_panel_fill,
};
use rune::{Color, Cx, El, GlassPolicy, GlassTier, LiquidGlass};
use std::sync::atomic::{AtomicU8, Ordering};

pub const EXO_GLASS_POLICY: GlassPolicy = GlassPolicy::Auto;
pub const GLASS_DOWNGRADE_STREAK: u32 = 6;

const FLAG_REDUCE_TRANSPARENCY: u8 = 1 << 0;
const FLAG_WINDOWS_TRANSPARENCY_OFF: u8 = 1 << 1;
const FLAG_INCREASE_CONTRAST: u8 = 1 << 2;
const FLAG_REDUCED_MOTION: u8 = 1 << 3;

static GLASS_A11Y_FLAGS: AtomicU8 = AtomicU8::new(0);

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct GlassAccessibility {
    pub reduce_transparency: bool,
    pub windows_transparency_effects: bool,
    pub increase_contrast: bool,
    pub reduced_motion: bool,
}

impl Default for GlassAccessibility {
    fn default() -> Self {
        Self {
            reduce_transparency: false,
            windows_transparency_effects: true,
            increase_contrast: false,
            reduced_motion: false,
        }
    }
}

impl GlassAccessibility {
    pub fn from_cx<S>(cx: &Cx<S>, app_reduce_motion: bool) -> Self {
        let reduced_transparency = cx.system_reduced_transparency();
        Self {
            reduce_transparency: reduced_transparency,
            windows_transparency_effects: !reduced_transparency,
            increase_contrast: cx.system_increased_contrast() || cx.system_contrast().is_some(),
            reduced_motion: app_reduce_motion || cx.is_reduced_motion(),
        }
    }

    pub fn allows_transparency(&self) -> bool {
        !self.reduce_transparency && self.windows_transparency_effects && !self.increase_contrast
    }

    pub fn allows_interactive_motion(&self) -> bool {
        !self.reduced_motion
    }

    fn to_bits(self) -> u8 {
        let mut bits = 0u8;
        if self.reduce_transparency {
            bits |= FLAG_REDUCE_TRANSPARENCY;
        }
        if !self.windows_transparency_effects {
            bits |= FLAG_WINDOWS_TRANSPARENCY_OFF;
        }
        if self.increase_contrast {
            bits |= FLAG_INCREASE_CONTRAST;
        }
        if self.reduced_motion {
            bits |= FLAG_REDUCED_MOTION;
        }
        bits
    }

    fn from_bits(bits: u8) -> Self {
        Self {
            reduce_transparency: (bits & FLAG_REDUCE_TRANSPARENCY) != 0,
            windows_transparency_effects: (bits & FLAG_WINDOWS_TRANSPARENCY_OFF) == 0,
            increase_contrast: (bits & FLAG_INCREASE_CONTRAST) != 0,
            reduced_motion: (bits & FLAG_REDUCED_MOTION) != 0,
        }
    }
}

pub fn set_glass_accessibility(a11y: GlassAccessibility) {
    GLASS_A11Y_FLAGS.store(a11y.to_bits(), Ordering::Relaxed);
}

pub fn glass_accessibility() -> GlassAccessibility {
    GlassAccessibility::from_bits(GLASS_A11Y_FLAGS.load(Ordering::Relaxed))
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum ExoGlassVariant {
    Regular,
    Clear,
    Tinted,
}

#[derive(Clone, Copy, Debug, PartialEq)]
pub enum ExoGlass {
    Bar,
    Menu,
    Popover,
    Panel,
    Sheet,
    Notice,
    Control,
    ClearMedia,
    Tinted(Color),
}

impl ExoGlass {
    pub fn variant(self) -> ExoGlassVariant {
        match self {
            Self::Bar
            | Self::Menu
            | Self::Popover
            | Self::Panel
            | Self::Sheet
            | Self::Notice
            | Self::Control => ExoGlassVariant::Regular,
            Self::ClearMedia => ExoGlassVariant::Clear,
            Self::Tinted(_) => ExoGlassVariant::Tinted,
        }
    }

    pub fn still_fill(self) -> Color {
        match self {
            Self::Menu | Self::Popover | Self::Panel | Self::Sheet | Self::Notice => {
                glass_still_panel_fill()
            }
            Self::Bar | Self::Control | Self::ClearMedia => glass_still_control_fill(),
            Self::Tinted(color) => color.alpha(0.84),
        }
    }

    pub fn to_liquid_glass(self, interactive: bool, a11y: GlassAccessibility) -> LiquidGlass {
        let base = match self {
            Self::Bar | Self::Control => LiquidGlass::regular()
                .tint(glass_bar_tint())
                .still(glass_still_control_fill()),
            Self::Menu | Self::Popover | Self::Panel | Self::Sheet | Self::Notice => {
                LiquidGlass::regular()
                    .tint(glass_panel_tint())
                    .still(glass_still_panel_fill())
            }
            Self::ClearMedia => LiquidGlass::clear().still(glass_still_control_fill()),
            Self::Tinted(color) => LiquidGlass::regular().tint(color).still(color.alpha(0.84)),
        };
        if interactive && a11y.allows_interactive_motion() {
            base.interactive()
        } else if !a11y.allows_interactive_motion() {
            base.cursor_light(false)
        } else {
            base
        }
    }
}

pub fn exo_glass(surface: ExoGlass, interactive: bool) -> LiquidGlass {
    surface.to_liquid_glass(interactive, glass_accessibility())
}

pub fn apply_glass(el: El, surface: ExoGlass, interactive: bool) -> El {
    el.liquid_glass(exo_glass(surface, interactive))
}

pub fn apply_glass_opt(el: El, surface: ExoGlass, interactive: bool, enabled: bool) -> El {
    if enabled {
        apply_glass(el, surface, interactive)
    } else {
        el.fill(surface.still_fill())
    }
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum GlassDeviceKind {
    DiscreteGpu,
    IntegratedGpu,
    Cpu,
    Other,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum GlassAdapterClass {
    Discrete,
    Integrated,
    Software,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum GlassEffectiveMode {
    Full,
    BlurOnly,
    ReducedTransparency,
    HighContrast,
    Solid,
}

pub fn classify_glass_adapter(kind: GlassDeviceKind, name: &str) -> GlassAdapterClass {
    let lower = name.to_ascii_lowercase();
    if kind == GlassDeviceKind::Cpu
        || lower.contains("basic render driver")
        || lower.contains("warp")
        || lower.contains("llvmpipe")
        || lower.contains("swiftshader")
        || lower.contains("software")
    {
        return GlassAdapterClass::Software;
    }
    if kind == GlassDeviceKind::IntegratedGpu
        || lower.contains("intel(r) uhd")
        || lower.contains("intel uhd")
        || lower.contains("iris")
        || lower.contains("hd graphics")
        || lower.contains("vega")
        || lower.contains("radeon(tm) graphics")
    {
        return GlassAdapterClass::Integrated;
    }
    GlassAdapterClass::Discrete
}

pub fn decide_glass_tier(
    kind: GlassDeviceKind,
    adapter_name: &str,
    on_battery: bool,
    over_budget_streak: u32,
    current: GlassTier,
) -> GlassTier {
    let class = classify_glass_adapter(kind, adapter_name);
    let baseline = match (class, on_battery) {
        (GlassAdapterClass::Software, _) => return GlassTier::Solid,
        (GlassAdapterClass::Integrated, true) => match current {
            GlassTier::Solid => GlassTier::Solid,
            _ => GlassTier::BlurOnly,
        },
        _ => current,
    };
    if over_budget_streak >= GLASS_DOWNGRADE_STREAK {
        match baseline {
            GlassTier::Full => GlassTier::BlurOnly,
            GlassTier::BlurOnly | GlassTier::Solid => GlassTier::Solid,
        }
    } else {
        baseline
    }
}

pub fn resolve_effective_glass_mode(
    tier: GlassTier,
    a11y: GlassAccessibility,
) -> GlassEffectiveMode {
    if tier == GlassTier::Solid {
        return GlassEffectiveMode::Solid;
    }
    if a11y.increase_contrast {
        return GlassEffectiveMode::HighContrast;
    }
    if a11y.reduce_transparency || !a11y.windows_transparency_effects {
        return GlassEffectiveMode::ReducedTransparency;
    }
    match tier {
        GlassTier::Full => GlassEffectiveMode::Full,
        GlassTier::BlurOnly => GlassEffectiveMode::BlurOnly,
        GlassTier::Solid => GlassEffectiveMode::Solid,
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::tokens::{bad, good};
    use rune::render::KIND_LIQUID_GLASS;
    use rune::{Env, Runtime, el, row};

    #[test]
    fn surface_kind_maps_to_expected_variant_and_interactive_flags() {
        let normal = GlassAccessibility::default();
        assert_eq!(ExoGlass::Bar.variant(), ExoGlassVariant::Regular);
        assert_eq!(ExoGlass::Menu.variant(), ExoGlassVariant::Regular);
        assert_eq!(ExoGlass::Popover.variant(), ExoGlassVariant::Regular);
        assert_eq!(ExoGlass::Panel.variant(), ExoGlassVariant::Regular);
        assert_eq!(ExoGlass::Sheet.variant(), ExoGlassVariant::Regular);
        assert_eq!(ExoGlass::Notice.variant(), ExoGlassVariant::Regular);
        assert_eq!(ExoGlass::Control.variant(), ExoGlassVariant::Regular);
        assert_eq!(ExoGlass::ClearMedia.variant(), ExoGlassVariant::Clear);
        assert_eq!(ExoGlass::Tinted(bad()).variant(), ExoGlassVariant::Tinted);
        assert_eq!(ExoGlass::Tinted(good()).variant(), ExoGlassVariant::Tinted);

        let bar_interactive = ExoGlass::Bar.to_liquid_glass(true, normal);
        assert!(bar_interactive.is_interactive());
        assert!(!bar_interactive.is_identity());

        let panel_static = ExoGlass::Panel.to_liquid_glass(false, normal);
        assert!(!panel_static.is_interactive());

        let clear = ExoGlass::ClearMedia.to_liquid_glass(true, normal);
        assert!(clear.is_interactive());

        let tinted = ExoGlass::Tinted(bad()).to_liquid_glass(true, normal);
        assert!(tinted.is_interactive());
    }

    #[test]
    fn reduced_motion_disables_interactive_bounce_and_touch_glow() {
        let reduced = GlassAccessibility {
            reduced_motion: true,
            ..GlassAccessibility::default()
        };
        assert!(!reduced.allows_interactive_motion());
        assert!(
            !ExoGlass::Bar
                .to_liquid_glass(true, reduced)
                .is_interactive()
        );
        assert!(
            !ExoGlass::Control
                .to_liquid_glass(true, reduced)
                .is_interactive()
        );
        assert!(
            !ExoGlass::ClearMedia
                .to_liquid_glass(true, reduced)
                .is_interactive()
        );
        assert!(
            !ExoGlass::Tinted(bad())
                .to_liquid_glass(true, reduced)
                .is_interactive()
        );
    }

    #[test]
    fn hardware_downgrade_decisions_cover_discrete_igpu_battery_warp_and_streak() {
        assert_eq!(
            decide_glass_tier(
                GlassDeviceKind::DiscreteGpu,
                "NVIDIA GeForce RTX 3070",
                false,
                0,
                GlassTier::Full,
            ),
            GlassTier::Full
        );
        assert_eq!(
            decide_glass_tier(
                GlassDeviceKind::IntegratedGpu,
                "Intel(R) UHD Graphics 770",
                false,
                0,
                GlassTier::Full,
            ),
            GlassTier::Full
        );
        assert_eq!(
            decide_glass_tier(
                GlassDeviceKind::IntegratedGpu,
                "Intel(R) UHD Graphics 770",
                true,
                0,
                GlassTier::Full,
            ),
            GlassTier::BlurOnly
        );
        assert_eq!(
            decide_glass_tier(
                GlassDeviceKind::IntegratedGpu,
                "AMD Radeon(TM) Graphics",
                true,
                GLASS_DOWNGRADE_STREAK,
                GlassTier::BlurOnly,
            ),
            GlassTier::Solid
        );
        assert_eq!(
            decide_glass_tier(
                GlassDeviceKind::Cpu,
                "Microsoft Basic Render Driver",
                false,
                0,
                GlassTier::Full,
            ),
            GlassTier::Solid
        );
        assert_eq!(
            decide_glass_tier(
                GlassDeviceKind::Other,
                "WARP Software Adapter",
                false,
                0,
                GlassTier::Full,
            ),
            GlassTier::Solid
        );
        assert_eq!(
            decide_glass_tier(
                GlassDeviceKind::DiscreteGpu,
                "NVIDIA GeForce RTX 3070",
                false,
                GLASS_DOWNGRADE_STREAK,
                GlassTier::Full,
            ),
            GlassTier::BlurOnly
        );
        assert_eq!(
            decide_glass_tier(
                GlassDeviceKind::DiscreteGpu,
                "NVIDIA GeForce RTX 3070",
                false,
                GLASS_DOWNGRADE_STREAK,
                GlassTier::BlurOnly,
            ),
            GlassTier::Solid
        );
    }

    #[test]
    fn accessibility_flags_resolve_transparency_contrast_and_motion() {
        let normal = GlassAccessibility::default();
        assert!(normal.allows_transparency());
        assert!(normal.allows_interactive_motion());
        assert_eq!(
            resolve_effective_glass_mode(GlassTier::Full, normal),
            GlassEffectiveMode::Full
        );

        let rt = GlassAccessibility {
            reduce_transparency: true,
            ..normal
        };
        assert!(!rt.allows_transparency());
        assert_eq!(
            resolve_effective_glass_mode(GlassTier::Full, rt),
            GlassEffectiveMode::ReducedTransparency
        );

        let win_off = GlassAccessibility {
            windows_transparency_effects: false,
            ..normal
        };
        assert!(!win_off.allows_transparency());
        assert_eq!(
            resolve_effective_glass_mode(GlassTier::Full, win_off),
            GlassEffectiveMode::ReducedTransparency
        );

        let contrast = GlassAccessibility {
            increase_contrast: true,
            ..normal
        };
        assert!(!contrast.allows_transparency());
        assert_eq!(
            resolve_effective_glass_mode(GlassTier::Full, contrast),
            GlassEffectiveMode::HighContrast
        );

        assert_eq!(
            resolve_effective_glass_mode(GlassTier::Solid, normal),
            GlassEffectiveMode::Solid
        );
    }

    #[test]
    fn runtime_honors_auto_policy_a11y_flags_and_container_merging() {
        let view = |_: &(), cx: &mut Cx<()>| {
            let a11y = GlassAccessibility::from_cx(cx, false);
            set_glass_accessibility(a11y);
            row()
                .key("bar")
                .size(400.0, 44.0)
                .gap(8.0)
                .glass_container(10.0)
                .child(apply_glass(
                    el().key("pill-a").size(120.0, 36.0).hit(),
                    ExoGlass::Bar,
                    true,
                ))
                .child(apply_glass(
                    el().key("pill-b").size(120.0, 36.0).hit(),
                    ExoGlass::Bar,
                    true,
                ))
        };

        let mut rt = Runtime::new((), view, (800.0, 600.0), Env::new(true, 1.0));
        rt.set_glass_policy(EXO_GLASS_POLICY);
        rt.frame(0.0);

        let lg_draws = rt
            .instances
            .iter()
            .filter(|i| i.params[1] == KIND_LIQUID_GLASS)
            .count();
        assert_eq!(lg_draws, 1);
        assert_eq!(rt.liquid_glass_frame().shapes.len(), 2);
        assert!(rt.glass_on());

        rt.set_system_reduced_transparency(true);
        rt.frame(1.0 / 60.0);
        assert!(!rt.glass_on());
        let sh = rt.liquid_glass_frame().shapes[0];
        assert_ne!((sh.light[1] as u32) & 16, 0);

        rt.set_system_reduced_transparency(false);
        rt.set_system_increased_contrast(true);
        rt.frame(2.0 / 60.0);
        assert!(!rt.glass_on());
        let sh_ic = rt.liquid_glass_frame().shapes[0];
        assert_ne!((sh_ic.light[1] as u32) & 32, 0);

        rt.set_system_increased_contrast(false);
        rt.set_system_reduced_motion(true);
        rt.frame(3.0 / 60.0);
        assert!(rt.glass_on());
        assert!(!glass_accessibility().allows_interactive_motion());
        let (px, py) = rt.find("pill-a").expect("pill-a");
        rt.pointer_move(px, py);
        rt.pointer_down(px, py);
        rt.frame(4.0 / 60.0);
        rt.frame(5.0 / 60.0);
        let sh_rm = rt.liquid_glass_frame().shapes[0];
        assert_eq!(sh_rm.glow[3], 0.0);

        rt.pointer_up(px, py);
        rt.set_glass_tier_override(Some(GlassTier::Solid));
        rt.frame(6.0 / 60.0);
        assert_eq!(rt.glass_tier(), GlassTier::Solid);
        assert!(!rt.glass_on());
        assert!(rt.liquid_glass_frame().shapes.is_empty());
    }
}
