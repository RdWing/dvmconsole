// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using System.Runtime.InteropServices;

namespace DvmConsole.Vocoder;

// Owns managed session lifetime and pins caller-owned spans for each native call.
internal sealed unsafe class NativeVocoderApi : IDisposable
{
    private const uint RequiredAbiVersion = 8;
    private readonly NativeVocoderBindings bindings;
    private readonly ulong capabilities;
    private int referenceCount = 1;
    private int ownerDisposed;

    private NativeVocoderApi(NativeVocoderBindings bindings)
    {
        this.bindings = bindings;

        uint abiVersion = bindings.version();
        if (abiVersion != RequiredAbiVersion)
            throw new InvalidOperationException($"Unsupported built-in vocoder ABI version {abiVersion}; expected {RequiredAbiVersion}.");
        capabilities = bindings.getCapabilities();
        if ((capabilities & 0b1111UL) != 0b1111UL)
            throw new InvalidOperationException("The built-in vocoder does not provide all required protocol modes.");
    }
    public string? LastError
    {
        get
        {
            IntPtr pointer = bindings.error();
            return pointer == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(pointer);
        }
    }

    public static NativeVocoderApi Load(NativeVocoderLinkage linkage = NativeVocoderLinkage.Dynamic)
    {
        NativeVocoderBindings bindings = NativeVocoderBindings.Load(linkage);
        try { return new NativeVocoderApi(bindings); }
        catch { bindings.Dispose(); throw; }
    }

    public bool Supports(VocoderMode mode)
    {
        int bit = (int)mode;
        return bit is >= 0 and < 64 && (capabilities & (1UL << bit)) != 0;
    }

    public SafeVocoderSessionHandle CreateSession(VocoderMode mode)
    {
        AddReference();
        IntPtr nativeHandle = bindings.create((uint)mode);
        if (nativeHandle == IntPtr.Zero)
        {
            ReleaseReference();
            return new SafeVocoderSessionHandle();
        }
        return new SafeVocoderSessionHandle(this, nativeHandle);
    }

    public int ConfigureReceiveAudioProcessing(SafeVocoderSessionHandle session, ReceiveAudioProcessingOptions options)
    {
        using var lease = new SafeVocoderSessionLease(session);
        return bindings.configureReceiveAudioProcessing(
            lease.Handle,
            options.HighPassFilterEnabled,
            options.HighPassFrequencyHz,
            options.PeakingFilterEnabled,
            options.PeakingFrequencyHz,
            options.PeakingGainDb,
            options.CompressorEnabled,
            options.CompressorRatio,
            options.CompressorThresholdDbfs,
            options.CompressorMakeupGainDb);
    }

    public int ResetSession(SafeVocoderSessionHandle session)
    {
        using var lease = new SafeVocoderSessionLease(session);
        return bindings.reset(lease.Handle);
    }

    public int Encode(SafeVocoderSessionHandle session, ReadOnlySpan<short> samples, Span<byte> output)
    {
        using var lease = new SafeVocoderSessionLease(session);
        fixed (short* samplesPointer = samples)
        fixed (byte* outputPointer = output)
            return bindings.encode(lease.Handle, samplesPointer, (nuint)samples.Length, outputPointer, (nuint)output.Length);
    }

    public int EncodeP25SingleTone(SafeVocoderSessionHandle session, double frequencyHz, Span<byte> output)
    {
        using var lease = new SafeVocoderSessionLease(session);
        fixed (byte* outputPointer = output)
            return bindings.encodeP25SingleTone(lease.Handle, frequencyHz, outputPointer, (nuint)output.Length);
    }

    public int FlushEncode(SafeVocoderSessionHandle session, Span<byte> output)
    {
        using var lease = new SafeVocoderSessionLease(session);
        fixed (byte* outputPointer = output)
            return bindings.flush(lease.Handle, outputPointer, (nuint)output.Length);
    }

    public int Decode(SafeVocoderSessionHandle session, ReadOnlySpan<byte> input, Span<short> samples)
    {
        using var lease = new SafeVocoderSessionLease(session);
        fixed (byte* inputPointer = input)
        fixed (short* samplesPointer = samples)
            return bindings.decode(lease.Handle, inputPointer, (nuint)input.Length, samplesPointer, (nuint)samples.Length);
    }

    public int DeferReceiveProcessing(SafeVocoderSessionHandle session)
    {
        using var lease = new SafeVocoderSessionLease(session);
        return bindings.deferReceiveProcessing(lease.Handle);
    }

    public int ProcessReceivePresentation(SafeVocoderSessionHandle session, Span<short> samples)
    {
        using var lease = new SafeVocoderSessionLease(session);
        fixed (short* pointer = samples)
            return bindings.processReceivePresentation(lease.Handle, pointer, (nuint)samples.Length);
    }

    public int DecodeLost(SafeVocoderSessionHandle session, Span<short> samples)
    {
        using var lease = new SafeVocoderSessionLease(session);
        fixed (short* samplesPointer = samples)
            return bindings.decodeLost(lease.Handle, samplesPointer, (nuint)samples.Length);
    }

    public int EncodeParameters(SafeVocoderSessionHandle session, ReadOnlySpan<short> samples, Span<byte> output)
    {
        using var lease = new SafeVocoderSessionLease(session);
        fixed (short* samplesPointer = samples)
        fixed (byte* outputPointer = output)
            return bindings.encodeParameters(lease.Handle, samplesPointer, (nuint)samples.Length, outputPointer, (nuint)output.Length);
    }

    public int FlushParameters(SafeVocoderSessionHandle session, Span<byte> output)
    {
        using var lease = new SafeVocoderSessionLease(session);
        fixed (byte* outputPointer = output)
            return bindings.flushParameters(lease.Handle, outputPointer, (nuint)output.Length);
    }

    public int DecodeParameters(SafeVocoderSessionHandle session, ReadOnlySpan<byte> parameters, uint correctedErrors, bool lost, Span<short> samples)
    {
        using var lease = new SafeVocoderSessionLease(session);
        fixed (byte* parametersPointer = parameters)
        fixed (short* samplesPointer = samples)
            return bindings.decodeParameters(lease.Handle, parametersPointer, (nuint)parameters.Length, correctedErrors, lost, samplesPointer, (nuint)samples.Length);
    }

    public int ExtractParameters(VocoderMode mode, ReadOnlySpan<byte> codeword, Span<byte> parameters, out ushort correctedErrors)
    {
        fixed (byte* codewordPointer = codeword)
        fixed (byte* parametersPointer = parameters)
            return bindings.extract((uint)mode, codewordPointer, (nuint)codeword.Length, parametersPointer, (nuint)parameters.Length, out correctedErrors);
    }

    public int BuildCodeword(VocoderMode mode, ReadOnlySpan<byte> parameters, Span<byte> codeword)
    {
        fixed (byte* parametersPointer = parameters)
        fixed (byte* codewordPointer = codeword)
            return bindings.build((uint)mode, parametersPointer, (nuint)parameters.Length, codewordPointer, (nuint)codeword.Length);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref ownerDisposed, 1) == 0)
            ReleaseReference();
    }

    internal void DestroySession(IntPtr session) => bindings.destroy(session);

    internal void ReleaseReference()
    {
        if (Interlocked.Decrement(ref referenceCount) == 0)
            bindings.Dispose();
    }

    private void AddReference()
    {
        while (true)
        {
            int current = Volatile.Read(ref referenceCount);
            if (current == 0)
                throw new ObjectDisposedException(nameof(NativeVocoderApi));
            if (Interlocked.CompareExchange(ref referenceCount, current + 1, current) == current)
                return;
        }
    }


}

internal ref struct SafeVocoderSessionLease
{
    private SafeVocoderSessionHandle? session;
    private bool addedReference;

    internal SafeVocoderSessionLease(SafeVocoderSessionHandle session)
    {
        this.session = session;
        addedReference = false;
        session.DangerousAddRef(ref addedReference);
        Handle = session.DangerousGetHandle();
    }

    internal IntPtr Handle { get; }

    public void Dispose()
    {
        if (!addedReference)
            return;
        addedReference = false;
        session!.DangerousRelease();
        session = null;
    }
}

internal sealed class SafeVocoderSessionHandle : SafeHandle
{
    private NativeVocoderApi? owner;

    internal SafeVocoderSessionHandle()
        : base(IntPtr.Zero, ownsHandle: true)
    {
    }

    internal SafeVocoderSessionHandle(NativeVocoderApi owner, IntPtr handle)
        : base(IntPtr.Zero, ownsHandle: true)
    {
        this.owner = owner;
        SetHandle(handle);
    }

    public override bool IsInvalid => handle == IntPtr.Zero;

    protected override bool ReleaseHandle()
    {
        NativeVocoderApi? currentOwner = owner;
        owner = null;
        if (currentOwner is null)
            return true;

        try
        {
            currentOwner.DestroySession(handle);
            return true;
        }
        finally
        {
            currentOwner.ReleaseReference();
        }
    }
}
