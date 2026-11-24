use crate::InstallError;
use crate::swap::{APP_USER_MODEL_ID, InstallRoot, graceful_close_in_dir};
use std::fs;
use std::path::{Path, PathBuf};

#[derive(Debug, Clone)]
pub struct RegistrationPaths {
    pub start_menu_shortcut: PathBuf,
    pub desktop_shortcut: PathBuf,
    pub registry_subkey: String,
}

pub fn shortcut_paths(root: &InstallRoot) -> Result<(PathBuf, PathBuf), InstallError> {
    if let Some(dir) = &root.shortcuts_dir {
        return Ok((
            dir.join("StartMenu").join("Exo Launcher.lnk"),
            dir.join("Desktop").join("Exo Launcher.lnk"),
        ));
    }
    let appdata = std::env::var_os("APPDATA")
        .ok_or_else(|| InstallError::InvalidArg("APPDATA not set".into()))?;
    let userprofile = std::env::var_os("USERPROFILE")
        .ok_or_else(|| InstallError::InvalidArg("USERPROFILE not set".into()))?;
    let start_menu =
        PathBuf::from(appdata).join(r"Microsoft\Windows\Start Menu\Programs\Exo Launcher.lnk");
    let desktop = PathBuf::from(userprofile).join(r"Desktop\Exo Launcher.lnk");
    Ok((start_menu, desktop))
}

#[cfg(windows)]
fn to_wide(s: &str) -> Vec<u16> {
    s.encode_utf16().chain(std::iter::once(0)).collect()
}

#[cfg(windows)]
pub fn create_shortcut(
    lnk_path: &Path,
    target_exe: &Path,
    working_dir: &Path,
    description: &str,
    aumid: &str,
) -> Result<(), InstallError> {
    use windows::Win32::Foundation::PROPERTYKEY;
    use windows::Win32::System::Com::StructuredStorage::PROPVARIANT;
    use windows::Win32::System::Com::{
        CLSCTX_INPROC_SERVER, COINIT_APARTMENTTHREADED, CoCreateInstance, CoInitializeEx,
        CoUninitialize, IPersistFile,
    };
    use windows::Win32::UI::Shell::PropertiesSystem::IPropertyStore;
    use windows::Win32::UI::Shell::{IShellLinkW, ShellLink};
    use windows::core::{GUID, Interface, PCWSTR};

    if let Some(parent) = lnk_path.parent() {
        fs::create_dir_all(parent)?;
    }

    let hr_init = unsafe { CoInitializeEx(None, COINIT_APARTMENTTHREADED) };
    let should_uninit = hr_init.is_ok();

    let res = (|| -> Result<(), InstallError> {
        let link: IShellLinkW = unsafe { CoCreateInstance(&ShellLink, None, CLSCTX_INPROC_SERVER) }
            .map_err(|e| InstallError::Win32(format!("CoCreateInstance ShellLink: {e}")))?;

        let target_w = to_wide(&target_exe.to_string_lossy());
        let dir_w = to_wide(&working_dir.to_string_lossy());
        let desc_w = to_wide(description);

        unsafe {
            link.SetPath(PCWSTR(target_w.as_ptr()))
                .map_err(|e| InstallError::Win32(format!("SetPath: {e}")))?;
            link.SetWorkingDirectory(PCWSTR(dir_w.as_ptr()))
                .map_err(|e| InstallError::Win32(format!("SetWorkingDirectory: {e}")))?;
            link.SetDescription(PCWSTR(desc_w.as_ptr()))
                .map_err(|e| InstallError::Win32(format!("SetDescription: {e}")))?;
            link.SetIconLocation(PCWSTR(target_w.as_ptr()), 0)
                .map_err(|e| InstallError::Win32(format!("SetIconLocation: {e}")))?;
        }

        let prop_store: IPropertyStore = link
            .cast()
            .map_err(|e| InstallError::Win32(format!("QI IPropertyStore: {e}")))?;
        let pkey_aumid = PROPERTYKEY {
            fmtid: GUID::from_u128(0x9F4C2855_9F79_4B39_A8D0_E1D42DE1D5F3),
            pid: 5,
        };
        let prop_val = PROPVARIANT::from(aumid);
        unsafe {
            prop_store
                .SetValue(&pkey_aumid, &prop_val)
                .map_err(|e| InstallError::Win32(format!("SetValue AUMID: {e}")))?;
            prop_store
                .Commit()
                .map_err(|e| InstallError::Win32(format!("Commit IPropertyStore: {e}")))?;
        }

        let persist: IPersistFile = link
            .cast()
            .map_err(|e| InstallError::Win32(format!("QI IPersistFile: {e}")))?;
        let lnk_w = to_wide(&lnk_path.to_string_lossy());
        unsafe {
            persist
                .Save(PCWSTR(lnk_w.as_ptr()), true)
                .map_err(|e| InstallError::Win32(format!("IPersistFile::Save: {e}")))?;
        }
        Ok(())
    })();

    if should_uninit {
        unsafe { CoUninitialize() };
    }
    res
}

#[cfg(not(windows))]
pub fn create_shortcut(
    lnk_path: &Path,
    target_exe: &Path,
    _working_dir: &Path,
    _description: &str,
    _aumid: &str,
) -> Result<(), InstallError> {
    if let Some(parent) = lnk_path.parent() {
        fs::create_dir_all(parent)?;
    }
    fs::write(lnk_path, target_exe.to_string_lossy().as_bytes())?;
    Ok(())
}

#[cfg(windows)]
fn set_reg_sz(
    hkey: windows::Win32::System::Registry::HKEY,
    name: &str,
    value: &str,
) -> Result<(), InstallError> {
    use windows::Win32::System::Registry::{REG_SZ, RegSetValueExW};
    use windows::core::PCWSTR;
    let name_w = to_wide(name);
    let val_w = to_wide(value);
    let bytes = unsafe { std::slice::from_raw_parts(val_w.as_ptr() as *const u8, val_w.len() * 2) };
    let st = unsafe { RegSetValueExW(hkey, PCWSTR(name_w.as_ptr()), Some(0), REG_SZ, Some(bytes)) };
    if st.is_err() {
        return Err(InstallError::Win32(format!(
            "RegSetValueExW({name}): {st:?}"
        )));
    }
    Ok(())
}

#[cfg(windows)]
fn set_reg_dword(
    hkey: windows::Win32::System::Registry::HKEY,
    name: &str,
    value: u32,
) -> Result<(), InstallError> {
    use windows::Win32::System::Registry::{REG_DWORD, RegSetValueExW};
    use windows::core::PCWSTR;
    let name_w = to_wide(name);
    let bytes = value.to_le_bytes();
    let st = unsafe {
        RegSetValueExW(
            hkey,
            PCWSTR(name_w.as_ptr()),
            Some(0),
            REG_DWORD,
            Some(&bytes),
        )
    };
    if st.is_err() {
        return Err(InstallError::Win32(format!(
            "RegSetValueExW({name}): {st:?}"
        )));
    }
    Ok(())
}

#[cfg(windows)]
pub fn register_install(
    root: &InstallRoot,
    version: &str,
    installed_bytes: u64,
    installer_exe: &Path,
) -> Result<RegistrationPaths, InstallError> {
    use windows::Win32::System::Registry::{
        HKEY, HKEY_CURRENT_USER, KEY_WRITE, REG_OPTION_NON_VOLATILE, RegCloseKey, RegCreateKeyExW,
    };
    use windows::core::PCWSTR;

    let (start_menu, desktop) = shortcut_paths(root)?;
    let target_exe = root.app_dir().join("ExoLauncher.exe");
    create_shortcut(
        &start_menu,
        &target_exe,
        &root.base_dir,
        "Exo Launcher",
        APP_USER_MODEL_ID,
    )?;
    create_shortcut(
        &desktop,
        &target_exe,
        &root.base_dir,
        "Exo Launcher",
        APP_USER_MODEL_ID,
    )?;

    let subkey_w = to_wide(&root.registry_subkey);
    let mut hkey = HKEY::default();
    let st = unsafe {
        RegCreateKeyExW(
            HKEY_CURRENT_USER,
            PCWSTR(subkey_w.as_ptr()),
            Some(0),
            None,
            REG_OPTION_NON_VOLATILE,
            KEY_WRITE,
            None,
            &mut hkey,
            None,
        )
    };
    if st.is_err() {
        return Err(InstallError::Win32(format!("RegCreateKeyExW: {st:?}")));
    }

    let uninstall_cmd = if root.is_test_root {
        format!(
            "\"{}\" --uninstall --test-root \"{}\"",
            installer_exe.display(),
            root.base_dir.display()
        )
    } else {
        format!("\"{}\" --uninstall", installer_exe.display())
    };

    let write_res = (|| -> Result<(), InstallError> {
        set_reg_sz(hkey, "DisplayName", "Exo Launcher")?;
        set_reg_sz(hkey, "DisplayVersion", version)?;
        set_reg_sz(hkey, "Publisher", "Exo")?;
        set_reg_sz(hkey, "InstallLocation", &root.app_dir().to_string_lossy())?;
        set_reg_sz(hkey, "DisplayIcon", &target_exe.to_string_lossy())?;
        set_reg_sz(hkey, "UninstallString", &uninstall_cmd)?;
        set_reg_sz(hkey, "URLInfoAbout", "https://getexo.app")?;
        set_reg_sz(hkey, "ExoInstallerEngine", "exo-installer-rust")?;
        set_reg_dword(hkey, "NoModify", 1)?;
        set_reg_dword(hkey, "NoRepair", 1)?;
        set_reg_dword(hkey, "EstimatedSize", (installed_bytes / 1024) as u32)?;
        Ok(())
    })();

    unsafe {
        let _ = RegCloseKey(hkey);
    }
    write_res?;

    Ok(RegistrationPaths {
        start_menu_shortcut: start_menu,
        desktop_shortcut: desktop,
        registry_subkey: root.registry_subkey.clone(),
    })
}

#[cfg(windows)]
pub fn registry_key_exists(subkey: &str) -> bool {
    use windows::Win32::System::Registry::{
        HKEY, HKEY_CURRENT_USER, KEY_READ, RegCloseKey, RegOpenKeyExW,
    };
    use windows::core::PCWSTR;
    let subkey_w = to_wide(subkey);
    let mut hkey = HKEY::default();
    let st = unsafe {
        RegOpenKeyExW(
            HKEY_CURRENT_USER,
            PCWSTR(subkey_w.as_ptr()),
            Some(0),
            KEY_READ,
            &mut hkey,
        )
    };
    if st.is_ok() {
        unsafe {
            let _ = RegCloseKey(hkey);
        }
        true
    } else {
        false
    }
}

#[cfg(windows)]
pub fn remove_registry_key(subkey: &str) -> Result<(), InstallError> {
    use windows::Win32::System::Registry::{HKEY_CURRENT_USER, RegDeleteTreeW};
    use windows::core::PCWSTR;
    let subkey_w = to_wide(subkey);
    let st = unsafe { RegDeleteTreeW(HKEY_CURRENT_USER, PCWSTR(subkey_w.as_ptr())) };
    if st.is_err() && registry_key_exists(subkey) {
        return Err(InstallError::Win32(format!("RegDeleteTreeW: {st:?}")));
    }
    Ok(())
}

#[cfg(not(windows))]
pub fn register_install(
    root: &InstallRoot,
    _version: &str,
    _installed_bytes: u64,
    _installer_exe: &Path,
) -> Result<RegistrationPaths, InstallError> {
    let (start_menu, desktop) = shortcut_paths(root)?;
    let target_exe = root.app_dir().join("ExoLauncher.exe");
    create_shortcut(
        &start_menu,
        &target_exe,
        &root.base_dir,
        "Exo Launcher",
        APP_USER_MODEL_ID,
    )?;
    create_shortcut(
        &desktop,
        &target_exe,
        &root.base_dir,
        "Exo Launcher",
        APP_USER_MODEL_ID,
    )?;
    Ok(RegistrationPaths {
        start_menu_shortcut: start_menu,
        desktop_shortcut: desktop,
        registry_subkey: root.registry_subkey.clone(),
    })
}

#[cfg(not(windows))]
pub fn registry_key_exists(_subkey: &str) -> bool {
    false
}

#[cfg(not(windows))]
pub fn remove_registry_key(_subkey: &str) -> Result<(), InstallError> {
    Ok(())
}

pub fn uninstall_clean(root: &InstallRoot, keep_data: bool) -> Result<(), InstallError> {
    let app_dir = root.app_dir();
    if app_dir.exists() {
        graceful_close_in_dir(&app_dir, 15_000)?;
    }

    let (start_menu, desktop) = shortcut_paths(root)?;
    let _ = fs::remove_file(&start_menu);
    let _ = fs::remove_file(&desktop);
    if let Some(dir) = &root.shortcuts_dir {
        let _ = fs::remove_dir_all(dir);
    }

    remove_registry_key(&root.registry_subkey)?;

    if keep_data {
        if app_dir.exists() {
            fs::remove_dir_all(&app_dir)?;
        }
        if root.base_dir.exists() {
            for entry in fs::read_dir(&root.base_dir)?.flatten() {
                let name = entry.file_name().to_string_lossy().into_owned();
                if name.starts_with("app.previous-")
                    || name.starts_with("app.incoming-")
                    || name.starts_with("app.failed-")
                    || name == "update-health.json"
                {
                    let p = entry.path();
                    if p.is_dir() {
                        let _ = fs::remove_dir_all(p);
                    } else {
                        let _ = fs::remove_file(p);
                    }
                }
            }
        }
    } else if root.base_dir.exists() {
        fs::remove_dir_all(&root.base_dir)?;
    }
    Ok(())
}
