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
	private static readonly EnumerationOptions EveryEntry = new() {
		AttributesToSkip = FileAttributes.None,
		// As git does, list what can be read: an unreadable directory holds nothing this user can open.
		IgnoreInaccessible = true,
	};
	private readonly GitService _git = new();
	private bool _plain;

	/// <summary>Every path in the workspace.</summary>
	public async Task<IReadOnlyList<string>> ListAsync(CancellationToken ct) {
		var paths = await _git.ListWorkspacePathsAsync(root, ct).ConfigureAwait(false);
		Volatile.Write(ref _plain, paths is null);
		return paths ?? await WalkAsync(root, string.Empty, required: true, ct).ConfigureAwait(false);
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
			if (owned.Key.Length == 0) {
				continue;
			}

			(string[], string[])? scope = owned.Contains(owned.Key, PathIdentity.CanonicalComparer)
				? null
				: ([.. owned.Where(known.Contains)], [.. owned.Where(path => !known.Contains(path))]);
			if (PathIdentity.CanonicalComparer.Equals(owned.Key, root)
				|| await ListRepositoryAsync(owned.Key, scope, ct).ConfigureAwait(false) is not { } listed) {
				plain.AddRange(owned);
			} else {
				paths.AddRange(listed);
			}
		}

		// Each path is listed as its parent's walk would list it, so a scoped listing agrees with a full one.
		foreach (var siblings in plain.GroupBy(path => Path.GetDirectoryName(path)!, PathIdentity.CanonicalComparer)) {
			var entries = Entries(siblings.Key, required: false).ToDictionary(entry => entry.Name, PathIdentity.CanonicalComparer);
			foreach (string path in siblings) {
				if (!entries.TryGetValue(Path.GetFileName(path), out var entry)) {
					continue;
				}

				string listed = Path.Combine(siblings.Key, entry.Name);
				if (entry.IsDirectory) {
					paths.AddRange(await WalkAsync(listed, Relative(listed) + "/", required: false, ct).ConfigureAwait(false));
				} else {
					paths.Add(Relative(listed));
				}
			}
		}

		return paths;
	}

	// The folder that lists `path`: the first repository on the way down to it, the root for a path the plain
	// walk reaches, or nothing for a path at or beneath a directory that walk skips.
	private string Owner(string path) {
		string[] segments = Path.GetRelativePath(root, path).Split(Path.DirectorySeparatorChar);
		string directory = root;
		for (int index = 0; index < segments.Length; index++) {
			directory = Path.Combine(directory, segments[index]);
			if (WorkspacePaths.IsIgnoredSegment(segments[index]) && (index < segments.Length - 1 || Directory.Exists(directory))) {
				return string.Empty;
			}

			if (HoldsGit(directory)) {
				return directory;
			}
		}

		return root;
	}

	private static bool HoldsGit(string directory) => Path.Exists(Path.Combine(directory, GitDirectory));

	private string Relative(string path) =>
		Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');

	// A repository beneath the root, whole or only `scope` (its known and discovered paths): null when git does
	// not accept it as one, nothing when it was deleted since it was found, a failure naming it otherwise.
	private async Task<List<string>?> ListRepositoryAsync(string directory, (string[] Known, string[] Discovered)? scope, CancellationToken ct) {
		string prefix = Relative(directory) + "/";
		try {
			var listed = scope is { } part
				? await _git.ListWorkspacePathsAsync(directory, part.Known, part.Discovered, ct).ConfigureAwait(false)
				: await _git.ListWorkspacePathsAsync(directory, ct).ConfigureAwait(false);
			return listed is null ? null : [.. scope is null ? [prefix] : Array.Empty<string>(), .. listed.Select(path => prefix + path)];
		} catch (GitException) when (!Directory.Exists(directory)) {
			return [];
		} catch (GitException ex) {
			throw new GitException($"{directory}: {ex.Message}", ex);
		}
	}

	// Walks `start` as a plain folder; a directory beneath it holding `.git` is listed by git when git agrees.
	private async Task<List<string>> WalkAsync(string start, string prefix, bool required, CancellationToken ct) {
		var paths = new List<string>();
		var repositories = new List<(string Directory, string Prefix)>();
		var pending = new Stack<(string Directory, string Prefix)>();
		pending.Push((start, prefix));
		while (pending.TryPop(out var current)) {
			ct.ThrowIfCancellationRequested();
			bool marked = HoldsGit(current.Directory);
			if (marked && current.Directory != start) {
				repositories.Add(current);
				continue;
			}

			var entries = Entries(current.Directory, required && current.Directory == start);
			if (current.Prefix.Length > 0) {
				paths.Add(current.Prefix);
			}

			// A `.git` directory git does not accept is listed, so it is watched until it becomes a repository.
			if (marked && Directory.Exists(Path.Combine(current.Directory, GitDirectory))) {
				paths.Add(current.Prefix + GitDirectory + "/");
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
			listings[index] = await ListRepositoryAsync(directory, null, token).ConfigureAwait(false)
				?? await WalkAsync(directory, repositoryPrefix, required: false, token).ConfigureAwait(false);
		}).ConfigureAwait(false);
		foreach (var listing in listings) {
			paths.AddRange(listing);
		}

		return paths;
	}

	// A directory's files and real subdirectories. A directory link is neither, so no link cycle can be walked;
	// a directory deleted since its parent listed it is empty unless it is `required`.
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
