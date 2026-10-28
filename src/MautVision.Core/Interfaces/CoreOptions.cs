using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MautVision.Core.Interfaces;

/// <summary>
/// LoggerFactory принадлежит вызывающему и не входит в сравнение конфигурации CoreFactory:
/// приёмник журнала не меняет данные, с которыми работает Core.
/// </summary>
/// <param name="WorkDirectory">User data: projects, installed weights and other mutable files.</param>
/// <param name="BaseModelCatalogPath">Catalogue shipped with the application; read in place, never copied to the work directory.</param>
public sealed record CoreOptions(string WorkDirectory, string BaseModelCatalogPath)
{
    public ILoggerFactory LoggerFactory { get; init; } = NullLoggerFactory.Instance;

    /// <summary>
    /// Папка поставки с ffmpeg.exe и ffprobe.exe (LGPL-сборка рядом с приложением). null или папка без них — видео
    /// недоступно: <c>video.ffmpeg_missing</c> при использовании, остальной Core работает.
    /// </summary>
    public string? FfmpegDirectory { get; init; }

    /// <summary>
    /// Как запускать Python worker (обучение, проверки моделей). null — среда не настроена: обучение отвечает
    /// <c>training.runtime_not_configured</c> при старте запуска, остальной Core работает.
    /// </summary>
    public Python.PythonWorkerLaunch? PythonWorker { get; init; }

    /// <summary>
    /// Только для smoke-проверок Desktop (<c>--smoke-report</c>): регистрирует вид материалов <c>smoke-fixture</c>,
    /// файлы которого лежат в files/smoke-fixture/&lt;id&gt;.jpg|.png без строк в базе.
    /// </summary>
    public bool SmokeFixtures { get; init; }
}
