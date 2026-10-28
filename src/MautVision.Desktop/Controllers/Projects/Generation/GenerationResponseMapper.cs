using MautVision.Core.Interfaces;
using MautVision.Core.Interfaces.Projects.Generation;
using MautVision.Desktop.Controllers.Jobs;
using MautVision.Desktop.Controllers.Projects.Generation.RequestDto;
using MautVision.Desktop.Controllers.Projects.Generation.ResponseDto;
using MautVision.Desktop.Controllers.Projects.Images.ResponseDto;

namespace MautVision.Desktop.Controllers.Projects.Generation;

/// <summary>Maps generation contracts to DTOs and the form back to the recipe input.</summary>
internal static class GenerationResponseMapper
{
    internal static GenerationRecipeInput Map(GenerationRecipeFormDto form) => new(
        form.Name,
        form.Description,
        form.Width,
        form.Height,
        form.BackgroundSetIds,
        form.ClassVariantIds,
        form.VariantWeights.Select(weight => new GenerationVariantWeight(weight.VariantId, weight.Percentage)).ToArray(),
        form.ObjectsPerImage,
        form.EmptyProbability,
        form.Scale);

    internal static GenerationRecipeFormDto Map(GenerationRecipeInput input) => new(
        input.Name,
        input.Description,
        input.Width,
        input.Height,
        input.BackgroundSetIds.ToArray(),
        input.ClassVariantIds.ToArray(),
        input.VariantWeights.Select(weight => new GenerationVariantWeightDto(weight.VariantId, weight.Percentage)).ToArray(),
        input.ObjectsPerImage,
        input.EmptyProbability,
        input.Scale);

    internal static GenerationRecipeResponseDto Map(GenerationRecipe recipe) => new(
        recipe.Id,
        recipe.Name,
        recipe.Description,
        recipe.Width,
        recipe.Height,
        recipe.BackgroundSetIds.ToArray(),
        recipe.ClassVariantIds.ToArray(),
        recipe.VariantWeights.Select(weight => new GenerationVariantWeightDto(weight.VariantId, weight.Percentage)).ToArray(),
        recipe.ObjectsPerImage,
        recipe.EmptyProbability,
        recipe.Scale,
        recipe.Revision,
        recipe.CreatedAt,
        recipe.UpdatedAt,
        recipe.ArchivedAt);

    internal static GenerationRunResponseDto Map(GenerationRun run, Func<string, string> messageFor) => new(
        JobResponseMapper.Map(run.Job, messageFor),
        Map(run.Recipe),
        run.Seed,
        run.RequestedCount,
        run.ProducedCount,
        run.MissingObjectFrames,
        run.OutputGroupId,
        run.OutputGroupName);

    internal static GenerationSampleObjectDto Map(GenerationSampleObject item)
        => new(item.ClassId, item.ClassVariantId, new ImageBoxResponseDto(item.Box.X, item.Box.Y, item.Box.Width, item.Box.Height));

    internal static GenerationChangedEventDto Map(GenerationChangedEventArgs args) => new(
        args.ProjectId,
        args.Recipes.Select(change => new GenerationEntityChangeDto(change.Id, change.Revision)).ToArray(),
        args.Runs.ToArray());

    /// <summary>A seed of the form: an integer 0–4 294 967 295 (<c>generation.seed_invalid</c>).</summary>
    internal static uint Seed(long value) => value is >= 0 and <= uint.MaxValue
        ? (uint)value
        : throw new CoreException("generation.seed_invalid", $"Seed {value} is outside the uint32 range.");
}
