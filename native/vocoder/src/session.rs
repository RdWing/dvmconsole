// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

use blip25_vocoder::enhancement::{apply, EnhancementMode, EnhancementState};
use blip25_vocoder::rate_conversion::HalfToFullConverter;
use blip25_vocoder::vocoder::Vocoder;

use crate::processing::{receive_enhancement, ReceiveAudioProcessingOptions};
use crate::rate;
use crate::tone::DetectedTone;

#[repr(C)]
pub struct Session {
    pub(crate) mode: u32,
    pub(crate) vocoder: Vocoder,
    pub(crate) tone_converter: HalfToFullConverter,
    pub(crate) pending_tone: Option<DetectedTone>,
    pub(crate) flushed: bool,
    receive_options: ReceiveAudioProcessingOptions,
    deferred_processing: bool,
    presentation_mode: EnhancementMode,
    pub(crate) presentation_state: EnhancementState,
}

impl Session {
    pub(crate) fn new(mode: u32) -> Option<Self> {
        let mut session = Self {
            mode,
            vocoder: Vocoder::new(rate(mode)?),
            tone_converter: HalfToFullConverter::new(),
            pending_tone: None,
            flushed: false,
            receive_options: ReceiveAudioProcessingOptions::default(),
            deferred_processing: false,
            presentation_mode: EnhancementMode::None,
            presentation_state: EnhancementState::default(),
        };
        session.set_receive_audio_processing(ReceiveAudioProcessingOptions::default());
        Some(session)
    }

    pub(crate) fn set_receive_audio_processing(&mut self, options: ReceiveAudioProcessingOptions) {
        self.receive_options = options;
        self.presentation_state = EnhancementState::default();
        let mut decode_options = options;
        if self.deferred_processing {
            decode_options.high_pass_enabled = false;
            decode_options.peaking_enabled = false;
            decode_options.compressor_enabled = false;
        }
        self.vocoder
            .set_enhancement(receive_enhancement(self.mode, decode_options));
        self.presentation_mode = receive_enhancement(self.mode, options);
        if let EnhancementMode::Classical(ref mut config) = self.presentation_mode {
            // Concealment boundary smoothing already ran in the decoder.
            config.boundary_fade_samples = 0;
            config.output_gain_db = 0.0;
        }
    }

    pub(crate) fn defer_receive_processing(&mut self) {
        self.deferred_processing = true;
        self.set_receive_audio_processing(self.receive_options);
    }

    pub(crate) fn process_presentation(&mut self, samples: &mut [i16]) {
        if self.deferred_processing {
            apply(
                &self.presentation_mode,
                &mut self.presentation_state,
                samples,
                8_000.0,
                true,
            );
        }
    }
}
