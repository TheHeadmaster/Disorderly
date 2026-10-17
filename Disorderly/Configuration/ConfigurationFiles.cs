using System.IO;
using System.Security.Cryptography;
using Serilog;

namespace Disorderly.Configuration;
public enum ConfigurationWriteStatus
{
    Saved,
    Conflict,
    Failed,
    Uncertain
}

public sealed record ConfigurationWriteResult(ConfigurationWriteStatus Status, bool? Replaced, string Code);
/// <summary>Only the configured document is read/written. Implementations must check the expected digest at replacement.</summary>
public interface IConfigurationFiles
{
    Task<byte[]?> ReadAsync(string path);
    Task<ConfigurationWriteResult> WriteAsync(string path, ReadOnlyMemory<byte> bytes, string? expectedFingerprint);
}

public interface IConfigurationReplacement
{
    void Replace(string temporary, string destination, bool existing);
}

public sealed class ConfigurationFiles(IConfigurationReplacement? replacement = null) : IConfigurationFiles
{
    private readonly IConfigurationReplacement replacement = replacement ?? new SystemReplacement();
    private sealed class SystemReplacement : IConfigurationReplacement
    {
        public void Replace(string temporary, string destination, bool existing)
        {
            if (existing)
            {
                File.Replace(temporary, destination, null);
            }
            else
            {
                File.Move(temporary, destination, false);
            }
        }
    }

    public static string Fingerprint(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    public async Task<byte[]?> ReadAsync(string path)
    {
        try
        {
            await using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous);
            using MemoryStream content = new();
            byte[] buffer = new byte[8192];
            int count;
            while ((count = await stream.ReadAsync(buffer).ConfigureAwait(false)) != 0)
            {
                if (content.Length + count > ConfigurationCodec.MaximumBytes)
                {
                    throw new ConfigurationDocumentException("DocumentTooLarge", "$", "Configuration exceeds the 1 MiB limit.");
                }

                content.Write(buffer, 0, count);
            }

            return content.ToArray();
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
    }

    public async Task<ConfigurationWriteResult> WriteAsync(string path, ReadOnlyMemory<byte> bytes, string? expectedFingerprint)
    {
        string? temporary = null;
        bool replacing = false;
        try
        {
            byte[]? before = await this.ReadAsync(path).ConfigureAwait(false);
            if ((before is null ? null : Fingerprint(before)) != expectedFingerprint)
            {
                return new(ConfigurationWriteStatus.Conflict, false, "ExternalConflict");
            }

            string directory = Path.GetDirectoryName(path)!;
            Directory.CreateDirectory(directory);
            temporary = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
            await using (FileStream stream = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes).ConfigureAwait(false);
                await stream.FlushAsync().ConfigureAwait(false);
                stream.Flush(true);
            }

            byte[]? current = await this.ReadAsync(path).ConfigureAwait(false);
            if ((current is null ? null : Fingerprint(current)) != expectedFingerprint)
            {
                return new(ConfigurationWriteStatus.Conflict, false, "ExternalConflict");
            }

            replacing = true;
            this.replacement.Replace(temporary, path, current is not null);
            temporary = null;
            return new(ConfigurationWriteStatus.Saved, true, "Saved");
        }
        catch (Exception error)when (error is IOException or UnauthorizedAccessException or NotSupportedException or System.Security.SecurityException or ConfigurationDocumentException)
        {
            Log.Warning("Configuration write raised {ExceptionType}; replacement attempted: {Attempted}.", error.GetType().Name, replacing);
            return new(replacing ? ConfigurationWriteStatus.Uncertain : ConfigurationWriteStatus.Failed, replacing ? null : false, replacing ? "ReplacementUncertain" : "WriteFailed");
        }
        finally
        {
            if (temporary is not null)
            {
                try
                {
                    File.Delete(temporary);
                }
                catch (Exception error)when (error is IOException or UnauthorizedAccessException)
                {
                    Log.Warning("Configuration temporary-file cleanup raised {ExceptionType}.", error.GetType().Name);
                }
            }
        }
    }
}