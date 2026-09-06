// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using System.Buffers;
using System.Text;

namespace DvmConsole.Core.IO;

public static class ManagedResourceLimits
{
    public const int SettingsBytes = 4 * 1024 * 1024;
    public const int ConfigurationYamlBytes = 8 * 1024 * 1024;
    public const int ConfigurationCompanionBytes = 16 * 1024 * 1024;
    public const int ImageBytes = 25 * 1024 * 1024;
    public const int AudioBytes = 100 * 1024 * 1024;
    public const int ManagedMetadataBytes = 16 * 1024 * 1024;
}

public static class BoundedResourceReader
{
    public static string ReadUtf8File(string path, int maximumBytes, string resourceName)
    {
        using FileStream stream = File.OpenRead(path);
        return ReadUtf8(stream, maximumBytes, resourceName);
    }

    public static byte[] ReadFile(string path, int maximumBytes, string resourceName)
    {
        using FileStream stream = File.OpenRead(path);
        return ReadBytes(stream, maximumBytes, resourceName);
    }

    public static string ReadUtf8(Stream source, int maximumBytes, string resourceName)
    {
        byte[] bytes = ReadBytes(source, maximumBytes, resourceName);
        ReadOnlySpan<byte> content = bytes;
        if (content.StartsWith(Encoding.UTF8.Preamble))
            content = content[Encoding.UTF8.Preamble.Length..];
        return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
            .GetString(content);
    }

    public static byte[] ReadBytes(Stream source, int maximumBytes, string resourceName)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceName);
        if (!source.CanRead)
            throw new ArgumentException("The resource source must be readable.", nameof(source));
        if (source.CanSeek && source.Length - source.Position > maximumBytes)
            throw TooLarge(resourceName, maximumBytes);

        byte[] rented = ArrayPool<byte>.Shared.Rent(Math.Min(maximumBytes + 1, 64 * 1024));
        try
        {
            using var destination = new MemoryStream(Math.Min(maximumBytes, 64 * 1024));
            int total = 0;
            while (true)
            {
                int remaining = maximumBytes - total;
                int requested = Math.Min(rented.Length, remaining + 1);
                int read = source.Read(rented, 0, requested);
                if (read == 0)
                    return destination.ToArray();
                total = checked(total + read);
                if (total > maximumBytes)
                    throw TooLarge(resourceName, maximumBytes);
                destination.Write(rented, 0, read);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    public static async ValueTask<byte[]> ReadBytesAsync(
        Stream source,
        int maximumBytes,
        string resourceName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceName);
        if (!source.CanRead)
            throw new ArgumentException("The resource source must be readable.", nameof(source));
        if (source.CanSeek && source.Length - source.Position > maximumBytes)
            throw TooLarge(resourceName, maximumBytes);

        byte[] rented = ArrayPool<byte>.Shared.Rent(Math.Min(maximumBytes + 1, 64 * 1024));
        try
        {
            using var destination = new MemoryStream(Math.Min(maximumBytes, 64 * 1024));
            int total = 0;
            while (true)
            {
                int remaining = maximumBytes - total;
                int requested = Math.Min(rented.Length, remaining + 1);
                int read = await source.ReadAsync(
                    rented.AsMemory(0, requested),
                    cancellationToken).ConfigureAwait(false);
                if (read == 0)
                    return destination.ToArray();
                total = checked(total + read);
                if (total > maximumBytes)
                    throw TooLarge(resourceName, maximumBytes);
                await destination.WriteAsync(
                    rented.AsMemory(0, read),
                    cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private static InvalidDataException TooLarge(string resourceName, int maximumBytes)
        => new($"{resourceName} exceeds the {maximumBytes / (1024 * 1024)} MiB safety limit.");
}
