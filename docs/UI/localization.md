# Localization

The interface speaks Russian and English. Core and Desktop send codes and values; the UI owns every text and chooses it from catalogs of
the current language. The host has a small catalog of its own for what it shows before or outside the page.

```text
Core:     CoreException(code, details)        → Desktop: { code, details }     → UI: errorText(code, details)
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
| a section (`images`, `training`…) | texts of one section | with the chunk of the section (`addResourceBundle`) |

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
- `workerErrorText(error)` — a training run or check error: the worker's own message for its `*.worker_<code>` codes, otherwise the code;
- `blockerText(area, blocker)`, `planWarningText`, `backgroundReasonText` — lines built from a code, a count and examples or sizes.

A code is a nested key: `projects.not_found` is `errors.projects.not_found`. A detail named `section` is a key of `errors.sections`; a
detail named `count` takes plural forms.

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
- keys of `errors` that look like codes nobody sends are warnings.

The native smoke switches to English, checks that the shell has no Cyrillic outside names the user typed and that a bridge error is shown
in English, and switches back.

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
