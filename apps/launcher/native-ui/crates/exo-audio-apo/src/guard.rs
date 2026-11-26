use std::panic::{AssertUnwindSafe, catch_unwind};

#[cfg(windows)]
use windows::Win32::Foundation::E_FAIL;
#[cfg(windows)]
use windows_core::{Error, HRESULT};

pub fn guarded<T>(on_panic: impl FnOnce() -> T, body: impl FnOnce() -> T) -> T {
    catch_unwind(AssertUnwindSafe(body)).unwrap_or_else(|_| on_panic())
}

pub fn panicked(body: impl FnOnce()) -> bool {
    catch_unwind(AssertUnwindSafe(body)).is_err()
}

#[cfg(windows)]
pub fn guard_hresult(body: impl FnOnce() -> HRESULT) -> HRESULT {
    guarded(|| E_FAIL, body)
}

#[cfg(windows)]
pub fn guard_result<T>(body: impl FnOnce() -> windows_core::Result<T>) -> windows_core::Result<T> {
    guarded(|| Err(Error::from(E_FAIL)), body)
}

pub unsafe fn pass_through(input: *const f32, output: *mut f32, samples: usize) {
    if samples == 0 || input.is_null() || output.is_null() || input == output.cast_const() {
        return;
    }
    unsafe { std::ptr::copy(input, output, samples) };
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn guarded_returns_body_value_without_panic() {
        assert_eq!(guarded(|| 0, || 7), 7);
        assert!(!panicked(|| {}));
    }

    #[test]
    fn guarded_turns_panic_into_fallback() {
        let value = guarded(|| -1, || -> i32 { panic!("apo test panic") });
        assert_eq!(value, -1);
        assert!(panicked(|| panic!("apo test panic")));
    }

    #[cfg(windows)]
    #[test]
    fn guard_hresult_turns_panic_into_e_fail() {
        use windows::Win32::Foundation::S_OK;

        assert_eq!(guard_hresult(|| S_OK), S_OK);
        assert_eq!(guard_hresult(|| panic!("apo test panic")), E_FAIL);
    }

    #[cfg(windows)]
    #[test]
    fn guard_result_turns_panic_into_e_fail() {
        assert_eq!(guard_result(|| Ok(5u32)).expect("ok passes through"), 5);
        let err = guard_result(|| -> windows_core::Result<u32> { panic!("apo test panic") })
            .expect_err("panic becomes error");
        assert_eq!(err.code(), E_FAIL);
    }

    #[test]
    fn pass_through_copies_input_to_output() {
        let input = [0.25_f32, -0.5, 0.75, -1.0];
        let mut output = [9.0_f32; 4];
        unsafe { pass_through(input.as_ptr(), output.as_mut_ptr(), input.len()) };
        assert_eq!(output, input);
    }

    #[test]
    fn pass_through_leaves_in_place_and_empty_buffers_alone() {
        let mut buf = [0.1_f32, 0.2, 0.3];
        let ptr = buf.as_mut_ptr();
        unsafe { pass_through(ptr, ptr, buf.len()) };
        assert_eq!(buf, [0.1, 0.2, 0.3]);

        let input = [1.0_f32; 2];
        let mut output = [3.0_f32; 2];
        unsafe { pass_through(input.as_ptr(), output.as_mut_ptr(), 0) };
        assert_eq!(output, [3.0, 3.0]);
        unsafe { pass_through(std::ptr::null(), output.as_mut_ptr(), 2) };
        assert_eq!(output, [3.0, 3.0]);
    }
}
