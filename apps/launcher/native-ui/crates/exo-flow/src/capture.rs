use std::time::Instant;
use windows::Graphics::Capture::{
    Direct3D11CaptureFramePool, GraphicsCaptureItem, GraphicsCaptureSession,
};
use windows::Graphics::DirectX::Direct3D11::IDirect3DDevice;
use windows::Graphics::DirectX::DirectXPixelFormat;
use windows::Win32::Foundation::{CloseHandle, HANDLE, HWND};
use windows::Win32::Graphics::Direct3D::{D3D_DRIVER_TYPE_HARDWARE, D3D_FEATURE_LEVEL_11_1};
use windows::Win32::Graphics::Direct3D11::{
    D3D11_BIND_RENDER_TARGET, D3D11_BIND_SHADER_RESOURCE, D3D11_CREATE_DEVICE_BGRA_SUPPORT,
    D3D11_RESOURCE_MISC_SHARED, D3D11_RESOURCE_MISC_SHARED_NTHANDLE, D3D11_SDK_VERSION,
    D3D11_TEXTURE2D_DESC, D3D11_USAGE_DEFAULT, D3D11CreateDevice, ID3D11Device,
    ID3D11DeviceContext, ID3D11Texture2D,
};
use windows::Win32::Graphics::Direct3D12::{ID3D12Device, ID3D12Resource};
use windows::Win32::Graphics::Dxgi::Common::{DXGI_FORMAT_B8G8R8A8_UNORM, DXGI_SAMPLE_DESC};
use windows::Win32::Graphics::Dxgi::{DXGI_SHARED_RESOURCE_READ, IDXGIDevice, IDXGIResource1};
use windows::Win32::System::WinRT::Direct3D11::{
    CreateDirect3D11DeviceFromDXGIDevice, IDirect3DDxgiInterfaceAccess,
};
use windows::Win32::System::WinRT::Graphics::Capture::IGraphicsCaptureItemInterop;
use windows::Win32::System::WinRT::{RO_INIT_MULTITHREADED, RoInitialize};
use windows::core::{IInspectable, Interface, factory};

pub struct WgcZeroCopyCapture {
    d3d11_device: ID3D11Device,
    d3d11_ctx: ID3D11DeviceContext,
    _winrt_device: IDirect3DDevice,
    _item: GraphicsCaptureItem,
    frame_pool: Direct3D11CaptureFramePool,
    session: GraphicsCaptureSession,
    shared_tex_11: ID3D11Texture2D,
    shared_handle: HANDLE,
    dx12_shared: ID3D12Resource,
    #[allow(dead_code)]
    pub width: u32,
    #[allow(dead_code)]
    pub height: u32,
}

impl WgcZeroCopyCapture {
    pub fn start(hwnd_raw: isize, dx12_device: &ID3D12Device) -> Result<Self, String> {
        unsafe {
            let _ = RoInitialize(RO_INIT_MULTITHREADED);

            let mut d3d11_dev = None;
            let mut d3d11_ctx = None;
            D3D11CreateDevice(
                None,
                D3D_DRIVER_TYPE_HARDWARE,
                windows::Win32::Foundation::HMODULE::default(),
                D3D11_CREATE_DEVICE_BGRA_SUPPORT,
                Some(&[D3D_FEATURE_LEVEL_11_1]),
                D3D11_SDK_VERSION,
                Some(&mut d3d11_dev),
                None,
                Some(&mut d3d11_ctx),
            )
            .map_err(|e| format!("D3D11CreateDevice: {e}"))?;
            let d3d11_device = d3d11_dev.ok_or("d3d11 device null")?;
            let d3d11_ctx = d3d11_ctx.ok_or("d3d11 ctx null")?;
            let dxgi_dev: IDXGIDevice = d3d11_device
                .cast()
                .map_err(|e| format!("cast IDXGIDevice: {e}"))?;
            let inspectable: IInspectable = CreateDirect3D11DeviceFromDXGIDevice(&dxgi_dev)
                .map_err(|e| format!("CreateDirect3D11DeviceFromDXGIDevice: {e}"))?;
            let winrt_device: IDirect3DDevice = inspectable
                .cast()
                .map_err(|e| format!("cast IDirect3DDevice: {e}"))?;

            let interop = factory::<GraphicsCaptureItem, IGraphicsCaptureItemInterop>()
                .map_err(|e| format!("IGraphicsCaptureItemInterop factory: {e}"))?;
            let item: GraphicsCaptureItem = interop
                .CreateForWindow(HWND(hwnd_raw as *mut _))
                .map_err(|e| format!("CreateForWindow: {e}"))?;
            let size = item.Size().map_err(|e| format!("item.Size: {e}"))?;
            let width = size.Width.max(1) as u32;
            let height = size.Height.max(1) as u32;

            let frame_pool = Direct3D11CaptureFramePool::CreateFreeThreaded(
                &winrt_device,
                DirectXPixelFormat::B8G8R8A8UIntNormalized,
                2,
                size,
            )
            .map_err(|e| format!("CreateFreeThreaded: {e}"))?;
            let session = frame_pool
                .CreateCaptureSession(&item)
                .map_err(|e| format!("CreateCaptureSession: {e}"))?;
            let _ = session.SetIsBorderRequired(false);
            let _ = session.SetIsCursorCaptureEnabled(false);
            session
                .StartCapture()
                .map_err(|e| format!("StartCapture: {e}"))?;

            let shared_desc = D3D11_TEXTURE2D_DESC {
                Width: width,
                Height: height,
                MipLevels: 1,
                ArraySize: 1,
                Format: DXGI_FORMAT_B8G8R8A8_UNORM,
                SampleDesc: DXGI_SAMPLE_DESC {
                    Count: 1,
                    Quality: 0,
                },
                Usage: D3D11_USAGE_DEFAULT,
                BindFlags: (D3D11_BIND_SHADER_RESOURCE.0 | D3D11_BIND_RENDER_TARGET.0) as u32,
                CPUAccessFlags: 0,
                MiscFlags: (D3D11_RESOURCE_MISC_SHARED.0 | D3D11_RESOURCE_MISC_SHARED_NTHANDLE.0)
                    as u32,
            };
            let mut shared_tex_opt = None;
            d3d11_device
                .CreateTexture2D(&shared_desc, None, Some(&mut shared_tex_opt))
                .map_err(|e| format!("CreateTexture2D(shared): {e}"))?;
            let shared_tex_11 = shared_tex_opt.ok_or("shared tex null")?;
            let dxgi_res: IDXGIResource1 = shared_tex_11
                .cast()
                .map_err(|e| format!("cast IDXGIResource1: {e}"))?;
            let shared_handle = dxgi_res
                .CreateSharedHandle(None, DXGI_SHARED_RESOURCE_READ.0, None)
                .map_err(|e| format!("CreateSharedHandle: {e}"))?;
            let mut dx12_opt: Option<ID3D12Resource> = None;
            dx12_device
                .OpenSharedHandle(shared_handle, &mut dx12_opt)
                .map_err(|e| format!("ID3D12Device::OpenSharedHandle: {e}"))?;
            let dx12_shared = dx12_opt.ok_or("OpenSharedHandle null")?;

            Ok(Self {
                d3d11_device,
                d3d11_ctx,
                _winrt_device: winrt_device,
                _item: item,
                frame_pool,
                session,
                shared_tex_11,
                shared_handle,
                dx12_shared,
                width,
                height,
            })
        }
    }

    pub fn try_acquire_latest(&self) -> Option<(f64, &ID3D12Resource)> {
        let t0 = Instant::now();
        let frame = self.frame_pool.TryGetNextFrame().ok()?;
        let surface = frame.Surface().ok()?;
        let access: IDirect3DDxgiInterfaceAccess = surface.cast().ok()?;
        let tex11: ID3D11Texture2D = unsafe { access.GetInterface().ok()? };
        unsafe {
            self.d3d11_ctx.CopyResource(&self.shared_tex_11, &tex11);
            self.d3d11_ctx.Flush();
        }
        let _ = &self.d3d11_device;
        let _ = frame.Close();
        Some((t0.elapsed().as_secs_f64() * 1000.0, &self.dx12_shared))
    }
}

impl Drop for WgcZeroCopyCapture {
    fn drop(&mut self) {
        let _ = self.session.Close();
        let _ = self.frame_pool.Close();
        unsafe {
            let _ = CloseHandle(self.shared_handle);
        }
    }
}
