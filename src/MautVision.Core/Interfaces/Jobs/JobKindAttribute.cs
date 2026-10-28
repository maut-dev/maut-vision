namespace MautVision.Core.Interfaces.Jobs;

/// <summary>
/// Стабильный ключ вида задачи, например <c>images.import</c>: lowercase, сегменты через точку.
/// Ключ хранится в задаче; его смена — несовместимое изменение (старые задачи получат статус Blocked).
/// </summary>
[AttributeUsage(AttributeTargets.Interface, AllowMultiple = false, Inherited = false)]
public sealed class JobKindAttribute(string key) : Attribute
{
    public string Key { get; } = key;
}
