# MCP server

AI clients on the same computer (Claude Code, Claude Desktop, Cursor and others) work with Maut Vision through the Model Context Protocol.
The server lives in the host, uses the one Core of the process and the same controllers as the page, so everything an AI does is visible
in the window: the same jobs, events and lists.

```text
MCP client ──Streamable HTTP──► http://127.0.0.1:<port>/mcp   Kestrel in MautVision.Desktop (Mcp/McpServerHost)
           ──stdio──► maut-vision-mcp.exe (proxy) ──►┘
                                │
                     McpToolbox → tools (Mcp/Tools) → controllers and managers of Core
```

## Why inside the host

`CoreFactory` keeps one instance per process and only that process opens `project.db`. A separate MCP process with its own Core would be
a second writer of the same database. The server is therefore a part of `MautVision.Desktop` (folder `Mcp/`), not a project of its own:
the tools reuse internal controllers and mappers of the host, which a separate assembly could not reach without making them public.

## Startup and loading

The server is off on a new installation. When it is on, it starts after the library is shown, never during startup. Only
`McpServerHost` touches ASP.NET Core and the MCP SDK, and it is reached through a method that is not inlined, so without MCP these
assemblies are never loaded; the native smoke checks that. Settings changes apply at runtime: a new port or token restarts Kestrel,
switching off stops it.

```csharp
await mcp.UpdateAsync(current => current with { Port = 47000 }, cancellationToken);   // restart on the new port
```

A busy port is reported as the state `port_busy` (`mcp.port_busy`) in the settings dialog; another port is never chosen silently. Besides
other programs, Windows itself may reserve ranges of ports (Hyper-V, WSL) that `netsh interface ipv4 show excludedportrange` does not
always list; the user then picks another port and copies the configuration again.

## Settings

`host-settings.json` keeps `mcp`: `enabled`, `port` (46100 by default), `accessLevel` (`read_only`, `read_write`, `full`; `full` by
default), `token`, `restrictFolders` with `allowedFolders`, and `launchOnRequest` for the stdio proxy. The token has 256 random bits
(`RandomNumberGenerator`) and appears on the first enable; the dialog regenerates it. Only the page changes these settings
(`mcp.getState`, `mcp.update`, `mcp.regenerateToken`, `mcp.addAllowedFolder`, `mcp.removeAllowedFolder`); no tool can, otherwise an AI
could lift its own limits.

The dialog «Подключение ИИ (MCP)» / "AI assistants (MCP)" opens from the header next to the language. It shows the state, port, access
level, the masked token, ready configurations (the `claude mcp add --transport http` command, `.mcp.json`, the Claude Desktop entry with
the proxy path), the folder restriction, a privacy note and the call log. Its texts are the UI namespace `mcp`, loaded with the dialog.

## Transport and authorization

Kestrel listens on `127.0.0.1` only. Every request passes a guard before the SDK:

- `Host` must be `127.0.0.1`, `localhost` or `[::1]` with the server port, and an `Origin`, when sent, must be a loopback origin;
  otherwise `403` (DNS rebinding protection required by the MCP specification);
- `Authorization: Bearer <token>` must match, compared in constant time; otherwise `401` with `WWW-Authenticate: Bearer`.

The SDK serves Streamable HTTP in `StatefulForInitializeClients` mode: clients with the initialize handshake get a session (its uploads
are removed when it ends), clients of the 2026-07-28 revision are served per request.

## Tools

A tool is an `McpToolDefinition`: name, class, idempotency, JSON schemas of input and output, an English description for the agent and a
handler. `McpToolbox` runs every call the same way: the tool exists, the access level allows its class, the handler reads its arguments,
errors become codes, the call is logged.

| Class | Annotations | Allowed at |
|---|---|---|
| Read | `readOnlyHint: true` | every level |
| Write | `readOnlyHint: false, destructiveHint: false` | `read_write`, `full` |
| Destructive | `destructiveHint: true` | `full` |

Project tools take `projectId` explicitly; there is no hidden current project. They prepare the project with
`IProjectsManager.PrepareAsync`: storage and jobs are ready, but the window does not switch and live checks of other projects keep
running. `open_project_in_app` is the only tool that shows something to the user.

A tool returns `structuredContent` that matches its output schema (DTOs of the controllers, camelCase like the page receives), a short
English text and, for pictures, image content.

### Adding a tool

1. Add a definition to the partial `McpTools` of its area (`Mcp/Tools/McpTools.<Area>.cs`) with `McpToolDefinition.Create`.
2. Read arguments through `McpArguments` and call `Only(...)` with the schema names, so a typo is an error, not an ignored value.
3. Call the controller the page uses; put new logic into Core or a job, not into the tool.
4. A long operation enqueues its job and returns `McpJobWaiter.StartedAsync(...)`: `{ jobId, status, job }` at once.
5. Cover it in `SmokeRunner.Mcp.cs` and give every new error code a text in `en` and `ru` `errors.json`.

```csharp
yield return McpToolDefinition.Create("archive_project", "Archive project", McpToolClass.Write, idempotent: true,
    "Moves a project to the archive (archived=true) or restores it. Nothing is deleted.",
    inputSchema, outputSchema,
    async call =>
    {
        call.Arguments.Only("projectId", "archived");
        var project = await dependencies.Projects.SetArchivedAsync(new(call.Arguments.String("projectId"), call.Arguments.Bool("archived", true)),
            call.CancellationToken);
        return new McpToolResult(McpToolResult.Object(new { project }), $"Project {project.Name} is archived.");
    });
```

## Long operations and progress

A tool that starts a job answers with the job snapshot at once. `wait_job` waits until the job finishes or its timeout (at most 120 s)
and returns `timedOut` without stopping the job; `get_job` reads it without waiting. While a call waits, every `job.changed` snapshot of
that job is sent as `notifications/progress` when the client passed a `progressToken`; progress values only grow, as the specification
requires.

## Confirmations

Deleting a project (and, in later steps, capturing the screen) is confirmed by the user in the window at every access level. The host
brings the window to the front (also from the tray) and the page shows who asks, with which tool and what happens; the action text is a
key of `mcp:confirm.actions` with values. «Разрешить» runs the tool; «Отклонить» gives `mcp.confirmation_denied`; no answer within two
minutes gives `mcp.confirmation_timeout`. Stopping the server denies open questions.

## Paths and uploads

Paths are absolute Windows paths. `McpPaths` normalizes a path and resolves it to its final target by handle
(`GetFinalPathNameByHandle`), so links and junctions are followed. By default any path the Windows user can read is accepted, like in a
file dialog. With the optional restriction the final path must lie inside an allowed folder, also resolved, so `..` and junctions cannot
leave it (`mcp.path_not_allowed`). Missing paths give `mcp.path_not_found`, relative ones `mcp.path_invalid`.

`upload_file` takes a file in base64 for clients without disk access: at most 10 MB per file and 50 MB per session. Files go to
`mcp-uploads/<session>/<uploadId>/` in the work directory; a session's files are removed when it ends, all of them when the server starts
or stops and after the application starts.

## Viewing images

`view` returns a picture for the model and its metadata: frames, class images, backgrounds, generation samples, dataset samples, training
plots, the latest frame of a live check, an image file, an upload or a video frame. `McpViewRenderer` (SkiaSharp) applies the EXIF
orientation, cuts `region` (fractions of the frame), fits `maxSize` (1024 by default, at most 2048), draws boxes with labels when
`overlay` is on and encodes JPEG (kept under about 1 MB) or PNG. Boxes in `structuredContent` are fractions of the oriented frame, as Core
stores annotation. `view_many` draws up to 8 targets as one numbered grid; a failed target keeps its number and its error.

## Errors

A failed call is `isError: true` with `structuredContent` `{ code, message, details }`. The code is the Core code (`projects.not_found`)
or an `mcp.*` code; the message is the English text of the code from `ui/src/locales/en/errors.json`, embedded into the host at build time
as the resource `MautVision.Desktop.Mcp.errors.en.json`, so the AI reads the words of the English interface. A malformed argument is
`mcp.invalid_arguments`; an unexpected exception is `mcp.internal_error`, its stack goes only to the log.

## Call log

The last 500 calls are kept in memory with the time, client name, tool, arguments (long strings such as base64 replaced by their length)
and the outcome; every call is also written to the application log. The settings dialog shows them as they happen (`mcp.callAdded`).

## Resources

`maut://projects` and `maut://projects/{projectId}` are JSON snapshots of the library and of a project with its overview.

## Checks

The native smoke enables the server from the dialog on a free port and drives it with the official C# client: the guard (401, 403),
tools with schemas and annotations, every tool, views with overlay and region, access levels, the confirmation (allow, deny, timeout),
the folder restriction with a junction, upload limits, `wait_job` with progress and timeout, a busy port and switching off.
