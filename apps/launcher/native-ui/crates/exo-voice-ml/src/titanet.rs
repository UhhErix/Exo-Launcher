use std::ffi::CString;
use std::path::Path;

use crate::mel::{MEL_BINS, extract_nemo_mel_80};
use crate::ort_ffi::{OrtRuntime, OrtSessionHandle};

pub const TITANET_EMBEDDING_DIM: usize = 192;

pub struct TitaNetExtractor {
    session: OrtSessionHandle,
    in_audio: CString,
    in_len: CString,
    out_embs: CString,
}

impl TitaNetExtractor {
    pub fn load(ort_dll: &Path, model_path: &Path, intra_threads: i32) -> Result<Self, String> {
        let rt = OrtRuntime::load(ort_dll)?;
        let session = rt.create_session(model_path, intra_threads.max(1))?;
        Ok(Self {
            session,
            in_audio: CString::new("audio_signal").unwrap(),
            in_len: CString::new("length").unwrap(),
            out_embs: CString::new("embs").unwrap(),
        })
    }

    pub fn embed_16k(&self, audio_16k: &[f32]) -> Result<Vec<f32>, String> {
        let (mut features, n_frames) = extract_nemo_mel_80(audio_16k);
        if n_frames < 4 {
            return Err("audio too short for titanet".to_string());
        }
        let mut lengths = [n_frames as i64];
        let feat_shape = [1i64, MEL_BINS as i64, n_frames as i64];
        let len_shape = [1i64];

        let rt = self.session.rt;
        let t_feat = rt.create_f32_tensor(&mut features, &feat_shape)?;
        let t_len = rt.create_i64_tensor(&mut lengths, &len_shape)?;

        let outs = self.session.run(
            &[self.in_audio.as_ptr(), self.in_len.as_ptr()],
            &[t_feat.val as *const _, t_len.val as *const _],
            &[self.out_embs.as_ptr()],
        )?;
        let raw = outs[0].as_f32_slice()?;
        let norm: f32 = raw.iter().map(|&x| x * x).sum::<f32>().sqrt();
        if !norm.is_finite() || norm <= 0.0 {
            return Err("titanet zero embedding".to_string());
        }
        Ok(raw.iter().map(|&x| x / norm).collect())
    }
}
