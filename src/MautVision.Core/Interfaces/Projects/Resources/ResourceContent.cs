namespace MautVision.Core.Interfaces.Projects.Resources;

/// <summary>
/// Открытый для чтения файл ресурса; освобождает его вызывающий. Поток поддерживает Seek,
/// поэтому часть файла (HTTP Range) читается без чтения всего файла.
/// </summary>
public sealed class ResourceContent(Stream stream, string contentType, DateTimeOffset lastModified) : IAsyncDisposable, IDisposable
{
    public Stream Stream { get; } = stream;

    /// <summary>MIME-тип из списка разрешённых Core, например <c>image/jpeg</c>.</summary>
    public string ContentType { get; } = contentType;

    public long Length => Stream.Length;

    public DateTimeOffset LastModified { get; } = lastModified;

    public ValueTask DisposeAsync() => Stream.DisposeAsync();

    public void Dispose() => Stream.Dispose();
}
