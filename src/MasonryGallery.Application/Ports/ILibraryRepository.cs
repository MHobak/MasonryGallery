namespace MasonryGallery.Application.Ports;

using System.Collections.ObjectModel;
using MasonryGallery.Domain;

public interface ILibraryRepository
{
    Task<Result<Library>> GetByIdAsync(LibraryId id, CancellationToken ct = default);
    Task<Result<Library>> AddAsync(Library library, CancellationToken ct = default);
    Task<Result> UpdateAsync(Library library, CancellationToken ct = default);
    /// <summary>Removes the library and, by cascade, its media, tags and favorites. Files on disk are untouched.</summary>
    Task<Result> DeleteAsync(LibraryId id, CancellationToken ct = default);
    Task<Result> SetStatusAsync(LibraryId id, LibraryStatus status, CancellationToken ct = default);
    Task<ReadOnlyCollection<Library>> ListAsync(CancellationToken ct = default);
    /// <summary>Records a scan ID as used, so the next full scan's ID is strictly newer than any row's.</summary>
    Task SetLastScanIdAsync(LibraryId id, int scanId, CancellationToken ct = default);
    Task<int> GetNextScanIdAsync(LibraryId id, CancellationToken ct = default);
}
