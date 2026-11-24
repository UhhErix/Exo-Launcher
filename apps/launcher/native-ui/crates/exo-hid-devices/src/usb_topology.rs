use serde::{Deserialize, Serialize};

#[derive(Clone, Copy, Debug, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub enum UsbPortClass {
    CpuDirect,
    Chipset,
    ExternalHub,
    Unknown,
}

#[derive(Clone, Debug, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct UsbTopologyReport {
    pub port_class: UsbPortClass,
    pub controller_id: Option<String>,
    pub hub_hops: u8,
    pub warning: Option<String>,
}

pub fn analyze_usb_chain(device_chain: &[String], polling_hz: u16) -> UsbTopologyReport {
    let mut hub_hops = 0u8;
    let mut controller_id = None;
    let mut saw_root_hub = false;

    for node in device_chain {
        let upper = node.to_ascii_uppercase();
        if upper.starts_with("USB\\ROOT_HUB") {
            saw_root_hub = true;
            continue;
        }
        if upper.contains("VID_") && !saw_root_hub && upper.contains("HUB") {
            hub_hops = hub_hops.saturating_add(1);
        }
        if upper.starts_with("PCI\\") {
            controller_id = Some(node.clone());
            break;
        }
    }

    let port_class = if hub_hops > 0 {
        UsbPortClass::ExternalHub
    } else if let Some(ref pci) = controller_id {
        classify_xhci_pci(pci)
    } else {
        UsbPortClass::Unknown
    };

    let warning = if polling_hz >= 4000 {
        match port_class {
            UsbPortClass::ExternalHub => Some(format!(
                "{polling_hz} Hz mouse is on an external USB hub — move it to a direct motherboard port for lower jitter."
            )),
            UsbPortClass::Chipset => Some(format!(
                "{polling_hz} Hz mouse is on a chipset USB controller — a CPU-direct rear port reduces polling latency."
            )),
            _ => None,
        }
    } else {
        None
    };

    UsbTopologyReport {
        port_class,
        controller_id,
        hub_hops,
        warning,
    }
}

pub fn classify_xhci_pci(pci_id: &str) -> UsbPortClass {
    let upper = pci_id.to_ascii_uppercase();
    if upper.contains("VEN_1022") {
        if upper.contains("DEV_149C")
            || upper.contains("DEV_15E0")
            || upper.contains("DEV_15E1")
            || upper.contains("DEV_161D")
            || upper.contains("DEV_161E")
            || upper.contains("DEV_1668")
            || upper.contains("DEV_15B6")
            || upper.contains("DEV_15B7")
        {
            return UsbPortClass::CpuDirect;
        }
        if upper.contains("DEV_43EE")
            || upper.contains("DEV_43F7")
            || upper.contains("DEV_43D5")
            || upper.contains("DEV_43BB")
        {
            return UsbPortClass::Chipset;
        }
    }
    if upper.contains("VEN_8086") {
        if upper.contains("DEV_461E")
            || upper.contains("DEV_A71E")
            || upper.contains("DEV_7EC0")
            || upper.contains("DEV_9A13")
        {
            return UsbPortClass::CpuDirect;
        }
        if upper.contains("DEV_7AE0")
            || upper.contains("DEV_7A60")
            || upper.contains("DEV_43ED")
            || upper.contains("DEV_A36D")
        {
            return UsbPortClass::Chipset;
        }
    }
    if upper.contains("VEN_1B21") || upper.contains("VEN_1912") {
        return UsbPortClass::Chipset;
    }
    UsbPortClass::Unknown
}

#[cfg(windows)]
pub fn walk_device_chain(device_instance_id: &str) -> Vec<String> {
    use windows::Win32::Devices::DeviceAndDriverInstallation::{
        CM_Get_Device_IDW, CM_Get_Parent, CM_LOCATE_DEVNODE_NORMAL, CM_Locate_DevNodeW, CR_SUCCESS,
    };
    use windows::core::PWSTR;

    let mut wide: Vec<u16> = device_instance_id.encode_utf16().chain(Some(0)).collect();
    let mut dev_inst = 0u32;
    let status = unsafe {
        CM_Locate_DevNodeW(
            &mut dev_inst,
            PWSTR(wide.as_mut_ptr()),
            CM_LOCATE_DEVNODE_NORMAL,
        )
    };
    if status != CR_SUCCESS {
        return Vec::new();
    }

    let mut chain = Vec::new();
    let mut current = dev_inst;
    for _ in 0..16 {
        let mut id_buf = [0u16; 512];
        if unsafe { CM_Get_Device_IDW(current, &mut id_buf, 0) } != CR_SUCCESS {
            break;
        }
        let end = id_buf.iter().position(|&c| c == 0).unwrap_or(id_buf.len());
        let id = String::from_utf16_lossy(&id_buf[..end]);
        let is_pci = id.to_ascii_uppercase().starts_with("PCI\\");
        chain.push(id);
        if is_pci {
            break;
        }
        let mut parent = 0u32;
        if unsafe { CM_Get_Parent(&mut parent, current, 0) } != CR_SUCCESS || parent == 0 {
            break;
        }
        current = parent;
    }
    chain
}

#[cfg(not(windows))]
pub fn walk_device_chain(_device_instance_id: &str) -> Vec<String> {
    Vec::new()
}
