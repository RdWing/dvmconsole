//! Panic-contained private C ABI for DVM Console's built-in vocoder.
//!
//! The ABI uses caller-owned buffers. Rust allocations, error values, and
//! panics never cross the boundary.

mod legacy_p25_tone_frames;

use std::cell::RefCell;
use std::ffi::c_char;
use std::panic::{catch_unwind, AssertUnwindSafe};

use blip25_vocoder::fullrate::frame::{decode_frame as decode_full_rate_frame, INFO_WIDTHS};
use blip25_vocoder::halfrate::dequantize::{
    encode_tone_frame_info, TONE_AMPLITUDE_EXPONENT_STEP, TONE_AMPLITUDE_PEAK,
};
use blip25_vocoder::halfrate::frame::{
    decode_code_vectors, decode_frame, encode_code_vectors, encode_frame, ANNEX_T, DIBITS_PER_FRAME,
};
use blip25_vocoder::halfrate::{pack_natural, unpack_natural};
use blip25_vocoder::rate_conversion::HalfToFullConverter;
use blip25_vocoder::vocoder::{FrameStatus, Rate, Vocoder};

const ABI_VERSION: u32 = 5;
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
    tone_converter: HalfToFullConverter,
    pending_tone: Option<DetectedTone>,
    flushed: bool,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
struct DetectedTone {
    id: u8,
    amplitude: u8,
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

fn pack_full_rate_natural(info: &[u16; 8]) -> [u8; P25_CODEWORD_BYTES] {
    let mut output = [0u8; P25_CODEWORD_BYTES];
    let mut bit = 0usize;
    for (vector, width) in INFO_WIDTHS.into_iter().enumerate() {
        for shift in (0..usize::from(width)).rev() {
            write_bit(&mut output, bit, u32::from(info[vector]) >> shift);
            bit += 1;
        }
    }
    output
}

fn tone_component(samples: &[f64; PCM_SAMPLES], frequency: f64) -> (f64, f64) {
    let step = std::f64::consts::TAU * frequency / 8000.0;
    let (step_sin, step_cos) = step.sin_cos();
    let (mut sin, mut cos) = (0.0, 1.0);
    let (mut sin_sum, mut cos_sum) = (0.0, 0.0);
    for sample in samples {
        cos_sum += sample * cos;
        sin_sum += sample * sin;
        (sin, cos) = (
            sin * step_cos + cos * step_sin,
            cos * step_cos - sin * step_sin,
        );
    }
    let magnitude_squared = cos_sum * cos_sum + sin_sum * sin_sum;
    let explained_energy = 2.0 * magnitude_squared / PCM_SAMPLES as f64;
    let peak = 2.0 * magnitude_squared.sqrt() / PCM_SAMPLES as f64;
    (explained_energy, peak)
}

/// Recognize only frames overwhelmingly explained by a representable single
/// or dual tone. Normal speech continues through the ordinary speech encoder.
fn detect_tone(samples: &[i16]) -> Option<DetectedTone> {
    let mean = samples.iter().map(|&sample| f64::from(sample)).sum::<f64>() / PCM_SAMPLES as f64;
    let centered: [f64; PCM_SAMPLES] =
        std::array::from_fn(|index| f64::from(samples[index]) - mean);
    let total_energy = centered.iter().map(|sample| sample * sample).sum::<f64>();
    if total_energy < PCM_SAMPLES as f64 * 256.0f64.powi(2) {
        return None;
    }

    let mut best: Option<(f64, u8, f64)> = None;
    for (id, row) in ANNEX_T.iter().enumerate() {
        let Some(tone) = row else {
            continue;
        };
        let frequency1 = f64::from(tone.f0) * f64::from(tone.l1);
        let frequency2 = f64::from(tone.f0) * f64::from(tone.l2);
        let (energy1, peak1) = tone_component(&centered, frequency1);
        let (explained, peak) = if tone.l1 == tone.l2 {
            (energy1, peak1)
        } else {
            let (energy2, peak2) = tone_component(&centered, frequency2);
            let balance = energy1.min(energy2) / energy1.max(energy2).max(1.0);
            if balance < 0.15 {
                continue;
            }
            (energy1 + energy2, peak1.max(peak2))
        };
        let score = explained / total_energy;
        if best.is_none_or(|(best_score, _, _)| score > best_score) {
            best = Some((score, id as u8, peak));
        }
    }

    let (score, id, peak) = best?;
    if score < 0.72 {
        return None;
    }
    let amplitude = ((peak / TONE_AMPLITUDE_PEAK).log10() / TONE_AMPLITUDE_EXPONENT_STEP + 127.0)
        .round()
        .clamp(0.0, 127.0) as u8;
    Some(DetectedTone { id, amplitude })
}

fn encode_detected_tone(session: &mut Session, tone: DetectedTone) -> Vec<u8> {
    let info = encode_tone_frame_info(tone.id, tone.amplitude);
    if is_half_rate(session.mode) {
        return pack_natural(&info).to_vec();
    }
    if session.mode == MODE_P25 {
        let row = ANNEX_T[tone.id as usize].expect("detected Annex T tone");
        if row.l1 == row.l2 {
            let frequency = f64::from(row.f0) * f64::from(row.l1);
            return legacy_p25_tone_frames::nearest(frequency).to_vec();
        }
    }
    let half_rate_dibits = encode_frame(&info);
    let full_rate_dibits = session
        .tone_converter
        .convert(&half_rate_dibits)
        .expect("valid Annex T tone conversion");
    pack_full_rate_natural(&decode_full_rate_frame(&full_rate_dibits).info).to_vec()
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
    // Generated P25 tones carry explicit metadata through the dedicated ABI
    // below. Do not inspect ordinary Phase 1 voice PCM for tone-like content.
    let detected_tone = if session.mode == MODE_P25 {
        None
    } else {
        detect_tone(samples)
    };
    let ordinary = if is_half_rate(session.mode) {
        let info: [u16; 4] = session
            .vocoder
            .encode_info(samples)
            .map_err(|error| error.to_string())?
            .try_into()
            .map_err(|_| "invalid half-rate parameter count".to_string())?;
        pack_natural(&info).to_vec()
    } else {
        session
            .vocoder
            .encode_pcm(samples)
            .map_err(|error| error.to_string())?
    };
    let encoded = session
        .pending_tone
        .map(|tone| encode_detected_tone(session, tone))
        .unwrap_or(ordinary);
    session.pending_tone = detected_tone;
    Ok(encoded)
}

#[no_mangle]
/// Returns one explicitly requested P25 Phase 1 single-tone lookup frame.
///
/// # Safety
/// The session and output buffer must be live and valid for the supplied size.
pub unsafe extern "C" fn dvmconsole_vocoder_encode_p25_single_tone(
    session: *mut Session,
    frequency_hz: f64,
    output: *mut u8,
    output_capacity: usize,
) -> i32 {
    checked(
        || {
            let Some(session) = (unsafe { session.as_mut() }) else {
                set_error("null P25 single-tone session");
                return ERR_INVALID;
            };
            if session.mode != MODE_P25 {
                set_error("single-tone lookup requires a P25 Phase 1 session");
                return ERR_STATE;
            }
            if !frequency_hz.is_finite() || !(300.0..=2500.0).contains(&frequency_hz) {
                set_error("P25 single-tone frequency must be 300 through 2500 Hz");
                return ERR_INVALID;
            }
            if output.is_null() || output_capacity < P25_CODEWORD_BYTES {
                set_error("P25 single-tone output buffer is too small");
                return ERR_LENGTH;
            }
            let codeword = legacy_p25_tone_frames::nearest(frequency_hz);
            unsafe { std::slice::from_raw_parts_mut(output, P25_CODEWORD_BYTES) }
                .copy_from_slice(&codeword);
            session.pending_tone = None;
            set_error("ok");
            P25_CODEWORD_BYTES as i32
        },
        ERR_STATE,
    )
}

fn flush_natural(session: &mut Session) -> Option<Vec<u8>> {
    if session.flushed {
        return None;
    }
    session.flushed = true;
    let ordinary = if is_half_rate(session.mode) {
        let info: [u16; 4] = session
            .vocoder
            .encode_info(&[0i16; PCM_SAMPLES])
            .ok()?
            .try_into()
            .ok()?;
        pack_natural(&info).to_vec()
    } else {
        session.vocoder.flush_encode().into_iter().next()?
    };
    Some(
        session
            .pending_tone
            .take()
            .map(|tone| encode_detected_tone(session, tone))
            .unwrap_or(ordinary),
    )
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
                tone_converter: HalfToFullConverter::new(),
                pending_tone: None,
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
            session.tone_converter = HalfToFullConverter::new();
            session.pending_tone = None;
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
        assert_eq!(dvmconsole_vocoder_abi_version(), 5);
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

    fn test_session(mode: u32) -> Session {
        Session {
            mode,
            vocoder: Vocoder::new(rate(mode).expect("test mode")),
            tone_converter: HalfToFullConverter::new(),
            pending_tone: None,
            flushed: false,
        }
    }

    fn tone(frequency: f64, frame: usize) -> [i16; PCM_SAMPLES] {
        std::array::from_fn(|sample| {
            let position = frame * PCM_SAMPLES + sample;
            ((std::f64::consts::TAU * frequency * position as f64 / 8000.0).sin()
                * 0.35
                * f64::from(i16::MAX)) as i16
        })
    }

    fn dual_tone(frequency1: f64, frequency2: f64) -> [i16; PCM_SAMPLES] {
        std::array::from_fn(|sample| {
            let time = sample as f64 / 8000.0;
            (((std::f64::consts::TAU * frequency1 * time).sin()
                + (std::f64::consts::TAU * frequency2 * time).sin())
                * 0.175
                * f64::from(i16::MAX)) as i16
        })
    }

    #[test]
    fn generated_alert_tones_use_nearest_annex_t_rows() {
        for (frequency, expected_id) in [(800.0, 26), (1000.0, 32), (1500.0, 48)] {
            let detected = detect_tone(&tone(frequency, 0)).expect("pure tone");
            assert_eq!(detected.id, expected_id, "{frequency} Hz");
        }
        assert_eq!(detect_tone(&[0; PCM_SAMPLES]), None);

        let mut state = 0x1234_5678u32;
        let noise: [i16; PCM_SAMPLES] = std::array::from_fn(|_| {
            state = state.wrapping_mul(1_664_525).wrapping_add(1_013_904_223);
            (state >> 16) as i16
        });
        assert_eq!(detect_tone(&noise), None);
    }

    #[test]
    fn generated_dtmf_uses_a_dual_tone_row() {
        let detected = detect_tone(&dual_tone(697.0, 1209.0)).expect("DTMF tone");
        let row = ANNEX_T[detected.id as usize].expect("Annex T row");
        assert_ne!(row.l1, row.l2);
        let mut frequencies = [
            f64::from(row.f0) * f64::from(row.l1),
            f64::from(row.f0) * f64::from(row.l2),
        ];
        frequencies.sort_by(f64::total_cmp);
        assert!(
            (frequencies[0] - 697.0).abs() < 6.0,
            "detected {} and {} Hz",
            frequencies[0],
            frequencies[1]
        );
        assert!(
            (frequencies[1] - 1209.0).abs() < 6.0,
            "detected {} and {} Hz",
            frequencies[0],
            frequencies[1]
        );
    }

    #[test]
    fn p25_single_alerts_use_legacy_vp8000_frames() {
        for &(frequency, expected) in legacy_p25_tone_frames::SINGLE_TONES {
            assert_eq!(
                legacy_p25_tone_frames::nearest(f64::from(frequency)),
                expected,
                "{frequency} Hz table entry"
            );
        }

        for (frequency, expected) in [
            (
                800.0,
                [
                    0x15, 0x47, 0x9D, 0x1B, 0xDC, 0xED, 0x82, 0x20, 0x71, 0x1E, 0x98,
                ],
            ),
            (
                1000.0,
                [
                    0x09, 0x23, 0x0B, 0x0D, 0xC4, 0xA5, 0xCA, 0xE8, 0x28, 0x0A, 0x32,
                ],
            ),
            (
                1500.0,
                [
                    0x01, 0x2D, 0xA7, 0x2A, 0xDD, 0xA8, 0x5C, 0xC8, 0x5C, 0x49, 0x46,
                ],
            ),
        ] {
            let detected = detect_tone(&tone(frequency, 0)).expect("generated alert tone");
            let mut session = test_session(MODE_P25);
            assert_eq!(
                encode_detected_tone(&mut session, detected),
                expected,
                "{frequency} Hz generated alert"
            );
        }
    }

    #[test]
    fn explicit_p25_generated_tone_abi_uses_fixed_frames() {
        let handle = dvmconsole_vocoder_session_create(MODE_P25);
        assert!(!handle.is_null());
        let mut output = [0u8; P25_CODEWORD_BYTES];

        assert_eq!(
            unsafe {
                dvmconsole_vocoder_encode_p25_single_tone(
                    handle,
                    1000.0,
                    output.as_mut_ptr(),
                    output.len(),
                )
            },
            P25_CODEWORD_BYTES as i32
        );
        assert_eq!(
            output,
            [0x09, 0x23, 0x0B, 0x0D, 0xC4, 0xA5, 0xCA, 0xE8, 0x28, 0x0A, 0x32,]
        );

        unsafe { dvmconsole_vocoder_session_destroy(handle) };
    }

    #[test]
    fn p25_dtmf_stays_on_regular_voice_encoder_path() {
        let detected = detect_tone(&dual_tone(697.0, 1209.0)).expect("DTMF tone");
        let mut session = test_session(MODE_P25);
        let encoded = encode_natural(&mut session, &dual_tone(697.0, 1209.0))
            .expect("ordinary P25 DTMF voice encode");
        let bridged = encode_detected_tone(&mut session, detected);

        assert!(session.pending_tone.is_none());
        assert_ne!(encoded, bridged);
    }

    #[test]
    fn generated_alert_tones_are_stable_half_rate_tone_frames() {
        use blip25_vocoder::halfrate::dequantize::parse_tone_frame;

        for mode in [MODE_DMR, MODE_NXDN, MODE_P25_PHASE2] {
            let mut session = test_session(mode);
            let _pre_roll = encode_natural(&mut session, &tone(1000.0, 0)).expect("pre-roll");
            let first = encode_natural(&mut session, &tone(1000.0, 1)).expect("first tone");
            let second = encode_natural(&mut session, &tone(1000.0, 2)).expect("second tone");
            assert_eq!(first, second, "mode {mode}");
            let fields = parse_tone_frame(&unpack_natural(&first)).expect("tone parameters");
            assert_eq!(fields.id, 32);
        }
    }

    #[test]
    fn p25_legacy_tones_produce_stable_decodable_full_rate_frames() {
        let mut rx = Vocoder::new(Rate::FullRate4400x4400);
        let mut payloads = std::collections::BTreeSet::new();
        for _frame in 0..50 {
            let encoded = legacy_p25_tone_frames::nearest(1000.0).to_vec();
            payloads.insert(encoded.clone());
            let decoded = rx.decode_bits(&encoded).expect("tone decode");
            assert_eq!(decoded.len(), PCM_SAMPLES);
        }
        assert_eq!(payloads.len(), 1);
    }

    #[test]
    fn p25_info_only_erasure_uses_native_concealment() {
        let mut rx = Vocoder::new(Rate::FullRate4400x4400);
        let erasure = Rate::FullRate4400x4400.erasure_frame();

        let decoded = rx.decode_bits(&erasure).expect("erasure decode");

        assert_eq!(decoded.len(), PCM_SAMPLES);
        assert!(rx
            .last_stats()
            .decode
            .as_ref()
            .expect("decode stats")
            .disposition
            .is_concealed());
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
            tone_converter: HalfToFullConverter::new(),
            pending_tone: None,
            flushed: false,
        };
        let mut rx = Session {
            mode,
            vocoder: Vocoder::new(rate(mode).unwrap()),
            tone_converter: HalfToFullConverter::new(),
            pending_tone: None,
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
