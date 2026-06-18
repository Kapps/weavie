using System.Text.Json;
using Weavie.Core.Commands;
using Xunit;

namespace Weavie.Core.Tests;

/// <summary>
/// Exercises <see cref="KeybindingStore"/> against a real on-disk temp file: default seeding from the
/// registry, user add / override / unbind, unknown-command dropping, args + when parsing, and the
/// malformed-file policy (keep defaults). Watcher disabled for deterministic, synchronous assertions.
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

	// A small deterministic registry: one keybinding-only command with a default + args, and one with none.
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

	[Fact]
	public void BuildCommandsJson_IncludesKeysPerCommand() {
		using var store = new KeybindingStore(TestRegistry(), FilePath, enableWatcher: false);
		var commands = Parse(store.BuildCommandsJson());
		var focus = commands.Single(c => c.GetProperty("id").GetString() == "weavie.pane.focusByIndex");
		var keys = focus.GetProperty("keys").EnumerateArray().Select(k => k.GetString()).ToList();
		Assert.Equal(["$mod+1", "$mod+2"], keys);
		Assert.Equal("web", focus.GetProperty("runsIn").GetString());
	}
}
