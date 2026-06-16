using Weavie.Core.FileSystem;

namespace Weavie.Core.Documents;

/// <summary>
/// Creates document models bound to a path and seeded with initial text. Injected so a
/// diff session can be opened without knowing whether the model is a Monaco proxy (prod)
/// or an in-memory buffer (T1 test).
/// </summary>
public interface IDocumentModelFactory {
	/// <summary>Creates a model bound to <paramref name="filePath"/> and seeded with <paramref name="initialText"/>.</summary>
	IDocumentModel Create(string filePath, string initialText);
}

/// <summary>Factory that produces <see cref="InMemoryDocumentModel"/> instances over a shared filesystem.</summary>
public sealed class InMemoryDocumentModelFactory : IDocumentModelFactory {
	private readonly IFileSystem _fileSystem;

	/// <summary>Creates a factory whose models persist through the given filesystem.</summary>
	public InMemoryDocumentModelFactory(IFileSystem fileSystem) {
		ArgumentNullException.ThrowIfNull(fileSystem);
		_fileSystem = fileSystem;
	}

	/// <inheritdoc/>
	public IDocumentModel Create(string filePath, string initialText) =>
		new InMemoryDocumentModel(filePath, initialText, _fileSystem);
}
