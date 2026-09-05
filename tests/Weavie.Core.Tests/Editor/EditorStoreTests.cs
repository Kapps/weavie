using System.Text.Json;
using Weavie.Core.Editor;
using Xunit;

namespace Weavie.Core.Tests;

/// <summary>
/// Editor-state store and the <c>activeChanged</c> parsing that feeds it: native paths, selection parsing,
/// and the Changed notification.
/// </summary>
public sealed class EditorStoreTests {
	private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;
	private static string AbsolutePath(string name) => Path.GetFullPath(Path.Combine(Path.GetTempPath(), name));

	[Fact]
	public void TryParse_NativePath_PreservesPathLanguageTextAndSelection() {
		string path = AbsolutePath("Program.cs");
		var message = JsonSerializer.SerializeToElement(new {
			path,
			languageId = "csharp",
			text = "var x = 1;",
			selection = new {
				start = new { line = 3, character = 4 },
				end = new { line = 3, character = 14 },
				isEmpty = false,
			},
		});

		Assert.True(ActiveEditor.TryParse(message, out var editor));
		Assert.NotNull(editor);
		Assert.Equal(path, editor!.FilePath);
		Assert.Equal("csharp", editor.LanguageId);
		Assert.Equal("var x = 1;", editor.SelectedText);
		Assert.Equal(new EditorPosition(3, 4), editor.Selection.Start);
		Assert.Equal(new EditorPosition(3, 14), editor.Selection.End);
		Assert.False(editor.Selection.IsEmpty);
	}

	[Fact]
	public void TryParse_CaretOnly_IsEmptySelection() {
		var message = JsonSerializer.SerializeToElement(new {
			path = AbsolutePath("a.ts"),
			languageId = "typescript",
			text = "",
			selection = new {
				start = new { line = 0, character = 0 },
				end = new { line = 0, character = 0 },
				isEmpty = true,
			},
		});

		Assert.True(ActiveEditor.TryParse(message, out var editor));
		Assert.Equal(string.Empty, editor!.SelectedText);
		Assert.True(editor.Selection.IsEmpty);
	}

	[Fact]
	public void TryParse_NoIsEmptyFlag_InfersFromRange() {
		string path = AbsolutePath("a.cs");
		var caret = JsonSerializer.SerializeToElement(new {
			path,
			selection = new {
				start = new { line = 2, character = 1 },
				end = new { line = 2, character = 1 },
			},
		});
		var range = JsonSerializer.SerializeToElement(new {
			path,
			selection = new {
				start = new { line = 2, character = 1 },
				end = new { line = 2, character = 5 },
			},
		});

		Assert.True(ActiveEditor.TryParse(caret, out var caretEditor));
		Assert.True(caretEditor!.Selection.IsEmpty);

		Assert.True(ActiveEditor.TryParse(range, out var rangeEditor));
		Assert.False(rangeEditor!.Selection.IsEmpty);
	}

	[Fact]
	public void ParseList_ReadsFlags_AndSkipsEntriesWithoutPath() {
		var message = Parse(
			"""
			{"editors":[
			 {"path":"/work/a.cs","isActive":true,"isPinned":true,"isPreview":false},
			 {"path":"","isActive":true},
			 {"isActive":true}]}
			""");

		var tabs = OpenEditorTab.ParseList(message);

		var only = Assert.Single(tabs);
		Assert.Equal("/work/a.cs", only.FilePath);
		Assert.True(only.IsActive);
		Assert.True(only.IsPinned);
		Assert.False(only.IsPreview);
	}

	[Fact]
	public void TryParse_RelativePath_ReturnsFalse() {
		var message = Parse("{\"path\":\"src/a.ts\",\"languageId\":\"typescript\",\"text\":\"\"}");
		Assert.False(ActiveEditor.TryParse(message, out var editor));
		Assert.Null(editor);
	}

	[Fact]
	public void TryParse_UriWithoutPath_ReturnsFalse() {
		var message = Parse("{\"uri\":\"file:///C:/work/a.ts\",\"languageId\":\"csharp\",\"text\":\"\"}");
		Assert.False(ActiveEditor.TryParse(message, out var editor));
		Assert.Null(editor);
	}

	[Fact]
	public void TryParse_EmptyPath_ReturnsFalse() {
		var message = Parse("{\"path\":\"\",\"languageId\":\"csharp\",\"text\":\"\"}");
		Assert.False(ActiveEditor.TryParse(message, out var editor));
		Assert.Null(editor);
	}

	[Fact]
	public void SetActive_UpdatesCurrentAndRaisesChanged() {
		var store = new EditorStore();
		Assert.Null(store.Active);

		ActiveEditor? observed = null;
		store.Changed += e => observed = e;

		var editor = new ActiveEditor("/work/a.cs", "csharp", "hi", new EditorSelection(default, default, IsEmpty: true));
		store.SetActive(editor);

		Assert.Same(editor, store.Active);
		Assert.Same(editor, observed);
	}

	[Fact]
	public void Clear_DropsActiveAndOpenEditors() {
		var store = new EditorStore();
		store.SetActive(new ActiveEditor("/work/a.cs", "csharp", "", new EditorSelection(default, default, IsEmpty: true)));
		store.SetOpenEditors([new OpenEditorTab("/work/a.cs", IsActive: true, IsPinned: false, IsPreview: false)]);

		store.Clear();

		// A backgrounded session reports "nothing open" so its Claude isn't told the user is looking at a file
		// they have switched away from — getCurrentSelection / getOpenEditors both read empty after Clear.
		Assert.Null(store.Active);
		Assert.Empty(store.OpenEditors);
	}

	[Fact]
	public void Clear_DoesNotRaiseChanged() {
		var store = new EditorStore();
		store.SetActive(new ActiveEditor("/work/a.cs", "csharp", "", new EditorSelection(default, default, IsEmpty: true)));

		bool raised = false;
		store.Changed += _ => raised = true;
		store.Clear();

		// Clear must not push a selection_changed to a backgrounded session's Claude.
		Assert.False(raised);
	}
}
