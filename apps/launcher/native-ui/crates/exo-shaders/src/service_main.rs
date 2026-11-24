use exo_shaders::protocol::ServiceState;
use serde_json::{Value, json};
use std::env;
use std::io::{self, BufRead, Write};
use std::path::{Path, PathBuf};
use std::process::ExitCode;
use std::sync::mpsc;
use std::time::Duration;
use windows::Win32::Foundation::{ERROR_ALREADY_EXISTS, GetLastError, HANDLE};
use windows::Win32::System::Threading::CreateMutexW;
use windows::core::PCWSTR;

struct CliArgs {
    data_dir: PathBuf,
    safe: bool,
    log_stderr: bool,
    oneshot_exit: bool,
}

fn parse_args() -> Result<CliArgs, String> {
    let mut data_dir = env::var("SCSKILLER_DATA_DIR")
        .or_else(|_| env::var("LOCALAPPDATA").map(|p| format!("{p}\\ExoShaders")))
        .map(PathBuf::from)
        .unwrap_or_else(|_| env::temp_dir().join("ExoShaders"));
    let mut safe = false;
    let mut log_stderr = false;
    let mut oneshot_exit = false;
    let mut args = env::args().skip(1);
    while let Some(arg) = args.next() {
        match arg.as_str() {
            "--data-dir" => {
                let next = args
                    .next()
                    .ok_or_else(|| "missing --data-dir value".to_string())?;
                data_dir = PathBuf::from(next);
            }
            "--safe" => safe = true,
            "--log-stderr" => log_stderr = true,
            "--offline-cleanup" => {
                let _ = args.next();
                oneshot_exit = true;
            }
            "--asd-remove-all" => {
                oneshot_exit = true;
            }
            other => return Err(format!("unknown argument '{other}'")),
        }
    }
    Ok(CliArgs {
        data_dir,
        safe,
        log_stderr,
        oneshot_exit,
    })
}

fn acquire_single_instance_mutex(data_dir: &Path) -> Result<HANDLE, String> {
    let canonical = data_dir.to_string_lossy().to_ascii_lowercase();
    let hash = blake3::hash(canonical.as_bytes()).to_hex();
    let name = format!("Local\\ExoShaders.Service.{}", &hash[..16]);
    let mut wide: Vec<u16> = name.encode_utf16().collect();
    wide.push(0);
    unsafe {
        let handle = CreateMutexW(None, false, PCWSTR(wide.as_ptr()))
            .map_err(|e| format!("CreateMutexW failed: {e}"))?;
        if GetLastError() == ERROR_ALREADY_EXISTS {
            return Err("another Exo Shaders service holds this data folder".into());
        }
        Ok(handle)
    }
}

fn emit_line(out: &mut impl Write, val: &Value) {
    if let Ok(s) = serde_json::to_string(val) {
        let _ = writeln!(out, "{s}");
        let _ = out.flush();
    }
}

fn main() -> ExitCode {
    let args = match parse_args() {
        Ok(a) => a,
        Err(e) => {
            eprintln!("{e}");
            return ExitCode::from(2);
        }
    };
    if args.oneshot_exit {
        return ExitCode::SUCCESS;
    }
    let _mutex = match acquire_single_instance_mutex(&args.data_dir) {
        Ok(m) => m,
        Err(e) => {
            eprintln!("{e}");
            return ExitCode::from(3);
        }
    };
    if args.log_stderr {
        eprintln!("[exo] starting native MIT Rust exo-shaders-service");
    }

    let mut state = ServiceState::new(args.data_dir.clone(), args.safe);
    let stdout = io::stdout();
    let mut out = stdout.lock();

    emit_line(
        &mut out,
        &json!({
            "event": "ready",
            "data": {
                "protocol": 1,
                "service": "exo-shaders-native",
                "engine": env!("CARGO_PKG_VERSION"),
                "dataDir": args.data_dir.display().to_string(),
                "pid": std::process::id(),
                "safe": args.safe,
            }
        }),
    );
    emit_line(
        &mut out,
        &json!({
            "event": "state",
            "data": state.snapshot_json(false),
        }),
    );

    let (tx, rx) = mpsc::channel::<Option<String>>();
    std::thread::spawn(move || {
        let stdin = io::stdin();
        for line in stdin.lock().lines() {
            match line {
                Ok(l) => {
                    if tx.send(Some(l)).is_err() {
                        break;
                    }
                }
                Err(_) => break,
            }
        }
        let _ = tx.send(None);
    });

    let exit_reason = loop {
        match rx.recv_timeout(Duration::from_millis(250)) {
            Ok(Some(line)) => {
                let trimmed = line.trim();
                if trimmed.is_empty() {
                    continue;
                }
                let req: Value = match serde_json::from_str(trimmed) {
                    Ok(v) => v,
                    Err(e) => {
                        emit_line(
                            &mut out,
                            &json!({ "id": null, "error": format!("invalid JSON: {e}") }),
                        );
                        continue;
                    }
                };
                let id = req.get("id").cloned().unwrap_or(Value::Null);
                let method = match req.get("method").and_then(Value::as_str) {
                    Some(m) => m,
                    None => {
                        emit_line(&mut out, &json!({ "id": id, "error": "missing method" }));
                        continue;
                    }
                };
                let params = req.get("params");
                match state.handle_method(method, params) {
                    Ok(result) => {
                        emit_line(&mut out, &json!({ "id": id, "result": result }));
                        if method == "shutdown" {
                            break "shutdown";
                        }
                        if state.ui_visible
                            && method != "snapshot"
                            && method != "ui.visible"
                            && method != "exitWhenIdle"
                        {
                            emit_line(
                                &mut out,
                                &json!({
                                    "event": "state",
                                    "data": state.snapshot_json(false),
                                }),
                            );
                        }
                    }
                    Err(err) => {
                        emit_line(&mut out, &json!({ "id": id, "error": err }));
                    }
                }
            }
            Ok(None) => break "stdinClosed",
            Err(mpsc::RecvTimeoutError::Timeout) => {
                if state.idle_expired() {
                    break "idle";
                }
            }
            Err(mpsc::RecvTimeoutError::Disconnected) => break "stdinClosed",
        }
    };

    state.write_at_rest_snapshot();
    emit_line(
        &mut out,
        &json!({
            "event": "exiting",
            "data": { "reason": exit_reason }
        }),
    );
    ExitCode::SUCCESS
}
