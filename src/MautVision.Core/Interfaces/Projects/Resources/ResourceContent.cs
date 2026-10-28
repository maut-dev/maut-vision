namespace MautVision.Core.Interfaces.Projects.Resources;

/// <summary>
/// A resource file open for reading; the caller releases it. The stream supports Seek,
/// so a part of the file (HTTP Range) is read without reading the whole file.
/// </summary>
public sealed class ResourceContent(Stream stream, string contentType, DateTimeOffset lastModified) : IAsyncDisposable, IDisposable
{
    public Stream Stream { get; } = stream;

    /// <summary>MIME type from the list allowed by Core, for example <c>image/jpeg</c>.</summary>
    public string ContentType { get; } = contentType;

    public long Length => Stream.Length;

    public DateTimeOffset LastModified { get; } = lastModified;

    public ValueTask DisposeAsync() => Stream.DisposeAsync();

    public void Dispose() => Stream.Dispose();
}
