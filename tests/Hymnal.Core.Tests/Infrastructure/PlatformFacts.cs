using System.Runtime.InteropServices;

namespace Hymnal.Core.Tests.Infrastructure;

/// <summary>
/// A fact that changes Unix file permissions (<see cref="File.SetUnixFileMode(string, UnixFileMode)"/>) and
/// relies on them being enforced: skipped on Windows (no Unix modes) and when running as root (root ignores
/// them, so the test would pass or fail for the wrong reason).
/// </summary>
public sealed class UnixPermissionFactAttribute : FactAttribute
{
    public UnixPermissionFactAttribute()
    {
        if (OperatingSystem.IsWindows())
            Skip = "Unix file permissions; not available on Windows.";
        else if (UnixUser.IsRoot)
            Skip = "Unix file permissions are not enforced for root.";
    }
}

/// <summary>A fact that creates symbolic links; skipped on Windows, where that needs extra privileges.</summary>
public sealed class SymlinkFactAttribute : FactAttribute
{
    public SymlinkFactAttribute()
    {
        if (OperatingSystem.IsWindows())
            Skip = "Creating symbolic links needs extra privileges on Windows.";
    }
}

internal static class UnixUser
{
    [DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint GetEffectiveUserId();

    public static bool IsRoot => !OperatingSystem.IsWindows() && GetEffectiveUserId() == 0;
}
