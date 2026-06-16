using System.Text.Json;
using Weavie.Core.Configuration;
using Xunit;

namespace Weavie.Core.Tests;

/// <summary>
/// Exercises <see cref="SettingsStore"/> deterministically against a real on-disk temp file: the
/// resolution precedence, per-kind coercion, comment/unknown-key preservation, atomic + validated
/// writes, malformed-file policy, env-shadow reporting, and the debounced file-watch change hub.
/// Serialized via the <c>Settings</c> collection because several tests mutate process env vars.
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

	private static JsonElement Json(string raw) => JsonDocument.Parse(raw).RootElement.Clone();

	[Fact]
	public void Resolution_Precedence_EnvBeatsFileBeatsDefault() {
		File.WriteAllText(FilePath, "t.str = \"from-file\"\n");
		var registry = ScalarRegistry();

		// default (no env, no key in file)
		using (var store = new SettingsStore(registry, FilePath, enableWatcher: false)) {
			Assert.Equal("from-file", store.Resolve("t.str").Value);
			Assert.Equal(SettingSource.UserFile, store.Resolve("t.str").Source);
		}

		// env wins over file
		using (EnvScope("WEAVIE_T_STR", "from-env"))
		using (var store = new SettingsStore(registry, FilePath, enableWatcher: false)) {
			Assert.Equal("from-env", store.Resolve("t.str").Value);
			Assert.Equal(SettingSource.Environment, store.Resolve("t.str").Source);
		}

		// no file key, no env -> default
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
			Assert.Equal(0L, store.Resolve("t.num").Value); // falls back to default
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

		var text = File.ReadAllText(FilePath);
		Assert.Contains("# a string", text, StringComparison.Ordinal);
		var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
		var keyLine = Array.FindIndex(lines, l => l.StartsWith("t.str", StringComparison.Ordinal));
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

		var text = File.ReadAllText(FilePath);
		Assert.Contains("# my own note", text, StringComparison.Ordinal);       // user comment kept
		Assert.Contains("[plugins.acme-linter]", text, StringComparison.Ordinal); // unknown subtree kept
		Assert.Contains("severity = \"error\"", text, StringComparison.Ordinal);
		Assert.Contains("t.str = \"new\"", text, StringComparison.Ordinal);       // value updated in place
		Assert.DoesNotContain("# a string", text, StringComparison.Ordinal);     // never clobbers an existing line's comment
	}

	[Fact]
	public void Set_PathKind_WritesLiteralStringForWindowsPath() {
		var registry = new SettingsRegistry();
		registry.Register(new SettingDefinition { Key = "claude.path", Kind = SettingKind.Path, Description = "claude" });
		using var store = new SettingsStore(registry, FilePath, enableWatcher: false);

		store.Set("claude.path", Json("\"C:\\\\tools\\\\claude.exe\""));

		var text = File.ReadAllText(FilePath);
		Assert.Contains("'C:\\tools\\claude.exe'", text, StringComparison.Ordinal); // single-quoted literal, no escaping
		Assert.DoesNotContain("\\\\", text, StringComparison.Ordinal);
		// And it round-trips back to the same path through a fresh store.
		using var reopened = new SettingsStore(registry, FilePath, enableWatcher: false);
		Assert.Equal(@"C:\tools\claude.exe", reopened.Resolve("claude.path").Value);
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
		Assert.Equal("fallback", store.Resolve("t.str").Value);            // defaults
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
			Assert.Contains("t.str = \"file-value\"", File.ReadAllText(FilePath), StringComparison.Ordinal); // but file written
			Assert.Empty(changes);                                          // effective value didn't change -> no reaction
		}
	}

	[Fact]
	public void Catalog_Json_IncludesValueSourceDefaultAndAllowedValues() {
		var registry = new SettingsRegistry();
		registry.Register(new SettingDefinition {
			Key = "theme", Kind = SettingKind.String, Description = "color theme",
			Aliases = ["colors"], AllowedValues = ["dark", "light"], Default = "dark", Apply = ApplyMode.Live,
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

		File.WriteAllText(FilePath, "t.num = 99\n"); // external hand-edit

		var change = await WaitAsync(signal.Task, TimeSpan.FromSeconds(5));
		Assert.Equal(99L, change.NewValue);
		Assert.Equal(1L, change.OldValue);
	}

	[Fact]
	public async Task Watcher_DoesNotDoubleFire_OnSelfWrite() {
		using var store = new SettingsStore(ScalarRegistry(), FilePath, enableWatcher: true);
		var count = 0;
		store.Subscribe("t.str", _ => Interlocked.Increment(ref count));

		store.Set("t.str", Json("\"once\""));
		await Task.Delay(1200); // give the watcher (250ms debounce) ample time to (not) re-fire

		Assert.Equal(1, count);
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
