namespace Weavie.Core.Lsp;

/// <summary>
/// The built-in set of <see cref="LanguageServerDescriptor"/> recipes. Bring-up grows this list one
/// milestone at a time (M0/M1 TypeScript, M2 C#, M3 Go). Lookup is by LSP language id, matching the
/// id Monaco assigns a model — the bridge URL carries that id so the host knows which server to spawn.
/// </summary>
public static class LanguageServerCatalog {
	/// <summary>
	/// TypeScript / JavaScript. Prefers <c>tsgo</c> (ts-go / TypeScript 7's native LSP) per the spec's
	/// "replace Monaco's bundled TS immediately", falling back to the tsserver-based <c>vtsls</c> then
	/// the classic <c>typescript-language-server</c> if tsgo isn't installed (bring-your-own, on PATH).
	/// </summary>
	public static LanguageServerDescriptor TypeScript { get; } = new() {
		Id = "typescript",
		DisplayName = "TypeScript / JavaScript",
		LanguageIds = ["typescript", "typescriptreact", "javascript", "javascriptreact"],
		FileExtensions = [".ts", ".tsx", ".mts", ".cts", ".js", ".jsx", ".mjs", ".cjs"],
		Candidates = [
			new("tsgo", ["--lsp", "-stdio"]),
			new("vtsls", ["--stdio"]),
			new("typescript-language-server", ["--stdio"]),
		],
		RootMarkers = ["tsconfig.json", "jsconfig.json", "package.json", ".git"],
	};

	/// <summary>
	/// C#. Served by <c>csharp-ls</c> (a Roslyn-based .NET global tool; speaks LSP over stdio with no
	/// flags). The heavier Roslyn <c>Microsoft.CodeAnalysis.LanguageServer</c> is a deferred upgrade.
	/// </summary>
	public static LanguageServerDescriptor CSharp { get; } = new() {
		Id = "csharp",
		DisplayName = "C#",
		LanguageIds = ["csharp"],
		FileExtensions = [".cs"],
		Candidates = [new("csharp-ls", [])],
		RootMarkers = [".sln", ".slnx", ".csproj", ".git"],
	};

	/// <summary>
	/// Go. Served by <c>gopls</c> (the reference Go LSP; serves over stdio with no flags). Acquired via
	/// <c>go install golang.org/x/tools/gopls@latest</c> (bring-your-own, on PATH).
	/// </summary>
	public static LanguageServerDescriptor Go { get; } = new() {
		Id = "go",
		DisplayName = "Go",
		LanguageIds = ["go"],
		FileExtensions = [".go"],
		Candidates = [new("gopls", [])],
		RootMarkers = ["go.work", "go.mod", ".git"],
		// gopls emits no semantic tokens unless this is enabled in its settings.
		DefaultSettingsJson = "{\"semanticTokens\":true}",
	};

	/// <summary>All built-in recipes, in catalog order.</summary>
	public static IReadOnlyList<LanguageServerDescriptor> All { get; } = [TypeScript, CSharp, Go];

	/// <summary>
	/// Returns the recipe that handles <paramref name="languageId"/> (case-insensitive), or
	/// <see langword="null"/> if no built-in server claims that language.
	/// </summary>
	/// <param name="languageId">The LSP language id (e.g. <c>typescript</c>).</param>
	public static LanguageServerDescriptor? ForLanguage(string languageId) =>
		All.FirstOrDefault(d => d.LanguageIds.Contains(languageId, StringComparer.OrdinalIgnoreCase));

	/// <summary>
	/// Returns the recipe whose id equals <paramref name="serverId"/> (case-insensitive), or
	/// <see langword="null"/>. Used when the bridge URL carries a server id directly.
	/// </summary>
	/// <param name="serverId">The <see cref="LanguageServerDescriptor.Id"/> to look up.</param>
	public static LanguageServerDescriptor? ForServerId(string serverId) =>
		All.FirstOrDefault(d => string.Equals(d.Id, serverId, StringComparison.OrdinalIgnoreCase));
}
