using System.Text.Json;
using Weavie.Core.FileSystem;
using Weavie.Core.Json;

namespace Weavie.Hosting;

public sealed partial class HostCore {
	private async Task<ScratchSaveResult> SaveScratchAsAsync(
		HostSession session,
		JsonElement root,
		CancellationToken ct) {
		if (!TryResolveScratch(session, root, out string scratchPath, out string? error)) {
			return ScratchSaveResult.Failed(scratchPath, error!);
		}

		string suggested = root.TryGetProperty("suggestedName", out var nameElement)
			? nameElement.GetString() ?? "Untitled"
			: "Untitled";
		try {
			string sessionRoot = Path.GetFullPath(session.WorkspaceRoot);
			string? target = _platform.Dialogs is { } dialogs
				? await dialogs.PickSaveAsPathAsync(suggested, sessionRoot, ct).ConfigureAwait(false)
				: null;
			return string.IsNullOrEmpty(target)
				? ScratchSaveResult.Cancelled(scratchPath)
				: WriteScratch(session, scratchPath, target);
		} catch (OperationCanceledException) when (ct.IsCancellationRequested) {
			throw;
		} catch (Exception ex) {
			return ScratchSaveResult.Failed(scratchPath, $"Couldn't save the file: {ex.Message}");
		}
	}

	private ScratchSaveResult SaveScratchNamed(HostSession session, JsonElement root) {
		if (!TryResolveScratch(session, root, out string scratchPath, out string? error)) {
			return ScratchSaveResult.Failed(scratchPath, error!);
		}

		string name = root.GetStringOrEmpty("name").Trim();
		if (name.Length == 0) {
			return ScratchSaveResult.Cancelled(scratchPath);
		}

		string workspace = Path.GetFullPath(session.WorkspaceRoot);
		string target;
		try {
			target = Path.GetFullPath(Path.Combine(workspace, name));
		} catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) {
			return ScratchSaveResult.Failed(scratchPath, $"Invalid save path: {ex.Message}");
		}
		var comparison = OperatingSystem.IsWindows()
			? StringComparison.OrdinalIgnoreCase
			: StringComparison.Ordinal;
		if (!PathBoundary.Contains(workspace, target, comparison)
			|| string.Equals(workspace, target, comparison)) {
			return ScratchSaveResult.Failed(scratchPath, "The save name must identify a file inside the workspace.");
		}

		return WriteScratch(session, scratchPath, target);
	}

	private static bool TryResolveScratch(
		HostSession session,
		JsonElement root,
		out string scratchPath,
		out string? error) {
		scratchPath = root.GetStringOrEmpty("path");
		if (!session.Scratch.Owns(scratchPath)) {
			error = "The requested scratch file does not belong to this session.";
			return false;
		}

		var comparison = OperatingSystem.IsWindows()
			? StringComparison.OrdinalIgnoreCase
			: StringComparison.Ordinal;
		string resolvedPath = scratchPath;
		bool referenced = session.EditorSession.Open.Any(entry =>
			entry.Scratch && string.Equals(Path.GetFullPath(entry.Path), Path.GetFullPath(resolvedPath), comparison));
		if (!referenced || !session.FileSystem.FileExists(scratchPath)) {
			error = "The requested scratch file is not open in this session.";
			return false;
		}

		error = null;
		return true;
	}

	private static ScratchSaveResult WriteScratch(
		HostSession session,
		string scratchPath,
		string target) {
		try {
			if (session.Scratch.Owns(target)) {
				return ScratchSaveResult.Failed(scratchPath, "Choose a permanent path outside the scratch directory.");
			}
			string content = session.FileSystem.ReadAllText(scratchPath);
			session.FileSystem.WriteAllText(target, content);
			if (session.FileSystem.TryGetStat(target, out var revision)) {
				session.FileActivity.ReportChanged(target, revision);
			}
			session.Scratch.Delete(scratchPath);
			return ScratchSaveResult.Saved(scratchPath, target);
		} catch (Exception ex) when (
			ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) {
			return ScratchSaveResult.Failed(
				scratchPath,
				$"Couldn't save {Path.GetFileName(target)}: {ex.Message}");
		}
	}

	private sealed record ScratchSaveResult(
		string ScratchPath,
		string Status,
		string? SavedPath,
		string? Error) {
		public static ScratchSaveResult Saved(string scratchPath, string savedPath) =>
			new(scratchPath, "saved", savedPath, null);

		public static ScratchSaveResult Cancelled(string scratchPath) =>
			new(scratchPath, "cancelled", null, null);

		public static ScratchSaveResult Failed(string scratchPath, string error) =>
			new(scratchPath, "failed", null, error);
	}
}
