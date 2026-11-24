use exo_wire::{
    FrameHeader, FrameKind, HEADER_LEN, LibraryPayload, METHOD_LIBRARY_GET, METHOD_MEDIA_FRAME,
    WIRE_VERSION, decode_library_owned, visit_library_zerocopy,
};
use std::env;
use std::fs::OpenOptions;
use std::io::{BufRead, BufReader, Read, Write};
use std::time::Instant;

fn run_json_pipe(pipe_path: &str, iters: usize, use_sonic: bool) -> (f64, usize) {
    let f = OpenOptions::new().read(true).write(true).open(pipe_path).expect("open json pipe");
    let mut w = f.try_clone().unwrap();
    let mut r = BufReader::with_capacity(128 * 1024, f);
    let mut line = Vec::with_capacity(128 * 1024);
    let mut last_len = 0usize;

    for i in 0..10 {
        let req = format!("{{\"id\":{i},\"method\":\"library.get\"}}\n");
        w.write_all(req.as_bytes()).unwrap();
        w.flush().unwrap();
        line.clear();
        r.read_until(b'\n', &mut line).unwrap();
    }

    let t = Instant::now();
    for i in 0..iters {
        let req = format!("{{\"id\":{i},\"method\":\"library.get\"}}\n");
        w.write_all(req.as_bytes()).unwrap();
        w.flush().unwrap();
        line.clear();
        r.read_until(b'\n', &mut line).unwrap();
        last_len = line.len();
        if use_sonic {
            let v: LibraryPayload = sonic_rs::from_slice(&line).unwrap();
            std::hint::black_box(v.games.len());
        } else {
            let v: LibraryPayload = serde_json::from_slice(&line).unwrap();
            std::hint::black_box(v.games.len());
        }
    }
    let us = t.elapsed().as_secs_f64() * 1e6 / iters as f64;
    (us, last_len)
}

fn run_wire_pipe(pipe_path: &str, iters: usize, zerocopy: bool, media: bool) -> (f64, usize) {
    let mut f = OpenOptions::new().read(true).write(true).open(pipe_path).expect("open wire pipe");
    let mut hdr_buf = [0u8; HEADER_LEN];
    let mut payload = Vec::with_capacity(256 * 1024);
    let method = if media { METHOD_MEDIA_FRAME } else { METHOD_LIBRARY_GET };
    let mut last_len = 0usize;

    for i in 0..10u64 {
        let req = FrameHeader {
            version: WIRE_VERSION,
            kind: FrameKind::Request,
            method_id: method,
            req_id: i,
            payload_len: 0,
        };
        f.write_all(&req.encode()).unwrap();
        f.flush().unwrap();
        f.read_exact(&mut hdr_buf).unwrap();
        let h = FrameHeader::decode(&hdr_buf).unwrap();
        payload.resize(h.payload_len as usize, 0);
        f.read_exact(&mut payload).unwrap();
    }

    let t = Instant::now();
    for i in 0..iters {
        let req = FrameHeader {
            version: WIRE_VERSION,
            kind: FrameKind::Request,
            method_id: method,
            req_id: i as u64,
            payload_len: 0,
        };
        f.write_all(&req.encode()).unwrap();
        f.flush().unwrap();
        f.read_exact(&mut hdr_buf).unwrap();
        let h = FrameHeader::decode(&hdr_buf).unwrap();
        payload.resize(h.payload_len as usize, 0);
        f.read_exact(&mut payload).unwrap();
        last_len = HEADER_LEN + payload.len();
        if media {
            std::hint::black_box(payload[0] ^ payload[payload.len() - 1]);
        } else if zerocopy {
            let mut sum = 0u64;
            let _ = visit_library_zerocopy(&payload, |g| {
                sum = sum.wrapping_add(g.title.len() as u64 + u64::from(g.installed));
            })
            .unwrap();
            std::hint::black_box(sum);
        } else {
            let v = decode_library_owned(&payload).unwrap();
            std::hint::black_box(v.games.len());
        }
    }
    let us = t.elapsed().as_secs_f64() * 1e6 / iters as f64;
    (us, last_len)
}

fn main() {
    let args: Vec<String> = env::args().collect();
    if args.len() < 4 {
        eprintln!("usage: exo-wire-bench <mode> <pipe_name> <iters>");
        std::process::exit(2);
    }
    let mode = args[1].as_str();
    let pipe = format!(r"\\.\pipe\{}", args[2]);
    let iters: usize = args[3].parse().unwrap();

    let (us, bytes) = match mode {
        "json-serde" => run_json_pipe(&pipe, iters, false),
        "json-sonic" => run_json_pipe(&pipe, iters, true),
        "wire-owned" => run_wire_pipe(&pipe, iters, false, false),
        "wire-zerocopy" => run_wire_pipe(&pipe, iters, true, false),
        "wire-media" => run_wire_pipe(&pipe, iters, true, true),
        other => {
            eprintln!("unknown mode: {other}");
            std::process::exit(2);
        }
    };
    println!("CLIENT mode={mode} iters={iters} frame_bytes={bytes} avg_rtt_us={us:.1} ops_sec={:.0}", 1e6 / us);
}
