using System.Security.Cryptography;
using DvmConsole.Vocoder;

namespace DvmConsole.Media;

public static class NxdnPrivacyAlgorithms
{
    public const byte Ehr = 0x01;
    public const byte Des = 0x02;
    public const byte Aes256 = 0x03;
    public const int MessageIndicatorBytes = 8;

    public static int KeyBytes(byte algorithmId) => algorithmId switch
    {
        Ehr => 2,
        Des => 8,
        Aes256 => 32,
        _ => throw new ArgumentOutOfRangeException(nameof(algorithmId), "Unsupported NXDN privacy algorithm.")
    };
}

public sealed class NxdnPrivacyOptions
{
    public NxdnPrivacyOptions(
        byte algorithmId,
        byte keyId,
        ReadOnlyMemory<byte> key,
        ReadOnlyMemory<byte> messageIndicator = default)
    {
        int expected = NxdnPrivacyAlgorithms.KeyBytes(algorithmId);
        if (keyId is 0 or > 63)
            throw new ArgumentOutOfRangeException(nameof(keyId), "NXDN key IDs are between 1 and 63.");
        if (key.Length != expected)
            throw new ArgumentException($"NXDN cipher type {algorithmId} requires exactly {expected} key bytes.", nameof(key));
        if (algorithmId == NxdnPrivacyAlgorithms.Ehr)
        {
            ushort seed = (ushort)((key.Span[0] << 8) | key.Span[1]);
            if ((seed & 0x7FFF) == 0)
                throw new ArgumentException("NXDN EHR requires a non-zero 15-bit seed.", nameof(key));
        }
        else if (messageIndicator.Length != NxdnPrivacyAlgorithms.MessageIndicatorBytes)
        {
            throw new ArgumentException("NXDN DES/AES privacy requires an 8-byte message indicator.", nameof(messageIndicator));
        }
        AlgorithmId = algorithmId;
        KeyId = keyId;
        Key = key.ToArray();
        MessageIndicator = algorithmId == NxdnPrivacyAlgorithms.Ehr ? [] : messageIndicator.ToArray();
    }

    public byte AlgorithmId { get; }
    public byte KeyId { get; }
    public ReadOnlyMemory<byte> Key { get; }
    public ReadOnlyMemory<byte> MessageIndicator { get; }

    public static NxdnPrivacyOptions CreateRandom(byte algorithmId, byte keyId, ReadOnlyMemory<byte> key)
    {
        byte[] mi = algorithmId == NxdnPrivacyAlgorithms.Ehr
            ? []
            : RandomNumberGenerator.GetBytes(NxdnPrivacyAlgorithms.MessageIndicatorBytes);
        return new NxdnPrivacyOptions(algorithmId, keyId, key, mi);
    }
}

// Symmetric transform over the 49 natural AMBE parameter bits. The native
// adapter handles NXDN FEC/interleave after this operation.
public sealed class NxdnPrivacyProcessor : IDisposable
{
    private readonly IHalfRateVocoderSession vocoder;
    private readonly byte algorithmId;
    private readonly byte[] key;
    private byte[] messageIndicator;
    private byte[] stream = [];
    private ICryptoTransform? encryptor;
    private SymmetricAlgorithm? cipher;
    private byte[] register = [];
    private ushort ehrSeed;
    private ushort ehrState;
    private int codewordIndex;
    private int streamBit;
    private bool disposed;

    public NxdnPrivacyProcessor(IHalfRateVocoderSession vocoder, NxdnPrivacyOptions options)
    {
        this.vocoder = vocoder ?? throw new ArgumentNullException(nameof(vocoder));
        ArgumentNullException.ThrowIfNull(options);
        algorithmId = options.AlgorithmId;
        key = options.Key.ToArray();
        messageIndicator = options.MessageIndicator.ToArray();
        Prepare();
    }

    public ReadOnlyMemory<byte> MessageIndicator => messageIndicator;

    public void ResetMessageIndicator(ReadOnlySpan<byte> nextMessageIndicator)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (algorithmId == NxdnPrivacyAlgorithms.Ehr)
            throw new InvalidOperationException("NXDN EHR does not use a message indicator.");
        if (nextMessageIndicator.Length != NxdnPrivacyAlgorithms.MessageIndicatorBytes)
            throw new ArgumentException("NXDN DES/AES privacy requires an 8-byte message indicator.", nameof(nextMessageIndicator));
        encryptor?.Dispose();
        encryptor = null;
        cipher?.Dispose();
        cipher = null;
        if (messageIndicator.Length > 0)
            CryptographicOperations.ZeroMemory(messageIndicator);
        if (register.Length > 0)
            CryptographicOperations.ZeroMemory(register);
        if (stream.Length > 0)
            CryptographicOperations.ZeroMemory(stream);
        messageIndicator = nextMessageIndicator.ToArray();
        register = [];
        stream = [];
        streamBit = 0;
        codewordIndex = 0;
        Prepare();
    }

    public int ProcessCodeword(ReadOnlySpan<byte> input, Span<byte> output)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (input.Length != VocoderFrameSizes.HalfRateCodewordBytes ||
            output.Length < VocoderFrameSizes.HalfRateCodewordBytes)
            throw new ArgumentException("NXDN privacy requires one 9-byte AMBE codeword.");
        Span<byte> parameters = stackalloc byte[VocoderFrameSizes.HalfRateParameterBytes];
        ExtractAndProcessParameters(input, parameters);
        vocoder.BuildCodeword(parameters, output);
        return VocoderFrameSizes.HalfRateCodewordBytes;
    }

    public HalfRateFecStatus ExtractAndProcessParameters(
        ReadOnlySpan<byte> input,
        Span<byte> parameters)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (input.Length != VocoderFrameSizes.HalfRateCodewordBytes)
            throw new ArgumentException("NXDN privacy requires one 9-byte AMBE codeword.", nameof(input));
        if (parameters.Length != VocoderFrameSizes.HalfRateParameterBytes)
            throw new ArgumentException("NXDN privacy requires seven bytes of AMBE parameters.", nameof(parameters));

        HalfRateFecStatus status = vocoder.ExtractParametersWithStatus(input, parameters);
        ProcessParameters(parameters);
        return status;
    }

    public void ProcessParameters(Span<byte> parameters)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (parameters.Length != VocoderFrameSizes.HalfRateParameterBytes)
            throw new ArgumentException("NXDN privacy requires seven bytes of AMBE parameters.", nameof(parameters));
        if (algorithmId == NxdnPrivacyAlgorithms.Ehr && codewordIndex % 16 == 0)
            ehrState = ehrSeed;

        if (algorithmId == NxdnPrivacyAlgorithms.Ehr)
        {
            for (int bit = 0; bit < 49; bit++)
            {
                bool keyBit = (ehrState & 1) != 0;
                bool feedback = (((ehrState >> 1) ^ ehrState) & 1) != 0;
                ehrState = (ushort)(((ehrState >> 1) | (feedback ? 0x4000 : 0)) & 0x7FFF);
                if (keyBit)
                    parameters[bit / 8] ^= (byte)(0x80 >> (bit % 8));
            }
        }
        else
        {
            for (int bit = 0; bit < 49; bit++)
            {
                if (streamBit >= stream.Length * 8)
                    FillStreamBlock();
                if ((stream[streamBit / 8] & (0x80 >> (streamBit % 8))) != 0)
                    parameters[bit / 8] ^= (byte)(0x80 >> (bit % 8));
                streamBit++;
            }
        }
        parameters[^1] &= 0x80;
        codewordIndex++;
    }

    public void Dispose()
    {
        if (disposed)
            return;
        encryptor?.Dispose();
        cipher?.Dispose();
        CryptographicOperations.ZeroMemory(key);
        CryptographicOperations.ZeroMemory(messageIndicator);
        CryptographicOperations.ZeroMemory(register);
        CryptographicOperations.ZeroMemory(stream);
        disposed = true;
    }

    private void Prepare()
    {
        if (algorithmId == NxdnPrivacyAlgorithms.Ehr)
        {
            ehrSeed = (ushort)(((key[0] << 8) | key[1]) & 0x7FFF);
            ehrState = ehrSeed;
            return;
        }

        if (algorithmId == NxdnPrivacyAlgorithms.Des)
        {
            var des = DES.Create();
            des.Mode = CipherMode.ECB;
            des.Padding = PaddingMode.None;
            try { des.Key = key.ToArray(); }
            catch (CryptographicException exception)
            {
                des.Dispose();
                throw new ArgumentException("The configured NXDN DES key is weak or invalid for DES.", nameof(key), exception);
            }
            cipher = des;
            register = messageIndicator.ToArray();
        }
        else
        {
            var aes = Aes.Create();
            aes.Mode = CipherMode.ECB;
            aes.Padding = PaddingMode.None;
            aes.KeySize = 256;
            aes.Key = key.ToArray();
            cipher = aes;
            register = ExpandAesIv(messageIndicator);
        }
        encryptor = cipher.CreateEncryptor();
        // NXDN discards the first DES/AES OFB block.
        AdvanceRegister();
        FillStreamBlock();
    }

    private void FillStreamBlock()
    {
        AdvanceRegister();
        if (stream.Length > 0)
            CryptographicOperations.ZeroMemory(stream);
        stream = register.ToArray();
        streamBit = 0;
    }

    private void AdvanceRegister()
    {
        byte[] next = new byte[register.Length];
        encryptor!.TransformBlock(register, 0, register.Length, next, 0);
        CryptographicOperations.ZeroMemory(register);
        register = next;
    }

    private static byte[] ExpandAesIv(ReadOnlySpan<byte> mi)
    {
        byte[] iv = new byte[16];
        mi.CopyTo(iv);
        ulong lfsr = 0;
        for (int index = 0; index < 8; index++)
            lfsr = (lfsr << 8) | mi[index];
        for (int index = 64; index < 128; index++)
        {
            ulong bit = ((lfsr >> 63) ^ (lfsr >> 61) ^ (lfsr >> 45) ^
                (lfsr >> 37) ^ (lfsr >> 26) ^ (lfsr >> 14)) & 1;
            lfsr = (lfsr << 1) | bit;
            iv[index / 8] = (byte)((iv[index / 8] << 1) | (byte)bit);
        }
        return iv;
    }
}
