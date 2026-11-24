use std::f32::consts::PI;
use std::ffi::CString;
use std::path::Path;

use crate::dpdfnet::estimate_speech_db;
use crate::ort_ffi::{OrtRuntime, OrtSessionHandle};

const VOXLOCK_N_FFT: usize = 512;
const VOXLOCK_HOP: usize = 128;
const VOXLOCK_STATE_LEN: usize = 6 * 32 * 256;

fn hann_window(n: usize) -> Vec<f32> {
    (0..n)
        .map(|i| 0.5 * (1.0 - (2.0 * PI * i as f32 / n as f32).cos()))
        .collect()
}

pub struct VoxLockStream {
    enroll_sess: OrtSessionHandle,
    chunk_sess: OrtSessionHandle,
    enroll_mag: Vec<f32>,
    enroll_mag_shape: Vec<i64>,
    context: Vec<f32>,
    context_shape: Vec<i64>,
    h: Vec<f32>,
    c: Vec<f32>,
    window: Vec<f32>,
    output_gain: f32,
    in_buf: Vec<f32>,
    ola: Vec<f32>,
    ola_norm: Vec<f32>,
    out_fifo: Vec<f32>,
    enrolled: bool,
    pub last_speech_db: f32,
}

impl VoxLockStream {
    pub fn load(
        ort_dll: &Path,
        enroll_onnx: &Path,
        chunk_onnx: &Path,
        intra_threads: i32,
    ) -> Result<Self, String> {
        let rt = OrtRuntime::load(ort_dll)?;
        let enroll_sess = rt.create_session(enroll_onnx, intra_threads)?;
        let chunk_sess = rt.create_session(chunk_onnx, intra_threads)?;
        let output_gain = chunk_sess
            .metadata_str("output_gain")?
            .and_then(|s| s.parse::<f32>().ok())
            .unwrap_or(-0.5);

        Ok(Self {
            enroll_sess,
            chunk_sess,
            enroll_mag: Vec::new(),
            enroll_mag_shape: Vec::new(),
            context: Vec::new(),
            context_shape: Vec::new(),
            h: vec![0.0; VOXLOCK_STATE_LEN],
            c: vec![0.0; VOXLOCK_STATE_LEN],
            window: hann_window(VOXLOCK_N_FFT),
            output_gain,
            in_buf: Vec::with_capacity(1024),
            ola: vec![0.0; VOXLOCK_N_FFT],
            ola_norm: vec![0.0; VOXLOCK_N_FFT],
            out_fifo: Vec::with_capacity(1024),
            enrolled: false,
            last_speech_db: -15.0,
        })
    }

    pub fn enroll_16k(&mut self, audio_16k: &[f32]) -> Result<(), String> {
        if audio_16k.len() < 8000 {
            return Err("enrollment audio too short".to_string());
        }
        let mut wav = audio_16k.to_vec();
        let shape = [1i64, wav.len() as i64];
        let rt = self.enroll_sess.rt;
        let t_in = rt.create_f32_tensor(&mut wav, &shape)?;
        let in_name = CString::new("enroll").unwrap();
        let out_m = CString::new("enroll_mag").unwrap();
        let out_c = CString::new("context").unwrap();
        let outs = self.enroll_sess.run(
            &[in_name.as_ptr()],
            &[t_in.val as *const _],
            &[out_m.as_ptr(), out_c.as_ptr()],
        )?;
        self.enroll_mag = outs[0].as_f32_slice()?.to_vec();
        self.enroll_mag_shape = outs[0].shape()?;
        self.context = outs[1].as_f32_slice()?.to_vec();
        self.context_shape = outs[1].shape()?;
        self.h.fill(0.0);
        self.c.fill(0.0);
        self.in_buf.clear();
        self.ola.fill(0.0);
        self.ola_norm.fill(0.0);
        self.out_fifo.clear();
        self.enrolled = true;
        Ok(())
    }

    pub fn process_16k(
        &mut self,
        input_16k: &[f32],
        output_16k: &mut [f32],
    ) -> Result<f32, String> {
        if !self.enrolled {
            output_16k.copy_from_slice(input_16k);
            return Ok(0.0);
        }
        self.in_buf.extend_from_slice(input_16k);
        while self.in_buf.len() >= VOXLOCK_N_FFT {
            let chunk_len = ((self.in_buf.len() - VOXLOCK_N_FFT) / VOXLOCK_HOP + 1) * VOXLOCK_HOP
                + (VOXLOCK_N_FFT - VOXLOCK_HOP);
            let mut wav = self.in_buf[..chunk_len].to_vec();
            let consumed = chunk_len - (VOXLOCK_N_FFT - VOXLOCK_HOP);
            self.in_buf.drain(..consumed);

            let rt = self.chunk_sess.rt;
            let wav_shape = [1i64, wav.len() as i64];
            let hc_shape = [6i64, 32, 256];

            let t_wav = rt.create_f32_tensor(&mut wav, &wav_shape)?;
            let t_em = rt.create_f32_tensor(&mut self.enroll_mag, &self.enroll_mag_shape)?;
            let t_ctx = rt.create_f32_tensor(&mut self.context, &self.context_shape)?;
            let t_h = rt.create_f32_tensor(&mut self.h, &hc_shape)?;
            let t_c = rt.create_f32_tensor(&mut self.c, &hc_shape)?;

            let n_wav = CString::new("wav").unwrap();
            let n_em = CString::new("enroll_mag").unwrap();
            let n_ctx = CString::new("context").unwrap();
            let n_h = CString::new("h").unwrap();
            let n_c = CString::new("c").unwrap();
            let o_fr = CString::new("frames").unwrap();
            let o_h = CString::new("h_out").unwrap();
            let o_c = CString::new("c_out").unwrap();

            let outs = self.chunk_sess.run(
                &[
                    n_wav.as_ptr(),
                    n_em.as_ptr(),
                    n_ctx.as_ptr(),
                    n_h.as_ptr(),
                    n_c.as_ptr(),
                ],
                &[
                    t_wav.val as *const _,
                    t_em.val as *const _,
                    t_ctx.val as *const _,
                    t_h.val as *const _,
                    t_c.val as *const _,
                ],
                &[o_fr.as_ptr(), o_h.as_ptr(), o_c.as_ptr()],
            )?;

            let frames_data = outs[0].as_f32_slice()?;
            let frames_shape = outs[0].shape()?;
            let k_frames = if frames_shape.len() == 3 {
                frames_shape[1] as usize
            } else {
                frames_data.len() / VOXLOCK_N_FFT
            };

            let h_out = outs[1].as_f32_slice()?;
            let c_out = outs[2].as_f32_slice()?;
            if h_out.len() == self.h.len() {
                self.h.copy_from_slice(h_out);
            }
            if c_out.len() == self.c.len() {
                self.c.copy_from_slice(c_out);
            }

            let gain = self.output_gain;
            for f in 0..k_frames {
                let slice = &frames_data[f * VOXLOCK_N_FFT..(f + 1) * VOXLOCK_N_FFT];
                for i in 0..VOXLOCK_N_FFT {
                    let w = self.window[i];
                    self.ola[i] += slice[i] * w * gain;
                    self.ola_norm[i] += w * w;
                }
                for i in 0..VOXLOCK_HOP {
                    let denom = self.ola_norm[i].max(1e-6);
                    self.out_fifo.push((self.ola[i] / denom).clamp(-1.0, 1.0));
                }
                self.ola.copy_within(VOXLOCK_HOP..VOXLOCK_N_FFT, 0);
                self.ola[VOXLOCK_N_FFT - VOXLOCK_HOP..].fill(0.0);
                self.ola_norm.copy_within(VOXLOCK_HOP..VOXLOCK_N_FFT, 0);
                self.ola_norm[VOXLOCK_N_FFT - VOXLOCK_HOP..].fill(0.0);
            }
        }

        let n = output_16k.len();
        if self.out_fifo.len() >= n {
            output_16k.copy_from_slice(&self.out_fifo[..n]);
            self.out_fifo.drain(..n);
        } else {
            let have = self.out_fifo.len();
            output_16k[..have].copy_from_slice(&self.out_fifo[..have]);
            output_16k[have..].fill(0.0);
            self.out_fifo.clear();
        }

        let in_p: f64 = input_16k
            .iter()
            .map(|&x| (x as f64) * (x as f64))
            .sum::<f64>()
            / n.max(1) as f64;
        let out_p: f64 = output_16k
            .iter()
            .map(|&x| (x as f64) * (x as f64))
            .sum::<f64>()
            / n.max(1) as f64;
        let speech_db = estimate_speech_db(in_p, out_p);
        self.last_speech_db = speech_db;
        Ok(speech_db)
    }
}
