#[cfg(windows)]
mod win {
    use std::ffi::OsStr;
    use std::io;
    use std::os::windows::ffi::OsStrExt;
    use std::ptr::{null, null_mut};
    use std::sync::Arc;
    use std::time::{Duration, Instant};
    use windows_sys::Win32::Foundation::{
        CloseHandle, ERROR_FILE_NOT_FOUND, ERROR_IO_PENDING, ERROR_PIPE_BUSY, GENERIC_READ,
        GENERIC_WRITE, GetLastError, HANDLE, INVALID_HANDLE_VALUE, WAIT_OBJECT_0, WAIT_TIMEOUT,
    };
    use windows_sys::Win32::Storage::FileSystem::{
        CreateFileW, FILE_FLAG_OVERLAPPED, OPEN_EXISTING, ReadFile, WriteFile,
    };
    use windows_sys::Win32::System::IO::{CancelIoEx, GetOverlappedResult, OVERLAPPED};
    use windows_sys::Win32::System::Pipes::WaitNamedPipeW;
    use windows_sys::Win32::System::Threading::{CreateEventW, ResetEvent, WaitForSingleObject};

    pub struct PipeHandle {
        raw: HANDLE,
    }

    unsafe impl Send for PipeHandle {}
    unsafe impl Sync for PipeHandle {}

    impl PipeHandle {
        pub fn connect(pipe_name: &str, timeout: Duration) -> io::Result<Arc<Self>> {
            Self::connect_with_access(pipe_name, timeout, GENERIC_READ | GENERIC_WRITE)
        }

        pub fn connect_read_only(pipe_name: &str, timeout: Duration) -> io::Result<Arc<Self>> {
            Self::connect_with_access(pipe_name, timeout, GENERIC_READ)
        }

        fn connect_with_access(
            pipe_name: &str,
            timeout: Duration,
            access: u32,
        ) -> io::Result<Arc<Self>> {
            let full = format!(r"\\.\pipe\{pipe_name}");
            let wide: Vec<u16> = OsStr::new(&full).encode_wide().chain(Some(0)).collect();
            let deadline = Instant::now() + timeout;

            let mut not_found_backoff_ms = 2u64;
            loop {
                let handle = unsafe {
                    CreateFileW(
                        wide.as_ptr(),
                        access,
                        0,
                        null(),
                        OPEN_EXISTING,
                        FILE_FLAG_OVERLAPPED,
                        null_mut(),
                    )
                };
                if handle != INVALID_HANDLE_VALUE {
                    return Ok(Arc::new(Self { raw: handle }));
                }
                let err = unsafe { GetLastError() };
                if Instant::now() >= deadline {
                    return Err(io::Error::from_raw_os_error(err as i32));
                }
                if err == ERROR_PIPE_BUSY {
                    unsafe {
                        WaitNamedPipeW(wide.as_ptr(), 150);
                    }
                } else if err == ERROR_FILE_NOT_FOUND {
                    std::thread::sleep(Duration::from_millis(not_found_backoff_ms));
                    not_found_backoff_ms = (not_found_backoff_ms * 2).min(16);
                } else {
                    return Err(io::Error::from_raw_os_error(err as i32));
                }
            }
        }

        pub fn cancel(&self) {
            unsafe {
                CancelIoEx(self.raw, null());
            }
        }

        pub fn read_overlapped(&self, event: HANDLE, buf: &mut [u8]) -> io::Result<usize> {
            let mut ov: OVERLAPPED = unsafe { std::mem::zeroed() };
            ov.hEvent = event;
            let mut read = 0u32;
            let ok = unsafe {
                ReadFile(
                    self.raw,
                    buf.as_mut_ptr(),
                    buf.len() as u32,
                    &mut read,
                    &mut ov,
                )
            };
            if ok != 0 {
                return Ok(read as usize);
            }
            let err = unsafe { GetLastError() };
            if err != ERROR_IO_PENDING {
                return Err(io::Error::from_raw_os_error(err as i32));
            }
            let done = unsafe { GetOverlappedResult(self.raw, &ov, &mut read, 1) };
            if done == 0 {
                let wait_err = unsafe { GetLastError() };
                return Err(io::Error::from_raw_os_error(wait_err as i32));
            }
            Ok(read as usize)
        }

        pub fn read_overlapped_timeout(
            &self,
            event: &EventHandle,
            buf: &mut [u8],
            timeout_ms: u32,
        ) -> io::Result<Option<usize>> {
            let raw_event = event.raw();
            unsafe {
                ResetEvent(raw_event);
            }
            let mut ov: OVERLAPPED = unsafe { std::mem::zeroed() };
            ov.hEvent = raw_event;
            let mut read = 0u32;
            let ok = unsafe {
                ReadFile(
                    self.raw,
                    buf.as_mut_ptr(),
                    buf.len() as u32,
                    &mut read,
                    &mut ov,
                )
            };
            if ok != 0 {
                return Ok(Some(read as usize));
            }
            let err = unsafe { GetLastError() };
            if err != ERROR_IO_PENDING {
                return Err(io::Error::from_raw_os_error(err as i32));
            }
            let wait = unsafe { WaitForSingleObject(raw_event, timeout_ms) };
            if wait == WAIT_OBJECT_0 {
                let done = unsafe { GetOverlappedResult(self.raw, &ov, &mut read, 1) };
                if done == 0 {
                    let wait_err = unsafe { GetLastError() };
                    return Err(io::Error::from_raw_os_error(wait_err as i32));
                }
                return Ok(Some(read as usize));
            }
            if wait == WAIT_TIMEOUT {
                unsafe {
                    CancelIoEx(self.raw, &ov);
                }
                let done = unsafe { GetOverlappedResult(self.raw, &ov, &mut read, 1) };
                if done != 0 && read > 0 {
                    return Ok(Some(read as usize));
                }
                return Ok(None);
            }
            Err(io::Error::last_os_error())
        }

        pub fn read_exact_overlapped(&self, event: HANDLE, mut buf: &mut [u8]) -> io::Result<()> {
            while !buf.is_empty() {
                let n = self.read_overlapped(event, buf)?;
                if n == 0 {
                    return Err(io::Error::new(
                        io::ErrorKind::UnexpectedEof,
                        "media pipe eof",
                    ));
                }
                buf = &mut buf[n..];
            }
            Ok(())
        }

        pub fn write_all_overlapped(&self, event: HANDLE, mut data: &[u8]) -> io::Result<()> {
            while !data.is_empty() {
                let mut ov: OVERLAPPED = unsafe { std::mem::zeroed() };
                ov.hEvent = event;
                let mut written = 0u32;
                let ok = unsafe {
                    WriteFile(
                        self.raw,
                        data.as_ptr(),
                        data.len() as u32,
                        &mut written,
                        &mut ov,
                    )
                };
                if ok == 0 {
                    let err = unsafe { GetLastError() };
                    if err != ERROR_IO_PENDING {
                        return Err(io::Error::from_raw_os_error(err as i32));
                    }
                    let done = unsafe { GetOverlappedResult(self.raw, &ov, &mut written, 1) };
                    if done == 0 {
                        let wait_err = unsafe { GetLastError() };
                        return Err(io::Error::from_raw_os_error(wait_err as i32));
                    }
                }
                if written == 0 {
                    return Err(io::Error::new(io::ErrorKind::WriteZero, "pipe write zero"));
                }
                data = &data[written as usize..];
            }
            Ok(())
        }
    }

    impl Drop for PipeHandle {
        fn drop(&mut self) {
            unsafe {
                CancelIoEx(self.raw, null());
                CloseHandle(self.raw);
            }
        }
    }

    pub struct EventHandle(HANDLE);

    unsafe impl Send for EventHandle {}

    impl EventHandle {
        pub fn new() -> io::Result<Self> {
            let h = unsafe { CreateEventW(null(), 1, 0, null()) };
            if h.is_null() {
                return Err(io::Error::last_os_error());
            }
            Ok(Self(h))
        }

        pub fn raw(&self) -> HANDLE {
            self.0
        }
    }

    impl Drop for EventHandle {
        fn drop(&mut self) {
            unsafe {
                CloseHandle(self.0);
            }
        }
    }
}

#[cfg(windows)]
pub use win::{EventHandle, PipeHandle};
