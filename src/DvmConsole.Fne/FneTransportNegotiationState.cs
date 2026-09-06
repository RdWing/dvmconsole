// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

#nullable enable

namespace fnecore;

internal sealed class FneTransportNegotiationState : IDisposable
{
    private readonly object sync = new();
    private readonly FneTransportEncryptionMode configuredMode;
    private FneTransportEncryptionMode sendMode;
    private FneTransportEncryptionMode lastSentMode;
    private FneTransportEncryptionMode? negotiatedMode;
    private bool isCryptoWrapped;
    private FneTransportCryptoContext? crypto;

    public FneTransportNegotiationState(FneTransportEncryptionMode configuredMode)
    {
        this.configuredMode = configuredMode;
        sendMode = InitialMode(configuredMode);
        lastSentMode = sendMode;
    }

    public FneTransportEncryptionMode ConfiguredMode => configuredMode;

    public FneTransportEncryptionMode? NegotiatedMode
    {
        get
        {
            lock (sync)
                return negotiatedMode;
        }
    }

    public void SetPresharedKey(byte[]? key)
    {
        lock (sync)
        {
            crypto?.Dispose();
            crypto = key is null ? null : new FneTransportCryptoContext(key);
            isCryptoWrapped = key is not null;
            sendMode = InitialMode(configuredMode);
            lastSentMode = sendMode;
            negotiatedMode = configuredMode == FneTransportEncryptionMode.Auto
                ? null
                : configuredMode;
        }
    }

    public byte[] WrapForSend(byte[] message)
    {
        lock (sync)
        {
            if (!isCryptoWrapped)
                return message;
            FneTransportCryptoContext context = crypto ??
                throw new InvalidOperationException("Encrypted FNE transport has no preshared key.");
            FneTransportEncryptionMode mode = negotiatedMode ?? sendMode;
            lastSentMode = mode;
            if (configuredMode == FneTransportEncryptionMode.Auto && negotiatedMode is null)
                sendMode = OtherMode(mode);
            return context.Wrap(message, mode);
        }
    }

    public byte[] Unwrap(byte[] wire, out bool wrapped)
    {
        lock (sync)
        {
            wrapped = isCryptoWrapped;
            if (!wrapped)
                return wire;
            FneTransportCryptoContext context = crypto ??
                throw new InvalidOperationException("Encrypted FNE transport has no preshared key.");
            if (configuredMode != FneTransportEncryptionMode.Auto)
            {
                return context.TryUnwrap(wire, configuredMode, out byte[] decrypted)
                    ? decrypted
                    : [];
            }

            FneTransportEncryptionMode preferredMode = negotiatedMode ?? lastSentMode;
            if (TryUnwrapAuto(context, wire, preferredMode, out byte[] candidate, out FneTransportEncryptionMode mode))
            {
                negotiatedMode = mode;
                sendMode = mode;
                lastSentMode = mode;
                return candidate;
            }
            return [];
        }
    }

    private static bool TryUnwrapAuto(
        FneTransportCryptoContext context,
        byte[] wire,
        FneTransportEncryptionMode preferredMode,
        out byte[] candidate,
        out FneTransportEncryptionMode acceptedMode)
    {
        if (context.TryUnwrap(wire, preferredMode, out candidate) &&
            FneTransportCryptoCodec.LooksLikeFneFrame(candidate))
        {
            acceptedMode = preferredMode;
            return true;
        }

        FneTransportEncryptionMode alternateMode = OtherMode(preferredMode);
        if (context.TryUnwrap(wire, alternateMode, out candidate) &&
            FneTransportCryptoCodec.LooksLikeFneFrame(candidate))
        {
            acceptedMode = alternateMode;
            return true;
        }

        acceptedMode = default;
        candidate = [];
        return false;
    }

    public void Dispose()
    {
        lock (sync)
        {
            crypto?.Dispose();
            crypto = null;
            isCryptoWrapped = false;
        }
    }

    internal static FneTransportEncryptionMode InitialMode(FneTransportEncryptionMode mode)
        => mode == FneTransportEncryptionMode.Cbc
            ? FneTransportEncryptionMode.Cbc
            : FneTransportEncryptionMode.Ecb;

    internal static FneTransportEncryptionMode OtherMode(FneTransportEncryptionMode mode)
        => mode == FneTransportEncryptionMode.Cbc
            ? FneTransportEncryptionMode.Ecb
            : FneTransportEncryptionMode.Cbc;
}
