// Distinguishes page instances without requiring secure-context Web Crypto. Native peers survive a reload,
// and remote workers may be opened over plain HTTP, so both request and LSP channel ids carry this epoch.
export const PAGE_EPOCH = Math.random().toString(36).slice(2, 10).padEnd(8, "0");
