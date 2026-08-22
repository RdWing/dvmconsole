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
}

impl Session {
    pub(crate) fn new(mode: u32) -> Option<Self> {
        let mut session = Self {
            mode,
            vocoder: Vocoder::new(rate(mode)?),
            tone_converter: HalfToFullConverter::new(),
            pending_tone: None,
            flushed: false,
        };
        session.set_receive_audio_processing(ReceiveAudioProcessingOptions::default());
        Some(session)
    }

    pub(crate) fn set_receive_audio_processing(&mut self, options: ReceiveAudioProcessingOptions) {
        self.vocoder
            .set_enhancement(receive_enhancement(self.mode, options));
    }
}
