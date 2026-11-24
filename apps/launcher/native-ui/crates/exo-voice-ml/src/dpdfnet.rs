use std::f32::consts::PI;
use std::ffi::CString;
use std::path::Path;

use crate::fft::{C32, MixedRadixFft};
use crate::ort_ffi::{OrtRuntime, OrtSessionHandle};

const ATTN_DELAY_HOPS: usize = 4;
const RESAMPLE_TAPS: usize = 31;

fn vorbis_window(n: usize) -> Vec<f32> {
    let half = (n / 2) as f32;
    (0..n)
        .map(|i| {
            let s = (0.5 * PI * (i as f32 + 0.5) / half).sin();
            (0.5 * PI * s * s).sin()
        })
        .collect()
}

fn make_lowpass_taps(cutoff_norm: f32) -> [f32; RESAMPLE_TAPS] {
    let mut taps = [0.0f32; RESAMPLE_TAPS];
    let mid = (RESAMPLE_TAPS - 1) as f32 * 0.5;
    let mut sum = 0.0f64;
    for (i, t) in taps.iter_mut().enumerate() {
        let n = i as f32 - mid;
        let sinc = if n.abs() < 1e-6 {
            2.0 * cutoff_norm
        } else {
            (2.0 * PI * cutoff_norm * n).sin() / (PI * n)
        };
        let win = 0.54 - 0.46 * (2.0 * PI * i as f32 / (RESAMPLE_TAPS - 1) as f32).cos();
        let v = sinc * win;
        *t = v;
        sum += v as f64;
    }
    let inv = (1.0 / sum) as f32;
    for t in &mut taps {
        *t *= inv;
    }
    taps
}

struct Decimator3 {
    taps: [f32; RESAMPLE_TAPS],
    hist: Vec<f32>,
}

impl Decimator3 {
    fn new() -> Self {
        Self {
            taps: make_lowpass_taps(7000.0 / 48000.0),
            hist: vec![0.0; RESAMPLE_TAPS - 1 + 960],
        }
    }

    fn process(&mut self, in48: &[f32], out16: &mut [f32]) {
        let keep = RESAMPLE_TAPS - 1;
        let n = in48.len();
        if self.hist.len() < keep + n {
            self.hist.resize(keep + n, 0.0);
        }
        self.hist[keep..keep + n].copy_from_slice(in48);
        for (k, dst) in out16.iter_mut().enumerate() {
            let base = k * 3;
            let mut acc = 0.0f32;
            for (j, &t) in self.taps.iter().enumerate() {
                acc += self.hist[base + j] * t;
            }
            *dst = acc;
        }
        self.hist.copy_within(n..n + keep, 0);
    }
}

struct Interpolator3 {
    taps: [f32; RESAMPLE_TAPS],
    hist: Vec<f32>,
}

impl Interpolator3 {
    fn new() -> Self {
        Self {
            taps: make_lowpass_taps(7000.0 / 48000.0),
            hist: vec![0.0; RESAMPLE_TAPS - 1 + 960],
        }
    }

    fn process(&mut self, in16: &[f32], out48: &mut [f32]) {
        let keep = RESAMPLE_TAPS - 1;
        let n48 = in16.len() * 3;
        if self.hist.len() < keep + n48 {
            self.hist.resize(keep + n48, 0.0);
        }
        for (k, &s) in in16.iter().enumerate() {
            let base = keep + k * 3;
            self.hist[base] = s * 3.0;
            self.hist[base + 1] = 0.0;
            self.hist[base + 2] = 0.0;
        }
        for (i, dst) in out48.iter_mut().enumerate() {
            let mut acc = 0.0f32;
            for (j, &t) in self.taps.iter().enumerate() {
                acc += self.hist[i + j] * t;
            }
            *dst = acc;
        }
        self.hist.copy_within(n48..n48 + keep, 0);
    }
}

fn parse_csv_f32(s: &str) -> Result<Vec<f32>, String> {
    s.split(',')
        .map(|tok| {
            tok.trim()
                .parse::<f32>()
                .map_err(|e| format!("bad float in metadata: {e}"))
        })
        .collect()
}

pub struct DpdfNetStream {
    session: OrtSessionHandle,
    sample_rate: usize,
    n_fft: usize,
    hop: usize,
    freq_bins: usize,
    window: Vec<f32>,
    fft: MixedRadixFft,
    in_win: Vec<f32>,
    frame_buf: Vec<f32>,
    time_buf: Vec<f32>,
    ola: Vec<f32>,
    spec_bins: Vec<C32>,
    spec_in: Vec<f32>,
    state: Vec<f32>,
    noisy_ring: Vec<Vec<f32>>,
    noisy_ring_idx: usize,
    in_spec_name: CString,
    in_state_name: CString,
    out_spec_name: CString,
    out_state_name: CString,
    atten_lim_db: f32,
    decimator: Decimator3,
    interpolator: Interpolator3,
    mono48: Vec<f32>,
    mono16_in: Vec<f32>,
    mono16_out: Vec<f32>,
    out48: Vec<f32>,
    hop_in_buf: Vec<f32>,
    hop_out_buf: Vec<f32>,
    pub last_speech_db: f32,
}

impl DpdfNetStream {
    pub fn load(ort_dll: &Path, model_path: &Path, intra_threads: i32) -> Result<Self, String> {
        let rt = OrtRuntime::load(ort_dll)?;
        let session = rt.create_session(model_path, intra_threads)?;

        let sample_rate: usize = session
            .metadata_str("sample_rate")?
            .ok_or("missing sample_rate metadata")?
            .parse()
            .map_err(|_| "invalid sample_rate")?;
        let n_fft: usize = session
            .metadata_str("n_fft")?
            .ok_or("missing n_fft metadata")?
            .parse()
            .map_err(|_| "invalid n_fft")?;
        let hop: usize = session
            .metadata_str("hop_length")?
            .ok_or("missing hop_length metadata")?
            .parse()
            .map_err(|_| "invalid hop_length")?;
        let state_size: usize = session
            .metadata_str("state_size")?
            .ok_or("missing state_size metadata")?
            .parse()
            .map_err(|_| "invalid state_size")?;
        let erb_sz: usize = session
            .metadata_str("erb_norm_state_size")?
            .ok_or("missing erb_norm_state_size")?
            .parse()
            .map_err(|_| "invalid erb_norm_state_size")?;
        let spec_sz: usize = session
            .metadata_str("spec_norm_state_size")?
            .ok_or("missing spec_norm_state_size")?
            .parse()
            .map_err(|_| "invalid spec_norm_state_size")?;
        let erb_init = parse_csv_f32(
            &session
                .metadata_str("erb_norm_init")?
                .ok_or("missing erb_norm_init")?,
        )?;
        let spec_init = parse_csv_f32(
            &session
                .metadata_str("spec_norm_init")?
                .ok_or("missing spec_norm_init")?,
        )?;

        if erb_init.len() != erb_sz || spec_init.len() != spec_sz || erb_sz + spec_sz > state_size {
            return Err("dpdfnet metadata state sizes mismatch".to_string());
        }

        let mut state = vec![0.0f32; state_size];
        state[..erb_sz].copy_from_slice(&erb_init);
        state[erb_sz..erb_sz + spec_sz].copy_from_slice(&spec_init);

        let freq_bins = n_fft / 2 + 1;
        let noisy_ring = (0..ATTN_DELAY_HOPS)
            .map(|_| vec![0.0f32; freq_bins * 2])
            .collect();

        Ok(Self {
            session,
            sample_rate,
            n_fft,
            hop,
            freq_bins,
            window: vorbis_window(n_fft),
            fft: MixedRadixFft::new(n_fft),
            in_win: vec![0.0; n_fft],
            frame_buf: vec![0.0; n_fft],
            time_buf: vec![0.0; n_fft],
            ola: vec![0.0; n_fft],
            spec_bins: vec![C32::default(); freq_bins],
            spec_in: vec![0.0; freq_bins * 2],
            state,
            noisy_ring,
            noisy_ring_idx: 0,
            in_spec_name: CString::new("spec").unwrap(),
            in_state_name: CString::new("state_in").unwrap(),
            out_spec_name: CString::new("spec_e").unwrap(),
            out_state_name: CString::new("state_out").unwrap(),
            atten_lim_db: 100.0,
            decimator: Decimator3::new(),
            interpolator: Interpolator3::new(),
            mono48: vec![0.0; 960],
            mono16_in: vec![0.0; 320],
            mono16_out: vec![0.0; 320],
            out48: vec![0.0; 960],
            hop_in_buf: vec![0.0; hop],
            hop_out_buf: vec![0.0; hop],
            last_speech_db: -15.0,
        })
    }

    pub fn sample_rate(&self) -> usize {
        self.sample_rate
    }

    pub fn hop_length(&self) -> usize {
        self.hop
    }

    pub fn set_atten_lim_db(&mut self, db: f32) {
        self.atten_lim_db = db.clamp(0.0, 100.0);
    }

    pub fn process_hop(&mut self, in_hop: &[f32], out_hop: &mut [f32]) -> Result<(), String> {
        debug_assert_eq!(in_hop.len(), self.hop);
        debug_assert_eq!(out_hop.len(), self.hop);

        let keep = self.n_fft - self.hop;
        self.in_win.copy_within(self.hop..self.n_fft, 0);
        self.in_win[keep..self.n_fft].copy_from_slice(in_hop);

        for i in 0..self.n_fft {
            self.frame_buf[i] = self.in_win[i] * self.window[i];
        }
        self.fft.rfft(&self.frame_buf, &mut self.spec_bins);

        for k in 0..self.freq_bins {
            self.spec_in[k * 2] = self.spec_bins[k].re;
            self.spec_in[k * 2 + 1] = self.spec_bins[k].im;
        }

        let spec_shape = [1i64, 1, self.freq_bins as i64, 2];
        let state_shape = [self.state.len() as i64];

        let rt = self.session.rt;
        let t_spec = rt.create_f32_tensor(&mut self.spec_in, &spec_shape)?;
        let t_state = rt.create_f32_tensor(&mut self.state, &state_shape)?;

        let in_names = [self.in_spec_name.as_ptr(), self.in_state_name.as_ptr()];
        let in_vals = [t_spec.val as *const _, t_state.val as *const _];
        let out_names = [self.out_spec_name.as_ptr(), self.out_state_name.as_ptr()];

        let outs = self.session.run(&in_names, &in_vals, &out_names)?;
        let enh = outs[0].as_f32_slice()?;
        let next_state = outs[1].as_f32_slice()?;

        let use_limit = self.atten_lim_db < 99.5;
        let alpha = if use_limit {
            10.0f32.powf(-self.atten_lim_db / 20.0)
        } else {
            0.0
        };
        let delayed_noisy = &self.noisy_ring[self.noisy_ring_idx];

        for k in 0..self.freq_bins {
            let er = enh[k * 2];
            let ei = enh[k * 2 + 1];
            if use_limit {
                let nr = delayed_noisy[k * 2];
                let ni = delayed_noisy[k * 2 + 1];
                self.spec_bins[k] = C32::new(
                    alpha * nr + (1.0 - alpha) * er,
                    alpha * ni + (1.0 - alpha) * ei,
                );
            } else {
                self.spec_bins[k] = C32::new(er, ei);
            }
        }

        self.noisy_ring[self.noisy_ring_idx].copy_from_slice(&self.spec_in);
        self.noisy_ring_idx = (self.noisy_ring_idx + 1) % ATTN_DELAY_HOPS;
        self.state.copy_from_slice(next_state);

        self.fft.irfft(&self.spec_bins, &mut self.time_buf);
        for i in 0..self.n_fft {
            self.ola[i] += self.time_buf[i] * self.window[i];
        }
        out_hop.copy_from_slice(&self.ola[..self.hop]);
        self.ola.copy_within(self.hop..self.n_fft, 0);
        self.ola[keep..self.n_fft].fill(0.0);
        Ok(())
    }

    pub fn process_48k_stereo(&mut self, stereo: &mut [f32]) -> Result<f32, String> {
        let frames = stereo.len() / 2;
        if frames == 0 {
            return Ok(-15.0);
        }
        if self.mono48.len() != frames {
            self.mono48.resize(frames, 0.0);
            self.out48.resize(frames, 0.0);
        }
        let mut in_power = 0.0f64;
        for i in 0..frames {
            let m = 0.5 * (stereo[i * 2] + stereo[i * 2 + 1]);
            self.mono48[i] = m;
            in_power += (m as f64) * (m as f64);
        }

        if self.sample_rate == 16000 && frames.is_multiple_of(3 * self.hop) {
            let n16 = frames / 3;
            if self.mono16_in.len() != n16 {
                self.mono16_in.resize(n16, 0.0);
                self.mono16_out.resize(n16, 0.0);
            }
            self.decimator.process(&self.mono48, &mut self.mono16_in);
            let mut hop_in = std::mem::take(&mut self.hop_in_buf);
            let mut hop_out = std::mem::take(&mut self.hop_out_buf);
            let mut off = 0;
            let mut res = Ok(());
            while off + self.hop <= n16 {
                hop_in.copy_from_slice(&self.mono16_in[off..off + self.hop]);
                if let Err(e) = self.process_hop(&hop_in, &mut hop_out) {
                    res = Err(e);
                    break;
                }
                self.mono16_out[off..off + self.hop].copy_from_slice(&hop_out);
                off += self.hop;
            }
            self.hop_in_buf = hop_in;
            self.hop_out_buf = hop_out;
            res?;
            self.interpolator.process(&self.mono16_out, &mut self.out48);
        } else if self.sample_rate == 48000 && frames.is_multiple_of(self.hop) {
            let mut hop_in = std::mem::take(&mut self.hop_in_buf);
            let mut hop_out = std::mem::take(&mut self.hop_out_buf);
            let mut off = 0;
            let mut res = Ok(());
            while off + self.hop <= frames {
                hop_in.copy_from_slice(&self.mono48[off..off + self.hop]);
                if let Err(e) = self.process_hop(&hop_in, &mut hop_out) {
                    res = Err(e);
                    break;
                }
                self.out48[off..off + self.hop].copy_from_slice(&hop_out);
                off += self.hop;
            }
            self.hop_in_buf = hop_in;
            self.hop_out_buf = hop_out;
            res?;
        } else {
            return Err("unsupported frame size for dpdfnet sample rate".to_string());
        }

        let mut out_power = 0.0f64;
        for i in 0..frames {
            let s = self.out48[i].clamp(-1.0, 1.0);
            out_power += (s as f64) * (s as f64);
            stereo[i * 2] = s;
            stereo[i * 2 + 1] = s;
        }
        let speech_db = estimate_speech_db(in_power / frames as f64, out_power / frames as f64);
        self.last_speech_db = speech_db;
        Ok(speech_db)
    }
}

pub fn estimate_speech_db(in_power: f64, out_power: f64) -> f32 {
    if in_power <= 1e-10 || out_power <= 1e-10 {
        return -15.0;
    }
    let ratio = (out_power / in_power).clamp(1e-6, 1.0);
    if ratio <= 0.02 {
        return -15.0;
    }
    if ratio >= 0.98 {
        return 30.0;
    }
    let snr = 10.0 * (ratio / (1.0 - ratio)).log10();
    (snr as f32).clamp(-15.0, 35.0)
}
