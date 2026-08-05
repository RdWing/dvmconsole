using System.Runtime.InteropServices;

namespace DvmConsole.Platform
{
    /// <summary>
    /// Dependency-free host OS detection for the DVM Console.
    ///
    /// This is intentionally a pure skeleton: no native audio or modem adapters
    /// are attached yet. Native adapter wiring (CoreAudio/ALSA/WASAPI, serial
    /// modem I/O) is a later stage and will live behind this same namespace.
    /// </summary>
    public static class PlatformInfo
    {
        /// <summary>
        /// True when running on macOS (including Mac Catalyst).
        /// </summary>
        public static bool IsMacOS => OperatingSystem.IsMacOS();

        /// <summary>
        /// True when running on Windows.
        /// </summary>
        public static bool IsWindows => OperatingSystem.IsWindows();

        /// <summary>
        /// True when running on Linux.
        /// </summary>
        public static bool IsLinux => OperatingSystem.IsLinux();

        /// <summary>
        /// Human-readable host platform name: "macOS", "Windows", "Linux" or "Unknown".
        /// </summary>
        public static string Description
        {
            get
            {
                if (IsMacOS)
                {
                    return "macOS";
                }

                if (IsWindows)
                {
                    return "Windows";
                }

                if (IsLinux)
                {
                    return "Linux";
                }

                return "Unknown";
            }
        }
    }
}
