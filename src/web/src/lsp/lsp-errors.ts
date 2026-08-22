/** The message text of a thrown value, for a log line or a toast. */
export function describeError(err: unknown): string {
  return err instanceof Error ? err.message : String(err);
}

// The vscode shim's CancellationError has no type identity across the bundle seam, so `name` is its stable
// marker (LSPCancellationError extends it and inherits the name).
/** Was this thrown because the editor or the server cancelled the request, rather than because it failed? */
export function isCancellation(err: unknown): boolean {
  return err instanceof Error && err.name === "Canceled";
}
