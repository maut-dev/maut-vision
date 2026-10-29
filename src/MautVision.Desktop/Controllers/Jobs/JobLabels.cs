using MautVision.Core.Interfaces;

namespace MautVision.Desktop.Controllers.Jobs;

/// <summary>
/// Labels a feature gives its job when it enqueues it, shown after the kind in lists of jobs ("Training · Dataset 3"): the name of the
/// group, set, recipe or dataset the job works on as a text, a code of the UI catalog jobLabels with values when words surround the data
/// ("Version N", "class · variant"). The UI composes the words in the language of the user.
/// A job an AI client starts through MCP names the client: while <see cref="RequestedBy"/> is in effect for the call, every label is
/// wrapped into jobLabels.ai_label ("AI (Claude Code) · Dataset 3") with the inner label flattened into its values.
/// </summary>
internal static class JobLabels
{
    /// <summary>Values of the inner label inside jobLabels.ai_label: its code, its text and its values under this prefix.</summary>
    internal const string InnerCode = "labelCode";
    internal const string InnerText = "labelText";
    internal const string InnerArgPrefix = "label.";

    private static readonly AsyncLocal<string?> Requester = new();

    /// <summary>Labels made in this asynchronous flow name the AI client; disposing restores the previous state.</summary>
    internal static IDisposable RequestedBy(string client)
    {
        var previous = Requester.Value;
        Requester.Value = client;
        return new Restore(previous);
    }

    /// <summary>A name the user typed: shown as is.</summary>
    internal static CodedText Name(string name) => ForRequester(CodedText.Plain(name));

    /// <summary>"Version N" (jobLabels.version).</summary>
    internal static CodedText Version(int number) => ForRequester(CodedText.Of("jobLabels.version", ("number", number)));

    /// <summary>"Version N · dataset" (jobLabels.version_dataset).</summary>
    internal static CodedText VersionDataset(int number, string dataset)
        => ForRequester(CodedText.Of("jobLabels.version_dataset", ("number", number), ("dataset", dataset)));

    /// <summary>"class · variant" (jobLabels.class_variant).</summary>
    internal static CodedText ClassVariant(string? className, string? variantName)
        => ForRequester(CodedText.Of("jobLabels.class_variant", ("class", className ?? ""), ("variant", variantName ?? "")));

    /// <summary>
    /// "AI: client" (jobLabels.ai) without a label, "AI (client) · label" (jobLabels.ai_label) with one. The English text is the fallback
    /// for a catalog that does not know the code.
    /// </summary>
    internal static CodedText Ai(string client, CodedText? inner)
    {
        if (inner is null || (inner.Code is null && string.IsNullOrWhiteSpace(inner.Text)))
            return new CodedText("jobLabels.ai", new Dictionary<string, string>(StringComparer.Ordinal) { ["client"] = client }, $"AI: {client}");
        var args = new Dictionary<string, string>(StringComparer.Ordinal) { ["client"] = client };
        if (inner.Code is not null) args[InnerCode] = inner.Code;
        if (inner.Text is not null) args[InnerText] = inner.Text;
        foreach (var (name, value) in inner.Args ?? new Dictionary<string, string>()) args[InnerArgPrefix + name] = value;
        return new CodedText("jobLabels.ai_label", args, $"AI ({client}) · {inner.Text ?? inner.Code}");
    }

    private static CodedText ForRequester(CodedText label) => Requester.Value is { } client ? Ai(client, label) : label;

    private sealed class Restore(string? previous) : IDisposable
    {
        public void Dispose() => Requester.Value = previous;
    }
}
