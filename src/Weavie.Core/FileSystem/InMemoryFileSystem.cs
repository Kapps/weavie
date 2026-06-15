using System.Collections.Concurrent;

namespace Weavie.Core.FileSystem;

/// <summary>
/// In-memory filesystem test fake. Paths are normalized to their full form so
/// "./a.txt" and an absolute path to the same logical file collide as expected.
/// The inspection surface for T1 tests: assert on saved content.
/// </summary>
public sealed class InMemoryFileSystem : IFileSystem
{
    private readonly ConcurrentDictionary<string, string> _files = new(StringComparer.Ordinal);

    public InMemoryFileSystem()
    {
    }

    public InMemoryFileSystem(IEnumerable<KeyValuePair<string, string>> seed)
    {
        ArgumentNullException.ThrowIfNull(seed);
        foreach (var (path, contents) in seed)
        {
            _files[Normalize(path)] = contents;
        }
    }

    public bool FileExists(string path) => _files.ContainsKey(Normalize(path));

    public string ReadAllText(string path)
    {
        if (_files.TryGetValue(Normalize(path), out var contents))
        {
            return contents;
        }

        throw new FileNotFoundException($"No in-memory file at '{path}'.", path);
    }

    public void WriteAllText(string path, string contents)
    {
        ArgumentNullException.ThrowIfNull(contents);
        _files[Normalize(path)] = contents;
    }

    /// <summary>All file paths currently present (normalized), for test assertions.</summary>
    public IReadOnlyCollection<string> Paths => _files.Keys.ToArray();

    private static string Normalize(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        return Path.GetFullPath(path);
    }
}
