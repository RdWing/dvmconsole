using DvmConsole.Operations;
using System.Security.Cryptography;
using System.Text;

namespace DvmConsole.Application;

public readonly record struct ConsoleSessionId(Guid Value)
{
    public static ConsoleSessionId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString("N");
}

public readonly record struct ConfigurationId(Guid Value)
{
    public static ConfigurationId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString("N");
}

public readonly record struct ConfigurationRevision(Guid Value)
{
    public static ConfigurationRevision New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString("N");
}

public readonly record struct SystemId(string Value)
{
    public static SystemId FromName(string name) => new(StableId.Normalize(name, nameof(name)));
    public override string ToString() => Value;
}

public readonly record struct ZoneId(string Value)
{
    public static ZoneId FromName(string name) => new(StableId.Normalize(name, nameof(name)));
    public override string ToString() => Value;
}

public readonly record struct ChannelId(ChannelSessionId Value)
{
    public override string ToString() => Value.ToString();
}

public readonly record struct CallId(Guid Value)
{
    public static CallId New() => new(Guid.NewGuid());
}

public readonly record struct RecordingId(Guid Value)
{
    public static RecordingId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString("N");
}

public readonly record struct WebStreamId(Guid Value)
{
    public static WebStreamId New() => new(Guid.NewGuid());

    public static WebStreamId FromIdentity(string name, string url)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("A web-stream name is required.", nameof(name));
        if (string.IsNullOrWhiteSpace(url))
            throw new ArgumentException("A web-stream URL is required.", nameof(url));
        byte[] identity = Encoding.UTF8.GetBytes(
            $"{name.Trim().ToLowerInvariant()}\n{url.Trim()}");
        byte[] hash = SHA256.HashData(identity);
        return new WebStreamId(new Guid(hash.AsSpan(0, 16)));
    }

    public override string ToString() => Value.ToString("N");
}

public readonly record struct StreamId
{
    public StreamId(ChannelId channel, uint value)
    {
        if (value == 0)
            throw new ArgumentOutOfRangeException(nameof(value));
        Channel = channel;
        Value = value;
    }

    public ChannelId Channel { get; }
    public uint Value { get; }
}

public readonly record struct PatchId(Guid Value)
{
    public static PatchId New() => new(Guid.NewGuid());
    public static PatchId FromName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("A patch name is required.", nameof(name));
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(name.Trim().ToLowerInvariant()));
        return new PatchId(new Guid(hash.AsSpan(0, 16)));
    }
    public override string ToString() => Value.ToString("N");
}

public readonly record struct AssetId(Guid Value)
{
    public static AssetId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString("N");
}

internal static class StableId
{
    public static string Normalize(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("A stable identifier value is required.", parameterName);
        return value.Trim().ToLowerInvariant();
    }
}
