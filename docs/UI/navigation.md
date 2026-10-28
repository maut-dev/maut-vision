# Navigation in the UI

The UI is loaded as one page `https://maut-ui.invalid/index.html`. The host accepts bridge messages and navigation only from this address;
the page path is part of the WebView2 security policy, not a navigation mechanism.

```text
allowed:    /index.html, /index.html#/projects/<id>
forbidden:  /projects/<id>  (history.pushState with a path)
```

A screen is React state. If a router is needed, memory or hash mode is used: changing the `#` fragment does not change the document path.
A router with the History API and paths (`BrowserRouter`, `createBrowserRouter`) is not used: after `pushState` the document address no
longer passes the host check, and UI messages are silently dropped. Do not extend the list of allowed paths for a router.

A transition from a section may carry an intent: `groupId` opens an image group, `versionId` — a version passport in "Models", `runId` — a
run on the "Current run" tab of training (a run outside the first history page is read with `training.getRun`), `checksMode` — the
"Dataset" mode of checks. A section with an intent is recreated (`key` includes the intent). The sidebar carries no intent. A section with a
long live operation that nobody would watch after leaving (a screen check) stops it on unmount.

Core is the source of truth for data. When a screen opens, after a page reload and after a WebView2 process restart, the UI requests the
current state; the address and React state restore only the screen choice. If a choice must survive restarts (for example, the last opened
project), Core keeps it.

Leaving a screen with unsaved changes requires confirmation; the UI enforces this rule for every way of navigating, including the mouse
and keyboard "back" buttons.

The window close button only hides the window to the tray ([Desktop](../Desktop/architecture.md#window-tray-and-single-instance)), and the
page becomes hidden. Exit ("Exit" in the tray) does not raise `beforeunload`, so the host first asks the page: the event
`app.closeRequested {requestId}` and the answer `system.closeDecision {requestId, allow}`. Without unsaved changes the UI answers
`allow: true`, and the application exits. With changes the UI answers `allow: false` and shows the shared unsaved changes dialog; after
"save and continue" or "leave without saving" the UI asks the host to complete the exit (`system.close`), and "continue editing" keeps the
application open. If there is no answer within 1 s (the page hangs or the WebView2 process crashed), the application exits: exit does not
depend on the page.

```ts
useEffect(() => subscribeCloseRequested(event => {
    const dirty = registration.current?.current.dirty ?? false;
    void systemApi.closeDecision({ requestId: event.data.requestId, allow: !dirty });
    if (dirty) setPending({ intent: () => void systemApi.close() });
}), []);
```

External links never open in WebView2: the UI calls a host command that opens the system browser. New windows and navigation to foreign
addresses are forbidden by the host policy.

Project images and video are loaded from a separate host `https://maut-resource.invalid/r/<token>` ([resource channel](../Desktop/resources.md)).
It is not a page: navigation to it is forbidden, and the host serves only requests of the UI document. The build CSP (`ui/vite.config.ts`)
allows this host in `img-src`, `media-src` and `connect-src`; the CSP host list and `WebViewPolicy` change together.
