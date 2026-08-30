using System.Text.Json;
using Weavie.Core.Editor;
using Weavie.Core.FileSystem;
using Xunit;

namespace Weavie.Core.Tests;

public sealed class EditorSessionSerializationTests {
	[Fact]
	public void BuildRestoreJson_ListsExistingFilesWithoutContent() {
		var fs = new InMemoryFileSystem();
		fs.WriteAllText("/root/file.ts", "export const x = 1;\n");
		using var viewState = JsonDocument.Parse("""{"scrollTop":42}""");
		var session = new EditorSession {
			Active = "/root/file.ts",
			Open = [new EditorSessionEntry {
				Path = "/root/file.ts",
				ViewState = viewState.RootElement.Clone(),
			}],
		};

		using var message = JsonDocument.Parse(
			EditorSessionSerialization.BuildRestoreJson(session, fs, _ => { }));
		var restored = message.RootElement.GetProperty("session");
		var entry = Assert.Single(restored.GetProperty("open").EnumerateArray());

		Assert.Equal("/root/file.ts", restored.GetProperty("active").GetString());
		Assert.Equal(42, entry.GetProperty("viewState").GetProperty("scrollTop").GetInt32());
		Assert.False(entry.TryGetProperty("content", out _));
	}

	[Fact]
	public void BuildRestoreJson_DropsMissingFilesAndKeepsOnesOutsideTheWorktree() {
		var fs = new InMemoryFileSystem();
		fs.WriteAllText("/root/in.ts", "x");
		fs.WriteAllText("/elsewhere/foreign.ts", "y");
		fs.WriteAllText("/scratch/untitled-1", "z");
		var session = new EditorSession {
			Active = "/elsewhere/foreign.ts",
			Open = [
				new EditorSessionEntry { Path = "/root/in.ts" },
				new EditorSessionEntry { Path = "/root/missing.ts" },
				new EditorSessionEntry { Path = "/elsewhere/foreign.ts" },
				new EditorSessionEntry { Path = "/scratch/untitled-1", Scratch = true },
			],
		};

		using var message = JsonDocument.Parse(
			EditorSessionSerialization.BuildRestoreJson(session, fs, _ => { }));
		var restored = message.RootElement.GetProperty("session");
		var open = restored.GetProperty("open").EnumerateArray()
			.Select(entry => entry.GetProperty("path").GetString()).ToList();

		Assert.Equal(["/root/in.ts", "/elsewhere/foreign.ts", "/scratch/untitled-1"], open);
		Assert.Equal("/elsewhere/foreign.ts", restored.GetProperty("active").GetString());
	}
}
