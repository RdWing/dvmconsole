use blip25_vocoder::halfrate::frame::{
    decode_code_vectors, decode_frame, encode_code_vectors, encode_frame, DIBITS_PER_FRAME,
};
use blip25_vocoder::halfrate::{pack_natural, unpack_natural};

use crate::{
    HALF_RATE_CODEWORD_BYTES, HALF_RATE_PARAMETER_BYTES, MODE_DMR, MODE_NXDN, MODE_P25_PHASE2,
};

pub(crate) const A_POSITIONS: [usize; 24] = [
    0, 4, 8, 12, 16, 20, 24, 28, 32, 36, 40, 44, 48, 52, 56, 60, 64, 68, 1, 5, 9, 13, 17, 21,
];
const B_POSITIONS: [usize; 23] = [
    25, 29, 33, 37, 41, 45, 49, 53, 57, 61, 65, 69, 2, 6, 10, 14, 18, 22, 26, 30, 34, 38, 42,
];
const C_POSITIONS: [usize; 25] = [
    46, 50, 54, 58, 62, 66, 70, 3, 7, 11, 15, 19, 23, 27, 31, 35, 39, 43, 47, 51, 55, 59, 63, 67,
    71,
];

pub(crate) fn read_bit(bytes: &[u8], bit: usize) -> u32 {
    u32::from((bytes[bit / 8] >> (7 - bit % 8)) & 1)
}

pub(crate) fn write_bit(bytes: &mut [u8], bit: usize, value: u32) {
    let mask = 1u8 << (7 - bit % 8);
    if value & 1 != 0 {
        bytes[bit / 8] |= mask;
    } else {
        bytes[bit / 8] &= !mask;
    }
}

pub(crate) fn dmr_codeword_to_vectors(codeword: &[u8]) -> [u32; 4] {
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

pub(crate) fn sequential_codeword_to_vectors(codeword: &[u8]) -> [u32; 4] {
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

pub(crate) fn pack_dibits(dibits: &[u8; DIBITS_PER_FRAME]) -> [u8; HALF_RATE_CODEWORD_BYTES] {
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

pub(crate) fn natural_to_codeword(mode: u32, parameters: &[u8]) -> [u8; HALF_RATE_CODEWORD_BYTES] {
    let info = unpack_natural(parameters);
    if mode == MODE_P25_PHASE2 {
        pack_dibits(&encode_frame(&info))
    } else {
        vectors_to_codeword(mode, &encode_code_vectors(&info))
    }
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub(crate) struct HalfRateDecode {
    pub(crate) parameters: [u8; HALF_RATE_PARAMETER_BYTES],
    pub(crate) corrected_errors: u16,
    pub(crate) unrecoverable: bool,
}

pub(crate) fn codeword_to_natural(mode: u32, codeword: &[u8]) -> HalfRateDecode {
    let frame = if mode == MODE_P25_PHASE2 {
        decode_frame(&unpack_dibits(codeword))
    } else {
        decode_code_vectors(codeword_to_vectors(mode, codeword))
    };
    let unrecoverable = frame.errors[0] == u8::MAX;
    HalfRateDecode {
        parameters: pack_natural(&frame.info),
        corrected_errors: if unrecoverable {
            15
        } else {
            frame.error_total()
        },
        unrecoverable,
    }
}
