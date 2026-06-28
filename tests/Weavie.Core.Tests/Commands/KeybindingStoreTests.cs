using System.Text.Json;
using Weavie.Core.Commands;
using Xunit;

namespace Weavie.Core.Tests;

/// <summary>
/// <see cref="KeybindingStore"/> against a real temp file: default seeding, user add/override/unbind,
/// unknown-command dropping, args + when parsing, and malformed-file handling (defaults on cold load,
/// last-good on a live edit). Watcher off for deterministic, synchronous assertions except the one
/// watcher-driven test that exercises the live malformed→last-good→recover transition.
/// </summary>
public sealed class KeybindingStoreTests : IDisposable {
	private readonly string _dir = Path.Combine(Path.GetTempPath(), "weavie-keybinding-tests", Guid.NewGuid().ToString("N"));

	public KeybindingStoreTests() {
		Directory.CreateDirectory(_dir);
	}

	public void Dispose() {
		try {
			Directory.Delete(_dir, recursive: true);
		} catch (IOException) {
		} catch (UnauthorizedAccessException) {
		}
	}

	private string FilePath => Path.Combine(_dir, "keybindings.json");

	// One command with default bindings + args, one with none.
	private static CommandRegistry TestRegistry() {
		var registry = new CommandRegistry();
		registry.Register(new CommandDefinition {
			Id = "weavie.pane.focusByIndex",
			Title = "Focus Pane",
			RunsIn = CommandLocation.Web,
			When = "paneFocused",
			DefaultKeybindings = [
				new CommandKeybinding { Key = "$mod+1", ArgsJson = "{\"index\":1}" },
				new CommandKeybinding { Key = "$mod+2", ArgsJson = "{\"index\":2}" },
			],
		});
		registry.Register(new CommandDefinition {
			Id = "weavie.terminal.reopen",
			Title = "Reopen Terminal",
			RunsIn = CommandLocation.Core,
		});
		return registry;
	}

	private static IReadOnlyList<JsonElement> Parse(string json) =>
		[.. JsonDocument.Parse(json).RootElement.EnumerateArray()];

	[Fact]
	public void Defaults_SeededFromRegistry() {
		using var store = new KeybindingStore(TestRegistry(), FilePath, enableWatcher: false);
		var resolved = store.Resolved;
		Assert.Equal(2, resolved.Count);
		Assert.All(resolved, b => Assert.Equal("weavie.pane.focusByIndex", b.Command));
		Assert.Equal("$mod+1", resolved[0].Key);
		Assert.Equal("{\"index\":1}", resolved[0].ArgsJson);
		Assert.Equal("paneFocused", resolved[0].When); // default binding inherits the command's when
	}

	[Fact]
	public void PerBindingWhen_OverridesCommandWhen_AndFallsBackWhenNull() {
		var registry = new CommandRegistry();
		registry.Register(new CommandDefinition {
			Id = "weavie.test.guarded",
			Title = "Guarded",
			RunsIn = CommandLocation.Web,
			When = "commandLevel",
			DefaultKeybindings = [
				new CommandKeybinding { Key = "$mod+1", When = "terminalFocused" }, // per-binding overrides
				new CommandKeybinding { Key = "$mod+2" }, // null → inherits the command-level guard
			],
		});

		using var store = new KeybindingStore(registry, FilePath, enableWatcher: false);
		Assert.Equal("terminalFocused", store.Resolved.Single(b => b.Key == "$mod+1").When);
		Assert.Equal("commandLevel", store.Resolved.Single(b => b.Key == "$mod+2").When);
	}

	[Fact]
	public void UserEntry_Adds_Binding() {
		File.WriteAllText(FilePath, """[{"key":"$mod+shift+t","command":"weavie.terminal.reopen"}]""");
		using var store = new KeybindingStore(TestRegistry(), FilePath, enableWatcher: false);
		Assert.Contains(store.Resolved, b => b is { Key: "$mod+shift+t", Command: "weavie.terminal.reopen" });
		Assert.Equal(3, store.Resolved.Count); // 2 defaults + 1 user
	}

	[Fact]
	public void UserEntry_Unbind_RemovesDefault() {
		File.WriteAllText(FilePath, """[{"key":"$mod+1","command":"-weavie.pane.focusByIndex"}]""");
		using var store = new KeybindingStore(TestRegistry(), FilePath, enableWatcher: false);
		Assert.DoesNotContain(store.Resolved, b => b.Key == "$mod+1");
		Assert.Contains(store.Resolved, b => b.Key == "$mod+2"); // the other default survives
	}

	[Fact]
	public void UserEntry_ParsesArgsAndWhen() {
		File.WriteAllText(FilePath,
			"""[{"key":"alt+1","command":"weavie.pane.focusByIndex","args":{"index":7},"when":"editorFocused"}]""");
		using var store = new KeybindingStore(TestRegistry(), FilePath, enableWatcher: false);
		var added = Assert.Single(store.Resolved, b => b.Key == "alt+1");
		Assert.Equal("{\"index\":7}", added.ArgsJson);
		Assert.Equal("editorFocused", added.When);
	}

	[Fact]
	public void UnknownCommand_Entry_IsDropped() {
		File.WriteAllText(FilePath, """[{"key":"$mod+k","command":"weavie.does.not.exist"}]""");
		using var store = new KeybindingStore(TestRegistry(), FilePath, enableWatcher: false);
		Assert.DoesNotContain(store.Resolved, b => b.Key == "$mod+k");
		Assert.Equal(2, store.Resolved.Count); // just the defaults
	}

	[Fact]
	public void MalformedFile_KeepsDefaults() {
		File.WriteAllText(FilePath, "{ this is not valid json ]");
		using var store = new KeybindingStore(TestRegistry(), FilePath, enableWatcher: false);
		Assert.Equal(2, store.Resolved.Count);
		Assert.All(store.Resolved, b => Assert.Equal("weavie.pane.focusByIndex", b.Command));
	}

	// #104: a parse error introduced *after* a good load must keep the last-good bindings (not revert to
	// defaults) and flag the file malformed; fixing the file clears the flag. Driven through the real watcher —
	// the only path that reaches the live reload — synchronized on MalformedChanged rather than a sleep.
	[Fact]
	public void MalformedAfterGoodLoad_KeepsLastGood_ThenRecovers() {
		File.WriteAllText(FilePath, """[{"key":"$mod+shift+t","command":"weavie.terminal.reopen"}]""");
		using var store = new KeybindingStore(TestRegistry(), FilePath, enableWatcher: true);
		Assert.Contains(store.Resolved, b => b is { Key: "$mod+shift+t", Command: "weavie.terminal.reopen" });

		using var flipped = new ManualResetEventSlim(false);
		store.MalformedChanged += malformed => {
			if (malformed) {
				flipped.Set();
			}
		};
		File.WriteAllText(FilePath, "{ not valid json ]");
		Assert.True(flipped.Wait(TimeSpan.FromSeconds(10)), "MalformedChanged(true) never fired");
		Assert.True(store.IsMalformed);
		Assert.Contains(store.Resolved, b => b.Key == "$mod+shift+t"); // last-good survived, not wiped to defaults

		using var recovered = new ManualResetEventSlim(false);
		store.MalformedChanged += malformed => {
			if (!malformed) {
				recovered.Set();
			}
		};
		File.WriteAllText(FilePath, """[{"key":"$mod+shift+t","command":"weavie.terminal.reopen"}]""");
		Assert.True(recovered.Wait(TimeSpan.FromSeconds(10)), "MalformedChanged(false) never fired on recovery");
		Assert.False(store.IsMalformed);
		Assert.Contains(store.Resolved, b => b.Key == "$mod+shift+t");
	}

	[Fact]
	public void BuildKeybindingsJson_EmitsArgsAndWhen() {
		File.WriteAllText(FilePath,
			"""[{"key":"alt+1","command":"weavie.pane.focusByIndex","args":{"index":7},"when":"editorFocused"}]""");
		using var store = new KeybindingStore(TestRegistry(), FilePath, enableWatcher: false);
		var entries = Parse(store.BuildKeybindingsJson());
		var added = entries.Single(e => e.GetProperty("key").GetString() == "alt+1");
		Assert.Equal(7, added.GetProperty("args").GetProperty("index").GetInt32());
		Assert.Equal("editorFocused", added.GetProperty("when").GetString());
	}

	// One command with a default global binding, for the global-flag tests.
	private static CommandRegistry GlobalRegistry() {
		var registry = new CommandRegistry();
		registry.Register(new CommandDefinition {
			Id = "weavie.window.toggle",
			Title = "Toggle Window",
			RunsIn = CommandLocation.Core,
			DefaultKeybindings = [new CommandKeybinding { Key = "ctrl+`", Global = true }],
		});
		return registry;
	}

	[Fact]
	public void Default_GlobalBinding_IsSeededGlobal() {
		using var store = new KeybindingStore(GlobalRegistry(), FilePath, enableWatcher: false);
		var binding = Assert.Single(store.Resolved);
		Assert.Equal("ctrl+`", binding.Key);
		Assert.True(binding.Global);
	}

	[Fact]
	public void NonGlobal_Default_IsNotGlobal() {
		using var store = new KeybindingStore(TestRegistry(), FilePath, enableWatcher: false);
		Assert.All(store.Resolved, b => Assert.False(b.Global));
	}

	[Fact]
	public void UserEntry_ParsesGlobalFlag() {
		File.WriteAllText(FilePath,
			"""[{"key":"ctrl+alt+w","command":"weavie.terminal.reopen","global":true}]""");
		using var store = new KeybindingStore(TestRegistry(), FilePath, enableWatcher: false);
		var added = Assert.Single(store.Resolved, b => b.Key == "ctrl+alt+w");
		Assert.True(added.Global);
	}

	[Fact]
	public void BuildKeybindingsJson_EmitsGlobalOnlyWhenSet() {
		using var store = new KeybindingStore(GlobalRegistry(), FilePath, enableWatcher: false);
		var entry = Assert.Single(Parse(store.BuildKeybindingsJson()));
		Assert.True(entry.GetProperty("global").GetBoolean());

		// Non-global bindings omit the property; absent means false on the web.
		using var plain = new KeybindingStore(TestRegistry(), FilePath, enableWatcher: false);
		Assert.All(Parse(plain.BuildKeybindingsJson()), e => Assert.False(e.TryGetProperty("global", out _)));
	}

	[Fact]
	public void BuildCommandsJson_IncludesKeysPerCommand() {
		using var store = new KeybindingStore(TestRegistry(), FilePath, enableWatcher: false);
		var commands = Parse(store.BuildCommandsJson());
		var focus = commands.Single(c => c.GetProperty("id").GetString() == "weavie.pane.focusByIndex");
		var keys = focus.GetProperty("keys").EnumerateArray().Select(k => k.GetString()).ToList();
		Assert.Equal(["$mod+1", "$mod+2"], keys);
		Assert.Equal("web", focus.GetProperty("runsIn").GetString());

		// A Core command reports runsIn "core" (not the web default) so listCommands routes it correctly.
		var reopen = commands.Single(c => c.GetProperty("id").GetString() == "weavie.terminal.reopen");
		Assert.Equal("core", reopen.GetProperty("runsIn").GetString());
		Assert.Empty(reopen.GetProperty("keys").EnumerateArray()); // no default bindings
	}
}
