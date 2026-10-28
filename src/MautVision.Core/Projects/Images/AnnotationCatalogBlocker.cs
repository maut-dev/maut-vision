using MautVision.Core.Interfaces.Projects.Catalog;
using MautVision.Core.Projects.Storage;
using static MautVision.Core.Projects.Catalog.CatalogSql;

namespace MautVision.Core.Projects.Images;

/// <summary>
/// Catalog deletion check registered by the Core composition: the current annotation of an image that references the class,
/// the class variant or any class variant of the project variant blocks its permanent deletion. Earlier annotation
/// revisions do not block. Count — such images; examples — a few image names.
/// </summary>
internal static class AnnotationCatalogBlocker
{
    internal const string Code = "images.annotations";
    private const int Examples = 5;

    internal static Task<IReadOnlyList<DeletionBlocker>> CollectAsync(ProjectStore store, CatalogTargetKind kind, Guid targetId,
        CancellationToken cancellationToken)
    {
        var filter = kind switch
        {
            CatalogTargetKind.Class => "o.class_id = $id",
            CatalogTargetKind.ClassVariant => "o.class_variant_id = $id",
            CatalogTargetKind.Variant => "o.class_variant_id IN (SELECT id FROM class_variants WHERE variant_id = $id)",
            _ => null,
        };
        if (filter is null) return Task.FromResult<IReadOnlyList<DeletionBlocker>>([]);
        return store.ReadAsync<IReadOnlyList<DeletionBlocker>>(async (connection, token) =>
        {
            var current = $"""
                SELECT DISTINCT o.image_id FROM image_annotation_objects o
                JOIN images i ON i.id = o.image_id AND i.annotation_revision = o.revision WHERE {filter}
                """;
            await using var count = Command(connection, null, $"SELECT count(*) FROM ({current});");
            count.Parameters.AddWithValue("$id", Key(targetId));
            var images = await ScalarAsync(count, token).ConfigureAwait(false);
            if (images == 0) return [];
            await using var names = Command(connection, null, $"""
                SELECT g.name || ' / ' || i.display_name FROM images i JOIN image_groups g ON g.id = i.group_id
                WHERE i.id IN ({current} LIMIT {Examples}) ORDER BY g.name, i.ordinal;
                """);
            names.Parameters.AddWithValue("$id", Key(targetId));
            var examples = await ListAsync(names, reader => reader.GetString(0), token).ConfigureAwait(false);
            return [new DeletionBlocker(Code, (int)Math.Min(images, int.MaxValue), examples)];
        }, cancellationToken);
    }
}
