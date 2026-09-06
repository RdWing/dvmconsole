// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Application;
using DvmConsole.Core.Settings;
using DvmConsole.FneClient;

namespace DvmConsole.Desktop;

internal static class ReceiveJitterBufferPolicy
{
    public static ReceiveJitterBufferConfiguration GetConfiguration(
        FneTrafficProtocol protocol,
        RxJitterBufferSetting settings)
        => ReceiveJitterBufferConfigurationPolicy.GetConfiguration(
            FneReceiveWorkQueueAdapter.ToRadioProtocol(protocol),
            settings);
}
