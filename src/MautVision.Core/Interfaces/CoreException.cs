namespace MautVision.Core.Interfaces;

/// <summary>
/// An error that can be reported to the user. Code — a stable code of the form
/// <c>&lt;area&gt;.&lt;reason&gt;</c>; Message is meant for the log. Details — values safe to display
/// that help the UI react (for example, a form field: <c>field</c>); null if there are none.
/// </summary>
public sealed class CoreException(string code, string message, Exception? innerException = null)
    : Exception(message, innerException)
{
    public string Code { get; } = code;

    public IReadOnlyDictionary<string, string>? Details { get; init; }
}
