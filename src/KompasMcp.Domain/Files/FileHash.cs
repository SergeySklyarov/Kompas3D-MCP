using System.Security.Cryptography;
using KompasMcp.Contracts;

namespace KompasMcp.Domain.Files;

/// <summary>
/// Hashing and atomic publication of artefacts (spec 1.12). An export is never "done" because a
/// file with the right name exists: the caller gets a hash of the bytes that are actually on disk.
/// </summary>
public static class FileHash
{
    public static string Sha256(string path)
    {
        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    public static async Task<string> Sha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>Stable hash of a normalised argument object, used as the idempotency key (spec 1.8).</summary>
    public static string ArgumentsHash(string canonicalJson) =>
        Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(canonicalJson))).ToLowerInvariant();
}

/// <summary>
/// Writes a file so that a reader can never see a half-written artefact: a temporary file on the
/// same volume, then a move into place.
/// </summary>
/// <remarks>
/// <c>File.Replace</c> requires the destination to exist and keeps a backup; <c>Move</c> over an
/// existing target is the documented way to publish. Both stay on the same directory, so the
/// operation is atomic on NTFS. A failure path deletes the temporary file but never touches the
/// destination — an interrupted export must leave the previous artefact intact.
/// </remarks>
public static class AtomicPublish
{
    public static void WriteFrom(string sourcePath, string destinationPath, bool allowOverwrite)
    {
        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException("Временный артефакт не найден.", sourcePath);
        }

        if (File.Exists(destinationPath) && !allowOverwrite)
        {
            throw new KompasContractException(
                ErrorCodes.FileExists,
                $"Файл уже существует, перезапись не разрешена: {destinationPath}",
                details: new Dictionary<string, object?>
                {
                    ["path"] = destinationPath,
                    ["existing_sha256"] = SafeSha256(destinationPath),
                });
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath) ?? ".");
            if (File.Exists(destinationPath))
            {
                File.Replace(sourcePath, destinationPath, destinationPath + ".bak");
                TryDelete(destinationPath + ".bak");
            }
            else
            {
                File.Move(sourcePath, destinationPath, overwrite: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDelete(sourcePath);
            throw new KompasContractException(
                ErrorCodes.ExportFailed,
                $"Публикация файла не удалась: {ex.Message}",
                partialEffects: true,
                details: new Dictionary<string, object?> { ["destination"] = destinationPath });
        }
    }

    public static string TemporarySibling(string destinationPath) =>
        Path.Combine(
            Path.GetDirectoryName(destinationPath) ?? ".",
            $".{Path.GetFileName(destinationPath)}.{Guid.NewGuid():N}.tmp");

    private static string? SafeSha256(string path)
    {
        try
        {
            return FileHash.Sha256(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover .tmp or .bak is noise, not data loss; the report will still show it.
        }
    }
}
