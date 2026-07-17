namespace Weavie.Core.Lsp;

/// <summary>
/// A per-language "recipe": which language ids and file extensions it serves, the candidate executables to
/// launch (in preference order), and the root markers that identify a project root. Acquisition is
/// bring-your-own: each candidate is resolved on <c>PATH</c> (see <see cref="ServerResolver"/>).
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

	/// <summary>Filenames whose presence marks a project root (e.g. <c>tsconfig.json</c>, <c>.git</c>).</summary>
	public IReadOnlyList<string> RootMarkers { get; init; } = [];

	/// <summary>
	/// Default server-specific settings (JSON object string) sent as both LSP <c>initializationOptions</c> and the
	/// answer to <c>workspace/configuration</c>. Some servers gate features on these (gopls needs
	/// <c>{ "semanticTokens": true }</c>). <see langword="null"/> means no defaults.
	/// </summary>
	public string? DefaultSettingsJson { get; init; }
}

/// <summary>
/// One way to launch a language server: a command name (resolved on <c>PATH</c>) plus its fixed arguments.
/// Listing several lets a recipe prefer one server but fall back to another.
/// </summary>
/// <param name="Command">The executable/command name to resolve on <c>PATH</c> (no extension needed).</param>
/// <param name="Arguments">Fixed arguments passed to the server (e.g. <c>--stdio</c>).</param>
public sealed record ServerLaunchCandidate(string Command, IReadOnlyList<string> Arguments) {
	/// <summary>
	/// How Weavie can install this candidate itself when it's missing (into <see cref="WeaviePaths.Tools"/>);
	/// <see langword="null"/> means bring-your-own only. Recipe-carrying candidates also resolve from that
	/// folder, so a Weavie-installed server is found without any PATH change.
	/// </summary>
	public ServerInstallRecipe? Install { get; init; }
}
