use crate::tokens::{BG, glass_still_control_fill, glass_still_panel_fill};
use rune::{Color, El, GlassPolicy, LiquidGlass};

pub const EXO_GLASS_POLICY: GlassPolicy = GlassPolicy::Glass;

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
    pub fn still_fill(self) -> Color {
        match self {
            Self::Menu | Self::Popover | Self::Panel | Self::Sheet | Self::Notice => {
                glass_still_panel_fill()
            }
            Self::Bar | Self::Control | Self::ClearMedia => glass_still_control_fill(),
            Self::Tinted(color) => color.alpha(0.84),
        }
    }

    pub fn to_liquid_glass(self) -> LiquidGlass {
        const LIGHT_ANGLE: f32 = -std::f32::consts::FRAC_PI_4 * 3.0;
        let base = match self {
            Self::Bar => LiquidGlass::clear()
                .frost(4.5)
                .saturation(1.70)
                .refraction(2.0)
                .bevel(14.0)
                .dispersion(0.050)
                .rim(0.32, 0.11, 0.14)
                .specular(1.30, LIGHT_ANGLE)
                .tint(BG.alpha(0.44))
                .still(self.still_fill()),
            Self::Control => LiquidGlass::clear()
                .frost(4.0)
                .saturation(1.70)
                .refraction(2.0)
                .bevel(13.0)
                .dispersion(0.050)
                .rim(0.30, 0.10, 0.12)
                .specular(1.25, LIGHT_ANGLE)
                .tint(BG.alpha(0.46))
                .still(self.still_fill()),
            Self::Menu | Self::Popover => LiquidGlass::clear()
                .frost(14.0)
                .saturation(1.65)
                .refraction(1.8)
                .bevel(16.0)
                .dispersion(0.045)
                .rim(0.28, 0.10, 0.12)
                .specular(1.20, LIGHT_ANGLE)
                .tint(BG.alpha(0.76))
                .still(self.still_fill()),
            Self::Panel | Self::Sheet | Self::Notice => LiquidGlass::clear()
                .frost(16.0)
                .saturation(1.60)
                .refraction(1.8)
                .bevel(18.0)
                .dispersion(0.040)
                .rim(0.26, 0.09, 0.11)
                .specular(1.18, LIGHT_ANGLE)
                .tint(BG.alpha(0.82))
                .still(self.still_fill()),
            Self::ClearMedia => LiquidGlass::clear()
                .frost(2.5)
                .saturation(1.80)
                .refraction(2.2)
                .bevel(13.0)
                .dispersion(0.055)
                .rim(0.36, 0.11, 0.15)
                .specular(1.35, LIGHT_ANGLE)
                .tint(BG.alpha(0.38))
                .still(self.still_fill()),
            Self::Tinted(color) => LiquidGlass::clear()
                .frost(5.0)
                .saturation(1.55)
                .refraction(2.0)
                .bevel(13.0)
                .dispersion(0.050)
                .rim(0.28, 0.10, 0.12)
                .specular(1.20, LIGHT_ANGLE)
                .tint(color)
                .still(color.alpha(0.84)),
        };
        base.cursor_light(false).shadow(false)
    }
}

pub fn exo_glass(surface: ExoGlass) -> LiquidGlass {
    surface.to_liquid_glass()
}

pub fn apply_glass(el: El, surface: ExoGlass) -> El {
    el.liquid_glass(exo_glass(surface))
}

pub fn apply_glass_opt(el: El, surface: ExoGlass, enabled: bool) -> El {
    if enabled {
        apply_glass(el, surface)
    } else {
        el.fill(surface.still_fill())
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::tokens::bad;
    use rune::render::KIND_LIQUID_GLASS;
    use rune::{Cx, Env, GlassTier, Runtime, el, row};

    #[test]
    fn no_surface_follows_the_cursor() {
        for surface in [
            ExoGlass::Bar,
            ExoGlass::Menu,
            ExoGlass::Popover,
            ExoGlass::Panel,
            ExoGlass::Sheet,
            ExoGlass::Notice,
            ExoGlass::Control,
            ExoGlass::ClearMedia,
            ExoGlass::Tinted(bad()),
        ] {
            let glass = surface.to_liquid_glass();
            assert!(!glass.is_interactive(), "{surface:?}");
            assert!(!glass.is_identity(), "{surface:?}");
        }
    }

    #[test]
    fn runtime_keeps_glass_without_transparency_effects_and_goes_solid_in_contrast_themes() {
        let view = |_: &(), _: &mut Cx<()>| {
            row()
                .key("bar")
                .size(400.0, 44.0)
                .gap(8.0)
                .glass_container(10.0)
                .child(apply_glass(
                    el().key("pill-a").size(120.0, 36.0).hit(),
                    ExoGlass::Bar,
                ))
                .child(apply_glass(
                    el().key("pill-b").size(120.0, 36.0).hit(),
                    ExoGlass::Bar,
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
        assert!(rt.glass_on());
        assert_eq!(rt.liquid_glass_frame().shapes.len(), 2);

        let night_sky = rune::SystemContrast {
            window: Color::BLACK,
            text: Color::BLACK,
            highlight: Color::BLACK,
            highlight_text: Color::BLACK,
            disabled: Color::BLACK,
            button: Color::BLACK,
            button_text: Color::BLACK,
            link: Color::BLACK,
        };
        rt.set_system_reduced_transparency(false);
        rt.set_system_contrast(Some(night_sky));
        rt.set_system_increased_contrast(true);
        rt.frame(2.0 / 60.0);
        assert!(!rt.glass_on());

        rt.set_system_contrast(None);
        rt.set_system_increased_contrast(false);
        rt.set_system_reduced_motion(true);
        rt.frame(3.0 / 60.0);
        assert!(rt.glass_on());
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
