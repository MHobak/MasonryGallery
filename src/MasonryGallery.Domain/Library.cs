namespace MasonryGallery.Domain;

public sealed class Library
{
    public LibraryId Id { get; set; }
    public string Name { get; init; } = string.Empty;
    public string RootPath { get; init; } = string.Empty;
    public bool Enabled { get; init; } = true;
    public LibraryStatus Status { get; init; } = LibraryStatus.Online;
    public bool FollowSymlinks { get; init; } = false;
    public int LastScanId { get; init; } = 0;
    public DateTime? LastScanUtc { get; init; }

    public Library(string name, string rootPath)
    {
        Name = name;
        RootPath = rootPath;
    }

    public Library()
    {
        Name = string.Empty;
        RootPath = string.Empty;
    }

    public bool IsOnline => Status == LibraryStatus.Online;
    public bool IsScanning => Status == LibraryStatus.Scanning;
}
