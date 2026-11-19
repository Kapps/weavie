using System.Text.Json;
using Weavie.Core.Git;
using Weavie.Core.Workspaces;
using Weavie.Hosting.Messaging;

namespace Weavie.Hosting;

public sealed partial class HostCore {
	// A change touching more paths than this is cheaper for a page to download whole than to apply one by one.
	internal const int MaxFileIndexChangePaths = 4096;
	private const int FileIndexBatchPaths = 4096;

	// `home` anchors the omnibar's `~/…` open-by-path expansion against the *host's* profile, not the browser's.
	private static void PushWorkspaceToWeb(HostSession session, MessageTarget target) =>
		target.Feature("files").Publish("workspace", WireJson.Default.FileWorkspace, new(
			Path.GetFullPath(session.WorkspaceRoot),
			Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)));

	// Every revision is published exactly once, so a page holding the one before it can always take the step.
	// The first listing is revision 1, and no page holds the empty list before it.
	private static void PushFileIndexChangeToWeb(HostSession session, WorkspaceInventoryChange change) {
		var files = session.Bus.Feature("files");
		long revision = change.Snapshot.Revision;
		if (revision == 1) {
			return;
		}

		if (change.CreatedFiles.Count + change.DeletedFiles.Count > MaxFileIndexChangePaths) {
			files.Publish("indexReplaced", WireJson.Default.FileIndexReplaced, new(revision));
		} else {
			files.Publish("indexChanged", WireJson.Default.FileIndexChanged, new(
				revision,
				[.. change.CreatedFiles.Select(session.Inventory.RelativePath)],
				[.. change.DeletedFiles.Select(session.Inventory.RelativePath)]));
		}
	}

	/// <summary>
	/// Streams one session's file list as newline-delimited batches of workspace-relative paths, or a single
	/// "unchanged" line when the reader already holds <paramref name="knownRevision"/>. False when no loaded
	/// session has that address or its folder is gone.
	/// </summary>
	internal async Task<bool> WriteFileIndexAsync(
		SessionAddress address,
		long? knownRevision,
		Stream output,
		CancellationToken ct) {
		if (ExactSession(address) is not { } session) {
			return false;
		}

		using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct, session.Background.Stopping);
		WorkspaceInventorySnapshot snapshot;
		try {
			snapshot = await session.FileActivity.InventorySnapshotAsync(cancellation.Token).ConfigureAwait(false);
		} catch (Exception ex) when (ex is GitException or IOException or UnauthorizedAccessException
			&& session.EndIfWorkspaceRootIsGone($"Couldn't load workspace files: {ex.Message}")) {
			return false;
		}

		// The watcher's snapshot outlives a deleted root until the deletion's own events arrive.
		if (session.EndIfWorkspaceRootIsGone($"Couldn't load workspace files: {session.WorkspaceRoot} no longer exists.")) {
			return false;
		}

		if (knownRevision == snapshot.Revision) {
			await WriteBatchAsync([], unchanged: true, complete: true).ConfigureAwait(false);
			return true;
		}

		foreach (string[] paths in snapshot.Files.Select(session.Inventory.RelativePath).Chunk(FileIndexBatchPaths)) {
			await WriteBatchAsync(paths, unchanged: false, complete: false).ConfigureAwait(false);
		}

		await WriteBatchAsync([], unchanged: false, complete: true).ConfigureAwait(false);
		return true;

		async Task WriteBatchAsync(string[] paths, bool unchanged, bool complete) {
			await JsonSerializer.SerializeAsync(
				output,
				new FileIndexBatch(snapshot.Revision, snapshot.Files.Count, paths, unchanged, complete),
				WireJson.Default.FileIndexBatch,
				cancellation.Token).ConfigureAwait(false);
			await output.WriteAsync("\n"u8.ToArray(), cancellation.Token).ConfigureAwait(false);
			await output.FlushAsync(cancellation.Token).ConfigureAwait(false);
		}
	}
}

internal sealed record FileWorkspace(string Root, string Home);

internal sealed record FileIndexChanged(long Revision, IReadOnlyList<string> Added, IReadOnlyList<string> Removed);

internal sealed record FileIndexReplaced(long Revision);

internal sealed record FileIndexBatch(long Revision, int Count, IReadOnlyList<string> Paths, bool Unchanged, bool Complete);
