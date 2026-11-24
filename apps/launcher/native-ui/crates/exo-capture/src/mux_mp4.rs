use std::io::{self, Write};

use crate::replay_ring::{EncodedPacket, TrackKind};

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum VideoCodec {
    H264,
    Hevc,
    Av1,
}

impl VideoCodec {
    pub fn display_name(self) -> &'static str {
        match self {
            Self::H264 => "H.264",
            Self::Hevc => "HEVC",
            Self::Av1 => "AV1",
        }
    }

    pub fn fourcc(self) -> [u8; 4] {
        match self {
            Self::H264 => *b"avc1",
            Self::Hevc => *b"hvc1",
            Self::Av1 => *b"av01",
        }
    }

    pub fn config_fourcc(self) -> [u8; 4] {
        match self {
            Self::H264 => *b"avcC",
            Self::Hevc => *b"hvcC",
            Self::Av1 => *b"av1C",
        }
    }
}

#[derive(Debug, Clone)]
pub struct VideoTrackConfig {
    pub codec: VideoCodec,
    pub width: u16,
    pub height: u16,
    pub timescale: u32,
    pub fps: u32,
    pub hdr10: bool,
    pub codec_config: Vec<u8>,
}

#[derive(Debug, Clone)]
pub struct AudioTrackConfig {
    pub kind: TrackKind,
    pub name: String,
    pub sample_rate: u32,
    pub channels: u16,
    pub audio_specific_config: Vec<u8>,
}

impl AudioTrackConfig {
    pub fn aac_lc_48khz_stereo(kind: TrackKind, name: &str) -> Self {
        Self {
            kind,
            name: name.to_string(),
            sample_rate: 48_000,
            channels: 2,
            audio_specific_config: vec![0x11, 0x90],
        }
    }
}

#[derive(Debug, Clone)]
pub struct FragmentSample {
    pub track_id: u32,
    pub dts: u64,
    pub duration: u32,
    pub is_keyframe: bool,
    pub data: Vec<u8>,
}

pub struct Mp4Muxer {
    video: VideoTrackConfig,
    audio: Vec<AudioTrackConfig>,
    fragments: Vec<Vec<u8>>,
    pending: Vec<FragmentSample>,
    sequence_number: u32,
    base_time_100ns: Option<i64>,
    last_dts: Vec<Option<u64>>,
}

impl Mp4Muxer {
    pub fn new(video: VideoTrackConfig, audio: Vec<AudioTrackConfig>) -> Self {
        let track_count = 1 + audio.len();
        Self {
            video,
            audio,
            fragments: Vec::new(),
            pending: Vec::new(),
            sequence_number: 1,
            base_time_100ns: None,
            last_dts: vec![None; track_count],
        }
    }

    pub fn push_packet(&mut self, pkt: &EncodedPacket) {
        let track_idx = match pkt.track {
            TrackKind::Video => 0,
            kind => match self.audio.iter().position(|a| a.kind == kind) {
                Some(i) => i + 1,
                None => return,
            },
        };
        let base = *self.base_time_100ns.get_or_insert(pkt.timestamp_100ns);
        let rel_100ns = pkt.timestamp_100ns.saturating_sub(base).max(0) as u64;
        let timescale = if track_idx == 0 {
            self.video.timescale as u64
        } else {
            self.audio[track_idx - 1].sample_rate as u64
        };
        let mut dts = rel_100ns.saturating_mul(timescale) / 10_000_000;
        let default_dur = if track_idx == 0 {
            (self.video.timescale / self.video.fps.max(1)).max(1)
        } else {
            1024
        };
        let duration = if pkt.duration_100ns > 0 {
            ((pkt.duration_100ns as u64).saturating_mul(timescale) / 10_000_000).max(1) as u32
        } else {
            default_dur
        };
        if let Some(prev) = self.last_dts[track_idx]
            && dts <= prev
        {
            dts = prev + 1;
        }
        self.last_dts[track_idx] = Some(dts);

        if track_idx == 0 && pkt.is_keyframe && !self.pending.is_empty() {
            self.flush_fragment();
        }
        self.pending.push(FragmentSample {
            track_id: (track_idx + 1) as u32,
            dts,
            duration,
            is_keyframe: pkt.is_keyframe,
            data: pkt.data.clone(),
        });
    }

    pub fn flush_fragment(&mut self) {
        if self.pending.is_empty() {
            return;
        }
        let samples = std::mem::take(&mut self.pending);
        let seq = self.sequence_number;
        self.sequence_number += 1;
        let frag = build_fragment_bytes(seq, &samples);
        self.fragments.push(frag);
    }

    pub fn finish_to_vec(&mut self) -> Vec<u8> {
        self.flush_fragment();
        let mut out = Vec::new();
        write_ftyp(&mut out);
        write_moov(&mut out, &self.video, &self.audio);
        for frag in &self.fragments {
            out.extend_from_slice(frag);
        }
        out
    }

    pub fn finish_to_writer<W: Write>(&mut self, mut writer: W) -> io::Result<u64> {
        let bytes = self.finish_to_vec();
        writer.write_all(&bytes)?;
        writer.flush()?;
        Ok(bytes.len() as u64)
    }
}

pub fn mux_packets_to_fmp4(
    video: VideoTrackConfig,
    audio: Vec<AudioTrackConfig>,
    packets: &[EncodedPacket],
) -> Vec<u8> {
    let mut muxer = Mp4Muxer::new(video, audio);
    for pkt in packets {
        muxer.push_packet(pkt);
    }
    muxer.finish_to_vec()
}

pub fn build_avcc_from_sps_pps(sps: &[u8], pps: &[u8]) -> Vec<u8> {
    let profile = sps.get(1).copied().unwrap_or(0x64);
    let compat = sps.get(2).copied().unwrap_or(0x00);
    let level = sps.get(3).copied().unwrap_or(0x2A);
    let mut out = Vec::with_capacity(11 + sps.len() + pps.len());
    out.push(1);
    out.push(profile);
    out.push(compat);
    out.push(level);
    out.push(0xFF);
    out.push(0xE1);
    out.extend_from_slice(&(sps.len() as u16).to_be_bytes());
    out.extend_from_slice(sps);
    out.push(1);
    out.extend_from_slice(&(pps.len() as u16).to_be_bytes());
    out.extend_from_slice(pps);
    out
}

pub fn annex_b_to_length_prefixed(annex_b: &[u8]) -> Vec<u8> {
    let mut out = Vec::with_capacity(annex_b.len() + 16);
    let mut i = 0;
    let len = annex_b.len();
    let mut nals: Vec<(usize, usize)> = Vec::new();
    let mut cur_start: Option<usize> = None;

    while i < len {
        let sc_len = if i + 4 <= len
            && annex_b[i] == 0
            && annex_b[i + 1] == 0
            && annex_b[i + 2] == 0
            && annex_b[i + 3] == 1
        {
            4
        } else if i + 3 <= len && annex_b[i] == 0 && annex_b[i + 1] == 0 && annex_b[i + 2] == 1 {
            3
        } else {
            0
        };
        if sc_len > 0 {
            if let Some(s) = cur_start
                && i > s
            {
                nals.push((s, i));
            }
            i += sc_len;
            cur_start = Some(i);
        } else {
            i += 1;
        }
    }
    if let Some(s) = cur_start
        && len > s
    {
        nals.push((s, len));
    }
    if nals.is_empty() && !annex_b.is_empty() {
        out.extend_from_slice(&(annex_b.len() as u32).to_be_bytes());
        out.extend_from_slice(annex_b);
        return out;
    }
    for (s, e) in nals {
        let nal = &annex_b[s..e];
        out.extend_from_slice(&(nal.len() as u32).to_be_bytes());
        out.extend_from_slice(nal);
    }
    out
}

pub fn extract_h264_sps_pps(annex_b: &[u8]) -> Option<(Vec<u8>, Vec<u8>)> {
    let lp = annex_b_to_length_prefixed(annex_b);
    let mut pos = 0;
    let mut sps = None;
    let mut pps = None;
    while pos + 4 <= lp.len() {
        let nal_len = u32::from_be_bytes(lp[pos..pos + 4].try_into().ok()?) as usize;
        pos += 4;
        if pos + nal_len > lp.len() || nal_len == 0 {
            break;
        }
        let nal_type = lp[pos] & 0x1F;
        if nal_type == 7 && sps.is_none() {
            sps = Some(lp[pos..pos + nal_len].to_vec());
        } else if nal_type == 8 && pps.is_none() {
            pps = Some(lp[pos..pos + nal_len].to_vec());
        }
        pos += nal_len;
    }
    match (sps, pps) {
        (Some(s), Some(p)) => Some((s, p)),
        _ => None,
    }
}

fn put_box(out: &mut Vec<u8>, fourcc: [u8; 4], payload: impl FnOnce(&mut Vec<u8>)) {
    let start = out.len();
    out.extend_from_slice(&0u32.to_be_bytes());
    out.extend_from_slice(&fourcc);
    payload(out);
    let size = (out.len() - start) as u32;
    out[start..start + 4].copy_from_slice(&size.to_be_bytes());
}

fn put_full_box(
    out: &mut Vec<u8>,
    fourcc: [u8; 4],
    version: u8,
    flags: u32,
    payload: impl FnOnce(&mut Vec<u8>),
) {
    put_box(out, fourcc, |b| {
        b.push(version);
        b.push(((flags >> 16) & 0xFF) as u8);
        b.push(((flags >> 8) & 0xFF) as u8);
        b.push((flags & 0xFF) as u8);
        payload(b);
    });
}

fn write_ftyp(out: &mut Vec<u8>) {
    put_box(out, *b"ftyp", |b| {
        b.extend_from_slice(b"iso6");
        b.extend_from_slice(&0x0000_0200u32.to_be_bytes());
        b.extend_from_slice(b"isom");
        b.extend_from_slice(b"iso6");
        b.extend_from_slice(b"mp41");
    });
}

fn write_moov(out: &mut Vec<u8>, video: &VideoTrackConfig, audio: &[AudioTrackConfig]) {
    put_box(out, *b"moov", |b| {
        put_full_box(b, *b"mvhd", 0, 0, |m| {
            m.extend_from_slice(&0u32.to_be_bytes());
            m.extend_from_slice(&0u32.to_be_bytes());
            m.extend_from_slice(&1000u32.to_be_bytes());
            m.extend_from_slice(&0u32.to_be_bytes());
            m.extend_from_slice(&0x0001_0000u32.to_be_bytes());
            m.extend_from_slice(&0x0100u16.to_be_bytes());
            m.extend_from_slice(&[0u8; 10]);
            let matrix: [u32; 9] = [0x0001_0000, 0, 0, 0, 0x0001_0000, 0, 0, 0, 0x4000_0000];
            for v in matrix {
                m.extend_from_slice(&v.to_be_bytes());
            }
            m.extend_from_slice(&[0u8; 24]);
            m.extend_from_slice(&((2 + audio.len()) as u32).to_be_bytes());
        });

        write_video_trak(b, 1, video);
        for (idx, aud) in audio.iter().enumerate() {
            write_audio_trak(b, (idx + 2) as u32, aud);
        }

        put_box(b, *b"mvex", |mv| {
            for track_id in 1..=(1 + audio.len() as u32) {
                put_full_box(mv, *b"trex", 0, 0, |t| {
                    t.extend_from_slice(&track_id.to_be_bytes());
                    t.extend_from_slice(&1u32.to_be_bytes());
                    t.extend_from_slice(&0u32.to_be_bytes());
                    t.extend_from_slice(&0u32.to_be_bytes());
                    t.extend_from_slice(&0u32.to_be_bytes());
                });
            }
        });
    });
}

fn write_video_trak(out: &mut Vec<u8>, track_id: u32, video: &VideoTrackConfig) {
    put_box(out, *b"trak", |b| {
        put_full_box(b, *b"tkhd", 0, 0x000003, |t| {
            t.extend_from_slice(&0u32.to_be_bytes());
            t.extend_from_slice(&0u32.to_be_bytes());
            t.extend_from_slice(&track_id.to_be_bytes());
            t.extend_from_slice(&0u32.to_be_bytes());
            t.extend_from_slice(&0u32.to_be_bytes());
            t.extend_from_slice(&[0u8; 8]);
            t.extend_from_slice(&0u16.to_be_bytes());
            t.extend_from_slice(&0u16.to_be_bytes());
            t.extend_from_slice(&0u16.to_be_bytes());
            t.extend_from_slice(&0u16.to_be_bytes());
            let matrix: [u32; 9] = [0x0001_0000, 0, 0, 0, 0x0001_0000, 0, 0, 0, 0x4000_0000];
            for v in matrix {
                t.extend_from_slice(&v.to_be_bytes());
            }
            t.extend_from_slice(&((video.width as u32) << 16).to_be_bytes());
            t.extend_from_slice(&((video.height as u32) << 16).to_be_bytes());
        });

        put_box(b, *b"mdia", |md| {
            put_full_box(md, *b"mdhd", 0, 0, |m| {
                m.extend_from_slice(&0u32.to_be_bytes());
                m.extend_from_slice(&0u32.to_be_bytes());
                m.extend_from_slice(&video.timescale.to_be_bytes());
                m.extend_from_slice(&0u32.to_be_bytes());
                m.extend_from_slice(&0x55C4u16.to_be_bytes());
                m.extend_from_slice(&0u16.to_be_bytes());
            });
            put_full_box(md, *b"hdlr", 0, 0, |h| {
                h.extend_from_slice(&0u32.to_be_bytes());
                h.extend_from_slice(b"vide");
                h.extend_from_slice(&[0u8; 12]);
                h.extend_from_slice(b"ExoVideo\0");
            });
            put_box(md, *b"minf", |mi| {
                put_full_box(mi, *b"vmhd", 0, 1, |v| {
                    v.extend_from_slice(&[0u8; 8]);
                });
                write_dinf(mi);
                put_box(mi, *b"stbl", |st| {
                    put_full_box(st, *b"stsd", 0, 0, |sd| {
                        sd.extend_from_slice(&1u32.to_be_bytes());
                        put_box(sd, video.codec.fourcc(), |e| {
                            e.extend_from_slice(&[0u8; 6]);
                            e.extend_from_slice(&1u16.to_be_bytes());
                            e.extend_from_slice(&[0u8; 16]);
                            e.extend_from_slice(&video.width.to_be_bytes());
                            e.extend_from_slice(&video.height.to_be_bytes());
                            e.extend_from_slice(&0x0048_0000u32.to_be_bytes());
                            e.extend_from_slice(&0x0048_0000u32.to_be_bytes());
                            e.extend_from_slice(&0u32.to_be_bytes());
                            e.extend_from_slice(&1u16.to_be_bytes());
                            e.extend_from_slice(&[0u8; 32]);
                            e.extend_from_slice(&0x0018u16.to_be_bytes());
                            e.extend_from_slice(&0xFFFFu16.to_be_bytes());
                            if !video.codec_config.is_empty() {
                                put_box(e, video.codec.config_fourcc(), |cfg| {
                                    cfg.extend_from_slice(&video.codec_config);
                                });
                            }
                            put_box(e, *b"colr", |c| {
                                c.extend_from_slice(b"nclx");
                                if video.hdr10 {
                                    c.extend_from_slice(&9u16.to_be_bytes());
                                    c.extend_from_slice(&16u16.to_be_bytes());
                                    c.extend_from_slice(&9u16.to_be_bytes());
                                } else {
                                    c.extend_from_slice(&1u16.to_be_bytes());
                                    c.extend_from_slice(&1u16.to_be_bytes());
                                    c.extend_from_slice(&1u16.to_be_bytes());
                                }
                                c.push(0);
                            });
                        });
                    });
                    write_empty_sample_tables(st);
                });
            });
        });
    });
}

fn write_audio_trak(out: &mut Vec<u8>, track_id: u32, audio: &AudioTrackConfig) {
    put_box(out, *b"trak", |b| {
        put_full_box(b, *b"tkhd", 0, 0x000003, |t| {
            t.extend_from_slice(&0u32.to_be_bytes());
            t.extend_from_slice(&0u32.to_be_bytes());
            t.extend_from_slice(&track_id.to_be_bytes());
            t.extend_from_slice(&0u32.to_be_bytes());
            t.extend_from_slice(&0u32.to_be_bytes());
            t.extend_from_slice(&[0u8; 8]);
            t.extend_from_slice(&0u16.to_be_bytes());
            t.extend_from_slice(&1u16.to_be_bytes());
            t.extend_from_slice(&0x0100u16.to_be_bytes());
            t.extend_from_slice(&0u16.to_be_bytes());
            let matrix: [u32; 9] = [0x0001_0000, 0, 0, 0, 0x0001_0000, 0, 0, 0, 0x4000_0000];
            for v in matrix {
                t.extend_from_slice(&v.to_be_bytes());
            }
            t.extend_from_slice(&0u32.to_be_bytes());
            t.extend_from_slice(&0u32.to_be_bytes());
        });

        put_box(b, *b"mdia", |md| {
            put_full_box(md, *b"mdhd", 0, 0, |m| {
                m.extend_from_slice(&0u32.to_be_bytes());
                m.extend_from_slice(&0u32.to_be_bytes());
                m.extend_from_slice(&audio.sample_rate.to_be_bytes());
                m.extend_from_slice(&0u32.to_be_bytes());
                m.extend_from_slice(&0x55C4u16.to_be_bytes());
                m.extend_from_slice(&0u16.to_be_bytes());
            });
            put_full_box(md, *b"hdlr", 0, 0, |h| {
                h.extend_from_slice(&0u32.to_be_bytes());
                h.extend_from_slice(b"soun");
                h.extend_from_slice(&[0u8; 12]);
                h.extend_from_slice(audio.name.as_bytes());
                h.push(0);
            });
            put_box(md, *b"minf", |mi| {
                put_full_box(mi, *b"smhd", 0, 0, |s| {
                    s.extend_from_slice(&[0u8; 4]);
                });
                write_dinf(mi);
                put_box(mi, *b"stbl", |st| {
                    put_full_box(st, *b"stsd", 0, 0, |sd| {
                        sd.extend_from_slice(&1u32.to_be_bytes());
                        put_box(sd, *b"mp4a", |e| {
                            e.extend_from_slice(&[0u8; 6]);
                            e.extend_from_slice(&1u16.to_be_bytes());
                            e.extend_from_slice(&[0u8; 8]);
                            e.extend_from_slice(&audio.channels.to_be_bytes());
                            e.extend_from_slice(&16u16.to_be_bytes());
                            e.extend_from_slice(&0u32.to_be_bytes());
                            e.extend_from_slice(&(audio.sample_rate << 16).to_be_bytes());
                            put_full_box(e, *b"esds", 0, 0, |es| {
                                let asc = &audio.audio_specific_config;
                                let dsi_len = asc.len() as u8;
                                let dcd_len = 13 + 2 + dsi_len;
                                let esd_len = 3 + 2 + dcd_len + 3;
                                es.extend_from_slice(&[0x03, esd_len, 0x00, track_id as u8, 0x00]);
                                es.extend_from_slice(&[
                                    0x04, dcd_len, 0x40, 0x15, 0x00, 0x00, 0x00,
                                ]);
                                es.extend_from_slice(&192_000u32.to_be_bytes());
                                es.extend_from_slice(&160_000u32.to_be_bytes());
                                es.extend_from_slice(&[0x05, dsi_len]);
                                es.extend_from_slice(asc);
                                es.extend_from_slice(&[0x06, 0x01, 0x02]);
                            });
                        });
                    });
                    write_empty_sample_tables(st);
                });
            });
        });
    });
}

fn write_dinf(out: &mut Vec<u8>) {
    put_box(out, *b"dinf", |d| {
        put_full_box(d, *b"dref", 0, 0, |r| {
            r.extend_from_slice(&1u32.to_be_bytes());
            put_full_box(r, *b"url ", 0, 1, |_| {});
        });
    });
}

fn write_empty_sample_tables(out: &mut Vec<u8>) {
    put_full_box(out, *b"stts", 0, 0, |b| {
        b.extend_from_slice(&0u32.to_be_bytes())
    });
    put_full_box(out, *b"stsc", 0, 0, |b| {
        b.extend_from_slice(&0u32.to_be_bytes())
    });
    put_full_box(out, *b"stsz", 0, 0, |b| {
        b.extend_from_slice(&0u32.to_be_bytes());
        b.extend_from_slice(&0u32.to_be_bytes());
    });
    put_full_box(out, *b"stco", 0, 0, |b| {
        b.extend_from_slice(&0u32.to_be_bytes())
    });
}

fn build_fragment_bytes(sequence_number: u32, samples: &[FragmentSample]) -> Vec<u8> {
    let mut track_ids: Vec<u32> = Vec::new();
    for s in samples {
        if !track_ids.contains(&s.track_id) {
            track_ids.push(s.track_id);
        }
    }
    track_ids.sort_unstable();

    let mut per_track: Vec<(u32, Vec<&FragmentSample>)> = Vec::new();
    let mut mdat_payload = Vec::new();
    let mut track_mdat_offsets = Vec::new();

    for tid in track_ids {
        let group: Vec<&FragmentSample> = samples.iter().filter(|s| s.track_id == tid).collect();
        track_mdat_offsets.push(mdat_payload.len());
        for s in &group {
            mdat_payload.extend_from_slice(&s.data);
        }
        per_track.push((tid, group));
    }

    let mut moof = Vec::new();
    let mut data_offset_positions = Vec::new();
    put_box(&mut moof, *b"moof", |mf| {
        put_full_box(mf, *b"mfhd", 0, 0, |h| {
            h.extend_from_slice(&sequence_number.to_be_bytes());
        });
        for (tid, group) in &per_track {
            put_box(mf, *b"traf", |tr| {
                put_full_box(tr, *b"tfhd", 0, 0x020000, |fh| {
                    fh.extend_from_slice(&tid.to_be_bytes());
                });
                put_full_box(tr, *b"tfdt", 1, 0, |dt| {
                    dt.extend_from_slice(&group[0].dts.to_be_bytes());
                });
                put_full_box(tr, *b"trun", 0, 0x000701, |rn| {
                    rn.extend_from_slice(&(group.len() as u32).to_be_bytes());
                    data_offset_positions.push(rn.len());
                    rn.extend_from_slice(&0i32.to_be_bytes());
                    for s in group {
                        rn.extend_from_slice(&s.duration.to_be_bytes());
                        rn.extend_from_slice(&(s.data.len() as u32).to_be_bytes());
                        let flags: u32 = if s.is_keyframe {
                            0x0200_0000
                        } else {
                            0x0101_0000
                        };
                        rn.extend_from_slice(&flags.to_be_bytes());
                    }
                });
            });
        }
    });

    let moof_size = moof.len();
    for (idx, pos) in data_offset_positions.into_iter().enumerate() {
        let offset = (moof_size + 8 + track_mdat_offsets[idx]) as i32;
        moof[pos..pos + 4].copy_from_slice(&offset.to_be_bytes());
    }

    let mut mdat = Vec::with_capacity(8 + mdat_payload.len());
    let mdat_size = (8 + mdat_payload.len()) as u32;
    mdat.extend_from_slice(&mdat_size.to_be_bytes());
    mdat.extend_from_slice(b"mdat");
    mdat.extend_from_slice(&mdat_payload);

    let mut out = Vec::with_capacity(moof.len() + mdat.len());
    out.extend_from_slice(&moof);
    out.extend_from_slice(&mdat);
    out
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Mp4ValidationReport {
    pub track_count: usize,
    pub fragment_count: usize,
    pub video_sample_count: usize,
    pub video_keyframe_count: usize,
    pub audio_sample_counts: Vec<(u32, usize)>,
    pub video_duration_ms: u64,
    pub max_av_drift_ms: u64,
}

fn read_u32(data: &[u8], off: usize) -> Option<u32> {
    Some(u32::from_be_bytes(data.get(off..off + 4)?.try_into().ok()?))
}

fn read_u64(data: &[u8], off: usize) -> Option<u64> {
    Some(u64::from_be_bytes(data.get(off..off + 8)?.try_into().ok()?))
}

struct ParsedBox<'a> {
    fourcc: [u8; 4],
    start: usize,
    end: usize,
    payload: &'a [u8],
}

fn iter_boxes(buf: &[u8]) -> Result<Vec<ParsedBox<'_>>, String> {
    let mut out = Vec::new();
    let mut pos = 0usize;
    while pos < buf.len() {
        if pos + 8 > buf.len() {
            return Err("truncated MP4 box header".to_string());
        }
        let size = read_u32(buf, pos).ok_or("bad box size")? as usize;
        if size < 8 || pos + size > buf.len() {
            return Err(format!("invalid MP4 box size {size} at offset {pos}"));
        }
        let fourcc: [u8; 4] = buf[pos + 4..pos + 8].try_into().map_err(|_| "bad fourcc")?;
        out.push(ParsedBox {
            fourcc,
            start: pos,
            end: pos + size,
            payload: &buf[pos + 8..pos + size],
        });
        pos += size;
    }
    Ok(out)
}

pub fn validate_mp4(bytes: &[u8]) -> Result<Mp4ValidationReport, String> {
    let top = iter_boxes(bytes)?;
    if top.is_empty() || top[0].fourcc != *b"ftyp" {
        return Err("missing leading ftyp box".to_string());
    }
    let moov = top
        .iter()
        .find(|b| b.fourcc == *b"moov")
        .ok_or("missing moov box")?;

    let mut track_timescales: Vec<(u32, u32)> = Vec::new();
    for child in iter_boxes(moov.payload)? {
        if child.fourcc == *b"trak" {
            let mut tid = 0u32;
            let mut timescale = 90_000u32;
            for tchild in iter_boxes(child.payload)? {
                if tchild.fourcc == *b"tkhd" && tchild.payload.len() >= 16 {
                    tid = read_u32(tchild.payload, 12).unwrap_or(0);
                } else if tchild.fourcc == *b"mdia" {
                    for mchild in iter_boxes(tchild.payload)? {
                        if mchild.fourcc == *b"mdhd" && mchild.payload.len() >= 16 {
                            timescale = read_u32(mchild.payload, 12).unwrap_or(90_000);
                        }
                    }
                }
            }
            if tid > 0 {
                track_timescales.push((tid, timescale.max(1)));
            }
        }
    }
    if track_timescales.is_empty() {
        return Err("moov contains no tracks".to_string());
    }

    let mut fragment_count = 0usize;
    let mut video_sample_count = 0usize;
    let mut video_keyframe_count = 0usize;
    let mut audio_counts: Vec<(u32, usize)> = track_timescales
        .iter()
        .skip(1)
        .map(|(tid, _)| (*tid, 0))
        .collect();
    let mut last_dts: Vec<(u32, Option<u64>)> = track_timescales
        .iter()
        .map(|(tid, _)| (*tid, None))
        .collect();
    let mut track_span: Vec<(u32, Option<u64>, Option<u64>)> = track_timescales
        .iter()
        .map(|(tid, _)| (*tid, None, None))
        .collect();

    let mut idx = 0;
    while idx < top.len() {
        if top[idx].fourcc == *b"moof" {
            let mdat = top.get(idx + 1).ok_or("moof without following mdat")?;
            if mdat.fourcc != *b"mdat" {
                return Err("moof not immediately followed by mdat".to_string());
            }
            fragment_count += 1;
            let moof_len = top[idx].end - top[idx].start;
            let mdat_total = mdat.end - mdat.start;
            for child in iter_boxes(top[idx].payload)? {
                if child.fourcc == *b"traf" {
                    let mut tid = 0u32;
                    let mut base_dts = 0u64;
                    for tr in iter_boxes(child.payload)? {
                        if tr.fourcc == *b"tfhd" && tr.payload.len() >= 8 {
                            tid = read_u32(tr.payload, 4).unwrap_or(0);
                        } else if tr.fourcc == *b"tfdt" && tr.payload.len() >= 12 {
                            let version = tr.payload[0];
                            base_dts = if version == 1 {
                                read_u64(tr.payload, 4).unwrap_or(0)
                            } else {
                                read_u32(tr.payload, 4).unwrap_or(0) as u64
                            };
                        } else if tr.fourcc == *b"trun" && tr.payload.len() >= 12 {
                            let sample_count = read_u32(tr.payload, 4).unwrap_or(0) as usize;
                            let data_offset = read_u32(tr.payload, 8).unwrap_or(0) as usize;
                            if data_offset < moof_len + 8 {
                                return Err(
                                    "trun data_offset points before mdat payload".to_string()
                                );
                            }
                            let mut cur_off = data_offset - moof_len;
                            let mut cur_dts = base_dts;
                            let mut pos = 12usize;
                            for _ in 0..sample_count {
                                if pos + 12 > tr.payload.len() {
                                    return Err("truncated trun sample entry".to_string());
                                }
                                let dur = read_u32(tr.payload, pos).unwrap_or(0) as u64;
                                let sz = read_u32(tr.payload, pos + 4).unwrap_or(0) as usize;
                                let flags = read_u32(tr.payload, pos + 8).unwrap_or(0);
                                pos += 12;
                                if cur_off + sz > mdat_total {
                                    return Err("sample payload exceeds mdat bounds".to_string());
                                }
                                cur_off += sz;
                                if let Some((_, prev)) =
                                    last_dts.iter_mut().find(|(t, _)| *t == tid)
                                {
                                    if let Some(p) = *prev
                                        && cur_dts < p
                                    {
                                        return Err(format!(
                                            "non-monotonic DTS on track {tid}: {cur_dts} < {p}"
                                        ));
                                    }
                                    *prev = Some(cur_dts);
                                }
                                if let Some((_, first, last)) =
                                    track_span.iter_mut().find(|(t, _, _)| *t == tid)
                                {
                                    if first.is_none() {
                                        *first = Some(cur_dts);
                                    }
                                    *last = Some(cur_dts + dur);
                                }
                                if tid == 1 {
                                    video_sample_count += 1;
                                    if (flags & 0x0100_0000) == 0 {
                                        video_keyframe_count += 1;
                                    }
                                } else if let Some((_, cnt)) =
                                    audio_counts.iter_mut().find(|(t, _)| *t == tid)
                                {
                                    *cnt += 1;
                                }
                                cur_dts += dur;
                            }
                        }
                    }
                }
            }
            idx += 2;
        } else {
            idx += 1;
        }
    }

    if fragment_count == 0 || video_sample_count == 0 {
        return Err("MP4 contains no video samples".to_string());
    }
    if video_keyframe_count == 0 {
        return Err("MP4 contains no video keyframes".to_string());
    }

    let video_ts = track_timescales[0].1 as u64;
    let video_duration_ms = match track_span[0] {
        (_, Some(s), Some(e)) => e.saturating_sub(s).saturating_mul(1000) / video_ts,
        _ => 0,
    };

    let mut max_av_drift_ms = 0u64;
    for (tid, first, last) in track_span.iter().skip(1) {
        if let (Some(s), Some(e)) = (first, last)
            && let Some((_, ts)) = track_timescales.iter().find(|(t, _)| t == tid)
        {
            let aud_ms = e.saturating_sub(*s).saturating_mul(1000) / (*ts as u64);
            let drift = aud_ms.abs_diff(video_duration_ms);
            max_av_drift_ms = max_av_drift_ms.max(drift);
        }
    }

    Ok(Mp4ValidationReport {
        track_count: track_timescales.len(),
        fragment_count,
        video_sample_count,
        video_keyframe_count,
        audio_sample_counts: audio_counts,
        video_duration_ms,
        max_av_drift_ms,
    })
}

pub fn trim_fmp4_keyframes(bytes: &[u8], start_ms: u64, end_ms: u64) -> Result<Vec<u8>, String> {
    if end_ms <= start_ms {
        return Err("invalid trim range".to_string());
    }
    let top = iter_boxes(bytes)?;
    let ftyp = top
        .iter()
        .find(|b| b.fourcc == *b"ftyp")
        .ok_or("missing ftyp")?;
    let moov = top
        .iter()
        .find(|b| b.fourcc == *b"moov")
        .ok_or("missing moov")?;

    let mut video_timescale = 90_000u64;
    for child in iter_boxes(moov.payload)? {
        if child.fourcc == *b"trak" {
            for tc in iter_boxes(child.payload)? {
                if tc.fourcc == *b"mdia" {
                    for mc in iter_boxes(tc.payload)? {
                        if mc.fourcc == *b"mdhd" && mc.payload.len() >= 16 {
                            video_timescale = read_u32(mc.payload, 12).unwrap_or(90_000) as u64;
                        }
                    }
                }
            }
            break;
        }
    }

    let mut out = Vec::new();
    out.extend_from_slice(&bytes[ftyp.start..ftyp.end]);
    out.extend_from_slice(&bytes[moov.start..moov.end]);

    let mut kept = 0usize;
    let mut idx = 0usize;
    while idx < top.len() {
        if top[idx].fourcc == *b"moof" {
            let mdat = top.get(idx + 1).ok_or("moof without mdat")?;
            let frag_ms = fragment_video_start_ms(top[idx].payload, video_timescale)?;
            if frag_ms <= end_ms
                && (frag_ms >= start_ms
                    || next_fragment_ms(&top, idx + 2, video_timescale)
                        .is_some_and(|n| n > start_ms))
            {
                out.extend_from_slice(&bytes[top[idx].start..top[idx].end]);
                out.extend_from_slice(&bytes[mdat.start..mdat.end]);
                kept += 1;
            }
            idx += 2;
        } else {
            idx += 1;
        }
    }

    if kept == 0 {
        return Err("no keyframes fell inside the requested trim range".to_string());
    }
    Ok(out)
}

fn fragment_video_start_ms(moof_payload: &[u8], timescale: u64) -> Result<u64, String> {
    for child in iter_boxes(moof_payload)? {
        if child.fourcc == *b"traf" {
            let mut tid = 0u32;
            let mut dts = 0u64;
            for tr in iter_boxes(child.payload)? {
                if tr.fourcc == *b"tfhd" && tr.payload.len() >= 8 {
                    tid = read_u32(tr.payload, 4).unwrap_or(0);
                } else if tr.fourcc == *b"tfdt" && tr.payload.len() >= 12 {
                    dts = if tr.payload[0] == 1 {
                        read_u64(tr.payload, 4).unwrap_or(0)
                    } else {
                        read_u32(tr.payload, 4).unwrap_or(0) as u64
                    };
                }
            }
            if tid == 1 {
                return Ok(dts.saturating_mul(1000) / timescale.max(1));
            }
        }
    }
    Ok(0)
}

fn next_fragment_ms(top: &[ParsedBox<'_>], from: usize, timescale: u64) -> Option<u64> {
    for b in &top[from..] {
        if b.fourcc == *b"moof" {
            return fragment_video_start_ms(b.payload, timescale).ok();
        }
    }
    None
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn multitrack_fmp4_mux_validate_and_keyframe_trim_roundtrip() {
        let video = VideoTrackConfig {
            codec: VideoCodec::H264,
            width: 1920,
            height: 1080,
            timescale: 90_000,
            fps: 60,
            hdr10: false,
            codec_config: build_avcc_from_sps_pps(&[0x67, 0x64, 0x00, 0x2A], &[0x68, 0xEE, 0x3C]),
        };
        let audio = vec![
            AudioTrackConfig::aac_lc_48khz_stereo(TrackKind::AudioGame, "Game"),
            AudioTrackConfig::aac_lc_48khz_stereo(TrackKind::AudioMic, "Microphone"),
            AudioTrackConfig::aac_lc_48khz_stereo(TrackKind::AudioVoice, "Voice"),
        ];

        let mut packets = Vec::new();
        for sec in 0..4i64 {
            for f in 0..10i64 {
                let ts = (sec * 1000 + f * 100) * 10_000;
                packets.push(EncodedPacket {
                    track: TrackKind::Video,
                    data: vec![0x00, 0x00, 0x00, 0x04, 0x65, 0x88, 0x84, 0x21],
                    timestamp_100ns: ts,
                    duration_100ns: 1_000_000,
                    is_keyframe: f == 0,
                });
                for kind in [
                    TrackKind::AudioGame,
                    TrackKind::AudioMic,
                    TrackKind::AudioVoice,
                ] {
                    packets.push(EncodedPacket {
                        track: kind,
                        data: vec![0xDE, 0xAD, 0xBE, 0xEF, 0x11, 0x22],
                        timestamp_100ns: ts,
                        duration_100ns: 1_000_000,
                        is_keyframe: true,
                    });
                }
            }
        }

        let mp4 = mux_packets_to_fmp4(video, audio, &packets);
        let report = validate_mp4(&mp4).expect("valid fmp4");
        assert_eq!(report.track_count, 4);
        assert_eq!(report.fragment_count, 4);
        assert_eq!(report.video_sample_count, 40);
        assert_eq!(report.video_keyframe_count, 4);
        assert_eq!(report.audio_sample_counts.len(), 3);
        assert!(report.audio_sample_counts.iter().all(|(_, c)| *c == 40));
        assert!(report.max_av_drift_ms <= 50);

        let trimmed = trim_fmp4_keyframes(&mp4, 1_000, 2_500).expect("valid trimmed fmp4");
        let trim_report = validate_mp4(&trimmed).expect("trimmed mp4 passes integrity check");
        assert_eq!(trim_report.track_count, 4);
        assert!(trim_report.fragment_count >= 2 && trim_report.fragment_count <= 3);
        assert!(trim_report.video_keyframe_count >= 2);
    }

    #[test]
    fn annex_b_conversion_extracts_sps_pps_and_length_prefixes() {
        let annex = [
            0, 0, 0, 1, 0x67, 0x64, 0x00, 0x2A, 0, 0, 0, 1, 0x68, 0xEE, 0x3C, 0, 0, 1, 0x65, 0x88,
        ];
        let (sps, pps) = extract_h264_sps_pps(&annex).expect("sps and pps found");
        assert_eq!(sps, vec![0x67, 0x64, 0x00, 0x2A]);
        assert_eq!(pps, vec![0x68, 0xEE, 0x3C]);
        let lp = annex_b_to_length_prefixed(&annex);
        assert_eq!(&lp[0..4], &4u32.to_be_bytes());
    }
}
