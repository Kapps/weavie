using System.Text.Json;
using Weavie.Core.Configuration;
using Weavie.Core.Lsp;
using Xunit;

namespace Weavie.Core.Tests;

/// <summary>The per-language server command setting: its shape, its layering, and how it resolves to a launch.</summary>
[Collection("Settings")]
public sealed class LspSettingsTests : IDisposable {
	private static readonly string Key = LspSettings.CommandKey("csharp");

	// `dotnet` is on PATH wherever tests run, so the built-in recipe always resolves.
	private static readonly LanguageServerDescriptor Recipe = LanguageServerCatalog.CSharp with { Candidates = [new("dotnet", [])] };

	private readonly TempDirectory _dir = new("weavie-lsp-settings");
	private readonly SettingsStore _store;
	private readonly string _repo;

	public LspSettingsTests() {
		var registry = new SettingsRegistry();
		LspSettings.Register(registry);
		_store = new SettingsStore(
			registry, _dir.Combine("settings.toml"), enableWatcher: false, root => Path.Combine(root, ".weavie", "settings.toml"));
		_repo = _dir.CreateDirectory("repo");
		_store.RegisterWorkspace(_repo);
	}

	public void Dispose() {
		_store.Dispose();
		_dir.Dispose();
	}

	private static JsonElement Json(string argv) => JsonSerializer.SerializeToElement(argv);

	private static JsonElement Raw(string json) => JsonDocument.Parse(json).RootElement.Clone();

	private ServerLaunchCandidate Configured(string worktree) =>
		Assert.Single(LspSettings.Effective(Recipe, _store, _repo, worktree).Candidates);

	[Fact]
	public void EveryRecipeHasAWorkspaceScopedCommand() {
		var registry = new SettingsRegistry();
		LspSettings.Register(registry);

		Assert.Equal(
			LanguageServerCatalog.All.Select(d => $"lsp.{d.Id}.command"),
			registry.Definitions.Select(d => d.Key));
		Assert.All(registry.Definitions, d => Assert.Equal(SettingScope.Workspace, d.Scope));
	}

	[Theory]
	[InlineData("tsgo --lsp")]
	[InlineData("{}")]
	[InlineData("[]")]
	[InlineData("[\" \"]")]
	[InlineData("[\"tsgo\",1]")]
	public void RejectsAnythingButAnArgvArray(string value) {
		Assert.Throws<SettingValidationException>(() => _store.Set(Key, Json(value), _repo));
		Assert.Same(Recipe, LspSettings.Effective(Recipe, _store, _repo, _repo));
	}

	[Theory]
	[InlineData("{}")]
	[InlineData("[]")]
	[InlineData("[\"tsgo\",1]")]
	[InlineData("5")]
	[InlineData("null")]
	[InlineData("\"\\\"[\\\\\\\"tsgo\\\\\\\"]\\\"\"")]
	public void RejectsAJsonValueThatIsNotAnArgvArray(string json) {
		Assert.Throws<SettingValidationException>(() => _store.Set(Key, Raw(json), _repo));
		Assert.Same(Recipe, LspSettings.Effective(Recipe, _store, _repo, _repo));
	}

	[Fact]
	public void TheArrayItselfIsStoredAsItsText() {
		_store.Set(Key, Raw("[\"my-ls\", \"--stdio\"]"), _repo);

		Assert.Equal("[\"my-ls\", \"--stdio\"]", _store.RequireString(Key, _repo));
		Assert.Equal("my-ls", Configured(_repo).Command);
		Assert.Equal(["--stdio"], Configured(_repo).Arguments);
	}

	[Fact]
	public void APathIsAnchoredAtTheSessionWorktree() {
		_store.Set(Key, Json("[\"tools/ls\",\"--stdio\",\"-v\"]"), _repo);
		string other = _dir.CreateDirectory("other");

		Assert.Equal(Path.Combine(_repo, "tools", "ls"), Configured(_repo).Command);
		Assert.Equal(["--stdio", "-v"], Configured(_repo).Arguments);
		Assert.Equal(Path.Combine(other, "tools", "ls"), Configured(other).Command);
	}

	[Fact]
	public void ABareNameStaysAPathLookupAndHomeExpands() {
		_store.Set(Key, Json("[\"my-ls\"]"), _repo);
		Assert.Equal("my-ls", Configured(_repo).Command);

		_store.Set(Key, Json("[\"~/bin/ls\"]"), _repo);
		Assert.Equal(
			Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "bin", "ls"),
			Configured(_repo).Command);
	}

	[Fact]
	public void AWorkspaceOverridesTheUserWideCommand() {
		string other = _dir.CreateDirectory("other");
		_store.RegisterWorkspace(other);
		_store.Set(Key, Json("[\"user-ls\"]"));
		_store.Set(Key, Json("[\"repo-ls\"]"), _repo);

		Assert.Equal("repo-ls", Configured(_repo).Command);
		Assert.Equal("user-ls", Assert.Single(LspSettings.Effective(Recipe, _store, other, other).Candidates).Command);
	}

	[Fact]
	public void AConfiguredCommandReplacesTheBuiltInsEvenWhenMissing() {
		Assert.NotNull(ServerResolver.Resolve(LspSettings.Effective(Recipe, _store, _repo, _repo)));

		_store.Set(Key, Json("[\"tools/ls.exe\",\"--stdio\"]"), _repo);
		Assert.Null(ServerResolver.Resolve(LspSettings.Effective(Recipe, _store, _repo, _repo)));

		string server = Path.Combine(Directory.CreateDirectory(Path.Combine(_repo, "tools")).FullName, "ls.exe");
		File.WriteAllText(server, "");
		var resolved = ServerResolver.Resolve(LspSettings.Effective(Recipe, _store, _repo, _repo));
		Assert.Equal(server, resolved?.ServerPath);
		Assert.Equal(["--stdio"], resolved?.Arguments);
	}
}
