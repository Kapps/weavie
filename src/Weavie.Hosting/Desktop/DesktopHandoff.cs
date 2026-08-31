namespace Weavie.Hosting.Desktop;

/// <summary>
/// The one policy every host applies to a handover, so a decline is decided the same way everywhere. A host
/// shows one workspace, so a handover is taken only when every path belongs to it; otherwise the caller is told
/// which workspace to boot and opens its own window.
/// </summary>
public static class DesktopHandoff {
	/// <summary>
	/// Decides <paramref name="paths"/> against <paramref name="workspaceRoot"/> (null when no workspace is
	/// open yet), resolving each through <paramref name="toplevel"/> and revealing accepted files with
	/// <paramref name="open"/>.
	/// </summary>
	public static HandoffReply Offer(
		IReadOnlyList<string> paths,
		string? workspaceRoot,
		Func<string, string?> toplevel,
		Action<string> open) {
		ArgumentNullException.ThrowIfNull(paths);
		ArgumentNullException.ThrowIfNull(toplevel);
		ArgumentNullException.ThrowIfNull(open);
		if (paths.Count == 0) {
			return new HandoffReply(true, string.Empty);
		}

		IReadOnlyList<string> roots = workspaceRoot is null ? [] : [workspaceRoot];
		List<OpenTarget> targets = [.. paths.Select(path => OpenTargetResolver.Resolve(path, roots, toplevel(path)))];
		if (targets.FirstOrDefault(target => !string.Equals(target.Root, workspaceRoot, StringComparison.Ordinal))
			is { Root.Length: > 0 } foreign) {
			return new HandoffReply(false, foreign.Root);
		}

		foreach (var target in targets) {
			if (target.File is { } file) {
				open(file);
			}
		}

		return new HandoffReply(true, string.Empty);
	}
}
