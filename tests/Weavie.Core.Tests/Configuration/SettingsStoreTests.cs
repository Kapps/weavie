using System.Text.Json;
using Weavie.Core.Configuration;
using Xunit;

namespace Weavie.Core.Tests;

/// <summary>
/// Tests <see cref="SettingsStore"/> against an on-disk temp file: resolution precedence, per-kind
/// coercion, comment/unknown-key preservation, atomic + validated writes, malformed-file policy,
/// env-shadow reporting, and the debounced file-watch change hub. Serialized via the <c>Settings</c>
/// collection because several tests mutate process env vars.
/// </summary>
[Collection("Settings")]
public sealed class SettingsStoreTests : IDisposable {
	private readonly string _dir = Path.Combine(Path.GetTempPath(), "weavie-settings-tests", Guid.NewGuid().ToString("N"));

	public SettingsStoreTests() {
		Directory.CreateDirectory(_dir);
	}

	public void Dispose() {
		try {
			Directory.Delete(_dir, recursive: true);
		} catch (IOException) {
			// A watcher may briefly hold the directory; a leaked temp dir is harmless.
		} catch (UnauthorizedAccessException) {
		}
	}

	private string FilePath => Path.Combine(_dir, "settings.toml");

	private static SettingsRegistry ScalarRegistry() {
		var registry = new SettingsRegistry();
		registry.Register(new SettingDefinition { Key = "t.str", Kind = SettingKind.String, Description = "a string", Default = "fallback" });
		registry.Register(new SettingDefinition { Key = "t.flag", Kind = SettingKind.Bool, Description = "a flag", Default = false });
		registry.Register(new SettingDefinition { Key = "t.num", Kind = SettingKind.Int, Description = "a number", Default = 0L });
		return registry;
	}

	// A registry with one workspace-scoped and one user-scoped string key, for the per-workspace layer tests.
	private static SettingsRegistry ScopedRegistry() {
		var registry = new SettingsRegistry();
		registry.Register(new SettingDefinition {
			Key = "t.wsstr", Kind = SettingKind.String, Description = "a per-workspace string",
			Scope = SettingScope.Workspace, Default = "ws-default",
		});
		registry.Register(new SettingDefinition {
			Key = "t.userstr", Kind = SettingKind.String, Description = "a user string", Default = "user-default",
		});
		return registry;
	}

	// The overlay file RegisterWorkspace resolves for a workspace root.
	private static string WorkspaceFile(string root) => Path.Combine(root, ".weavie", "settings.toml");

	private string WorkspaceRoot {
		get {
			string root = Path.Combine(_dir, "repo");
			Directory.CreateDirectory(root);
			return root;
		}
	}

	private static JsonElement Json(string raw) => JsonDocument.Parse(raw).RootElement.Clone();

	[Fact]
	public void Resolution_Precedence_EnvBeatsFileBeatsDefault() {
		File.WriteAllText(FilePath, "t.str = \"from-file\"\n");
		var registry = ScalarRegistry();

		// File value, no env.
		using (var store = new SettingsStore(registry, FilePath, enableWatcher: false)) {
			Assert.Equal("from-file", store.Resolve("t.str").Value);
			Assert.Equal(SettingSource.UserFile, store.Resolve("t.str").Source);
		}

		// Env wins over file.
		using (EnvScope("WEAVIE_T_STR", "from-env"))
		using (var store = new SettingsStore(registry, FilePath, enableWatcher: false)) {
			Assert.Equal("from-env", store.Resolve("t.str").Value);
			Assert.Equal(SettingSource.Environment, store.Resolve("t.str").Source);
		}

		// No file key, no env -> default.
		File.WriteAllText(FilePath, "");
		using (var store = new SettingsStore(registry, FilePath, enableWatcher: false)) {
			Assert.Equal("fallback", store.Resolve("t.str").Value);
			Assert.Equal(SettingSource.Default, store.Resolve("t.str").Source);
		}
	}

	[Fact]
	public void Coercion_FromFile_ProducesTypedValues() {
		File.WriteAllText(FilePath, "t.str = \"hello\"\nt.flag = true\nt.num = 42\n");
		using var store = new SettingsStore(ScalarRegistry(), FilePath, enableWatcher: false);

		Assert.Equal("hello", store.Resolve("t.str").Value);
		Assert.Equal(true, store.Resolve("t.flag").Value);
		Assert.Equal(42L, store.Resolve("t.num").Value);
	}

	[Fact]
	public void Coercion_FromEnv_ParsesBoolAndIntCaseInsensitively() {
		using (EnvScope("WEAVIE_T_FLAG", "TRUE"))
		using (EnvScope("WEAVIE_T_NUM", "-17")) {
			using var store = new SettingsStore(ScalarRegistry(), FilePath, enableWatcher: false);
			Assert.Equal(true, store.Resolve("t.flag").Value);
			Assert.Equal(-17L, store.Resolve("t.num").Value);
		}
	}

	[Fact]
	public void Coercion_BadEnv_IsIgnoredLoudlyAndFallsBack() {
		var logged = new List<string>();
		using (EnvScope("WEAVIE_T_NUM", "not-a-number")) {
			using var store = new SettingsStore(ScalarRegistry(), FilePath, enableWatcher: false);
			store.Log += logged.Add;
			Assert.Equal(0L, store.Resolve("t.num").Value); // default
			Assert.Equal(SettingSource.Default, store.Resolve("t.num").Source);
		}

		Assert.Contains(logged, l => l.Contains("WEAVIE_T_NUM", StringComparison.Ordinal));
	}

	[Fact]
	public void Set_WritesValue_AndRaisesChange() {
		using var store = new SettingsStore(ScalarRegistry(), FilePath, enableWatcher: false);
		var changes = new List<SettingChange>();
		store.SettingChanged += changes.Add;

		var result = store.Set("t.str", Json("\"written\""));

		Assert.True(result.Written);
		Assert.Null(result.ShadowedByEnv);
		Assert.Equal("written", store.Resolve("t.str").Value);
		var change = Assert.Single(changes);
		Assert.Equal("t.str", change.Key);
		Assert.Equal("written", change.NewValue);
		Assert.Contains("t.str = \"written\"", File.ReadAllText(FilePath), StringComparison.Ordinal);
	}

	[Fact]
	public void Set_InjectsDescriptionComment_AboveNewKey() {
		using var store = new SettingsStore(ScalarRegistry(), FilePath, enableWatcher: false);
		store.Set("t.str", Json("\"x\""));

		string text = File.ReadAllText(FilePath);
		Assert.Contains("# a string", text, StringComparison.Ordinal);
		string[] lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
		int keyLine = Array.FindIndex(lines, l => l.StartsWith("t.str", StringComparison.Ordinal));
		Assert.True(keyLine > 0 && lines[keyLine - 1].Contains("# a string", StringComparison.Ordinal));
	}

	[Fact]
	public void Set_PreservesUserComments_AndUnknownPluginTables() {
		const string original = """
			# my own note
			t.str = "old"

			[plugins.acme-linter]
			severity = "error"
			""";
		File.WriteAllText(FilePath, original);
		using var store = new SettingsStore(ScalarRegistry(), FilePath, enableWatcher: false);

		store.Set("t.str", Json("\"new\""));

		string text = File.ReadAllText(FilePath);
		Assert.Contains("# my own note", text, StringComparison.Ordinal);       // user comment kept
		Assert.Contains("[plugins.acme-linter]", text, StringComparison.Ordinal); // unknown subtree kept
		Assert.Contains("severity = \"error\"", text, StringComparison.Ordinal);
		Assert.Contains("t.str = \"new\"", text, StringComparison.Ordinal);       // value updated in place
		Assert.DoesNotContain("# a string", text, StringComparison.Ordinal);     // existing line's comment not clobbered
	}

	[Fact]
	public void Set_PathKind_WritesLiteralStringForWindowsPath() {
		var registry = new SettingsRegistry();
		registry.Register(new SettingDefinition { Key = "claude.path", Kind = SettingKind.Path, Description = "claude" });
		using var store = new SettingsStore(registry, FilePath, enableWatcher: false);

		store.Set("claude.path", Json("\"C:\\\\tools\\\\claude.exe\""));

		string text = File.ReadAllText(FilePath);
		Assert.Contains("'C:\\tools\\claude.exe'", text, StringComparison.Ordinal); // single-quoted literal, no escaping
		Assert.DoesNotContain("\\\\", text, StringComparison.Ordinal);
		// Round-trips through a fresh store.
		using var reopened = new SettingsStore(registry, FilePath, enableWatcher: false);
		Assert.Equal(@"C:\tools\claude.exe", reopened.Resolve("claude.path").Value);
	}

	[Fact]
	public void Set_ThreeSegmentKey_WritesNestedDottedKey_AndRoundTrips() {
		var registry = new SettingsRegistry();
		registry.Register(new SettingDefinition { Key = "editor.font.size", Kind = SettingKind.Int, Description = "editor font size", Default = 0L });
		using var store = new SettingsStore(registry, FilePath, enableWatcher: false);

		store.Set("editor.font.size", Json("14"));

		Assert.Contains("editor.font.size = 14", File.ReadAllText(FilePath), StringComparison.Ordinal);
		// Round-trips through a fresh store, exercising the nested-table read path.
		using var reopened = new SettingsStore(registry, FilePath, enableWatcher: false);
		Assert.Equal(14L, reopened.Resolve("editor.font.size").Value);
	}

	[Fact]
	public void Set_AtomicWrite_LeavesNoTempFileAndParses() {
		using var store = new SettingsStore(ScalarRegistry(), FilePath, enableWatcher: false);
		store.Set("t.num", Json("123"));

		Assert.True(File.Exists(FilePath));
		Assert.False(File.Exists(FilePath + ".tmp"));
		Assert.Equal(123L, store.Resolve("t.num").Value);
	}

	[Fact]
	public void Set_UnknownKey_Throws() {
		using var store = new SettingsStore(ScalarRegistry(), FilePath, enableWatcher: false);
		Assert.Throws<UnknownSettingException>(() => store.Set("does.not.exist", Json("\"x\"")));
	}

	[Fact]
	public void Set_KindMismatch_Throws() {
		using var store = new SettingsStore(ScalarRegistry(), FilePath, enableWatcher: false);
		Assert.Throws<SettingValidationException>(() => store.Set("t.flag", Json("\"not-a-bool\"")));
		Assert.Throws<SettingValidationException>(() => store.Set("t.num", Json("\"NaN\"")));
	}

	[Fact]
	public void Set_StringifiedScalars_AreCoerced_LikeEnvVars() {
		// LLM tool calls routinely stringify scalars; the MCP boundary tolerates numeric/bool strings
		// (like the env-var path) but still rejects genuinely non-numeric/non-bool strings.
		using var store = new SettingsStore(ScalarRegistry(), FilePath, enableWatcher: false);

		store.Set("t.num", Json("\"16\""));   // stringified int
		Assert.Equal(16L, store.Resolve("t.num").Value);

		store.Set("t.flag", Json("\"true\"")); // stringified bool (case-insensitive)
		Assert.Equal(true, store.Resolve("t.flag").Value);
	}

	[Fact]
	public void Set_AllowedValues_RejectsOutsideTheSet() {
		var registry = new SettingsRegistry();
		registry.Register(new SettingDefinition {
			Key = "theme",
			Kind = SettingKind.String,
			Description = "color theme",
			AllowedValues = ["dark", "light"],
		});
		using var store = new SettingsStore(registry, FilePath, enableWatcher: false);

		store.Set("theme", Json("\"dark\""));            // allowed
		Assert.Equal("dark", store.Resolve("theme").Value);
		var ex = Assert.Throws<SettingValidationException>(() => store.Set("theme", Json("\"blue\"")));
		Assert.Contains("dark, light", ex.Message, StringComparison.Ordinal);
	}

	[Fact]
	public void Set_Validate_RejectsInvalidValue() {
		var registry = new SettingsRegistry();
		registry.Register(new SettingDefinition {
			Key = "t.even",
			Kind = SettingKind.Int,
			Description = "must be even",
			Validate = value => value is long l && l % 2 == 0
				? ValidationResult.Success
				: ValidationResult.Failure("must be even"),
		});
		using var store = new SettingsStore(registry, FilePath, enableWatcher: false);

		store.Set("t.even", Json("4"));
		Assert.Throws<SettingValidationException>(() => store.Set("t.even", Json("5")));
	}

	[Fact]
	public void Malformed_FallsBackToDefaults_AndRefusesWrites_NonDestructively() {
		const string broken = "t.str = = oops\n[unterminated\n";
		File.WriteAllText(FilePath, broken);
		var logged = new List<string>();

		using var store = new SettingsStore(ScalarRegistry(), FilePath, enableWatcher: false);
		store.Log += logged.Add;

		Assert.True(store.IsMalformed);
		Assert.Equal("fallback", store.Resolve("t.str").Value);            // default
		Assert.Throws<SettingsFileMalformedException>(() => store.Set("t.str", Json("\"x\"")));
		Assert.Equal(broken, File.ReadAllText(FilePath));                  // file left intact
	}

	[Fact]
	public void EnvShadow_IsReported_AndEffectiveValueUnchanged() {
		using (EnvScope("WEAVIE_T_STR", "env-wins")) {
			using var store = new SettingsStore(ScalarRegistry(), FilePath, enableWatcher: false);
			var changes = new List<SettingChange>();
			store.SettingChanged += changes.Add;

			var result = store.Set("t.str", Json("\"file-value\""));

			Assert.True(result.Written);
			Assert.Equal("WEAVIE_T_STR", result.ShadowedByEnv);
			Assert.Equal("env-wins", store.Resolve("t.str").Value);         // env still wins
			Assert.Contains("t.str = \"file-value\"", File.ReadAllText(FilePath), StringComparison.Ordinal); // file still written
			Assert.Empty(changes);                                          // effective value unchanged -> no event
		}
	}

	[Fact]
	public void Clear_RemovesOverride_FallsBackToDefault_AndRaisesChange() {
		File.WriteAllText(FilePath, "t.str = \"from-file\"\n");
		using var store = new SettingsStore(ScalarRegistry(), FilePath, enableWatcher: false);
		Assert.Equal("from-file", store.Resolve("t.str").Value);
		var changes = new List<SettingChange>();
		store.SettingChanged += changes.Add;

		var result = store.Clear("t.str");

		Assert.True(result.Removed);
		Assert.Null(result.ShadowedByEnv);
		Assert.Equal("fallback", store.Resolve("t.str").Value);
		Assert.Equal(SettingSource.Default, store.Resolve("t.str").Source);
		var change = Assert.Single(changes);
		Assert.Equal("t.str", change.Key);
		Assert.Equal("fallback", change.NewValue);
		Assert.DoesNotContain("t.str", File.ReadAllText(FilePath), StringComparison.Ordinal);
	}

	[Fact]
	public void Clear_NoOverride_IsNoOp_AndRaisesNoChange() {
		using var store = new SettingsStore(ScalarRegistry(), FilePath, enableWatcher: false);
		var changes = new List<SettingChange>();
		store.SettingChanged += changes.Add;

		var result = store.Clear("t.str"); // never set

		Assert.False(result.Removed);
		Assert.Empty(changes);
		Assert.Equal("fallback", store.Resolve("t.str").Value);
	}

	[Fact]
	public void Clear_RemovesKey_WrittenInHandEditedTableForm() {
		// A [table] header rather than the root dotted key Set writes — RemoveKeyLocked must handle both.
		File.WriteAllText(FilePath, "[editor.font]\nsize = 22\n");
		var registry = new SettingsRegistry();
		registry.Register(new SettingDefinition { Key = "editor.font.size", Kind = SettingKind.Int, Description = "size", Default = 0L });
		using var store = new SettingsStore(registry, FilePath, enableWatcher: false);
		Assert.Equal(22L, store.Resolve("editor.font.size").Value);

		var result = store.Clear("editor.font.size");

		Assert.True(result.Removed);
		Assert.Equal(0L, store.Resolve("editor.font.size").Value);
		Assert.DoesNotContain("size = 22", File.ReadAllText(FilePath), StringComparison.Ordinal);
	}

	[Fact]
	public void Clear_RemovesOnlyTargetKey_PreservesSiblingsAndUnknownTables() {
		const string original = """
			t.str = "keep-me"
			t.num = 7

			[plugins.acme-linter]
			severity = "error"
			""";
		File.WriteAllText(FilePath, original);
		using var store = new SettingsStore(ScalarRegistry(), FilePath, enableWatcher: false);

		store.Clear("t.num");

		string text = File.ReadAllText(FilePath);
		Assert.Contains("t.str = \"keep-me\"", text, StringComparison.Ordinal);    // sibling key kept
		Assert.Contains("[plugins.acme-linter]", text, StringComparison.Ordinal);  // unknown subtree kept
		Assert.Contains("severity = \"error\"", text, StringComparison.Ordinal);
		Assert.DoesNotContain("t.num", text, StringComparison.Ordinal);            // cleared key gone
		Assert.Equal("keep-me", store.Resolve("t.str").Value);
		Assert.Equal(0L, store.Resolve("t.num").Value);                            // default
	}

	[Fact]
	public void Clear_EnvShadow_IsReported_AndEffectiveValueUnchanged() {
		File.WriteAllText(FilePath, "t.str = \"file-value\"\n");
		using (EnvScope("WEAVIE_T_STR", "env-wins")) {
			using var store = new SettingsStore(ScalarRegistry(), FilePath, enableWatcher: false);
			var changes = new List<SettingChange>();
			store.SettingChanged += changes.Add;

			var result = store.Clear("t.str");

			Assert.True(result.Removed);                                  // file override removed
			Assert.Equal("WEAVIE_T_STR", result.ShadowedByEnv);           // env still in play
			Assert.Equal("env-wins", store.Resolve("t.str").Value);       // env still wins -> effective unchanged
			Assert.Empty(changes);                                        // effective value unchanged -> no event
			Assert.DoesNotContain("t.str", File.ReadAllText(FilePath), StringComparison.Ordinal); // file cleaned
		}
	}

	[Fact]
	public void Clear_UnknownKey_Throws() {
		using var store = new SettingsStore(ScalarRegistry(), FilePath, enableWatcher: false);
		Assert.Throws<UnknownSettingException>(() => store.Clear("does.not.exist"));
	}

	[Fact]
	public void Clear_OnMalformedFile_Throws_NonDestructively() {
		const string broken = "t.str = = oops\n[unterminated\n";
		File.WriteAllText(FilePath, broken);
		using var store = new SettingsStore(ScalarRegistry(), FilePath, enableWatcher: false);

		Assert.True(store.IsMalformed);
		Assert.Throws<SettingsFileMalformedException>(() => store.Clear("t.str"));
		Assert.Equal(broken, File.ReadAllText(FilePath)); // file left intact
	}

	[Fact]
	public void Catalog_Json_IncludesValueSourceDefaultAndAllowedValues() {
		var registry = new SettingsRegistry();
		registry.Register(new SettingDefinition {
			Key = "theme",
			Kind = SettingKind.String,
			Description = "color theme",
			Aliases = ["colors"],
			AllowedValues = ["dark", "light"],
			Default = "dark",
			Apply = ApplyMode.Live,
		});
		File.WriteAllText(FilePath, "theme = \"light\"\n");
		using var store = new SettingsStore(registry, FilePath, enableWatcher: false);

		using var doc = JsonDocument.Parse(store.BuildCatalogJson());
		var entry = doc.RootElement.GetProperty("settings")[0];
		Assert.Equal("theme", entry.GetProperty("key").GetString());
		Assert.Equal("string", entry.GetProperty("type").GetString());
		Assert.Equal("light", entry.GetProperty("value").GetString());
		Assert.Equal("userFile", entry.GetProperty("source").GetString());
		Assert.Equal("dark", entry.GetProperty("default").GetString());
		Assert.Equal("live", entry.GetProperty("apply").GetString());
		Assert.Equal(new[] { "dark", "light" }, entry.GetProperty("allowedValues").EnumerateArray().Select(e => e.GetString()!).ToArray());
	}

	[Fact]
	public async Task Watcher_ExternalEdit_RaisesDiffedChange() {
		File.WriteAllText(FilePath, "t.num = 1\n");
		using var store = new SettingsStore(ScalarRegistry(), FilePath, enableWatcher: true);
		var signal = new TaskCompletionSource<SettingChange>(TaskCreationOptions.RunContinuationsAsynchronously);
		store.Subscribe("t.num", c => signal.TrySetResult(c));

		File.WriteAllText(FilePath, "t.num = 99\n"); // external edit

		var change = await WaitAsync(signal.Task, TimeSpan.FromSeconds(5));
		Assert.Equal(99L, change.NewValue);
		Assert.Equal(1L, change.OldValue);
	}

	[Fact]
	public async Task Watcher_DoesNotDoubleFire_OnSelfWrite() {
		using var store = new SettingsStore(ScalarRegistry(), FilePath, enableWatcher: true);
		int count = 0;
		store.Subscribe("t.str", _ => Interlocked.Increment(ref count));

		store.Set("t.str", Json("\"once\""));
		await Task.Delay(1200); // ample time past the 250ms debounce for a stray re-fire

		Assert.Equal(1, count);
	}

	[Fact]
	public void WorkspaceScope_Resolution_EnvBeatsWorkspaceBeatsUserBeatsDefault() {
		string root = WorkspaceRoot;
		Directory.CreateDirectory(Path.GetDirectoryName(WorkspaceFile(root))!);
		File.WriteAllText(FilePath, "t.wsstr = \"from-user\"\n");
		File.WriteAllText(WorkspaceFile(root), "t.wsstr = \"from-workspace\"\n");

		// Workspace file wins over user file.
		using (var store = new SettingsStore(ScopedRegistry(), FilePath, enableWatcher: false)) {
			store.RegisterWorkspace(root);
			Assert.Equal("from-workspace", store.Resolve("t.wsstr", root).Value);
			Assert.Equal(SettingSource.WorkspaceFile, store.Resolve("t.wsstr", root).Source);

			// A bare resolve (no workspace) skips the overlay and falls through to the user file.
			Assert.Equal("from-user", store.Resolve("t.wsstr").Value);
			Assert.Equal(SettingSource.UserFile, store.Resolve("t.wsstr").Source);
		}

		// Env wins over everything.
		using (EnvScope("WEAVIE_T_WSSTR", "from-env"))
		using (var store = new SettingsStore(ScopedRegistry(), FilePath, enableWatcher: false)) {
			store.RegisterWorkspace(root);
			Assert.Equal("from-env", store.Resolve("t.wsstr", root).Value);
			Assert.Equal(SettingSource.Environment, store.Resolve("t.wsstr", root).Source);
		}

		// No workspace value -> user file.
		File.WriteAllText(WorkspaceFile(root), "");
		using (var store = new SettingsStore(ScopedRegistry(), FilePath, enableWatcher: false)) {
			store.RegisterWorkspace(root);
			Assert.Equal("from-user", store.Resolve("t.wsstr", root).Value);
			Assert.Equal(SettingSource.UserFile, store.Resolve("t.wsstr", root).Source);
		}

		// No workspace, no user -> default.
		File.WriteAllText(FilePath, "");
		using (var store = new SettingsStore(ScopedRegistry(), FilePath, enableWatcher: false)) {
			store.RegisterWorkspace(root);
			Assert.Equal("ws-default", store.Resolve("t.wsstr", root).Value);
			Assert.Equal(SettingSource.Default, store.Resolve("t.wsstr", root).Source);
		}
	}

	[Fact]
	public void Set_WorkspaceScopedKey_WithRoot_WritesWorkspaceFile_NotUserFile() {
		string root = WorkspaceRoot;
		using var store = new SettingsStore(ScopedRegistry(), FilePath, enableWatcher: false);
		store.RegisterWorkspace(root);

		var result = store.Set("t.wsstr", Json("\"repo-value\""), root);

		Assert.True(result.Written);
		Assert.Equal("repo-value", store.Resolve("t.wsstr", root).Value);
		Assert.Equal(SettingSource.WorkspaceFile, store.Resolve("t.wsstr", root).Source);
		Assert.True(File.Exists(WorkspaceFile(root)));
		Assert.Contains("t.wsstr = \"repo-value\"", File.ReadAllText(WorkspaceFile(root)), StringComparison.Ordinal);
		// The user file is untouched.
		Assert.False(File.Exists(FilePath) && File.ReadAllText(FilePath).Contains("t.wsstr", StringComparison.Ordinal));
	}

	[Fact]
	public void Set_UserScopedKey_WithRoot_WritesUserFile_IgnoringWorkspace() {
		string root = WorkspaceRoot;
		using var store = new SettingsStore(ScopedRegistry(), FilePath, enableWatcher: false);
		store.RegisterWorkspace(root);

		store.Set("t.userstr", Json("\"u\""), root);

		Assert.Contains("t.userstr = \"u\"", File.ReadAllText(FilePath), StringComparison.Ordinal);
		Assert.False(File.Exists(WorkspaceFile(root)));
	}

	[Fact]
	public async Task Watcher_WorkspaceFileEdit_RaisesChange_CarryingRoot() {
		string root = WorkspaceRoot;
		Directory.CreateDirectory(Path.GetDirectoryName(WorkspaceFile(root))!);
		File.WriteAllText(WorkspaceFile(root), "t.wsstr = \"one\"\n");
		using var store = new SettingsStore(ScopedRegistry(), FilePath, enableWatcher: true);
		store.RegisterWorkspace(root);
		var signal = new TaskCompletionSource<SettingChange>(TaskCreationOptions.RunContinuationsAsynchronously);
		store.Subscribe("t.wsstr", c => signal.TrySetResult(c));

		File.WriteAllText(WorkspaceFile(root), "t.wsstr = \"two\"\n"); // external edit

		var change = await WaitAsync(signal.Task, TimeSpan.FromSeconds(5));
		Assert.Equal("two", change.NewValue);
		Assert.Equal("one", change.OldValue);
		Assert.Equal(root, change.WorkspaceRoot);
	}

	[Fact]
	public void Malformed_WorkspaceFile_FallsBackToUserThenDefault_AndReportsMalformed() {
		string root = WorkspaceRoot;
		Directory.CreateDirectory(Path.GetDirectoryName(WorkspaceFile(root))!);
		File.WriteAllText(WorkspaceFile(root), "t.wsstr = = broken\n[unterminated\n");
		File.WriteAllText(FilePath, "t.wsstr = \"from-user\"\n");
		using var store = new SettingsStore(ScopedRegistry(), FilePath, enableWatcher: false);
		store.RegisterWorkspace(root);

		Assert.True(store.IsMalformed);
		// The broken overlay is ignored; resolution falls through to the user file.
		Assert.Equal("from-user", store.Resolve("t.wsstr", root).Value);
		Assert.Equal(SettingSource.UserFile, store.Resolve("t.wsstr", root).Source);
		// Writing to the malformed overlay is refused non-destructively.
		Assert.Throws<SettingsFileMalformedException>(() => store.Set("t.wsstr", Json("\"x\""), root));
	}

	private static async Task<T> WaitAsync<T>(Task<T> task, TimeSpan timeout) {
		var completed = await Task.WhenAny(task, Task.Delay(timeout));
		if (completed != task) {
			throw new TimeoutException("Expected change event did not arrive.");
		}

		return await task;
	}

	private static EnvScopeHandle EnvScope(string name, string? value) => new(name, value);

	private sealed class EnvScopeHandle : IDisposable {
		private readonly string _name;
		private readonly string? _old;

		public EnvScopeHandle(string name, string? value) {
			_name = name;
			_old = Environment.GetEnvironmentVariable(name);
			Environment.SetEnvironmentVariable(name, value);
		}

		public void Dispose() => Environment.SetEnvironmentVariable(_name, _old);
	}
}

/// <summary>Serializes settings tests that mutate process-wide environment variables.</summary>
[CollectionDefinition("Settings", DisableParallelization = true)]
public sealed class SettingsCollection {
}
