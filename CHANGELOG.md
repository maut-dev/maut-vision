# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project adheres to
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- **MCP server**: a local Model Context Protocol server in the host for AI clients on this computer (Claude Code, Claude Desktop,
  Cursor): Streamable HTTP on `127.0.0.1` (port 46100 by default) with the official C# SDK, a Bearer token and Host/Origin checks against
  DNS rebinding. Off by default; turned on, restarted on a new port or token and turned off at runtime. ASP.NET Core and the SDK are not
  loaded while it is off.
- **MCP settings**: the dialog «Подключение ИИ (MCP)» / "AI assistants (MCP)" in the header: state, port, access level (read only, read
  and change, full — the default), the token with copy and regeneration, ready configurations for Claude Code and Claude Desktop, an
  optional allowed-folders restriction, launch on request, a privacy note and the log of the last 500 calls.
- **MCP tools** (first set): `get_app_info`, `get_hardware`, `set_interface_language`, `list_jobs`, `get_job`, `wait_job`, `cancel_job`,
  `preview_folder`, `upload_file`, `view`, `view_many`, `list_projects`, `get_project`, `create_project`, `update_project`,
  `archive_project`, `open_project_in_app`, `delete_project`, and the resources `maut://projects` and `maut://projects/{id}`. Every tool
  has input and output schemas and MCP annotations; errors carry a code and the English text of the UI catalog.
- **MCP confirmations**: deleting a project asks the user in the window («Разрешить» / «Отклонить», two minutes).
- **Core**: `IProjectsManager.PrepareAsync` prepares a project for background work without switching to it.

## [1.1.0] - 2026-10-29

### Added

- **Localization**: the whole interface in English besides Russian: the library, base models, project dialogs, every project section
  (overview, variants, classes, backgrounds, images, generation, datasets, training, models, checks, analytics), error texts, job
  progress and the training log. Catalogs live in `ui/src/locales/<language>/<namespace>.json` with keys typed by the Russian catalog;
  each section loads its texts with its own chunk (`ui/src/i18n/sections/<namespace>.ts`), so startup does not grow.
- **Localization**: the setting «Язык интерфейса» / "Interface language" in the header («Как в системе», «Русский», «English»), kept in
  `host-settings.json` and applied without a restart; by default the language of Windows (Russian for `ru-*`, otherwise English);
  `--language system|ru|en` for one run.
- **Localization**: `npm run check-locales` (part of `npm run typecheck`) compares the languages key by key and placeholder by placeholder,
  checks plural forms and requires a text for every error code, line code and worker message the host can send.
- **Host**: the loading window, tray, exit question, native file dialogs and startup failures in English and Russian (`Strings.resx`);
  the tray is relabelled when the language changes.
- **Installer**: an English and a Russian wizard chosen by the language of Windows.

### Changed

- **Errors**: bridge responses carry `{ code, details }` without a text; error texts, deletion blockers, plan warnings, skip reasons and
  hardware reasons are built by the UI from codes in the current language. Job errors stored by earlier versions keep their message as
  a fallback for unknown codes.
- **Progress and logs**: job progress, job labels, system lines of the training log and worker errors and warnings come from the host as
  codes with values (`CodedText`: `{ code, args, text }`) and are shown in the interface language. `JobInfo.ProgressText` became
  `ProgressMessage`; progress lines and labels stored by 1.0.0 are shown as they were. Console lines of Ultralytics stay raw.
- **Python worker**: errors and warnings carry `reason` and `args` (an optional extension of protocol 1) and English messages from
  `worker/protocol/messages.py`; all worker messages, comments and logs are in English.
- **Russian plural forms**: counts take the right form everywhere (one, few, many: «1 фон», «2 фона», «5 фонов»), including counts that
  were shown with a fixed form before.
- **Formatting**: numbers, sizes, dates and durations follow the interface language through `ui/src/lib/format.ts`.
- **Projects**: the import suffix of a copy, the fallback export file name and the name of a repeated generation preset come from the UI
  in its language. The README.md of a model ZIP is in English.
- **Focus**: saving or cancelling a preset edit, archiving and the dataset actions move the focus after the render that enables the
  target button, so the focus no longer gets lost when the render comes after the next frame.
- Core interfaces, Desktop DTOs, log messages and UI comments are written in English.

## [1.0.0] - 2026-10-27

First public release.

### Added

- **Projects**: a project library with creation, settings (base model and input size), archive, last opened project, export to a ZIP
  archive and import with integrity checks, deletion with background cleanup.
- **Catalog**: project variants and classes with class variants, colours, ordering, archive and two-phase permanent deletion; import of
  class variant images (PNG, JPEG, WebP, BMP).
- **Images**: image groups with split hints, import of files, folders and video frames (ffmpeg), duplicate detection by pixels, a
  virtualized gallery, a full-screen viewer and an annotation editor with boxes, ROI, undo and review state.
- **Backgrounds**: background sets of the project input size from ready frames, crops of images and video frames, deterministic blur,
  similarity filtering accelerated with AVX2/SSE2.
- **Generation**: presets of synthetic scenes (backgrounds, class variants, variant shares, objects per frame, empty frames, scale),
  deterministic sample frames, resumable parallel runs that produce annotated images.
- **Datasets**: build plans with selection reasons and warnings, deterministic splits without leakage of identical pixels, YOLO dataset
  versions with hard links, integrity checks by SHA-256 and ZIP export.
- **Training**: Ultralytics YOLO detection training on an NVIDIA GPU (or CPU) through a bundled Python worker, the full parameter set with
  presets, a live run passport with epoch charts and console log, recovery after an application exit.
- **Models**: model versions with metrics, parameters, plots, a working version, `best.pt` and `best.onnx`, reproducible ZIP export and
  deletion with blockers.
- **Checks**: evaluation on a dataset split with a comparison matrix, live checks on an image, a video and a screen region.
- **Overview and analytics**: project data, running work, latest models, CPU/GPU/RAM load, and a comparison of model versions with metrics,
  parameters and training curves.
- **Base models**: a catalog of YOLO detection models (YOLOv3u–YOLO26) with verified downloads.
- **Desktop**: WPF host with WebView2, tray mode, single instance per work directory, per-monitor DPI, event-driven UI without polling, a
  resource channel for images, structured logs.
- **Delivery**: a per-user Inno Setup installer with the WebView2 bootstrapper, bundled ffmpeg (LGPL) and Python worker.

[Unreleased]: https://github.com/maut-dev/maut-vision/compare/v1.1.0...dev
[1.1.0]: https://github.com/maut-dev/maut-vision/compare/v1.0.0...v1.1.0
[1.0.0]: https://github.com/maut-dev/maut-vision/releases/tag/v1.0.0
