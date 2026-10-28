using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MautVision.Core.Interfaces;
using MautVision.Core.Interfaces.Projects.Datasets;
using MautVision.Core.Interfaces.Projects.Images;
using MautVision.Core.Jobs;
using MautVision.Core.Projects.Catalog;
using MautVision.Core.Projects.Images;

namespace MautVision.Core.Projects.Datasets;

/// <summary>A frame of a plan: its source image and group, the split, the YOLO boxes of targets and the negatives.</summary>
internal sealed record SelectedFrame(ImageSource Image, ImageSourceGroup Group, DatasetSplit Split, DatasetBox[] Boxes, DatasetNegative[] Negatives);

/// <summary>
/// What a specification selects from the sources: class map, variant names, frames in sample order (train, val, test, each by
/// image ID), composition, skip reasons, warnings and the plan hash, which the build compares to find a stale plan.
/// </summary>
internal sealed record DatasetSelectionResult(
    IReadOnlyList<DatasetClassEntry> Classes,
    IReadOnlyList<DatasetVariantEntry> Variants,
    IReadOnlyList<SelectedFrame> Frames,
    DatasetComposition Composition,
    DatasetPlanSkips Skipped,
    IReadOnlyList<DatasetPlanWarning> Warnings,
    bool Sufficient,
    string Hash,
    string SourceRevision);

/// <summary>
/// The dataset selection policy:
/// groups with a split hint go wholly to their split; archived or deleted classes and variants turn objects into negatives
/// instead of failing the plan; skip reasons are counted. Frames of unassigned groups are split deterministically: components
/// of a union-find over the source family and the pixel hash, the key of a component is the ordinal minimum of
/// "&lt;family&gt;:&lt;pixelHash&gt;", and v = uint32BE(sha256("&lt;seed&gt;:&lt;key&gt;")[0..4]) / 2^32 goes to test below testRatio, to val
/// below testRatio + valRatio and to train otherwise.
/// </summary>
internal static class DatasetSelection
{
    internal const int MaxNameLength = 120;
    internal const int MaxDescriptionLength = 4000;
    internal const string InsufficientSamples = "datasets.insufficient_samples";
    internal const string MixedSourceSplits = "datasets.mixed_source_splits";
    internal const string ObjectsAsNegatives = "datasets.objects_as_negatives";
    internal const string SplitCollision = "datasets.split_collision";

    /// <summary>The geometry tolerance: right and bottom edges up to 1.000001.</summary>
    private const double Edge = 1.000001;

    /// <summary>Trims the texts and checks every field; the first wrong one is reported by its code with Details["field"].</summary>
    internal static DatasetSpecification Validate(DatasetSpecification specification)
    {
        ArgumentNullException.ThrowIfNull(specification);
        var name = specification.Name?.Trim() ?? "";
        if (name.Length is 0 or > MaxNameLength) throw Invalid("datasets.name_invalid", "name", "A dataset name has 1–120 characters.");
        var description = specification.Description?.Trim() ?? "";
        if (description.Length > MaxDescriptionLength) throw Invalid("datasets.description_invalid", "description", "A description has at most 4000 characters.");
        if (!IsIdList(specification.GroupIds, required: true)) throw Invalid("datasets.selection_invalid", "groupIds", "Choose one or more distinct image groups.");
        if (!IsIdList(specification.ClassIds, required: true)) throw Invalid("datasets.selection_invalid", "classIds", "Choose one or more distinct classes.");
        if (!IsIdList(specification.TargetVariantIds, required: false) || !IsIdList(specification.NegativeVariantIds, required: false)
            || specification.TargetVariantIds.Intersect(specification.NegativeVariantIds).Any())
        {
            throw Invalid("datasets.variants_invalid", "variants", "Target and negative variants are distinct lists without common entries.");
        }
        if (!Enum.IsDefined(specification.UnassignedVariantRole) || !Enum.IsDefined(specification.UnselectedClassRole))
            throw Invalid("datasets.roles_invalid", "roles", "Object roles are not known.");
        var (val, test) = (specification.ValRatio, specification.TestRatio);
        if (!double.IsFinite(val) || !double.IsFinite(test) || val <= 0 || test < 0 || val + test >= 1)
            throw Invalid("datasets.split_invalid", "ratios", "Validation is above 0, test is 0 or more and together they stay below 1.");
        return specification with
        {
            Name = name,
            Description = description,
            GroupIds = specification.GroupIds.ToArray(),
            ClassIds = specification.ClassIds.ToArray(),
            TargetVariantIds = specification.TargetVariantIds.ToArray(),
            NegativeVariantIds = specification.NegativeVariantIds.ToArray(),
        };
    }

    /// <summary>Revision of everything a plan reads: images of the groups, the catalog and the frame size.</summary>
    internal static string SourceRevision(string imagesRevision, DatasetCatalog catalog, int width, int height)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{imagesRevision}\n{catalog.Fingerprint()}\n{width}x{height}")));

    internal static DatasetSelectionResult Select(DatasetSpecification specification, DatasetCatalog catalog, ImageSourceSet sources, int width, int height)
    {
        var classes = catalog.Classes.ToDictionary(item => item.Id);
        foreach (var id in specification.ClassIds)
        {
            if (!classes.TryGetValue(id, out var item) || item.Archived)
            {
                throw new CoreException("datasets.class_unavailable", $"Class {id} is missing or archived.")
                    { Details = new Dictionary<string, string> { ["classId"] = id.ToString("D") } };
            }
        }
        var selected = specification.ClassIds.ToHashSet();
        var classMap = catalog.Classes.Where(item => selected.Contains(item.Id)).Select((item, index) => new DatasetClassEntry(index, item.Id, item.Name)).ToArray();
        var indexes = classMap.ToDictionary(item => item.ClassId, item => item.Index);
        var bindings = catalog.Bindings.ToDictionary(item => item.Id);
        foreach (var id in specification.TargetVariantIds.Concat(specification.NegativeVariantIds))
        {
            if (!bindings.ContainsKey(id))
            {
                throw new CoreException("datasets.variants_invalid", $"Class variant {id} does not exist.")
                    { Details = new Dictionary<string, string> { ["field"] = "variants" } };
            }
        }
        var groups = sources.Groups.ToDictionary(item => item.Id);
        foreach (var id in specification.GroupIds)
        {
            if (!groups.TryGetValue(id, out var group) || group.Archived)
            {
                throw new CoreException("datasets.group_unavailable", $"Image group {id} is missing or archived.")
                    { Details = new Dictionary<string, string> { ["groupId"] = id.ToString("D") } };
            }
        }
        var targets = specification.TargetVariantIds.ToHashSet();
        var negatives = specification.NegativeVariantIds.ToHashSet();

        int unreviewed = 0, partial = 0, withoutTargets = 0, invalidGeometry = 0, demoted = 0;
        var frames = new List<SelectedFrame>(sources.Images.Count);
        var boxes = new List<DatasetBox>();
        var negativeObjects = new List<DatasetNegative>();
        foreach (var image in sources.Images)
        {
            var group = groups[image.GroupId];
            if (!image.Reviewed)
            {
                unreviewed++;
                continue;
            }
            if (group.Origin == ImageGroupOrigin.Generated && group.GenerationState != ImageGenerationState.Completed && !specification.AllowPartialGeneration)
            {
                partial++;
                continue;
            }
            if (!ValidGeometry(image))
            {
                invalidGeometry++;
                continue;
            }
            boxes.Clear();
            negativeObjects.Clear();
            var frameDemoted = 0;
            foreach (var item in image.Objects)
            {
                var (cx, cy, w, h) = (item.Box.X + item.Box.Width / 2, item.Box.Y + item.Box.Height / 2, item.Box.Width, item.Box.Height);
                if (Role(specification, classes, bindings, selected, targets, negatives, item, ref frameDemoted))
                    boxes.Add(new DatasetBox(indexes[item.ClassId], item.ClassId, item.ClassVariantId, cx, cy, w, h));
                else
                    negativeObjects.Add(new DatasetNegative(item.ClassId, item.ClassVariantId, cx, cy, w, h));
            }
            if (boxes.Count == 0 && !specification.IncludeReviewedEmpty)
            {
                withoutTargets++;
                continue;
            }
            demoted += frameDemoted;
            frames.Add(new SelectedFrame(image, group, DatasetSplit.Train, [.. boxes], [.. negativeObjects]));
        }

        var warnings = new List<DatasetPlanWarning>();
        var assigned = Assign(frames, specification, out var mixed);
        if (mixed > 0) warnings.Add(new DatasetPlanWarning(MixedSourceSplits, mixed));
        if (demoted > 0) warnings.Add(new DatasetPlanWarning(ObjectsAsNegatives, demoted));
        var collisions = assigned.GroupBy(frame => frame.Image.PixelHash, StringComparer.Ordinal)
            .Where(same => same.Select(frame => frame.Split).Distinct().Count() > 1).Sum(same => same.Count());
        if (collisions > 0) warnings.Add(new DatasetPlanWarning(SplitCollision, collisions));
        var ordered = assigned.OrderBy(frame => frame.Split).ThenBy(frame => frame.Image.Id.ToString("D"), StringComparer.Ordinal).ToArray();
        var counts = new DatasetSplitCounts(ordered.Count(frame => frame.Split == DatasetSplit.Train), ordered.Count(frame => frame.Split == DatasetSplit.Validation),
            ordered.Count(frame => frame.Split == DatasetSplit.Test));
        var sufficient = counts.Train > 0 && counts.Validation > 0 && (specification.TestRatio == 0 || counts.Test > 0);
        if (!sufficient) warnings.Insert(0, new DatasetPlanWarning(InsufficientSamples, 0));

        var perClass = new int[classMap.Length];
        foreach (var box in ordered.SelectMany(frame => frame.Boxes)) perClass[box.Index]++;
        var perGroup = ordered.GroupBy(frame => frame.Group.Id).ToDictionary(same => same.Key, same => same.Count());
        var composition = new DatasetComposition(
            counts,
            ordered.Count(frame => frame.Boxes.Length == 0),
            ordered.Count(frame => frame.Group.Origin == ImageGroupOrigin.Manual),
            ordered.Count(frame => frame.Group.Origin == ImageGroupOrigin.Generated),
            perClass.Sum(),
            ordered.Sum(frame => frame.Negatives.Length),
            classMap.Select(item => new DatasetClassCount(item.Index, item.ClassId, item.Name, perClass[item.Index])).ToArray(),
            specification.GroupIds.Select(id => new DatasetGroupCount(id, groups[id].Name, perGroup.GetValueOrDefault(id))).ToArray());

        var used = ordered.SelectMany(frame => frame.Boxes.Select(box => box.ClassVariantId).Concat(frame.Negatives.Select(item => item.ClassVariantId)))
            .OfType<Guid>().ToHashSet();
        var variants = catalog.Bindings.Where(item => selected.Contains(item.ClassId) || used.Contains(item.Id))
            .Select(item => new DatasetVariantEntry(item.Id, item.ClassId, item.VariantName)).ToArray();
        var sourceRevision = SourceRevision(sources.Revision, catalog, width, height);
        return new DatasetSelectionResult(classMap, variants, ordered, composition, new DatasetPlanSkips(unreviewed, partial, withoutTargets, invalidGeometry),
            warnings, sufficient, Hash(specification, width, height, sourceRevision, ordered), sourceRevision);
    }

    /// <summary>
    /// Splits the frames: a group with a hint gives its split, frames of unassigned groups are hashed by their components. Mixed —
    /// frames of components (over all frames) that join different hints or a hint with unassigned frames; each frame follows its group.
    /// </summary>
    private static List<SelectedFrame> Assign(List<SelectedFrame> frames, DatasetSpecification specification, out int mixed)
    {
        var all = new UnionFind(frames.Count);
        all.Join(frames, Family, frame => frame.Image.PixelHash);
        mixed = 0;
        foreach (var component in Enumerable.Range(0, frames.Count).GroupBy(all.Find))
        {
            var hints = component.Select(index => frames[index].Group.SplitHint).Distinct().Count();
            if (hints > 1) mixed += component.Count();
        }

        var unassigned = frames.Where(frame => frame.Group.SplitHint == ImageSplitHint.Unassigned).ToList();
        var splits = Split(unassigned.Select(frame => (Family(frame), frame.Image.PixelHash)).ToArray(), specification.Seed, specification.ValRatio,
            specification.TestRatio);
        var hashed = new Dictionary<SelectedFrame, DatasetSplit>(ReferenceEqualityComparer.Instance);
        for (var index = 0; index < unassigned.Count; index++) hashed[unassigned[index]] = splits[index];
        return frames.Select(frame => frame with
        {
            Split = frame.Group.SplitHint switch
            {
                ImageSplitHint.Train => DatasetSplit.Train,
                ImageSplitHint.Validation => DatasetSplit.Validation,
                ImageSplitHint.Test => DatasetSplit.Test,
                _ => hashed[frame],
            },
        }).ToList();
    }

    /// <summary>
    /// The split for frames given as (source family, pixel hash), in their order: union-find over
    /// both keys, the ordinal minimum of "family:pixelHash" per component, then the hash threshold.
    /// </summary>
    internal static DatasetSplit[] Split(IReadOnlyList<(string Family, string PixelHash)> frames, uint seed, double valRatio, double testRatio)
    {
        var union = new UnionFind(frames.Count);
        union.Join(frames, frame => frame.Family, frame => frame.PixelHash);
        var keys = new Dictionary<int, string>();
        for (var index = 0; index < frames.Count; index++)
        {
            var root = union.Find(index);
            var key = frames[index].Family + ":" + frames[index].PixelHash;
            if (!keys.TryGetValue(root, out var current) || string.CompareOrdinal(key, current) < 0) keys[root] = key;
        }
        var cache = new Dictionary<int, DatasetSplit>();
        var result = new DatasetSplit[frames.Count];
        for (var index = 0; index < frames.Count; index++)
        {
            var root = union.Find(index);
            if (!cache.TryGetValue(root, out var split))
            {
                var value = Value(seed, keys[root]);
                split = value < testRatio ? DatasetSplit.Test : value < testRatio + valRatio ? DatasetSplit.Validation : DatasetSplit.Train;
                cache[root] = split;
            }
            result[index] = split;
        }
        return result;
    }

    /// <summary>uint32BE of the first four bytes of sha256("&lt;seed&gt;:&lt;key&gt;") divided by 2^32.</summary>
    internal static double Value(uint seed, string key)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(seed.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + key), hash);
        return BinaryPrimitives.ReadUInt32BigEndian(hash) / 4294967296.0;
    }

    /// <summary>The lineage family; a generated frame is a family of its own.</summary>
    internal static string Family(SelectedFrame frame)
        => frame.Group.Origin == ImageGroupOrigin.Generated ? "generated-image:" + frame.Image.Id.ToString("D") : frame.Image.SourceFamilyId;

    /// <summary>True — a target; false — a negative. Rejecting roles throw; archived or deleted classes and variants are counted.</summary>
    private static bool Role(DatasetSpecification specification, Dictionary<Guid, DatasetCatalogClass> classes, Dictionary<Guid, DatasetCatalogBinding> bindings,
        HashSet<Guid> selected, HashSet<Guid> targets, HashSet<Guid> negatives, ImageSourceObject item, ref int demoted)
    {
        if (!classes.TryGetValue(item.ClassId, out var owner) || owner.Archived)
        {
            demoted++;
            return false;
        }
        if (!selected.Contains(item.ClassId))
        {
            if (specification.UnselectedClassRole == DatasetUnselectedClassRole.Reject)
                throw new CoreException("datasets.unselected_class", $"An object of the unselected class {item.ClassId} is rejected by the specification.");
            return false;
        }
        if (item.ClassVariantId is not { } variant)
        {
            return specification.UnassignedVariantRole switch
            {
                DatasetUnassignedVariantRole.Target => true,
                DatasetUnassignedVariantRole.Negative => false,
                _ => throw new CoreException("datasets.unassigned_variant", "An object without a class variant is rejected by the specification."),
            };
        }
        if (!bindings.TryGetValue(variant, out var binding) || !binding.Available)
        {
            demoted++;
            return false;
        }
        if (targets.Contains(variant)) return true;
        if (negatives.Contains(variant)) return false;
        throw new CoreException("datasets.variant_role_missing", $"Class variant {variant} of a selected class has no role.")
            { Details = new Dictionary<string, string> { ["classVariantId"] = variant.ToString("D") } };
    }

    private static bool ValidGeometry(ImageSource image)
    {
        if (image.Width <= 0 || image.Height <= 0) return false;
        if (image.Roi is { } roi && !ValidBox(roi)) return false;
        foreach (var item in image.Objects)
        {
            if (!ValidBox(item.Box)) return false;
        }
        return true;
    }

    private static bool ValidBox(ImageBox box)
        => double.IsFinite(box.X) && double.IsFinite(box.Y) && double.IsFinite(box.Width) && double.IsFinite(box.Height)
            && box.X >= 0 && box.Y >= 0 && box.Width > 0 && box.Height > 0 && box.X + box.Width <= Edge && box.Y + box.Height <= Edge;

    private static string Hash(DatasetSpecification specification, int width, int height, string sourceRevision, IReadOnlyList<SelectedFrame> frames)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(JsonSerializer.SerializeToUtf8Bytes(specification, JobsManager.Json));
        hash.AppendData(Encoding.UTF8.GetBytes($"\n{width}x{height}\n{sourceRevision}\n"));
        var line = new StringBuilder();
        foreach (var frame in frames)
        {
            line.Clear().Append(frame.Image.Id.ToString("D")).Append(':').Append((int)frame.Split).Append(':').Append(frame.Image.AnnotationRevision).Append('\n');
            hash.AppendData(Encoding.UTF8.GetBytes(line.ToString()));
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static bool IsIdList(IReadOnlyList<Guid>? ids, bool required)
        => ids is not null && (!required || ids.Count > 0) && !ids.Contains(Guid.Empty) && ids.Distinct().Count() == ids.Count;

    private static CoreException Invalid(string code, string field, string message)
        => new(code, message) { Details = new Dictionary<string, string> { ["field"] = field } };

    /// <summary>Union-find with path halving, joining items that share any of the given keys.</summary>
    private sealed class UnionFind(int count)
    {
        private readonly int[] parent = Enumerable.Range(0, count).ToArray();

        internal int Find(int index)
        {
            while (parent[index] != index)
            {
                parent[index] = parent[parent[index]];
                index = parent[index];
            }
            return index;
        }

        /// <summary>Joins each item with the last earlier item that had the same key, key by key.</summary>
        internal void Join<T>(IReadOnlyList<T> items, params Func<T, string>[] keys)
        {
            var seen = keys.Select(_ => new Dictionary<string, int>(StringComparer.Ordinal)).ToArray();
            for (var index = 0; index < items.Count; index++)
            {
                for (var key = 0; key < keys.Length; key++)
                {
                    var value = keys[key](items[index]);
                    if (seen[key].TryGetValue(value, out var previous)) parent[Find(index)] = Find(previous);
                    seen[key][value] = index;
                }
            }
        }
    }
}
