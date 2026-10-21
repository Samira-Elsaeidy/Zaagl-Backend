namespace Zaagl.SharedKernel.Storage;

public interface IFileStorage
{
    Task<string> SaveAsync(Stream content, string contentType, CancellationToken cancellationToken);
    Task<Stream> OpenReadAsync(string storageKey, CancellationToken cancellationToken);
    Task DeleteAsync(string storageKey, CancellationToken cancellationToken);
}
