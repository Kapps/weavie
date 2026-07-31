# Warm language clients

**Status:** implemented as part of [LSP on the session bus](lsp-over-bridge.md).

Language clients are owned by exact `ClientSession` instances, not by the selected view. The pool keeps one
client per session incarnation and language server while matching models are open. A selection change only
changes which model the shared editor presents; it does not rebind, prune, or restart language services.

Session-namespaced Monaco URIs make provider ownership automatic. Closing a `ClientSession`, changing its
configuration, or disposing its models removes only that session's clients. A background session and a
session on another connected host remain live because every `HostConnection` keeps its own transport open.

The complete ownership, protocol, lifecycle, and test contract now lives in
[lsp-over-bridge.md](lsp-over-bridge.md).
