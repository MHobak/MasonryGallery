namespace MasonryGallery.Application.UseCases;

using MasonryGallery.Application.Ports;
using MasonryGallery.Domain;

public sealed class AddLibrary
{
    private readonly ILibraryRepository _libraryRepository;

    public AddLibrary(ILibraryRepository libraryRepository)
        => _libraryRepository = libraryRepository;

    public async Task<Result<Library>> ExecuteAsync(string name, string rootPath, CancellationToken ct = default)
    {
        var library = new Library(name, rootPath);
        return await _libraryRepository.AddAsync(library, ct);
    }
}
