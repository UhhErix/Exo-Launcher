use std::ffi::OsStr;
use std::os::windows::ffi::OsStrExt;
use std::ptr::null_mut;

use crate::win32::Handle;

const EVT_QUERY_CHANNEL_PATH: u32 = 0x1;
const EVT_QUERY_REVERSE_DIRECTION: u32 = 0x200;

#[link(name = "wevtapi")]
unsafe extern "system" {
    fn EvtQuery(session: Handle, path: *const u16, query: *const u16, flags: u32) -> Handle;
    fn EvtNext(
        result_set: Handle,
        event_array_size: u32,
        event_array: *mut Handle,
        timeout: u32,
        flags: u32,
        returned: *mut u32,
    ) -> i32;
    fn EvtClose(object: Handle) -> i32;
}

#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct StabilityEventSummary {
    pub window_hours: u32,
    pub whea_errors: u32,
    pub gpu_tdr_resets: u32,
    pub kernel_power_41: u32,
    pub bugcheck_1001: u32,
}

impl StabilityEventSummary {
    pub fn has_recent_crash_or_instability(&self) -> bool {
        self.whea_errors > 0
            || self.gpu_tdr_resets > 0
            || self.kernel_power_41 > 0
            || self.bugcheck_1001 > 0
    }
}

pub fn query_stability_events(window_hours: u32) -> StabilityEventSummary {
    let ms = (window_hours as u64).saturating_mul(3_600_000).max(60_000);
    let whea_xpath = format!(
        "*[System[Provider[@Name='Microsoft-Windows-WHEA-Logger'] and (EventID=17 or EventID=18 or EventID=19 or EventID=20) and TimeCreated[timediff(@SystemTime) <= {ms}]]]"
    );
    let tdr_xpath = format!(
        "*[System[Provider[@Name='Display'] and EventID=4101 and TimeCreated[timediff(@SystemTime) <= {ms}]]]"
    );
    let kp41_xpath = format!(
        "*[System[Provider[@Name='Microsoft-Windows-Kernel-Power'] and EventID=41 and TimeCreated[timediff(@SystemTime) <= {ms}]]]"
    );
    let bugcheck_xpath = format!(
        "*[System[(Provider[@Name='Microsoft-Windows-WER-SystemErrorReporting'] or Provider[@Name='BugCheck']) and EventID=1001 and TimeCreated[timediff(@SystemTime) <= {ms}]]]"
    );

    StabilityEventSummary {
        window_hours,
        whea_errors: count_system_events(&whea_xpath, 64),
        gpu_tdr_resets: count_system_events(&tdr_xpath, 64),
        kernel_power_41: count_system_events(&kp41_xpath, 16),
        bugcheck_1001: count_system_events(&bugcheck_xpath, 16),
    }
}

pub fn query_boot_crash_window(window_minutes: u32) -> StabilityEventSummary {
    let ms = (window_minutes as u64).saturating_mul(60_000).max(60_000);
    let whea_xpath = format!(
        "*[System[Provider[@Name='Microsoft-Windows-WHEA-Logger'] and (EventID=17 or EventID=18 or EventID=19 or EventID=20) and TimeCreated[timediff(@SystemTime) <= {ms}]]]"
    );
    let tdr_xpath = format!(
        "*[System[Provider[@Name='Display'] and EventID=4101 and TimeCreated[timediff(@SystemTime) <= {ms}]]]"
    );
    let kp41_xpath = format!(
        "*[System[Provider[@Name='Microsoft-Windows-Kernel-Power'] and EventID=41 and TimeCreated[timediff(@SystemTime) <= {ms}]]]"
    );
    let bugcheck_xpath = format!(
        "*[System[(Provider[@Name='Microsoft-Windows-WER-SystemErrorReporting'] or Provider[@Name='BugCheck']) and EventID=1001 and TimeCreated[timediff(@SystemTime) <= {ms}]]]"
    );

    StabilityEventSummary {
        window_hours: 0,
        whea_errors: count_system_events(&whea_xpath, 32),
        gpu_tdr_resets: count_system_events(&tdr_xpath, 32),
        kernel_power_41: count_system_events(&kp41_xpath, 16),
        bugcheck_1001: count_system_events(&bugcheck_xpath, 16),
    }
}

fn count_system_events(xpath: &str, cap: u32) -> u32 {
    let channel: Vec<u16> = OsStr::new("System")
        .encode_wide()
        .chain(std::iter::once(0))
        .collect();
    let query: Vec<u16> = OsStr::new(xpath)
        .encode_wide()
        .chain(std::iter::once(0))
        .collect();

    let results = unsafe {
        EvtQuery(
            null_mut(),
            channel.as_ptr(),
            query.as_ptr(),
            EVT_QUERY_CHANNEL_PATH | EVT_QUERY_REVERSE_DIRECTION,
        )
    };
    if results.is_null() {
        return 0;
    }

    let mut total = 0u32;
    let mut batch: [Handle; 16] = [null_mut(); 16];
    while total < cap {
        let mut returned = 0u32;
        let ok = unsafe {
            EvtNext(
                results,
                batch.len() as u32,
                batch.as_mut_ptr(),
                250,
                0,
                &mut returned,
            )
        };
        if ok == 0 || returned == 0 {
            break;
        }
        for &ev in batch.iter().take(returned as usize) {
            if !ev.is_null() {
                unsafe {
                    EvtClose(ev);
                }
            }
        }
        total = total.saturating_add(returned).min(cap);
    }

    unsafe {
        EvtClose(results);
    }
    total
}
