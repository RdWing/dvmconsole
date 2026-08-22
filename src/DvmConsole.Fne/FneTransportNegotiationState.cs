#nullable enable
// SPDX-License-Identifier: AGPL-3.0-only

namespace fnecore;

internal sealed class FneTransportNegotiationState
{
    private readonly object sync = new();
    private readonly FneTransportEncryptionMode configuredMode;
    private FneTransportEncryptionMode sendMode;
    private FneTransportEncryptionMode lastSentMode;
    private FneTransportEncryptionMode? negotiatedMode;
    private bool isCryptoWrapped;
    private byte[]? presharedKey;

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
            presharedKey = key?.ToArray();
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
        byte[]? key;
        bool wrapped;
        FneTransportEncryptionMode mode;

        lock (sync)
        {
            wrapped = isCryptoWrapped;
            key = presharedKey;
            mode = negotiatedMode ?? sendMode;
            lastSentMode = mode;
            if (configuredMode == FneTransportEncryptionMode.Auto && negotiatedMode is null)
                sendMode = OtherMode(mode);
        }

        if (!wrapped)
            return message;
        if (key is null)
            throw new InvalidOperationException("Encrypted FNE transport has no preshared key.");

        return FneTransportCryptoCodec.Wrap(message, key, mode);
    }

    public byte[] Unwrap(byte[] wire, out bool wrapped)
    {
        byte[]? key;
        FneTransportEncryptionMode preferredMode;
        lock (sync)
        {
            wrapped = isCryptoWrapped;
            key = presharedKey;
            preferredMode = negotiatedMode ?? lastSentMode;
        }

        if (!wrapped)
            return wire;
        if (key is null)
            throw new InvalidOperationException("Encrypted FNE transport has no preshared key.");

        if (configuredMode != FneTransportEncryptionMode.Auto)
        {
            return FneTransportCryptoCodec.TryUnwrap(wire, key, configuredMode, out byte[] decrypted)
                ? decrypted
                : [];
        }

        FneTransportEncryptionMode alternateMode = OtherMode(preferredMode);
        foreach (FneTransportEncryptionMode mode in new[] { preferredMode, alternateMode })
        {
            if (!FneTransportCryptoCodec.TryUnwrap(wire, key, mode, out byte[] candidate) ||
                !FneTransportCryptoCodec.LooksLikeFneFrame(candidate))
            {
                continue;
            }

            lock (sync)
            {
                negotiatedMode = mode;
                sendMode = mode;
                lastSentMode = mode;
            }

            return candidate;
        }

        return [];
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
