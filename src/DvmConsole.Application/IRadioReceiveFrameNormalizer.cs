// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Core.Runtime;

namespace DvmConsole.Application;

/// <summary>Transport-specific call identity normalization before route admission.</summary>
public interface IRadioReceiveFrameNormalizer
{
    IRadioMediaFrame? Normalize(IRadioMediaFrame traffic);
}
