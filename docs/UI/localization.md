# Localization

The interface speaks Russian and English. Core and Desktop send codes and values; the UI owns every text and chooses it from catalogs of
the current language. The host has a small catalog of its own for what it shows before or outside the page.

```text
Core:     CoreException(code, details)        → Desktop: { code, details }     → UI: errorText(code, details)
Core:     CodedText(code, args, text)         → Desktop: { code, args, text }  → UI: codedText(value)
Worker:   error/warning {code, reason, args}  → Core: CodedText worker.<code>.<reason>
Desktop:  Strings.resx / Strings.ru.resx      — loading window, tray, message boxes, native file dialogs
Installer: [Languages] english, russian       — wizard texts and CustomMessages
```

## Catalogs and keys

Catalogs are JSON files `ui/src/locales/<language>/<namespace>.json`. A key names a place, not a text: `library.create`,
`projectDeletion.title`. Nested objects group keys of one screen; the key path is joined by dots.

| Namespace | Contents | Loaded |
|---|---|---|
| `common` | the shell: header, navigation, library, project dialogs, shared components, units | with the page |
| `errors` | texts of error codes and lines built from codes (deletion blockers, plan warnings, skip reasons) | with the page |
| `progress` | what a job is doing: `progress.epoch` «Эпоха {{epoch}} из {{total}}» | with the page |
| `trainingLog` | system lines of the training log written by Core | with the page |
| `jobLabels` | labels of jobs with words around data: «Версия {{number}}», «{{class}} · {{variant}}» | with the page |
| `worker` | errors and warnings of the Python worker, keys `<code>` or `<code>.<reason>` of `python/worker/protocol/messages.py` | with the page |
| a section (`images`, `training`…) | texts of one section | with the chunk of the section (`registerSection`) |

A section namespace has one module `ui/src/i18n/sections/<namespace>.ts` that adds its Russian and English catalogs with
`registerSection` of `ui/src/i18n/sections.ts`. Every module that reads the namespace imports that module for its side effect, so the texts
come with the first chunk that needs them (dataset checks share `checks` with analytics; datasets read the purposes of image groups from
`images`). A module of the page (the overview, the catalog) loads before the catalogs initialize; `registerSection` then waits for them.
A section never registers a namespace itself and never copies a text of another namespace: it imports that namespace or a helper that
reads it (`setSummary` of backgrounds, `splitHintLabels` of images). Texts shared by several sections (metric names, job kinds) live in
`common`.

```ts
// ui/src/i18n/sections/checks.ts
import { registerSection } from "@/i18n/sections";
import en from "@/locales/en/checks.json";
import ru from "@/locales/ru/checks.json";

registerSection("checks", { ru, en });
```

```ts
// a module of the section
import "@/i18n/sections/checks";
const { t } = useTranslation("checks");
```

```tsx
const { t } = useTranslation();
<Button>{t("library.create")}</Button>
<p>{t("transfer.files", { processed: formatNumber(done), total: formatNumber(all) })}</p>
```

Keys are typed by the Russian catalogs (`ui/src/i18n/i18next.d.ts`): a key that does not exist fails `npm run typecheck`. Code that runs
outside a component (helpers that build a line) uses `i18n.t` and is called during render, so a language change renders it again.

Values are substituted with `{{name}}`; the UI never glues words around a translated fragment. Plurals use the suffixes of the language:
Russian `_one`, `_few`, `_many` (and `_other` for fractions), English `_one`, `_other`; the value is `count`.

```json
"bridge": {
  "timeout_one": "Приложение не ответило за {{count}} секунду. Повторите запрос.",
  "timeout_few": "Приложение не ответило за {{count}} секунды. Повторите запрос.",
  "timeout_many": "Приложение не ответило за {{count}} секунд. Повторите запрос."
}
```

Numbers, sizes, dates and durations go through `ui/src/lib/format.ts` (`formatNumber`, `formatBytes`, `formatDate`, `formatDuration`,
`locale()` for `Intl` and sorting); a locale is never written in a component.

## Errors by code

The host sends `{ code, details }` and no text ([errors](../Core/errors.md)). The UI translates in `ui/src/lib/errors.ts`:

- `errorText(code, details)` — the text of `errors:<code>` with the details substituted; a code without a text gives the generic text;
- `errorMessage(error)` — the text of a caught error: a `BridgeError` by its code, an error of the page by its message;
- `jobErrorText(error)` — a stored job error; its stored message is shown only for a code the catalogs do not know (an older version);
- `workerErrorText(error)` — a training run or check error: for a failure the worker reported itself, the text of its worker code with
  values (`workerMessage`), otherwise the text of the error code;
- `blockerText(area, blocker)`, `planWarningText`, `backgroundReasonText` — lines built from a code, a count and examples or sizes.

A code is a nested key: `projects.not_found` is `errors.projects.not_found`. A detail named `section` is a key of `errors.sections`; a
detail named `count` takes plural forms.

## Lines sent as codes

Besides errors, the host sends every line the user reads as a code with values (`CodedText` of Core): job progress, job labels, system
lines of the training log, worker errors and warnings. A code `<namespace>.<key>` is the key `<namespace>:<key>`; `args` are strings for its
placeholders; `text` is shown as is when there is no code (a name the user typed, a line stored by version 1.0.0) or when the catalogs do
not know the code (the English message of the worker). `lib/coded-text.ts` turns it into text:

```tsx
codedText(job.progressMessage)          // {code: "progress.epoch", args: {epoch: "3", total: "10"}} → «Эпоха 3 из 10» / "Epoch 3 of 10"
codedText(job.label)                    // {code: null, text: "Улица"} → «Улица»
logLineText(entry)                      // a warning → «amp_disabled: AMP отключён…»; a console line of Ultralytics → as is
```

Data stays data: names the user typed are never translated, the console of Ultralytics and the stages of the worker are a technical log in
English. Defaults the host would otherwise invent come from the page in its language: the copy suffix of an imported project
(`copyLabel`: «импорт»), the export file name of a project without a usable name (`fallbackName`), the name of a repeated generation preset
(`generation.repeatName`). Without them the host uses English.

## Language

The setting «Язык интерфейса» / "Interface language" in the header has «Как в системе», «Русский», «English». The host keeps it in
`host-settings.json` (`language`: `system`, `ru`, `en`) and resolves `system` by the user interface language of Windows: `ru-*` is Russian,
anything else English.

- The page asks `settings.getLanguage` before its first render; `settings.setLanguage` stores a choice and the event
  `settings.languageChanged` switches the page without a reload.
- The host switches the culture of `Strings` at once: the tray menu is relabelled, dialogs and message boxes use the new language. The
  loading window takes it from the next start.
- `--language system|ru|en` replaces the setting for one run without saving it. A smoke run is Russian unless the option says otherwise,
  so its text checks do not depend on the machine.

## Adding a language

1. Copy `ui/src/locales/en` to `ui/src/locales/<language>` and translate every value; keep the keys and the `{{placeholders}}`.
2. Add the language to `resources` and `supportedLngs` in `ui/src/i18n/index.ts`, to `locale()` in `lib/format.ts` and to the plural
   forms in `ui/scripts/check-locales.ts`.
3. Add `Strings.<language>.resx`, the language to `HostLanguage` and `SatelliteResourceLanguages`, and a `[Languages]` entry with its
   `CustomMessages` to `packaging/MautVision.iss`.

## Check

`npm run check-locales` (part of `npm run typecheck`) runs `ui/scripts/check-locales.ts`:

- every language has the same namespaces and keys (plural forms compare by their base key) and the same placeholders;
- Russian plurals have one/few/many, English one/other;
- every error code the host can send has a text: literals `"<area>.<reason>"` of the C# sources, the `{area}` templates of the catalog,
  worker outcomes under `training.` and `checks.`, and the codes the UI bridge raises;
- keys of `errors` that look like codes nobody sends are warnings;
- every line code the host sends — literals `"progress.<key>"`, `"trainingLog.<key>"`, `"jobLabels.<key>"` of the C# sources — has a text
  in its namespace; keys nobody sends or uses are warnings;
- the `worker` namespace has exactly the keys of `python/worker/protocol/messages.py`, with the same placeholders (`{name}` there,
  `{{name}}` in the catalogs).

The native smoke switches to English, checks that the shell and every section have no Cyrillic outside data the user gave (names,
descriptions, file names), that a bridge error, a job progress line, a job label and a system line of the training log are shown in
English, and switches back. Cyrillic in `ui/src` outside `locales/ru` is limited to data rules: the Russian keyboard layout of the
annotation shortcuts and the stored 1.0.0 label «Версия N» the overview recognizes.

## Glossary

| Russian | English |
|---|---|
| вариант | variant |
| фон | background |
| проверка | check |
| датасет | dataset |
| версия модели | model version |
| рабочая версия | working version |
| задача | job |
| разметка | annotation |
| генерация | generation |
| обучение | training |
| набор фонов | background set |
| пресет | preset |
| запуск | run |
| вход модели | model input |
| базовая модель | base model |
