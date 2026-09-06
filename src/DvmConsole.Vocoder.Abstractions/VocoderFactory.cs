// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

namespace DvmConsole.Vocoder;

public interface IVocoderFactory
{
    IVocoderBackend Create(
        IReadOnlyDictionary<VocoderMode, ReceiveAudioProcessingOptions>? receiveAudioProcessingOptions = null);
}
