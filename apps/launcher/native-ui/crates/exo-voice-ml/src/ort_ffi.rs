use std::ffi::{CStr, CString, c_char, c_int, c_void};
use std::path::Path;
use std::ptr::{null, null_mut};
use std::sync::{Mutex, OnceLock};

#[repr(C)]
pub struct OrtEnv {
    _private: [u8; 0],
}
#[repr(C)]
pub struct OrtStatus {
    _private: [u8; 0],
}
#[repr(C)]
pub struct OrtMemoryInfo {
    _private: [u8; 0],
}
#[repr(C)]
pub struct OrtSession {
    _private: [u8; 0],
}
#[repr(C)]
pub struct OrtValue {
    _private: [u8; 0],
}
#[repr(C)]
pub struct OrtRunOptions {
    _private: [u8; 0],
}
#[repr(C)]
pub struct OrtSessionOptions {
    _private: [u8; 0],
}
#[repr(C)]
pub struct OrtTensorTypeAndShapeInfo {
    _private: [u8; 0],
}
#[repr(C)]
pub struct OrtAllocator {
    _private: [u8; 0],
}
#[repr(C)]
pub struct OrtModelMetadata {
    _private: [u8; 0],
}

#[repr(C)]
struct OrtApiBase {
    get_api: unsafe extern "system" fn(u32) -> *const *const c_void,
    get_version_string: unsafe extern "system" fn() -> *const c_char,
}

const ORT_API_VERSION: u32 = 17;
pub const ONNX_TENSOR_ELEMENT_DATA_TYPE_FLOAT: c_int = 1;
pub const ONNX_TENSOR_ELEMENT_DATA_TYPE_INT64: c_int = 7;

const IDX_GET_ERROR_MESSAGE: usize = 2;
const IDX_CREATE_ENV: usize = 3;
const IDX_CREATE_SESSION: usize = 7;
const IDX_RUN: usize = 9;
const IDX_CREATE_SESSION_OPTIONS: usize = 10;
const IDX_SET_GRAPH_OPT: usize = 23;
const IDX_SET_INTRA_THREADS: usize = 24;
const IDX_SET_INTER_THREADS: usize = 25;
const IDX_CREATE_TENSOR_WITH_DATA: usize = 49;
const IDX_GET_TENSOR_MUTABLE_DATA: usize = 51;
const IDX_GET_DIMS_COUNT: usize = 61;
const IDX_GET_DIMS: usize = 62;
const IDX_GET_SHAPE_ELEM_COUNT: usize = 64;
const IDX_GET_TENSOR_TYPE_AND_SHAPE: usize = 65;
const IDX_CREATE_CPU_MEM_INFO: usize = 69;
const IDX_ALLOCATOR_FREE: usize = 76;
const IDX_GET_DEFAULT_ALLOCATOR: usize = 78;
const IDX_RELEASE_ENV: usize = 92;
const IDX_RELEASE_STATUS: usize = 93;
const IDX_RELEASE_MEM_INFO: usize = 94;
const IDX_RELEASE_SESSION: usize = 95;
const IDX_RELEASE_VALUE: usize = 96;
const IDX_RELEASE_SHAPE_INFO: usize = 99;
const IDX_RELEASE_SESSION_OPTIONS: usize = 100;
const IDX_GET_MODEL_METADATA: usize = 111;
const IDX_METADATA_LOOKUP: usize = 116;
const IDX_RELEASE_MODEL_METADATA: usize = 118;

#[cfg(windows)]
#[link(name = "kernel32")]
unsafe extern "system" {
    fn LoadLibraryW(lp_lib_file_name: *const u16) -> *mut c_void;
    fn GetProcAddress(h_module: *mut c_void, lp_proc_name: *const c_char) -> *const c_void;
}

pub struct OrtRuntime {
    api: *const *const c_void,
    env: *mut OrtEnv,
    mem_info: *mut OrtMemoryInfo,
    allocator: *mut OrtAllocator,
}

unsafe impl Send for OrtRuntime {}
unsafe impl Sync for OrtRuntime {}

static ORT_GLOBAL: OnceLock<Mutex<Option<&'static OrtRuntime>>> = OnceLock::new();

impl OrtRuntime {
    pub fn load(dll_path: &Path) -> Result<&'static OrtRuntime, String> {
        let lock = ORT_GLOBAL.get_or_init(|| Mutex::new(None));
        let mut guard = lock.lock().map_err(|_| "ort lock poisoned".to_string())?;
        if let Some(rt) = *guard {
            return Ok(rt);
        }

        #[cfg(windows)]
        let api = {
            use std::os::windows::ffi::OsStrExt;
            let wide: Vec<u16> = dll_path.as_os_str().encode_wide().chain([0]).collect();
            let handle = unsafe { LoadLibraryW(wide.as_ptr()) };
            if handle.is_null() {
                return Err(format!("LoadLibraryW failed for {}", dll_path.display()));
            }
            let sym = CString::new("OrtGetApiBase").unwrap();
            let proc = unsafe { GetProcAddress(handle, sym.as_ptr()) };
            if proc.is_null() {
                return Err("OrtGetApiBase not found".to_string());
            }
            let get_base: unsafe extern "system" fn() -> *const OrtApiBase =
                unsafe { std::mem::transmute(proc) };
            let base = unsafe { get_base() };
            if base.is_null() {
                return Err("OrtGetApiBase returned null".to_string());
            }
            let api_ptr = unsafe { ((*base).get_api)(ORT_API_VERSION) };
            if api_ptr.is_null() {
                return Err("OrtGetApi(17) returned null".to_string());
            }
            api_ptr
        };

        #[cfg(not(windows))]
        let api: *const *const c_void = {
            let _ = dll_path;
            return Err("onnxruntime dynamic loader is windows-only".to_string());
        };

        let mut env: *mut OrtEnv = null_mut();
        let log_id = CString::new("exo-voice-ml").unwrap();
        let create_env: unsafe extern "system" fn(
            c_int,
            *const c_char,
            *mut *mut OrtEnv,
        ) -> *mut OrtStatus = unsafe { std::mem::transmute(*api.add(IDX_CREATE_ENV)) };
        Self::check_raw(api, unsafe { create_env(3, log_id.as_ptr(), &mut env) })?;

        let mut mem_info: *mut OrtMemoryInfo = null_mut();
        let create_mem: unsafe extern "system" fn(
            c_int,
            c_int,
            *mut *mut OrtMemoryInfo,
        ) -> *mut OrtStatus = unsafe { std::mem::transmute(*api.add(IDX_CREATE_CPU_MEM_INFO)) };
        if let Err(e) = Self::check_raw(api, unsafe { create_mem(1, 0, &mut mem_info) }) {
            let rel: unsafe extern "system" fn(*mut OrtEnv) =
                unsafe { std::mem::transmute(*api.add(IDX_RELEASE_ENV)) };
            unsafe { rel(env) };
            return Err(e);
        }

        let mut allocator: *mut OrtAllocator = null_mut();
        let get_alloc: unsafe extern "system" fn(*mut *mut OrtAllocator) -> *mut OrtStatus =
            unsafe { std::mem::transmute(*api.add(IDX_GET_DEFAULT_ALLOCATOR)) };
        if let Err(e) = Self::check_raw(api, unsafe { get_alloc(&mut allocator) }) {
            let rel_m: unsafe extern "system" fn(*mut OrtMemoryInfo) =
                unsafe { std::mem::transmute(*api.add(IDX_RELEASE_MEM_INFO)) };
            let rel_e: unsafe extern "system" fn(*mut OrtEnv) =
                unsafe { std::mem::transmute(*api.add(IDX_RELEASE_ENV)) };
            unsafe {
                rel_m(mem_info);
                rel_e(env);
            }
            return Err(e);
        }

        let leaked: &'static OrtRuntime = Box::leak(Box::new(OrtRuntime {
            api,
            env,
            mem_info,
            allocator,
        }));
        *guard = Some(leaked);
        Ok(leaked)
    }

    fn check_raw(api: *const *const c_void, status: *mut OrtStatus) -> Result<(), String> {
        if status.is_null() {
            return Ok(());
        }
        let get_msg: unsafe extern "system" fn(*const OrtStatus) -> *const c_char =
            unsafe { std::mem::transmute(*api.add(IDX_GET_ERROR_MESSAGE)) };
        let rel: unsafe extern "system" fn(*mut OrtStatus) =
            unsafe { std::mem::transmute(*api.add(IDX_RELEASE_STATUS)) };
        let msg = unsafe {
            let ptr = get_msg(status);
            let s = if ptr.is_null() {
                "unknown ort error".to_string()
            } else {
                CStr::from_ptr(ptr).to_string_lossy().into_owned()
            };
            rel(status);
            s
        };
        Err(msg)
    }

    pub fn check(&self, status: *mut OrtStatus) -> Result<(), String> {
        Self::check_raw(self.api, status)
    }

    pub fn create_session(
        &'static self,
        model_path: &Path,
        intra_threads: i32,
    ) -> Result<OrtSessionHandle, String> {
        let mut opts: *mut OrtSessionOptions = null_mut();
        let create_opts: unsafe extern "system" fn(*mut *mut OrtSessionOptions) -> *mut OrtStatus =
            unsafe { std::mem::transmute(*self.api.add(IDX_CREATE_SESSION_OPTIONS)) };
        self.check(unsafe { create_opts(&mut opts) })?;

        let rel_opts: unsafe extern "system" fn(*mut OrtSessionOptions) =
            unsafe { std::mem::transmute(*self.api.add(IDX_RELEASE_SESSION_OPTIONS)) };

        let setup = (|| -> Result<*mut OrtSession, String> {
            let set_intra: unsafe extern "system" fn(
                *mut OrtSessionOptions,
                c_int,
            ) -> *mut OrtStatus =
                unsafe { std::mem::transmute(*self.api.add(IDX_SET_INTRA_THREADS)) };
            let set_inter: unsafe extern "system" fn(
                *mut OrtSessionOptions,
                c_int,
            ) -> *mut OrtStatus =
                unsafe { std::mem::transmute(*self.api.add(IDX_SET_INTER_THREADS)) };
            let set_opt: unsafe extern "system" fn(
                *mut OrtSessionOptions,
                c_int,
            ) -> *mut OrtStatus = unsafe { std::mem::transmute(*self.api.add(IDX_SET_GRAPH_OPT)) };
            self.check(unsafe { set_intra(opts, intra_threads.max(1)) })?;
            self.check(unsafe { set_inter(opts, 1) })?;
            self.check(unsafe { set_opt(opts, 99) })?;

            #[cfg(windows)]
            let session = {
                use std::os::windows::ffi::OsStrExt;
                let wide: Vec<u16> = model_path.as_os_str().encode_wide().chain([0]).collect();
                let mut sess: *mut OrtSession = null_mut();
                let create_sess: unsafe extern "system" fn(
                    *const OrtEnv,
                    *const u16,
                    *const OrtSessionOptions,
                    *mut *mut OrtSession,
                ) -> *mut OrtStatus =
                    unsafe { std::mem::transmute(*self.api.add(IDX_CREATE_SESSION)) };
                self.check(unsafe { create_sess(self.env, wide.as_ptr(), opts, &mut sess) })?;
                sess
            };

            #[cfg(not(windows))]
            let session: *mut OrtSession = {
                let _ = model_path;
                return Err("windows-only".to_string());
            };

            Ok(session)
        })();

        unsafe { rel_opts(opts) };
        let session = setup?;
        Ok(OrtSessionHandle { rt: self, session })
    }

    pub fn create_f32_tensor(
        &self,
        data: &mut [f32],
        shape: &[i64],
    ) -> Result<OrtValueHandle<'_>, String> {
        let mut val: *mut OrtValue = null_mut();
        let f: unsafe extern "system" fn(
            *const OrtMemoryInfo,
            *mut c_void,
            usize,
            *const i64,
            usize,
            c_int,
            *mut *mut OrtValue,
        ) -> *mut OrtStatus =
            unsafe { std::mem::transmute(*self.api.add(IDX_CREATE_TENSOR_WITH_DATA)) };
        self.check(unsafe {
            f(
                self.mem_info,
                data.as_mut_ptr().cast(),
                std::mem::size_of_val(data),
                shape.as_ptr(),
                shape.len(),
                ONNX_TENSOR_ELEMENT_DATA_TYPE_FLOAT,
                &mut val,
            )
        })?;
        Ok(OrtValueHandle { rt: self, val })
    }

    pub fn create_i64_tensor(
        &self,
        data: &mut [i64],
        shape: &[i64],
    ) -> Result<OrtValueHandle<'_>, String> {
        let mut val: *mut OrtValue = null_mut();
        let f: unsafe extern "system" fn(
            *const OrtMemoryInfo,
            *mut c_void,
            usize,
            *const i64,
            usize,
            c_int,
            *mut *mut OrtValue,
        ) -> *mut OrtStatus =
            unsafe { std::mem::transmute(*self.api.add(IDX_CREATE_TENSOR_WITH_DATA)) };
        self.check(unsafe {
            f(
                self.mem_info,
                data.as_mut_ptr().cast(),
                std::mem::size_of_val(data),
                shape.as_ptr(),
                shape.len(),
                ONNX_TENSOR_ELEMENT_DATA_TYPE_INT64,
                &mut val,
            )
        })?;
        Ok(OrtValueHandle { rt: self, val })
    }
}

pub struct OrtSessionHandle {
    pub rt: &'static OrtRuntime,
    pub session: *mut OrtSession,
}

unsafe impl Send for OrtSessionHandle {}

impl OrtSessionHandle {
    pub fn metadata_str(&self, key: &str) -> Result<Option<String>, String> {
        let mut meta: *mut OrtModelMetadata = null_mut();
        let get_meta: unsafe extern "system" fn(
            *const OrtSession,
            *mut *mut OrtModelMetadata,
        ) -> *mut OrtStatus =
            unsafe { std::mem::transmute(*self.rt.api.add(IDX_GET_MODEL_METADATA)) };
        self.rt
            .check(unsafe { get_meta(self.session, &mut meta) })?;

        let rel_meta: unsafe extern "system" fn(*mut OrtModelMetadata) =
            unsafe { std::mem::transmute(*self.rt.api.add(IDX_RELEASE_MODEL_METADATA)) };
        let ckey = CString::new(key).map_err(|_| "invalid metadata key".to_string())?;
        let mut out_ptr: *mut c_char = null_mut();
        let lookup: unsafe extern "system" fn(
            *const OrtModelMetadata,
            *mut OrtAllocator,
            *const c_char,
            *mut *mut c_char,
        ) -> *mut OrtStatus = unsafe { std::mem::transmute(*self.rt.api.add(IDX_METADATA_LOOKUP)) };
        let res = self
            .rt
            .check(unsafe { lookup(meta, self.rt.allocator, ckey.as_ptr(), &mut out_ptr) });
        unsafe { rel_meta(meta) };
        res?;
        if out_ptr.is_null() {
            return Ok(None);
        }
        let value = unsafe { CStr::from_ptr(out_ptr).to_string_lossy().into_owned() };
        let free_fn: unsafe extern "system" fn(*mut OrtAllocator, *mut c_void) -> *mut OrtStatus =
            unsafe { std::mem::transmute(*self.rt.api.add(IDX_ALLOCATOR_FREE)) };
        let _ = unsafe { free_fn(self.rt.allocator, out_ptr.cast()) };
        Ok(Some(value))
    }

    pub fn run<'a>(
        &self,
        input_names: &[*const c_char],
        inputs: &[*const OrtValue],
        output_names: &[*const c_char],
    ) -> Result<Vec<OrtValueHandle<'a>>, String> {
        let mut raw_outs: Vec<*mut OrtValue> = vec![null_mut(); output_names.len()];
        let run_fn: unsafe extern "system" fn(
            *mut OrtSession,
            *const OrtRunOptions,
            *const *const c_char,
            *const *const OrtValue,
            usize,
            *const *const c_char,
            usize,
            *mut *mut OrtValue,
        ) -> *mut OrtStatus = unsafe { std::mem::transmute(*self.rt.api.add(IDX_RUN)) };
        self.rt.check(unsafe {
            run_fn(
                self.session,
                null(),
                input_names.as_ptr(),
                inputs.as_ptr(),
                inputs.len(),
                output_names.as_ptr(),
                output_names.len(),
                raw_outs.as_mut_ptr(),
            )
        })?;
        Ok(raw_outs
            .into_iter()
            .map(|val| OrtValueHandle { rt: self.rt, val })
            .collect())
    }
}

impl Drop for OrtSessionHandle {
    fn drop(&mut self) {
        if !self.session.is_null() {
            let rel: unsafe extern "system" fn(*mut OrtSession) =
                unsafe { std::mem::transmute(*self.rt.api.add(IDX_RELEASE_SESSION)) };
            unsafe { rel(self.session) };
            self.session = null_mut();
        }
    }
}

pub struct OrtValueHandle<'a> {
    rt: &'a OrtRuntime,
    pub val: *mut OrtValue,
}

impl<'a> OrtValueHandle<'a> {
    pub fn as_f32_slice(&self) -> Result<&[f32], String> {
        let count = self.element_count()?;
        let mut ptr: *mut c_void = null_mut();
        let f: unsafe extern "system" fn(*mut OrtValue, *mut *mut c_void) -> *mut OrtStatus =
            unsafe { std::mem::transmute(*self.rt.api.add(IDX_GET_TENSOR_MUTABLE_DATA)) };
        self.rt.check(unsafe { f(self.val, &mut ptr) })?;
        if ptr.is_null() {
            return Err("null tensor data".to_string());
        }
        Ok(unsafe { std::slice::from_raw_parts(ptr.cast::<f32>(), count) })
    }

    pub fn shape(&self) -> Result<Vec<i64>, String> {
        let mut info: *mut OrtTensorTypeAndShapeInfo = null_mut();
        let get_info: unsafe extern "system" fn(
            *const OrtValue,
            *mut *mut OrtTensorTypeAndShapeInfo,
        ) -> *mut OrtStatus =
            unsafe { std::mem::transmute(*self.rt.api.add(IDX_GET_TENSOR_TYPE_AND_SHAPE)) };
        self.rt.check(unsafe { get_info(self.val, &mut info) })?;
        let rel_info: unsafe extern "system" fn(*mut OrtTensorTypeAndShapeInfo) =
            unsafe { std::mem::transmute(*self.rt.api.add(IDX_RELEASE_SHAPE_INFO)) };

        let res = (|| -> Result<Vec<i64>, String> {
            let mut n_dims: usize = 0;
            let get_n: unsafe extern "system" fn(
                *const OrtTensorTypeAndShapeInfo,
                *mut usize,
            ) -> *mut OrtStatus =
                unsafe { std::mem::transmute(*self.rt.api.add(IDX_GET_DIMS_COUNT)) };
            self.rt.check(unsafe { get_n(info, &mut n_dims) })?;
            let mut dims = vec![0i64; n_dims];
            let get_d: unsafe extern "system" fn(
                *const OrtTensorTypeAndShapeInfo,
                *mut i64,
                usize,
            ) -> *mut OrtStatus = unsafe { std::mem::transmute(*self.rt.api.add(IDX_GET_DIMS)) };
            self.rt
                .check(unsafe { get_d(info, dims.as_mut_ptr(), n_dims) })?;
            Ok(dims)
        })();
        unsafe { rel_info(info) };
        res
    }

    pub fn element_count(&self) -> Result<usize, String> {
        let mut info: *mut OrtTensorTypeAndShapeInfo = null_mut();
        let get_info: unsafe extern "system" fn(
            *const OrtValue,
            *mut *mut OrtTensorTypeAndShapeInfo,
        ) -> *mut OrtStatus =
            unsafe { std::mem::transmute(*self.rt.api.add(IDX_GET_TENSOR_TYPE_AND_SHAPE)) };
        self.rt.check(unsafe { get_info(self.val, &mut info) })?;
        let rel_info: unsafe extern "system" fn(*mut OrtTensorTypeAndShapeInfo) =
            unsafe { std::mem::transmute(*self.rt.api.add(IDX_RELEASE_SHAPE_INFO)) };

        let mut count: usize = 0;
        let get_c: unsafe extern "system" fn(
            *const OrtTensorTypeAndShapeInfo,
            *mut usize,
        ) -> *mut OrtStatus =
            unsafe { std::mem::transmute(*self.rt.api.add(IDX_GET_SHAPE_ELEM_COUNT)) };
        let res = self.rt.check(unsafe { get_c(info, &mut count) });
        unsafe { rel_info(info) };
        res?;
        Ok(count)
    }
}

impl Drop for OrtValueHandle<'_> {
    fn drop(&mut self) {
        if !self.val.is_null() {
            let rel: unsafe extern "system" fn(*mut OrtValue) =
                unsafe { std::mem::transmute(*self.rt.api.add(IDX_RELEASE_VALUE)) };
            unsafe { rel(self.val) };
            self.val = null_mut();
        }
    }
}
