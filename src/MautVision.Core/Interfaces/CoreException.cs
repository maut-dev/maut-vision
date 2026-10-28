namespace MautVision.Core.Interfaces;

/// <summary>
/// Ошибка, о которой можно сообщить пользователю. Code — стабильный код вида
/// <c>&lt;область&gt;.&lt;причина&gt;</c>; Message предназначено для журнала. Details — безопасные для показа значения,
/// которые помогают UI отреагировать (например, поле формы: <c>field</c>); null, если их нет.
/// </summary>
public sealed class CoreException(string code, string message, Exception? innerException = null)
    : Exception(message, innerException)
{
    public string Code { get; } = code;

    public IReadOnlyDictionary<string, string>? Details { get; init; }
}
