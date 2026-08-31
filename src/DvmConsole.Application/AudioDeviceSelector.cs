using DvmConsole.Audio;

namespace DvmConsole.Application;

internal sealed record AudioDeviceSelection(
    AudioDeviceInfo Device,
    bool FollowsSystemDefault);

// Keeps the operator's route policy separate from the physical endpoint that
// currently satisfies it. A missing fixed device temporarily follows the
// system default so it can be reconsidered when the device topology changes.
internal static class AudioDeviceSelector
{
    public static bool HasSpecificRequest(string? requestedDeviceId)
        => !string.IsNullOrWhiteSpace(requestedDeviceId) &&
            !requestedDeviceId.Equals("default", StringComparison.OrdinalIgnoreCase);

    public static AudioDeviceSelection Select(
        IReadOnlyList<AudioDeviceInfo> devices,
        AudioDirection direction,
        string? requestedDeviceId)
    {
        ArgumentNullException.ThrowIfNull(devices);

        bool hasSpecificRequest = HasSpecificRequest(requestedDeviceId);
        AudioDeviceInfo? requested = hasSpecificRequest
            ? devices.FirstOrDefault(device =>
                device.Direction == direction &&
                device.Id.Equals(requestedDeviceId, StringComparison.OrdinalIgnoreCase))
            : null;
        if (requested is not null)
            return new AudioDeviceSelection(requested, FollowsSystemDefault: false);

        AudioDeviceInfo fallback = devices.FirstOrDefault(device =>
                device.Direction == direction && device.IsDefault)
            ?? devices.FirstOrDefault(device => device.Direction == direction)
            ?? throw new InvalidOperationException(
                $"No {direction.ToString().ToLowerInvariant()} audio device is available.");
        return new AudioDeviceSelection(fallback, FollowsSystemDefault: true);
    }
}
