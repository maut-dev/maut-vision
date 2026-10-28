using System.Security.Cryptography;
using System.Text;

namespace MautVision.Core.Projects.Catalog;

/// <summary>An image of a binding for generation: its stored file is files/class-assets/&lt;Id&gt;&lt;Extension&gt;.</summary>
internal sealed record CatalogSourceAsset(Guid Id, string Extension, int Width, int Height, bool HasAlpha);

/// <summary>A binding with its class, project variant, availability (none of the three archived) and images in import order.</summary>
internal sealed record CatalogSourceBinding(Guid Id, Guid ClassId, Guid VariantId, bool Available, IReadOnlyList<CatalogSourceAsset> Assets);

internal sealed record CatalogSourceVariant(Guid Id, bool Archived);

/// <summary>The part of the catalog a generation recipe refers to, read in one transaction.</summary>
internal sealed record CatalogSources(IReadOnlyList<CatalogSourceBinding> Bindings, IReadOnlyList<CatalogSourceVariant> Variants);

/// <summary>A class for a dataset plan: its name and whether it is archived; the list is in catalog order.</summary>
internal sealed record DatasetCatalogClass(Guid Id, string Name, bool Archived);

/// <summary>A binding for a dataset plan: its class, the name of its project variant and availability (none of the three archived).</summary>
internal sealed record DatasetCatalogBinding(Guid Id, Guid ClassId, string VariantName, bool Available);

/// <summary>The whole catalog as a dataset plan sees it, read in one transaction.</summary>
internal sealed record DatasetCatalog(IReadOnlyList<DatasetCatalogClass> Classes, IReadOnlyList<DatasetCatalogBinding> Bindings)
{
    /// <summary>SHA-256 of what decides object roles and the class map: classes in order with names and archive, bindings with availability.</summary>
    internal string Fingerprint()
    {
        var text = new StringBuilder();
        foreach (var item in Classes) text.Append("c:").Append(item.Id.ToString("D")).Append(':').Append(item.Archived ? '1' : '0').Append(':').Append(item.Name).Append('\n');
        foreach (var item in Bindings) text.Append("b:").Append(item.Id.ToString("D")).Append(':').Append(item.ClassId.ToString("D")).Append(':')
            .Append(item.Available ? '1' : '0').Append(':').Append(item.VariantName).Append('\n');
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }
}
