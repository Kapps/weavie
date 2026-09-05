using Weavie.Core.FileSystem;

namespace Weavie.Core.Spelling;

/// <summary>A live, plain-text custom dictionary, with one word per line.</summary>
public sealed class SpellDictionary : IDisposable {
	private readonly Lock _gate = new();
	private readonly ReloadingFile<HashSet<string>> _file;
	private readonly bool _confined;

	/// <summary>Loads a dictionary; project files are confined to their parent directory.</summary>
	public SpellDictionary(string path, bool confined, bool watch) {
		_confined = confined;
		_file = new(path, _gate, new(StringComparer.OrdinalIgnoreCase), Load, watch);
		_file.Reloaded += reload => {
			lock (_gate) {
				if (!reload.PreviousValue.SetEquals(reload.Value) || reload.PreviousError != reload.Error) {
					Changed?.Invoke();
				}
			}
		};
	}

	/// <summary>Raised when words or the dictionary's availability change.</summary>
	public event Action? Changed;

	/// <summary>Returns accepted words, or reports a read failure.</summary>
	public IReadOnlySet<string> Words {
		get {
			lock (_gate) {
				if (_file.Error is { } error) {
					throw new IOException($"Cannot read dictionary '{_file.Path}': {error.Message}", error);
				}
				return _file.Value;
			}
		}
	}

	/// <summary>Appends an accepted word and immediately reloads it.</summary>
	public void Add(string word) {
		if (!SpellChecker.IsWord(word)) {
			throw new ArgumentException("Choose a single word to add to the dictionary.", nameof(word));
		}
		word = SpellChecker.Normalize(word);
		lock (_gate) {
			var words = Load(_file.Path);
			if (!words.Contains(word)) {
				Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_file.Path)!);
				// Append preserves other processes' additions and a user-managed symlink.
				File.AppendAllText(_file.Path, $"\n{word}\n");
			}
		}
		_file.Reload();
	}

	/// <inheritdoc/>
	public void Dispose() {
		lock (_gate) {
			Changed = null;
		}
		_file.Dispose();
	}

	private HashSet<string> Load(string path) {
		if (_confined && !PhysicalPath.IsSameOrDescendant(path, System.IO.Path.GetDirectoryName(path)!)) {
			throw new IOException("The project dictionary must stay inside the project.");
		}
		string contents;
		try {
			contents = File.ReadAllText(path);
		} catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) {
			return new(StringComparer.OrdinalIgnoreCase);
		}
		var words = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (string line in contents.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)) {
			if (!SpellChecker.IsWord(line)) {
				throw new InvalidDataException($"Dictionary '{path}' must contain one word per line.");
			}
			words.Add(SpellChecker.Normalize(line));
		}
		return words;
	}
}
