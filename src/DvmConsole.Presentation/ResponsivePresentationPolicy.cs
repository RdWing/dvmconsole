namespace DvmConsole.Presentation;

public enum ConsoleRendererPreference
{
    Cards,
    List
}

public enum ResponsivePresentationState
{
    Wide,
    DesktopCompact,
    Narrow,
    Phone
}

public readonly record struct ResponsivePresentation(
    ResponsivePresentationState State,
    bool TightPhone,
    ConsoleRendererPreference EffectiveRenderer);

public static class ResponsivePresentationPolicy
{
    public const double WideMinimum = 1120;
    public const double DesktopCompactMinimum = 880;
    public const double NarrowMinimum = 600;
    public const double TightPhoneMaximum = 400;

    public static ResponsivePresentation Resolve(
        double logicalWidth,
        ConsoleRendererPreference savedPreference,
        bool mobileHost = false)
    {
        double width = double.IsFinite(logicalWidth) ? Math.Max(0, logicalWidth) : 0;
        ResponsivePresentationState state = width switch
        {
            >= WideMinimum => ResponsivePresentationState.Wide,
            >= DesktopCompactMinimum => ResponsivePresentationState.DesktopCompact,
            >= NarrowMinimum => ResponsivePresentationState.Narrow,
            _ => ResponsivePresentationState.Phone
        };
        ConsoleRendererPreference renderer = mobileHost || width < NarrowMinimum
            ? ConsoleRendererPreference.List
            : savedPreference;
        return new ResponsivePresentation(
            state,
            width < TightPhoneMaximum,
            renderer);
    }
}
