use blip25_vocoder::enhancement::{Biquad, ClassicalConfig, Compressor, EnhancementMode};

use crate::{MODE_DMR, MODE_NXDN, MODE_P25_PHASE2};

pub(crate) const RX_OUTPUT_GAIN_DB: f32 = 9.0;
pub(crate) const RX_BOUNDARY_FADE_SAMPLES: usize = 40;
pub(crate) const RX_COMPRESSOR_ATTACK_MS: f32 = 10.0;
pub(crate) const RX_COMPRESSOR_RELEASE_MS: f32 = 250.0;

#[derive(Clone, Copy, Debug)]
pub(crate) struct ReceiveAudioProcessingOptions {
    pub(crate) high_pass_enabled: bool,
    pub(crate) high_pass_frequency_hz: f32,
    pub(crate) peaking_enabled: bool,
    pub(crate) peaking_frequency_hz: f32,
    pub(crate) peaking_gain_db: f32,
    pub(crate) compressor_enabled: bool,
    pub(crate) compressor_ratio: f32,
    pub(crate) compressor_threshold_dbfs: f32,
    pub(crate) compressor_makeup_gain_db: f32,
}

impl Default for ReceiveAudioProcessingOptions {
    fn default() -> Self {
        Self {
            high_pass_enabled: true,
            high_pass_frequency_hz: 250.0,
            peaking_enabled: true,
            peaking_frequency_hz: 2_500.0,
            peaking_gain_db: 3.0,
            compressor_enabled: false,
            compressor_ratio: 3.0,
            compressor_threshold_dbfs: -18.0,
            compressor_makeup_gain_db: 3.0,
        }
    }
}

impl ReceiveAudioProcessingOptions {
    pub(crate) fn is_valid(self) -> bool {
        self.high_pass_frequency_hz.is_finite()
            && (0.0..=500.0).contains(&self.high_pass_frequency_hz)
            && self.peaking_frequency_hz.is_finite()
            && (250.0..=3_000.0).contains(&self.peaking_frequency_hz)
            && self.peaking_gain_db.is_finite()
            && (-10.0..=10.0).contains(&self.peaking_gain_db)
            && self.compressor_ratio.is_finite()
            && (1.0..=10.0).contains(&self.compressor_ratio)
            && self.compressor_threshold_dbfs.is_finite()
            && (-40.0..=0.0).contains(&self.compressor_threshold_dbfs)
            && self.compressor_makeup_gain_db.is_finite()
            && (0.0..=10.0).contains(&self.compressor_makeup_gain_db)
    }
}

// ClassicalConfig is non-exhaustive, so external consumers must begin with its
// default value before applying the complete operator-selected table.
#[allow(clippy::field_reassign_with_default)]
pub(crate) fn receive_enhancement(
    mode: u32,
    options: ReceiveAudioProcessingOptions,
) -> EnhancementMode {
    let mut config = ClassicalConfig::default();
    config.biquads = [
        (options.high_pass_enabled && options.high_pass_frequency_hz > 0.0)
            .then(|| Biquad::high_pass(8_000.0, options.high_pass_frequency_hz, 0.707)),
        options.peaking_enabled.then(|| {
            Biquad::peaking(
                8_000.0,
                options.peaking_frequency_hz,
                1.0,
                options.peaking_gain_db,
            )
        }),
    ];
    config.compressor = options.compressor_enabled.then_some(Compressor {
        threshold_db: options.compressor_threshold_dbfs,
        ratio: options.compressor_ratio,
        attack_ms: RX_COMPRESSOR_ATTACK_MS,
        release_ms: RX_COMPRESSOR_RELEASE_MS,
        makeup_db: options.compressor_makeup_gain_db,
    });
    config.boundary_fade_samples = RX_BOUNDARY_FADE_SAMPLES;
    if matches!(mode, MODE_DMR | MODE_NXDN | MODE_P25_PHASE2) {
        config.output_gain_db = RX_OUTPUT_GAIN_DB;
    }
    EnhancementMode::Classical(config)
}
