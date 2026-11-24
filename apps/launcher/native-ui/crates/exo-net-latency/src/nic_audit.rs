use serde::{Deserialize, Serialize};
use std::collections::HashMap;

pub trait RegistryStore {
    fn get_string(&self, key: &str, name: &str) -> Option<String>;
    fn set_string(&mut self, key: &str, name: &str, val: &str) -> Result<(), String>;
    fn delete_value(&mut self, key: &str, name: &str) -> Result<(), String>;
}

#[derive(Debug, Clone, Default)]
pub struct MockRegistryStore {
    pub values: HashMap<(String, String), String>,
}

impl RegistryStore for MockRegistryStore {
    fn get_string(&self, key: &str, name: &str) -> Option<String> {
        self.values
            .get(&(key.to_string(), name.to_string()))
            .cloned()
    }

    fn set_string(&mut self, key: &str, name: &str, val: &str) -> Result<(), String> {
        self.values
            .insert((key.to_string(), name.to_string()), val.to_string());
        Ok(())
    }

    fn delete_value(&mut self, key: &str, name: &str) -> Result<(), String> {
        self.values.remove(&(key.to_string(), name.to_string()));
        Ok(())
    }
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct KeepLedgerEntry {
    pub key: String,
    pub name: String,
    pub previous: Option<String>,
    pub applied: String,
}

pub fn apply_reversible_nic_tuning<F>(
    store: &mut dyn RegistryStore,
    adapter_key: &str,
    changes: &[(&str, &str)],
    verify_gateway_ok: F,
) -> Result<Vec<KeepLedgerEntry>, String>
where
    F: FnOnce() -> bool,
{
    let mut ledger = Vec::with_capacity(changes.len());
    for &(name, val) in changes {
        let prev = store.get_string(adapter_key, name);
        store.set_string(adapter_key, name, val)?;
        ledger.push(KeepLedgerEntry {
            key: adapter_key.to_string(),
            name: name.to_string(),
            previous: prev,
            applied: val.to_string(),
        });
    }
    if !verify_gateway_ok() {
        rollback_nic_tuning(store, &ledger)?;
        return Err("gateway connectivity lost after NIC tune; rolled back cleanly".into());
    }
    Ok(ledger)
}

pub fn rollback_nic_tuning(
    store: &mut dyn RegistryStore,
    ledger: &[KeepLedgerEntry],
) -> Result<(), String> {
    for entry in ledger.iter().rev() {
        match &entry.previous {
            Some(prev) => store.set_string(&entry.key, &entry.name, prev)?,
            None => store.delete_value(&entry.key, &entry.name)?,
        }
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn byte_for_byte_rollback_restores_registry_on_gateway_failure() {
        let mut reg = MockRegistryStore::default();
        let key = r"SYSTEM\CurrentControlSet\Control\Class\{4d36e972}\0001";
        reg.set_string(key, "*EEE", "1").unwrap();
        reg.set_string(key, "*InterruptModeration", "1").unwrap();
        let snapshot = reg.values.clone();

        let err = apply_reversible_nic_tuning(
            &mut reg,
            key,
            &[
                ("*EEE", "0"),
                ("*InterruptModeration", "0"),
                ("*FlowControl", "0"),
            ],
            || false,
        );
        assert!(err.is_err());
        assert_eq!(reg.values, snapshot);
    }
}
