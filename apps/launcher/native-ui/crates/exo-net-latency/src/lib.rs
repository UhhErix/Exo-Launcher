pub mod bufferbloat;
pub mod click_to_photon;
pub mod dns_racer;
pub mod dpc_isr;
pub mod dscp_qos;
pub mod nic_audit;

#[cfg(test)]
mod tests {
    #[test]
    fn source_contains_zero_packet_hook_or_game_injection_references() {
        let banned = [
            "WinDivert",
            "WFP_",
            "Fwps",
            "Npcap",
            "WSCEnumProtocols",
            "SetWindowsHookEx",
        ];
        let files = [
            include_str!("lib.rs"),
            include_str!("dpc_isr.rs"),
            include_str!("click_to_photon.rs"),
            include_str!("dns_racer.rs"),
            include_str!("bufferbloat.rs"),
            include_str!("nic_audit.rs"),
            include_str!("dscp_qos.rs"),
        ];
        for src in files {
            for needle in banned {
                assert!(
                    !src.contains(needle) || src.contains("let banned ="),
                    "forbidden packet-hook or injection symbol {needle} found"
                );
            }
        }
    }
}
