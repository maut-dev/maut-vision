# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project adheres to
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- **Localization**: the interface language setting «Язык интерфейса» / "Interface language" in the header («Как в системе», «Русский»,
  «English»), kept in `host-settings.json` and applied without a restart; by default the language of Windows (Russian for `ru-*`,
  otherwise English). The library, base models, project dialogs, navigation and shared components are translated; project sections
  follow in the next steps.
- **Localization**: i18next catalogs in `ui/src/locales` with typed keys and Russian and English plural forms; `npm run check-locales`
  (part of `npm run typecheck`) compares the languages and requires a text for every error code of the host.
- **Host**: the loading window, tray, exit question, native file dialogs and startup failures in English and Russian (`Strings.resx`);
  the tray is relabelled when the language changes; `--language system|ru|en` for one run.
- **Installer**: English and Russian wizard chosen by the language of Windows.

### Changed

- **Errors**: bridge responses carry `{ code, details }` without a text; error texts, deletion blockers, plan warnings, skip reasons and
  hardware reasons are built by the UI from codes in the current language. Job errors stored by earlier versions keep their message as
  a fallback for unknown codes.
- **Formatting**: numbers, sizes and dates follow the interface language through `ui/src/lib/format.ts`.

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

[Unreleased]: https://github.com/maut-dev/maut-vision/compare/v1.0.0...dev
[1.0.0]: https://github.com/maut-dev/maut-vision/releases/tag/v1.0.0
