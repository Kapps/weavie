using System.Collections.Concurrent;

namespace Weavie.Core.FileSystem;

/// <summary>
/// In-memory filesystem test fake. Paths are normalized to their full form so
/// "./a.txt" and an absolute path to the same logical file collide as expected.
/// The inspection surface for T1 tests: assert on saved content.
/// </summary>
public sealed class InMemoryFileSystem : IFileSystem {
	private readonly ConcurrentDictionary<string, string> _files = new(StringComparer.Ordinal);

	/// <summary>Creates an empty in-memory filesystem.</summary>
	public InMemoryFileSystem() {
	}

	/// <summary>Creates an in-memory filesystem pre-populated from <paramref name="seed"/> (path -&gt; contents).</summary>
	public InMemoryFileSystem(IEnumerable<KeyValuePair<string, string>> seed) {
		ArgumentNullException.ThrowIfNull(seed);
		foreach (var (path, contents) in seed) {
			_files[Normalize(path)] = contents;
		}
	}

	/// <inheritdoc/>
	public bool FileExists(string path) => _files.ContainsKey(Normalize(path));

	/// <inheritdoc/>
	public bool DirectoryExists(string path) {
		string prefix = WithTrailingSeparator(Normalize(path));
		return _files.Keys.Any(key => key.StartsWith(prefix, StringComparison.Ordinal));
	}

	/// <inheritdoc/>
	public IReadOnlyList<DirectoryEntry> EnumerateDirectory(string path) {
		string prefix = WithTrailingSeparator(Normalize(path));
		var subdirs = new HashSet<string>(StringComparer.Ordinal);
		var files = new HashSet<string>(StringComparer.Ordinal);
		foreach (string key in _files.Keys) {
			if (!key.StartsWith(prefix, StringComparison.Ordinal)) {
				continue;
			}

			string rest = key[prefix.Length..];
			int separator = rest.IndexOf(Path.DirectorySeparatorChar);
			if (separator < 0) {
				files.Add(rest);
			} else {
				subdirs.Add(rest[..separator]);
			}
		}

		var entries = new List<DirectoryEntry>(subdirs.Count + files.Count);
		entries.AddRange(subdirs.Select(name => new DirectoryEntry(name, true)));
		entries.AddRange(files.Where(name => !subdirs.Contains(name)).Select(name => new DirectoryEntry(name, false)));
		return entries;
	}

	private static string WithTrailingSeparator(string path) =>
		path.EndsWith(Path.DirectorySeparatorChar) ? path : path + Path.DirectorySeparatorChar;

	/// <inheritdoc/>
	public string ReadAllText(string path) {
		if (_files.TryGetValue(Normalize(path), out string? contents)) {
			return contents;
		}

		throw new FileNotFoundException($"No in-memory file at '{path}'.", path);
	}

	/// <inheritdoc/>
	public void WriteAllText(string path, string contents) {
		ArgumentNullException.ThrowIfNull(contents);
		_files[Normalize(path)] = contents;
	}

	/// <inheritdoc/>
	public void WriteAllTextAtomic(string path, string contents) {
		// A single dictionary assignment is atomic by construction, so the fake honors the same
		// all-or-nothing contract the real filesystem provides via temp-file + replace.
		ArgumentNullException.ThrowIfNull(contents);
		_files[Normalize(path)] = contents;
	}

	/// <summary>All file paths currently present (normalized), for test assertions.</summary>
	public IReadOnlyCollection<string> Paths => _files.Keys.ToArray();

	private static string Normalize(string path) {
		ArgumentException.ThrowIfNullOrEmpty(path);
		return Path.GetFullPath(path);
	}
}
