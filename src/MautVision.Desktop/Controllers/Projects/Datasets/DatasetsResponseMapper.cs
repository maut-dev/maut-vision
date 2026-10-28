using MautVision.Core.Interfaces;
using MautVision.Core.Interfaces.Projects;
using MautVision.Core.Interfaces.Projects.Datasets;
using MautVision.Core.Interfaces.Projects.Images;
using MautVision.Core.Interfaces.Projects.Resources;
using MautVision.Desktop.Controllers.Jobs;
using MautVision.Desktop.Controllers.Projects.Catalog.ResponseDto;
using MautVision.Desktop.Controllers.Projects.Datasets.RequestDto;
using MautVision.Desktop.Controllers.Projects.Datasets.ResponseDto;
using MautVision.Desktop.Controllers.Projects.Images.ResponseDto;
using MautVision.Desktop.Resources;

namespace MautVision.Desktop.Controllers.Projects.Datasets;

/// <summary>Maps dataset contracts to DTOs and the form back to the specification; enum values travel as lowercase strings.</summary>
internal static class DatasetsResponseMapper
{
    internal static DatasetSpecification Map(DatasetSpecificationDto form) => new(
        form.Name,
        form.Description,
        form.GroupIds,
        form.ClassIds,
        form.TargetVariantIds,
        form.NegativeVariantIds,
        form.UnassignedVariantRole switch
        {
            "target" => DatasetUnassignedVariantRole.Target,
            "negative" => DatasetUnassignedVariantRole.Negative,
            "reject" => DatasetUnassignedVariantRole.Reject,
            _ => throw Invalid("roles_invalid", "unassignedVariantRole"),
        },
        form.UnselectedClassRole switch
        {
            "negative" => DatasetUnselectedClassRole.Negative,
            "reject" => DatasetUnselectedClassRole.Reject,
            _ => throw Invalid("roles_invalid", "unselectedClassRole"),
        },
        form.IncludeReviewedEmpty,
        form.AllowPartialGeneration,
        form.Seed is >= 0 and <= uint.MaxValue ? (uint)form.Seed : throw Invalid("seed_invalid", "seed"),
        form.ValRatio,
        form.TestRatio);

    internal static DatasetSpecificationDto Map(DatasetSpecification specification) => new(
        specification.Name,
        specification.Description,
        specification.GroupIds.ToArray(),
        specification.ClassIds.ToArray(),
        specification.TargetVariantIds.ToArray(),
        specification.NegativeVariantIds.ToArray(),
        specification.UnassignedVariantRole switch
        {
            DatasetUnassignedVariantRole.Target => "target",
            DatasetUnassignedVariantRole.Negative => "negative",
            _ => "reject",
        },
        specification.UnselectedClassRole == DatasetUnselectedClassRole.Negative ? "negative" : "reject",
        specification.IncludeReviewedEmpty,
        specification.AllowPartialGeneration,
        specification.Seed,
        specification.ValRatio,
        specification.TestRatio);

    internal static DatasetPlanResponseDto Map(DatasetPlan plan) => new(
        plan.Id,
        Map(plan.Specification),
        plan.Width,
        plan.Height,
        plan.Classes.Select(Map).ToArray(),
        Map(plan.Counts),
        Map(plan.Composition),
        new DatasetPlanSkipsDto(plan.Skipped.Unreviewed, plan.Skipped.PartialGeneration, plan.Skipped.WithoutTargets, plan.Skipped.InvalidGeometry,
            plan.Skipped.Total),
        plan.Warnings.Select(warning => new DatasetPlanWarningDto(warning.Code, warning.Count, DatasetErrorMessages.Warning(warning))).ToArray(),
        plan.SufficientSamples,
        plan.Counts.Total > 0 && plan.Warnings.All(warning => warning.Code != "datasets.split_collision"),
        plan.Hash,
        plan.CreatedAt);

    internal static DatasetVersionResponseDto Map(DatasetVersion version, InputSize input) => new(
        version.Id,
        version.Name,
        version.Description,
        version.Revision,
        version.ArchivedAt,
        Key(version.IntegrityState),
        version.VerifiedAt,
        version.Width,
        version.Height,
        version.Width == input.Width && version.Height == input.Height,
        Map(version.Specification),
        version.ClassMap.Select(Map).ToArray(),
        version.Variants.Select(item => new DatasetVariantEntryDto(item.ClassVariantId, item.ClassId, item.Name)).ToArray(),
        Map(version.Counts),
        Map(version.Composition),
        version.ManifestHash,
        version.PlanId,
        version.CreatedAt,
        version.UpdatedAt,
        version.DeletedAt);

    internal static DatasetSampleResponseDto Map(string projectId, DatasetSample sample, ResourceHandles resources)
    {
        var reference = new ResourceReference(DatasetSample.ResourceKind, sample.ResourceId);
        return new DatasetSampleResponseDto(
            sample.Id,
            Key(sample.Split),
            sample.Format == ImageFileFormat.Jpeg ? "jpeg" : "png",
            sample.Width,
            sample.Height,
            sample.ByteSize,
            sample.ImageHash,
            sample.LabelHash,
            sample.PixelHash,
            sample.AnnotationRevision,
            sample.Roi is { } roi ? new ImageBoxResponseDto(roi.X, roi.Y, roi.Width, roi.Height) : null,
            sample.Boxes.Select(box => new DatasetBoxDto(box.Index, box.ClassId, box.ClassVariantId, box.CenterX, box.CenterY, box.Width, box.Height)).ToArray(),
            sample.Negatives.Select(item => new DatasetNegativeDto(item.ClassId, item.ClassVariantId, item.CenterX, item.CenterY, item.Width, item.Height)).ToArray(),
            sample.GroupId,
            resources.Issue(projectId, reference, ResourceVariant.Original),
            resources.Issue(projectId, reference, ResourceVariant.Thumbnail));
    }

    internal static DatasetOperationResponseDto Map(DatasetOperation operation, Func<string, string> messageFor) => new(
        JobResponseMapper.Map(operation.Job, messageFor),
        Key(operation.Kind),
        operation.VersionId,
        operation.VersionName,
        operation.VersionAvailable,
        operation.PlanId,
        operation.Specification is { } specification ? Map(specification) : null,
        operation.FileName);

    internal static DatasetDeletionReviewResponseDto Map(DatasetDeletionReview review) => new(
        review.VersionId,
        review.Name,
        review.Samples,
        review.Operations,
        review.Blockers.Select(blocker => new DeletionBlockerResponseDto(blocker.Code, blocker.Count, DatasetErrorMessages.For(blocker))).ToArray(),
        review.Revision);

    internal static DatasetsChangedEventDto Map(DatasetsChangedEventArgs args)
        => new(args.ProjectId, args.Versions.Select(change => new DatasetEntityChangeDto(change.Id, change.Revision)).ToArray());

    internal static DatasetSplitCountsDto Map(DatasetSplitCounts counts) => new(counts.Train, counts.Validation, counts.Test, counts.Total);

    internal static DatasetBuildResultDto Map(DatasetBuildResult result)
        => new(result.VersionId, result.Total, result.Produced, result.Linked, result.Copied, result.Converted, Map(result.Counts));

    internal static DatasetVerifyResultDto Map(DatasetVerifyResult result, Func<string, string> messageFor) => new(
        result.VersionId,
        result.TotalFiles,
        result.CheckedFiles,
        result.IssueCount,
        result.Issues.Select(issue => new DatasetIntegrityIssueDto(issue.SampleId, issue.Split is { } split ? Key(split) : null, Key(issue.Role), issue.Code,
            messageFor(issue.Code))).ToArray(),
        result.State is { } state ? Key(state) : null,
        result.Bytes,
        result.ElapsedMs);

    internal static DatasetExportResultDto Map(DatasetExportResult result)
        => new(result.VersionId, result.FileName, result.TotalImages, result.ExportedImages, result.Bytes, result.ElapsedMs);

    /// <summary>train | val | test, the folder names of YOLO.</summary>
    internal static string Key(DatasetSplit split) => split switch
    {
        DatasetSplit.Train => "train",
        DatasetSplit.Validation => "val",
        _ => "test",
    };

    internal static DatasetSplit ParseSplit(string value) => value switch
    {
        "train" => DatasetSplit.Train,
        "val" => DatasetSplit.Validation,
        "test" => DatasetSplit.Test,
        _ => throw Invalid("request_invalid", "split"),
    };

    internal static DatasetOperationKind ParseKind(string value) => value switch
    {
        "build" => DatasetOperationKind.Build,
        "verify" => DatasetOperationKind.Verify,
        "export" => DatasetOperationKind.Export,
        _ => throw Invalid("request_invalid", "kind"),
    };

    private static DatasetClassEntryDto Map(DatasetClassEntry entry) => new(entry.Index, entry.ClassId, entry.Name);

    private static DatasetCompositionDto Map(DatasetComposition composition) => new(
        Map(composition.Images),
        composition.WithoutBoxes,
        composition.FromManual,
        composition.FromGenerated,
        composition.Boxes,
        composition.Negatives,
        composition.BoxesPerClass.Select(item => new DatasetClassCountDto(item.Index, item.ClassId, item.Name, item.Boxes)).ToArray(),
        composition.Groups.Select(item => new DatasetGroupCountDto(item.GroupId, item.Name, item.Images)).ToArray());

    private static string Key(DatasetIntegrityState state) => state switch
    {
        DatasetIntegrityState.Verified => "verified",
        DatasetIntegrityState.Corrupt => "corrupt",
        _ => "unverified",
    };

    private static string Key(DatasetOperationKind kind) => kind switch
    {
        DatasetOperationKind.Build => "build",
        DatasetOperationKind.Verify => "verify",
        _ => "export",
    };

    private static string Key(DatasetFileRole role) => role switch
    {
        DatasetFileRole.Manifest => "manifest",
        DatasetFileRole.DataYaml => "dataYaml",
        DatasetFileRole.Image => "image",
        _ => "label",
    };

    private static CoreException Invalid(string code, string field)
        => new("datasets." + code, $"The request field {field} is not valid.") { Details = new Dictionary<string, string> { ["field"] = field } };
}
