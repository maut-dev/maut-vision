# Resource channel

Images, thumbnails, previews and video frames do not travel through the bridge: a message is limited to 32 KB, and base64 adds a third
to the size and keeps the UI thread busy parsing. A DTO contains only the resource URL; WebView2 fetches the bytes with a separate
request.

```text
Controller → DTO { thumbnailUrl: "https://maut-resource.invalid/r/<token>" }
<img src=…> → WebResourceRequested → ResourceHandles → IProject.Resources.OpenAsync → file
```

## Issuing URLs

A controller issues the URL when it maps an entity to a DTO. The token is 128 random bits; neither the path nor the material ID appears in
the URL.

```csharp
// CatalogResponseMapper: a class variant image gets URLs of the original and the thumbnail.
var reference = new ResourceReference(ClassAsset.ResourceKind, asset.Id);
return new ClassAssetResponseDto(asset.Id, …,
    resources.Issue(projectId, reference, ResourceVariant.Original),
    resources.Issue(projectId, reference, ResourceVariant.Thumbnail));
```

- A token is valid for 10 minutes and only for the page it was issued to: navigation or reload of the UI and closing the window
  invalidate all tokens. Issuing the same resource again in the first half of the lifetime returns the same URL, so WebView2 reuses the
  already loaded image.
- The registry holds at most 20,000 tokens; on overflow the oldest are removed. An unknown, expired or evicted token gives 404, after
  which the UI requests a fresh DTO.
- Only requests of the UI document are served: source Document, the current page `https://maut-ui.invalid/index.html`, an Origin or
  Referer header of that page. Navigation to the resource host is forbidden by the [WebView2 policy](../UI/navigation.md).

## Transient resources

Previews that are not project files (a frame of a selected file or video for cropping, samples of future backgrounds) are returned by
Core as encoded bytes, and the controller issues a temporary URL of the same form for them. The bytes are kept in host memory for the
token lifetime and only for this page; the total is limited to 96 MB, and the oldest are removed on overflow. Every issue is a new URL.

```csharp
var preview = await project.Backgrounds.PreviewFileAsync(path);
var url = resources.IssueTransient(projectId, preview.Preview.Content, preview.Preview.ContentType);
```

Screenshots and previews of live model checks are served the same way: the worker writes a preview as a file, Core keeps only the latest
one in memory, and the host issues one temporary URL per new preview.

## Response

The request is validated on the UI thread; opening and reading the file happen on the thread pool under a deferral. The response has a
`Content-Type` from the allowed list, `Cache-Control: private, max-age=<remaining token lifetime>`, `Accept-Ranges: bytes` and
`X-Content-Type-Options: nosniff`; errors have no body and no paths.

WebView2 requires the whole body by the time the deferral completes, so the response is buffered: a Range request gets at most 4 MB
(`206` with `Content-Range`; the browser requests the next part itself), a request without Range — a file up to 64 MB, larger ones get
`413`. A video element always requests ranges. WebView2 gives no notification when a request is aborted: reading is cancelled by the end
of the page session or by closing the window.

## UI

```tsx
<ResourceImage src={image.thumbnailUrl} alt={image.name} width={256} height={192} onExpired={reloadPage} />
```

`ResourceImage` loads lazily (`loading="lazy"`, `decoding="async"`), reserves space by size and shows a placeholder. A load error usually
means an expired token, so the component calls `onExpired` once — the owner rereads the DTO — and only then shows the error. The URL type
is `ResourceUrl` (`ui/src/api/resources`), the DTO check is `isResourceUrl`. The build CSP allows `img-src`, `media-src` and
`connect-src` for the resource host.

## Core files and thumbnails

`IProject.Resources.OpenAsync(reference, variant)` opens a material file by kind and ID. Only `MaterialFiles` builds the path: the kind is
checked against the `ResourceKinds` registry, the extension is given by the kind's manager, and the kind folder and the file are checked
for reparse points on every request.

```csharp
// Core composition: kinds class-assets, images and backgrounds; the extension is read from the row of its table.
ResourceKinds.Register(ClassAsset.ResourceKind, ClassesManager.FindAssetExtensionAsync);
ResourceKinds.Register(ProjectImage.ResourceKind, ImagesManager.FindExtensionAsync);
ResourceKinds.Register(BackgroundItem.ResourceKind, BackgroundsManager.FindExtensionAsync);
```

A kind whose files live in nested folders (dataset samples, `datasets`; model version files, `models`) is registered with
`RegisterNested`: the manager returns, by the material row, a path inside `files/<kind>/` of segments separated by `/`, and every level is
checked for reparse points on every request.

A thumbnail (`ResourceVariant.Thumbnail`) is created on the first request: SkiaSharp decodes with codec downscaling (`SKCodec`), fits the
image into 256 px, applies EXIF orientation and encodes JPEG q80 with a white background instead of transparency.

```text
projects/<projectId>/cache/thumbnails/v1/<kind>/<2 hex of ID>/<id>-<size>-<write time>.jpg
```

- The name contains the size and write time of the original: a changed original gets a new thumbnail, and the stale one is removed. New
  thumbnail parameters mean a new version folder.
- When a manager deletes a material file, it also evicts its thumbnails (`ResourcesManager.EvictThumbnails`); a failure does not hinder
  the deletion, and a leftover file goes away with `cache/`.
- Writing is atomic (a temporary file and a rename). The cache is disposable: `cache/` can be deleted at any time, and thumbnails will be
  created again.
- At most `ProcessorCount / 2` thumbnails are created at once for the whole application; concurrent requests for one thumbnail wait for
  one creation. Cancelling a request stops waiting, while an already started creation finishes and stays in the cache.
