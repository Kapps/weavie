using System.IO.Enumeration;
using Weavie.Core.FileSystem;
using Weavie.Core.Git;

namespace Weavie.Core.Workspaces;

/// <summary>
/// Lists what one workspace root contains, as root-relative paths whose directories end in a slash. Git answers
/// for a repository. A plain folder is walked without following directory links, and each repository beneath it
/// is listed by its own git, so a child repository's ignore rules decide its files.
/// </summary>
internal sealed class WorkspaceListing(string root) {
	private const string GitDirectory = ".git";
	private static readonly EnumerationOptions EveryEntry = new() { AttributesToSkip = FileAttributes.None };
	private readonly GitService _git = new();
	private bool _plain;

	/// <summary>Every path in the workspace.</summary>
	public async Task<IReadOnlyList<string>> ListAsync(CancellationToken ct) {
		var paths = await _git.ListWorkspacePathsAsync(root, ct).ConfigureAwait(false);
		Volatile.Write(ref _plain, paths is null);
		return paths ?? await WalkAsync(root, string.Empty, ct).ConfigureAwait(false);
	}

	/// <summary>
	/// The paths at or beneath <paramref name="known"/> and <paramref name="discovered"/> (absolute, inside the
	/// root), or <c>null</c> when the root stopped being a repository since it was last listed in full.
	/// </summary>
	public async Task<IReadOnlyList<string>?> ListAsync(
		IReadOnlyCollection<string> known,
		IReadOnlyCollection<string> discovered,
		CancellationToken ct) {
		if (!Volatile.Read(ref _plain)) {
			return await _git.ListWorkspacePathsAsync(root, known, discovered, ct).ConfigureAwait(false);
		}

		var paths = new List<string>();
		var plain = new List<string>();
		foreach (var owned in known.Concat(discovered).GroupBy(Owner, PathIdentity.CanonicalComparer)) {
			if (PathIdentity.CanonicalComparer.Equals(owned.Key, root)) {
				plain.AddRange(owned);
			} else if (owned.Key.Length > 0) {
				var listed = owned.Contains(owned.Key, PathIdentity.CanonicalComparer)
					? await _git.ListWorkspacePathsAsync(owned.Key, ct).ConfigureAwait(false)
					: await _git.ListWorkspacePathsAsync(
						owned.Key, [.. owned.Where(known.Contains)], [.. owned.Where(path => !known.Contains(path))], ct).ConfigureAwait(false);
				if (listed is null) {
					plain.AddRange(owned);
				} else {
					paths.AddRange(Beneath(Relative(owned.Key) + "/", listed));
				}
			}
		}

		// Each path is listed as its parent's walk would list it, so a scoped listing agrees with a full one.
		foreach (var siblings in plain.GroupBy(path => Path.GetDirectoryName(path)!, PathIdentity.CanonicalComparer)) {
			var entries = Entries(siblings.Key, required: false).ToDictionary(entry => entry.Name, entry => entry.IsDirectory);
			foreach (string path in siblings) {
				if (!entries.TryGetValue(Path.GetFileName(path), out bool isDirectory)) {
					continue;
				}

				if (isDirectory) {
					paths.AddRange(await WalkAsync(path, Relative(path) + "/", ct).ConfigureAwait(false));
				} else {
					paths.Add(Relative(path));
				}
			}
		}

		return paths;
	}

	// The folder that lists `path`: the first repository on the way down to it, the root for a path the plain
	// walk reaches, or nothing for a path beneath a directory that walk skips.
	private string Owner(string path) {
		string directory = root;
		foreach (string segment in Path.GetRelativePath(root, path).Split(Path.DirectorySeparatorChar)) {
			if (WorkspacePaths.IsIgnoredSegment(segment)) {
				return string.Empty;
			}

			directory = Path.Combine(directory, segment);
			if (Path.Exists(Path.Combine(directory, GitDirectory))) {
				return directory;
			}
		}

		return root;
	}

	private string Relative(string path) =>
		Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');

	private static IEnumerable<string> Beneath(string prefix, IReadOnlyList<string> paths) =>
		paths.Select(path => prefix + path).Prepend(prefix);

	// Walks `start` as a plain folder; a directory beneath it holding `.git` is listed by git when git agrees.
	private async Task<List<string>> WalkAsync(string start, string prefix, CancellationToken ct) {
		var paths = new List<string>();
		var repositories = new List<(string Directory, string Prefix)>();
		var pending = new Stack<(string Directory, string Prefix)>();
		pending.Push((start, prefix));
		while (pending.TryPop(out var current)) {
			ct.ThrowIfCancellationRequested();
			var entries = Entries(current.Directory, current.Directory == start);
			if (current.Directory != start && entries.Any(entry => entry.Name == GitDirectory)) {
				repositories.Add(current);
				continue;
			}

			if (current.Prefix.Length > 0) {
				paths.Add(current.Prefix);
			}

			foreach (var (name, isDirectory) in entries) {
				if (!isDirectory) {
					paths.Add(current.Prefix + name);
				} else if (!WorkspacePaths.IsIgnoredSegment(name)) {
					pending.Push((Path.Combine(current.Directory, name), current.Prefix + name + "/"));
				}
			}
		}

		var listings = new List<string>[repositories.Count];
		await Parallel.ForAsync(0, repositories.Count, ct, async (index, token) => {
			var (directory, repositoryPrefix) = repositories[index];
			listings[index] = await _git.ListWorkspacePathsAsync(directory, token).ConfigureAwait(false) is { } listed
				? [.. Beneath(repositoryPrefix, listed)]
				: await WalkAsync(directory, repositoryPrefix, token).ConfigureAwait(false);
		}).ConfigureAwait(false);
		foreach (var listing in listings) {
			paths.AddRange(listing);
		}

		return paths;
	}

	// A directory's files and real subdirectories. A directory link is neither, so no link cycle can be walked;
	// a directory deleted since its parent listed it is empty, unless the walk starts there.
	private static List<(string Name, bool IsDirectory)> Entries(string directory, bool required) {
		try {
			return [.. new FileSystemEnumerable<(string, bool)>(
				directory,
				(ref FileSystemEntry entry) => (entry.FileName.ToString(), entry.IsDirectory),
				EveryEntry) {
				ShouldIncludePredicate = (ref FileSystemEntry entry) => !entry.IsDirectory
					|| !entry.Attributes.HasFlag(FileAttributes.ReparsePoint)
					|| entry.ToFileSystemInfo().LinkTarget is null,
			}];
		} catch (DirectoryNotFoundException) when (!required) {
			return [];
		}
	}
}
