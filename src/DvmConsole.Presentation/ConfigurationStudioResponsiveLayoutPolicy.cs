// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

namespace DvmConsole.Presentation;

internal readonly record struct ConfigurationStudioResponsiveLayout(
    bool UsePhoneChannelList,
    bool StackInspector,
    bool UseTouchTargets,
    double PreviewMaximumHeight);

internal static class ConfigurationStudioResponsiveLayoutPolicy
{
    internal const double PhoneChannelListWidth = 600;
    internal const double SideInspectorWidth = 1180;
    internal const double TouchTargetWidth = 400;

    public static ConfigurationStudioResponsiveLayout Evaluate(double width, double height)
    {
        bool phoneList = width is > 0 and < PhoneChannelListWidth;
        bool stackInspector = width is > 0 and < SideInspectorWidth;
        bool touchTargets = width is > 0 and < TouchTargetWidth;
        return new ConfigurationStudioResponsiveLayout(
            phoneList,
            stackInspector,
            touchTargets,
            phoneList ? Math.Max(120, height * 0.4) : double.PositiveInfinity);
    }
}
