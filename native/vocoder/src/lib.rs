//! Panic-contained private C ABI for DVM Console's built-in vocoder.
//!
//! The ABI uses caller-owned buffers. Rust allocations, error values, and
//! panics never cross the boundary.

use std::cell::RefCell;
use std::ffi::c_char;
use std::panic::{catch_unwind, AssertUnwindSafe};

use blip25_vocoder::halfrate::frame::{
    decode_code_vectors, decode_frame, encode_code_vectors, encode_frame, DIBITS_PER_FRAME,
};
use blip25_vocoder::halfrate::{pack_natural, unpack_natural};
use blip25_vocoder::vocoder::{FrameStatus, Rate, Vocoder};

const ABI_VERSION: u32 = 4;
const MODE_DMR: u32 = 0;
const MODE_P25: u32 = 1;
const MODE_NXDN: u32 = 2;
const MODE_P25_PHASE2: u32 = 3;
const CAP_DMR: u64 = 1 << MODE_DMR;
const CAP_P25: u64 = 1 << MODE_P25;
const CAP_NXDN: u64 = 1 << MODE_NXDN;
const CAP_P25_PHASE2: u64 = 1 << MODE_P25_PHASE2;
const PCM_SAMPLES: usize = 160;
const HALF_RATE_CODEWORD_BYTES: usize = 9;
const HALF_RATE_PARAMETER_BYTES: usize = 7;
const P25_CODEWORD_BYTES: usize = 11;

const OK: i32 = 0;
const ERR_INVALID: i32 = -1;
const ERR_LENGTH: i32 = -2;
const ERR_STATE: i32 = -3;

// Positions of the four protocol-agnostic half-rate code vectors in a DMR
// 72-bit codeword, in first-transmitted-bit order. NXDN carries the same code
// vectors sequentially and therefore must not use this interleave.
const A_POSITIONS: [usize; 24] = [
    0, 4, 8, 12, 16, 20, 24, 28, 32, 36, 40, 44, 48, 52, 56, 60, 64, 68, 1, 5, 9, 13, 17, 21,
];
const B_POSITIONS: [usize; 23] = [
    25, 29, 33, 37, 41, 45, 49, 53, 57, 61, 65, 69, 2, 6, 10, 14, 18, 22, 26, 30, 34, 38, 42,
];
const C_POSITIONS: [usize; 25] = [
    46, 50, 54, 58, 62, 66, 70, 3, 7, 11, 15, 19, 23, 27, 31, 35, 39, 43, 47, 51, 55, 59, 63, 67,
    71,
];

thread_local! {
    static LAST_ERROR: RefCell<Vec<u8>> = RefCell::new(b"ok\0".to_vec());
}

#[repr(C)]
pub struct Session {
    mode: u32,
    vocoder: Vocoder,
    flushed: bool,
}

fn set_error(message: &str) {
    LAST_ERROR.with(|slot| {
        let mut bytes = message.as_bytes().to_vec();
        bytes.retain(|byte| *byte != 0);
        bytes.push(0);
        *slot.borrow_mut() = bytes;
    });
}

fn checked<T>(action: impl FnOnce() -> T, fallback: T) -> T {
    match catch_unwind(AssertUnwindSafe(action)) {
        Ok(value) => value,
        Err(_) => {
            set_error("native vocoder panic was contained");
            fallback
        }
    }
}

fn is_half_rate(mode: u32) -> bool {
    mode == MODE_DMR || mode == MODE_NXDN || mode == MODE_P25_PHASE2
}

fn codeword_bytes(mode: u32) -> Option<usize> {
    match mode {
        MODE_DMR | MODE_NXDN | MODE_P25_PHASE2 => Some(HALF_RATE_CODEWORD_BYTES),
        MODE_P25 => Some(P25_CODEWORD_BYTES),
        _ => None,
    }
}

fn rate(mode: u32) -> Option<Rate> {
    match mode {
        // All three use the 3600x2450 half-rate codec/FEC family. DMR and
        // NXDN still bypass its P25 Annex-S wire wrapper below and substitute
        // their own protocol interleave around encode_info/decode_info.
        MODE_DMR | MODE_NXDN | MODE_P25_PHASE2 => Some(Rate::HalfRate3600x2450),
        MODE_P25 => Some(Rate::FullRate4400x4400),
        _ => None,
    }
}

fn read_bit(bytes: &[u8], bit: usize) -> u32 {
    u32::from((bytes[bit / 8] >> (7 - bit % 8)) & 1)
}

fn write_bit(bytes: &mut [u8], bit: usize, value: u32) {
    let mask = 1u8 << (7 - bit % 8);
    if value & 1 != 0 {
        bytes[bit / 8] |= mask;
    } else {
        bytes[bit / 8] &= !mask;
    }
}

fn dmr_codeword_to_vectors(codeword: &[u8]) -> [u32; 4] {
    let mut vectors = [0u32; 4];
    for &position in &A_POSITIONS {
        vectors[0] = (vectors[0] << 1) | read_bit(codeword, position);
    }
    for &position in &B_POSITIONS {
        vectors[1] = (vectors[1] << 1) | read_bit(codeword, position);
    }
    for (index, &position) in C_POSITIONS.iter().enumerate() {
        let vector = if index < 11 { 2 } else { 3 };
        vectors[vector] = (vectors[vector] << 1) | read_bit(codeword, position);
    }
    vectors
}

fn dmr_vectors_to_codeword(vectors: &[u32; 4]) -> [u8; HALF_RATE_CODEWORD_BYTES] {
    let mut codeword = [0u8; HALF_RATE_CODEWORD_BYTES];
    for (index, &position) in A_POSITIONS.iter().enumerate() {
        write_bit(&mut codeword, position, vectors[0] >> (23 - index));
    }
    for (index, &position) in B_POSITIONS.iter().enumerate() {
        write_bit(&mut codeword, position, vectors[1] >> (22 - index));
    }
    for (index, &position) in C_POSITIONS.iter().enumerate() {
        let bit = if index < 11 {
            vectors[2] >> (10 - index)
        } else {
            vectors[3] >> (13 - (index - 11))
        };
        write_bit(&mut codeword, position, bit);
    }
    codeword
}

fn sequential_codeword_to_vectors(codeword: &[u8]) -> [u32; 4] {
    const WIDTHS: [usize; 4] = [24, 23, 11, 14];
    let mut bit = 0usize;
    std::array::from_fn(|vector| {
        let mut word = 0u32;
        for _ in 0..WIDTHS[vector] {
            word = (word << 1) | read_bit(codeword, bit);
            bit += 1;
        }
        word
    })
}

fn sequential_vectors_to_codeword(vectors: &[u32; 4]) -> [u8; HALF_RATE_CODEWORD_BYTES] {
    const WIDTHS: [usize; 4] = [24, 23, 11, 14];
    let mut codeword = [0u8; HALF_RATE_CODEWORD_BYTES];
    let mut bit = 0usize;
    for (vector, width) in WIDTHS.into_iter().enumerate() {
        for shift in (0..width).rev() {
            write_bit(&mut codeword, bit, vectors[vector] >> shift);
            bit += 1;
        }
    }
    codeword
}

fn unpack_dibits(codeword: &[u8]) -> [u8; DIBITS_PER_FRAME] {
    std::array::from_fn(|index| {
        let bit = index * 2;
        (codeword[bit / 8] >> (6 - bit % 8)) & 0x03
    })
}

fn pack_dibits(dibits: &[u8; DIBITS_PER_FRAME]) -> [u8; HALF_RATE_CODEWORD_BYTES] {
    let mut codeword = [0u8; HALF_RATE_CODEWORD_BYTES];
    for (index, dibit) in dibits.iter().enumerate() {
        let bit = index * 2;
        codeword[bit / 8] |= (dibit & 0x03) << (6 - bit % 8);
    }
    codeword
}

fn codeword_to_vectors(mode: u32, codeword: &[u8]) -> [u32; 4] {
    match mode {
        MODE_DMR => dmr_codeword_to_vectors(codeword),
        MODE_NXDN => sequential_codeword_to_vectors(codeword),
        _ => unreachable!("validated half-rate mode"),
    }
}

fn vectors_to_codeword(mode: u32, vectors: &[u32; 4]) -> [u8; HALF_RATE_CODEWORD_BYTES] {
    match mode {
        MODE_DMR => dmr_vectors_to_codeword(vectors),
        MODE_NXDN => sequential_vectors_to_codeword(vectors),
        _ => unreachable!("validated half-rate mode"),
    }
}

fn natural_to_codeword(mode: u32, parameters: &[u8]) -> [u8; HALF_RATE_CODEWORD_BYTES] {
    let info = unpack_natural(parameters);
    if mode == MODE_P25_PHASE2 {
        pack_dibits(&encode_frame(&info))
    } else {
        vectors_to_codeword(mode, &encode_code_vectors(&info))
    }
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
struct HalfRateDecode {
    parameters: [u8; HALF_RATE_PARAMETER_BYTES],
    corrected_errors: u16,
    unrecoverable: bool,
}

fn codeword_to_natural(mode: u32, codeword: &[u8]) -> HalfRateDecode {
    let frame = if mode == MODE_P25_PHASE2 {
        decode_frame(&unpack_dibits(codeword))
    } else {
        decode_code_vectors(codeword_to_vectors(mode, codeword))
    };
    let unrecoverable = frame.errors[0] == u8::MAX;
    HalfRateDecode {
        parameters: pack_natural(&frame.info),
        // An uncorrectable c0 marker is not an error count. Keep the numeric
        // metric in the decoder's live range and carry erasure separately.
        corrected_errors: if unrecoverable {
            15
        } else {
            frame.error_total()
        },
        unrecoverable,
    }
}

fn encode_natural(session: &mut Session, samples: &[i16]) -> Result<Vec<u8>, String> {
    if is_half_rate(session.mode) {
        let info: [u16; 4] = session
            .vocoder
            .encode_info(samples)
            .map_err(|error| error.to_string())?
            .try_into()
            .map_err(|_| "invalid half-rate parameter count".to_string())?;
        Ok(pack_natural(&info).to_vec())
    } else {
        session
            .vocoder
            .encode_pcm(samples)
            .map_err(|error| error.to_string())
    }
}

fn flush_natural(session: &mut Session) -> Option<Vec<u8>> {
    if session.flushed {
        return None;
    }
    session.flushed = true;
    if is_half_rate(session.mode) {
        let info: [u16; 4] = session
            .vocoder
            .encode_info(&[0i16; PCM_SAMPLES])
            .ok()?
            .try_into()
            .ok()?;
        Some(pack_natural(&info).to_vec())
    } else {
        session.vocoder.flush_encode().into_iter().next()
    }
}

#[no_mangle]
pub extern "C" fn dvmconsole_vocoder_abi_version() -> u32 {
    ABI_VERSION
}

#[no_mangle]
pub extern "C" fn dvmconsole_vocoder_capabilities() -> u64 {
    CAP_DMR | CAP_P25 | CAP_NXDN | CAP_P25_PHASE2
}

#[no_mangle]
pub extern "C" fn dvmconsole_vocoder_last_error() -> *const c_char {
    LAST_ERROR.with(|slot| slot.borrow().as_ptr().cast())
}

#[no_mangle]
pub extern "C" fn dvmconsole_vocoder_session_create(mode: u32) -> *mut Session {
    checked(
        || {
            let Some(rate) = rate(mode) else {
                set_error("unsupported vocoder mode");
                return std::ptr::null_mut();
            };
            set_error("ok");
            Box::into_raw(Box::new(Session {
                mode,
                vocoder: Vocoder::new(rate),
                flushed: false,
            }))
        },
        std::ptr::null_mut(),
    )
}

#[no_mangle]
/// # Safety
/// `session` must be null or a live handle returned by `session_create` that
/// has not already been destroyed.
pub unsafe extern "C" fn dvmconsole_vocoder_session_destroy(session: *mut Session) {
    checked(
        || {
            if !session.is_null() {
                // SAFETY: handles are created by session_create and ownership
                // is returned exactly once by the managed SafeHandle.
                unsafe { drop(Box::from_raw(session)) };
            }
        },
        (),
    );
}

#[no_mangle]
/// # Safety
/// `session` must point to a live session for the duration of the call.
pub unsafe extern "C" fn dvmconsole_vocoder_session_reset(session: *mut Session) -> i32 {
    checked(
        || {
            let Some(session) = (unsafe { session.as_mut() }) else {
                set_error("null session");
                return ERR_STATE;
            };
            session.vocoder.reset();
            session.flushed = false;
            set_error("ok");
            OK
        },
        ERR_STATE,
    )
}

#[no_mangle]
/// # Safety
/// The session and buffers must be live and valid for the lengths supplied.
pub unsafe extern "C" fn dvmconsole_vocoder_encode(
    session: *mut Session,
    samples: *const i16,
    sample_count: usize,
    output: *mut u8,
    output_capacity: usize,
) -> i32 {
    checked(
        || {
            let Some(session) = (unsafe { session.as_mut() }) else {
                set_error("null encode session");
                return ERR_INVALID;
            };
            if samples.is_null() || output.is_null() {
                set_error("null encode buffer");
                return ERR_INVALID;
            }
            if sample_count != PCM_SAMPLES {
                set_error("encode requires 160 samples");
                return ERR_LENGTH;
            }
            let needed = codeword_bytes(session.mode).expect("validated session mode");
            if output_capacity < needed {
                set_error("encode output buffer is too small");
                return ERR_LENGTH;
            }
            let input = unsafe { std::slice::from_raw_parts(samples, sample_count) };
            let natural = match encode_natural(session, input) {
                Ok(value) => value,
                Err(error) => {
                    set_error(&format!("native encode failed: {error}"));
                    return ERR_STATE;
                }
            };
            session.flushed = false;
            let encoded = if is_half_rate(session.mode) {
                natural_to_codeword(session.mode, &natural).to_vec()
            } else {
                natural
            };
            unsafe { std::slice::from_raw_parts_mut(output, needed) }.copy_from_slice(&encoded);
            set_error("ok");
            needed as i32
        },
        ERR_STATE,
    )
}

#[no_mangle]
/// # Safety
/// The session and output buffer must be live and valid for the supplied size.
pub unsafe extern "C" fn dvmconsole_vocoder_flush_encode(
    session: *mut Session,
    output: *mut u8,
    output_capacity: usize,
) -> i32 {
    checked(
        || {
            let Some(session) = (unsafe { session.as_mut() }) else {
                set_error("null flush session");
                return ERR_INVALID;
            };
            if output.is_null() {
                set_error("null flush buffer");
                return ERR_INVALID;
            }
            let needed = codeword_bytes(session.mode).expect("validated session mode");
            if output_capacity < needed {
                set_error("flush output buffer is too small");
                return ERR_LENGTH;
            }
            let Some(natural) = flush_natural(session) else {
                set_error("ok");
                return 0;
            };
            let encoded = if is_half_rate(session.mode) {
                natural_to_codeword(session.mode, &natural).to_vec()
            } else {
                natural
            };
            unsafe { std::slice::from_raw_parts_mut(output, needed) }.copy_from_slice(&encoded);
            set_error("ok");
            needed as i32
        },
        ERR_STATE,
    )
}

#[no_mangle]
/// # Safety
/// The session and buffers must be live and valid for the lengths supplied.
pub unsafe extern "C" fn dvmconsole_vocoder_decode(
    session: *mut Session,
    input: *const u8,
    input_length: usize,
    samples: *mut i16,
    sample_capacity: usize,
) -> i32 {
    checked(
        || {
            let Some(session) = (unsafe { session.as_mut() }) else {
                set_error("null decode session");
                return ERR_INVALID;
            };
            if input.is_null() || samples.is_null() {
                set_error("null decode buffer");
                return ERR_INVALID;
            }
            let needed = codeword_bytes(session.mode).expect("validated session mode");
            if input_length != needed || sample_capacity < PCM_SAMPLES {
                set_error("invalid decode buffer length");
                return ERR_LENGTH;
            }
            let input = unsafe { std::slice::from_raw_parts(input, input_length) };
            let (decoded, errors) = if is_half_rate(session.mode) {
                let frame = codeword_to_natural(session.mode, input);
                let info = unpack_natural(&frame.parameters);
                let status = FrameStatus::new(u32::from(frame.corrected_errors), false)
                    .with_lost(frame.unrecoverable);
                (
                    session.vocoder.decode_info(&info, status),
                    frame.corrected_errors,
                )
            } else {
                (session.vocoder.decode_bits(input), 0)
            };
            let decoded = match decoded {
                Ok(value) => value,
                Err(error) => {
                    set_error(&format!("native decode failed: {error}"));
                    return ERR_STATE;
                }
            };
            if decoded.len() != PCM_SAMPLES {
                set_error("native decoder returned an invalid frame length");
                return ERR_STATE;
            }
            unsafe { std::slice::from_raw_parts_mut(samples, PCM_SAMPLES) }
                .copy_from_slice(&decoded);
            set_error("ok");
            i32::from(errors)
        },
        ERR_STATE,
    )
}

#[no_mangle]
/// Decode one missing 20 ms frame through the codec's native concealment path.
///
/// # Safety
/// The session and sample buffer must be live and valid for the supplied size.
pub unsafe extern "C" fn dvmconsole_vocoder_decode_lost(
    session: *mut Session,
    samples: *mut i16,
    sample_capacity: usize,
) -> i32 {
    checked(
        || {
            let Some(session) = (unsafe { session.as_mut() }) else {
                set_error("null lost-frame decode session");
                return ERR_INVALID;
            };
            if samples.is_null() || sample_capacity < PCM_SAMPLES {
                set_error("invalid lost-frame decode buffer");
                return ERR_LENGTH;
            }
            let decoded = if is_half_rate(session.mode) {
                session.vocoder.decode_info(&[0u16; 4], FrameStatus::LOST)
            } else {
                session.vocoder.decode_info(&[0u16; 8], FrameStatus::LOST)
            };
            let decoded = match decoded {
                Ok(value) => value,
                Err(error) => {
                    set_error(&format!("native lost-frame decode failed: {error}"));
                    return ERR_STATE;
                }
            };
            unsafe { std::slice::from_raw_parts_mut(samples, PCM_SAMPLES) }
                .copy_from_slice(&decoded);
            set_error("ok");
            OK
        },
        ERR_STATE,
    )
}

#[no_mangle]
/// # Safety
/// The session and buffers must be live and valid for the lengths supplied.
pub unsafe extern "C" fn dvmconsole_vocoder_encode_parameters(
    session: *mut Session,
    samples: *const i16,
    sample_count: usize,
    parameters: *mut u8,
    parameter_capacity: usize,
) -> i32 {
    checked(
        || {
            let Some(session) = (unsafe { session.as_mut() }) else {
                set_error("null parameter encode session");
                return ERR_INVALID;
            };
            if !is_half_rate(session.mode) {
                set_error("parameter encoding requires a half-rate session");
                return ERR_STATE;
            }
            if samples.is_null() || parameters.is_null() {
                set_error("null parameter encode buffer");
                return ERR_INVALID;
            }
            if sample_count != PCM_SAMPLES || parameter_capacity < HALF_RATE_PARAMETER_BYTES {
                set_error("invalid parameter encode buffer length");
                return ERR_LENGTH;
            }
            let input = unsafe { std::slice::from_raw_parts(samples, sample_count) };
            let encoded = match encode_natural(session, input) {
                Ok(value) => value,
                Err(error) => {
                    set_error(&format!("native parameter encode failed: {error}"));
                    return ERR_STATE;
                }
            };
            session.flushed = false;
            unsafe { std::slice::from_raw_parts_mut(parameters, HALF_RATE_PARAMETER_BYTES) }
                .copy_from_slice(&encoded);
            set_error("ok");
            HALF_RATE_PARAMETER_BYTES as i32
        },
        ERR_STATE,
    )
}

#[no_mangle]
/// # Safety
/// The session and output buffer must be live and valid for the supplied size.
pub unsafe extern "C" fn dvmconsole_vocoder_flush_parameters(
    session: *mut Session,
    parameters: *mut u8,
    parameter_capacity: usize,
) -> i32 {
    checked(
        || {
            let Some(session) = (unsafe { session.as_mut() }) else {
                set_error("null parameter flush session");
                return ERR_INVALID;
            };
            if !is_half_rate(session.mode) {
                set_error("parameter flushing requires a half-rate session");
                return ERR_STATE;
            }
            if parameters.is_null() {
                set_error("null parameter flush buffer");
                return ERR_INVALID;
            }
            if parameter_capacity < HALF_RATE_PARAMETER_BYTES {
                set_error("parameter flush output buffer is too small");
                return ERR_LENGTH;
            }
            let Some(encoded) = flush_natural(session) else {
                set_error("ok");
                return 0;
            };
            unsafe { std::slice::from_raw_parts_mut(parameters, HALF_RATE_PARAMETER_BYTES) }
                .copy_from_slice(&encoded);
            set_error("ok");
            HALF_RATE_PARAMETER_BYTES as i32
        },
        ERR_STATE,
    )
}

#[no_mangle]
/// # Safety
/// The session and buffers must be live and valid for the lengths supplied.
pub unsafe extern "C" fn dvmconsole_vocoder_decode_parameters(
    session: *mut Session,
    parameters: *const u8,
    parameter_length: usize,
    corrected_errors: u32,
    lost: bool,
    samples: *mut i16,
    sample_capacity: usize,
) -> i32 {
    checked(
        || {
            let Some(session) = (unsafe { session.as_mut() }) else {
                set_error("null parameter decode session");
                return ERR_INVALID;
            };
            if !is_half_rate(session.mode) {
                set_error("parameter decoding requires a half-rate session");
                return ERR_STATE;
            }
            if parameters.is_null() || samples.is_null() {
                set_error("null parameter decode buffer");
                return ERR_INVALID;
            }
            if parameter_length != HALF_RATE_PARAMETER_BYTES || sample_capacity < PCM_SAMPLES {
                set_error("invalid parameter decode buffer length");
                return ERR_LENGTH;
            }
            let input = unsafe { std::slice::from_raw_parts(parameters, parameter_length) };
            if input[HALF_RATE_PARAMETER_BYTES - 1] & 0x7f != 0 {
                set_error("half-rate parameter padding bits must be zero");
                return ERR_INVALID;
            }
            let info = unpack_natural(input);
            let decoded = match session.vocoder.decode_info(
                &info,
                FrameStatus::new(corrected_errors, false).with_lost(lost),
            ) {
                Ok(value) => value,
                Err(error) => {
                    set_error(&format!("native parameter decode failed: {error}"));
                    return ERR_STATE;
                }
            };
            unsafe { std::slice::from_raw_parts_mut(samples, PCM_SAMPLES) }
                .copy_from_slice(&decoded);
            set_error("ok");
            OK
        },
        ERR_STATE,
    )
}

#[no_mangle]
/// # Safety
/// All buffers must be live and valid for the lengths supplied.
pub unsafe extern "C" fn dvmconsole_vocoder_half_rate_extract(
    mode: u32,
    codeword: *const u8,
    codeword_length: usize,
    parameters: *mut u8,
    parameter_capacity: usize,
    corrected_errors: *mut u16,
) -> i32 {
    checked(
        || {
            if !is_half_rate(mode) {
                set_error("half-rate extract requires DMR, NXDN, or P25 Phase 2 mode");
                return ERR_INVALID;
            }
            if codeword.is_null() || parameters.is_null() || corrected_errors.is_null() {
                set_error("null half-rate extract buffer");
                return ERR_INVALID;
            }
            if codeword_length != HALF_RATE_CODEWORD_BYTES
                || parameter_capacity < HALF_RATE_PARAMETER_BYTES
            {
                set_error("invalid half-rate extract buffer length");
                return ERR_LENGTH;
            }
            let input = unsafe { std::slice::from_raw_parts(codeword, codeword_length) };
            let frame = codeword_to_natural(mode, input);
            unsafe {
                std::slice::from_raw_parts_mut(parameters, HALF_RATE_PARAMETER_BYTES)
                    .copy_from_slice(&frame.parameters);
                // Preserve the erasure marker across the compact ABI without
                // widening it: no valid corrected-error total can equal MAX.
                *corrected_errors = if frame.unrecoverable {
                    u16::MAX
                } else {
                    frame.corrected_errors
                };
            }
            set_error("ok");
            HALF_RATE_PARAMETER_BYTES as i32
        },
        ERR_STATE,
    )
}

#[no_mangle]
/// # Safety
/// All buffers must be live and valid for the lengths supplied.
pub unsafe extern "C" fn dvmconsole_vocoder_half_rate_build(
    mode: u32,
    parameters: *const u8,
    parameter_length: usize,
    codeword: *mut u8,
    codeword_capacity: usize,
) -> i32 {
    checked(
        || {
            if !is_half_rate(mode) {
                set_error("half-rate build requires DMR, NXDN, or P25 Phase 2 mode");
                return ERR_INVALID;
            }
            if parameters.is_null() || codeword.is_null() {
                set_error("null half-rate build buffer");
                return ERR_INVALID;
            }
            if parameter_length != HALF_RATE_PARAMETER_BYTES
                || codeword_capacity < HALF_RATE_CODEWORD_BYTES
            {
                set_error("invalid half-rate build buffer length");
                return ERR_LENGTH;
            }
            let input = unsafe { std::slice::from_raw_parts(parameters, parameter_length) };
            if input[HALF_RATE_PARAMETER_BYTES - 1] & 0x7f != 0 {
                set_error("half-rate parameter padding bits must be zero");
                return ERR_INVALID;
            }
            let output = natural_to_codeword(mode, input);
            unsafe { std::slice::from_raw_parts_mut(codeword, HALF_RATE_CODEWORD_BYTES) }
                .copy_from_slice(&output);
            set_error("ok");
            HALF_RATE_CODEWORD_BYTES as i32
        },
        ERR_STATE,
    )
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn abi_reports_all_required_modes() {
        assert_eq!(dvmconsole_vocoder_abi_version(), 4);
        assert_eq!(dvmconsole_vocoder_capabilities(), 15);
    }

    #[test]
    fn half_rate_fec_and_interleave_round_trip() {
        let parameters = [0x12, 0x34, 0x56, 0x78, 0x9a, 0xbc, 0x80];
        for mode in [MODE_DMR, MODE_NXDN, MODE_P25_PHASE2] {
            let codeword = natural_to_codeword(mode, &parameters);
            let decoded = codeword_to_natural(mode, &codeword);
            assert_eq!(decoded.parameters, parameters);
            assert_eq!(decoded.corrected_errors, 0);
            assert!(!decoded.unrecoverable);
        }
    }

    #[test]
    fn p25_phase2_uses_annex_s_interleave() {
        let parameters = [0x12, 0x34, 0x56, 0x78, 0x9a, 0xbc, 0x80];
        let expected = pack_dibits(&encode_frame(&unpack_natural(&parameters)));
        assert_eq!(natural_to_codeword(MODE_P25_PHASE2, &parameters), expected);
    }

    #[test]
    fn nxdn_is_sequential_while_dmr_is_interleaved() {
        let parameters = [0x12, 0x34, 0x56, 0x78, 0x9a, 0xbc, 0x80];
        let vectors = encode_code_vectors(&unpack_natural(&parameters));
        let nxdn = natural_to_codeword(MODE_NXDN, &parameters);
        let dmr = natural_to_codeword(MODE_DMR, &parameters);

        assert_eq!(sequential_codeword_to_vectors(&nxdn), vectors);
        assert_eq!(dmr_codeword_to_vectors(&dmr), vectors);
        assert_ne!(nxdn, dmr);
    }

    #[test]
    fn published_dmr_silence_fixture_matches_wire_mapping() {
        // Widely published DMR 72-bit silence fixture (ACAA40200044408080),
        // independent of this adapter's position tables. ETSI TS 102 361-1
        // section 6.1 requires each 72-bit vocoder frame to remain contiguous
        // in the 216-bit voice socket.
        let codeword = [0xac, 0xaa, 0x40, 0x20, 0x00, 0x44, 0x40, 0x80, 0x80];
        let frame = codeword_to_natural(MODE_DMR, &codeword);
        assert_eq!(frame.parameters, [0xf0, 0, 0, 0, 0, 0, 0]);
        assert_eq!(natural_to_codeword(MODE_DMR, &frame.parameters), codeword);
        assert_eq!(frame.corrected_errors, 0);
        assert!(!frame.unrecoverable);
    }

    #[test]
    fn one_bit_error_is_corrected() {
        let parameters = [0x12, 0x34, 0x56, 0x78, 0x9a, 0xbc, 0x80];
        let mut codeword = natural_to_codeword(MODE_DMR, &parameters);
        codeword[0] ^= 0x80;
        let decoded = codeword_to_natural(MODE_DMR, &codeword);
        assert_eq!(decoded.parameters, parameters);
        assert!(decoded.corrected_errors >= 1);
        assert!(!decoded.unrecoverable);
    }

    #[test]
    fn four_errors_in_extended_golay_c0_are_unrecoverable() {
        let parameters = [0x12, 0x34, 0x56, 0x78, 0x9a, 0xbc, 0x80];
        let mut codeword = natural_to_codeword(MODE_DMR, &parameters);
        for &position in &A_POSITIONS[..4] {
            let bit = read_bit(&codeword, position);
            write_bit(&mut codeword, position, bit ^ 1);
        }

        let decoded = codeword_to_natural(MODE_DMR, &codeword);
        assert!(decoded.unrecoverable);
        assert_eq!(decoded.corrected_errors, 15);
    }

    #[test]
    fn flush_is_one_shot() {
        let handle = dvmconsole_vocoder_session_create(MODE_P25);
        assert!(!handle.is_null());
        let samples = [0i16; PCM_SAMPLES];
        let mut output = [0u8; P25_CODEWORD_BYTES];
        assert_eq!(
            unsafe {
                dvmconsole_vocoder_encode(
                    handle,
                    samples.as_ptr(),
                    samples.len(),
                    output.as_mut_ptr(),
                    output.len(),
                )
            },
            P25_CODEWORD_BYTES as i32
        );
        assert_eq!(
            unsafe { dvmconsole_vocoder_flush_encode(handle, output.as_mut_ptr(), output.len()) },
            P25_CODEWORD_BYTES as i32
        );
        assert_eq!(
            unsafe { dvmconsole_vocoder_flush_encode(handle, output.as_mut_ptr(), output.len()) },
            0
        );
        unsafe { dvmconsole_vocoder_session_destroy(handle) };
    }

    #[test]
    fn invalid_mode_is_rejected() {
        assert!(dvmconsole_vocoder_session_create(99).is_null());
    }

    fn loopback_rms(mode: u32) -> f64 {
        let mut tx = Session {
            mode,
            vocoder: Vocoder::new(rate(mode).unwrap()),
            flushed: false,
        };
        let mut rx = Session {
            mode,
            vocoder: Vocoder::new(rate(mode).unwrap()),
            flushed: false,
        };
        let mut energy = 0.0f64;
        let mut count = 0usize;
        for frame_index in 0..24 {
            let samples: Vec<i16> = (0..PCM_SAMPLES)
                .map(|sample_index| {
                    let sample = frame_index * PCM_SAMPLES + sample_index;
                    (9000.0 * (2.0 * std::f64::consts::PI * 440.0 * sample as f64 / 8000.0).sin())
                        as i16
                })
                .collect();
            let natural = encode_natural(&mut tx, &samples).unwrap();
            let decoded = if is_half_rate(mode) {
                let codeword = natural_to_codeword(mode, &natural);
                let frame = codeword_to_natural(mode, &codeword);
                assert_eq!(frame.corrected_errors, 0);
                assert!(!frame.unrecoverable);
                rx.vocoder
                    .decode_info(
                        &unpack_natural(&frame.parameters),
                        FrameStatus::new(0, false),
                    )
                    .unwrap()
            } else {
                rx.vocoder.decode_bits(&natural).unwrap()
            };
            if frame_index >= 8 {
                for sample in decoded {
                    energy += f64::from(sample) * f64::from(sample);
                    count += 1;
                }
            }
        }
        (energy / count as f64).sqrt()
    }

    #[test]
    fn reports_protocol_loopback_levels() {
        let dmr = loopback_rms(MODE_DMR);
        let p25 = loopback_rms(MODE_P25);
        println!(
            "loopback RMS: DMR={dmr:.1}, P25={p25:.1}, ratio={:.3}",
            p25 / dmr
        );
        assert!(dmr > 0.0 && p25 > 0.0);
    }
}
