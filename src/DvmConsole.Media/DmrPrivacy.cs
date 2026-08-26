using System.Security.Cryptography;
using DvmConsole.Vocoder;

namespace DvmConsole.Media;

public static class DmrPrivacyAlgorithms
{
    public const byte Arc4 = 0x01;
    public const byte DesOfb = 0x02;
    public const byte Aes256 = 0x05;
    public const byte FeatureId = 0x10;
    public const int MessageIndicatorBytes = 4;

    public static int KeyBytes(byte algorithmId) => algorithmId switch
    {
        Arc4 => 5,
        DesOfb => 8,
        Aes256 => 32,
        _ => throw new ArgumentOutOfRangeException(nameof(algorithmId), "Unsupported DMR privacy algorithm.")
    };
}

public sealed class DmrPrivacyOptions
{
    public DmrPrivacyOptions(
        byte algorithmId,
        byte keyId,
        ReadOnlyMemory<byte> key,
        ReadOnlyMemory<byte> messageIndicator)
    {
        int expectedKeyBytes = DmrPrivacyAlgorithms.KeyBytes(algorithmId);
        if (keyId == 0)
            throw new ArgumentOutOfRangeException(nameof(keyId));
        if (key.Length != expectedKeyBytes)
        {
            throw new ArgumentException(
                $"DMR algorithm 0x{algorithmId:X2} requires exactly {expectedKeyBytes} bytes of key material.",
                nameof(key));
        }
        if (messageIndicator.Length != DmrPrivacyAlgorithms.MessageIndicatorBytes)
            throw new ArgumentException("DMR privacy requires a 4-byte message indicator.", nameof(messageIndicator));

        AlgorithmId = algorithmId;
        KeyId = keyId;
        Key = key.ToArray();
        MessageIndicator = messageIndicator.ToArray();
    }

    public byte AlgorithmId { get; }
    public byte KeyId { get; }
    public ReadOnlyMemory<byte> Key { get; }
    public ReadOnlyMemory<byte> MessageIndicator { get; }

    public static DmrPrivacyOptions CreateRandom(byte algorithmId, byte keyId, ReadOnlyMemory<byte> key)
    {
        byte[] messageIndicator = new byte[DmrPrivacyAlgorithms.MessageIndicatorBytes];
        RandomNumberGenerator.Fill(messageIndicator);
        return new DmrPrivacyOptions(algorithmId, keyId, key, messageIndicator);
    }
}

// Symmetric DMR Association privacy transform. The 49 significant AMBE bits
// occupy the high bits of the seven-byte natural parameter representation;
// applying a seven-byte stream preserves the protocol's seven padding-bit
// advance between voice frames.
public sealed class DmrPrivacyProcessor : IDisposable
{
    private const int CodewordsPerPrivacyCycle = 18;
    private const int ParameterBytes = VocoderFrameSizes.HalfRateParameterBytes;
    private const int Arc4DiscardBytes = 256;
    private readonly IHalfRateVocoderSession vocoder;
    private readonly byte algorithmId;
    private readonly byte[] key;
    private readonly byte[] messageIndicator;
    private byte[] keystream = [];
    private int codewordIndex;
    private bool disposed;

    public DmrPrivacyProcessor(IHalfRateVocoderSession vocoder, DmrPrivacyOptions options)
    {
        this.vocoder = vocoder ?? throw new ArgumentNullException(nameof(vocoder));
        ArgumentNullException.ThrowIfNull(options);
        algorithmId = options.AlgorithmId;
        key = options.Key.ToArray();
        messageIndicator = options.MessageIndicator.ToArray();
        PrepareCycle();
    }

    public ReadOnlyMemory<byte> MessageIndicator => messageIndicator;

    public byte[] GetNextMessageIndicator()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        return CalculateNextMessageIndicator();
    }

    public int ProcessCodeword(ReadOnlySpan<byte> input, Span<byte> output)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (input.Length != VocoderFrameSizes.HalfRateCodewordBytes ||
            output.Length < VocoderFrameSizes.HalfRateCodewordBytes)
            throw new ArgumentException("DMR privacy requires one 9-byte AMBE codeword.");

        Span<byte> parameters = stackalloc byte[ParameterBytes];
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
            throw new ArgumentException("DMR privacy requires one 9-byte AMBE codeword.", nameof(input));
        if (parameters.Length != ParameterBytes)
            throw new ArgumentException("DMR privacy requires seven bytes of AMBE parameters.", nameof(parameters));

        HalfRateFecStatus status = vocoder.ExtractParametersWithStatus(input, parameters);
        ProcessParameters(parameters);
        return status;
    }

    public void SkipCodewords(int count)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (count < 0)
            throw new ArgumentOutOfRangeException(nameof(count));
        Span<byte> discarded = stackalloc byte[ParameterBytes];
        for (int index = 0; index < count; index++)
        {
            discarded.Clear();
            ProcessParameters(discarded);
        }
    }

    public void ProcessParameters(Span<byte> parameters)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (parameters.Length != ParameterBytes)
            throw new ArgumentException("DMR privacy requires seven bytes of AMBE parameters.", nameof(parameters));

        int offset = codewordIndex * ParameterBytes;
        for (int index = 0; index < ParameterBytes; index++)
            parameters[index] ^= keystream[offset + index];
        // Only the most-significant bit of the final byte is an AMBE bit. The
        // low seven bits are padding and must remain canonical for the native
        // packer even though their stream positions are consumed.
        parameters[^1] &= 0x80;

        codewordIndex++;
        if (codewordIndex == CodewordsPerPrivacyCycle)
        {
            AdvanceMessageIndicator();
            PrepareCycle();
        }
    }

    public void Dispose()
    {
        if (disposed)
            return;
        CryptographicOperations.ZeroMemory(key);
        CryptographicOperations.ZeroMemory(keystream);
        CryptographicOperations.ZeroMemory(messageIndicator);
        disposed = true;
    }

    private void PrepareCycle()
    {
        codewordIndex = 0;
        if (keystream.Length > 0)
            CryptographicOperations.ZeroMemory(keystream);
        keystream = algorithmId switch
        {
            DmrPrivacyAlgorithms.Arc4 => CreateArc4Keystream(key, messageIndicator),
            DmrPrivacyAlgorithms.DesOfb => CreateDesKeystream(key, ExpandDesIv(messageIndicator)),
            DmrPrivacyAlgorithms.Aes256 => CreateAesKeystream(key, ExpandAesIv(messageIndicator)),
            _ => throw new InvalidOperationException("Unsupported DMR privacy algorithm.")
        };
    }

    private void AdvanceMessageIndicator()
    {
        byte[] next = CalculateNextMessageIndicator();
        next.CopyTo(messageIndicator, 0);
    }

    private byte[] CalculateNextMessageIndicator()
    {
        return algorithmId switch
        {
            DmrPrivacyAlgorithms.Arc4 => CycleArc4Mi(messageIndicator),
            DmrPrivacyAlgorithms.DesOfb => ExpandDesIv(messageIndicator)[4..8],
            DmrPrivacyAlgorithms.Aes256 => ExpandAesIv(messageIndicator)[4..8],
            _ => throw new InvalidOperationException("Unsupported DMR privacy algorithm.")
        };
    }

    private static byte[] CreateArc4Keystream(ReadOnlySpan<byte> key, ReadOnlySpan<byte> mi)
    {
        byte[] combined = new byte[key.Length + mi.Length];
        key.CopyTo(combined);
        mi.CopyTo(combined.AsSpan(key.Length));
        byte[] state = Enumerable.Range(0, 256).Select(value => (byte)value).ToArray();
        int j = 0;
        for (int i = 0; i < state.Length; i++)
        {
            j = (j + state[i] + combined[i % combined.Length]) & 0xFF;
            (state[i], state[j]) = (state[j], state[i]);
        }

        byte[] output = new byte[CodewordsPerPrivacyCycle * ParameterBytes];
        int x = 0;
        j = 0;
        int generatedBytes = Arc4DiscardBytes + output.Length;
        for (int index = 0; index < generatedBytes; index++)
        {
            x = (x + 1) & 0xFF;
            j = (j + state[x]) & 0xFF;
            (state[x], state[j]) = (state[j], state[x]);
            if (index >= Arc4DiscardBytes)
                output[index - Arc4DiscardBytes] = state[(state[x] + state[j]) & 0xFF];
        }
        CryptographicOperations.ZeroMemory(combined);
        CryptographicOperations.ZeroMemory(state);
        return output;
    }

    private static byte[] CreateDesKeystream(ReadOnlySpan<byte> key, ReadOnlySpan<byte> iv)
    {
        using DES des = DES.Create();
        des.Mode = CipherMode.ECB;
        des.Padding = PaddingMode.None;
        try
        {
            des.Key = key.ToArray();
        }
        catch (CryptographicException exception)
        {
            throw new ArgumentException("The configured DMR DES key is weak or invalid for DES.", nameof(key), exception);
        }
        return CreateOfbKeystream(des, iv, discardBytes: 8);
    }

    private static byte[] CreateAesKeystream(ReadOnlySpan<byte> key, ReadOnlySpan<byte> iv)
    {
        using Aes aes = Aes.Create();
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;
        aes.KeySize = 256;
        aes.Key = key.ToArray();
        return CreateOfbKeystream(aes, iv, discardBytes: 16);
    }

    private static byte[] CreateOfbKeystream(
        SymmetricAlgorithm algorithm,
        ReadOnlySpan<byte> iv,
        int discardBytes)
    {
        int blockBytes = algorithm.BlockSize / 8;
        int requiredBytes = CodewordsPerPrivacyCycle * ParameterBytes;
        int totalBytes = discardBytes + requiredBytes;
        int blocks = (totalBytes + blockBytes - 1) / blockBytes;
        byte[] register = iv.ToArray();
        byte[] generated = new byte[blocks * blockBytes];
        using ICryptoTransform encryptor = algorithm.CreateEncryptor();
        for (int block = 0; block < blocks; block++)
        {
            encryptor.TransformBlock(register, 0, blockBytes, generated, block * blockBytes);
            generated.AsSpan(block * blockBytes, blockBytes).CopyTo(register);
        }

        byte[] output = generated.AsSpan(discardBytes, requiredBytes).ToArray();
        CryptographicOperations.ZeroMemory(generated);
        CryptographicOperations.ZeroMemory(register);
        return output;
    }

    private static byte[] ExpandDesIv(ReadOnlySpan<byte> mi)
    {
        ulong lfsr = ReadUInt32BigEndian(mi);
        for (int count = 0; count < 32; count++)
        {
            ulong bit = ((lfsr >> 31) ^ (lfsr >> 21) ^ (lfsr >> 1) ^ lfsr) & 1;
            lfsr = (lfsr << 1) | bit;
        }
        byte[] iv = new byte[8];
        for (int index = 0; index < iv.Length; index++)
            iv[index] = (byte)(lfsr >> (56 - index * 8));
        return iv;
    }

    private static byte[] ExpandAesIv(ReadOnlySpan<byte> mi)
    {
        byte[] iv = new byte[16];
        mi.CopyTo(iv);
        ulong lfsr = ReadUInt32BigEndian(mi);
        for (int bitIndex = 32; bitIndex < 128; bitIndex++)
        {
            ulong bit = ((lfsr >> 31) ^ (lfsr >> 21) ^ (lfsr >> 1) ^ lfsr) & 1;
            lfsr = (lfsr << 1) | bit;
            iv[bitIndex / 8] = (byte)((iv[bitIndex / 8] << 1) | (int)bit);
        }
        return iv;
    }

    private static byte[] CycleArc4Mi(ReadOnlySpan<byte> mi)
    {
        ulong lfsr = ReadUInt32BigEndian(mi);
        for (int count = 0; count < 32; count++)
        {
            ulong bit = ((lfsr >> 31) ^ (lfsr >> 3) ^ (lfsr >> 1)) & 1;
            lfsr = ((lfsr << 1) | bit) & 0xFFFFFFFF;
        }
        return
        [
            (byte)(lfsr >> 24),
            (byte)(lfsr >> 16),
            (byte)(lfsr >> 8),
            (byte)lfsr
        ];
    }

    private static uint ReadUInt32BigEndian(ReadOnlySpan<byte> value)
        => (uint)(value[0] << 24 | value[1] << 16 | value[2] << 8 | value[3]);
}
