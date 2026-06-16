using Weavie.Core.FileSystem;

namespace Weavie.Core.Documents;

/// <summary>
/// T1 test implementation of <see cref="IDocumentModel"/>: a naive in-memory buffer
/// over <see cref="TextBuffer"/>, persisting through an injected <see cref="IFileSystem"/>.
/// A substitute for Monaco, never run alongside it (so there is no sync to get wrong).
/// </summary>
public sealed class InMemoryDocumentModel : IDocumentModel {
	private readonly IFileSystem _fileSystem;
	private readonly TextBuffer _buffer;
	private string _savedText;

	/// <summary>Creates a model bound to <paramref name="filePath"/>, seeded with <paramref name="initialText"/> as its saved baseline.</summary>
	public InMemoryDocumentModel(string filePath, string initialText, IFileSystem fileSystem) {
		ArgumentException.ThrowIfNullOrEmpty(filePath);
		ArgumentNullException.ThrowIfNull(initialText);
		ArgumentNullException.ThrowIfNull(fileSystem);

		FilePath = filePath;
		_fileSystem = fileSystem;
		_buffer = new TextBuffer(initialText);
		_savedText = _buffer.Text;
		Selection = TextRange.Collapsed(Position.Start);
	}

	/// <summary>Loads an existing file from the filesystem into a fresh model.</summary>
	public static InMemoryDocumentModel OpenFromDisk(string filePath, IFileSystem fileSystem) {
		ArgumentNullException.ThrowIfNull(fileSystem);
		var initialText = fileSystem.FileExists(filePath) ? fileSystem.ReadAllText(filePath) : string.Empty;
		return new InMemoryDocumentModel(filePath, initialText, fileSystem);
	}

	/// <inheritdoc/>
	public string FilePath { get; }

	/// <inheritdoc/>
	public bool IsDirty => !string.Equals(_buffer.Text, _savedText, StringComparison.Ordinal);

	/// <inheritdoc/>
	public TextRange Selection { get; set; }

	/// <inheritdoc/>
	public string GetText() => _buffer.Text;

	/// <inheritdoc/>
	public string GetText(TextRange range) => _buffer.GetText(range);

	/// <inheritdoc/>
	public void ApplyEdit(TextEdit edit) => _buffer.Apply(edit);

	/// <inheritdoc/>
	public void ApplyEdits(IReadOnlyList<TextEdit> edits) => _buffer.Apply(edits);

	/// <inheritdoc/>
	public void Save() {
		_fileSystem.WriteAllText(FilePath, _buffer.Text);
		_savedText = _buffer.Text;
	}
}
