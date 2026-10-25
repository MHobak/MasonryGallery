namespace MasonryGallery.Infrastructure.FileSystem;

using System.Collections.Frozen;

/// <summary>
/// What kind of storage a library lives on, to size worker pools (design §5.5).
/// Linux reads /proc/self/mountinfo and /sys; elsewhere only network drives are detected and disks count as SSD.
/// </summary>
internal static class VolumeInfo
{
    private static readonly FrozenSet<string> NetworkFileSystems = new[]
    {
        "nfs", "nfs4", "cifs", "smb3", "smbfs", "9p", "afs", "ceph", "glusterfs",
        "fuse.sshfs", "fuse.rclone", "davfs", "fuse.davfs2"
    }.ToFrozenSet(StringComparer.Ordinal);

    internal sealed record Mount(string MountPoint, string MajorMinor, string FileSystem, string Source);

    public static bool IsNetwork(string path)
    {
        if (path.StartsWith(@"\\", StringComparison.Ordinal))
            return true;
        if (OperatingSystem.IsLinux())
            return FindMount(path) is { } m && NetworkFileSystems.Contains(m.FileSystem);
        try
        {
            return new DriveInfo(path).DriveType == DriveType.Network;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    public static bool IsRotational(string path)
    {
        if (!OperatingSystem.IsLinux() || FindMount(path) is not { } mount)
            return false;
        try
        {
            // Mount's device number first; btrfs and friends report an anonymous 0:N, so fall back to the source device.
            return ReadRotational($"/sys/dev/block/{mount.MajorMinor}")
                   ?? (mount.Source.StartsWith("/dev/", StringComparison.Ordinal)
                       ? ReadRotational($"/sys/class/block/{Path.GetFileName(ResolveLink(mount.Source))}")
                       : null)
                   ?? false;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    // A partition has no queue/ of its own; its parent disk does.
    private static bool? ReadRotational(string sysDevice)
    {
        if (!Directory.Exists(sysDevice))
            return null;
        var device = ResolveLink(sysDevice);
        if (File.Exists(Path.Join(device, "partition")))
            device = Path.GetDirectoryName(device)!;
        var flag = Path.Join(device, "queue", "rotational");
        return File.Exists(flag) ? File.ReadAllText(flag).Trim() == "1" : null;
    }

    private static string ResolveLink(string path)
        => new FileInfo(path).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? path;

    private static Mount? FindMount(string path)
    {
        try
        {
            return FindMount(Path.GetFullPath(path), File.ReadLines("/proc/self/mountinfo"));
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>The mount with the longest mount point containing <paramref name="fullPath"/>.</summary>
    internal static Mount? FindMount(string fullPath, IEnumerable<string> mountinfo)
    {
        Mount? best = null;
        foreach (var line in mountinfo)
        {
            // 36 35 98:0 /mnt1 /mnt/parent rw,noatime master:1 - ext3 /dev/root rw,errors=continue
            var fields = line.Split(' ');
            var separator = Array.IndexOf(fields, "-");
            if (fields.Length < 5 || separator < 0 || separator + 2 >= fields.Length)
                continue;

            var mountPoint = Unescape(fields[4]);
            var contains = mountPoint == "/"
                           || fullPath == mountPoint
                           || fullPath.StartsWith(mountPoint + "/", StringComparison.Ordinal);
            if (contains && (best is null || mountPoint.Length > best.MountPoint.Length))
                best = new Mount(mountPoint, fields[2], fields[separator + 1], Unescape(fields[separator + 2]));
        }
        return best;
    }

    // mountinfo escapes space, tab, newline and backslash as \040 \011 \012 \134.
    private static string Unescape(string field)
    {
        if (!field.Contains('\\'))
            return field;
        var sb = new System.Text.StringBuilder(field.Length);
        for (var i = 0; i < field.Length; i++)
        {
            if (field[i] == '\\' && i + 3 < field.Length && field[(i + 1)..(i + 4)].All(char.IsAsciiDigit))
            {
                sb.Append((char)Convert.ToInt32(field[(i + 1)..(i + 4)], 8));
                i += 3;
            }
            else
            {
                sb.Append(field[i]);
            }
        }
        return sb.ToString();
    }
}
