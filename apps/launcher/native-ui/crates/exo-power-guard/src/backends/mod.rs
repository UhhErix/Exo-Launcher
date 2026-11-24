pub mod amd;
pub mod d3dkmt;
pub mod intel;
pub mod nvidia;

use crate::types::{BackendKind, GpuSnapshot};

pub trait TelemetryBackend {
    fn kind(&self) -> BackendKind;
    fn read(&mut self) -> Option<GpuSnapshot>;
}

pub fn discover_primary_backend() -> Option<Box<dyn TelemetryBackend>> {
    if let Some(nv) = nvidia::NvidiaBackend::open() {
        return Some(Box::new(nv));
    }
    if let Some(amd) = amd::AmdAdlxBackend::open() {
        return Some(Box::new(amd));
    }
    if let Some(intel) = intel::IntelIgclBackend::open() {
        return Some(Box::new(intel));
    }
    if let Some(d3d) = d3dkmt::D3dkmtBackend::open() {
        return Some(Box::new(d3d));
    }
    None
}

pub fn discover_all_backends() -> Vec<Box<dyn TelemetryBackend>> {
    let mut out: Vec<Box<dyn TelemetryBackend>> = Vec::new();
    if let Some(nv) = nvidia::NvidiaBackend::open() {
        out.push(Box::new(nv));
    }
    if let Some(amd) = amd::AmdAdlxBackend::open() {
        out.push(Box::new(amd));
    }
    if let Some(intel) = intel::IntelIgclBackend::open() {
        out.push(Box::new(intel));
    }
    if let Some(d3d) = d3dkmt::D3dkmtBackend::open() {
        out.push(Box::new(d3d));
    }
    out
}
