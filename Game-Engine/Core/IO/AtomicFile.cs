#nullable enable
using System;
using System.IO;
using System.Text;

namespace Game_Engine.Core.IO;

/// <summary>
/// Crash-safe file I/O helpers. Writes go to a sibling temporary file, flush to disk
/// (<see cref="FileStream.Flush(bool)"/>), and atomically replace/move into place so a
/// crash or power loss never leaves a 0-byte or half-written project/scene/asset file.
/// </summary>
public static class AtomicFile
{
    static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// Atomically writes UTF-8 text (no BOM) to <paramref name="path"/>.
    /// When <paramref name="keepBackup"/> is true and <paramref name="path"/> already exists,
    /// the previous file is rotated to <c>path + ".bak"</c>.
    /// </summary>
    public static void WriteAllTextAtomic(string path, string content, bool keepBackup = true)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Path must not be empty.", nameof(path));

        byte[] bytes = Utf8NoBom.GetBytes(content ?? string.Empty);
        WriteAllBytesAtomic(path, bytes, keepBackup);
    }

    /// <summary>
    /// Atomically writes <paramref name="bytes"/> to <paramref name="path"/>.
    /// Ensures the parent directory exists, flushes data to disk, and atomically replaces
    /// or moves the temporary file into place.
    /// </summary>
    public static void WriteAllBytesAtomic(string path, byte[] bytes, bool keepBackup = false)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Path must not be empty.", nameof(path));
        ArgumentNullException.ThrowIfNull(bytes);

        string fullPath = Path.GetFullPath(path);
        string? dir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(dir))
            Directory.CreateDirectory(dir);

        string tmpPath = fullPath + ".tmp." + Guid.NewGuid().ToString("N")[..8];
        try
        {
            using (var fs = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, FileOptions.None))
            {
                if (bytes.Length > 0)
                    fs.Write(bytes, 0, bytes.Length);
                fs.Flush(flushToDisk: true);
            }

            if (File.Exists(fullPath))
            {
                if (keepBackup)
                {
                    string bakPath = fullPath + ".bak";
                    try
                    {
                        File.Replace(tmpPath, fullPath, bakPath, ignoreMetadataErrors: true);
                    }
                    catch (Exception ex) when (ex is PlatformNotSupportedException or IOException or UnauthorizedAccessException)
                    {
                        // Fallback for filesystems/platforms where File.Replace is unsupported or blocked.
                        File.Move(tmpPath, fullPath, overwrite: true);
                    }
                }
                else
                {
                    File.Move(tmpPath, fullPath, overwrite: true);
                }
            }
            else
            {
                File.Move(tmpPath, fullPath, overwrite: true);
            }
        }
        catch
        {
            try
            {
                if (File.Exists(tmpPath))
                    File.Delete(tmpPath);
            }
            catch
            {
                // Best-effort cleanup of temp file.
            }
            throw;
        }
    }

    /// <summary>
    /// Reads UTF-8 text from <paramref name="path"/>. If <paramref name="path"/> is missing or
    /// 0 bytes and a non-empty <c>path + ".bak"</c> exists, logs a warning and reads the backup.
    /// </summary>
    public static string ReadAllTextWithBackupFallback(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Path must not be empty.", nameof(path));

        bool primaryMissingOrEmpty = !File.Exists(path);
        if (!primaryMissingOrEmpty)
        {
            try
            {
                primaryMissingOrEmpty = new FileInfo(path).Length == 0;
            }
            catch
            {
                primaryMissingOrEmpty = false;
            }
        }

        if (primaryMissingOrEmpty)
        {
            string bakPath = path + ".bak";
            if (File.Exists(bakPath))
            {
                try
                {
                    if (new FileInfo(bakPath).Length > 0)
                    {
                        Log.Warning($"[AtomicFile] '{path}' is missing or empty; recovering from backup '{bakPath}'.");
                        return File.ReadAllText(bakPath);
                    }
                }
                catch
                {
                    // Fall through to normal read below so caller gets the expected exception.
                }
            }
        }

        return File.ReadAllText(path);
    }
}
