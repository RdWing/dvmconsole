using System.Security.Cryptography;
using System.Text;
using DvmConsole.Application;
using DvmConsole.Core.Configuration;

namespace DvmConsole.Configuration.Yaml;

public sealed record ConfigurationBundleExportResult(
    IReadOnlyList<string> OmittedCompanionReferences);

// Exports an in-memory Studio document without giving Presentation or the
// portable configuration layer a filesystem path. This is separate from the
// library revision exporter because exporting a dirty Studio draft must not
// commit it or change its dirty baseline.
public static class ConfigurationBundleExporter
{
    public static async ValueTask<ConfigurationBundleExportResult> ExportAsync(
        string yaml,
        IImportDocumentSet companionSource,
        IExportDocumentSet destination,
        ConfigurationExportOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(yaml);
        ArgumentNullException.ThrowIfNull(companionSource);
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(options);

        ConfigurationDocument document = ParseAndValidate(yaml, destination.Primary.DisplayName);
        var companions = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        var omittedCompanionReferences = new List<string>();
        string exportYaml;
        if (options.Sanitized)
        {
            exportYaml = document.SerializeSanitized();
        }
        else if (!options.IncludeCompanions)
        {
            exportYaml = document.IsReadOnly ? document.SourceText : document.Serialize();
        }
        else
        {
            string[] references = EnumerateCompanionReferences(document.Configuration)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (document.IsReadOnly && references.Length > 0)
            {
                throw new InvalidOperationException(
                    "This read-only YAML cannot have its companion references rewritten for a portable export bundle.");
            }

            var rewrites = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string reference in references)
            {
                IReadableDocument? companion = await companionSource
                    .ResolveCompanionAsync(reference, cancellationToken)
                    .ConfigureAwait(false);
                if (companion is null)
                {
                    omittedCompanionReferences.Add(reference);
                    continue;
                }
                byte[] content;
                try
                {
                    content = await ReadAllBytesAsync(companion, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
                {
                    omittedCompanionReferences.Add(reference);
                    continue;
                }
                string name = AllocateCompanionName(reference, content, companions);
                companions[name] = content;
                rewrites[reference] = "./" + name;
            }

            RewriteCompanionReferences(document.Configuration, rewrites);
            document.MarkDirty();
            exportYaml = document.Serialize();
        }

        _ = ParseAndValidate(exportYaml, destination.Primary.DisplayName);
        await WriteTextAsync(destination.Primary, exportYaml, cancellationToken).ConfigureAwait(false);
        foreach ((string name, byte[] content) in companions)
        {
            IWritableDocument target = await destination
                .CreateCompanionAsync(name, cancellationToken)
                .ConfigureAwait(false);
            await WriteBytesAsync(target, content, cancellationToken).ConfigureAwait(false);
        }

        byte[] exportedPrimary = await ReadAllBytesAsync(destination.Primary, cancellationToken)
            .ConfigureAwait(false);
        string exportedYaml = DecodeYaml(exportedPrimary);
        if (!string.Equals(exportedYaml, exportYaml, StringComparison.Ordinal))
            throw new IOException("The exported codeplug did not match the bytes written to the destination.");
        _ = ParseAndValidate(exportedYaml, destination.Primary.DisplayName);
        foreach ((string name, byte[] expectedContent) in companions)
        {
            IReadableDocument? exported = await destination
                .ResolveExportedCompanionAsync(name, cancellationToken)
                .ConfigureAwait(false);
            if (exported is null)
                throw new IOException($"Exported companion '{name}' could not be read back.");
            byte[] actualContent = await ReadAllBytesAsync(exported, cancellationToken).ConfigureAwait(false);
            if (!actualContent.AsSpan().SequenceEqual(expectedContent))
                throw new IOException($"Exported companion '{name}' did not match the bytes written to the destination.");
        }

        return new ConfigurationBundleExportResult(
            Array.AsReadOnly(omittedCompanionReferences.ToArray()));
    }

    private static ConfigurationDocument ParseAndValidate(string yaml, string displayName)
    {
        ConfigurationDocument document = ConfigurationDocument.Parse(yaml);
        ConfigurationValidationIssue? error = document.Validate().FirstOrDefault(issue => issue.IsError);
        if (error is not null)
            throw new InvalidDataException($"Configuration '{displayName}' is invalid: {error.Message}");
        return document;
    }

    private static IEnumerable<string> EnumerateCompanionReferences(ConsoleConfiguration configuration)
    {
        if (!string.IsNullOrWhiteSpace(configuration.KeyFile))
            yield return configuration.KeyFile;
        foreach (SystemConfiguration system in configuration.Systems)
        {
            if (!string.IsNullOrWhiteSpace(system.AliasPath))
                yield return system.AliasPath;
        }
    }

    private static void RewriteCompanionReferences(
        ConsoleConfiguration configuration,
        IReadOnlyDictionary<string, string> rewrites)
    {
        if (!string.IsNullOrWhiteSpace(configuration.KeyFile) &&
            rewrites.TryGetValue(configuration.KeyFile, out string? keyFile))
        {
            configuration.KeyFile = keyFile;
        }
        foreach (SystemConfiguration system in configuration.Systems)
        {
            if (rewrites.TryGetValue(system.AliasPath, out string? aliasPath))
                system.AliasPath = aliasPath;
        }
    }

    private static string AllocateCompanionName(
        string reference,
        byte[] content,
        IReadOnlyDictionary<string, byte[]> existing)
    {
        string normalized = reference.Replace('\\', '/');
        string candidate = SanitizeFileName(normalized[(normalized.LastIndexOf('/') + 1)..]);
        if (candidate.Length == 0)
            candidate = "companion";
        if (!existing.ContainsKey(candidate))
            return candidate;
        string extension = Path.GetExtension(candidate);
        string stem = Path.GetFileNameWithoutExtension(candidate);
        string suffix = Convert.ToHexString(SHA256.HashData(content))[..8].ToLowerInvariant();
        return $"{stem}-{suffix}{extension}";
    }

    private static string SanitizeFileName(string value)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        return new string(value.Where(character =>
            !invalid.Contains(character) && character is not '/' and not '\\').ToArray());
    }

    private static async ValueTask<byte[]> ReadAllBytesAsync(
        IReadableDocument document,
        CancellationToken cancellationToken)
    {
        await using Stream stream = await document.OpenReadAsync(cancellationToken).ConfigureAwait(false);
        using var memory = new MemoryStream();
        await stream.CopyToAsync(memory, cancellationToken).ConfigureAwait(false);
        return memory.ToArray();
    }

    private static async ValueTask WriteTextAsync(
        IWritableDocument document,
        string content,
        CancellationToken cancellationToken)
        => await WriteBytesAsync(document, Encoding.UTF8.GetBytes(content), cancellationToken).ConfigureAwait(false);

    private static async ValueTask WriteBytesAsync(
        IWritableDocument document,
        ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken)
    {
        await using Stream stream = await document.OpenWriteAsync(cancellationToken).ConfigureAwait(false);
        if (stream.CanSeek)
            stream.SetLength(0);
        await stream.WriteAsync(content, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string DecodeYaml(byte[] bytes)
    {
        using var reader = new StreamReader(
            new MemoryStream(bytes, writable: false),
            new UTF8Encoding(false, true),
            detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }
}
