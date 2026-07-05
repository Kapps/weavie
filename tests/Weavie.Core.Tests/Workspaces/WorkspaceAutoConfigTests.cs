using System.Text.Json;
using Weavie.Core.Configuration;
using Weavie.Core.TestRunning;
using Weavie.Core.Workspaces;
using Xunit;

namespace Weavie.Core.Tests.Workspaces;

/// <summary>
/// Exercises <see cref="WorkspaceAutoConfig"/>: it writes only unset workspace-scoped settings, never clobbers a
/// user value, and never writes an empty profile (a runner-unknown detection leaves <c>test.profile</c> unset so
/// the setup card stays up). Backed by a real temp-file <see cref="SettingsStore"/>.
/// </summary>
public sealed class WorkspaceAutoConfigTests : IDisposable {
	private const string Root = "/repo";

	private readonly string _dir = Path.Combine(Path.GetTempPath(), "weavie-autoconfig-tests", Guid.NewGuid().ToString("N"));

	public WorkspaceAutoConfigTests() {
		Directory.CreateDirectory(_dir);
	}

	public void Dispose() {
		try {
			Directory.Delete(_dir, recursive: true);
		} catch (IOException) {
		} catch (UnauthorizedAccessException) {
		}
	}

	private static readonly TestRule GoRule = new() {
		Glob = "**/*_test.go",
		Symbol = "^(Test\\w+)",
		RunOne = "go test ${fileDir} -run '^${name}$'",
		RunFile = "go test ${fileDir}",
	};

	private SettingsStore NewStore() {
		string wsFile = Path.Combine(_dir, Guid.NewGuid().ToString("N") + "-ws.toml");
		return new SettingsStore(CoreSettings.CreateRegistry(),
			Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".toml"), enableWatcher: false, _ => wsFile);
	}

	private static WorkspaceDetection Detection(string? setup, IReadOnlyList<TestRule> rules) => new() {
		HasManifest = true,
		SetupCommand = setup,
		TestRules = rules,
		ConfiguredLanguages = ["Go"],
	};

	private static JsonElement Json(string value) => JsonSerializer.SerializeToElement(value);

	[Fact]
	public void UnsetWorkspace_WritesBoth_AndProfileParsesBack() {
		var store = NewStore();

		var outcome = new WorkspaceAutoConfig(store, Root).Apply(Detection("go mod download", [GoRule]));

		Assert.Equal(["worktree.setupCommand", "test.profile"], outcome.Wrote);
		Assert.Equal("go mod download", store.GetString("worktree.setupCommand", Root));
		Assert.True(TestProfile.TryParse(store.GetString("test.profile", Root)!, out var profile, out _));
		Assert.Equal("**/*_test.go", Assert.Single(profile.Rules).Glob);
	}

	[Fact]
	public void SetupCommandAlreadySet_IsNotClobbered() {
		var store = NewStore();
		store.Set("worktree.setupCommand", Json("my custom setup"), Root);

		var outcome = new WorkspaceAutoConfig(store, Root).Apply(Detection("go mod download", [GoRule]));

		Assert.Equal(["test.profile"], outcome.Wrote);
		Assert.Equal("my custom setup", store.GetString("worktree.setupCommand", Root));
	}

	[Fact]
	public void TestProfileAlreadySet_IsNotClobbered() {
		var store = NewStore();
		store.Set("test.profile", Json("[]"), Root); // "no tests here" — an explicit user choice

		var outcome = new WorkspaceAutoConfig(store, Root).Apply(Detection("go mod download", [GoRule]));

		Assert.Equal(["worktree.setupCommand"], outcome.Wrote);
		Assert.Equal("[]", store.GetString("test.profile", Root));
	}

	[Fact]
	public void RunnerUnknown_WritesSetupOnly_LeavesProfileUnsetForTheCard() {
		var store = NewStore();

		var outcome = new WorkspaceAutoConfig(store, Root).Apply(Detection("npm install", []));

		Assert.Equal(["worktree.setupCommand"], outcome.Wrote);
		Assert.True(string.IsNullOrEmpty(store.GetString("test.profile", Root)));
	}

	[Fact]
	public void NoPresetMatched_WritesNothing() {
		var store = NewStore();

		var outcome = new WorkspaceAutoConfig(store, Root).Apply(Detection(setup: null, []));

		Assert.Empty(outcome.Wrote);
		Assert.True(string.IsNullOrEmpty(store.GetString("worktree.setupCommand", Root)));
	}
}
