use std::f32::consts::PI;

use crate::fft::{C32, MixedRadixFft};

pub const MEL_BINS: usize = 80;
const SAMPLE_RATE: f32 = 16000.0;
const FRAME_LEN: usize = 400;
const FRAME_SHIFT: usize = 160;
const N_FFT: usize = 512;
const N_FREQ: usize = N_FFT / 2 + 1;
const PREEMPH: f32 = 0.97;
const LOW_FREQ: f32 = 0.0;
const HIGH_FREQ: f32 = 7600.0;

fn hz_to_mel_slaney(freq: f64) -> f64 {
    const F_MIN: f64 = 0.0;
    const F_SP: f64 = 200.0 / 3.0;
    const MIN_LOG_HZ: f64 = 1000.0;
    let min_log_mel = (MIN_LOG_HZ - F_MIN) / F_SP;
    let logstep = (6.4_f64).ln() / 27.0;
    if freq >= MIN_LOG_HZ {
        min_log_mel + (freq / MIN_LOG_HZ).ln() / logstep
    } else {
        (freq - F_MIN) / F_SP
    }
}

fn mel_to_hz_slaney(mel: f64) -> f64 {
    const F_MIN: f64 = 0.0;
    const F_SP: f64 = 200.0 / 3.0;
    const MIN_LOG_HZ: f64 = 1000.0;
    let min_log_mel = (MIN_LOG_HZ - F_MIN) / F_SP;
    let logstep = (6.4_f64).ln() / 27.0;
    if mel >= min_log_mel {
        MIN_LOG_HZ * (logstep * (mel - min_log_mel)).exp()
    } else {
        F_MIN + F_SP * mel
    }
}

fn build_slaney_filterbank() -> Vec<Vec<(usize, f32)>> {
    let min_mel = hz_to_mel_slaney(LOW_FREQ as f64);
    let max_mel = hz_to_mel_slaney(HIGH_FREQ as f64);
    let mut mel_f = Vec::with_capacity(MEL_BINS + 2);
    for i in 0..(MEL_BINS + 2) {
        let m = min_mel + (max_mel - min_mel) * (i as f64) / ((MEL_BINS + 1) as f64);
        mel_f.push(mel_to_hz_slaney(m));
    }
    let fft_freqs: Vec<f64> = (0..N_FREQ)
        .map(|k| (k as f64) * (SAMPLE_RATE as f64) / (N_FFT as f64))
        .collect();

    let mut bank = Vec::with_capacity(MEL_BINS);
    for m in 0..MEL_BINS {
        let f_left = mel_f[m];
        let f_center = mel_f[m + 1];
        let f_right = mel_f[m + 2];
        let enorm = 2.0 / (f_right - f_left);
        let mut weights = Vec::new();
        for (k, &f) in fft_freqs.iter().enumerate() {
            let lower = (f - f_left) / (f_center - f_left);
            let upper = (f_right - f) / (f_right - f_center);
            let w = lower.min(upper).max(0.0) * enorm;
            if w > 0.0 {
                weights.push((k, w as f32));
            }
        }
        bank.push(weights);
    }
    bank
}

pub fn extract_nemo_mel_80(audio_16k: &[f32]) -> (Vec<f32>, usize) {
    if audio_16k.len() < FRAME_LEN {
        return (Vec::new(), 0);
    }
    let num_frames = 1 + (audio_16k.len() - FRAME_LEN) / FRAME_SHIFT;
    let window: Vec<f32> = (0..FRAME_LEN)
        .map(|i| 0.5 - 0.5 * (2.0 * PI * (i as f32) / ((FRAME_LEN - 1) as f32)).cos())
        .collect();
    let filterbank = build_slaney_filterbank();
    let mut fft = MixedRadixFft::new(N_FFT);
    let mut buf = vec![0.0f32; N_FFT];
    let mut bins = vec![C32::default(); N_FREQ];
    let mut power = vec![0.0f32; N_FREQ];
    let mut features = vec![0.0f32; MEL_BINS * num_frames];

    for t in 0..num_frames {
        let start = t * FRAME_SHIFT;
        let frame = &audio_16k[start..start + FRAME_LEN];
        buf[0] = frame[0] * (1.0 - PREEMPH) * window[0];
        for i in 1..FRAME_LEN {
            buf[i] = (frame[i] - PREEMPH * frame[i - 1]) * window[i];
        }
        buf[FRAME_LEN..N_FFT].fill(0.0);

        fft.rfft(&buf, &mut bins);
        for k in 0..N_FREQ {
            power[k] = bins[k].re * bins[k].re + bins[k].im * bins[k].im;
        }
        for (m, weights) in filterbank.iter().enumerate() {
            let mut e = 0.0f32;
            for &(k, w) in weights {
                e += power[k] * w;
            }
            features[m * num_frames + t] = e.max(f32::EPSILON).ln();
        }
    }

    for m in 0..MEL_BINS {
        let row = &mut features[m * num_frames..(m + 1) * num_frames];
        let mean: f32 = row.iter().sum::<f32>() / (num_frames as f32);
        let var: f32 = row
            .iter()
            .map(|&x| {
                let d = x - mean;
                d * d
            })
            .sum::<f32>()
            / (num_frames as f32);
        let denom = var.sqrt() + 1e-5;
        for x in row.iter_mut() {
            *x = (*x - mean) / denom;
        }
    }

    (features, num_frames)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn mel_shape_and_normalization_sane() {
        let audio: Vec<f32> = (0..16000)
            .map(|i| 0.2 * (2.0 * PI * 440.0 * (i as f32) / SAMPLE_RATE).sin())
            .collect();
        let (feats, n_frames) = extract_nemo_mel_80(&audio);
        assert_eq!(n_frames, 98);
        assert_eq!(feats.len(), MEL_BINS * n_frames);
        assert!(feats.iter().all(|x| x.is_finite()));
    }
}
