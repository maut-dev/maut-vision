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

Besides other programs, Windows itself may reserve ranges of ports (Hyper-V, WSL) that `netsh interface ipv4 show excludedportrange`
does not always list. Only the first enable (no token yet) with the default port 46100 handles that itself: when the port cannot be bound,
the first free port of 39100–39999 is taken, saved and shown in the dialog. Afterwards a busy saved port is reported as the state
`port_busy` (`mcp.port_busy`) and another port is never chosen silently, since clients already hold the address; the user picks another
port and copies the configuration again.

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

### Base models, catalog and images

The tools use the controllers of these screens over the same Core, with their own selection tokens and resource registry, so an AI client
never takes the capacity of the page's registries. A path or upload becomes a selection token of `FileSelections` and the import goes
through the same controller method as after a file dialog. Changes take an optional `revision` (`orderRevision`, `annotationRevision`);
without it the current one is used, with a stale one the Core conflict code is returned. Resource URLs of the page are removed from the
results; the AI looks at pictures with `view`.

| Tool | Class | What it does |
|---|---|---|
| `list_base_models` | R | catalog models with status and progress |
| `install_base_model` | W | starts the installation of the model (not a project job); `waitSeconds` waits with progress, a repeat joins it |
| `delete_base_model` | D | removes the installed weights |
| `list_variants`, `create_variant`, `update_variant`, `archive_variant` | R, W | project variants |
| `delete_variant` | D | review, blockers as `variants.deletion_blocked`, deletion |
| `list_classes` | R | classes in order with bindings (`classVariantId`, variant name, colour, image counts), `classOrderRevision` |
| `create_class`, `update_class`, `archive_class`, `reorder_classes` | W | classes and their order (a full permutation) |
| `delete_class` | D | review, blockers, deletion with bindings and images |
| `bind_class_variant`, `update_class_variant` | W | binding with colour; colour and archive of a binding |
| `unbind_class_variant` | D | deletes a binding with its images |
| `list_class_assets` | R | cut-out images of a binding by cursor |
| `import_class_assets` ⏳ | W | files and folders by `path`/`paths`, uploads by `uploadId`/`uploadIds` |
| `delete_class_assets` | D | up to 500 images; all are reviewed before any is deleted |
| `list_image_groups`, `create_image_group`, `update_image_group` | R, W | groups; `update_image_group` also archives and restores |
| `delete_image_group` | D | review, blockers, deletion |
| `import_images` ⏳ | W | one folder as a folder source, otherwise files; uploads |
| `import_video_frames` ⏳ | W | `startSeconds`, `intervalSeconds`, `count` (by default every mark before the end) |
| `list_images` | R | a group page by cursor with archive and review filters |
| `get_annotations` | R | boxes with class and variant names; one image (any revision) or up to 200 |
| `set_annotations` | W | replaces the boxes, checked like the editor (inside the frame, sides ≥ 0.002) |
| `set_image_reviewed` | W | the review state with the same boxes, up to 400 images |
| `move_images` | W | up to 400 images of one group to a group of the same origin |
| `delete_images` | D | up to 400 images of one group after the review |

A destructive tool performs the deletion review itself: blockers refuse the call with the area's `deletion_blocked` code and
`details.blockers` (`images.annotations: 2 (кадр-1.png, кадр-2.png)`), the same reasons the deletion dialog lists.

```text
set_annotations { projectId, imageId, annotationRevision: 1, reviewed: true,
                  boxes: [{ classId, classVariantId, x: 0.25, y: 0.25, width: 0.5, height: 0.5 }] }
```

Helpers shared by every area (argument lists, revisions, `operationId`, files of an import, the controllers' registries, deletion
blockers) live in `Mcp/Tools/McpTools.Arguments.cs`.

### Backgrounds, generation and datasets

The same rule: the controllers of these screens with the registries of the tools. A tool that starts a job takes an optional
`operationId` (a repeat returns the same job) and `waitSeconds`, and answers `{ jobId, status, job }` plus its area's object (`run`,
`operation`, `versionId`).

| Tool | Class | What it does |
|---|---|---|
| `list_background_sets`, `create_background_set`, `update_background_set` | R, W | sets of the project input size; update renames, archives and restores |
| `delete_background_set`, `delete_backgrounds` | D | review, blockers (`backgrounds.deletion_blocked`: a running import, recipes that use the set), deletion |
| `import_backgrounds` ⏳ | W | files, folders and uploads; `mode` `whole` takes files of exactly the set size, `crop` cuts the set size from larger files at `crops[]` or centered (≤ 500 files); `skipSimilar`, `blur` |
| `import_video_backgrounds` ⏳ | W | `videoPath` or `uploadId`, `startSeconds`, `stepSeconds`, `count` (every available frame by default), `region` (centered by default) |
| `list_backgrounds` | R | a set page by cursor: id, size, origin, blur radius |
| `list_recipes`, `get_recipe` | R | recipes with every parameter of the form |
| `create_recipe`, `update_recipe` | W | the recipe form; omitted sources default like the window (see below); update also archives and restores |
| `delete_recipe` | D | blocked by an unfinished run (`generation.deletion_blocked`) |
| `start_generation` ⏳ | W | `count`, `seed` (random by default); `run.outputGroupId` is the image group of the frames |
| `list_generation_runs` | R | runs with the recipe copy, seed, counts and output group |
| `list_datasets`, `get_dataset_version` | R | versions; `get_dataset_version` adds the first samples of each split with their boxes |
| `plan_dataset` | R | "check before build": counts per split and class, skips, warnings with English texts, `buildable`, `hash` |
| `build_dataset` ⏳ | W | a `planId` (hash optional) or the specification fields to plan and build in one call; the version ID is the job ID |
| `verify_dataset` ⏳ | W | integrity check |
| `export_dataset` ⏳ | W | YOLO ZIP to a `.zip` path or into a folder; an existing file is `mcp.destination_exists` unless `overwrite` (full access) |
| `archive_dataset` | W | archive or restore |
| `delete_dataset_version` | D | review, blockers, deletion; the passport stays |

Defaults follow the forms of the window. A recipe takes every available binding with images (of `classIds` when given) and even whole
shares over their project variants; given `variantWeights` choose the bindings of variants with a share. A dataset takes every active
class, targets — available bindings of the chosen classes, negatives — the others, Train 80 % and Validation 20 %, empty frames included
and a random seed.

IDs of these tools are `view` targets: `{ background: backgroundId }`, `{ generationSample: recipeId, sampleIndex, seed }` (the frames a
run with this seed makes) and `{ datasetSample: versionId, split, index }` or `{ datasetSample: versionId, sampleId }`.

```text
create_background_set → import_backgrounds { mode: "crop" } → create_recipe { backgroundSetIds } → view { generationSample }
  → start_generation { count } → wait_job → plan_dataset { groupIds: [run.outputGroupId] } → build_dataset { planId }
  → verify_dataset → export_dataset { destinationPath: "D:\\export\\set.zip" }
```

## Long operations and progress

A tool that starts a job answers with the job snapshot at once. `wait_job` waits until the job finishes or its timeout (at most 120 s)
and returns `timedOut` without stopping the job; `get_job` reads it without waiting. While a call waits, every `job.changed` snapshot of
that job is sent as `notifications/progress` when the client passed a `progressToken`; progress values only grow, as the specification
requires.

Jobs an AI client starts are marked in the job lists of the window. `McpToolbox` runs each call under `JobLabels.RequestedBy(client)`;
while it lasts, every label the controllers make is wrapped into `jobLabels.ai_label` («ИИ (Claude Code) · Кружка · День») with the
inner label in its values (`labelCode`, `labelText`, `label.*`), or `jobLabels.ai` («ИИ: Claude Code») without one. `codedText` in the
UI renders the inner label in the current language.

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
`maut://projects/{projectId}/datasets/{versionId}/manifest` is the composition of a dataset version: the version and every sample with
its split, group, hashes and boxes.

## Prompts

A prompt is an `McpPromptDefinition` (`Mcp/McpPrompt.cs`): name, title, description, arguments and a function that renders the English
user message; `McpTools.CreatePrompts` lists them by area like the tools. `review_annotations(projectId, groupId)` walks the AI through
the unreviewed images of a group: `list_images`, `view` with overlay, `set_annotations` with `reviewed: true`, and a report of what it
left for the user. `prepare_dataset_from_folder(folder)`, `synthetic_dataset(classesFolder, backgroundsFolder)` and
`generate_from_video(videoPath)` lead from files on disk to a built dataset through the import, background, generation and dataset tools.

## Checks

The native smoke enables the server from the dialog with the default port held by a listener (the first enable moves to the fallback
range) and drives it with the official C# client: the guard (401, 403),
tools with schemas and annotations, every tool, views with overlay and region, access levels, the confirmation (allow, deny, timeout),
the folder restriction with a junction, upload limits, `wait_job` with progress and timeout, a busy port and switching off.
`SmokeRunner.McpCatalog.cs` and `SmokeRunner.McpImages.cs` cover the base model, catalog and image tools in a project of their own:
error codes of the window, imports by path, by upload and from an ffmpeg clip with `wait_job`, the AI job label, the `set_annotations`
round trip with `view`, deletion blockers and the `review_annotations` prompt. `SmokeRunner.McpBackgrounds.cs`,
`SmokeRunner.McpGeneration.cs` and `SmokeRunner.McpDatasets.cs` run the chain through MCP: background sets from images (whole, cropped,
uploaded) and from an ffmpeg clip, a recipe, generation, plan, build, verification, the export ZIP checked on disk, views of a
background, a generation sample and a dataset sample, the manifest resource, the error paths and the three dataset prompts.
