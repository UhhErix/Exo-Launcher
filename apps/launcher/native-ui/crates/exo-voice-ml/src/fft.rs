use std::f32::consts::PI;

#[derive(Clone, Copy, Debug, Default)]
pub struct C32 {
    pub re: f32,
    pub im: f32,
}

impl C32 {
    #[inline]
    pub const fn new(re: f32, im: f32) -> Self {
        Self { re, im }
    }

    #[inline]
    pub fn c_add(self, o: Self) -> Self {
        Self {
            re: self.re + o.re,
            im: self.im + o.im,
        }
    }

    #[inline]
    pub fn c_sub(self, o: Self) -> Self {
        Self {
            re: self.re - o.re,
            im: self.im - o.im,
        }
    }

    #[inline]
    pub fn c_mul(self, o: Self) -> Self {
        Self {
            re: self.re * o.re - self.im * o.im,
            im: self.re * o.im + self.im * o.re,
        }
    }
}

pub struct MixedRadixFft {
    n: usize,
    pow2: usize,
    rem: usize,
    bitrev: Vec<usize>,
    tw_pow2_fwd: Vec<C32>,
    tw_pow2_inv: Vec<C32>,
    tw_rem_fwd: Vec<C32>,
    tw_rem_inv: Vec<C32>,
    tw_combine_fwd: Vec<C32>,
    tw_combine_inv: Vec<C32>,
    scratch: Vec<C32>,
    work_in: Vec<C32>,
    work_out: Vec<C32>,
}

impl MixedRadixFft {
    pub fn new(n: usize) -> Self {
        let mut pow2 = 1usize;
        let mut rem = n;
        while rem.is_multiple_of(2) {
            pow2 *= 2;
            rem /= 2;
        }
        let bits = pow2.trailing_zeros();
        let bitrev = (0..pow2)
            .map(|i| i.reverse_bits() >> (usize::BITS - bits))
            .collect();

        let mut tw_pow2_fwd = Vec::with_capacity(pow2);
        let mut tw_pow2_inv = Vec::with_capacity(pow2);
        let mut len = 2usize;
        while len <= pow2 {
            let half = len / 2;
            let step = -2.0 * PI / (len as f32);
            for k in 0..half {
                let a = step * (k as f32);
                let (s, c) = a.sin_cos();
                tw_pow2_fwd.push(C32::new(c, s));
                tw_pow2_inv.push(C32::new(c, -s));
            }
            len *= 2;
        }

        let mut tw_rem_fwd = vec![C32::default(); rem * rem];
        let mut tw_rem_inv = vec![C32::default(); rem * rem];
        for k in 0..rem {
            for j in 0..rem {
                let a = -2.0 * PI * ((k * j) as f32) / (rem as f32);
                let (s, c) = a.sin_cos();
                tw_rem_fwd[k * rem + j] = C32::new(c, s);
                tw_rem_inv[k * rem + j] = C32::new(c, -s);
            }
        }

        let mut tw_combine_fwd = vec![C32::default(); n];
        let mut tw_combine_inv = vec![C32::default(); n];
        for r in 0..rem {
            for k in 0..pow2 {
                let a = -2.0 * PI * ((r * k) as f32) / (n as f32);
                let (s, c) = a.sin_cos();
                tw_combine_fwd[r * pow2 + k] = C32::new(c, s);
                tw_combine_inv[r * pow2 + k] = C32::new(c, -s);
            }
        }

        Self {
            n,
            pow2,
            rem,
            bitrev,
            tw_pow2_fwd,
            tw_pow2_inv,
            tw_rem_fwd,
            tw_rem_inv,
            tw_combine_fwd,
            tw_combine_inv,
            scratch: vec![C32::default(); n],
            work_in: vec![C32::default(); n],
            work_out: vec![C32::default(); n],
        }
    }

    fn fft_pow2_slice(bitrev: &[usize], tw: &[C32], buf: &mut [C32]) {
        let m = buf.len();
        for (i, &j) in bitrev.iter().enumerate() {
            if i < j {
                buf.swap(i, j);
            }
        }
        let mut tw_off = 0usize;
        let mut len = 2usize;
        while len <= m {
            let half = len / 2;
            let mut start = 0usize;
            while start < m {
                for k in 0..half {
                    let u = buf[start + k];
                    let v = buf[start + k + half].c_mul(tw[tw_off + k]);
                    buf[start + k] = u.c_add(v);
                    buf[start + k + half] = u.c_sub(v);
                }
                start += len;
            }
            tw_off += half;
            len *= 2;
        }
    }

    pub fn forward(&mut self, input: &[C32], output: &mut [C32]) {
        self.transform(input, output, false);
    }

    pub fn inverse(&mut self, input: &[C32], output: &mut [C32]) {
        self.transform(input, output, true);
        let inv_n = 1.0 / (self.n as f32);
        for c in output.iter_mut() {
            c.re *= inv_n;
            c.im *= inv_n;
        }
    }

    fn transform(&mut self, input: &[C32], output: &mut [C32], inverse: bool) {
        debug_assert_eq!(input.len(), self.n);
        debug_assert_eq!(output.len(), self.n);

        let tw_p2 = if inverse {
            &self.tw_pow2_inv
        } else {
            &self.tw_pow2_fwd
        };

        if self.rem == 1 {
            output.copy_from_slice(input);
            Self::fft_pow2_slice(&self.bitrev, tw_p2, output);
            return;
        }

        let m = self.pow2;
        let r = self.rem;
        for q in 0..r {
            let row = &mut self.scratch[q * m..(q + 1) * m];
            for j in 0..m {
                row[j] = input[j * r + q];
            }
            Self::fft_pow2_slice(&self.bitrev, tw_p2, row);
        }

        let tw_c = if inverse {
            &self.tw_combine_inv
        } else {
            &self.tw_combine_fwd
        };
        let tw_r = if inverse {
            &self.tw_rem_inv
        } else {
            &self.tw_rem_fwd
        };

        for q in 0..r {
            for k in 0..m {
                self.scratch[q * m + k] = self.scratch[q * m + k].c_mul(tw_c[q * m + k]);
            }
        }

        for p in 0..r {
            for k in 0..m {
                let mut acc = C32::default();
                for q in 0..r {
                    acc = acc.c_add(self.scratch[q * m + k].c_mul(tw_r[p * r + q]));
                }
                output[p * m + k] = acc;
            }
        }
    }

    pub fn rfft(&mut self, real: &[f32], out_bins: &mut [C32]) {
        debug_assert_eq!(real.len(), self.n);
        debug_assert_eq!(out_bins.len(), self.n / 2 + 1);
        for (dst, &v) in self.work_in.iter_mut().zip(real.iter()) {
            *dst = C32::new(v, 0.0);
        }
        let inp = std::mem::take(&mut self.work_in);
        let mut full = std::mem::take(&mut self.work_out);
        self.forward(&inp, &mut full);
        out_bins.copy_from_slice(&full[..self.n / 2 + 1]);
        self.work_in = inp;
        self.work_out = full;
    }

    pub fn irfft(&mut self, bins: &[C32], out_real: &mut [f32]) {
        debug_assert_eq!(bins.len(), self.n / 2 + 1);
        debug_assert_eq!(out_real.len(), self.n);
        let half = self.n / 2;
        self.work_in[..=half].copy_from_slice(bins);
        for k in 1..half {
            let b = bins[k];
            self.work_in[self.n - k] = C32::new(b.re, -b.im);
        }
        let full = std::mem::take(&mut self.work_in);
        let mut time = std::mem::take(&mut self.work_out);
        self.inverse(&full, &mut time);
        for (dst, c) in out_real.iter_mut().zip(time.iter()) {
            *dst = c.re;
        }
        self.work_in = full;
        self.work_out = time;
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn roundtrip_320_512_960() {
        for n in [320usize, 512, 960] {
            let mut fft = MixedRadixFft::new(n);
            let input: Vec<f32> = (0..n)
                .map(|i| ((i as f32) * 0.13).sin() + 0.4 * ((i as f32) * 0.47).cos())
                .collect();
            let mut bins = vec![C32::default(); n / 2 + 1];
            let mut recon = vec![0.0f32; n];
            fft.rfft(&input, &mut bins);
            fft.irfft(&bins, &mut recon);
            let max_err = input
                .iter()
                .zip(recon.iter())
                .map(|(a, b)| (a - b).abs())
                .fold(0.0f32, f32::max);
            assert!(max_err < 1e-4, "n={n} max_err={max_err}");
        }
    }
}
