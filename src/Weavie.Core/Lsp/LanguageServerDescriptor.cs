namespace Weavie.Core.Lsp;

/// <summary>
/// A per-language "recipe": which language ids and file extensions it serves, the candidate
/// executables to launch (in preference order), and the root markers that identify a project root.
/// Modeled on Zed's <c>LspAdapter</c> and nvim-lspconfig server defs. For the M0–M3 bring-up,
/// acquisition is <em>bring-your-own</em> — each candidate is resolved on <c>PATH</c>
/// (see <see cref="ServerResolver"/>); managed download is a deliberately deferred concern.
/// </summary>
public sealed record LanguageServerDescriptor {
	/// <summary>Stable identifier for this server recipe (e.g. <c>"typescript"</c>), used in the bridge URL path.</summary>
	public required string Id { get; init; }

	/// <summary>Human-readable name for logs and status (e.g. <c>"TypeScript / JavaScript"</c>).</summary>
	public required string DisplayName { get; init; }

	/// <summary>The LSP language ids this server handles (e.g. <c>typescript</c>, <c>typescriptreact</c>).</summary>
	public required IReadOnlyList<string> LanguageIds { get; init; }

	/// <summary>File extensions (with leading dot) that map to this server, for host-side file watching.</summary>
	public required IReadOnlyList<string> FileExtensions { get; init; }

	/// <summary>Launch candidates in preference order; the first one resolvable on <c>PATH</c> wins.</summary>
	public required IReadOnlyList<ServerLaunchCandidate> Candidates { get; init; }

	/// <summary>
	/// Filenames whose presence marks a project root (e.g. <c>tsconfig.json</c>, <c>.git</c>). Used
	/// later to pick the workspace root per server; informational for the M0 single-root bring-up.
	/// </summary>
	public IReadOnlyList<string> RootMarkers { get; init; } = [];

	/// <summary>
	/// Default server-specific settings, as a JSON object string, sent both as LSP
	/// <c>initializationOptions</c> and as the answer to the server's <c>workspace/configuration</c>
	/// requests. Some servers gate features on these (e.g. gopls needs <c>{ "semanticTokens": true }</c>
	/// to emit semantic tokens). <see langword="null"/> means "no defaults". (Spec §15: the bridge must
	/// answer configuration from an adapter-supplied map or features silently degrade.)
	/// </summary>
	public string? DefaultSettingsJson { get; init; }
}

/// <summary>
/// One way to launch a language server: a command name (resolved on <c>PATH</c>) plus its fixed
/// arguments. Listing several lets a recipe prefer one server but fall back to another
/// (e.g. <c>vtsls</c> then <c>typescript-language-server</c>).
/// </summary>
/// <param name="Command">The executable/command name to resolve on <c>PATH</c> (no extension needed).</param>
/// <param name="Arguments">Fixed arguments passed to the server (e.g. <c>--stdio</c>).</param>
public sealed record ServerLaunchCandidate(string Command, IReadOnlyList<string> Arguments);
