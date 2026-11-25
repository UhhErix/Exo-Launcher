use core::f64::consts::{FRAC_1_SQRT_2, PI};
use serde::{Deserialize, Serialize};

pub const MAX_BANDS: usize = 16;
pub const MAX_CHANNELS: usize = 8;
pub const MIN_FREQ_HZ: f64 = 20.0;
pub const MAX_FREQ_HZ: f64 = 20_000.0;
pub const MIN_GAIN_DB: f64 = -24.0;
pub const MAX_GAIN_DB: f64 = 12.0;
pub const MIN_Q: f64 = 0.1;
pub const MAX_Q: f64 = 10.0;
pub const HEADROOM_MARGIN_DB: f64 = 0.5;
pub const HARD_LIMITER_CEILING_DBFS: f64 = -0.5;
pub const HARD_LIMITER_CEILING_LINEAR: f64 = 0.944_060_876_285_923_4;
const SOFT_KNEE_START_LINEAR: f64 = 0.794_328_234_724_281_5;
const DENORMAL_EPS: f64 = 1.0e-24;
const CROSSFADE_MS: f64 = 10.0;
const MAX_ITD_SAMPLES: usize = 128;

#[repr(u8)]
#[derive(Debug, Clone, Copy, Default, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub enum FilterKind {
    #[default]
    Off = 0,
    Peak = 1,
    LowShelf = 2,
    HighShelf = 3,
    LowPass = 4,
    HighPass = 5,
}

impl FilterKind {
    pub const fn from_u8(v: u8) -> Self {
        match v {
            1 => Self::Peak,
            2 => Self::LowShelf,
            3 => Self::HighShelf,
            4 => Self::LowPass,
            5 => Self::HighPass,
            _ => Self::Off,
        }
    }
}

#[repr(u8)]
#[derive(Debug, Clone, Copy, Default, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub enum SpatialMode {
    #[default]
    Off = 0,
    Crossfeed = 1,
    HrtfCompetitive = 2,
}

impl SpatialMode {
    pub const fn from_u8(v: u8) -> Self {
        match v {
            1 => Self::Crossfeed,
            2 => Self::HrtfCompetitive,
            _ => Self::Off,
        }
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ParametricBand {
    pub kind: FilterKind,
    pub freq_hz: f64,
    pub gain_db: f64,
    pub q: f64,
}

impl Default for ParametricBand {
    fn default() -> Self {
        Self {
            kind: FilterKind::Off,
            freq_hz: 1000.0,
            gain_db: 0.0,
            q: FRAC_1_SQRT_2,
        }
    }
}

impl ParametricBand {
    pub const fn off() -> Self {
        Self {
            kind: FilterKind::Off,
            freq_hz: 1000.0,
            gain_db: 0.0,
            q: FRAC_1_SQRT_2,
        }
    }

    pub const fn peak(freq_hz: f64, gain_db: f64, q: f64) -> Self {
        Self {
            kind: FilterKind::Peak,
            freq_hz,
            gain_db,
            q,
        }
    }

    pub const fn low_shelf(freq_hz: f64, gain_db: f64, q: f64) -> Self {
        Self {
            kind: FilterKind::LowShelf,
            freq_hz,
            gain_db,
            q,
        }
    }

    pub const fn high_shelf(freq_hz: f64, gain_db: f64, q: f64) -> Self {
        Self {
            kind: FilterKind::HighShelf,
            freq_hz,
            gain_db,
            q,
        }
    }

    pub const fn low_pass(freq_hz: f64, q: f64) -> Self {
        Self {
            kind: FilterKind::LowPass,
            freq_hz,
            gain_db: 0.0,
            q,
        }
    }

    pub const fn high_pass(freq_hz: f64, q: f64) -> Self {
        Self {
            kind: FilterKind::HighPass,
            freq_hz,
            gain_db: 0.0,
            q,
        }
    }

    pub fn clamped(self, sample_rate: f64) -> Self {
        let nyquist_cap = (sample_rate * 0.495).clamp(MIN_FREQ_HZ, MAX_FREQ_HZ);
        let freq_hz = if self.freq_hz.is_finite() {
            self.freq_hz.clamp(MIN_FREQ_HZ, nyquist_cap)
        } else {
            1000.0
        };
        let gain_db = if self.gain_db.is_finite() {
            self.gain_db.clamp(MIN_GAIN_DB, MAX_GAIN_DB)
        } else {
            0.0
        };
        let q = if self.q.is_finite() {
            self.q.clamp(MIN_Q, MAX_Q)
        } else {
            FRAC_1_SQRT_2
        };
        Self {
            kind: self.kind,
            freq_hz,
            gain_db,
            q,
        }
    }
}

#[derive(Debug, Clone, Copy, PartialEq)]
pub struct DspConfig {
    pub bypass: bool,
    pub preamp_db: f64,
    pub auto_preamp: bool,
    pub limiter_enabled: bool,
    pub spatial_mode: SpatialMode,
    pub spatial_strength: f64,
    pub band_count: usize,
    pub bands: [ParametricBand; MAX_BANDS],
}

impl Default for DspConfig {
    fn default() -> Self {
        Self {
            bypass: false,
            preamp_db: 0.0,
            auto_preamp: true,
            limiter_enabled: true,
            spatial_mode: SpatialMode::Off,
            spatial_strength: 0.5,
            band_count: 0,
            bands: [ParametricBand::off(); MAX_BANDS],
        }
    }
}

impl DspConfig {
    pub fn from_bands(bands: &[ParametricBand]) -> Self {
        let mut cfg = Self::default();
        let count = bands.len().min(MAX_BANDS);
        cfg.band_count = count;
        cfg.bands[..count].copy_from_slice(&bands[..count]);
        cfg
    }

    pub fn active_bands(&self) -> &[ParametricBand] {
        &self.bands[..self.band_count.min(MAX_BANDS)]
    }
}

#[repr(C, align(32))]
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct BiquadCoeffs {
    pub b0: f64,
    pub b1: f64,
    pub b2: f64,
    pub a1: f64,
    pub a2: f64,
}

impl Default for BiquadCoeffs {
    fn default() -> Self {
        Self::identity()
    }
}

impl BiquadCoeffs {
    pub const fn identity() -> Self {
        Self {
            b0: 1.0,
            b1: 0.0,
            b2: 0.0,
            a1: 0.0,
            a2: 0.0,
        }
    }

    pub fn from_band(band: ParametricBand, sample_rate: f64) -> Self {
        let fs = if sample_rate.is_finite() && sample_rate >= 8_000.0 {
            sample_rate
        } else {
            48_000.0
        };
        let b = band.clamped(fs);
        if b.kind == FilterKind::Off {
            return Self::identity();
        }
        if matches!(
            b.kind,
            FilterKind::Peak | FilterKind::LowShelf | FilterKind::HighShelf
        ) && b.gain_db.abs() < 1.0e-6
        {
            return Self::identity();
        }

        let w0 = 2.0 * PI * b.freq_hz / fs;
        let cos_w0 = w0.cos();
        let sin_w0 = w0.sin();
        let alpha = sin_w0 / (2.0 * b.q);
        let a = 10.0_f64.powf(b.gain_db / 40.0);
        let two_sqrt_a_alpha = 2.0 * a.sqrt() * alpha;

        let (b0, b1, b2, a0, a1, a2) = match b.kind {
            FilterKind::Off => return Self::identity(),
            FilterKind::Peak => (
                1.0 + alpha * a,
                -2.0 * cos_w0,
                1.0 - alpha * a,
                1.0 + alpha / a,
                -2.0 * cos_w0,
                1.0 - alpha / a,
            ),
            FilterKind::LowShelf => (
                a * ((a + 1.0) - (a - 1.0) * cos_w0 + two_sqrt_a_alpha),
                2.0 * a * ((a - 1.0) - (a + 1.0) * cos_w0),
                a * ((a + 1.0) - (a - 1.0) * cos_w0 - two_sqrt_a_alpha),
                (a + 1.0) + (a - 1.0) * cos_w0 + two_sqrt_a_alpha,
                -2.0 * ((a - 1.0) + (a + 1.0) * cos_w0),
                (a + 1.0) + (a - 1.0) * cos_w0 - two_sqrt_a_alpha,
            ),
            FilterKind::HighShelf => (
                a * ((a + 1.0) + (a - 1.0) * cos_w0 + two_sqrt_a_alpha),
                -2.0 * a * ((a - 1.0) + (a + 1.0) * cos_w0),
                a * ((a + 1.0) + (a - 1.0) * cos_w0 - two_sqrt_a_alpha),
                (a + 1.0) - (a - 1.0) * cos_w0 + two_sqrt_a_alpha,
                2.0 * ((a - 1.0) - (a + 1.0) * cos_w0),
                (a + 1.0) - (a - 1.0) * cos_w0 - two_sqrt_a_alpha,
            ),
            FilterKind::LowPass => (
                (1.0 - cos_w0) * 0.5,
                1.0 - cos_w0,
                (1.0 - cos_w0) * 0.5,
                1.0 + alpha,
                -2.0 * cos_w0,
                1.0 - alpha,
            ),
            FilterKind::HighPass => (
                (1.0 + cos_w0) * 0.5,
                -(1.0 + cos_w0),
                (1.0 + cos_w0) * 0.5,
                1.0 + alpha,
                -2.0 * cos_w0,
                1.0 - alpha,
            ),
        };

        if !a0.is_finite() || a0.abs() < 1.0e-15 {
            return Self::identity();
        }
        let inv_a0 = 1.0 / a0;
        Self {
            b0: b0 * inv_a0,
            b1: b1 * inv_a0,
            b2: b2 * inv_a0,
            a1: a1 * inv_a0,
            a2: a2 * inv_a0,
        }
    }

    pub fn magnitude_db(&self, freq_hz: f64, sample_rate: f64) -> f64 {
        let w = 2.0 * PI * freq_hz / sample_rate;
        let cos1 = w.cos();
        let sin1 = -w.sin();
        let cos2 = (2.0 * w).cos();
        let sin2 = -(2.0 * w).sin();

        let num_re = self.b0 + self.b1 * cos1 + self.b2 * cos2;
        let num_im = self.b1 * sin1 + self.b2 * sin2;
        let den_re = 1.0 + self.a1 * cos1 + self.a2 * cos2;
        let den_im = self.a1 * sin1 + self.a2 * sin2;

        let num_sq = num_re * num_re + num_im * num_im;
        let den_sq = (den_re * den_re + den_im * den_im).max(1.0e-30);
        10.0 * (num_sq / den_sq).max(1.0e-18).log10()
    }
}

pub fn frequency_response_db(bands: &[ParametricBand], freq_hz: f64, sample_rate: f64) -> f64 {
    let mut total = 0.0;
    for &band in bands.iter().take(MAX_BANDS) {
        if band.kind == FilterKind::Off {
            continue;
        }
        let c = BiquadCoeffs::from_band(band, sample_rate);
        total += c.magnitude_db(freq_hz, sample_rate);
    }
    total
}

pub fn peak_boost_db(bands: &[ParametricBand], sample_rate: f64) -> f64 {
    if bands.is_empty() {
        return 0.0;
    }
    let mut coeffs = [BiquadCoeffs::identity(); MAX_BANDS];
    let mut n = 0usize;
    for &band in bands.iter().take(MAX_BANDS) {
        if band.kind != FilterKind::Off {
            coeffs[n] = BiquadCoeffs::from_band(band, sample_rate);
            n += 1;
        }
    }
    if n == 0 {
        return 0.0;
    }
    let mut peak = f64::NEG_INFINITY;
    for i in 0..=240 {
        let hz = 20.0 * 1000.0_f64.powf(i as f64 / 240.0);
        let mut db = 0.0;
        for c in &coeffs[..n] {
            db += c.magnitude_db(hz, sample_rate);
        }
        if db > peak {
            peak = db;
        }
    }
    peak
}

pub fn compute_auto_preamp_db(bands: &[ParametricBand], sample_rate: f64) -> f64 {
    let peak = peak_boost_db(bands, sample_rate);
    if !peak.is_finite() || peak <= 0.005 {
        0.0
    } else {
        let rounded = (peak * 1_000_000.0).round() / 1_000_000.0;
        -((rounded * 10.0).ceil() / 10.0 + HEADROOM_MARGIN_DB)
    }
}

#[inline]
pub fn db_to_linear(db: f64) -> f64 {
    10.0_f64.powf(db / 20.0)
}

#[inline]
pub fn linear_to_db(lin: f64) -> f64 {
    20.0 * lin.max(1.0e-12).log10()
}

#[repr(C, align(32))]
#[derive(Clone, Copy)]
struct FilterBank {
    active_count: usize,
    preamp_linear: f64,
    coeffs: [BiquadCoeffs; MAX_BANDS],
    z1: [[f64; MAX_CHANNELS]; MAX_BANDS],
    z2: [[f64; MAX_CHANNELS]; MAX_BANDS],
}

impl FilterBank {
    const fn new() -> Self {
        Self {
            active_count: 0,
            preamp_linear: 1.0,
            coeffs: [BiquadCoeffs::identity(); MAX_BANDS],
            z1: [[0.0; MAX_CHANNELS]; MAX_BANDS],
            z2: [[0.0; MAX_CHANNELS]; MAX_BANDS],
        }
    }

    fn reset_state(&mut self) {
        self.z1 = [[0.0; MAX_CHANNELS]; MAX_BANDS];
        self.z2 = [[0.0; MAX_CHANNELS]; MAX_BANDS];
    }

    fn configure(&mut self, cfg: &DspConfig, sample_rate: f64, reset_delay: bool) {
        let mut n = 0usize;
        if !cfg.bypass {
            for &band in cfg.active_bands() {
                if band.kind == FilterKind::Off {
                    continue;
                }
                let c = BiquadCoeffs::from_band(band, sample_rate);
                if (c.b0 - 1.0).abs() > 1.0e-12
                    || c.b1.abs() > 1.0e-12
                    || c.b2.abs() > 1.0e-12
                    || c.a1.abs() > 1.0e-12
                    || c.a2.abs() > 1.0e-12
                {
                    self.coeffs[n] = c;
                    n += 1;
                }
            }
        }
        self.active_count = n;
        for c in &mut self.coeffs[n..MAX_BANDS] {
            *c = BiquadCoeffs::identity();
        }
        let manual_preamp = if cfg.preamp_db.is_finite() {
            cfg.preamp_db.clamp(-24.0, 0.0)
        } else {
            0.0
        };
        let auto_preamp = if cfg.auto_preamp && !cfg.bypass {
            compute_auto_preamp_db(cfg.active_bands(), sample_rate)
        } else {
            0.0
        };
        let total_preamp_db = if cfg.bypass {
            0.0
        } else {
            manual_preamp.min(auto_preamp)
        };
        self.preamp_linear = db_to_linear(total_preamp_db);
        if reset_delay {
            self.reset_state();
        }
    }

    #[inline]
    fn process_stereo_sample_scalar(&mut self, l: f64, r: f64) -> (f64, f64) {
        let mut xl = l * self.preamp_linear;
        let mut xr = r * self.preamp_linear;
        let count = self.active_count;
        for i in 0..count {
            let c = self.coeffs[i];
            let z1l = self.z1[i][0];
            let z2l = self.z2[i][0];
            let yl = c.b0 * xl + z1l;
            let nz1l = c.b1 * xl - c.a1 * yl + z2l;
            let nz2l = c.b2 * xl - c.a2 * yl;
            self.z1[i][0] = if nz1l.abs() < DENORMAL_EPS { 0.0 } else { nz1l };
            self.z2[i][0] = if nz2l.abs() < DENORMAL_EPS { 0.0 } else { nz2l };
            xl = yl;

            let z1r = self.z1[i][1];
            let z2r = self.z2[i][1];
            let yr = c.b0 * xr + z1r;
            let nz1r = c.b1 * xr - c.a1 * yr + z2r;
            let nz2r = c.b2 * xr - c.a2 * yr;
            self.z1[i][1] = if nz1r.abs() < DENORMAL_EPS { 0.0 } else { nz1r };
            self.z2[i][1] = if nz2r.abs() < DENORMAL_EPS { 0.0 } else { nz2r };
            xr = yr;
        }
        (xl, xr)
    }

    #[cfg(target_arch = "x86_64")]
    #[target_feature(enable = "avx2", enable = "fma")]
    unsafe fn process_stereo_chunk_avx2_fma(&mut self, stereo_f64: &mut [[f64; 2]]) {
        use core::arch::x86_64::*;
        unsafe {
            let count = self.active_count;
            let preamp = _mm_set1_pd(self.preamp_linear);
            let has_pre = (self.preamp_linear - 1.0).abs() > 1.0e-12;
            if count == 0 {
                if has_pre {
                    for frame in stereo_f64.iter_mut() {
                        let x = _mm_mul_pd(_mm_loadu_pd(frame.as_ptr()), preamp);
                        _mm_storeu_pd(frame.as_mut_ptr(), x);
                    }
                }
                return;
            }

            let mut i = 0usize;
            while i + 2 <= count {
                let c0 = self.coeffs[i];
                let c1 = self.coeffs[i + 1];
                let b0_0 = _mm_set1_pd(c0.b0);
                let b1_0 = _mm_set1_pd(c0.b1);
                let b2_0 = _mm_set1_pd(c0.b2);
                let a1_0 = _mm_set1_pd(c0.a1);
                let a2_0 = _mm_set1_pd(c0.a2);
                let mut z1_0 = _mm_loadu_pd(self.z1[i].as_ptr());
                let mut z2_0 = _mm_loadu_pd(self.z2[i].as_ptr());

                let b0_1 = _mm_set1_pd(c1.b0);
                let b1_1 = _mm_set1_pd(c1.b1);
                let b2_1 = _mm_set1_pd(c1.b2);
                let a1_1 = _mm_set1_pd(c1.a1);
                let a2_1 = _mm_set1_pd(c1.a2);
                let mut z1_1 = _mm_loadu_pd(self.z1[i + 1].as_ptr());
                let mut z2_1 = _mm_loadu_pd(self.z2[i + 1].as_ptr());

                if i == 0 && has_pre {
                    for frame in stereo_f64.iter_mut() {
                        let x0 = _mm_mul_pd(_mm_loadu_pd(frame.as_ptr()), preamp);
                        let y0 = _mm_fmadd_pd(b0_0, x0, z1_0);
                        z1_0 = _mm_fnmadd_pd(a1_0, y0, _mm_fmadd_pd(b1_0, x0, z2_0));
                        z2_0 = _mm_fnmadd_pd(a2_0, y0, _mm_mul_pd(b2_0, x0));

                        let y1 = _mm_fmadd_pd(b0_1, y0, z1_1);
                        z1_1 = _mm_fnmadd_pd(a1_1, y1, _mm_fmadd_pd(b1_1, y0, z2_1));
                        z2_1 = _mm_fnmadd_pd(a2_1, y1, _mm_mul_pd(b2_1, y0));
                        _mm_storeu_pd(frame.as_mut_ptr(), y1);
                    }
                } else {
                    for frame in stereo_f64.iter_mut() {
                        let x0 = _mm_loadu_pd(frame.as_ptr());
                        let y0 = _mm_fmadd_pd(b0_0, x0, z1_0);
                        z1_0 = _mm_fnmadd_pd(a1_0, y0, _mm_fmadd_pd(b1_0, x0, z2_0));
                        z2_0 = _mm_fnmadd_pd(a2_0, y0, _mm_mul_pd(b2_0, x0));

                        let y1 = _mm_fmadd_pd(b0_1, y0, z1_1);
                        z1_1 = _mm_fnmadd_pd(a1_1, y1, _mm_fmadd_pd(b1_1, y0, z2_1));
                        z2_1 = _mm_fnmadd_pd(a2_1, y1, _mm_mul_pd(b2_1, y0));
                        _mm_storeu_pd(frame.as_mut_ptr(), y1);
                    }
                }

                let mut s = [0.0_f64; 2];
                _mm_storeu_pd(s.as_mut_ptr(), z1_0);
                self.z1[i][0] = flush_denormal(s[0]);
                self.z1[i][1] = flush_denormal(s[1]);
                _mm_storeu_pd(s.as_mut_ptr(), z2_0);
                self.z2[i][0] = flush_denormal(s[0]);
                self.z2[i][1] = flush_denormal(s[1]);
                _mm_storeu_pd(s.as_mut_ptr(), z1_1);
                self.z1[i + 1][0] = flush_denormal(s[0]);
                self.z1[i + 1][1] = flush_denormal(s[1]);
                _mm_storeu_pd(s.as_mut_ptr(), z2_1);
                self.z2[i + 1][0] = flush_denormal(s[0]);
                self.z2[i + 1][1] = flush_denormal(s[1]);
                i += 2;
            }

            if i < count {
                let c = self.coeffs[i];
                let b0 = _mm_set1_pd(c.b0);
                let b1 = _mm_set1_pd(c.b1);
                let b2 = _mm_set1_pd(c.b2);
                let a1 = _mm_set1_pd(c.a1);
                let a2 = _mm_set1_pd(c.a2);
                let mut z1 = _mm_loadu_pd(self.z1[i].as_ptr());
                let mut z2 = _mm_loadu_pd(self.z2[i].as_ptr());

                for frame in stereo_f64.iter_mut() {
                    let raw = _mm_loadu_pd(frame.as_ptr());
                    let x = if i == 0 && has_pre {
                        _mm_mul_pd(raw, preamp)
                    } else {
                        raw
                    };
                    let y = _mm_fmadd_pd(b0, x, z1);
                    z1 = _mm_fnmadd_pd(a1, y, _mm_fmadd_pd(b1, x, z2));
                    z2 = _mm_fnmadd_pd(a2, y, _mm_mul_pd(b2, x));
                    _mm_storeu_pd(frame.as_mut_ptr(), y);
                }

                let mut s1 = [0.0_f64; 2];
                let mut s2 = [0.0_f64; 2];
                _mm_storeu_pd(s1.as_mut_ptr(), z1);
                _mm_storeu_pd(s2.as_mut_ptr(), z2);
                self.z1[i][0] = flush_denormal(s1[0]);
                self.z1[i][1] = flush_denormal(s1[1]);
                self.z2[i][0] = flush_denormal(s2[0]);
                self.z2[i][1] = flush_denormal(s2[1]);
            }
        }
    }

    #[cfg(target_arch = "x86_64")]
    #[target_feature(enable = "sse2")]
    unsafe fn process_stereo_chunk_sse2(&mut self, stereo_f64: &mut [[f64; 2]]) {
        use core::arch::x86_64::*;
        unsafe {
            let preamp = _mm_set1_pd(self.preamp_linear);
            for frame in stereo_f64.iter_mut() {
                let x = _mm_mul_pd(_mm_loadu_pd(frame.as_ptr()), preamp);
                _mm_storeu_pd(frame.as_mut_ptr(), x);
            }
            let count = self.active_count;
            for i in 0..count {
                let c = self.coeffs[i];
                let b0 = _mm_set1_pd(c.b0);
                let b1 = _mm_set1_pd(c.b1);
                let b2 = _mm_set1_pd(c.b2);
                let a1 = _mm_set1_pd(c.a1);
                let a2 = _mm_set1_pd(c.a2);
                let mut z1 = _mm_loadu_pd(self.z1[i].as_ptr());
                let mut z2 = _mm_loadu_pd(self.z2[i].as_ptr());

                for frame in stereo_f64.iter_mut() {
                    let x = _mm_loadu_pd(frame.as_ptr());
                    let y = _mm_add_pd(_mm_mul_pd(b0, x), z1);
                    z1 = _mm_add_pd(_mm_sub_pd(_mm_mul_pd(b1, x), _mm_mul_pd(a1, y)), z2);
                    z2 = _mm_sub_pd(_mm_mul_pd(b2, x), _mm_mul_pd(a2, y));
                    _mm_storeu_pd(frame.as_mut_ptr(), y);
                }

                let mut s1 = [0.0_f64; 2];
                let mut s2 = [0.0_f64; 2];
                _mm_storeu_pd(s1.as_mut_ptr(), z1);
                _mm_storeu_pd(s2.as_mut_ptr(), z2);
                self.z1[i][0] = if s1[0].abs() < DENORMAL_EPS {
                    0.0
                } else {
                    s1[0]
                };
                self.z1[i][1] = if s1[1].abs() < DENORMAL_EPS {
                    0.0
                } else {
                    s1[1]
                };
                self.z2[i][0] = if s2[0].abs() < DENORMAL_EPS {
                    0.0
                } else {
                    s2[0]
                };
                self.z2[i][1] = if s2[1].abs() < DENORMAL_EPS {
                    0.0
                } else {
                    s2[1]
                };
            }
        }
    }

    fn process_stereo_chunk_scalar(&mut self, stereo_f64: &mut [[f64; 2]]) {
        let preamp = self.preamp_linear;
        for frame in stereo_f64.iter_mut() {
            frame[0] *= preamp;
            frame[1] *= preamp;
        }
        let count = self.active_count;
        for i in 0..count {
            let c = self.coeffs[i];
            let mut z1l = self.z1[i][0];
            let mut z2l = self.z2[i][0];
            let mut z1r = self.z1[i][1];
            let mut z2r = self.z2[i][1];
            for frame in stereo_f64.iter_mut() {
                let xl = frame[0];
                let yl = c.b0 * xl + z1l;
                z1l = c.b1 * xl - c.a1 * yl + z2l;
                z2l = c.b2 * xl - c.a2 * yl;
                frame[0] = yl;

                let xr = frame[1];
                let yr = c.b0 * xr + z1r;
                z1r = c.b1 * xr - c.a1 * yr + z2r;
                z2r = c.b2 * xr - c.a2 * yr;
                frame[1] = yr;
            }
            self.z1[i][0] = if z1l.abs() < DENORMAL_EPS { 0.0 } else { z1l };
            self.z2[i][0] = if z2l.abs() < DENORMAL_EPS { 0.0 } else { z2l };
            self.z1[i][1] = if z1r.abs() < DENORMAL_EPS { 0.0 } else { z1r };
            self.z2[i][1] = if z2r.abs() < DENORMAL_EPS { 0.0 } else { z2r };
        }
    }

    #[cfg(target_arch = "x86_64")]
    #[target_feature(enable = "avx2", enable = "fma")]
    #[inline]
    unsafe fn process_stereo_sample_avx2_fma(&mut self, l: f64, r: f64) -> (f64, f64) {
        use core::arch::x86_64::*;
        unsafe {
            let mut x = _mm_mul_pd(_mm_set_pd(r, l), _mm_set1_pd(self.preamp_linear));
            let eps = _mm_set1_pd(DENORMAL_EPS);
            let sign_mask = _mm_set1_pd(-0.0);
            let count = self.active_count;
            for i in 0..count {
                let c = &self.coeffs[i];
                let z1 = _mm_loadu_pd(self.z1[i].as_ptr());
                let z2 = _mm_loadu_pd(self.z2[i].as_ptr());
                let b0 = _mm_set1_pd(c.b0);
                let b1 = _mm_set1_pd(c.b1);
                let b2 = _mm_set1_pd(c.b2);
                let a1 = _mm_set1_pd(c.a1);
                let a2 = _mm_set1_pd(c.a2);

                let y = _mm_fmadd_pd(b0, x, z1);
                let nz1 = _mm_fnmadd_pd(a1, y, _mm_fmadd_pd(b1, x, z2));
                let nz2 = _mm_fnmadd_pd(a2, y, _mm_mul_pd(b2, x));

                let nz1_clean = _mm_and_pd(nz1, _mm_cmpgt_pd(_mm_andnot_pd(sign_mask, nz1), eps));
                let nz2_clean = _mm_and_pd(nz2, _mm_cmpgt_pd(_mm_andnot_pd(sign_mask, nz2), eps));
                _mm_storeu_pd(self.z1[i].as_mut_ptr(), nz1_clean);
                _mm_storeu_pd(self.z2[i].as_mut_ptr(), nz2_clean);
                x = y;
            }
            let mut out = [0.0_f64; 2];
            _mm_storeu_pd(out.as_mut_ptr(), x);
            (out[0], out[1])
        }
    }

    #[cfg(target_arch = "x86_64")]
    #[target_feature(enable = "sse2")]
    #[inline]
    unsafe fn process_stereo_sample_sse2(&mut self, l: f64, r: f64) -> (f64, f64) {
        use core::arch::x86_64::*;
        unsafe {
            let mut x = _mm_mul_pd(_mm_set_pd(r, l), _mm_set1_pd(self.preamp_linear));
            let eps = _mm_set1_pd(DENORMAL_EPS);
            let sign_mask = _mm_set1_pd(-0.0);
            let count = self.active_count;
            for i in 0..count {
                let c = &self.coeffs[i];
                let z1 = _mm_loadu_pd(self.z1[i].as_ptr());
                let z2 = _mm_loadu_pd(self.z2[i].as_ptr());
                let y = _mm_add_pd(_mm_mul_pd(_mm_set1_pd(c.b0), x), z1);
                let nz1 = _mm_add_pd(
                    _mm_sub_pd(
                        _mm_mul_pd(_mm_set1_pd(c.b1), x),
                        _mm_mul_pd(_mm_set1_pd(c.a1), y),
                    ),
                    z2,
                );
                let nz2 = _mm_sub_pd(
                    _mm_mul_pd(_mm_set1_pd(c.b2), x),
                    _mm_mul_pd(_mm_set1_pd(c.a2), y),
                );
                let nz1_clean = _mm_and_pd(nz1, _mm_cmpgt_pd(_mm_andnot_pd(sign_mask, nz1), eps));
                let nz2_clean = _mm_and_pd(nz2, _mm_cmpgt_pd(_mm_andnot_pd(sign_mask, nz2), eps));
                _mm_storeu_pd(self.z1[i].as_mut_ptr(), nz1_clean);
                _mm_storeu_pd(self.z2[i].as_mut_ptr(), nz2_clean);
                x = y;
            }
            let mut out = [0.0_f64; 2];
            _mm_storeu_pd(out.as_mut_ptr(), x);
            (out[0], out[1])
        }
    }

    #[inline]
    fn process_channel_sample(&mut self, ch: usize, sample: f64) -> f64 {
        let mut x = sample * self.preamp_linear;
        let count = self.active_count;
        for i in 0..count {
            let c = self.coeffs[i];
            let z1 = self.z1[i][ch];
            let z2 = self.z2[i][ch];
            let y = c.b0 * x + z1;
            let nz1 = c.b1 * x - c.a1 * y + z2;
            let nz2 = c.b2 * x - c.a2 * y;
            self.z1[i][ch] = if nz1.abs() < DENORMAL_EPS { 0.0 } else { nz1 };
            self.z2[i][ch] = if nz2.abs() < DENORMAL_EPS { 0.0 } else { nz2 };
            x = y;
        }
        x
    }
}

#[derive(Clone, Copy)]
struct TruePeakLimiter {
    gain: f64,
    release_coeff: f64,
    prev1: [f64; MAX_CHANNELS],
    prev2: [f64; MAX_CHANNELS],
    prev3: [f64; MAX_CHANNELS],
    min_gain: f64,
}

impl TruePeakLimiter {
    fn new(sample_rate: f64) -> Self {
        Self {
            gain: 1.0,
            release_coeff: Self::release_for_rate(sample_rate),
            prev1: [0.0; MAX_CHANNELS],
            prev2: [0.0; MAX_CHANNELS],
            prev3: [0.0; MAX_CHANNELS],
            min_gain: 1.0,
        }
    }

    fn release_for_rate(sample_rate: f64) -> f64 {
        let fs = sample_rate.max(8_000.0);
        (-1.0 / (0.060 * fs)).exp()
    }

    fn set_sample_rate(&mut self, sample_rate: f64) {
        self.release_coeff = Self::release_for_rate(sample_rate);
    }

    fn reset(&mut self) {
        self.gain = 1.0;
        self.prev1 = [0.0; MAX_CHANNELS];
        self.prev2 = [0.0; MAX_CHANNELS];
        self.prev3 = [0.0; MAX_CHANNELS];
        self.min_gain = 1.0;
    }

    #[inline]
    fn begin_block(&mut self) {
        self.min_gain = 1.0;
    }

    #[inline]
    fn block_max_gr_db(&self) -> f64 {
        if self.min_gain >= 0.999_999 {
            0.0
        } else {
            -linear_to_db(self.min_gain).min(0.0)
        }
    }

    #[inline]
    fn estimate_true_peak_channel(&mut self, ch: usize, x0: f64) -> f64 {
        let x1 = self.prev1[ch];
        let x2 = self.prev2[ch];
        let x3 = self.prev3[ch];
        self.prev3[ch] = x2;
        self.prev2[ch] = x1;
        self.prev1[ch] = x0;

        let mid = 0.5625 * (x1 + x2) - 0.0625 * (x0 + x3);
        x0.abs().max(x1.abs()).max(mid.abs())
    }

    #[inline]
    fn soft_ceiling(sample: f64) -> f64 {
        let mag = sample.abs();
        if mag <= SOFT_KNEE_START_LINEAR {
            return sample;
        }
        let headroom = HARD_LIMITER_CEILING_LINEAR - SOFT_KNEE_START_LINEAR;
        let excess = (mag - SOFT_KNEE_START_LINEAR) / headroom;
        let shaped = SOFT_KNEE_START_LINEAR + headroom * excess.tanh();
        let clamped = shaped.min(HARD_LIMITER_CEILING_LINEAR);
        clamped.copysign(sample)
    }

    #[inline]
    fn process_stereo(&mut self, l: f64, r: f64, enabled: bool) -> (f64, f64) {
        let tp = self
            .estimate_true_peak_channel(0, l)
            .max(self.estimate_true_peak_channel(1, r));
        if enabled {
            let target_gain = if tp > HARD_LIMITER_CEILING_LINEAR {
                HARD_LIMITER_CEILING_LINEAR / tp
            } else {
                1.0
            };
            if target_gain < self.gain {
                self.gain = target_gain;
            } else {
                self.gain = target_gain + (self.gain - target_gain) * self.release_coeff;
            }
            if self.gain < self.min_gain {
                self.min_gain = self.gain;
            }
            let ol = Self::soft_ceiling(l * self.gain);
            let or = Self::soft_ceiling(r * self.gain);
            (ol, or)
        } else {
            let ol = Self::soft_ceiling(l);
            let or = Self::soft_ceiling(r);
            (ol, or)
        }
    }

    #[inline]
    fn process_multi(&mut self, samples: &mut [f64], channels: usize, enabled: bool) {
        let mut tp = 0.0_f64;
        for (ch, &s) in samples.iter().enumerate().take(channels) {
            tp = tp.max(self.estimate_true_peak_channel(ch, s));
        }
        if enabled {
            let target_gain = if tp > HARD_LIMITER_CEILING_LINEAR {
                HARD_LIMITER_CEILING_LINEAR / tp
            } else {
                1.0
            };
            if target_gain < self.gain {
                self.gain = target_gain;
            } else {
                self.gain = target_gain + (self.gain - target_gain) * self.release_coeff;
            }
            if self.gain < self.min_gain {
                self.min_gain = self.gain;
            }
            for s in samples.iter_mut().take(channels) {
                *s = Self::soft_ceiling(*s * self.gain);
            }
        } else {
            for s in samples.iter_mut().take(channels) {
                *s = Self::soft_ceiling(*s);
            }
        }
    }
}

#[derive(Clone, Copy)]
struct Spatializer {
    mode: SpatialMode,
    strength: f64,
    delay_l: [f64; MAX_ITD_SAMPLES],
    delay_r: [f64; MAX_ITD_SAMPLES],
    delay_idx: usize,
    delay_len: usize,
    lp_l: f64,
    lp_r: f64,
    lp_alpha: f64,
    pinna_coeffs: BiquadCoeffs,
    pinna_z1: [f64; 2],
    pinna_z2: [f64; 2],
}

impl Spatializer {
    fn new(sample_rate: f64) -> Self {
        let mut s = Self {
            mode: SpatialMode::Off,
            strength: 0.5,
            delay_l: [0.0; MAX_ITD_SAMPLES],
            delay_r: [0.0; MAX_ITD_SAMPLES],
            delay_idx: 0,
            delay_len: 12,
            lp_l: 0.0,
            lp_r: 0.0,
            lp_alpha: 0.1,
            pinna_coeffs: BiquadCoeffs::identity(),
            pinna_z1: [0.0; 2],
            pinna_z2: [0.0; 2],
        };
        s.set_sample_rate(sample_rate);
        s
    }

    fn set_sample_rate(&mut self, sample_rate: f64) {
        let fs = sample_rate.max(8_000.0);
        let itd_samples = (0.000_25 * fs).round() as usize;
        self.delay_len = itd_samples.clamp(1, MAX_ITD_SAMPLES - 1);
        let rc = 1.0 / (2.0 * PI * 700.0);
        let dt = 1.0 / fs;
        self.lp_alpha = dt / (rc + dt);
        self.pinna_coeffs = BiquadCoeffs::from_band(ParametricBand::peak(3_200.0, 1.8, 1.4), fs);
    }

    fn configure(&mut self, mode: SpatialMode, strength: f64) {
        self.mode = mode;
        self.strength = if strength.is_finite() {
            strength.clamp(0.0, 1.0)
        } else {
            0.5
        };
    }

    fn reset(&mut self) {
        self.delay_l = [0.0; MAX_ITD_SAMPLES];
        self.delay_r = [0.0; MAX_ITD_SAMPLES];
        self.delay_idx = 0;
        self.lp_l = 0.0;
        self.lp_r = 0.0;
        self.pinna_z1 = [0.0; 2];
        self.pinna_z2 = [0.0; 2];
    }

    #[inline]
    fn process_stereo(&mut self, l: f64, r: f64) -> (f64, f64) {
        if self.mode == SpatialMode::Off || self.strength <= 1.0e-4 {
            return (l, r);
        }

        let read_idx = (self.delay_idx + MAX_ITD_SAMPLES - self.delay_len) % MAX_ITD_SAMPLES;
        let dl = self.delay_l[read_idx];
        let dr = self.delay_r[read_idx];
        self.delay_l[self.delay_idx] = l;
        self.delay_r[self.delay_idx] = r;
        self.delay_idx = (self.delay_idx + 1) % MAX_ITD_SAMPLES;

        self.lp_l += self.lp_alpha * (dl - self.lp_l);
        self.lp_r += self.lp_alpha * (dr - self.lp_r);
        if self.lp_l.abs() < DENORMAL_EPS {
            self.lp_l = 0.0;
        }
        if self.lp_r.abs() < DENORMAL_EPS {
            self.lp_r = 0.0;
        }

        match self.mode {
            SpatialMode::Off => (l, r),
            SpatialMode::Crossfeed => {
                let feed = 0.25 * self.strength;
                let direct = 1.0 - 0.15 * self.strength;
                (direct * l + feed * self.lp_r, direct * r + feed * self.lp_l)
            }
            SpatialMode::HrtfCompetitive => {
                let mid = 0.5 * (l + r);
                let side = 0.5 * (l - r);
                let c = self.pinna_coeffs;

                let y0 = c.b0 * side + self.pinna_z1[0];
                let nz1 = c.b1 * side - c.a1 * y0 + self.pinna_z2[0];
                let nz2 = c.b2 * side - c.a2 * y0;
                self.pinna_z1[0] = if nz1.abs() < DENORMAL_EPS { 0.0 } else { nz1 };
                self.pinna_z2[0] = if nz2.abs() < DENORMAL_EPS { 0.0 } else { nz2 };

                let side_boost = 1.0 + 0.18 * self.strength;
                let shaped_side = side + (y0 - side) * self.strength;
                let cross = 0.14 * self.strength;
                let out_l = mid + shaped_side * side_boost + cross * self.lp_r;
                let out_r = mid - shaped_side * side_boost + cross * self.lp_l;
                (out_l, out_r)
            }
        }
    }
}

#[derive(Debug, Clone, Copy, Default)]
pub struct BlockMetrics {
    pub peak_linear: f64,
    pub peak_dbfs: f64,
    pub max_limiter_gr_db: f64,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum SimdPath {
    Avx2Fma,
    Sse2,
    Scalar,
}

pub struct DspEngine {
    sample_rate: f64,
    channels: usize,
    simd_path: SimdPath,
    banks: [FilterBank; 2],
    active_bank: usize,
    crossfade_remaining: usize,
    crossfade_total: usize,
    limiter: TruePeakLimiter,
    limiter_enabled: bool,
    spatializer: Spatializer,
    bypass: bool,
}

impl DspEngine {
    pub fn new(sample_rate: f64, channels: usize) -> Self {
        let fs = if sample_rate.is_finite() && sample_rate >= 8_000.0 {
            sample_rate
        } else {
            48_000.0
        };
        let ch = channels.clamp(1, MAX_CHANNELS);
        Self {
            sample_rate: fs,
            channels: ch,
            simd_path: detect_simd_path(),
            banks: [FilterBank::new(), FilterBank::new()],
            active_bank: 0,
            crossfade_remaining: 0,
            crossfade_total: fade_frames_for_rate(fs),
            limiter: TruePeakLimiter::new(fs),
            limiter_enabled: true,
            spatializer: Spatializer::new(fs),
            bypass: false,
        }
    }

    pub const fn simd_path(&self) -> SimdPath {
        self.simd_path
    }

    pub const fn sample_rate(&self) -> f64 {
        self.sample_rate
    }

    pub const fn channels(&self) -> usize {
        self.channels
    }

    pub fn set_format(&mut self, sample_rate: f64, channels: usize) {
        let fs = if sample_rate.is_finite() && sample_rate >= 8_000.0 {
            sample_rate
        } else {
            48_000.0
        };
        self.sample_rate = fs;
        self.channels = channels.clamp(1, MAX_CHANNELS);
        self.crossfade_total = fade_frames_for_rate(fs);
        self.limiter.set_sample_rate(fs);
        self.spatializer.set_sample_rate(fs);
        self.reset();
    }

    pub fn reset(&mut self) {
        self.banks[0].reset_state();
        self.banks[1].reset_state();
        self.crossfade_remaining = 0;
        self.limiter.reset();
        self.spatializer.reset();
    }

    pub fn apply_config_immediate(&mut self, cfg: &DspConfig) {
        self.bypass = cfg.bypass;
        self.limiter_enabled = cfg.limiter_enabled;
        self.spatializer
            .configure(cfg.spatial_mode, cfg.spatial_strength);
        let idx = self.active_bank;
        self.banks[idx].configure(cfg, self.sample_rate, true);
        self.crossfade_remaining = 0;
    }

    pub fn apply_config_smooth(&mut self, cfg: &DspConfig) {
        self.bypass = cfg.bypass;
        self.limiter_enabled = cfg.limiter_enabled;
        self.spatializer
            .configure(cfg.spatial_mode, cfg.spatial_strength);
        let target_idx = self.active_bank ^ 1;
        let same_count = self.banks[self.active_bank].active_count
            == cfg
                .active_bands()
                .iter()
                .filter(|b| b.kind != FilterKind::Off)
                .count();
        if same_count && !cfg.bypass {
            self.banks[target_idx].z1 = self.banks[self.active_bank].z1;
            self.banks[target_idx].z2 = self.banks[self.active_bank].z2;
            self.banks[target_idx].configure(cfg, self.sample_rate, false);
        } else {
            self.banks[target_idx].configure(cfg, self.sample_rate, true);
        }
        self.crossfade_remaining = self.crossfade_total;
    }

    #[inline]
    fn run_stereo_bank(&mut self, bank_idx: usize, l: f64, r: f64) -> (f64, f64) {
        match self.simd_path {
            #[cfg(target_arch = "x86_64")]
            SimdPath::Avx2Fma => unsafe {
                self.banks[bank_idx].process_stereo_sample_avx2_fma(l, r)
            },
            #[cfg(target_arch = "x86_64")]
            SimdPath::Sse2 => unsafe { self.banks[bank_idx].process_stereo_sample_sse2(l, r) },
            _ => self.banks[bank_idx].process_stereo_sample_scalar(l, r),
        }
    }

    pub fn process_interleaved_f32(&mut self, buffer: &mut [f32]) -> BlockMetrics {
        let ch = self.channels;
        if ch == 0 || buffer.len() < ch {
            return BlockMetrics::default();
        }
        let frames = buffer.len() / ch;
        self.limiter.begin_block();
        let mut peak = 0.0_f64;

        if self.bypass && self.crossfade_remaining == 0 {
            for s in buffer.iter_mut().take(frames * ch) {
                let v = sanitize_f32(*s);
                *s = v;
                let mag = (v as f64).abs();
                if mag > peak {
                    peak = mag;
                }
            }
            return BlockMetrics {
                peak_linear: peak,
                peak_dbfs: linear_to_db(peak),
                max_limiter_gr_db: 0.0,
            };
        }

        if ch == 2 {
            if self.crossfade_remaining == 0 {
                const CHUNK_FRAMES: usize = 512;
                let mut scratch = [[0.0_f64; 2]; CHUNK_FRAMES];
                let active = self.active_bank;
                let spatial_on =
                    self.spatializer.mode != SpatialMode::Off && self.spatializer.strength > 1.0e-4;
                let mut offset = 0usize;
                while offset < frames {
                    let n = (frames - offset).min(CHUNK_FRAMES);
                    for (j, slot) in scratch.iter_mut().enumerate().take(n) {
                        let i = (offset + j) * 2;
                        slot[0] = sanitize_f32(buffer[i]) as f64;
                        slot[1] = sanitize_f32(buffer[i + 1]) as f64;
                    }
                    let slice = &mut scratch[..n];
                    match self.simd_path {
                        #[cfg(target_arch = "x86_64")]
                        SimdPath::Avx2Fma => unsafe {
                            self.banks[active].process_stereo_chunk_avx2_fma(slice);
                        },
                        #[cfg(target_arch = "x86_64")]
                        SimdPath::Sse2 => unsafe {
                            self.banks[active].process_stereo_chunk_sse2(slice);
                        },
                        _ => self.banks[active].process_stereo_chunk_scalar(slice),
                    }
                    for (j, frame) in slice.iter().enumerate() {
                        let (sp_l, sp_r) = if spatial_on {
                            self.spatializer.process_stereo(frame[0], frame[1])
                        } else {
                            (frame[0], frame[1])
                        };
                        let (out_l, out_r) =
                            self.limiter
                                .process_stereo(sp_l, sp_r, self.limiter_enabled);
                        peak = peak.max(out_l.abs()).max(out_r.abs());
                        let i = (offset + j) * 2;
                        buffer[i] = out_l as f32;
                        buffer[i + 1] = out_r as f32;
                    }
                    offset += n;
                }
            } else {
                let active = self.active_bank;
                let standby = active ^ 1;
                for frame in 0..frames {
                    let i = frame * 2;
                    let in_l = sanitize_f32(buffer[i]) as f64;
                    let in_r = sanitize_f32(buffer[i + 1]) as f64;

                    let (eq_l, eq_r) = if self.crossfade_remaining > 0 {
                        let (al, ar) = self.run_stereo_bank(active, in_l, in_r);
                        let (bl, br) = self.run_stereo_bank(standby, in_l, in_r);
                        let progress =
                            1.0 - (self.crossfade_remaining as f64 / self.crossfade_total as f64);
                        let t = 0.5 * (1.0 - (PI * progress).cos());
                        self.crossfade_remaining -= 1;
                        if self.crossfade_remaining == 0 {
                            self.active_bank = standby;
                        }
                        (al + (bl - al) * t, ar + (br - ar) * t)
                    } else {
                        self.run_stereo_bank(self.active_bank, in_l, in_r)
                    };

                    let (sp_l, sp_r) = self.spatializer.process_stereo(eq_l, eq_r);
                    let (out_l, out_r) =
                        self.limiter
                            .process_stereo(sp_l, sp_r, self.limiter_enabled);

                    peak = peak.max(out_l.abs()).max(out_r.abs());
                    buffer[i] = out_l as f32;
                    buffer[i + 1] = out_r as f32;
                }
            }
        } else {
            let mut frame_buf = [0.0_f64; MAX_CHANNELS];
            for frame in 0..frames {
                let base = frame * ch;
                if self.crossfade_remaining > 0 {
                    let active = self.active_bank;
                    let standby = active ^ 1;
                    let progress =
                        1.0 - (self.crossfade_remaining as f64 / self.crossfade_total as f64);
                    let t = 0.5 * (1.0 - (PI * progress).cos());
                    for c in 0..ch {
                        let x = sanitize_f32(buffer[base + c]) as f64;
                        let a = self.banks[active].process_channel_sample(c, x);
                        let b = self.banks[standby].process_channel_sample(c, x);
                        frame_buf[c] = a + (b - a) * t;
                    }
                    self.crossfade_remaining -= 1;
                    if self.crossfade_remaining == 0 {
                        self.active_bank = standby;
                    }
                } else {
                    let active = self.active_bank;
                    for c in 0..ch {
                        let x = sanitize_f32(buffer[base + c]) as f64;
                        frame_buf[c] = self.banks[active].process_channel_sample(c, x);
                    }
                }
                self.limiter
                    .process_multi(&mut frame_buf, ch, self.limiter_enabled);
                for c in 0..ch {
                    let v = frame_buf[c];
                    peak = peak.max(v.abs());
                    buffer[base + c] = v as f32;
                }
            }
        }

        BlockMetrics {
            peak_linear: peak,
            peak_dbfs: linear_to_db(peak),
            max_limiter_gr_db: self.limiter.block_max_gr_db(),
        }
    }
}

#[inline]
fn flush_denormal(v: f64) -> f64 {
    if v.abs() < DENORMAL_EPS { 0.0 } else { v }
}

#[inline]
fn sanitize_f32(v: f32) -> f32 {
    if v.is_finite() { v } else { 0.0 }
}

fn fade_frames_for_rate(sample_rate: f64) -> usize {
    ((sample_rate * CROSSFADE_MS / 1000.0).round() as usize).clamp(64, 4096)
}

fn detect_simd_path() -> SimdPath {
    #[cfg(target_arch = "x86_64")]
    {
        if std::arch::is_x86_feature_detected!("avx2") && std::arch::is_x86_feature_detected!("fma")
        {
            return SimdPath::Avx2Fma;
        }
        if std::arch::is_x86_feature_detected!("sse2") {
            return SimdPath::Sse2;
        }
    }
    SimdPath::Scalar
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn swept_sine_frequency_response_matches_rbj_within_0_02_db() {
        let fs = 48_000.0;
        let bands = [
            ParametricBand::low_shelf(105.0, -5.0, 0.71),
            ParametricBand::peak(260.0, -2.0, 1.1),
            ParametricBand::peak(1_150.0, 1.5, 1.2),
            ParametricBand::peak(2_600.0, 2.8, 1.15),
            ParametricBand::peak(4_200.0, 2.0, 1.4),
            ParametricBand::peak(7_500.0, -2.0, 1.8),
            ParametricBand::high_shelf(11_000.0, -1.5, 0.71),
        ];
        let mut cfg = DspConfig::from_bands(&bands);
        cfg.auto_preamp = false;
        cfg.preamp_db = 0.0;
        cfg.limiter_enabled = false;

        let test_freqs = [
            30.0, 60.0, 105.0, 260.0, 500.0, 1_150.0, 2_600.0, 4_200.0, 7_500.0, 11_000.0, 16_000.0,
        ];
        let amp = 0.05_f64;
        let settle_frames = 4_800usize;
        let measure_frames = 4_800usize;
        let mut max_err_db = 0.0_f64;

        for &hz in &test_freqs {
            let mut engine = DspEngine::new(fs, 2);
            engine.apply_config_immediate(&cfg);
            let total = settle_frames + measure_frames;
            let mut buf = vec![0.0_f32; total * 2];
            for n in 0..total {
                let s = (amp * (2.0 * PI * hz * (n as f64) / fs).sin()) as f32;
                buf[n * 2] = s;
                buf[n * 2 + 1] = s;
            }
            engine.process_interleaved_f32(&mut buf);

            let mut re = 0.0_f64;
            let mut im = 0.0_f64;
            for n in settle_frames..total {
                let y = buf[n * 2] as f64;
                let phase = 2.0 * PI * hz * (n as f64) / fs;
                re += y * phase.cos();
                im -= y * phase.sin();
            }
            let measured_amp = 2.0 * (re * re + im * im).sqrt() / (measure_frames as f64);
            let measured_db = 20.0 * (measured_amp / amp).log10();
            let expected_db = frequency_response_db(&bands, hz, fs);
            let err = (measured_db - expected_db).abs();
            max_err_db = max_err_db.max(err);
            assert!(
                err <= 0.02,
                "freq {hz} Hz: measured {measured_db:.4} dB vs expected {expected_db:.4} dB (err {err:.4} dB)"
            );
        }
        assert!(max_err_db <= 0.02);
    }

    #[test]
    fn hearing_protection_enforces_hard_limiter_and_boost_clamp() {
        let fs = 48_000.0;
        let hostile = [
            ParametricBand::peak(1_000.0, 99.0, 1.0),
            ParametricBand::peak(1_000.0, 99.0, 1.0),
            ParametricBand::low_shelf(100.0, 50.0, 0.71),
        ];
        assert!((hostile[0].clamped(fs).gain_db - MAX_GAIN_DB).abs() < 1.0e-9);

        let mut cfg = DspConfig::from_bands(&hostile);
        cfg.auto_preamp = false;
        cfg.preamp_db = 12.0;
        cfg.limiter_enabled = true;

        let mut engine = DspEngine::new(fs, 2);
        engine.apply_config_immediate(&cfg);

        let mut buf = vec![0.0_f32; 4_800 * 2];
        for n in 0..4_800 {
            let s = (0.95 * (2.0 * PI * 1_000.0 * n as f64 / fs).sin()) as f32;
            buf[n * 2] = s;
            buf[n * 2 + 1] = s;
        }
        buf[10] = f32::NAN;
        buf[11] = f32::INFINITY;
        buf[12] = f32::NEG_INFINITY;

        let metrics = engine.process_interleaved_f32(&mut buf);
        for &sample in &buf {
            assert!(sample.is_finite());
            assert!(
                (sample as f64).abs() <= HARD_LIMITER_CEILING_LINEAR + 1.0e-5,
                "sample {sample} exceeded hard ceiling {HARD_LIMITER_CEILING_LINEAR}"
            );
        }
        assert!(metrics.peak_dbfs <= HARD_LIMITER_CEILING_DBFS + 0.02);
    }

    #[test]
    fn rapid_100_swaps_from_minus_12_to_plus_12_produce_zero_clicks() {
        let fs = 48_000.0;
        let mut engine = DspEngine::new(fs, 2);
        let block_frames = 480usize;
        let mut buf = vec![0.0_f32; block_frames * 2];
        let mut phase = 0.0_f64;
        let phase_inc = 2.0 * PI * 440.0 / fs;
        let mut prev_l = 0.0_f32;
        let mut max_delta = 0.0_f32;

        for iter in 0..100 {
            let gain = if iter % 2 == 0 { -12.0 } else { 12.0 };
            let cfg = DspConfig::from_bands(&[ParametricBand::peak(1_000.0, gain, 1.0)]);
            if iter == 0 {
                engine.apply_config_immediate(&cfg);
            } else {
                engine.apply_config_smooth(&cfg);
            }
            for f in 0..block_frames {
                let s = (0.25 * phase.sin()) as f32;
                phase += phase_inc;
                buf[f * 2] = s;
                buf[f * 2 + 1] = s;
            }
            engine.process_interleaved_f32(&mut buf);
            for f in 0..block_frames {
                let cur = buf[f * 2];
                if iter > 0 || f > 0 {
                    let delta = (cur - prev_l).abs();
                    if delta > max_delta {
                        max_delta = delta;
                    }
                }
                prev_l = cur;
            }
        }
        assert!(
            max_delta < 0.08,
            "click detected during 100 rapid -12/+12 dB swaps: max_delta={max_delta}"
        );
    }

    #[test]
    fn crossfeed_and_hrtf_spatializer_stay_bounded_and_distinct() {
        let fs = 48_000.0;
        let frames = 960usize;
        let mut dry = vec![0.0_f32; frames * 2];
        for n in 0..frames {
            dry[n * 2] = (0.5 * (2.0 * PI * 300.0 * n as f64 / fs).sin()) as f32;
            dry[n * 2 + 1] = 0.0;
        }

        let mut cf_buf = dry.clone();
        let mut cf_eng = DspEngine::new(fs, 2);
        let cf_cfg = DspConfig {
            spatial_mode: SpatialMode::Crossfeed,
            spatial_strength: 0.8,
            ..DspConfig::default()
        };
        cf_eng.apply_config_immediate(&cf_cfg);
        cf_eng.process_interleaved_f32(&mut cf_buf);

        let right_energy_cf: f64 = cf_buf
            .as_chunks::<2>()
            .0
            .iter()
            .map(|c| (c[1] as f64).powi(2))
            .sum();
        assert!(
            right_energy_cf > 1.0e-3,
            "crossfeed should bleed attenuated low-frequency cue into opposite ear"
        );

        let mut hrtf_buf = dry.clone();
        let mut hrtf_eng = DspEngine::new(fs, 2);
        let hrtf_cfg = DspConfig {
            spatial_mode: SpatialMode::HrtfCompetitive,
            spatial_strength: 0.8,
            ..DspConfig::default()
        };
        hrtf_eng.apply_config_immediate(&hrtf_cfg);
        let metrics = hrtf_eng.process_interleaved_f32(&mut hrtf_buf);
        assert!(metrics.peak_linear <= HARD_LIMITER_CEILING_LINEAR + 1.0e-5);
        assert_ne!(hrtf_buf, dry);
    }

    #[test]
    fn auto_preamp_matches_headroom_policy() {
        let boost = [ParametricBand::peak(1_000.0, 6.0, 1.0)];
        let preamp = compute_auto_preamp_db(&boost, 48_000.0);
        assert!((preamp - (-6.5)).abs() < 0.05, "got preamp {preamp}");

        let cut = [ParametricBand::peak(1_000.0, -6.0, 1.0)];
        assert_eq!(compute_auto_preamp_db(&cut, 48_000.0), 0.0);
    }
}
