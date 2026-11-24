use std::sync::Arc;
use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};

use windows::Foundation::TypedEventHandler;
use windows::Graphics::Capture::{
    Direct3D11CaptureFramePool, GraphicsCaptureItem, GraphicsCaptureSession,
};
use windows::Graphics::DirectX::Direct3D11::IDirect3DDevice;
use windows::Graphics::DirectX::DirectXPixelFormat;
use windows::Win32::Foundation::HWND;
use windows::Win32::Graphics::Direct3D11::ID3D11Texture2D;
use windows::Win32::Graphics::Dxgi::IDXGIDevice;
use windows::Win32::Graphics::Gdi::HMONITOR;
use windows::Win32::System::WinRT::Direct3D11::{
    CreateDirect3D11DeviceFromDXGIDevice, IDirect3DDxgiInterfaceAccess,
};
use windows::Win32::System::WinRT::Graphics::Capture::IGraphicsCaptureItemInterop;
use windows::Win32::UI::WindowsAndMessaging::IsWindow;
use windows::core::{IInspectable, Interface, factory};

use crate::gpu::GpuDevice;

#[derive(Debug, Clone, Copy)]
pub enum WgcTarget {
    Window(isize),
    Monitor(isize),
}

pub struct WgcFrame {
    pub texture: ID3D11Texture2D,
    pub width: u32,
    pub height: u32,
    pub system_relative_time_100ns: i64,
}

pub struct WgcCapture {
    _gpu: Arc<GpuDevice>,
    winrt_device: IDirect3DDevice,
    _item: GraphicsCaptureItem,
    frame_pool: Direct3D11CaptureFramePool,
    session: GraphicsCaptureSession,
    target: WgcTarget,
    pixel_format: DirectXPixelFormat,
    width: u32,
    height: u32,
    frames_arrived: Arc<AtomicU64>,
    closed: Arc<AtomicBool>,
}

impl WgcCapture {
    pub fn open(
        gpu: Arc<GpuDevice>,
        target: WgcTarget,
        cursor: bool,
        hdr: bool,
    ) -> Result<Self, String> {
        let dxgi_device: IDXGIDevice = gpu
            .device
            .cast()
            .map_err(|e| format!("cast IDXGIDevice failed: {e}"))?;
        let inspectable: IInspectable =
            unsafe { CreateDirect3D11DeviceFromDXGIDevice(&dxgi_device) }
                .map_err(|e| format!("CreateDirect3D11DeviceFromDXGIDevice failed: {e}"))?;
        let winrt_device: IDirect3DDevice = inspectable
            .cast()
            .map_err(|e| format!("cast IDirect3DDevice failed: {e}"))?;

        let interop = factory::<GraphicsCaptureItem, IGraphicsCaptureItemInterop>()
            .map_err(|e| format!("IGraphicsCaptureItemInterop factory failed: {e}"))?;

        let item: GraphicsCaptureItem = match target {
            WgcTarget::Window(raw) => unsafe {
                interop
                    .CreateForWindow(HWND(raw as *mut _))
                    .map_err(|e| format!("CreateForWindow(0x{raw:X}) failed: {e}"))?
            },
            WgcTarget::Monitor(raw) => unsafe {
                interop
                    .CreateForMonitor(HMONITOR(raw as *mut _))
                    .map_err(|e| format!("CreateForMonitor(0x{raw:X}) failed: {e}"))?
            },
        };

        let size = item
            .Size()
            .map_err(|e| format!("GraphicsCaptureItem::Size failed: {e}"))?;
        if size.Width <= 0 || size.Height <= 0 {
            return Err("capture target has zero size".to_string());
        }

        let pixel_format = if hdr {
            DirectXPixelFormat::R16G16B16A16Float
        } else {
            DirectXPixelFormat::B8G8R8A8UIntNormalized
        };

        let frame_pool =
            Direct3D11CaptureFramePool::CreateFreeThreaded(&winrt_device, pixel_format, 2, size)
                .map_err(|e| format!("CreateFreeThreaded frame pool failed: {e}"))?;

        let session = frame_pool
            .CreateCaptureSession(&item)
            .map_err(|e| format!("CreateCaptureSession failed: {e}"))?;

        let _ = session.SetIsBorderRequired(false);
        let _ = session.SetIsCursorCaptureEnabled(cursor);

        let frames_arrived = Arc::new(AtomicU64::new(0));
        let counter = Arc::clone(&frames_arrived);
        let _ = frame_pool.FrameArrived(&TypedEventHandler::new(move |_, _| {
            counter.fetch_add(1, Ordering::Relaxed);
            Ok(())
        }));

        let closed = Arc::new(AtomicBool::new(false));
        let closed_flag = Arc::clone(&closed);
        let _ = item.Closed(&TypedEventHandler::new(move |_, _| {
            closed_flag.store(true, Ordering::Release);
            Ok(())
        }));

        session
            .StartCapture()
            .map_err(|e| format!("StartCapture failed: {e}"))?;

        Ok(Self {
            _gpu: gpu,
            winrt_device,
            _item: item,
            frame_pool,
            session,
            target,
            pixel_format,
            width: size.Width as u32,
            height: size.Height as u32,
            frames_arrived,
            closed,
        })
    }

    pub fn is_closed(&self) -> bool {
        if self.closed.load(Ordering::Acquire) {
            return true;
        }
        if let WgcTarget::Window(raw) = self.target {
            return !unsafe { IsWindow(Some(HWND(raw as *mut _))) }.as_bool();
        }
        false
    }

    pub fn set_cursor_enabled(&self, enabled: bool) {
        let _ = self.session.SetIsCursorCaptureEnabled(enabled);
    }

    pub fn width(&self) -> u32 {
        self.width
    }

    pub fn height(&self) -> u32 {
        self.height
    }

    pub fn arrived_count(&self) -> u64 {
        self.frames_arrived.load(Ordering::Relaxed)
    }

    pub fn try_next_frame(&mut self) -> Result<Option<WgcFrame>, String> {
        let mut latest = None;
        while let Ok(frame) = self.frame_pool.TryGetNextFrame() {
            latest = Some(frame);
        }
        let Some(frame) = latest else {
            return Ok(None);
        };

        let content_size = frame
            .ContentSize()
            .map_err(|e| format!("ContentSize failed: {e}"))?;
        let ts = frame.SystemRelativeTime().map(|t| t.Duration).unwrap_or(0);

        let surface = frame
            .Surface()
            .map_err(|e| format!("Frame::Surface failed: {e}"))?;
        let access: IDirect3DDxgiInterfaceAccess = surface
            .cast()
            .map_err(|e| format!("IDirect3DDxgiInterfaceAccess cast failed: {e}"))?;
        let texture: ID3D11Texture2D = unsafe { access.GetInterface() }
            .map_err(|e| format!("GetInterface<ID3D11Texture2D> failed: {e}"))?;

        let w = content_size.Width.max(1) as u32;
        let h = content_size.Height.max(1) as u32;
        if w != self.width || h != self.height {
            self.width = w;
            self.height = h;
            let _ =
                self.frame_pool
                    .Recreate(&self.winrt_device, self.pixel_format, 2, content_size);
        }

        let _ = frame.Close();
        Ok(Some(WgcFrame {
            texture,
            width: w,
            height: h,
            system_relative_time_100ns: ts,
        }))
    }
}

impl Drop for WgcCapture {
    fn drop(&mut self) {
        let _ = self.session.Close();
        let _ = self.frame_pool.Close();
    }
}
