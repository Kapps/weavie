using System.Text;
using Weavie.Core.FileSystem;

namespace Weavie.Core.Spelling;

/// <summary>Owns dictionary file IO and the stricter path boundary for a project-scoped dictionary.</summary>
internal sealed class DictionaryStorage {
	private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
	private static readonly Action Noop = () => { };

	private readonly string? _trustedProjectRoot;

	private DictionaryStorage(string filePath, string? trustedProjectRoot) {
		FilePath = filePath;
		_trustedProjectRoot = trustedProjectRoot;
	}

	internal string FilePath { get; }

	internal static DictionaryStorage User(string filePath) {
		ArgumentException.ThrowIfNullOrEmpty(filePath);
		return new DictionaryStorage(Path.GetFullPath(filePath), trustedProjectRoot: null);
	}

	internal static DictionaryStorage Project(string workspaceRoot) {
		ArgumentException.ThrowIfNullOrEmpty(workspaceRoot);
		string root = Path.GetFullPath(workspaceRoot);
		return new DictionaryStorage(WeaviePaths.ProjectDictionaryFile(root), root);
	}

	internal HashSet<string> ReadWords() {
		ValidateProjectLocation(createDirectory: false);
		if (!File.Exists(FilePath)) {
			return NewWords();
		}

		string[] lines;
		try {
			lines = File.ReadAllLines(FilePath, Utf8NoBom);
		} catch (FileNotFoundException) {
			return NewWords();
		} catch (DirectoryNotFoundException) {
			return NewWords();
		}

		var words = NewWords();
		for (int index = 0; index < lines.Length; index++) {
			string line = lines[index].Trim();
			if (line.Length == 0 || line.StartsWith('#')) {
				continue;
			}

			if (!SpellWord.TryNormalize(line, out string normalized)) {
				throw new InvalidDataException($"Line {index + 1} is not a dictionary word.");
			}

			words.Add(normalized);
		}

		return words;
	}

	internal void WriteWords(IEnumerable<string> words) {
		string target = PrepareWriteTarget();
		WriteAtomically(target, words, _trustedProjectRoot is null ? Noop : VerifyProjectDestination);
	}

	internal string? WatchDirectory() {
		ValidateProjectLocation(createDirectory: false);
		string? directory = Path.GetDirectoryName(FilePath);
		return string.IsNullOrEmpty(directory) || !Directory.Exists(directory) ? null : directory;
	}

	private string PrepareWriteTarget() {
		if (_trustedProjectRoot is not null) {
			ValidateProjectLocation(createDirectory: true);
			return FilePath;
		}

		string target = ResolveUserTarget();
		string? directory = Path.GetDirectoryName(target);
		if (string.IsNullOrEmpty(directory)) {
			throw new IOException($"Spell dictionary '{FilePath}' has no parent directory.");
		}

		Directory.CreateDirectory(directory);
		return target;
	}

	private string ResolveUserTarget() {
		var link = new FileInfo(FilePath);
		if (link.LinkTarget is null) {
			return FilePath;
		}

		return link.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? FilePath;
	}

	private void VerifyProjectDestination() => ValidateProjectLocation(createDirectory: false);

	private void ValidateProjectLocation(bool createDirectory) {
		if (_trustedProjectRoot is null) {
			return;
		}

		if (!PathBoundary.Contains(_trustedProjectRoot, FilePath)) {
			throw new IOException($"Project spell dictionary '{FilePath}' escapes workspace '{_trustedProjectRoot}'.");
		}

		var root = new DirectoryInfo(_trustedProjectRoot);
		EnsureDirectory(root, "workspace root");

		string directoryPath = Path.GetDirectoryName(FilePath)
			?? throw new IOException($"Project spell dictionary '{FilePath}' has no parent directory.");
		var directory = new DirectoryInfo(directoryPath);
		ThrowIfLink(directory, "project dictionary directory");
		if (!directory.Exists) {
			if (File.Exists(directoryPath)) {
				throw new IOException($"Project dictionary directory '{directoryPath}' is not a directory.");
			}

			if (!createDirectory) {
				return;
			}

			Directory.CreateDirectory(directoryPath);
			directory.Refresh();
			EnsureDirectory(directory, "project dictionary directory");
		}

		var file = new FileInfo(FilePath);
		ThrowIfLink(file, "project dictionary file");
		var asDirectory = new DirectoryInfo(FilePath);
		ThrowIfLink(asDirectory, "project dictionary file");
		if (asDirectory.Exists) {
			throw new IOException($"Project spell dictionary '{FilePath}' is a directory.");
		}
	}

	private static void EnsureDirectory(DirectoryInfo directory, string description) {
		ThrowIfLink(directory, description);
		if (!directory.Exists) {
			throw new DirectoryNotFoundException($"Project {description} '{directory.FullName}' does not exist.");
		}
	}

	private static void ThrowIfLink(FileSystemInfo entry, string description) {
		if (entry.LinkTarget is not null || (entry.Exists && entry.Attributes.HasFlag(FileAttributes.ReparsePoint))) {
			throw new IOException($"The {description} '{entry.FullName}' must not be a symbolic link or reparse point.");
		}
	}

	private static void WriteAtomically(string filePath, IEnumerable<string> words, Action verifyDestination) {
		string directory = Path.GetDirectoryName(filePath)
			?? throw new IOException($"Spell dictionary '{filePath}' has no parent directory.");
		string temporary = Path.Combine(directory, $".{Path.GetFileName(filePath)}.{Guid.NewGuid():N}.tmp");
		try {
			using (var stream = new FileStream(
				temporary,
				FileMode.CreateNew,
				FileAccess.Write,
				FileShare.None,
				bufferSize: 4096,
				FileOptions.WriteThrough)) {
				using (var writer = new StreamWriter(stream, Utf8NoBom, bufferSize: 4096, leaveOpen: true)) {
					foreach (string word in words) {
						writer.WriteLine(word);
					}

					writer.Flush();
				}

				stream.Flush(flushToDisk: true);
			}

			verifyDestination();
			File.Move(temporary, filePath, overwrite: true);
		} finally {
			if (File.Exists(temporary)) {
				File.Delete(temporary);
			}
		}
	}

	private static HashSet<string> NewWords() => new(StringComparer.OrdinalIgnoreCase);
}
