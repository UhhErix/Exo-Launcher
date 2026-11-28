use crate::InstallError;

#[derive(Debug, Clone, Copy)]
pub struct D2dBenchResult {
    pub hwnd_create_ms: f64,
    pub first_paint_ms: f64,
    pub total_ms: f64,
}

#[cfg(windows)]
pub fn bench_d2d_first_paint() -> Result<D2dBenchResult, InstallError> {
    use windows::Win32::Foundation::{HWND, LPARAM, LRESULT, WPARAM};
    use windows::Win32::Graphics::Direct2D::Common::{D2D_RECT_F, D2D_SIZE_U, D2D1_COLOR_F};
    use windows::Win32::Graphics::Direct2D::{
        D2D1_FACTORY_TYPE_SINGLE_THREADED, D2D1_HWND_RENDER_TARGET_PROPERTIES,
        D2D1_RENDER_TARGET_PROPERTIES, D2D1_RENDER_TARGET_TYPE_SOFTWARE, D2D1_ROUNDED_RECT,
        D2D1CreateFactory, ID2D1Factory, ID2D1HwndRenderTarget,
    };
    use windows::Win32::Graphics::DirectWrite::{
        DWRITE_FACTORY_TYPE_SHARED, DWRITE_FONT_STRETCH_NORMAL, DWRITE_FONT_STYLE_NORMAL,
        DWRITE_FONT_WEIGHT_SEMI_BOLD, DWriteCreateFactory, IDWriteFactory,
    };
    use windows::Win32::System::LibraryLoader::GetModuleHandleW;
    use windows::Win32::System::Performance::{QueryPerformanceCounter, QueryPerformanceFrequency};
    use windows::Win32::UI::WindowsAndMessaging::{
        CreateWindowExW, DefWindowProcW, DestroyWindow, RegisterClassW, UnregisterClassW,
        WNDCLASSW, WS_OVERLAPPEDWINDOW,
    };
    use windows::core::w;

    unsafe extern "system" fn wnd_proc(
        hwnd: HWND,
        msg: u32,
        wparam: WPARAM,
        lparam: LPARAM,
    ) -> LRESULT {
        unsafe { DefWindowProcW(hwnd, msg, wparam, lparam) }
    }

    let mut freq = 0i64;
    let mut t0 = 0i64;
    unsafe {
        let _ = QueryPerformanceFrequency(&mut freq);
        let _ = QueryPerformanceCounter(&mut t0);
    }

    let hinst = unsafe { GetModuleHandleW(None) }
        .map_err(|e| InstallError::Win32(format!("GetModuleHandleW: {e}")))?;
    let class_name = w!("ExoInstallerD2DBenchClass");
    let wc = WNDCLASSW {
        lpfnWndProc: Some(wnd_proc),
        hInstance: hinst.into(),
        lpszClassName: class_name,
        ..Default::default()
    };
    unsafe {
        RegisterClassW(&wc);
    }

    let hwnd = unsafe {
        CreateWindowExW(
            Default::default(),
            class_name,
            w!("Exo Launcher Setup"),
            WS_OVERLAPPEDWINDOW,
            -32000,
            -32000,
            520,
            340,
            None,
            None,
            Some(hinst.into()),
            None,
        )
    }
    .map_err(|e| InstallError::Win32(format!("CreateWindowExW: {e}")))?;

    let mut t_hwnd = 0i64;
    unsafe {
        let _ = QueryPerformanceCounter(&mut t_hwnd);
    }

    let paint_res = (|| -> Result<(), InstallError> {
        let factory: ID2D1Factory =
            unsafe { D2D1CreateFactory(D2D1_FACTORY_TYPE_SINGLE_THREADED, None) }
                .map_err(|e| InstallError::Win32(format!("D2D1CreateFactory: {e}")))?;

        let rt_props = D2D1_RENDER_TARGET_PROPERTIES {
            r#type: D2D1_RENDER_TARGET_TYPE_SOFTWARE,
            ..Default::default()
        };
        let hwnd_props = D2D1_HWND_RENDER_TARGET_PROPERTIES {
            hwnd,
            pixelSize: D2D_SIZE_U {
                width: 520,
                height: 340,
            },
            ..Default::default()
        };
        let rt: ID2D1HwndRenderTarget =
            unsafe { factory.CreateHwndRenderTarget(&rt_props, &hwnd_props) }
                .map_err(|e| InstallError::Win32(format!("CreateHwndRenderTarget: {e}")))?;

        let dwrite: IDWriteFactory = unsafe { DWriteCreateFactory(DWRITE_FACTORY_TYPE_SHARED) }
            .map_err(|e| InstallError::Win32(format!("DWriteCreateFactory: {e}")))?;
        let text_fmt = unsafe {
            dwrite.CreateTextFormat(
                w!("Geist"),
                None,
                DWRITE_FONT_WEIGHT_SEMI_BOLD,
                DWRITE_FONT_STYLE_NORMAL,
                DWRITE_FONT_STRETCH_NORMAL,
                22.0,
                w!("en-US"),
            )
        }
        .map_err(|e| InstallError::Win32(format!("CreateTextFormat: {e}")))?;

        let white = unsafe {
            rt.CreateSolidColorBrush(
                &D2D1_COLOR_F {
                    r: 1.0,
                    g: 1.0,
                    b: 1.0,
                    a: 1.0,
                },
                None,
            )
        }
        .map_err(|e| InstallError::Win32(format!("CreateSolidColorBrush white: {e}")))?;

        let track = unsafe {
            rt.CreateSolidColorBrush(
                &D2D1_COLOR_F {
                    r: 0.14,
                    g: 0.14,
                    b: 0.15,
                    a: 1.0,
                },
                None,
            )
        }
        .map_err(|e| InstallError::Win32(format!("CreateSolidColorBrush track: {e}")))?;

        let title: Vec<u16> = "Installing Exo Launcher".encode_utf16().collect();
        let title_rect = D2D_RECT_F {
            left: 36.0,
            top: 44.0,
            right: 484.0,
            bottom: 92.0,
        };
        let bar_track = D2D1_ROUNDED_RECT {
            rect: D2D_RECT_F {
                left: 36.0,
                top: 150.0,
                right: 484.0,
                bottom: 158.0,
            },
            radiusX: 4.0,
            radiusY: 4.0,
        };
        let bar_fill = D2D1_ROUNDED_RECT {
            rect: D2D_RECT_F {
                left: 36.0,
                top: 150.0,
                right: 320.0,
                bottom: 158.0,
            },
            radiusX: 4.0,
            radiusY: 4.0,
        };

        unsafe {
            rt.BeginDraw();
            rt.Clear(Some(&D2D1_COLOR_F {
                r: 0.0,
                g: 0.0,
                b: 0.0,
                a: 1.0,
            }));
            rt.DrawText(
                &title,
                &text_fmt,
                &title_rect,
                &white,
                Default::default(),
                Default::default(),
            );
            rt.FillRoundedRectangle(&bar_track, &track);
            rt.FillRoundedRectangle(&bar_fill, &white);
            rt.EndDraw(None, None)
                .map_err(|e| InstallError::Win32(format!("EndDraw: {e}")))?;
        }
        Ok(())
    })();

    let mut t_paint = 0i64;
    unsafe {
        let _ = QueryPerformanceCounter(&mut t_paint);
        let _ = DestroyWindow(hwnd);
        let _ = UnregisterClassW(class_name, Some(hinst.into()));
    }
    paint_res?;

    let to_ms = |a: i64, b: i64| ((b - a) as f64) * 1000.0 / (freq.max(1) as f64);
    Ok(D2dBenchResult {
        hwnd_create_ms: to_ms(t0, t_hwnd),
        first_paint_ms: to_ms(t_hwnd, t_paint),
        total_ms: to_ms(t0, t_paint),
    })
}

#[cfg(not(windows))]
pub fn bench_d2d_first_paint() -> Result<D2dBenchResult, InstallError> {
    Ok(D2dBenchResult {
        hwnd_create_ms: 0.0,
        first_paint_ms: 0.0,
        total_ms: 0.0,
    })
}
