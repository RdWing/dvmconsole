use blip25_vocoder::fullrate::frame::{decode_frame as decode_full_rate_frame, INFO_WIDTHS};
use blip25_vocoder::halfrate::dequantize::{
    encode_tone_frame_info, TONE_AMPLITUDE_EXPONENT_STEP, TONE_AMPLITUDE_PEAK,
};
use blip25_vocoder::halfrate::frame::{encode_frame, ANNEX_T};
use blip25_vocoder::halfrate::pack_natural;

use crate::{
    is_half_rate, legacy_p25_tone_frames, Session, MODE_P25, P25_CODEWORD_BYTES, PCM_SAMPLES,
};

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub(crate) struct DetectedTone {
    pub(crate) id: u8,
    pub(crate) amplitude: u8,
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

pub(crate) fn detect_tone(samples: &[i16]) -> Option<DetectedTone> {
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

pub(crate) fn encode_detected_tone(session: &mut Session, tone: DetectedTone) -> Vec<u8> {
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
