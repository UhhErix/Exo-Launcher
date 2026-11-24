use std::collections::HashMap;
use std::io::{BufRead, BufReader, Write};
use std::net::TcpListener;
use std::sync::Arc;
use std::sync::atomic::{AtomicBool, Ordering};
use std::thread;
use std::time::{Duration, Instant};

use exo_stores::downloader::{
    ChunkCompression, ChunkHash, ChunkSpec, FileChunkSlice, FileLayoutSpec, InstallOptions,
    StoreManifest, install_or_update, uninstall_manifest,
};
use exo_stores::hash::{sha256, to_hex};
use exo_stores::steam::ipc::discover_steam_root_readonly;
use exo_stores::steam::vdf::{
    build_synthetic_appinfo_shard, build_synthetic_appinfo_v41, parse_binary_appinfo,
    parse_library_folders,
};
use exo_stores::{current_process_cpu_ms, current_process_memory_mb};

fn run_bench() {
    let (mem_start_ws, mem_start_priv) = current_process_memory_mb();
    let cpu_start_ms = current_process_cpu_ms();

    let cold_t0 = Instant::now();
    let steam_root = discover_steam_root_readonly();
    let cold_us = cold_t0.elapsed().as_secs_f64() * 1_000_000.0;

    let v41_bytes = build_synthetic_appinfo_v41(155_000);
    let v41_mb = v41_bytes.len() as f64 / (1024.0 * 1024.0);
    let tv0 = Instant::now();
    let v41_recs = parse_binary_appinfo(&v41_bytes).expect("v41 parse");
    let v41_ms = tv0.elapsed().as_secs_f64() * 1000.0;

    let shard_bytes = build_synthetic_appinfo_shard(50_000);
    let shard_mb = shard_bytes.len() as f64 / (1024.0 * 1024.0);
    let ts0 = Instant::now();
    let shard_recs = parse_binary_appinfo(&shard_bytes).expect("shard parse");
    let shard_ms = ts0.elapsed().as_secs_f64() * 1000.0;

    let chunk_size = 4 * 1024 * 1024usize;
    let chunk_count = 256usize;
    let payload: Vec<u8> = (0..chunk_size)
        .map(|i| ((i.wrapping_mul(131) ^ (i >> 7)) & 0xFF) as u8)
        .collect();
    let chunk_hash = to_hex(&sha256(&payload));
    let mut routes = HashMap::new();
    let payload_arc = Arc::new(payload);
    routes.insert("/bench/chunk.bin".to_string(), Arc::clone(&payload_arc));

    let listener = TcpListener::bind("127.0.0.1:0").expect("bind loopback");
    let port = listener.local_addr().unwrap().port();
    let _ = listener.set_nonblocking(true);
    let stop = Arc::new(AtomicBool::new(false));
    let stop_c = Arc::clone(&stop);
    let routes_arc = Arc::new(routes);

    let srv = thread::spawn(move || {
        while !stop_c.load(Ordering::Relaxed) {
            match listener.accept() {
                Ok((stream, _)) => {
                    let routes = Arc::clone(&routes_arc);
                    let stop_conn = Arc::clone(&stop_c);
                    thread::spawn(move || {
                        let _ = stream.set_nonblocking(false);
                        let _ = stream.set_nodelay(true);
                        let _ = stream.set_read_timeout(Some(Duration::from_millis(500)));
                        let mut reader = BufReader::with_capacity(262_144, stream);
                        while !stop_conn.load(Ordering::Relaxed) {
                            let mut line = String::new();
                            let Ok(n) = reader.read_line(&mut line) else {
                                break;
                            };
                            if n == 0 {
                                break;
                            }
                            let path = line.split_whitespace().nth(1).unwrap_or("/").to_string();
                            let mut range: Option<(usize, usize)> = None;
                            loop {
                                let mut h = String::new();
                                let Ok(hn) = reader.read_line(&mut h) else {
                                    return;
                                };
                                if hn == 0 || h == "\r\n" || h == "\n" {
                                    break;
                                }
                                if let Some(v) = h.strip_prefix("Range: bytes=")
                                    && let Some((a, b)) = v.trim().split_once('-')
                                    && let (Ok(s), Ok(e)) = (a.parse::<usize>(), b.parse::<usize>())
                                {
                                    range = Some((s, e));
                                }
                            }
                            if let Some(data) = routes.get(&path) {
                                let slice: &[u8] = if let Some((s, e)) = range {
                                    let end = e.min(data.len().saturating_sub(1));
                                    if s <= end { &data[s..=end] } else { &data[..] }
                                } else {
                                    &data[..]
                                };
                                let hdr = format!(
                                    "HTTP/1.1 200 OK\r\nContent-Length: {}\r\nConnection: keep-alive\r\n\r\n",
                                    slice.len()
                                );
                                let sock = reader.get_mut();
                                if sock.write_all(hdr.as_bytes()).is_err()
                                    || sock.write_all(slice).is_err()
                                {
                                    break;
                                }
                            } else {
                                let _ = reader.get_mut().write_all(
                                    b"HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\n\r\n",
                                );
                            }
                        }
                    });
                }
                Err(e) if e.kind() == std::io::ErrorKind::WouldBlock => {
                    thread::sleep(Duration::from_millis(1));
                }
                Err(_) => break,
            }
        }
    });

    let mut chunks = Vec::with_capacity(chunk_count);
    let mut slices = Vec::with_capacity(chunk_count);
    for i in 0..chunk_count {
        let id = format!("chunk-{i:03}");
        chunks.push(ChunkSpec {
            id: id.clone(),
            url_path: "bench/chunk.bin".into(),
            hash: ChunkHash::Sha256(chunk_hash.clone()),
            compressed_size: chunk_size as u64,
            uncompressed_size: chunk_size as u64,
            compression: ChunkCompression::Raw,
        });
        slices.push(FileChunkSlice {
            chunk_id: id,
            chunk_offset: 0,
            file_offset: (i * chunk_size) as u64,
            length: chunk_size as u64,
        });
    }

    let manifest = StoreManifest {
        store: "bench".into(),
        app_id: "BenchApp".into(),
        build_version: "1.0".into(),
        cdn_bases: vec![format!("http://127.0.0.1:{port}")],
        chunks,
        files: vec![FileLayoutSpec {
            rel_path: "bench_payload.bin".into(),
            total_size: (chunk_size * chunk_count) as u64,
            sha256: None,
            tags: Vec::new(),
            slices,
        }],
    };

    let bench_dir = std::env::temp_dir().join(format!("exo-stores-bench-{}", std::process::id()));
    let opts = InstallOptions {
        workers: 8,
        allow_loopback: true,
        ..Default::default()
    };
    let cpu_dl_start = current_process_cpu_ms();
    let dl_report = install_or_update(&manifest, &bench_dir, &opts, None).expect("bench install");
    let cpu_dl_ms = (current_process_cpu_ms() - cpu_dl_start).max(0.0);
    let _ = uninstall_manifest(&manifest, &bench_dir);
    let _ = std::fs::remove_dir_all(&bench_dir);

    stop.store(true, Ordering::SeqCst);
    let _ = srv.join();

    let (mem_end_ws, mem_end_priv) = current_process_memory_mb();
    let cpu_total_ms = (current_process_cpu_ms() - cpu_start_ms).max(0.0);

    let steam_libs = steam_root
        .as_ref()
        .and_then(|r| std::fs::read_to_string(r.join("steamapps/libraryfolders.vdf")).ok())
        .and_then(|txt| parse_library_folders(&txt).ok().map(|f| f.len()))
        .unwrap_or(0);

    println!("=== EXO-STORES BENCHMARK ===");
    println!("cold_inproc_probe_us={cold_us:.1}");
    println!(
        "steam_readonly detected={} library_folders={steam_libs}",
        steam_root.is_some()
    );
    println!(
        "vdf_v41 size_mb={v41_mb:.2} apps={} parse_ms={v41_ms:.2}",
        v41_recs.len()
    );
    println!(
        "vdf_shard size_mb={shard_mb:.2} apps={} parse_ms={shard_ms:.2}",
        shard_recs.len()
    );
    println!(
        "loopback_install bytes_mb={:.1} workers={} elapsed_ms={:.2} throughput_mb_s={:.1} proc_cpu_ms={cpu_dl_ms:.2}",
        dl_report.bytes_written as f64 / (1024.0 * 1024.0),
        opts.workers,
        dl_report.elapsed_ms,
        dl_report.throughput_mb_s
    );
    println!(
        "memory_mb start_ws={mem_start_ws:.2} start_priv={mem_start_priv:.2} end_ws={mem_end_ws:.2} end_priv={mem_end_priv:.2} total_cpu_ms={cpu_total_ms:.2}"
    );
}

fn main() {
    let arg = std::env::args().nth(1).unwrap_or_else(|| "bench".into());
    match arg.as_str() {
        "bench" => run_bench(),
        _ => {
            println!("exo-stores: usage: exo-stores [bench]");
        }
    }
}
