namespace MautVision.Desktop.Controllers.Jobs;

/// <summary>
/// Labels a feature gives its job when it enqueues it, shown after the kind in lists of jobs («Обучение · Датасет 3»): the name of the
/// group, set, recipe or dataset the job works on, «Класс · вариант» for class images, «Версия N» for a model version.
/// </summary>
internal static class JobLabels
{
    internal static string Version(int number) => $"Версия {number}";
}
