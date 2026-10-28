# Maut Vision UI

The React/TypeScript/shadcn/Tailwind interface hosted in WebView2. Components use a typed API; domain DTOs are separated from Transport.

```powershell
npm --prefix ui run typecheck
npm --prefix ui run build
```

Commands run from the repository root. `start.ps1` builds the UI and runs it with the C# host; `npm --prefix ui run dev` serves it in a
browser with a fallback transport. TypeScript/TSX use 4 spaces. Subscriptions are removed when a component unmounts.

Navigation is React state or a hash router, never History API paths: see [navigation](../docs/UI/navigation.md).

## Appearance

The dark theme and shadcn components (`src/components/ui`, `radix-ui` primitives) use colour tokens from `src/index.css`. The lime brand
colour (`brand`) is used sparingly: progress and the ready mark. The `@/` import points to `src/`.

`public/brand` contains the mark (`logo-mark.png`: header, icons) and the full logo (`logo-full.png`: the Desktop loading window); see
`brand/README.md`.

The interface texts are in Russian; English localization is planned. Keep user-facing texts of an area together so that they can be
extracted for translation.

## Screen data

A screen gets its initial state with a request and then applies events: the library — `project.changed`, the model catalog —
`baseModel.changed`. A response with a smaller project revision never overwrites an event that already arrived.

[API](../docs/Desktop/transport.md) · [Events](../docs/Desktop/events.md) · [Errors](../docs/Core/errors.md) · [Code style](../docs/Code/style.md)
