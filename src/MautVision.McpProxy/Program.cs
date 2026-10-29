using System.Text;
using MautVision.McpProxy;
using ModelContextProtocol.Server;

// maut-vision-mcp: MCP over stdio for clients that start a command (Claude Desktop), forwarded to the Streamable HTTP endpoint of the running
// Maut Vision. The endpoint and the token come from host-settings.json of the work directory, never from the command line. stdout carries only
// MCP messages; every message for people goes to stderr, which clients keep in their logs.
Console.OutputEncoding = new UTF8Encoding(false);
string workDirectory;
try
{
    workDirectory = ProxyArguments.WorkDirectory(args);
}
catch (ArgumentException error)
{
    Console.Error.WriteLine($"maut-vision-mcp: {error.Message}");
    Console.Error.WriteLine("Usage: maut-vision-mcp [--work-directory <folder>]   (default %LOCALAPPDATA%\\MautVision)");
    return 2;
}

using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    stop.Cancel();
};
await using var upstream = new Upstream(new AppInstance(workDirectory, AppContext.BaseDirectory));
try
{
    // The server description (instructions, capabilities) is the application's own, so the application must answer first.
    var first = await upstream.GetAsync(null, stop.Token);
    var options = ProxyServer.Options(first, upstream);
    await using var server = McpServer.Create(new StdioServerTransport(options), options);
    upstream.Downstream = server;
    await server.RunAsync(stop.Token);
    return 0;
}
catch (ProxyException error)
{
    Console.Error.WriteLine($"maut-vision-mcp: {error.Message}");
    return 1;
}
catch (OperationCanceledException) when (stop.IsCancellationRequested)
{
    return 0;
}
