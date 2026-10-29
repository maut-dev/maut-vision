using System.IO;
using System.Security.Cryptography;

namespace MautVision.Desktop.Mcp;

/// <summary>
/// Settings of the MCP server kept in host-settings.json. Only the user changes them, in the settings dialog of the application: no MCP
/// tool reads or writes them, otherwise an AI client could lift its own restrictions.
/// </summary>
/// <param name="Enabled">The server runs; off until the user turns it on.</param>
/// <param name="Port">The TCP port on 127.0.0.1; a busy port is reported, another one is never chosen silently.</param>
/// <param name="AccessLevel">read_only, read_write or full (<see cref="McpAccessLevels"/>).</param>
/// <param name="Token">The Bearer token clients send; generated with a CSPRNG on the first enable.</param>
/// <param name="RestrictFolders">Paths are accepted only inside <paramref name="AllowedFolders"/>; off by default.</param>
/// <param name="LaunchOnRequest">The stdio proxy may start the application minimized to the tray when it is not running.</param>
internal sealed record McpSettings(bool Enabled, int Port, string AccessLevel, string? Token, bool RestrictFolders, IReadOnlyList<string> AllowedFolders,
    bool LaunchOnRequest)
{
    internal const int DefaultPort = 46100;
    internal const int MaxAllowedFolders = 64;

    internal static McpSettings Default { get; } = new(false, DefaultPort, McpAccessLevels.Full, null, false, [], true);

    /// <summary>A stored record from an older or edited file: invalid values fall back to their defaults.</summary>
    internal McpSettings Normalized() => this with
    {
        Port = IsPort(Port) ? Port : DefaultPort,
        AccessLevel = McpAccessLevels.IsLevel(AccessLevel) ? AccessLevel : McpAccessLevels.Full,
        Token = Token is { Length: >= 32 } ? Token : null,
        AllowedFolders = (AllowedFolders ?? []).Where(folder => !string.IsNullOrWhiteSpace(folder) && Path.IsPathFullyQualified(folder))
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(MaxAllowedFolders).ToArray(),
    };

    /// <summary>Ports below 1024 need elevation on some systems and are never offered.</summary>
    internal static bool IsPort(int port) => port is >= 1024 and <= 65535;

    /// <summary>256 random bits as base64url without padding (43 characters).</summary>
    internal static string NewToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>What an AI client may do: read_only — read tools; read_write — reading and changes; full — also deletion and overwriting.</summary>
internal static class McpAccessLevels
{
    internal const string ReadOnly = "read_only";
    internal const string ReadWrite = "read_write";
    internal const string Full = "full";

    internal static bool IsLevel(string? value) => value is ReadOnly or ReadWrite or Full;

    /// <summary>Whether the level lets a tool of this class run.</summary>
    internal static bool Allows(string level, McpToolClass toolClass) => toolClass switch
    {
        McpToolClass.Read => true,
        McpToolClass.Write => level is ReadWrite or Full,
        McpToolClass.Destructive => level is Full,
        _ => false,
    };
}

/// <summary>The class of a tool: reading, creating and changing, or deleting and overwriting.</summary>
internal enum McpToolClass
{
    Read,
    Write,
    Destructive,
}
