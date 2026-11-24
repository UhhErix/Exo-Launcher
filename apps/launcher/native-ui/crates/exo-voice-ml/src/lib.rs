#![allow(clippy::missing_safety_doc)]

pub mod dpdfnet;
pub mod fft;
pub mod mel;
pub mod ort_ffi;
pub mod titanet;
pub mod voxlock;

use std::ffi::{CStr, c_char, c_int};
use std::panic::catch_unwind;
use std::path::Path;
use std::ptr::null_mut;

use dpdfnet::DpdfNetStream;
use titanet::TitaNetExtractor;
use voxlock::VoxLockStream;

fn cstr_to_path<'a>(p: *const c_char) -> Option<&'a Path> {
    if p.is_null() {
        return None;
    }
    unsafe { CStr::from_ptr(p) }.to_str().ok().map(Path::new)
}

#[unsafe(no_mangle)]
pub extern "C" fn exo_voice_ml_version() -> u32 {
    1
}

#[unsafe(no_mangle)]
pub extern "C" fn exo_voice_dpdfnet_create(
    ort_dll_utf8: *const c_char,
    model_utf8: *const c_char,
    intra_threads: c_int,
) -> *mut DpdfNetStream {
    catch_unwind(|| {
        let (Some(ort), Some(model)) = (cstr_to_path(ort_dll_utf8), cstr_to_path(model_utf8))
        else {
            return null_mut();
        };
        match DpdfNetStream::load(ort, model, intra_threads) {
            Ok(s) => Box::into_raw(Box::new(s)),
            Err(_) => null_mut(),
        }
    })
    .unwrap_or(null_mut())
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn exo_voice_dpdfnet_destroy(handle: *mut DpdfNetStream) {
    let _ = catch_unwind(|| {
        if !handle.is_null() {
            drop(unsafe { Box::from_raw(handle) });
        }
    });
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn exo_voice_dpdfnet_set_atten_lim(handle: *mut DpdfNetStream, db: f32) {
    let _ = catch_unwind(|| {
        if let Some(s) = unsafe { handle.as_mut() } {
            s.set_atten_lim_db(db);
        }
    });
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn exo_voice_dpdfnet_sample_rate(handle: *const DpdfNetStream) -> c_int {
    catch_unwind(|| unsafe { handle.as_ref() }.map_or(0, |s| s.sample_rate() as c_int)).unwrap_or(0)
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn exo_voice_dpdfnet_process_48k_stereo(
    handle: *mut DpdfNetStream,
    stereo: *mut f32,
    float_count: c_int,
) -> f32 {
    catch_unwind(|| {
        if handle.is_null() || stereo.is_null() || float_count <= 0 {
            return f32::NAN;
        }
        let s = unsafe { &mut *handle };
        let buf = unsafe { std::slice::from_raw_parts_mut(stereo, float_count as usize) };
        s.process_48k_stereo(buf).unwrap_or(f32::NAN)
    })
    .unwrap_or(f32::NAN)
}

#[unsafe(no_mangle)]
pub extern "C" fn exo_voice_titanet_create(
    ort_dll_utf8: *const c_char,
    model_utf8: *const c_char,
    intra_threads: c_int,
) -> *mut TitaNetExtractor {
    catch_unwind(|| {
        let (Some(ort), Some(model)) = (cstr_to_path(ort_dll_utf8), cstr_to_path(model_utf8))
        else {
            return null_mut();
        };
        match TitaNetExtractor::load(ort, model, intra_threads) {
            Ok(e) => Box::into_raw(Box::new(e)),
            Err(_) => null_mut(),
        }
    })
    .unwrap_or(null_mut())
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn exo_voice_titanet_destroy(handle: *mut TitaNetExtractor) {
    let _ = catch_unwind(|| {
        if !handle.is_null() {
            drop(unsafe { Box::from_raw(handle) });
        }
    });
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn exo_voice_titanet_embed(
    handle: *const TitaNetExtractor,
    samples_16k: *const f32,
    sample_count: c_int,
    out_embedding: *mut f32,
    out_capacity: c_int,
) -> c_int {
    catch_unwind(|| {
        if handle.is_null()
            || samples_16k.is_null()
            || out_embedding.is_null()
            || sample_count <= 0
            || out_capacity <= 0
        {
            return 0;
        }
        let ext = unsafe { &*handle };
        let audio = unsafe { std::slice::from_raw_parts(samples_16k, sample_count as usize) };
        match ext.embed_16k(audio) {
            Ok(emb) if emb.len() <= out_capacity as usize => {
                let dst = unsafe { std::slice::from_raw_parts_mut(out_embedding, emb.len()) };
                dst.copy_from_slice(&emb);
                emb.len() as c_int
            }
            _ => 0,
        }
    })
    .unwrap_or(0)
}

#[unsafe(no_mangle)]
pub extern "C" fn exo_voice_voxlock_create(
    ort_dll_utf8: *const c_char,
    enroll_utf8: *const c_char,
    chunk_utf8: *const c_char,
    intra_threads: c_int,
) -> *mut VoxLockStream {
    catch_unwind(|| {
        let (Some(ort), Some(enroll), Some(chunk)) = (
            cstr_to_path(ort_dll_utf8),
            cstr_to_path(enroll_utf8),
            cstr_to_path(chunk_utf8),
        ) else {
            return null_mut();
        };
        match VoxLockStream::load(ort, enroll, chunk, intra_threads) {
            Ok(v) => Box::into_raw(Box::new(v)),
            Err(_) => null_mut(),
        }
    })
    .unwrap_or(null_mut())
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn exo_voice_voxlock_destroy(handle: *mut VoxLockStream) {
    let _ = catch_unwind(|| {
        if !handle.is_null() {
            drop(unsafe { Box::from_raw(handle) });
        }
    });
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn exo_voice_voxlock_enroll_16k(
    handle: *mut VoxLockStream,
    samples_16k: *const f32,
    sample_count: c_int,
) -> c_int {
    catch_unwind(|| {
        if handle.is_null() || samples_16k.is_null() || sample_count <= 0 {
            return 0;
        }
        let v = unsafe { &mut *handle };
        let audio = unsafe { std::slice::from_raw_parts(samples_16k, sample_count as usize) };
        if v.enroll_16k(audio).is_ok() { 1 } else { 0 }
    })
    .unwrap_or(0)
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn exo_voice_voxlock_process_16k(
    handle: *mut VoxLockStream,
    in_16k: *const f32,
    out_16k: *mut f32,
    sample_count: c_int,
) -> f32 {
    catch_unwind(|| {
        if handle.is_null() || in_16k.is_null() || out_16k.is_null() || sample_count <= 0 {
            return f32::NAN;
        }
        let v = unsafe { &mut *handle };
        let inp = unsafe { std::slice::from_raw_parts(in_16k, sample_count as usize) };
        let out = unsafe { std::slice::from_raw_parts_mut(out_16k, sample_count as usize) };
        v.process_16k(inp, out).unwrap_or(f32::NAN)
    })
    .unwrap_or(f32::NAN)
}
