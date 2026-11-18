using System.Security.Cryptography;
using System.Text;

namespace Hymnal.Core.Infrastructure;

/// <summary>
/// A lock on one file shared by every Hymnal process, for read-modify-write updates of files that two running
/// copies of the app could both change (the global writer profile, a workspace's notes). It is an exclusively
/// opened lock file in the temp folder, named by a hash of the target's full path, so nothing is added next to the
/// target itself. A process that dies releases it with its handles.
/// </summary>
public static class CrossProcessFileLock
{
    /// <summary>How long a writer waits for another process to finish before giving up.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Waits for the lock on <paramref name="targetPath"/> and returns it; dispose to release. Throws
    /// <see cref="IOException"/> when it can't be had within <paramref name="timeout"/>.
    /// </summary>
    public static async Task<IDisposable> AcquireAsync(string targetPath, TimeSpan? timeout = null)
    {
        var lockPath = LockPathFor(targetPath);
        Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);
        var deadline = DateTime.UtcNow + (timeout ?? DefaultTimeout);
        while (true)
        {
            try
            {
                return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                await Task.Delay(25).ConfigureAwait(false);
            }
            catch (IOException ex)
            {
                throw new IOException($"another Hymnal is still writing {Path.GetFileName(targetPath)}", ex);
            }
        }
    }

    /// <summary>The lock file for a target: the same for every process, whatever the path's spelling.</summary>
    internal static string LockPathFor(string targetPath)
    {
        var full = Path.GetFullPath(targetPath);
        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
            full = full.ToUpperInvariant(); // case-insensitive file systems
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(full)))[..32];
        return Path.Combine(Path.GetTempPath(), "hymnal-locks", hash + ".lock");
    }
}
