using DvmConsole.Audio;
using DvmConsole.Media;

namespace DvmConsole.Desktop.Tests;

internal static class PcmWavTestFile
{
    public static PcmWavFileWriter Create(string path, PcmAudioFormat format)
    {
        string fullPath = Path.GetFullPath(path);
        string? directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        var stream = new FileStream(
            fullPath,
            FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.Read,
            bufferSize: 16 * 1024,
            options: FileOptions.SequentialScan);
        try
        {
            return new PcmWavFileWriter(stream, format);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }
}
