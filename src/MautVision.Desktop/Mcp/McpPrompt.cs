namespace MautVision.Desktop.Mcp;

/// <summary>An argument of a prompt: its name, an English description for the client and whether it is required.</summary>
internal sealed record McpPromptArgument(string Name, string Description, bool Required);

/// <summary>
/// A ready scenario of the server (prompts/get): the client offers it to the user, and the text it renders guides the AI through the
/// tools. Render receives the arguments the client sent (missing optional ones are absent) and returns the English text of the user
/// message; an unknown or invalid argument is mcp.invalid_arguments.
/// </summary>
internal sealed record McpPromptDefinition(string Name, string Title, string Description, IReadOnlyList<McpPromptArgument> Arguments,
    Func<IReadOnlyDictionary<string, string>, CancellationToken, Task<string>> Render);
