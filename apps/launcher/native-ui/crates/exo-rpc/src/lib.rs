pub mod pipe;
pub mod remote_art;
pub mod types;

use serde::Serialize;
use serde::de::DeserializeOwned;
use serde_json::{Value, json};
use std::collections::HashMap;
use std::fmt;
use std::io;
use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
use std::sync::{Arc, Mutex, mpsc};
use std::time::Duration;
pub use types::{
    ActionResult, ArtBase, ExoAccountState, Game, GameExtras, GameGetReply, GameProperties,
    GamePropertySetReply, HostContract, HostEvent, InstallProgress, KeyPreview, LauncherSettings,
    LibraryPayload, StoreArt, StoreGame, StoreHome, StoreLowest, StoreOffer, StoreOtherOffers,
    StoreSearchReply, StoreShelf, StoreTile,
};

pub const PROTOCOL_VERSION: u32 = 1;
const MAX_INBOUND_BYTES: usize = 32 * 1024 * 1024;

pub const METHOD_ART_BASE: &str = "art.base";
pub const METHOD_EXO_ACCOUNT_STATE: &str = "exoAccount.state";
pub const METHOD_LIBRARY_GET: &str = "library.get";
pub const METHOD_LIBRARY_REFRESH: &str = "library.refresh";
pub const METHOD_GAME_GET: &str = "game.get";
pub const METHOD_GAME_EXTRAS: &str = "game.extras";
pub const METHOD_GAME_LAUNCH: &str = "game.launch";
pub const METHOD_GAME_STOP: &str = "game.stop";
pub const METHOD_GAME_INSTALL: &str = "game.install";
pub const METHOD_GAME_UPDATE: &str = "game.update";
pub const METHOD_GAME_UNINSTALL: &str = "game.uninstall";
pub const METHOD_GAME_REPAIR: &str = "game.repair";
pub const METHOD_GAME_OPEN_FOLDER: &str = "game.openFolder";
pub const METHOD_GAME_CANCEL_INSTALL: &str = "game.cancelInstall";
pub const METHOD_GAME_PAUSE_INSTALL: &str = "game.pauseInstall";
pub const METHOD_GAME_PROPERTIES: &str = "game.properties";
pub const METHOD_GAME_PROPERTIES_SET: &str = "game.properties.set";
pub const METHOD_KEYS_PREVIEW: &str = "keys.preview";
pub const METHOD_KEYS_ACTIVATE: &str = "keys.activate";
pub const METHOD_SETTINGS_GET: &str = "settings.get";
pub const METHOD_SETTINGS_SET: &str = "settings.set";
pub const METHOD_STORE_HOME: &str = "store.home";
pub const METHOD_STORE_SEARCH: &str = "store.search";
pub const METHOD_STORE_GAME: &str = "store.game";
pub const METHOD_STORE_GAME_OFFERS: &str = "store.gameOffers";
pub const METHOD_STORE_OPEN: &str = "store.open";
pub const METHOD_STORE_WISHLIST: &str = "store.wishlist";

pub const USED_METHODS: &[&str] = &[
    METHOD_ART_BASE,
    METHOD_EXO_ACCOUNT_STATE,
    METHOD_LIBRARY_GET,
    METHOD_LIBRARY_REFRESH,
    METHOD_GAME_GET,
    METHOD_GAME_EXTRAS,
    METHOD_GAME_LAUNCH,
    METHOD_GAME_STOP,
    METHOD_GAME_INSTALL,
    METHOD_GAME_UPDATE,
    METHOD_GAME_UNINSTALL,
    METHOD_GAME_REPAIR,
    METHOD_GAME_OPEN_FOLDER,
    METHOD_GAME_CANCEL_INSTALL,
    METHOD_GAME_PAUSE_INSTALL,
    METHOD_GAME_PROPERTIES,
    METHOD_GAME_PROPERTIES_SET,
    METHOD_KEYS_PREVIEW,
    METHOD_KEYS_ACTIVATE,
    METHOD_SETTINGS_GET,
    METHOD_SETTINGS_SET,
    METHOD_STORE_HOME,
    METHOD_STORE_SEARCH,
    METHOD_STORE_GAME,
    METHOD_STORE_GAME_OFFERS,
    METHOD_STORE_OPEN,
    METHOD_STORE_WISHLIST,
];

#[derive(Debug)]
pub enum RpcError {
    Io(io::Error),
    Json(serde_json::Error),
    Host(String),
    Disconnected,
    Timeout,
}

impl fmt::Display for RpcError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::Io(e) => write!(f, "pipe io: {e}"),
            Self::Json(e) => write!(f, "json: {e}"),
            Self::Host(msg) => f.write_str(msg),
            Self::Disconnected => f.write_str("host disconnected"),
            Self::Timeout => f.write_str("rpc timed out"),
        }
    }
}

impl std::error::Error for RpcError {}

impl From<io::Error> for RpcError {
    fn from(value: io::Error) -> Self {
        Self::Io(value)
    }
}

impl From<serde_json::Error> for RpcError {
    fn from(value: serde_json::Error) -> Self {
        Self::Json(value)
    }
}

type PendingCallback = Box<dyn FnOnce(Result<Value, RpcError>) + Send + 'static>;

pub fn load_host_contract(json: &str) -> Result<HostContract, serde_json::Error> {
    serde_json::from_str(json)
}

#[cfg(windows)]
pub struct RpcClient {
    pipe: Arc<pipe::PipeHandle>,
    outbound: mpsc::Sender<String>,
    pending: Arc<Mutex<HashMap<String, PendingCallback>>>,
    next_id: AtomicU64,
    closed: Arc<AtomicBool>,
}

#[cfg(windows)]
impl RpcClient {
    pub fn connect(
        pipe_name: &str,
        timeout: Duration,
        on_event: impl Fn(HostEvent) + Send + Sync + 'static,
    ) -> Result<Arc<Self>, RpcError> {
        let pipe = pipe::PipeHandle::connect(pipe_name, timeout)?;
        let (tx, rx) = mpsc::channel::<String>();
        let pending: Arc<Mutex<HashMap<String, PendingCallback>>> =
            Arc::new(Mutex::new(HashMap::new()));
        let closed = Arc::new(AtomicBool::new(false));
        let on_event: Arc<dyn Fn(HostEvent) + Send + Sync> = Arc::new(on_event);

        let write_pipe = Arc::clone(&pipe);
        let write_closed = Arc::clone(&closed);
        std::thread::Builder::new()
            .name("exo-rpc-writer".into())
            .spawn(move || {
                let Ok(event) = pipe::EventHandle::new() else {
                    return;
                };
                while let Ok(mut line) = rx.recv() {
                    if write_closed.load(Ordering::Acquire) {
                        break;
                    }
                    line.push('\n');
                    if write_pipe
                        .write_all_overlapped(event.raw(), line.as_bytes())
                        .is_err()
                    {
                        break;
                    }
                }
            })?;

        let read_pipe = Arc::clone(&pipe);
        let read_pending = Arc::clone(&pending);
        let read_closed = Arc::clone(&closed);
        let read_events = Arc::clone(&on_event);
        std::thread::Builder::new()
            .name("exo-rpc-reader".into())
            .spawn(move || {
                let reason = read_loop(&read_pipe, &read_pending, &*read_events);
                read_closed.store(true, Ordering::Release);
                let mut map = read_pending.lock().unwrap_or_else(|e| e.into_inner());
                for (_, cb) in map.drain() {
                    cb(Err(RpcError::Disconnected));
                }
                drop(map);
                read_events(HostEvent::Disconnected(reason));
            })?;

        Ok(Arc::new(Self {
            pipe,
            outbound: tx,
            pending,
            next_id: AtomicU64::new(1),
            closed,
        }))
    }

    pub fn call_raw<P: Serialize>(
        &self,
        method: &str,
        params: &P,
        callback: impl FnOnce(Result<Value, RpcError>) + Send + 'static,
    ) {
        if self.closed.load(Ordering::Acquire) {
            callback(Err(RpcError::Disconnected));
            return;
        }
        let id = self.next_id.fetch_add(1, Ordering::Relaxed).to_string();
        let envelope = json!({
            "version": PROTOCOL_VERSION,
            "id": id,
            "method": method,
            "params": params,
        });
        let payload = match serde_json::to_string(&envelope) {
            Ok(s) => s,
            Err(e) => {
                callback(Err(RpcError::Json(e)));
                return;
            }
        };
        {
            let mut map = self.pending.lock().unwrap_or_else(|e| e.into_inner());
            map.insert(id.clone(), Box::new(callback));
        }
        if self.outbound.send(payload).is_err() {
            let mut map = self.pending.lock().unwrap_or_else(|e| e.into_inner());
            if let Some(cb) = map.remove(&id) {
                cb(Err(RpcError::Disconnected));
            }
        }
    }

    pub fn call<P: Serialize, R: DeserializeOwned + Send + 'static>(
        &self,
        method: &str,
        params: &P,
        callback: impl FnOnce(Result<R, RpcError>) + Send + 'static,
    ) {
        self.call_raw(method, params, move |res| {
            let typed = res.and_then(|v| serde_json::from_value::<R>(v).map_err(RpcError::Json));
            callback(typed);
        });
    }

    pub fn call_blocking<P: Serialize, R: DeserializeOwned + Send + 'static>(
        &self,
        method: &str,
        params: &P,
        timeout: Duration,
    ) -> Result<R, RpcError> {
        let (tx, rx) = mpsc::channel();
        self.call(method, params, move |res| {
            let _ = tx.send(res);
        });
        rx.recv_timeout(timeout).map_err(|_| RpcError::Timeout)?
    }

    pub fn close(&self) {
        self.closed.store(true, Ordering::Release);
        self.pipe.cancel();
    }
}

#[cfg(windows)]
impl Drop for RpcClient {
    fn drop(&mut self) {
        self.close();
    }
}

#[cfg(windows)]
fn read_loop(
    pipe: &pipe::PipeHandle,
    pending: &Mutex<HashMap<String, PendingCallback>>,
    on_event: &(dyn Fn(HostEvent) + Send + Sync),
) -> String {
    let event = match pipe::EventHandle::new() {
        Ok(e) => e,
        Err(e) => return e.to_string(),
    };
    let mut buf = vec![0u8; 32 * 1024];
    let mut line = Vec::with_capacity(4096);

    loop {
        let n = match pipe.read_overlapped(event.raw(), &mut buf) {
            Ok(0) => return "eof".into(),
            Ok(n) => n,
            Err(e) => return e.to_string(),
        };
        let mut start = 0;
        for i in 0..n {
            if buf[i] != b'\n' {
                continue;
            }
            if line.len() + (i - start) > MAX_INBOUND_BYTES {
                return "inbound message exceeded limit".into();
            }
            line.extend_from_slice(&buf[start..i]);
            if line.last() == Some(&b'\r') {
                line.pop();
            }
            if !line.is_empty() {
                dispatch_line(&line, pending, on_event);
            }
            line.clear();
            start = i + 1;
        }
        if start < n {
            if line.len() + (n - start) > MAX_INBOUND_BYTES {
                return "inbound message exceeded limit".into();
            }
            line.extend_from_slice(&buf[start..n]);
        }
    }
}

#[cfg(windows)]
fn dispatch_line(
    raw: &[u8],
    pending: &Mutex<HashMap<String, PendingCallback>>,
    on_event: &(dyn Fn(HostEvent) + Send + Sync),
) {
    let Ok(val) = serde_json::from_slice::<Value>(raw) else {
        return;
    };
    if let Some(id) = val.get("id").and_then(Value::as_str) {
        let cb = pending
            .lock()
            .unwrap_or_else(|e| e.into_inner())
            .remove(id);
        if let Some(cb) = cb {
            let ok = val.get("ok").and_then(Value::as_bool).unwrap_or(false);
            if ok {
                cb(Ok(val.get("result").cloned().unwrap_or(Value::Null)));
            } else {
                let err = val
                    .get("error")
                    .and_then(Value::as_str)
                    .unwrap_or("Host request failed")
                    .to_string();
                cb(Err(RpcError::Host(err)));
            }
        }
        return;
    }
    if let Some(name) = val.get("event").and_then(Value::as_str) {
        let data = val.get("data").cloned().unwrap_or(Value::Null);
        let parsed = match name {
            "library.updated" => serde_json::from_value::<LibraryPayload>(data.clone())
                .map(HostEvent::LibraryUpdated)
                .unwrap_or(HostEvent::Raw {
                    event: name.to_string(),
                    data,
                }),
            "install.progress" => serde_json::from_value::<InstallProgress>(data.clone())
                .map(HostEvent::InstallProgress)
                .unwrap_or(HostEvent::Raw {
                    event: name.to_string(),
                    data,
                }),
            _ => HostEvent::Raw {
                event: name.to_string(),
                data,
            },
        };
        on_event(parsed);
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    const CONTRACT_JSON: &str = include_str!("../../../../ui/contracts/host-v1.json");

    #[test]
    fn host_contract_contains_every_rust_rpc_method() {
        let contract = load_host_contract(CONTRACT_JSON).expect("valid host-v1.json");
        assert_eq!(contract.version, PROTOCOL_VERSION);
        for method in USED_METHODS {
            assert!(
                contract.methods.contains_key(*method),
                "Rust calls undeclared host method: {method}"
            );
        }
    }

    #[test]
    fn art_base_rejects_traversal_and_unsafe_leaves() {
        let base = ArtBase {
            covers: "https://covers.exo-launcher.local/tok/".into(),
            covers_dir: Some(std::env::temp_dir().to_string_lossy().into_owned()),
            devices_dir: None,
            notes_dir: None,
        };
        assert!(
            base.resolve_local_cover("https://covers.exo-launcher.local/tok/..%2Fsecret.png")
                .is_none()
        );
        assert!(
            base.resolve_local_cover("https://evil.example/tok/a.png")
                .is_none()
        );
    }
}
