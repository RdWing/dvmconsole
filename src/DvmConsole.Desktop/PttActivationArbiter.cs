// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

namespace DvmConsole.Desktop;

// Owns the source of the current transmit lifecycle. Diagnostic history is
// deliberately separate because the last observed input is not ownership.
internal sealed class PttActivationArbiter
{
    private sealed record Ownership(
        PttActivationSource Source,
        PttTargetScope? Scope);

    private Ownership? ownership;

    public PttActivationSource Owner
        => Volatile.Read(ref ownership)?.Source ?? PttActivationSource.None;

    public void RecordStarted(
        PttActivationSource source,
        PttTargetScope? scope = null)
    {
        if (source == PttActivationSource.None)
            throw new ArgumentOutOfRangeException(nameof(source));
        if (IsKeyboard(source) && scope is null)
            throw new ArgumentNullException(nameof(scope));
        Volatile.Write(ref ownership, new Ownership(source, scope));
    }

    public void Clear() => Volatile.Write(ref ownership, null);

    // Window and OS-global events are two capture paths for the same configured
    // keyboard binding. Either release edge for the owning scope must end TX;
    // an unrelated binding or serial source must not be able to do so.
    public bool ShouldReleaseFromInput(
        PttTargetScope scope,
        PttActivationSource source)
    {
        Ownership? current = Volatile.Read(ref ownership);
        if (current is null)
            return false;
        if (current.Source == PttActivationSource.SerialHardware ||
            source == PttActivationSource.SerialHardware)
        {
            return current.Source == source;
        }
        return IsKeyboard(current.Source) &&
               IsKeyboard(source) &&
               current.Scope == scope;
    }

    public bool TryGetKeyboardOwner(
        out PttTargetScope scope,
        out PttActivationSource source)
    {
        Ownership? current = Volatile.Read(ref ownership);
        if (current?.Scope is PttTargetScope currentScope &&
            IsKeyboard(current.Source))
        {
            scope = currentScope;
            source = current.Source;
            return true;
        }

        scope = default;
        source = PttActivationSource.None;
        return false;
    }

    public bool ShouldReleaseFromKeyboard(
        bool toggleMode,
        bool hasActiveTransmit,
        PttActivationSource requestedSource)
        => toggleMode &&
           hasActiveTransmit &&
           Owner == PttActivationSource.LocalChannelControl &&
           requestedSource is PttActivationSource.WindowLocalKeyboard or
               PttActivationSource.OsGlobalKeyboard;

    private static bool IsKeyboard(PttActivationSource source)
        => source is PttActivationSource.WindowLocalKeyboard or
            PttActivationSource.OsGlobalKeyboard;
}
