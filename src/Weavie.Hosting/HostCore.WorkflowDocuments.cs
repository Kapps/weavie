using Weavie.Core;
using Weavie.Core.Workflows;
using Weavie.Core.Workspaces;
using Weavie.Workflows;

namespace Weavie.Hosting;

public sealed partial class HostCore {
	private readonly WorkflowDocumentFolders _workflowDocuments;
	private bool _worktreeReconciliationFailed;

	private void RestoreWorkflowDocuments() {
		try {
			if (_worktreeReconciliationFailed) throw new IOException("Worktree reconciliation failed; session ownership is incomplete.");
			if (_sessionStore.LoadFailure is { } failure) throw new IOException(failure);
			_workflowDocuments.Restore(WorkflowDocumentOwners());
		} catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) {
			_sessionStartupNotices.Add(("error", $"Could not restore workflow document ownership; automatic cleanup is stopped: {ex.Message}"));
		}
	}

	private IEnumerable<KeyValuePair<string, string>> WorkflowDocumentOwners() {
		foreach (var slot in _sessions!.Slots) {
			string path = WeaviePaths.WorkspaceWorkflowRunFile(WorkspaceId.ForPath(slot.WorktreePath));
			WorkflowRun? run;
			try {
				run = WorkflowRunDocument.Read(File.ReadAllText(path));
			} catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) {
				run = null;
			}
			if ((run?.DocumentsDirectory ?? slot.WorkflowDocumentsDirectory) is { } directory) {
				yield return new(slot.WorktreePath, directory);
			}
		}
	}
}
