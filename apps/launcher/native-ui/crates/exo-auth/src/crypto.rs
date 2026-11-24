#[cfg(windows)]
use std::ffi::c_void;
#[cfg(windows)]
use std::ptr;
#[cfg(windows)]
use windows::Win32::Foundation::{HLOCAL, LocalFree};
#[cfg(windows)]
use windows::Win32::Security::Cryptography::{
    BCRYPT_ALG_HANDLE, BCRYPT_HASH_HANDLE, BCRYPT_KEY_HANDLE, BCRYPT_OAEP_PADDING_INFO,
    BCRYPT_OPEN_ALGORITHM_PROVIDER_FLAGS, BCRYPT_PAD_OAEP, BCRYPT_PAD_PKCS1, BCRYPT_RSA_ALGORITHM,
    BCRYPT_RSAKEY_BLOB, BCRYPT_RSAPUBLIC_BLOB, BCRYPT_RSAPUBLIC_MAGIC, BCRYPT_SHA256_ALGORITHM,
    BCRYPT_USE_SYSTEM_PREFERRED_RNG, BCryptCloseAlgorithmProvider, BCryptCreateHash, BCryptDecrypt,
    BCryptDestroyHash, BCryptDestroyKey, BCryptEncrypt, BCryptExportKey, BCryptFinalizeKeyPair,
    BCryptFinishHash, BCryptGenRandom, BCryptGenerateKeyPair, BCryptHashData, BCryptImportKeyPair,
    BCryptOpenAlgorithmProvider, CRYPT_INTEGER_BLOB, CRYPTPROTECT_UI_FORBIDDEN, CryptProtectData,
    CryptUnprotectData,
};

const B64_STD: &[u8; 64] = b"ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
const B64_URL: &[u8; 64] = b"ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";

pub fn base64_encode(input: &[u8]) -> String {
    encode_with_table(input, B64_STD, true)
}

pub fn base64url_encode(input: &[u8]) -> String {
    encode_with_table(input, B64_URL, false)
}

fn encode_with_table(input: &[u8], table: &[u8; 64], pad: bool) -> String {
    let mut out = String::with_capacity(input.len().div_ceil(3) * 4);
    for chunk in input.chunks(3) {
        let b0 = chunk[0] as u32;
        let b1 = if chunk.len() > 1 { chunk[1] as u32 } else { 0 };
        let b2 = if chunk.len() > 2 { chunk[2] as u32 } else { 0 };
        let n = (b0 << 16) | (b1 << 8) | b2;
        out.push(table[((n >> 18) & 0x3f) as usize] as char);
        out.push(table[((n >> 12) & 0x3f) as usize] as char);
        if chunk.len() > 1 {
            out.push(table[((n >> 6) & 0x3f) as usize] as char);
        } else if pad {
            out.push('=');
        }
        if chunk.len() > 2 {
            out.push(table[(n & 0x3f) as usize] as char);
        } else if pad {
            out.push('=');
        }
    }
    out
}

pub fn base64_decode(input: &str) -> Result<Vec<u8>, String> {
    let mut out = Vec::with_capacity((input.len() * 3) / 4);
    let mut buf = 0u32;
    let mut bits = 0u32;
    for b in input.bytes() {
        if b.is_ascii_whitespace() || b == b'=' {
            continue;
        }
        let val = match b {
            b'A'..=b'Z' => (b - b'A') as u32,
            b'a'..=b'z' => (b - b'a' + 26) as u32,
            b'0'..=b'9' => (b - b'0' + 52) as u32,
            b'+' | b'-' => 62,
            b'/' | b'_' => 63,
            _ => return Err("invalid base64 byte".to_string()),
        };
        buf = (buf << 6) | val;
        bits += 6;
        if bits >= 8 {
            bits -= 8;
            out.push(((buf >> bits) & 0xff) as u8);
        }
    }
    Ok(out)
}

pub fn hex_encode(bytes: &[u8]) -> String {
    const HEX: &[u8; 16] = b"0123456789abcdef";
    let mut out = String::with_capacity(bytes.len() * 2);
    for &b in bytes {
        out.push(HEX[(b >> 4) as usize] as char);
        out.push(HEX[(b & 0x0f) as usize] as char);
    }
    out
}

pub fn hex_decode(hex: &str) -> Result<Vec<u8>, String> {
    let clean = hex.trim();
    if !clean.len().is_multiple_of(2) {
        return Err("odd hex length".to_string());
    }
    let bytes = clean.as_bytes();
    let mut out = Vec::with_capacity(bytes.len() / 2);
    for pair in bytes.as_chunks::<2>().0 {
        let hi = hex_nibble(pair[0])?;
        let lo = hex_nibble(pair[1])?;
        out.push((hi << 4) | lo);
    }
    Ok(out)
}

fn hex_nibble(b: u8) -> Result<u8, String> {
    match b {
        b'0'..=b'9' => Ok(b - b'0'),
        b'a'..=b'f' => Ok(b - b'a' + 10),
        b'A'..=b'F' => Ok(b - b'A' + 10),
        _ => Err("invalid hex char".to_string()),
    }
}

pub fn constant_time_eq(a: &str, b: &str) -> bool {
    let ab = a.as_bytes();
    let bb = b.as_bytes();
    if ab.len() != bb.len() {
        return false;
    }
    let mut diff = 0u8;
    for (&x, &y) in ab.iter().zip(bb.iter()) {
        diff |= x ^ y;
    }
    diff == 0
}

#[cfg(windows)]
pub fn csprng_bytes(len: usize) -> Result<Vec<u8>, String> {
    let mut buf = vec![0u8; len];
    let status = unsafe { BCryptGenRandom(None, &mut buf, BCRYPT_USE_SYSTEM_PREFERRED_RNG) };
    if status.is_err() {
        return Err(format!("BCryptGenRandom failed: 0x{:08x}", status.0));
    }
    Ok(buf)
}

#[cfg(windows)]
pub fn sha256(data: &[u8]) -> Result<[u8; 32], String> {
    unsafe {
        let mut alg = BCRYPT_ALG_HANDLE::default();
        let st = BCryptOpenAlgorithmProvider(
            &mut alg,
            BCRYPT_SHA256_ALGORITHM,
            None,
            BCRYPT_OPEN_ALGORITHM_PROVIDER_FLAGS(0),
        );
        if st.is_err() {
            return Err(format!(
                "BCryptOpenAlgorithmProvider(SHA256) failed: 0x{:08x}",
                st.0
            ));
        }
        let mut hash = BCRYPT_HASH_HANDLE::default();
        let st_create = BCryptCreateHash(alg, &mut hash, None, None, 0);
        if st_create.is_err() {
            let _ = BCryptCloseAlgorithmProvider(alg, 0);
            return Err(format!("BCryptCreateHash failed: 0x{:08x}", st_create.0));
        }
        let st_data = BCryptHashData(hash, data, 0);
        let mut digest = [0u8; 32];
        let st_fin = if st_data.is_ok() {
            BCryptFinishHash(hash, &mut digest, 0)
        } else {
            st_data
        };
        let _ = BCryptDestroyHash(hash);
        let _ = BCryptCloseAlgorithmProvider(alg, 0);
        if st_fin.is_err() {
            return Err(format!("BCrypt SHA256 failed: 0x{:08x}", st_fin.0));
        }
        Ok(digest)
    }
}

#[cfg(windows)]
pub fn pkce_pair() -> Result<(String, String), String> {
    let raw = csprng_bytes(32)?;
    let verifier = base64url_encode(&raw);
    let challenge = base64url_encode(&sha256(verifier.as_bytes())?);
    Ok((verifier, challenge))
}

#[cfg(windows)]
pub struct Rsa2048Oaep {
    alg: BCRYPT_ALG_HANDLE,
    key: BCRYPT_KEY_HANDLE,
    spki_der: Vec<u8>,
}

#[cfg(windows)]
unsafe impl Send for Rsa2048Oaep {}

#[cfg(windows)]
impl Rsa2048Oaep {
    pub fn generate() -> Result<Self, String> {
        unsafe {
            let mut alg = BCRYPT_ALG_HANDLE::default();
            let st = BCryptOpenAlgorithmProvider(
                &mut alg,
                BCRYPT_RSA_ALGORITHM,
                None,
                BCRYPT_OPEN_ALGORITHM_PROVIDER_FLAGS(0),
            );
            if st.is_err() {
                return Err(format!(
                    "BCryptOpenAlgorithmProvider(RSA) failed: 0x{:08x}",
                    st.0
                ));
            }
            let mut key = BCRYPT_KEY_HANDLE::default();
            let st_gen = BCryptGenerateKeyPair(alg, &mut key, 2048, 0);
            if st_gen.is_err() {
                let _ = BCryptCloseAlgorithmProvider(alg, 0);
                return Err(format!("BCryptGenerateKeyPair failed: 0x{:08x}", st_gen.0));
            }
            let st_fin = BCryptFinalizeKeyPair(key, 0);
            if st_fin.is_err() {
                let _ = BCryptDestroyKey(key);
                let _ = BCryptCloseAlgorithmProvider(alg, 0);
                return Err(format!("BCryptFinalizeKeyPair failed: 0x{:08x}", st_fin.0));
            }
            let mut blob_len = 0u32;
            let st_sz = BCryptExportKey(key, None, BCRYPT_RSAPUBLIC_BLOB, None, &mut blob_len, 0);
            if st_sz.is_err() || blob_len == 0 {
                let _ = BCryptDestroyKey(key);
                let _ = BCryptCloseAlgorithmProvider(alg, 0);
                return Err(format!("BCryptExportKey size failed: 0x{:08x}", st_sz.0));
            }
            let mut blob = vec![0u8; blob_len as usize];
            let st_exp = BCryptExportKey(
                key,
                None,
                BCRYPT_RSAPUBLIC_BLOB,
                Some(&mut blob),
                &mut blob_len,
                0,
            );
            if st_exp.is_err() {
                let _ = BCryptDestroyKey(key);
                let _ = BCryptCloseAlgorithmProvider(alg, 0);
                return Err(format!("BCryptExportKey failed: 0x{:08x}", st_exp.0));
            }
            let header_size = std::mem::size_of::<BCRYPT_RSAKEY_BLOB>();
            if blob.len() < header_size {
                let _ = BCryptDestroyKey(key);
                let _ = BCryptCloseAlgorithmProvider(alg, 0);
                return Err("exported RSA blob truncated".to_string());
            }
            let hdr = ptr::read_unaligned(blob.as_ptr() as *const BCRYPT_RSAKEY_BLOB);
            let exp_start = header_size;
            let exp_end = exp_start + hdr.cbPublicExp as usize;
            let mod_end = exp_end + hdr.cbModulus as usize;
            if mod_end > blob.len() {
                let _ = BCryptDestroyKey(key);
                let _ = BCryptCloseAlgorithmProvider(alg, 0);
                return Err("exported RSA blob bounds invalid".to_string());
            }
            let exponent = &blob[exp_start..exp_end];
            let modulus = &blob[exp_end..mod_end];
            let spki_der = encode_rsa_spki_der(modulus, exponent);
            Ok(Self { alg, key, spki_der })
        }
    }

    pub fn spki_base64(&self) -> String {
        base64_encode(&self.spki_der)
    }

    pub fn decrypt_oaep_sha256(&self, ciphertext: &[u8]) -> Result<Vec<u8>, String> {
        unsafe {
            let pad = BCRYPT_OAEP_PADDING_INFO {
                pszAlgId: BCRYPT_SHA256_ALGORITHM,
                pbLabel: ptr::null_mut(),
                cbLabel: 0,
            };
            let mut out_len = 0u32;
            let st_sz = BCryptDecrypt(
                self.key,
                Some(ciphertext),
                Some(&pad as *const BCRYPT_OAEP_PADDING_INFO as *const c_void),
                None,
                None,
                &mut out_len,
                BCRYPT_PAD_OAEP,
            );
            if st_sz.is_err() {
                return Err(format!("BCryptDecrypt size failed: 0x{:08x}", st_sz.0));
            }
            let mut plain = vec![0u8; out_len as usize];
            let st_dec = BCryptDecrypt(
                self.key,
                Some(ciphertext),
                Some(&pad as *const BCRYPT_OAEP_PADDING_INFO as *const c_void),
                None,
                Some(&mut plain),
                &mut out_len,
                BCRYPT_PAD_OAEP,
            );
            if st_dec.is_err() {
                return Err(format!("BCryptDecrypt failed: 0x{:08x}", st_dec.0));
            }
            plain.truncate(out_len as usize);
            Ok(plain)
        }
    }
}

#[cfg(windows)]
impl Drop for Rsa2048Oaep {
    fn drop(&mut self) {
        unsafe {
            let _ = BCryptDestroyKey(self.key);
            let _ = BCryptCloseAlgorithmProvider(self.alg, 0);
        }
    }
}

#[cfg(windows)]
fn der_len(out: &mut Vec<u8>, len: usize) {
    if len < 0x80 {
        out.push(len as u8);
    } else if len <= 0xff {
        out.push(0x81);
        out.push(len as u8);
    } else {
        out.push(0x82);
        out.push(((len >> 8) & 0xff) as u8);
        out.push((len & 0xff) as u8);
    }
}

#[cfg(windows)]
fn der_tlv(tag: u8, content: &[u8]) -> Vec<u8> {
    let mut out = Vec::with_capacity(content.len() + 4);
    out.push(tag);
    der_len(&mut out, content.len());
    out.extend_from_slice(content);
    out
}

#[cfg(windows)]
fn der_integer_unsigned(bytes: &[u8]) -> Vec<u8> {
    let trimmed = match bytes.iter().position(|&b| b != 0) {
        Some(idx) => &bytes[idx..],
        None => &[0u8],
    };
    let mut content = Vec::with_capacity(trimmed.len() + 1);
    if trimmed[0] & 0x80 != 0 {
        content.push(0x00);
    }
    content.extend_from_slice(trimmed);
    der_tlv(0x02, &content)
}

#[cfg(windows)]
fn encode_rsa_spki_der(modulus: &[u8], exponent: &[u8]) -> Vec<u8> {
    let mut rsa_pub_inner = Vec::with_capacity(modulus.len() + exponent.len() + 16);
    rsa_pub_inner.extend_from_slice(&der_integer_unsigned(modulus));
    rsa_pub_inner.extend_from_slice(&der_integer_unsigned(exponent));
    let rsa_pub_seq = der_tlv(0x30, &rsa_pub_inner);

    let mut bit_string_content = Vec::with_capacity(rsa_pub_seq.len() + 1);
    bit_string_content.push(0x00);
    bit_string_content.extend_from_slice(&rsa_pub_seq);
    let bit_string = der_tlv(0x03, &bit_string_content);

    let alg_id: &[u8] = &[
        0x30, 0x0d, 0x06, 0x09, 0x2a, 0x86, 0x48, 0x86, 0xf7, 0x0d, 0x01, 0x01, 0x01, 0x05, 0x00,
    ];
    let mut spki_inner = Vec::with_capacity(alg_id.len() + bit_string.len());
    spki_inner.extend_from_slice(alg_id);
    spki_inner.extend_from_slice(&bit_string);
    der_tlv(0x30, &spki_inner)
}

#[cfg(windows)]
pub fn rsa_encrypt_pkcs1(
    modulus_hex: &str,
    exponent_hex: &str,
    plaintext: &[u8],
) -> Result<String, String> {
    let modulus = hex_decode(modulus_hex)?;
    let exponent = hex_decode(exponent_hex)?;
    let bit_len = (modulus.len() * 8) as u32;
    let hdr = BCRYPT_RSAKEY_BLOB {
        Magic: BCRYPT_RSAPUBLIC_MAGIC,
        BitLength: bit_len,
        cbPublicExp: exponent.len() as u32,
        cbModulus: modulus.len() as u32,
        cbPrime1: 0,
        cbPrime2: 0,
    };
    let hdr_size = std::mem::size_of::<BCRYPT_RSAKEY_BLOB>();
    let mut blob = vec![0u8; hdr_size + exponent.len() + modulus.len()];
    unsafe {
        ptr::write_unaligned(blob.as_mut_ptr() as *mut BCRYPT_RSAKEY_BLOB, hdr);
    }
    blob[hdr_size..hdr_size + exponent.len()].copy_from_slice(&exponent);
    blob[hdr_size + exponent.len()..].copy_from_slice(&modulus);

    unsafe {
        let mut alg = BCRYPT_ALG_HANDLE::default();
        let st = BCryptOpenAlgorithmProvider(
            &mut alg,
            BCRYPT_RSA_ALGORITHM,
            None,
            BCRYPT_OPEN_ALGORITHM_PROVIDER_FLAGS(0),
        );
        if st.is_err() {
            return Err(format!(
                "BCryptOpenAlgorithmProvider(RSA) failed: 0x{:08x}",
                st.0
            ));
        }
        let mut key = BCRYPT_KEY_HANDLE::default();
        let st_imp = BCryptImportKeyPair(alg, None, BCRYPT_RSAPUBLIC_BLOB, &mut key, &blob, 0);
        if st_imp.is_err() {
            let _ = BCryptCloseAlgorithmProvider(alg, 0);
            return Err(format!("BCryptImportKeyPair failed: 0x{:08x}", st_imp.0));
        }
        let mut out_len = 0u32;
        let st_sz = BCryptEncrypt(
            key,
            Some(plaintext),
            None,
            None,
            None,
            &mut out_len,
            BCRYPT_PAD_PKCS1,
        );
        if st_sz.is_err() {
            let _ = BCryptDestroyKey(key);
            let _ = BCryptCloseAlgorithmProvider(alg, 0);
            return Err(format!("BCryptEncrypt size failed: 0x{:08x}", st_sz.0));
        }
        let mut cipher = vec![0u8; out_len as usize];
        let st_enc = BCryptEncrypt(
            key,
            Some(plaintext),
            None,
            None,
            Some(&mut cipher),
            &mut out_len,
            BCRYPT_PAD_PKCS1,
        );
        let _ = BCryptDestroyKey(key);
        let _ = BCryptCloseAlgorithmProvider(alg, 0);
        if st_enc.is_err() {
            return Err(format!("BCryptEncrypt failed: 0x{:08x}", st_enc.0));
        }
        cipher.truncate(out_len as usize);
        Ok(base64_encode(&cipher))
    }
}

#[cfg(windows)]
pub fn dpapi_protect(plain: &[u8]) -> Result<Vec<u8>, String> {
    unsafe {
        let input = CRYPT_INTEGER_BLOB {
            cbData: plain.len() as u32,
            pbData: plain.as_ptr() as *mut u8,
        };
        let mut output = CRYPT_INTEGER_BLOB::default();
        CryptProtectData(
            &input,
            None,
            None,
            None,
            None,
            CRYPTPROTECT_UI_FORBIDDEN,
            &mut output,
        )
        .map_err(|e| format!("CryptProtectData failed: {e}"))?;
        let slice = std::slice::from_raw_parts(output.pbData, output.cbData as usize);
        let out = slice.to_vec();
        let _ = LocalFree(Some(HLOCAL(output.pbData as *mut c_void)));
        Ok(out)
    }
}

#[cfg(windows)]
pub fn dpapi_unprotect(sealed: &[u8]) -> Result<Vec<u8>, String> {
    unsafe {
        let input = CRYPT_INTEGER_BLOB {
            cbData: sealed.len() as u32,
            pbData: sealed.as_ptr() as *mut u8,
        };
        let mut output = CRYPT_INTEGER_BLOB::default();
        CryptUnprotectData(
            &input,
            None,
            None,
            None,
            None,
            CRYPTPROTECT_UI_FORBIDDEN,
            &mut output,
        )
        .map_err(|e| format!("CryptUnprotectData failed: {e}"))?;
        let slice = std::slice::from_raw_parts(output.pbData, output.cbData as usize);
        let out = slice.to_vec();
        let _ = LocalFree(Some(HLOCAL(output.pbData as *mut c_void)));
        Ok(out)
    }
}
