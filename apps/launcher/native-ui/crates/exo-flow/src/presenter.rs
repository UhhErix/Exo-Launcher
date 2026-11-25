use std::mem::ManuallyDrop;
use std::sync::Once;
use windows::Win32::Foundation::{CloseHandle, HANDLE, HINSTANCE, HWND, LPARAM, LRESULT, WPARAM};
use windows::Win32::Graphics::Direct3D12::{
    D3D12_COMMAND_LIST_TYPE_DIRECT, D3D12_FENCE_FLAG_NONE, D3D12_RESOURCE_BARRIER,
    D3D12_RESOURCE_BARRIER_0, D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES,
    D3D12_RESOURCE_BARRIER_FLAG_NONE, D3D12_RESOURCE_BARRIER_TYPE_TRANSITION,
    D3D12_RESOURCE_STATE_COMMON, D3D12_RESOURCE_STATE_COPY_DEST, D3D12_RESOURCE_STATE_COPY_SOURCE,
    D3D12_RESOURCE_STATE_PRESENT, D3D12_RESOURCE_STATES, D3D12_RESOURCE_TRANSITION_BARRIER,
    ID3D12CommandAllocator, ID3D12CommandQueue, ID3D12Device, ID3D12Fence,
    ID3D12GraphicsCommandList, ID3D12Resource,
};
use windows::Win32::Graphics::DirectComposition::{
    DCompositionCreateDevice, IDCompositionDevice, IDCompositionTarget, IDCompositionVisual,
};
use windows::Win32::Graphics::Dxgi::Common::{
    DXGI_ALPHA_MODE_PREMULTIPLIED, DXGI_FORMAT_B8G8R8A8_UNORM, DXGI_SAMPLE_DESC,
};
use windows::Win32::Graphics::Dxgi::{
    DXGI_ERROR_DEVICE_REMOVED, DXGI_ERROR_DEVICE_RESET, DXGI_PRESENT, DXGI_PRESENT_PARAMETERS,
    DXGI_SCALING_STRETCH, DXGI_SWAP_CHAIN_DESC1,
    DXGI_SWAP_CHAIN_FLAG_FRAME_LATENCY_WAITABLE_OBJECT, DXGI_SWAP_EFFECT_FLIP_DISCARD,
    DXGI_USAGE_RENDER_TARGET_OUTPUT, IDXGIFactory4, IDXGISwapChain3,
};
use windows::Win32::System::LibraryLoader::GetModuleHandleW;
use windows::Win32::System::Threading::{CreateEventW, WaitForSingleObjectEx};
use windows::Win32::UI::WindowsAndMessaging::{
    CreateWindowExW, DefWindowProcW, DestroyWindow, GetForegroundWindow, IsIconic, IsWindow,
    RegisterClassExW, SW_HIDE, SW_SHOWNOACTIVATE, ShowWindow, WNDCLASSEXW, WS_EX_LAYERED,
    WS_EX_NOACTIVATE, WS_EX_NOREDIRECTIONBITMAP, WS_EX_TOOLWINDOW, WS_EX_TOPMOST,
    WS_EX_TRANSPARENT, WS_POPUP,
};
use windows::core::{Interface, PCWSTR, w};

static CLASS_INIT: Once = Once::new();
const OVERLAY_CLASS: PCWSTR = w!("ExoFlowOverlayWindow");
const BACK_BUFFER_COUNT: usize = 2;

unsafe extern "system" fn overlay_wnd_proc(
    hwnd: HWND,
    msg: u32,
    wparam: WPARAM,
    lparam: LPARAM,
) -> LRESULT {
    unsafe { DefWindowProcW(hwnd, msg, wparam, lparam) }
}

pub struct DCompOverlayPresenter {
    hwnd: HWND,
    visible: bool,
    swap_chain: IDXGISwapChain3,
    back_buffers: Vec<ID3D12Resource>,
    frame_latency_waitable: HANDLE,
    _dcomp_device: IDCompositionDevice,
    _dcomp_target: IDCompositionTarget,
    _dcomp_visual: IDCompositionVisual,
    allocators: Vec<ID3D12CommandAllocator>,
    cmd_list: ID3D12GraphicsCommandList,
    fence: ID3D12Fence,
    fence_event: HANDLE,
    fence_value: u64,
    pub device_lost_count: u32,
}

impl DCompOverlayPresenter {
    #[allow(clippy::too_many_arguments)]
    pub fn new(
        factory: &IDXGIFactory4,
        device: &ID3D12Device,
        queue: &ID3D12CommandQueue,
        x: i32,
        y: i32,
        width: u32,
        height: u32,
        visible: bool,
    ) -> Result<Self, String> {
        let hmodule =
            unsafe { GetModuleHandleW(None).map_err(|e| format!("GetModuleHandleW: {e}"))? };
        let hinstance = HINSTANCE(hmodule.0);

        CLASS_INIT.call_once(|| {
            let wc = WNDCLASSEXW {
                cbSize: std::mem::size_of::<WNDCLASSEXW>() as u32,
                lpfnWndProc: Some(overlay_wnd_proc),
                hInstance: hinstance,
                lpszClassName: OVERLAY_CLASS,
                ..Default::default()
            };
            unsafe {
                let _ = RegisterClassExW(&wc);
            }
        });

        let ex_style = WS_EX_NOREDIRECTIONBITMAP
            | WS_EX_TRANSPARENT
            | WS_EX_TOPMOST
            | WS_EX_NOACTIVATE
            | WS_EX_TOOLWINDOW
            | WS_EX_LAYERED;

        let hwnd = unsafe {
            CreateWindowExW(
                ex_style,
                OVERLAY_CLASS,
                w!("Exo Flow Overlay"),
                WS_POPUP,
                x,
                y,
                width as i32,
                height as i32,
                None,
                None,
                Some(hinstance),
                None,
            )
            .map_err(|e| format!("CreateWindowExW: {e}"))?
        };

        unsafe {
            let _ = ShowWindow(hwnd, if visible { SW_SHOWNOACTIVATE } else { SW_HIDE });
        }

        let sc_desc = DXGI_SWAP_CHAIN_DESC1 {
            Width: width,
            Height: height,
            Format: DXGI_FORMAT_B8G8R8A8_UNORM,
            Stereo: false.into(),
            SampleDesc: DXGI_SAMPLE_DESC {
                Count: 1,
                Quality: 0,
            },
            BufferUsage: DXGI_USAGE_RENDER_TARGET_OUTPUT,
            BufferCount: BACK_BUFFER_COUNT as u32,
            Scaling: DXGI_SCALING_STRETCH,
            SwapEffect: DXGI_SWAP_EFFECT_FLIP_DISCARD,
            AlphaMode: DXGI_ALPHA_MODE_PREMULTIPLIED,
            Flags: DXGI_SWAP_CHAIN_FLAG_FRAME_LATENCY_WAITABLE_OBJECT.0 as u32,
        };

        let swap_chain1 = unsafe {
            factory
                .CreateSwapChainForComposition(queue, &sc_desc, None)
                .map_err(|e| format!("CreateSwapChainForComposition: {e}"))?
        };
        let swap_chain: IDXGISwapChain3 = swap_chain1
            .cast()
            .map_err(|e| format!("Cast IDXGISwapChain3: {e}"))?;

        unsafe {
            let _ = swap_chain.SetMaximumFrameLatency(1);
        }
        let frame_latency_waitable = unsafe { swap_chain.GetFrameLatencyWaitableObject() };

        let dcomp_device: IDCompositionDevice = unsafe {
            DCompositionCreateDevice(None).map_err(|e| format!("DCompositionCreateDevice: {e}"))?
        };
        let dcomp_target = unsafe {
            dcomp_device
                .CreateTargetForHwnd(hwnd, true)
                .map_err(|e| format!("CreateTargetForHwnd: {e}"))?
        };
        let dcomp_visual = unsafe {
            dcomp_device
                .CreateVisual()
                .map_err(|e| format!("CreateVisual: {e}"))?
        };
        unsafe {
            dcomp_visual
                .SetContent(&swap_chain)
                .map_err(|e| format!("SetContent: {e}"))?;
            dcomp_target
                .SetRoot(&dcomp_visual)
                .map_err(|e| format!("SetRoot: {e}"))?;
            dcomp_device.Commit().map_err(|e| format!("Commit: {e}"))?;
        }

        let mut back_buffers = Vec::with_capacity(BACK_BUFFER_COUNT);
        let mut allocators = Vec::with_capacity(BACK_BUFFER_COUNT);
        for idx in 0..BACK_BUFFER_COUNT {
            let buf: ID3D12Resource = unsafe {
                swap_chain
                    .GetBuffer(idx as u32)
                    .map_err(|e| format!("GetBuffer({idx}): {e}"))?
            };
            let alloc: ID3D12CommandAllocator = unsafe {
                device
                    .CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE_DIRECT)
                    .map_err(|e| format!("CreateCommandAllocator: {e}"))?
            };
            back_buffers.push(buf);
            allocators.push(alloc);
        }

        let cmd_list: ID3D12GraphicsCommandList = unsafe {
            device
                .CreateCommandList(0, D3D12_COMMAND_LIST_TYPE_DIRECT, &allocators[0], None)
                .map_err(|e| format!("CreateCommandList: {e}"))?
        };
        unsafe {
            cmd_list
                .Close()
                .map_err(|e| format!("Close initial cmd_list: {e}"))?;
        }

        let fence: ID3D12Fence = unsafe {
            device
                .CreateFence(0, D3D12_FENCE_FLAG_NONE)
                .map_err(|e| format!("CreateFence: {e}"))?
        };
        let fence_event = unsafe {
            CreateEventW(None, false, false, PCWSTR::null())
                .map_err(|e| format!("CreateEventW: {e}"))?
        };

        Ok(Self {
            hwnd,
            visible,
            swap_chain,
            back_buffers,
            frame_latency_waitable,
            _dcomp_device: dcomp_device,
            _dcomp_target: dcomp_target,
            _dcomp_visual: dcomp_visual,
            allocators,
            cmd_list,
            fence,
            fence_event,
            fence_value: 0,
            device_lost_count: 0,
        })
    }

    pub fn hwnd(&self) -> HWND {
        self.hwnd
    }

    pub fn should_disengage_for_target(target_hwnd: isize) -> bool {
        let hwnd = HWND(target_hwnd as *mut _);
        unsafe {
            if !IsWindow(Some(hwnd)).as_bool() {
                return true;
            }
            if IsIconic(hwnd).as_bool() {
                return true;
            }
            let fg = GetForegroundWindow();
            if !fg.0.is_null() && fg != hwnd {
                return true;
            }
        }
        false
    }

    pub fn present_from_resource(
        &mut self,
        queue: &ID3D12CommandQueue,
        src: &ID3D12Resource,
        src_state: D3D12_RESOURCE_STATES,
    ) -> Result<(), String> {
        if self.visible && !self.frame_latency_waitable.is_invalid() {
            unsafe {
                let _ = WaitForSingleObjectEx(self.frame_latency_waitable, 16, false);
            }
        }

        let idx = unsafe { self.swap_chain.GetCurrentBackBufferIndex() as usize };
        let alloc = &self.allocators[idx % self.allocators.len()];
        let dst = &self.back_buffers[idx % self.back_buffers.len()];

        unsafe {
            alloc
                .Reset()
                .map_err(|e| format!("present allocator reset: {e}"))?;
            self.cmd_list
                .Reset(alloc, None)
                .map_err(|e| format!("present cmd_list reset: {e}"))?;

            let mut pre = [
                transition_barrier(src, src_state, D3D12_RESOURCE_STATE_COPY_SOURCE),
                transition_barrier(
                    dst,
                    D3D12_RESOURCE_STATE_COMMON,
                    D3D12_RESOURCE_STATE_COPY_DEST,
                ),
            ];
            self.cmd_list.ResourceBarrier(&pre);
            drop_barriers(&mut pre);

            self.cmd_list.CopyResource(dst, src);

            let mut post = [
                transition_barrier(src, D3D12_RESOURCE_STATE_COPY_SOURCE, src_state),
                transition_barrier(
                    dst,
                    D3D12_RESOURCE_STATE_COPY_DEST,
                    D3D12_RESOURCE_STATE_PRESENT,
                ),
            ];
            self.cmd_list.ResourceBarrier(&post);
            drop_barriers(&mut post);

            self.cmd_list
                .Close()
                .map_err(|e| format!("present cmd_list close: {e}"))?;

            let lists = [Some(self.cmd_list.clone().into())];
            queue.ExecuteCommandLists(&lists);
        }

        let params = DXGI_PRESENT_PARAMETERS::default();
        let hr = unsafe { self.swap_chain.Present1(0, DXGI_PRESENT(0), &params) };
        if hr.is_err() {
            if hr == DXGI_ERROR_DEVICE_REMOVED || hr == DXGI_ERROR_DEVICE_RESET {
                self.device_lost_count = self.device_lost_count.saturating_add(1);
                return Err(format!(
                    "DXGI device lost during Present1: 0x{:08X}",
                    hr.0 as u32
                ));
            }
            return Err(format!("Present1 failed: 0x{:08X}", hr.0 as u32));
        }

        self.fence_value += 1;
        let target = self.fence_value;
        unsafe {
            queue
                .Signal(&self.fence, target)
                .map_err(|e| format!("present Signal: {e}"))?;
            if self.fence.GetCompletedValue() < target {
                self.fence
                    .SetEventOnCompletion(target, self.fence_event)
                    .map_err(|e| format!("present SetEventOnCompletion: {e}"))?;
                let _ = WaitForSingleObjectEx(self.fence_event, 2000, false);
            }
        }
        Ok(())
    }
}

impl Drop for DCompOverlayPresenter {
    fn drop(&mut self) {
        unsafe {
            if !self.frame_latency_waitable.is_invalid() {
                let _ = CloseHandle(self.frame_latency_waitable);
            }
            if !self.fence_event.is_invalid() {
                let _ = CloseHandle(self.fence_event);
            }
            if !self.hwnd.0.is_null() {
                let _ = DestroyWindow(self.hwnd);
            }
        }
    }
}

fn transition_barrier(
    resource: &ID3D12Resource,
    before: D3D12_RESOURCE_STATES,
    after: D3D12_RESOURCE_STATES,
) -> D3D12_RESOURCE_BARRIER {
    D3D12_RESOURCE_BARRIER {
        Type: D3D12_RESOURCE_BARRIER_TYPE_TRANSITION,
        Flags: D3D12_RESOURCE_BARRIER_FLAG_NONE,
        Anonymous: D3D12_RESOURCE_BARRIER_0 {
            Transition: ManuallyDrop::new(D3D12_RESOURCE_TRANSITION_BARRIER {
                pResource: ManuallyDrop::new(Some(resource.clone())),
                Subresource: D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES,
                StateBefore: before,
                StateAfter: after,
            }),
        },
    }
}

fn drop_barriers(barriers: &mut [D3D12_RESOURCE_BARRIER]) {
    for b in barriers {
        unsafe {
            let t = &mut *b.Anonymous.Transition;
            ManuallyDrop::drop(&mut t.pResource);
            ManuallyDrop::drop(&mut b.Anonymous.Transition);
        }
    }
}
