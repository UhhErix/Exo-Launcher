use std::sync::Arc;

use windows::Win32::Foundation::RECT;
use windows::Win32::Graphics::Direct3D11::{
    D3D11_BIND_RENDER_TARGET, D3D11_BIND_SHADER_RESOURCE, D3D11_TEX2D_VPIV, D3D11_TEX2D_VPOV,
    D3D11_TEXTURE2D_DESC, D3D11_USAGE_DEFAULT, D3D11_VIDEO_FRAME_FORMAT_PROGRESSIVE,
    D3D11_VIDEO_PROCESSOR_COLOR_SPACE, D3D11_VIDEO_PROCESSOR_CONTENT_DESC,
    D3D11_VIDEO_PROCESSOR_INPUT_VIEW_DESC, D3D11_VIDEO_PROCESSOR_INPUT_VIEW_DESC_0,
    D3D11_VIDEO_PROCESSOR_OUTPUT_VIEW_DESC, D3D11_VIDEO_PROCESSOR_OUTPUT_VIEW_DESC_0,
    D3D11_VIDEO_PROCESSOR_STREAM, D3D11_VIDEO_USAGE_PLAYBACK_NORMAL,
    D3D11_VPIV_DIMENSION_TEXTURE2D, D3D11_VPOV_DIMENSION_TEXTURE2D, ID3D11Texture2D,
    ID3D11VideoContext1, ID3D11VideoProcessor, ID3D11VideoProcessorEnumerator,
    ID3D11VideoProcessorOutputView,
};
use windows::Win32::Graphics::Dxgi::Common::{
    DXGI_COLOR_SPACE_RGB_FULL_G10_NONE_P709, DXGI_COLOR_SPACE_RGB_FULL_G22_NONE_P709,
    DXGI_COLOR_SPACE_YCBCR_STUDIO_G22_LEFT_P709, DXGI_COLOR_SPACE_YCBCR_STUDIO_G2084_LEFT_P2020,
    DXGI_FORMAT, DXGI_FORMAT_NV12, DXGI_FORMAT_P010, DXGI_FORMAT_R16G16B16A16_FLOAT, DXGI_RATIONAL,
    DXGI_SAMPLE_DESC,
};
use windows::core::Interface;

use crate::gpu::GpuDevice;

pub fn needs_recreate_converter(
    cur_in_w: u32,
    cur_in_h: u32,
    cur_in_fmt: DXGI_FORMAT,
    new_in_w: u32,
    new_in_h: u32,
    new_in_fmt: DXGI_FORMAT,
) -> bool {
    cur_in_w != new_in_w || cur_in_h != new_in_h || cur_in_fmt != new_in_fmt
}

pub fn needs_hdr_to_sdr_tonemap(input_format: DXGI_FORMAT, output_hdr10: bool) -> bool {
    input_format == DXGI_FORMAT_R16G16B16A16_FLOAT && !output_hdr10
}

pub fn output_surface_format(output_hdr10: bool) -> DXGI_FORMAT {
    if output_hdr10 {
        DXGI_FORMAT_P010
    } else {
        DXGI_FORMAT_NV12
    }
}

pub struct GpuColorConverter {
    gpu: Arc<GpuDevice>,
    enumerator: ID3D11VideoProcessorEnumerator,
    processor: ID3D11VideoProcessor,
    output_tex: ID3D11Texture2D,
    output_view: ID3D11VideoProcessorOutputView,
    in_w: u32,
    in_h: u32,
    in_fmt: DXGI_FORMAT,
    out_w: u32,
    out_h: u32,
    out_hdr10: bool,
}

impl GpuColorConverter {
    pub fn new(
        gpu: Arc<GpuDevice>,
        in_w: u32,
        in_h: u32,
        in_fmt: DXGI_FORMAT,
        out_w: u32,
        out_h: u32,
        out_hdr10: bool,
    ) -> Result<Self, String> {
        let even_out_w = (out_w.max(64)) & !1;
        let even_out_h = (out_h.max(64)) & !1;
        let content_desc = D3D11_VIDEO_PROCESSOR_CONTENT_DESC {
            InputFrameFormat: D3D11_VIDEO_FRAME_FORMAT_PROGRESSIVE,
            InputFrameRate: DXGI_RATIONAL {
                Numerator: 60,
                Denominator: 1,
            },
            InputWidth: in_w.max(2),
            InputHeight: in_h.max(2),
            OutputFrameRate: DXGI_RATIONAL {
                Numerator: 60,
                Denominator: 1,
            },
            OutputWidth: even_out_w,
            OutputHeight: even_out_h,
            Usage: D3D11_VIDEO_USAGE_PLAYBACK_NORMAL,
        };

        let enumerator = unsafe {
            gpu.video_device
                .CreateVideoProcessorEnumerator(&content_desc)
        }
        .map_err(|e| format!("CreateVideoProcessorEnumerator failed: {e}"))?;
        let processor = unsafe { gpu.video_device.CreateVideoProcessor(&enumerator, 0) }
            .map_err(|e| format!("CreateVideoProcessor failed: {e}"))?;

        let out_fmt = output_surface_format(out_hdr10);
        let tex_desc = D3D11_TEXTURE2D_DESC {
            Width: even_out_w,
            Height: even_out_h,
            MipLevels: 1,
            ArraySize: 1,
            Format: out_fmt,
            SampleDesc: DXGI_SAMPLE_DESC {
                Count: 1,
                Quality: 0,
            },
            Usage: D3D11_USAGE_DEFAULT,
            BindFlags: (D3D11_BIND_RENDER_TARGET.0 | D3D11_BIND_SHADER_RESOURCE.0) as u32,
            CPUAccessFlags: 0,
            MiscFlags: 0,
        };

        let mut output_tex: Option<ID3D11Texture2D> = None;
        unsafe {
            gpu.device
                .CreateTexture2D(&tex_desc, None, Some(&mut output_tex))
        }
        .map_err(|e| format!("CreateTexture2D NV12/P010 failed: {e}"))?;
        let output_tex = output_tex.ok_or("CreateTexture2D returned null")?;

        let vpov_desc = D3D11_VIDEO_PROCESSOR_OUTPUT_VIEW_DESC {
            ViewDimension: D3D11_VPOV_DIMENSION_TEXTURE2D,
            Anonymous: D3D11_VIDEO_PROCESSOR_OUTPUT_VIEW_DESC_0 {
                Texture2D: D3D11_TEX2D_VPOV { MipSlice: 0 },
            },
        };
        let mut output_view: Option<ID3D11VideoProcessorOutputView> = None;
        unsafe {
            gpu.video_device.CreateVideoProcessorOutputView(
                &output_tex,
                &enumerator,
                &vpov_desc,
                Some(&mut output_view),
            )
        }
        .map_err(|e| format!("CreateVideoProcessorOutputView failed: {e}"))?;
        let output_view = output_view.ok_or("CreateVideoProcessorOutputView returned null")?;

        if let Ok(vc1) = gpu.video_context.cast::<ID3D11VideoContext1>() {
            let in_cs = if in_fmt == DXGI_FORMAT_R16G16B16A16_FLOAT {
                DXGI_COLOR_SPACE_RGB_FULL_G10_NONE_P709
            } else {
                DXGI_COLOR_SPACE_RGB_FULL_G22_NONE_P709
            };
            let out_cs = if out_hdr10 {
                DXGI_COLOR_SPACE_YCBCR_STUDIO_G2084_LEFT_P2020
            } else {
                DXGI_COLOR_SPACE_YCBCR_STUDIO_G22_LEFT_P709
            };
            unsafe {
                vc1.VideoProcessorSetStreamColorSpace1(&processor, 0, in_cs);
                vc1.VideoProcessorSetOutputColorSpace1(&processor, out_cs);
            }
        } else {
            let cs = D3D11_VIDEO_PROCESSOR_COLOR_SPACE { _bitfield: 0x05 };
            unsafe {
                gpu.video_context
                    .VideoProcessorSetStreamColorSpace(&processor, 0, &cs);
                gpu.video_context
                    .VideoProcessorSetOutputColorSpace(&processor, &cs);
            }
        }

        let src_rect = RECT {
            left: 0,
            top: 0,
            right: in_w as i32,
            bottom: in_h as i32,
        };
        let dst_rect = RECT {
            left: 0,
            top: 0,
            right: even_out_w as i32,
            bottom: even_out_h as i32,
        };
        unsafe {
            gpu.video_context.VideoProcessorSetStreamSourceRect(
                &processor,
                0,
                true,
                Some(&src_rect),
            );
            gpu.video_context
                .VideoProcessorSetStreamDestRect(&processor, 0, true, Some(&dst_rect));
            gpu.video_context
                .VideoProcessorSetOutputTargetRect(&processor, true, Some(&dst_rect));
        }

        Ok(Self {
            gpu,
            enumerator,
            processor,
            output_tex,
            output_view,
            in_w,
            in_h,
            in_fmt,
            out_w: even_out_w,
            out_h: even_out_h,
            out_hdr10,
        })
    }

    pub fn convert(&mut self, src: &ID3D11Texture2D) -> Result<ID3D11Texture2D, String> {
        let mut desc = Default::default();
        unsafe { src.GetDesc(&mut desc) };
        if needs_recreate_converter(
            self.in_w,
            self.in_h,
            self.in_fmt,
            desc.Width,
            desc.Height,
            desc.Format,
        ) {
            *self = Self::new(
                Arc::clone(&self.gpu),
                desc.Width,
                desc.Height,
                desc.Format,
                self.out_w,
                self.out_h,
                self.out_hdr10,
            )?;
        }

        let vpiv_desc = D3D11_VIDEO_PROCESSOR_INPUT_VIEW_DESC {
            FourCC: 0,
            ViewDimension: D3D11_VPIV_DIMENSION_TEXTURE2D,
            Anonymous: D3D11_VIDEO_PROCESSOR_INPUT_VIEW_DESC_0 {
                Texture2D: D3D11_TEX2D_VPIV {
                    MipSlice: 0,
                    ArraySlice: 0,
                },
            },
        };
        let mut input_view = None;
        unsafe {
            self.gpu.video_device.CreateVideoProcessorInputView(
                src,
                &self.enumerator,
                &vpiv_desc,
                Some(&mut input_view),
            )
        }
        .map_err(|e| format!("CreateVideoProcessorInputView failed: {e}"))?;
        let input_view = input_view.ok_or("CreateVideoProcessorInputView returned null")?;

        let mut streams = [D3D11_VIDEO_PROCESSOR_STREAM {
            Enable: true.into(),
            pInputSurface: std::mem::ManuallyDrop::new(Some(input_view)),
            ..Default::default()
        }];

        let blt_res = unsafe {
            self.gpu.video_context.VideoProcessorBlt(
                &self.processor,
                &self.output_view,
                0,
                &streams,
            )
        };
        unsafe {
            std::mem::ManuallyDrop::drop(&mut streams[0].pInputSurface);
        }
        blt_res.map_err(|e| format!("VideoProcessorBlt failed: {e}"))?;

        Ok(self.output_tex.clone())
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use windows::Win32::Graphics::Dxgi::Common::DXGI_FORMAT_B8G8R8A8_UNORM;

    #[test]
    fn converter_recreation_and_hdr_tonemap_rules() {
        assert!(!needs_recreate_converter(
            1920,
            1080,
            DXGI_FORMAT_B8G8R8A8_UNORM,
            1920,
            1080,
            DXGI_FORMAT_B8G8R8A8_UNORM
        ));
        assert!(needs_recreate_converter(
            1920,
            1080,
            DXGI_FORMAT_B8G8R8A8_UNORM,
            2560,
            1440,
            DXGI_FORMAT_B8G8R8A8_UNORM
        ));
        assert!(needs_recreate_converter(
            1920,
            1080,
            DXGI_FORMAT_B8G8R8A8_UNORM,
            1920,
            1080,
            DXGI_FORMAT_R16G16B16A16_FLOAT
        ));
        assert!(needs_hdr_to_sdr_tonemap(
            DXGI_FORMAT_R16G16B16A16_FLOAT,
            false
        ));
        assert!(!needs_hdr_to_sdr_tonemap(
            DXGI_FORMAT_R16G16B16A16_FLOAT,
            true
        ));
        assert!(!needs_hdr_to_sdr_tonemap(DXGI_FORMAT_B8G8R8A8_UNORM, false));
        assert_eq!(output_surface_format(false), DXGI_FORMAT_NV12);
        assert_eq!(output_surface_format(true), DXGI_FORMAT_P010);
    }
}
