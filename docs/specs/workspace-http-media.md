# Workspace HTTP media

**Status:** implemented.

## Decision

Every HostCore owns one `WorkspaceHttpServer` from `Weavie.Hosting`. Windows, macOS, Linux, and Headless use
that exact Kestrel implementation for the workspace app and file data plane. Native shells retain their
in-process WebView message bridges for control messages; Headless attaches its WebSocket bridge to the same
server. Welcome-only windows may retain native resource schemes because no workspace or HostCore exists yet.

Native servers bind only to `127.0.0.1` on an OS-assigned port and mint a 256-bit token. Headless local mode
does the same on its selected loopback port; remote mode uses the runner-derived worker token and network binding. Index,
media, bridge, control, and unknown routes are default-deny; hashed static assets are public. The document uses
`Referrer-Policy: no-referrer` so its token is not sent to external navigations.

## Media scope

`/weavie-media` requires all of:

- the server token;
- an exact currently loaded `HostSession.Id`;
- a passive-media content type — `image/*` (except `image/svg+xml`) or `video/*`.

The route serves any path the editor can open, matching the bridge-backed file provider so the two do not
maintain competing validators. The content-type gate is the security-relevant one: it stops active content
(HTML, SVG) being served same-origin regardless of where the file lives. Missing, unloaded, malformed, and
non-media requests all return 404.

## HTTP behavior

The endpoint opens a `FileStream` with asynchronous sequential access and returns ASP.NET Core's range-enabled
file result. It never reads a whole file and never enters the UI dispatcher. The framework supplies full,
prefix/suffix/open Range responses, 206/416, HEAD, `If-Range`, and cancellation. Responses include a weak
mtime/size ETag, Last-Modified, `Cache-Control: private, no-cache`, and `nosniff`.

The web builds the URL from the backend's resource descriptor plus session/path/revision. The page-serving
server injects an origin-relative base so a reverse-proxied remote worker uses the browser's authoritative
origin; remote-agent handshakes return an absolute bridge/resource pair and replace both on reconnect. An
unchanged file keeps the same URL across pane and session remounts, letting the browser reuse or conditionally
validate it. A live file-change push increments only the visible URL's revision.

The removed path was `fs-stat` → `fs-read-bytes` → host `ReadAllBytes`/base64 JSON → browser JSON parse/`atob`
and byte copy → Blob URL. Removing it also removes the media size cap and its extra stat round trip.
