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

	/// <summary>All built-in recipes, in catalog order.</summary>
	public static IReadOnlyList<LanguageServerDescriptor> All { get; } = [TypeScript];

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
