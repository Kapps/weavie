namespace Weavie.Core;

/// <summary>
/// The auto-updater's on-disk version layout — <c>&lt;root&gt;/versions/&lt;build&gt;/</c> bundles behind a
/// <c>current</c> symlink — reasoned about from both sides: the runner (<c>Weavie.Runner.VersionStore</c>)
/// maintains it, and the worker resolves its version-independent hook-relay path from it. Sole owner of the
/// layout-root detection so neither side reimplements the walk. See docs/specs/runner-auto-update.md.
/// </summary>
public static class ManagedRunnerLayout {
	/// <summary>The layout root when <paramref name="dir"/> sits inside a <c>versions/&lt;build&gt;/</c> tree, else null.</summary>
	public static string? RootContaining(string dir) {
		ArgumentException.ThrowIfNullOrEmpty(dir);
		var info = new DirectoryInfo(Path.TrimEndingDirectorySeparator(Path.GetFullPath(dir)));
		for (var d = info; d?.Parent is { } parent; d = parent) {
			if (parent.Name == "versions" && int.TryParse(d.Name, out _)) {
				return parent.Parent?.FullName;
			}
		}

		return null;
	}

	/// <summary>
	/// The hook-relay path to bake into a worker's Claude <c>--settings</c>, resolved through the <c>current</c>
	/// symlink (<c>&lt;root&gt;/current/worker/&lt;relayFileName&gt;</c>) so it keeps resolving after the worker's
	/// own version dir is pruned — or null when <paramref name="workerBaseDir"/> isn't in a managed layout (a
	/// dev/local host, where the relay beside the app is already stable). Unlike the worker executable and web
	/// assets (pinned to a resolved version so they never swap mid-flight), the relay is version-independent —
	/// it forwards over a named pipe whose name arrives per hook — so riding <c>current</c> is safe.
	/// See docs/specs/runner-auto-update.md §Recover.
	/// </summary>
	public static string? CurrentRelayPath(string workerBaseDir, string relayFileName) {
		ArgumentException.ThrowIfNullOrEmpty(workerBaseDir);
		ArgumentException.ThrowIfNullOrEmpty(relayFileName);
		return RootContaining(workerBaseDir) is { } root
			? Path.Combine(root, "current", "worker", relayFileName)
			: null;
	}
}
