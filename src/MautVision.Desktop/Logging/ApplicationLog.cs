using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Web.WebView2.Core;

namespace MautVision.Desktop.Logging;

/// <summary>The application log of one run: a file in &lt;work directory&gt;/logs, or nothing if it cannot be opened.</summary>
internal sealed class ApplicationLog : IDisposable
{
    private readonly FileLoggerProvider? provider;

    private ApplicationLog(ILoggerFactory factory, FileLoggerProvider? provider)
    {
        Factory = factory;
        this.provider = provider;
    }

    public static ApplicationLog Disabled { get; } = new(NullLoggerFactory.Instance, null);

    public ILoggerFactory Factory { get; }

    public string? FilePath => provider?.FilePath;

    /// <summary>Opens the log and writes the startup record. Performs file I/O; call it off the UI thread.</summary>
    public static ApplicationLog Start(string workDirectory, string baseModelCatalogPath)
    {
        ApplicationLog log;
        try
        {
            var provider = FileLoggerProvider.Create(Path.Combine(workDirectory, "logs"));
            var factory = LoggerFactory.Create(builder => builder
#if DEBUG
                .SetMinimumLevel(LogLevel.Debug)
#else
                .SetMinimumLevel(LogLevel.Information)
#endif
                .AddProvider(provider));
            log = new ApplicationLog(factory, provider);
        }
        catch (Exception)
        {
            // The application works without a log when the work directory does not accept it.
            return Disabled;
        }
        log.Factory.CreateLogger<App>().LogInformation(
            "Maut Vision {Version} started. Work directory: {WorkDirectory}. Model catalogue: {Catalog}. OS: {Os}. Runtime: {Runtime}. WebView2: {WebView}.",
            ApplicationVersion(), workDirectory, baseModelCatalogPath, RuntimeInformation.OSDescription, RuntimeInformation.FrameworkDescription, WebViewVersion());
        return log;
    }

    public void Dispose()
    {
        if (provider is null) return;
        Factory.Dispose();
        provider.Dispose();
    }

    private static string ApplicationVersion()
    {
        var assembly = typeof(ApplicationLog).Assembly;
        return assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString()
            ?? "unknown";
    }

    private static string WebViewVersion()
    {
        try
        {
            return CoreWebView2Environment.GetAvailableBrowserVersionString();
        }
        catch (Exception)
        {
            return "not found";
        }
    }
}
