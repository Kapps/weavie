using System.Text;
using Weavie.Core.FileSystem;

namespace Weavie.Core.Spelling;

internal readonly record struct DictionaryWatchTarget(
	string DirectoryPath,
	string EntryPath);

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
		FileStream stream;
		try {
			stream = new FileStream(
				FilePath,
				FileMode.Open,
				FileAccess.Read,
				FileShare.ReadWrite | FileShare.Delete,
				bufferSize: 4096,
				FileOptions.SequentialScan);
		} catch (FileNotFoundException) {
			return NewWords();
		} catch (DirectoryNotFoundException) {
			return NewWords();
		}

		var words = NewWords();
		using (stream)
		using (var reader = new StreamReader(stream, Utf8NoBom, detectEncodingFromByteOrderMarks: true)) {
			int lineNumber = 0;
			while (reader.ReadLine() is { } rawLine) {
				lineNumber++;
				string line = rawLine.Trim();
				if (line.Length == 0 || line.StartsWith('#')) {
					continue;
				}

				if (!SpellWord.TryNormalize(line, out string normalized)) {
					throw new InvalidDataException($"Line {lineNumber} is not a dictionary word.");
				}

				words.Add(normalized);
			}
		}

		return words;
	}

	internal void WriteWords(IEnumerable<string> words) {
		string target = PrepareWriteTarget();
		WriteAtomically(target, words, _trustedProjectRoot is null ? Noop : VerifyProjectDestination);
	}

	internal IReadOnlyList<DictionaryWatchTarget> WatchTargets() {
		if (_trustedProjectRoot is not null) {
			return [ProjectWatchTarget(_trustedProjectRoot)];
		}

		var alias = RequireWatchTarget(FilePath);
		string resolved = ResolveUserTarget();
		if (PathsEqual(resolved, FilePath)) {
			return [alias];
		}

		var target = RequireWatchTarget(resolved);
		return SameTarget(alias, target) ? [alias] : [alias, target];
	}

	internal DictionaryWatchTarget RecoveryWatchTarget() {
		if (_trustedProjectRoot is null) {
			return RequireWatchTarget(FilePath);
		}

		var root = new DirectoryInfo(_trustedProjectRoot);
		if (IsPlainDirectory(root)) {
			string dictionaryDirectory = Path.GetDirectoryName(FilePath)!;
			return new DictionaryWatchTarget(root.FullName, dictionaryDirectory);
		}

		return RequireWatchTarget(_trustedProjectRoot);
	}

	internal static DictionaryWatchTarget ParentWatchTarget(string directoryPath) =>
		RequireWatchTarget(directoryPath);

	private DictionaryWatchTarget ProjectWatchTarget(string projectRoot) {
		var root = new DirectoryInfo(projectRoot);
		if (!IsPlainDirectory(root)) {
			return RequireWatchTarget(projectRoot);
		}

		string dictionaryDirectory = Path.GetDirectoryName(FilePath)!;
		var directory = new DirectoryInfo(dictionaryDirectory);
		return IsPlainDirectory(directory)
			? new DictionaryWatchTarget(dictionaryDirectory, FilePath)
			: new DictionaryWatchTarget(projectRoot, dictionaryDirectory);
	}

	private static DictionaryWatchTarget RequireWatchTarget(string path) {
		if (TryWatchTarget(path, out var target)) {
			return target;
		}

		throw new DirectoryNotFoundException($"Spell dictionary path '{path}' has no watchable ancestor.");
	}

	private static bool TryWatchTarget(string path, out DictionaryWatchTarget target) {
		string entry = Path.GetFullPath(path);
		string? directory = Path.GetDirectoryName(entry);
		while (directory is not null && !Directory.Exists(directory)) {
			entry = directory;
			directory = Path.GetDirectoryName(directory);
		}

		if (directory is null) {
			target = default;
			return false;
		}

		target = new DictionaryWatchTarget(directory, entry);
		return true;
	}

	private static bool IsPlainDirectory(DirectoryInfo directory) {
		if (!directory.Exists || directory.LinkTarget is not null) {
			return false;
		}

		return !directory.Attributes.HasFlag(FileAttributes.ReparsePoint);
	}

	private static bool SameTarget(DictionaryWatchTarget left, DictionaryWatchTarget right) =>
		PathsEqual(left.DirectoryPath, right.DirectoryPath)
		&& PathsEqual(left.EntryPath, right.EntryPath);

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

		return (link.ResolveLinkTarget(returnFinalTarget: true)
			?? link.ResolveLinkTarget(returnFinalTarget: false))?.FullName
			?? FilePath;
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

	internal static bool PathsEqual(string left, string right) =>
		string.Equals(
			Path.GetFullPath(left),
			Path.GetFullPath(right),
			OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

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
