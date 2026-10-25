namespace MasonryGallery.Infrastructure.Tests;

using FluentAssertions;
using MasonryGallery.Infrastructure.FileSystem;
using MasonryGallery.Infrastructure.Thumbnails;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

public sealed class FileSystemTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("mg-fs-").FullName;

    public void Dispose() => Directory.Delete(_dir, true);

    private string Touch(string rel)
    {
        var path = Path.Combine(_dir, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "x");
        return path;
    }

    private async Task<List<string>> EnumerateAsync(bool followSymlinks)
    {
        var found = new List<string>();
        await foreach (var e in new FileSystemEnumerator(NullLogger<FileSystemEnumerator>.Instance)
                           .EnumerateAsync(Path.Combine(_dir, "lib"), followSymlinks))
            found.Add(e.RelativePath.Replace('\\', '/'));
        return found.Order().ToList();
    }

    [Fact]
    public async Task Enumerator_skips_nomedia_hidden_and_ignored_folders()
    {
        Touch("lib/a.jpg");
        Touch("lib/notes.txt");
        Touch("lib/sub/B.PNG");
        Touch("lib/private/.nomedia");
        Touch("lib/private/c.jpg");
        Touch("lib/.thumbnails/d.jpg");
        Touch("lib/@eaDir/e.jpg");

        (await EnumerateAsync(followSymlinks: false)).Should().Equal("a.jpg", "sub/B.PNG");
    }

    [Fact]
    public async Task Symlinks_are_followed_only_when_enabled_and_loops_terminate()
    {
        Touch("lib/a.jpg");
        Touch("outside/z.jpg");
        Directory.CreateSymbolicLink(Path.Combine(_dir, "lib", "linked"), Path.Combine(_dir, "outside"));
        Directory.CreateSymbolicLink(Path.Combine(_dir, "lib", "loop"), Path.Combine(_dir, "lib"));
        File.CreateSymbolicLink(Path.Combine(_dir, "lib", "file-link.jpg"), Path.Combine(_dir, "outside", "z.jpg"));

        (await EnumerateAsync(followSymlinks: false)).Should().Equal("a.jpg");
        (await EnumerateAsync(followSymlinks: true)).Should().Equal("a.jpg", "file-link.jpg", "linked/z.jpg");
    }

    [Fact]
    public void Mount_lookup_picks_longest_prefix_and_unescapes()
    {
        string[] mountinfo =
        [
            "22 1 259:2 / / rw,relatime shared:1 - btrfs /dev/nvme0n1p2 rw",
            "40 22 0:45 / /mnt/shared rw,relatime shared:9 - cifs //nas/share rw,vers=3.1.1",
            "41 22 8:17 / /mnt/my\\040disk rw,relatime shared:10 - ext4 /dev/sdb1 rw",
        ];

        VolumeInfo.FindMount("/mnt/shared/photos/x.jpg", mountinfo)!.FileSystem.Should().Be("cifs");
        VolumeInfo.FindMount("/mnt/sharedness/x.jpg", mountinfo)!.MountPoint.Should().Be("/");
        VolumeInfo.FindMount("/mnt/my disk/a.jpg", mountinfo)!
            .Should().Be(new VolumeInfo.Mount("/mnt/my disk", "8:17", "ext4", "/dev/sdb1"));
    }

    [Fact]
    public async Task Janitor_removes_orphans_and_debris_but_keeps_known_and_recent_files()
    {
        var store = new ShardedThumbnailStore(Path.Combine(_dir, "thumbs"), "v2", NullLogger<ShardedThumbnailStore>.Instance);
        foreach (var id in new[] { 1, 2, 3 })
            await store.SaveAsync(id, 320, new byte[] { 1 });
        var debris = Path.Combine(Path.GetDirectoryName(store.GetPath(1, 320))!, "1.tmp.abc");
        File.WriteAllText(debris, "");
        var cutoff = DateTime.UtcNow.AddSeconds(1);
        File.SetLastWriteTimeUtc(store.GetPath(3, 320), cutoff.AddMinutes(1)); // written after the ID snapshot

        var removed = await store.CleanOrphansAsync(new HashSet<int> { 1 }, cutoff);

        removed.Should().Be(2); // orphan 2 + debris
        File.Exists(store.GetPath(1, 320)).Should().BeTrue();
        File.Exists(store.GetPath(2, 320)).Should().BeFalse();
        File.Exists(store.GetPath(3, 320)).Should().BeTrue();
        File.Exists(debris).Should().BeFalse();
    }

    [Fact]
    public async Task Stale_version_folders_are_deleted()
    {
        var root = Path.Combine(_dir, "thumbs");
        Directory.CreateDirectory(Path.Combine(root, "v1", "small"));
        Directory.CreateDirectory(Path.Combine(root, "vintage"));
        var store = new ShardedThumbnailStore(root, "v2", NullLogger<ShardedThumbnailStore>.Instance);

        await store.DeleteStaleVersionsAsync();

        Directory.GetDirectories(root).Select(Path.GetFileName).Should().BeEquivalentTo(["v2", "vintage"]);
    }
}
