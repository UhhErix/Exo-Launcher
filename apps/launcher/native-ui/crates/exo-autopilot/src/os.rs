use crate::topology::CpuSetInfo;
use serde::{Deserialize, Serialize};

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ProcessEntry {
    pub pid: u32,
    pub parent_pid: u32,
    pub name: String,
    pub session_id: Option<u32>,
}

#[derive(Debug, Clone, Copy, Default, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct MemorySnapshot {
    pub total_phys_bytes: u64,
    pub avail_phys_bytes: u64,
    pub standby_bytes: Option<u64>,
}

#[derive(Debug, Clone, Copy, Default, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct TimerResolutionInfo {
    pub min_100ns: u32,
    pub max_100ns: u32,
    pub current_100ns: u32,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ServiceSnapshot {
    pub name: String,
    pub running: bool,
    pub paused: bool,
    pub can_pause: bool,
    pub can_stop: bool,
}

pub const PRIORITY_NORMAL: u32 = 0x0000_0020;
pub const PRIORITY_BELOW_NORMAL: u32 = 0x0000_4000;
pub const PRIORITY_IDLE: u32 = 0x0000_0040;

pub trait AutopilotOs: Send + Sync {
    fn current_pid(&self) -> u32;
    fn current_session_id(&self) -> u32;
    fn read_cpu_brand(&self) -> String;
    fn read_system_cpu_sets(&self) -> Vec<CpuSetInfo>;
    fn enumerate_processes(&self) -> Vec<ProcessEntry>;
    fn foreground_pid(&self) -> Option<u32>;
    fn process_cpu_time_100ns(&self, pid: u32) -> Option<u64>;
    fn get_priority_class(&self, pid: u32) -> Option<u32>;
    fn set_priority_class(&self, pid: u32, priority_class: u32) -> bool;
    fn get_default_cpu_sets(&self, pid: u32) -> Option<Vec<u32>>;
    fn set_default_cpu_sets(&self, pid: u32, ids: &[u32]) -> bool;
    fn set_eco_qos(&self, pid: u32, enabled: bool) -> bool;
    fn query_timer_resolution(&self) -> Option<TimerResolutionInfo>;
    fn set_timer_resolution(&self, desired_100ns: u32, set: bool) -> Option<u32>;
    fn memory_snapshot(&self) -> MemorySnapshot;
    fn purge_standby_list(&self) -> Result<(), String>;
    fn list_active_bits_jobs(&self) -> Vec<String>;
    fn suspend_bits_job(&self, job_id: &str) -> bool;
    fn resume_bits_job(&self, job_id: &str) -> bool;
    fn query_service(&self, name: &str) -> Option<ServiceSnapshot>;
    fn pause_service(&self, name: &str) -> bool;
    fn stop_service(&self, name: &str) -> bool;
    fn continue_service(&self, name: &str) -> bool;
    fn start_service(&self, name: &str) -> bool;
    fn is_on_battery(&self) -> bool;
    fn active_power_scheme(&self) -> Option<String>;
    fn set_power_scheme(&self, guid_str: &str) -> bool;
    fn read_mouse_accel(&self) -> Option<[i32; 3]>;
    fn write_mouse_accel(&self, values: [i32; 3]) -> bool;
}

#[cfg(windows)]
pub struct Win32Os;

#[cfg(windows)]
impl Win32Os {
    pub fn new() -> Self {
        Self
    }
}

#[cfg(windows)]
impl Default for Win32Os {
    fn default() -> Self {
        Self::new()
    }
}

#[cfg(windows)]
mod win32_impl {
    use super::*;
    use std::ffi::c_void;
    use windows::Win32::Foundation::{CloseHandle, FILETIME, HANDLE, LUID};
    use windows::Win32::Networking::BackgroundIntelligentTransferService::{
        BG_JOB_STATE_CONNECTING, BG_JOB_STATE_QUEUED, BG_JOB_STATE_TRANSFERRING,
        BackgroundCopyManager, IBackgroundCopyJob, IBackgroundCopyManager,
    };
    use windows::Win32::Security::{
        AdjustTokenPrivileges, LUID_AND_ATTRIBUTES, LookupPrivilegeValueW, SE_PRIVILEGE_ENABLED,
        TOKEN_ADJUST_PRIVILEGES, TOKEN_PRIVILEGES, TOKEN_QUERY,
    };
    use windows::Win32::System::Com::{
        CLSCTX_LOCAL_SERVER, COINIT_MULTITHREADED, CoCreateInstance, CoInitializeEx, CoUninitialize,
    };
    use windows::Win32::System::Diagnostics::ToolHelp::{
        CreateToolhelp32Snapshot, PROCESSENTRY32W, Process32FirstW, Process32NextW,
        TH32CS_SNAPPROCESS,
    };
    use windows::Win32::System::LibraryLoader::{GetModuleHandleW, GetProcAddress};
    use windows::Win32::System::Power::{
        GetSystemPowerStatus, PowerGetActiveScheme, PowerSetActiveScheme, SYSTEM_POWER_STATUS,
    };
    use windows::Win32::System::Registry::{HKEY_LOCAL_MACHINE, RRF_RT_REG_SZ, RegGetValueW};
    use windows::Win32::System::Services::{
        CloseServiceHandle, ControlService, OpenSCManagerW, OpenServiceW, QueryServiceStatus,
        SC_MANAGER_CONNECT, SERVICE_ACCEPT_PAUSE_CONTINUE, SERVICE_ACCEPT_STOP,
        SERVICE_CONTROL_CONTINUE, SERVICE_CONTROL_PAUSE, SERVICE_CONTROL_STOP,
        SERVICE_PAUSE_CONTINUE, SERVICE_PAUSED, SERVICE_QUERY_STATUS, SERVICE_RUNNING,
        SERVICE_START, SERVICE_STATUS, SERVICE_STOP, StartServiceW,
    };
    use windows::Win32::System::SystemInformation::{
        GetSystemCpuSetInformation, GlobalMemoryStatusEx, MEMORYSTATUSEX,
        SYSTEM_CPU_SET_INFORMATION,
    };
    use windows::Win32::System::Threading::{
        GetCurrentProcess, GetPriorityClass, GetProcessDefaultCpuSets, GetProcessTimes,
        OpenProcess, OpenProcessToken, PROCESS_CREATION_FLAGS,
        PROCESS_POWER_THROTTLING_CURRENT_VERSION, PROCESS_POWER_THROTTLING_EXECUTION_SPEED,
        PROCESS_POWER_THROTTLING_STATE, PROCESS_QUERY_LIMITED_INFORMATION, PROCESS_SET_INFORMATION,
        ProcessPowerThrottling, SetPriorityClass, SetProcessDefaultCpuSets, SetProcessInformation,
    };
    #[link(name = "kernel32")]
    unsafe extern "system" {
        fn ProcessIdToSessionId(dw_process_id: u32, p_session_id: *mut u32) -> i32;
    }
    use windows::Win32::UI::WindowsAndMessaging::{
        GetForegroundWindow, GetWindowThreadProcessId, SPI_GETMOUSE, SPI_SETMOUSE,
        SYSTEM_PARAMETERS_INFO_UPDATE_FLAGS, SystemParametersInfoW,
    };
    use windows_core::{GUID, PCWSTR, w};

    type NtQueryTimerResFn = unsafe extern "system" fn(*mut u32, *mut u32, *mut u32) -> i32;
    type NtSetTimerResFn = unsafe extern "system" fn(u32, u8, *mut u32) -> i32;
    type NtSetSystemInfoFn = unsafe extern "system" fn(i32, *mut c_void, u32) -> i32;
    type NtQuerySystemInfoFn = unsafe extern "system" fn(i32, *mut c_void, u32, *mut u32) -> i32;

    #[repr(C)]
    struct SystemMemoryListInformation {
        zero_page_count: usize,
        free_page_count: usize,
        modified_page_count: usize,
        modified_no_write_page_count: usize,
        bad_page_count: usize,
        page_count_by_priority: [usize; 8],
        repurposed_pages_by_priority: [usize; 8],
        modified_page_count_page_file: usize,
    }

    unsafe fn ntdll_proc(name: &[u8]) -> Option<*const c_void> {
        let hmod = unsafe { GetModuleHandleW(w!("ntdll.dll")).ok()? };
        let sym = windows_core::PCSTR(name.as_ptr());
        unsafe { GetProcAddress(hmod, sym).map(|f| f as *const c_void) }
    }

    fn format_guid(g: GUID) -> String {
        format!(
            "{:08x}-{:04x}-{:04x}-{:02x}{:02x}-{:02x}{:02x}{:02x}{:02x}{:02x}{:02x}",
            g.data1,
            g.data2,
            g.data3,
            g.data4[0],
            g.data4[1],
            g.data4[2],
            g.data4[3],
            g.data4[4],
            g.data4[5],
            g.data4[6],
            g.data4[7]
        )
    }

    fn parse_guid(s: &str) -> Option<GUID> {
        let clean = s.trim().trim_matches(|c| c == '{' || c == '}');
        let parts: Vec<&str> = clean.split('-').collect();
        if parts.len() != 5 {
            return None;
        }
        let d1 = u32::from_str_radix(parts[0], 16).ok()?;
        let d2 = u16::from_str_radix(parts[1], 16).ok()?;
        let d3 = u16::from_str_radix(parts[2], 16).ok()?;
        let p3 = u16::from_str_radix(parts[3], 16).ok()?;
        let p4 = u64::from_str_radix(parts[4], 16).ok()?;
        let mut d4 = [0u8; 8];
        d4[0] = (p3 >> 8) as u8;
        d4[1] = (p3 & 0xff) as u8;
        for i in 0..6 {
            d4[2 + i] = ((p4 >> ((5 - i) * 8)) & 0xff) as u8;
        }
        Some(GUID::from_values(d1, d2, d3, d4))
    }

    fn to_wide_null(s: &str) -> Vec<u16> {
        s.encode_utf16().chain(std::iter::once(0)).collect()
    }

    impl AutopilotOs for Win32Os {
        fn current_pid(&self) -> u32 {
            std::process::id()
        }

        fn current_session_id(&self) -> u32 {
            let mut sid = 0u32;
            unsafe {
                let _ = ProcessIdToSessionId(std::process::id(), &mut sid);
            }
            sid
        }

        fn read_cpu_brand(&self) -> String {
            let mut buf = [0u16; 128];
            let mut size = (buf.len() * 2) as u32;
            let ok = unsafe {
                RegGetValueW(
                    HKEY_LOCAL_MACHINE,
                    w!("HARDWARE\\DESCRIPTION\\System\\CentralProcessor\\0"),
                    w!("ProcessorNameString"),
                    RRF_RT_REG_SZ,
                    None,
                    Some(buf.as_mut_ptr().cast()),
                    Some(&mut size),
                )
                .is_ok()
            };
            if !ok || size < 2 {
                return String::new();
            }
            let len = ((size as usize) / 2).min(buf.len());
            let end = buf[..len].iter().position(|&c| c == 0).unwrap_or(len);
            String::from_utf16_lossy(&buf[..end]).trim().to_string()
        }

        fn read_system_cpu_sets(&self) -> Vec<CpuSetInfo> {
            unsafe {
                let mut needed = 0u32;
                let _ = GetSystemCpuSetInformation(None, 0, &mut needed, None, Some(0));
                if needed == 0 || needed > 1024 * 1024 {
                    return Vec::new();
                }
                let mut buf = vec![0u8; needed as usize];
                let mut returned = 0u32;
                let ok = GetSystemCpuSetInformation(
                    Some(buf.as_mut_ptr().cast::<SYSTEM_CPU_SET_INFORMATION>()),
                    needed,
                    &mut returned,
                    None,
                    Some(0),
                )
                .as_bool();
                if !ok || returned == 0 {
                    return Vec::new();
                }
                let mut out = Vec::new();
                let mut offset = 0usize;
                while offset + 32 <= returned as usize {
                    let size =
                        u32::from_ne_bytes(buf[offset..offset + 4].try_into().unwrap()) as usize;
                    if size < 32 || offset + size > returned as usize {
                        break;
                    }
                    let ty = u32::from_ne_bytes(buf[offset + 4..offset + 8].try_into().unwrap());
                    if ty == 0 {
                        let id =
                            u32::from_ne_bytes(buf[offset + 8..offset + 12].try_into().unwrap());
                        let group =
                            u16::from_ne_bytes(buf[offset + 12..offset + 14].try_into().unwrap());
                        let logical_processor_index = buf[offset + 14];
                        let core_index = buf[offset + 15];
                        let last_level_cache_index = buf[offset + 16];
                        let efficiency_class = buf[offset + 18];
                        out.push(CpuSetInfo {
                            id,
                            group,
                            core_index,
                            last_level_cache_index,
                            efficiency_class,
                            logical_processor_index,
                        });
                    }
                    offset += size;
                }
                out
            }
        }

        fn enumerate_processes(&self) -> Vec<ProcessEntry> {
            let mut rows = Vec::new();
            unsafe {
                let Ok(snap) = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0) else {
                    return rows;
                };
                if snap == HANDLE::default() {
                    return rows;
                }
                let mut entry = PROCESSENTRY32W {
                    dwSize: std::mem::size_of::<PROCESSENTRY32W>() as u32,
                    ..Default::default()
                };
                if Process32FirstW(snap, &mut entry).is_ok() {
                    loop {
                        let pid = entry.th32ProcessID;
                        let parent_pid = entry.th32ParentProcessID;
                        let len = entry
                            .szExeFile
                            .iter()
                            .position(|&c| c == 0)
                            .unwrap_or(entry.szExeFile.len());
                        let name = String::from_utf16_lossy(&entry.szExeFile[..len]);
                        let mut sid = 0u32;
                        let session_id = if ProcessIdToSessionId(pid, &mut sid) != 0 {
                            Some(sid)
                        } else {
                            None
                        };
                        rows.push(ProcessEntry {
                            pid,
                            parent_pid,
                            name,
                            session_id,
                        });
                        if Process32NextW(snap, &mut entry).is_err() {
                            break;
                        }
                    }
                }
                let _ = CloseHandle(snap);
            }
            rows
        }

        fn foreground_pid(&self) -> Option<u32> {
            unsafe {
                let hwnd = GetForegroundWindow();
                if hwnd.0.is_null() {
                    return None;
                }
                let mut pid = 0u32;
                GetWindowThreadProcessId(hwnd, Some(&mut pid));
                if pid > 0 { Some(pid) } else { None }
            }
        }

        fn process_cpu_time_100ns(&self, pid: u32) -> Option<u64> {
            unsafe {
                let h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid).ok()?;
                let mut c = FILETIME::default();
                let mut e = FILETIME::default();
                let mut k = FILETIME::default();
                let mut u = FILETIME::default();
                let ok = GetProcessTimes(h, &mut c, &mut e, &mut k, &mut u).is_ok();
                let _ = CloseHandle(h);
                if ok {
                    let kt = ((k.dwHighDateTime as u64) << 32) | (k.dwLowDateTime as u64);
                    let ut = ((u.dwHighDateTime as u64) << 32) | (u.dwLowDateTime as u64);
                    Some(kt.saturating_add(ut))
                } else {
                    None
                }
            }
        }

        fn get_priority_class(&self, pid: u32) -> Option<u32> {
            unsafe {
                let h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid).ok()?;
                let p = GetPriorityClass(h);
                let _ = CloseHandle(h);
                if p != 0 { Some(p) } else { None }
            }
        }

        fn set_priority_class(&self, pid: u32, priority_class: u32) -> bool {
            unsafe {
                let Ok(h) = OpenProcess(PROCESS_SET_INFORMATION, false, pid) else {
                    return false;
                };
                let ok = SetPriorityClass(h, PROCESS_CREATION_FLAGS(priority_class)).is_ok();
                let _ = CloseHandle(h);
                ok
            }
        }

        fn get_default_cpu_sets(&self, pid: u32) -> Option<Vec<u32>> {
            unsafe {
                let h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid).ok()?;
                let mut required = 0u32;
                let _ = GetProcessDefaultCpuSets(h, None, &mut required);
                if required == 0 {
                    let _ = CloseHandle(h);
                    return Some(Vec::new());
                }
                let mut ids = vec![0u32; required as usize];
                let ok = GetProcessDefaultCpuSets(h, Some(&mut ids), &mut required).as_bool();
                let _ = CloseHandle(h);
                if ok {
                    ids.truncate(required as usize);
                    Some(ids)
                } else {
                    None
                }
            }
        }

        fn set_default_cpu_sets(&self, pid: u32, ids: &[u32]) -> bool {
            unsafe {
                let Ok(h) = OpenProcess(
                    PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_SET_INFORMATION,
                    false,
                    pid,
                ) else {
                    return false;
                };
                let slice = if ids.is_empty() { None } else { Some(ids) };
                let ok = SetProcessDefaultCpuSets(h, slice).as_bool();
                let _ = CloseHandle(h);
                ok
            }
        }

        fn set_eco_qos(&self, pid: u32, enabled: bool) -> bool {
            unsafe {
                let Ok(h) = OpenProcess(PROCESS_SET_INFORMATION, false, pid) else {
                    return false;
                };
                let state = PROCESS_POWER_THROTTLING_STATE {
                    Version: PROCESS_POWER_THROTTLING_CURRENT_VERSION,
                    ControlMask: PROCESS_POWER_THROTTLING_EXECUTION_SPEED,
                    StateMask: if enabled {
                        PROCESS_POWER_THROTTLING_EXECUTION_SPEED
                    } else {
                        0
                    },
                };
                let ok = SetProcessInformation(
                    h,
                    ProcessPowerThrottling,
                    (&state as *const PROCESS_POWER_THROTTLING_STATE).cast(),
                    std::mem::size_of::<PROCESS_POWER_THROTTLING_STATE>() as u32,
                )
                .is_ok();
                let _ = CloseHandle(h);
                ok
            }
        }

        fn query_timer_resolution(&self) -> Option<TimerResolutionInfo> {
            unsafe {
                let p = ntdll_proc(b"NtQueryTimerResolution\0")?;
                let f: NtQueryTimerResFn = std::mem::transmute(p);
                let mut min = 0u32;
                let mut max = 0u32;
                let mut cur = 0u32;
                if f(&mut min, &mut max, &mut cur) == 0 {
                    Some(TimerResolutionInfo {
                        min_100ns: min,
                        max_100ns: max,
                        current_100ns: cur,
                    })
                } else {
                    None
                }
            }
        }

        fn set_timer_resolution(&self, desired_100ns: u32, set: bool) -> Option<u32> {
            unsafe {
                let p = ntdll_proc(b"NtSetTimerResolution\0")?;
                let f: NtSetTimerResFn = std::mem::transmute(p);
                let mut cur = 0u32;
                let status = f(desired_100ns, if set { 1 } else { 0 }, &mut cur);
                if status == 0 { Some(cur) } else { None }
            }
        }

        fn memory_snapshot(&self) -> MemorySnapshot {
            let mut ms = MEMORYSTATUSEX {
                dwLength: std::mem::size_of::<MEMORYSTATUSEX>() as u32,
                ..Default::default()
            };
            unsafe {
                let _ = GlobalMemoryStatusEx(&mut ms);
            }
            let standby_bytes = unsafe {
                ntdll_proc(b"NtQuerySystemInformation\0").and_then(|p| {
                    let f: NtQuerySystemInfoFn = std::mem::transmute(p);
                    let mut info = std::mem::zeroed::<SystemMemoryListInformation>();
                    let mut ret = 0u32;
                    let status = f(
                        80,
                        (&mut info as *mut SystemMemoryListInformation).cast(),
                        std::mem::size_of::<SystemMemoryListInformation>() as u32,
                        &mut ret,
                    );
                    if status == 0 {
                        let pages: usize = info.page_count_by_priority.iter().sum();
                        Some((pages as u64).saturating_mul(4096))
                    } else {
                        None
                    }
                })
            };
            MemorySnapshot {
                total_phys_bytes: ms.ullTotalPhys,
                avail_phys_bytes: ms.ullAvailPhys,
                standby_bytes,
            }
        }

        fn purge_standby_list(&self) -> Result<(), String> {
            unsafe {
                let mut token = HANDLE::default();
                OpenProcessToken(
                    GetCurrentProcess(),
                    TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY,
                    &mut token,
                )
                .map_err(|e| format!("OpenProcessToken: {e}"))?;
                let mut luid = LUID::default();
                let lookup_ok =
                    LookupPrivilegeValueW(None, w!("SeProfileSingleProcessPrivilege"), &mut luid);
                if let Err(e) = lookup_ok {
                    let _ = CloseHandle(token);
                    return Err(format!("LookupPrivilegeValueW: {e}"));
                }
                let tp = TOKEN_PRIVILEGES {
                    PrivilegeCount: 1,
                    Privileges: [LUID_AND_ATTRIBUTES {
                        Luid: luid,
                        Attributes: SE_PRIVILEGE_ENABLED,
                    }],
                };
                let adj = AdjustTokenPrivileges(token, false, Some(&tp), 0, None, None);
                let _ = CloseHandle(token);
                if let Err(e) = adj {
                    return Err(format!("AdjustTokenPrivileges: {e}"));
                }
                let Some(p) = ntdll_proc(b"NtSetSystemInformation\0") else {
                    return Err("NtSetSystemInformation unavailable".into());
                };
                let f: NtSetSystemInfoFn = std::mem::transmute(p);
                let mut cmd: i32 = 4;
                let status = f(80, (&mut cmd as *mut i32).cast(), 4);
                if status == 0 {
                    Ok(())
                } else {
                    Err(format!(
                        "NtSetSystemInformation NTSTATUS 0x{:08X}",
                        status as u32
                    ))
                }
            }
        }

        fn list_active_bits_jobs(&self) -> Vec<String> {
            let mut out = Vec::new();
            unsafe {
                let hr = CoInitializeEx(None, COINIT_MULTITHREADED);
                let need_uninit = hr.is_ok();
                if let Ok(mgr) = CoCreateInstance::<_, IBackgroundCopyManager>(
                    &BackgroundCopyManager,
                    None,
                    CLSCTX_LOCAL_SERVER,
                ) && let Ok(en) = mgr.EnumJobs(0)
                    && let Ok(count) = en.GetCount()
                {
                    for _ in 0..count {
                        let mut jobs: [Option<IBackgroundCopyJob>; 1] = [None];
                        let mut fetched = 0u32;
                        if en.Next(&mut jobs, &mut fetched).is_ok()
                            && fetched == 1
                            && let Some(job) = &jobs[0]
                            && let Ok(state) = job.GetState()
                            && (state == BG_JOB_STATE_TRANSFERRING
                                || state == BG_JOB_STATE_CONNECTING
                                || state == BG_JOB_STATE_QUEUED)
                            && let Ok(id) = job.GetId()
                        {
                            out.push(format_guid(id));
                        }
                    }
                }
                if need_uninit {
                    CoUninitialize();
                }
            }
            out
        }

        fn suspend_bits_job(&self, job_id: &str) -> bool {
            let Some(guid) = parse_guid(job_id) else {
                return false;
            };
            unsafe {
                let hr = CoInitializeEx(None, COINIT_MULTITHREADED);
                let need_uninit = hr.is_ok();
                let ok = CoCreateInstance::<_, IBackgroundCopyManager>(
                    &BackgroundCopyManager,
                    None,
                    CLSCTX_LOCAL_SERVER,
                )
                .ok()
                .and_then(|mgr| mgr.GetJob(&guid).ok())
                .is_some_and(|job| job.Suspend().is_ok());
                if need_uninit {
                    CoUninitialize();
                }
                ok
            }
        }

        fn resume_bits_job(&self, job_id: &str) -> bool {
            let Some(guid) = parse_guid(job_id) else {
                return false;
            };
            unsafe {
                let hr = CoInitializeEx(None, COINIT_MULTITHREADED);
                let need_uninit = hr.is_ok();
                let ok = CoCreateInstance::<_, IBackgroundCopyManager>(
                    &BackgroundCopyManager,
                    None,
                    CLSCTX_LOCAL_SERVER,
                )
                .ok()
                .and_then(|mgr| mgr.GetJob(&guid).ok())
                .is_some_and(|job| job.Resume().is_ok());
                if need_uninit {
                    CoUninitialize();
                }
                ok
            }
        }

        fn query_service(&self, name: &str) -> Option<ServiceSnapshot> {
            unsafe {
                let scm = OpenSCManagerW(None, None, SC_MANAGER_CONNECT).ok()?;
                let wname = to_wide_null(name);
                let svc = match OpenServiceW(scm, PCWSTR(wname.as_ptr()), SERVICE_QUERY_STATUS) {
                    Ok(s) => s,
                    Err(_) => {
                        let _ = CloseServiceHandle(scm);
                        return None;
                    }
                };
                let mut status = SERVICE_STATUS::default();
                let ok = QueryServiceStatus(svc, &mut status).is_ok();
                let _ = CloseServiceHandle(svc);
                let _ = CloseServiceHandle(scm);
                if !ok {
                    return None;
                }
                Some(ServiceSnapshot {
                    name: name.to_string(),
                    running: status.dwCurrentState == SERVICE_RUNNING,
                    paused: status.dwCurrentState == SERVICE_PAUSED,
                    can_pause: (status.dwControlsAccepted & SERVICE_ACCEPT_PAUSE_CONTINUE) != 0,
                    can_stop: (status.dwControlsAccepted & SERVICE_ACCEPT_STOP) != 0,
                })
            }
        }

        fn pause_service(&self, name: &str) -> bool {
            unsafe {
                let Ok(scm) = OpenSCManagerW(None, None, SC_MANAGER_CONNECT) else {
                    return false;
                };
                let wname = to_wide_null(name);
                let Ok(svc) = OpenServiceW(scm, PCWSTR(wname.as_ptr()), SERVICE_PAUSE_CONTINUE)
                else {
                    let _ = CloseServiceHandle(scm);
                    return false;
                };
                let mut st = SERVICE_STATUS::default();
                let ok = ControlService(svc, SERVICE_CONTROL_PAUSE, &mut st).is_ok();
                let _ = CloseServiceHandle(svc);
                let _ = CloseServiceHandle(scm);
                ok
            }
        }

        fn stop_service(&self, name: &str) -> bool {
            unsafe {
                let Ok(scm) = OpenSCManagerW(None, None, SC_MANAGER_CONNECT) else {
                    return false;
                };
                let wname = to_wide_null(name);
                let Ok(svc) = OpenServiceW(scm, PCWSTR(wname.as_ptr()), SERVICE_STOP) else {
                    let _ = CloseServiceHandle(scm);
                    return false;
                };
                let mut st = SERVICE_STATUS::default();
                let ok = ControlService(svc, SERVICE_CONTROL_STOP, &mut st).is_ok();
                let _ = CloseServiceHandle(svc);
                let _ = CloseServiceHandle(scm);
                ok
            }
        }

        fn continue_service(&self, name: &str) -> bool {
            unsafe {
                let Ok(scm) = OpenSCManagerW(None, None, SC_MANAGER_CONNECT) else {
                    return false;
                };
                let wname = to_wide_null(name);
                let Ok(svc) = OpenServiceW(scm, PCWSTR(wname.as_ptr()), SERVICE_PAUSE_CONTINUE)
                else {
                    let _ = CloseServiceHandle(scm);
                    return false;
                };
                let mut st = SERVICE_STATUS::default();
                let ok = ControlService(svc, SERVICE_CONTROL_CONTINUE, &mut st).is_ok();
                let _ = CloseServiceHandle(svc);
                let _ = CloseServiceHandle(scm);
                ok
            }
        }

        fn start_service(&self, name: &str) -> bool {
            unsafe {
                let Ok(scm) = OpenSCManagerW(None, None, SC_MANAGER_CONNECT) else {
                    return false;
                };
                let wname = to_wide_null(name);
                let Ok(svc) = OpenServiceW(scm, PCWSTR(wname.as_ptr()), SERVICE_START) else {
                    let _ = CloseServiceHandle(scm);
                    return false;
                };
                let ok = StartServiceW(svc, None).is_ok();
                let _ = CloseServiceHandle(svc);
                let _ = CloseServiceHandle(scm);
                ok
            }
        }

        fn is_on_battery(&self) -> bool {
            let mut sps = SYSTEM_POWER_STATUS::default();
            unsafe {
                if GetSystemPowerStatus(&mut sps).is_ok() {
                    sps.ACLineStatus == 0
                } else {
                    false
                }
            }
        }

        fn active_power_scheme(&self) -> Option<String> {
            unsafe {
                let mut ptr: *mut GUID = std::ptr::null_mut();
                if PowerGetActiveScheme(None, &mut ptr).is_ok() && !ptr.is_null() {
                    let g = *ptr;
                    let _ = windows::Win32::Foundation::LocalFree(Some(
                        windows::Win32::Foundation::HLOCAL(ptr.cast()),
                    ));
                    Some(format_guid(g))
                } else {
                    None
                }
            }
        }

        fn set_power_scheme(&self, guid_str: &str) -> bool {
            let Some(g) = parse_guid(guid_str) else {
                return false;
            };
            unsafe { PowerSetActiveScheme(None, Some(&g)).is_ok() }
        }

        fn read_mouse_accel(&self) -> Option<[i32; 3]> {
            let mut vals = [0i32; 3];
            let ok = unsafe {
                SystemParametersInfoW(
                    SPI_GETMOUSE,
                    0,
                    Some(vals.as_mut_ptr().cast()),
                    SYSTEM_PARAMETERS_INFO_UPDATE_FLAGS(0),
                )
                .is_ok()
            };
            if ok { Some(vals) } else { None }
        }

        fn write_mouse_accel(&self, mut values: [i32; 3]) -> bool {
            unsafe {
                SystemParametersInfoW(
                    SPI_SETMOUSE,
                    0,
                    Some(values.as_mut_ptr().cast()),
                    SYSTEM_PARAMETERS_INFO_UPDATE_FLAGS(0x0003),
                )
                .is_ok()
            }
        }
    }
}
