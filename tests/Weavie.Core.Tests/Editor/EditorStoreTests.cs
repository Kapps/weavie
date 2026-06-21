using System.Text.Json;
using Weavie.Core.Editor;
using Xunit;

namespace Weavie.Core.Tests;

/// <summary>
/// Editor-state store and the <c>active-editor-changed</c> parsing that feeds it: file-URI → native-path
/// conversion, selection parsing, and the Changed notification.
/// </summary>
public sealed class EditorStoreTests {
	private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

	[Fact]
	public void TryParse_FileUri_ExtractsPathLanguageTextAndSelection() {
		var message = Parse(
			"""
			{"type":"active-editor-changed","uri":"file:///C:/work/app/Program.cs","languageId":"csharp",
			 "text":"var x = 1;","selection":{"start":{"line":3,"character":4},"end":{"line":3,"character":14},"isEmpty":false}}
			""");

		Assert.True(ActiveEditor.TryParse(message, out var editor));
		Assert.NotNull(editor);
		Assert.Equal(new Uri("file:///C:/work/app/Program.cs").LocalPath, editor!.FilePath);
		Assert.Equal("csharp", editor.LanguageId);
		Assert.Equal("var x = 1;", editor.SelectedText);
		Assert.Equal(new EditorPosition(3, 4), editor.Selection.Start);
		Assert.Equal(new EditorPosition(3, 14), editor.Selection.End);
		Assert.False(editor.Selection.IsEmpty);
	}

	[Fact]
	public void TryParse_CaretOnly_IsEmptySelection() {
		var message = Parse(
			"""
			{"uri":"file:///C:/work/a.ts","languageId":"typescript","text":"",
			 "selection":{"start":{"line":0,"character":0},"end":{"line":0,"character":0},"isEmpty":true}}
			""");

		Assert.True(ActiveEditor.TryParse(message, out var editor));
		Assert.Equal(string.Empty, editor!.SelectedText);
		Assert.True(editor.Selection.IsEmpty);
	}

	[Fact]
	public void TryParse_NonFileUri_ReturnsFalse() {
		var message = Parse("{\"uri\":\"inmemory://model/1\",\"languageId\":\"typescript\",\"text\":\"\"}");
		Assert.False(ActiveEditor.TryParse(message, out var editor));
		Assert.Null(editor);
	}

	[Fact]
	public void TryParse_MissingUri_ReturnsFalse() {
		var message = Parse("{\"languageId\":\"csharp\",\"text\":\"\"}");
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
