using Weavie.Core.FileSystem;
using Weavie.Workflows;

namespace Weavie.Core.Workflows;

/// <summary>Reads workflow evidence with the shared text decoder and worktree path rule.</summary>
public sealed class WorkflowFileReader(IFileSystem fileSystem, string worktree) : IWorkflowFileReader {
	/// <inheritdoc/>
	public WorkflowFileRead Read(string relativePath) {
		if (!WorkflowFilePaths.TryResolve(worktree, relativePath, out _, out string full)) {
			return new WorkflowFileRead.Unreadable("not a file path inside the worktree");
		}
		try {
			return fileSystem.TryReadAllText(full, out string text)
				? new WorkflowFileRead.Text(text)
				: new WorkflowFileRead.Unreadable("not readable text");
		} catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) {
			return new WorkflowFileRead.Unreadable("file no longer exists");
		} catch (UnauthorizedAccessException) {
			return new WorkflowFileRead.Unreadable("access denied");
		} catch (IOException) {
			return new WorkflowFileRead.Unreadable("file could not be read");
		}
	}
}
