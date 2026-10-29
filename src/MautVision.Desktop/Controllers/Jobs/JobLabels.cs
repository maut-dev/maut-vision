using MautVision.Core.Interfaces;

namespace MautVision.Desktop.Controllers.Jobs;

/// <summary>
/// Labels a feature gives its job when it enqueues it, shown after the kind in lists of jobs ("Training · Dataset 3"): the name of the
/// group, set, recipe or dataset the job works on as a text, a code of the UI catalog jobLabels with values when words surround the data
/// ("Version N", "class · variant"). The UI composes the words in the language of the user.
/// </summary>
internal static class JobLabels
{
    /// <summary>A name the user typed: shown as is.</summary>
    internal static CodedText Name(string name) => CodedText.Plain(name);

    /// <summary>"Version N" (jobLabels.version).</summary>
    internal static CodedText Version(int number) => CodedText.Of("jobLabels.version", ("number", number));

    /// <summary>"Version N · dataset" (jobLabels.version_dataset).</summary>
    internal static CodedText VersionDataset(int number, string dataset) => CodedText.Of("jobLabels.version_dataset", ("number", number), ("dataset", dataset));

    /// <summary>"class · variant" (jobLabels.class_variant).</summary>
    internal static CodedText ClassVariant(string? className, string? variantName)
        => CodedText.Of("jobLabels.class_variant", ("class", className ?? ""), ("variant", variantName ?? ""));
}
