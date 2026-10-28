using MautVision.Core.Interfaces.Projects.Catalog;

namespace MautVision.Core.Projects.Catalog;

/// <summary>Project variants: the variants table. Bindings to classes and their images belong to ClassesManager.</summary>
internal sealed class VariantsManager : IVariantsManager
{
    private const string Area = "variants";

    private readonly CatalogContext context;
    private readonly NamedRows<ProjectVariant> rows;

    internal VariantsManager(CatalogContext context)
    {
        this.context = context;
        rows = new NamedRows<ProjectVariant>(context, Area, "variants", CatalogSql.ReadVariant,
            variant => (variant.Id, variant.Revision, variant.ArchivedAt is not null),
            (variant, revisions) => context.Raise(revisions.OrderRevision, revisions.CatalogRevision, variants: [new(variant.Id, variant.Revision)]));
    }

    public Task<IReadOnlyList<ProjectVariant>> ListAsync(CancellationToken cancellationToken = default) => rows.ListAsync(cancellationToken);

    public Task<ProjectVariant> CreateAsync(string name, string description, CancellationToken cancellationToken = default)
        => rows.CreateAsync(name, description, bumpOrder: false, cancellationToken);

    public Task<ProjectVariant> UpdateAsync(Guid variantId, string name, string description, long expectedRevision,
        CancellationToken cancellationToken = default)
        => rows.UpdateAsync(variantId, name, description, expectedRevision, cancellationToken);

    public Task<ProjectVariant> SetArchivedAsync(Guid variantId, bool archived, long expectedRevision, CancellationToken cancellationToken = default)
        => rows.SetArchivedAsync(variantId, archived, expectedRevision, cancellationToken);

    public Task<DeletionReview> ReviewDeletionAsync(Guid variantId, CancellationToken cancellationToken = default)
        => context.Deletion.ReviewAsync(Area, CatalogTargetKind.Variant, variantId, cancellationToken);

    public Task<DeletionResult> DeleteAsync(Guid variantId, string reviewRevision, CancellationToken cancellationToken = default)
        => context.Deletion.DeleteAsync(Area, CatalogTargetKind.Variant, variantId, reviewRevision, cancellationToken);
}
