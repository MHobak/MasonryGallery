namespace MasonryGallery.Domain;

public readonly record struct LibraryId(int Value);

public enum MediaKind : int
{
    Image = 0,
    Video = 1
}

public enum ProbeState : int
{
    Pending = 0,
    Done = 1,
    Failed = 2
}

public enum ThumbState : int
{
    Pending = 0,
    Done = 1,
    Failed = 2
}

public enum LibraryStatus : int
{
    Online = 0,
    Offline = 1,
    Scanning = 2,
    Error = 3
}
