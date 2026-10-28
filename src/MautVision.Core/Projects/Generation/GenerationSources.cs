using MautVision.Core.Interfaces;
using MautVision.Core.Interfaces.Projects;
using MautVision.Core.Interfaces.Projects.Generation;
using MautVision.Core.Projects.Backgrounds;
using MautVision.Core.Projects.Catalog;

namespace MautVision.Core.Projects.Generation;

/// <summary>An image of a manifest binding; its file is &lt;Id&gt;&lt;Extension&gt; under class-assets or the run snapshot.</summary>
internal sealed record ManifestAsset(Guid Id, string Extension);

/// <summary>A binding the run draws from: class, project variant and images in import order.</summary>
internal sealed record ManifestBinding(Guid Id, Guid ClassId, Guid VariantId, IReadOnlyList<ManifestAsset> Assets);

/// <summary>
/// The ordered inputs of a run, fixed when it is prepared and stored with it: bindings in recipe order (only those of weighted
/// variants) and active backgrounds in recipe set order, then order of addition. A resumed run reads the same lists.
/// </summary>
internal sealed record GenerationManifest(IReadOnlyList<ManifestBinding> Bindings, IReadOnlyList<Guid> Backgrounds)
{
    /// <summary>The sampling layout of a recipe over this manifest.</summary>
    internal GenerationLayout Layout(GenerationRecipe recipe)
    {
        var weights = recipe.VariantWeights.Select(weight => new LayoutWeight(weight.Percentage,
            Bindings.Select((binding, index) => (binding, index)).Where(entry => entry.binding.VariantId == weight.VariantId)
                .Select(entry => entry.index).ToArray())).ToArray();
        return new GenerationLayout(recipe.Width, recipe.Height, recipe.ObjectsPerImage, recipe.EmptyProbability, recipe.Scale, weights,
            Bindings.Select(binding => binding.Assets.Count).ToArray(), Backgrounds.Count);
    }
}

/// <summary>
/// Checks the sources of a recipe as a run starts and builds its manifest. Order of checks:
/// canvas against the project input, background sets (available, of the canvas size), weighted project variants, bindings
/// (available, with images, each with an alpha channel), weighted variants without bindings, backgrounds present. Nothing is
/// decoded here: images without transparent pixels despite an alpha channel are found when their sprite is first made.
/// </summary>
internal static class GenerationSources
{
    internal static async Task<GenerationManifest> LoadAsync(GenerationRecipe recipe, InputSize projectInput, ClassesManager classes,
        BackgroundsManager backgrounds, CancellationToken cancellationToken)
    {
        if (recipe.Width != projectInput.Width || recipe.Height != projectInput.Height)
            throw Error("generation.canvas_mismatch", $"Recipe {recipe.Id} canvas {recipe.Width}×{recipe.Height} differs from the project input.");
        var sets = await backgrounds.ReadGenerationSourcesAsync(recipe.BackgroundSetIds, cancellationToken).ConfigureAwait(false);
        foreach (var setId in recipe.BackgroundSetIds)
        {
            var set = sets.FirstOrDefault(item => item.Id == setId);
            if (set is null || set.Archived)
                throw Error("generation.backgrounds_unavailable", $"Background set {setId} is deleted or archived.");
            if (set.TargetSize.Width != recipe.Width || set.TargetSize.Height != recipe.Height)
                throw Error("generation.background_size_mismatch", $"Background set {setId} size differs from the canvas.");
        }
        var weighted = recipe.VariantWeights.Where(weight => weight.Percentage > 0).Select(weight => weight.VariantId).ToArray();
        var catalog = await classes.ReadGenerationSourcesAsync(recipe.ClassVariantIds, weighted, cancellationToken).ConfigureAwait(false);
        foreach (var variantId in weighted)
        {
            if (catalog.Variants.FirstOrDefault(item => item.Id == variantId) is not { Archived: false })
                throw Error("generation.variant_unavailable", $"Project variant {variantId} is deleted or archived.");
        }
        var bindings = new List<ManifestBinding>();
        foreach (var bindingId in recipe.ClassVariantIds)
        {
            var binding = catalog.Bindings.FirstOrDefault(item => item.Id == bindingId);
            if (binding is null || !binding.Available)
                throw Error("generation.variant_unavailable", $"Class variant {bindingId} is deleted or archived.");
            // Bindings of variants without a share are never drawn.
            if (!weighted.Contains(binding.VariantId)) continue;
            if (binding.Assets.Count == 0)
                throw Error("generation.variant_without_asset", $"Class variant {bindingId} has no images.");
            if (binding.Assets.FirstOrDefault(asset => !asset.HasAlpha) is { } opaque)
                throw Error("generation.unsupported_asset", $"Image {opaque.Id} of class variant {bindingId} has no transparent pixels.");
            bindings.Add(new ManifestBinding(binding.Id, binding.ClassId, binding.VariantId,
                binding.Assets.Select(asset => new ManifestAsset(asset.Id, asset.Extension)).ToArray()));
        }
        foreach (var variantId in weighted)
        {
            if (!bindings.Any(binding => binding.VariantId == variantId))
                throw Error("generation.variant_without_asset", $"Project variant {variantId} has no class variant with images in the recipe.");
        }
        var items = recipe.BackgroundSetIds.SelectMany(setId => sets.First(set => set.Id == setId).Items).ToArray();
        if (items.Length == 0) throw Error("generation.backgrounds_empty", $"Background sets of recipe {recipe.Id} have no active backgrounds.");
        return new GenerationManifest(bindings, items);
    }

    private static CoreException Error(string code, string message) => new(code, message);
}
