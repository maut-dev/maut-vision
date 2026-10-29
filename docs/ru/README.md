# Maut Vision — архитектура (кратко)

Русский обзор для разработчиков. Подробные правила каждой области — в английской документации, вход — [docs/overview.md](../overview.md).

## Что это

Maut Vision — настольное приложение для Windows: проекты с классами и вариантами объектов, импорт и разметка изображений, фоны (в том
числе кадры видео), генерация синтетических датасетов, сборка датасетов YOLO с проверкой целостности, обучение моделей Ultralytics YOLO на
видеокарте NVIDIA, версии моделей с экспортом ONNX и проверки моделей на изображении, датасете, видео и экране.

## Слои

```text
React (WebView2) → API → Transport → Controllers → ICore
React ← события ← Transport ← Events ← Core
Core → Python worker (обучение, проверки) · ffmpeg (кадры видео)
```

- **Core** (`src/MautVision.Core`) — вся бизнес-логика. Один экземпляр через `CoreFactory`, асинхронная инициализация. Публичные контракты —
  в `Interfaces`, реализации — `internal` в папках аспектов. Менеджеры независимы; сценарии из нескольких менеджеров и длинные операции —
  задачи Jobs. Данные проекта: `projects/<projectId>/project.json`, SQLite `project.db`, файлы в `files/`.
- **Desktop** (`src/MautVision.Desktop`) — WPF-хост: окно загрузки, ожидание `Core.InitializeAsync`, затем основной UI в WebView2.
  Controllers вызывают Core и возвращают DTO, Transport доставляет JSON-сообщения, Events пересылает события Core. Изображения идут через
  канал ресурсов по URL, а не через сообщения.
- **UI** (`ui/`) — React, TypeScript, Tailwind, shadcn/Radix. Экраны получают состояние запросом и обновляются событиями, без опроса.
  DTO на C# и TypeScript меняются вместе; UI и host поставляются вместе, без `protocolVersion`. Интерфейс — на русском и английском:
  язык переключается в заголовке, по умолчанию — как в Windows; тексты лежат в каталогах `ui/src/locales/<язык>/`, Core и Desktop
  передают коды и значения ([docs/UI/localization.md](../UI/localization.md)).
- **Python worker** (`python/`) — отдельный процесс на одну операцию над папкой задачи: `request.json` → события JSON Lines в stdout →
  `output/result.json`. Ничего не скачивает, не поднимает сервер; дерево процессов не переживает приложение.

## Где что лежит

| Путь | Содержимое |
|---|---|
| `src/MautVision.Core` | Core: проекты, каталог, изображения, фоны, генерация, датасеты, обучение, проверки, Jobs |
| `src/MautVision.Desktop` | WPF-хост, контроллеры, транспорт, события, канал ресурсов, smoke-проверки |
| `ui/` | интерфейс React |
| `python/` | Python worker, тесты, сборка PyInstaller |
| `catalog/` | каталог базовых моделей (адреса, размеры, SHA-256) |
| `packaging/` | скрипт установщика Inno Setup |
| `tests/` | smoke Core, поддельный worker, замер производительности, сквозной прогон |
| `tools/` | загрузка ffmpeg и WebView2, проверка установки, E2E, окружение Python |
| `docs/` | документация (английская) и этот обзор |

## Окружение разработки

Нужны Windows 10/11 x64, .NET 10 SDK, Node.js с npm, WebView2 Runtime, для обучения — Python 3.14 и видеокарта NVIDIA с драйвером,
поддерживающим CUDA 13 (без неё обучение идёт на процессоре).

```powershell
.\tools\setup-python.ps1            # .dev/python-venv из python/requirements.txt (несколько ГБ)
.\start.ps1                         # сборка UI и решения, запуск с рабочей папкой .dev/DevWorkDirectory
.\start.ps1 -NoLaunch               # только сборка
.\smoke.ps1 -SkipBuild              # проверки Core, страницы и хоста с поддельным worker
npm --prefix ui run typecheck
.\perf.ps1                          # замер на большой библиотеке
.\build-installer.ps1               # установщик (нужны Inno Setup 6.5+ и собранный worker)
```

`start.ps1` берёт интерпретатор из `-Python`, затем из `MAUT_PYTHON`, затем `.dev/python-venv`, затем собранный worker из
`.dev/python-worker`.

## Правила

- Коммиты — Conventional Commits `type(scope): description` со scope `Core/<Block>`, `Desktop/<Block>`, `UI/<Block>`, `Python`, `Docs`,
  `Build`; ветки `master` (релизы) и `dev` (разработка), рабочие ветки `<type>/<topic>`. Подробно — [git.md](../Code/git.md) и
  [CONTRIBUTING.md](../../CONTRIBUTING.md).
- Версии — SemVer, единственный источник — `Version` в `Directory.Build.props`; `ui/package.json` и `CHANGELOG.md` обновляются вместе с ним.
- Интерфейс приложения пока только на русском; английская локализация запланирована.
