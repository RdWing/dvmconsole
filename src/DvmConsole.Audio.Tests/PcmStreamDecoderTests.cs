using DvmConsole.Audio;
using Xunit;

namespace DvmConsole.Audio.Tests;

public sealed class PcmStreamDecoderTests
{
    private const string SampleMp3Base64 =
        "SUQzBAAAAAAAI1RTU0UAAAAPAAADTGF2ZjYyLjEyLjEwMgAAAAAAAAAAAAAA/+M4wAAAAAAAAAAAAEluZm8AAAAPAAAABQAABRAAVVVVVVVVVVVVVVVVVVVVVVVVVYCAgICAgICAgICAgICAgICAgICAqqqqqqqqqqqqqqqqqqqqqqqqqqrV1dXV1dXV1dXV1dXV1dXV1f//////////////////////////AAAAAExhdmM2Mi4yOAAAAAAAAAAAAAAAACQCwAAAAAAAAAUQX/2kDQAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA/+M4xAAnsNJgF1kYAAGoAC713rvXeu9d7O2vu+5DWGGLsXYuxnDkO45bD1h1A1B11xfjhpgNxME83aTz5P30+czpdBTbiv3AC7F2M4dyHLHc6eNxuX058+CAIOBw5iAH3xODgISgIA+D4Pg+DgIAgCAIA+D4Pg+DgIAgCAIA+D4Pg+DgIAgCAIA+D4Pg+DgIAgMDAPvg+DgIAgCAYB8Hz9HwfD///z///BA5pQAAAIBQGAzATA0MDkDowSwZjAYBBq2a1b//zBIBPMGYIswKwYjB3CStTNWM/+M4xCwzsypEeZ6gAP//mGmJIYLgWJsxFfGR6NJARgXAKGzSTUYcAFnmA2Cua0ih5hjgggbb6B/NYHi1gY9gBs1wGvaGTmpiyVHwAC4GYLgZY6AkQBixQGLHBYiBggKnXq/8DBBQ2YG4gbWD9QuFC0EUkGrg1d/+6vxjRBUQVHKFBC5iGi5RzSaHOIt//1e1piRUnTIvGyy6yknt//9l9vq1qUktExUkZIomINf/66XVzDtb7nda5bqZ6rYd7Zzw3L8zAphPA4lncwMVkDMsjAwBb80Mz6FM/+M4xCgv47oEAZ94AS6wzO+YNqL1GaUfjhgpYWtYf1KY/IbWmNwiH5jX/i7+4GomfreI+9YvFvumoG9W9IOa6zGtEg0xil7ahX942d1zNLFhX36Qpsb1BlprWbQ4GaxZb5mr82g43elp87jadW+d6zqm2zfgQd0vml4UasDxYU/3m9aff8DWp6eW+oLzUHXi7veBS+qtdq0mvj//1vmNT3vuE5lV//98Wbu4yAwMA8GgwYDgmYHgh//7VmDl70wjAAIzAoIjBoGzBMRPMAgrNgbDMHABCAEM/+M4xDM5q8JgAZ2gABQMDhiECIijB0ZzF0qjKkeDAMX/xQwhggmIXHcLgA1J4DNKQCDQBwEDCiAKh/8XITpBxmy8RAghdEAgbVDBg0QwEHfJcPl/8nyDmRUJwxNyfOGglxUEpC9L4skZ4wGaHT/+Th03L6jQ0WmbqQMxwjqLBBiGlkiJDikRImf/80WmbqY0W5upkFlEqk0UisXSiWi8Uj5dKJ7//901My3dTMu6rLLxkfNTE0NjI3NTFA2MkzxigoCUCQDIPK6jaVIADMINXTPS/pclMF/C/+M4xBcvib4cAdlgAOcYpRllF2WeAUIxyDJCUZAiJxYnpmAohEOZx5kAqw0qaJiHmker8wkTUTMwVUKlRaYtsmFLFbkAyYUDQCw1YViKsJQAQOqToQgbCM4SgbE55kxEkm3ORJMYmjoyPqHRkfeytWrdWrXesuXWxpc9Na1rrK06DQdEQNFQVWCrgaiIOlXLBWeWIniWITqnlT2oGeJT2oGeCr8sHOCr8sHOCr8se4K8se4K1UxBTUU0LjBVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVV";

    private const string SampleOggBase64 =
        "T2dnUwACAAAAAAAAAABw+QnzAAAAAEapBNUBE09wdXNIZWFkAQE4AYC7AAAAAABPZ2dTAAAAAAAAAAAAAHD5CfMBAAAAUKb7MwE+T3B1c1RhZ3MNAAAATGF2ZjYyLjEyLjEwMgEAAAAdAAAAZW5jb2Rlcj1MYXZjNjIuMjguMTAyIGxpYm9wdXNPZ2dTAAQYMAAAAAAAAHD5CfMCAAAAqF2CTA1FMSw3NDktMjgxLCsseIIBt2x+QOYAAAq+mqe+/7aF1wpx4N1roOPGxcMyxmQebmNSOkUMilOPRBiwjGSr7JiWeWxUUzBVToLkFspZSdjHCV1ZeKM/96yYhQNXTCUn9YrD9Yx2h69+WnNdx9nWzkMtCUmvb886tC36lWJZjpkib+nuw3iboxJRRQCs0VX/p5G09hwYk4oJPU9RTXBWnAQO5yE8SQnSf4k/xfyhxX4DeJujEbQcv6ibF3w+xqdLWQt13a9P8nDVKyGwtc0xVqDZ2SzsxHrpjeEeN69pgnqvpHraqKFflnibo191nPxF0LQ4BZwzEqEBu4QdTbPQ5uO+iwrzYVL+PAoqCslQ4cq8M1CHjZvPis8G4vp4m6MSU6pEuuelZYD2B2h+FdkPNlmW0+qoXMVyWvWbdV8MAW0Yt0Z3pSbKi9AxUVG5csY3eS9ChWN4m6NfdZz8SUoi6sLGxzHlb5x0PU8JsjPrdcRvyUltPdEp9jXfwzv0kasO3oZ4m6MSVs4jiHSQ5ANvzjFHYj1z4DciBuk1K4lli9P9hJWxOFTU+8MhoQ9EJ1h904r8BniboxG0HL9SUP+UOxsA4CR2vhiSI8CpJzZP4KOTCPX+CY9GVwgFQS2w/nL0airgMy27Q3cFhQ2LSJsrH3Wc/EWecpEjapQTpQ1LKYRoQJZ2dGHEMa2DgC+IOuej6VFp9O02Gu1hI2oY6EibKtJTqkS4HAbV7urU0Kbzh7CFWOaNS05b3UWA1DceqFRfW5CrLP1hB/SSSJsrH3Wc/EXvTOHHY560XT9OrRYNZaGwn6DDkl1LRf7iy9MSWh6Q0S7L4EibvBMlNWKre+YI/L/hjjg/kxndKY1B4VBVfzSJDEtS9QXEJKXpafQhpuIo";

    [Fact]
    public async Task SelectsManagedMpegDecoderAndProducesMonoPcm()
    {
        await using var reader = await PcmStreamDecoder.OpenAsync(
            new MemoryStream(Convert.FromBase64String(SampleMp3Base64)));
        short[] samples = new short[160];

        int count = await reader.ReadSamplesAsync(samples);

        Assert.Equal(8_000, reader.SampleRate);
        Assert.True(count > 0);
        Assert.All(samples[count..], sample => Assert.Equal((short)0, sample));
    }

    [Fact]
    public async Task RejectsUnknownCompressedFormatWithoutPlatformDecoder()
    {
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            PcmStreamDecoder.OpenAsync(new MemoryStream("fLaCnot-supported"u8.ToArray())));
    }

    [Fact]
    public async Task UsesManagedDecoderForOggOpusAudio()
    {
        await using var reader = await PcmStreamDecoder.OpenAsync(
            new MemoryStream(Convert.FromBase64String(SampleOggBase64)));
        short[] samples = new short[1600];

        int count = await reader.ReadSamplesAsync(samples);

        Assert.Equal(8_000, reader.SampleRate);
        Assert.True(count > 0);
        Assert.Contains(samples[..count], sample => sample != 0);
    }

}
