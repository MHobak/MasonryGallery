namespace MasonryGallery.Host;

/// <summary>
/// Keeps a second copy of the app from writing the same database (design §10).
/// Holds an exclusive handle on a lock file for the life of the process. On Linux .NET backs
/// <see cref="FileShare.None"/> with flock, on Windows with a sharing lock; both are released by the OS
/// if the process dies, so a crash never leaves a stale lock behind.
/// </summary>
internal sealed class SingleInstanceLock : IDisposable
{
    private readonly FileStream _stream;

    private SingleInstanceLock(FileStream stream) => _stream = stream;

    /// <summary>Returns null when another process already holds the lock.</summary>
    public static SingleInstanceLock? TryAcquire(string path)
    {
        try
        {
            var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            // The PID is only a hint for whoever looks at the file; the lock itself is the open handle.
            stream.SetLength(0);
            using (var writer = new StreamWriter(stream, leaveOpen: true))
                writer.Write(Environment.ProcessId);
            stream.Flush(true);
            return new SingleInstanceLock(stream);
        }
        catch (IOException)
        {
            return null;
        }
    }

    public void Dispose() => _stream.Dispose();
}
