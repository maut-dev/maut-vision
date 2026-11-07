# Design system in the UI

The interface follows the Maut Vision Glass design system, dark theme only. Its tokens are CSS variables in `ui/src/index.css`; the
shadcn variables (`--background`, `--card`, `--primary`, `--border`…) point to them, so `bg-card` or `text-muted-foreground` already
use the palette. New code uses the token classes directly: `bg-ink-2`, `text-text-2`, `text-ok`, `border-line`, `shadow-glass-3`.

| Token | Meaning |
|---|---|
| `ink-0`…`ink-3` | the canvas, the solid content surface, raised solid (fields), hover and tracks |
| `text-1`…`text-3` | main, secondary and the quietest text (≥ 4.5:1 on `ink-0` and `ink-1`) |
| `brand`, `brand-strong`, `brand-soft`, `on-brand` | the lime of the mark and text on it |
| `ok`, `warn`, `danger`, `info` | states, always with a word or an icon |
| `line`, `line-strong`, `glass-*`, `focus` | lines, glass fills and edges, the focus ring |
| `series-1`…`series-8` | chart series and classes by number |

Radii: `rounded-md` 8 px (buttons, fields), `rounded-lg` 12 px (cards), `rounded-xl` 18 px (glass panels, dialogs), `rounded-2xl` 26 px.
Motion: 120 ms for press and hover, 220 ms for a state change, 420 ms for progress, `ease-glass`; only transform, opacity and colour
animate, and reduced motion makes everything instant. Fonts are Geist and Geist Mono bundled with the page (Latin and Cyrillic), Segoe UI
otherwise; numbers use `tabular-nums`.

## Layers

There are three layers and glass never nests in glass. Blur is computed every frame for everything under the layer, so content that
repeats or redraws stays solid.

| Class | Where |
|---|---|
| `.surface` | content: lists, galleries, tables, forms, the annotation canvas, charts |
| `.glass` | the shell: the header and the sidebar; a floating panel over the viewer |
| `.glass-raised` | overlays: dialogs, Select lists, menus, popovers, toasts, tooltips |

Without `backdrop-filter` and with `prefers-contrast: more` both glass classes become solid `ink-2`. The main area stays on the canvas: a
section never wraps itself in glass. Depth comes from 1 px lines and the top highlight; shadows belong only to floating layers.

## Brand colour

Lime is a rare signal: at most one primary action of a view (`Button variant="brand"`), progress, the active navigation item and the
"ready" state. The main action inside a form or a dialog is the white `default` button; the rest are `glass`/`outline`, `ghost` or
`destructive`. `.corners` marks a selected or focused object with the viewfinder corners of the logo (`aria-selected="true"`).

## Layout primitives

Shared pieces live in `ui/src/components/layout/`: `SectionLayout`, `Pane`, `MasterDetail`, `Panel`, `Collapsible`, `EmptyState`,
`Notice`, `Progress`, `Tabs`, `Toolbar`. Components of `components/ui/` keep the shadcn API with the design system variants (`Button`
`brand` and `glass`, `Badge` `ok`/`warn`/`danger`/`info`/`brand`/`plain`, `Card` `surface` and `glass`).

## One scroll per region

A bounded section fits the window: the page never scrolls, and each region that can grow has exactly one scroll of its own. The root is
`SectionLayout`; a region that scrolls is a `Pane`; its ancestors up to the root carry `min-h-0` and no overflow of their own. A dialog that
can grow puts its content in `DialogBody`: the header and the footer stay, only the body scrolls.

A scrolling region is positioned (`Pane` and `main` are `relative`). Radix Select and Checkbox render a hidden native input with
`position: absolute`; without a positioned scrolling ancestor it is placed against the document and stretches it past the window. The
layout smoke fails when the page is taller than the window. The document is not clipped with `overflow` instead: in WebView2 that stops
`loading="lazy"` images below a scrolling region's edge from loading ahead of the scroll.

```tsx
<SectionLayout>
    <Toolbar>{actions}</Toolbar>
    <MasterDetail>
        <Pane surface aria-label={t("section.sets")}>{list}</Pane>
        <Pane>{details}</Pane>
    </MasterDetail>
</SectionLayout>
```

```tsx
<DialogContent>
    <DialogHeader>…</DialogHeader>
    <DialogBody>{longContent}</DialogBody>
    <DialogFooter>…</DialogFooter>
</DialogContent>
```

A dialog with a form wraps the body and the footer in `DialogForm`, so Enter in a field and the submit button of the footer send it while
only the body scrolls.

```tsx
<DialogHeader>…</DialogHeader>
<DialogForm onSubmit={submit}>
    <DialogBody>{fields}</DialogBody>
    <DialogFooter><Button type="submit">…</Button></DialogFooter>
</DialogForm>
```

Grids that hold truncated or `nowrap` text use `minmax(0, 1fr)` columns, and flex children `min-w-0`: otherwise the longest line widens
the region and the window scrolls horizontally. The smoke layout check verifies this at the 1080p and the minimum window sizes.

A scrolling region never holds another one (`max-h-*` with its own overflow inside a `Pane`): long content gets its own tab, its own
column or a dialog, and a disclosure inside a list unfolds inline. Two work columns of a tab (a form and its results) scroll each at `lg`
and wider; below `lg` they stack and the tab itself is the one scroll. A table wider than its region scrolls horizontally with
`overflow-x-auto overflow-y-hidden`: it never becomes a second vertical scroll.

Work sections (catalogue, images, backgrounds, generation, datasets, training, models, checks) are bounded. Dashboards (the overview,
analytics) and the library scroll with the page instead: `main` is their only scroll, so their lists are paged ("show more") and never get
a `max-h` scroll of their own. The smoke layout check opens every tab of a background set, an image group, generation, datasets and a
chosen dataset, training and checks, and the classes and models sections, and fails on a scrolling region inside another, more than three of
them or one squeezed below 96 px; in the overview and analytics it fails on any scrolling region.

## List rows

A row of a long list is one line: the name and a short status or count. Its actions are compact icon buttons with an accessible name and a
`title`; in a narrow list column they float over the right end of the row while it is hovered, holds the focus or is selected, and they stay
in the tab order (only their opacity changes). A hint that applies to every row is shown once, under the list or in the empty state.

## Tabs of a section

A detail column with more than one kind of content splits it into `Tabs` under a compact header. A tab that holds a form keeps it mounted
while another tab is shown (`forceMount` with `hidden`), so the chosen files and the fields survive. Work a section runs in the background
is always visible: a count and a live dot on its history tab (`TabsTrigger count live`), and the progress of running jobs next to the form
that started them (`ActiveJobs`).

```tsx
<TabsTrigger value="runs" count={runs.jobs.length} live={runs.active}>{t("tabs.runs")}</TabsTrigger>
<TabsContent value="add" forceMount hidden={tab !== "add"}>…</TabsContent>
```
