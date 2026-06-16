namespace Weavie.Core.Documents;

/// <summary>
/// The document-model seam. Tiny by design: apply a structured edit, read
/// text/range, get/set selection, save. The production implementation will proxy
/// a Monaco <c>TextModel</c> over the webview bridge; the T1 test implementation
/// (<see cref="InMemoryDocumentModel"/>) is a naive in-memory buffer.
/// One model per file (Editor &amp; Shared Models: one TextModel, no parallel buffer).
/// </summary>
public interface IDocumentModel {
	/// <summary>Absolute path this model is bound to; <see cref="Save"/> writes here.</summary>
	string FilePath { get; }

	/// <summary>Whether the buffer has unsaved changes relative to the last save/load.</summary>
	bool IsDirty { get; }

	/// <summary>Returns the full text of the document.</summary>
	string GetText();

	/// <summary>Returns the text covered by <paramref name="range"/>.</summary>
	string GetText(TextRange range);

	/// <summary>Applies a single structured edit to the buffer.</summary>
	void ApplyEdit(TextEdit edit);

	/// <summary>Applies a batch of structured edits to the buffer.</summary>
	void ApplyEdits(IReadOnlyList<TextEdit> edits);

	/// <summary>The current selection/caret range within the document.</summary>
	TextRange Selection { get; set; }

	/// <summary>Persists the current text to <see cref="FilePath"/> through the filesystem seam.</summary>
	void Save();
}
