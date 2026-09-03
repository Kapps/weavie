using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Weavie.Core;
using Weavie.Core.Commands;
using Weavie.Core.Editor;
using Weavie.Core.FileSystem;
using Weavie.Core.Git;
using Weavie.Core.Sessions;
using Weavie.Core.Workspaces;
using Weavie.Core.Worktrees;

namespace Weavie.Hosting;

// Exact-session preview, consent, revalidation, and teardown for the destructive delete lifecycle.
public sealed partial class HostCore {
	private readonly Dictionary<SessionSlot, DeleteAdmission> _deleteAdmissions = [];

	private sealed record DeleteAdmission(
		string Revision,
		string Fingerprint,
		string? Incarnation,
		DeleteSessionPreview Preview);

	private sealed record DeleteSnapshot(
		DeleteWorktreeRisk Worktree,
		IReadOnlyList<ScratchFileSnapshot> Drafts,
		string Fingerprint);

	private sealed record DeleteSnapshotResult(DeleteSnapshot? Snapshot, CommandResult? Failure);

	private sealed record DeleteValidationResult(DeleteSnapshot? Snapshot, CommandResult? Failure);

	private string ScratchDirectoryFor(string worktreePath) =>
		Path.Combine(WeaviePaths.WorkspaceScratchDir(Id), WorkspaceId.ForPath(worktreePath).Value);

	private ScratchStore ScratchStoreFor(SessionSlot slot) =>
		slot.Session?.Scratch ?? new ScratchStore(new LocalFileSystem(), ScratchDirectoryFor(slot.WorktreePath));

	private Task<CommandResult> PreviewDeleteSessionAsync(string? sessionId, CancellationToken ct) =>
		RunSessionLifecycleAsync(() => PreviewDeleteSessionCoreAsync(sessionId, ct), ct);

	private async Task<CommandResult> PreviewDeleteSessionCoreAsync(string? sessionId, CancellationToken ct) {
		if (ResolveDeleteTarget(sessionId) is not { } target) {
			return CommandResult.Failure("No such session.");
		}

		var captured = await CaptureDeleteSnapshotAsync(target, ct).ConfigureAwait(false);
		if (captured.Failure is { } failure) {
			return failure;
		}

		var preview = AdmitDeletePreview(target, captured.Snapshot!);
		return CommandResult.Success(null, SerializeDeletePreview(preview));
	}

	private Task<CommandResult> ConfirmDeleteSessionAsync(
		HostSession? source,
		string? sessionId,
		DeleteSessionConfirmation confirmation,
		CommandInvocationContext context,
		CancellationToken ct) =>
		RunSessionLifecycleAsync(
			() => ConfirmDeleteSessionCoreAsync(source, sessionId, confirmation, context, ct),
			ct);

	private async Task<CommandResult> ConfirmDeleteSessionCoreAsync(
		HostSession? source,
		string? sessionId,
		DeleteSessionConfirmation confirmation,
		CommandInvocationContext context,
		CancellationToken ct) {
		if (ResolveDeleteTarget(sessionId) is not { } target) {
			return CommandResult.Failure("No such session.");
		}

		var validation = await ValidateDeleteConfirmationAsync(
			target,
			confirmation,
			plannedUnloadCompleted: false,
			ct).ConfigureAwait(false);
		if (validation.Failure is { } failure) {
			return failure;
		}

		if (target.Session is { } targetSession && ReferenceEquals(targetSession, source)) {
			context.AfterReply(callbackCt => DeleteAfterReplyAsync(target, confirmation, callbackCt));
			return CommandResult.Success();
		}

		return await DeleteAdmittedSessionAsync(target, confirmation, ct)
			.ConfigureAwait(false);
	}

	private async Task DeleteAfterReplyAsync(
		SessionSlot target,
		DeleteSessionConfirmation confirmation,
		CancellationToken ct) {
		try {
			var result = await RunSessionLifecycleAsync(async () => {
				if (!ReferenceEquals(_sessions?.Find(target.Id), target)) {
					return CommandResult.Failure("The session changed after deletion was confirmed.");
				}

				var validation = await ValidateDeleteConfirmationAsync(
					target,
					confirmation,
					plannedUnloadCompleted: false,
					ct).ConfigureAwait(false);
				return validation.Failure
					?? await DeleteAdmittedSessionAsync(target, confirmation, ct).ConfigureAwait(false);
			}, ct).ConfigureAwait(false);
			if (!result.Ok) {
				Notify("error", result.Error ?? $"Couldn't delete session '{target.Label}'.");
			}
		} catch (OperationCanceledException) when (ct.IsCancellationRequested) {
			throw;
		} catch (Exception ex) {
			Notify("error", $"Couldn't delete session '{target.Label}': {ex.Message}");
			throw;
		}
	}

	private SessionSlot? ResolveDeleteTarget(string? sessionId) {
		var target = string.IsNullOrWhiteSpace(sessionId) ? null : _sessions?.Find(sessionId);
		return target is not null && (IsWorkspaceCheckout(target) || _worktrees is not null) ? target : null;
	}

	private async Task<DeleteValidationResult> ValidateDeleteConfirmationAsync(
		SessionSlot target,
		DeleteSessionConfirmation confirmation,
		bool plannedUnloadCompleted,
		CancellationToken ct) {
		if (!_deleteAdmissions.TryGetValue(target, out var admission)
			|| !string.Equals(admission.Revision, confirmation.Revision, StringComparison.Ordinal)) {
			return new(null, CommandResult.Failure("Delete Session needs a current preview revision."));
		}

		var captured = await CaptureDeleteSnapshotAsync(target, ct).ConfigureAwait(false);
		if (captured.Failure is { } captureFailure) {
			return new(null, captureFailure);
		}

		var snapshot = captured.Snapshot!;
		string? currentIncarnation = target.Session?.Incarnation;
		bool ownerMatches = string.Equals(currentIncarnation, admission.Incarnation, StringComparison.Ordinal)
			|| (plannedUnloadCompleted && currentIncarnation is null);
		if (!ownerMatches
			|| !string.Equals(snapshot.Fingerprint, admission.Fingerprint, StringComparison.Ordinal)) {
			var refreshed = AdmitDeletePreview(target, snapshot);
			return new(null, CommandResult.Failure(
				"The session changed while deletion was open. Review the refreshed details.",
				SerializeDeletePreview(refreshed)));
		}

		if (snapshot.Worktree.Branchless && !confirmation.ForceWorktree) {
			return new(null, CommandResult.Failure(
				$"Session '{target.Label}' has no branch keeping its commits, so deleting it would leave them unreachable.",
				SerializeDeletePreview(admission.Preview)));
		}
		if (snapshot.Worktree.State != "clean" && !confirmation.ForceWorktree) {
			return new(null, CommandResult.Failure(
				$"Session '{target.Label}' has uncommitted changes; deleting would discard them.",
				SerializeDeletePreview(admission.Preview)));
		}
		if (snapshot.Drafts.Count > 0 && !confirmation.DiscardDrafts) {
			return new(null, CommandResult.Failure(
				$"Session '{target.Label}' has unsaved drafts; deleting would discard them.",
				SerializeDeletePreview(admission.Preview)));
		}

		return new(snapshot, null);
	}

	private async Task<DeleteSnapshotResult> CaptureDeleteSnapshotAsync(
		SessionSlot target,
		CancellationToken ct) {
		if (target.Session is { } session
			&& await FlushSessionViewAsync(session, ct).ConfigureAwait(false) is { } flushFailure) {
			return new(null, flushFailure);
		}

		try {
			var drafts = ScratchStoreFor(target).Inspect(target.EditorSession);
			bool removesCheckout = !IsWorkspaceCheckout(target);
			bool branchless = false;
			string state = "clean";
			string[] changed = [];
			string? gitFingerprint = null;
			if (removesCheckout && IsLiveWorktree(target.WorktreePath)) {
				var git = await new GitService().GetDeletionSnapshotAsync(target.WorktreePath, ct)
					.ConfigureAwait(false);
				branchless = git.Branch is null;
				state = git.Changes.State switch {
					WorktreeChangeState.UntrackedOnly => "untracked",
					WorktreeChangeState.Modified => "modified",
					_ => "clean",
				};
				changed = [.. git.Changes.TrackedFiles
					.Concat(git.Changes.UntrackedFiles)
					.Order(StringComparer.Ordinal)];
				gitFingerprint = git.Fingerprint;
			}

			const int previewLimit = 5;
			var risk = new DeleteWorktreeRisk {
				State = state,
				Branchless = branchless,
				ChangedFiles = changed.Take(previewLimit).ToArray(),
				ChangedCount = changed.Length,
			};
			string fingerprint = DeleteFingerprint(target, risk, changed, drafts, gitFingerprint);
			return new(new DeleteSnapshot(risk, drafts, fingerprint), null);
		} catch (Exception ex) when (ex is GitException or IOException or UnauthorizedAccessException) {
			return new(null, CommandResult.Failure(
				$"Couldn't inspect session '{target.Label}' before deletion: {ex.Message}"));
		}
	}

	private DeleteSessionPreview AdmitDeletePreview(SessionSlot target, DeleteSnapshot snapshot) {
		string revision = Guid.NewGuid().ToString("n");
		var preview = new DeleteSessionPreview {
			Revision = revision,
			Label = target.Label,
			RemovesCheckout = !IsWorkspaceCheckout(target),
			Worktree = snapshot.Worktree,
			Drafts = [.. snapshot.Drafts.Select(draft => new ScratchDraftInfo {
				Path = draft.Path,
				Name = draft.Name,
			})],
		};
		_deleteAdmissions[target] = new DeleteAdmission(
			revision,
			snapshot.Fingerprint,
			target.Session?.Incarnation,
			preview);
		return preview;
	}

	private string DeleteFingerprint(
		SessionSlot target,
		DeleteWorktreeRisk risk,
		IReadOnlyList<string> changed,
		IReadOnlyList<ScratchFileSnapshot> drafts,
		string? gitFingerprint) {
		string json = JsonSerializer.Serialize(new {
			host = _hostIncarnation,
			target.Id,
			target.WorktreePath,
			risk.State,
			risk.Branchless,
			changed,
			gitFingerprint,
			drafts = drafts.Select(draft => new { draft.Path, draft.ContentHash }),
		}, EditorSessionSerialization.MessageOptions);
		return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
	}

	private static string SerializeDeletePreview(DeleteSessionPreview preview) =>
		JsonSerializer.Serialize(preview, EditorSessionSerialization.MessageOptions);

	private async Task<CommandResult> DeleteAdmittedSessionAsync(
		SessionSlot target,
		DeleteSessionConfirmation confirmation,
		CancellationToken admissionCancellation) {
		try {
			if (!ReferenceEquals(_sessions?.Find(target.Id), target)) {
				return CommandResult.Failure("The session changed after deletion was confirmed.");
			}

			var scratch = ScratchStoreFor(target);
			if (target.Loaded) {
				await _ui.InvokeAsync(() => UnloadSlotAsync(target), admissionCancellation).ConfigureAwait(false);
			}

			if (!IsWorkspaceCheckout(target)) {
				await Task.Delay(TimeSpan.FromSeconds(1), CancellationToken.None).ConfigureAwait(false);
			}

			// Unload closes the only in-app writer, and the worktree settle delay is deliberately before this final
			// comparison. Nothing previewed may change during either teardown window and still be discarded.
			var finalValidation = await ValidateDeleteConfirmationAsync(
				target,
				confirmation,
				plannedUnloadCompleted: true,
				CancellationToken.None).ConfigureAwait(false);
			if (finalValidation.Failure is { } finalFailure) {
				return finalFailure;
			}
			var snapshot = finalValidation.Snapshot!;

			if (!IsWorkspaceCheckout(target)) {
				await _worktrees!.RemoveAsync(
					target.WorktreePath,
					deleteBranch: false,
					confirmation.ForceWorktree,
					CancellationToken.None).ConfigureAwait(false);
			}

			scratch.DeleteReferenced(target.EditorSession);
			await _ui.InvokeAsync(() => {
				_sessions?.Remove(target);
				_deleteAdmissions.Remove(target);
				if (_sessions?.Slots.Count == 0) {
					EnsureWorkspaceSession();
				} else {
					PushSessionList();
					PersistSessionState();
				}
				return Task.CompletedTask;
			}, CancellationToken.None).ConfigureAwait(false);
			Notify("info", (IsWorkspaceCheckout(target), snapshot.Worktree.Branchless) switch {
				(true, _) => $"Session '{target.Label}' was deleted. Its checkout was kept.",
				(false, true) => $"Session '{target.Label}' was deleted. Its checkout had no branch to keep.",
				_ => $"Session '{target.Label}' was deleted. Its branch was kept.",
			});
			return CommandResult.Success();
		} catch (WorktreeDirtyException) {
			return CommandResult.Failure(
				$"Session '{target.Label}' changed before its worktree could be removed. Preview deletion again.");
		} catch (WorktreeOrphanException ex) {
			return CommandResult.Failure($"Couldn't delete session '{target.Label}': {ex.Message}");
		} catch (Exception ex) when (ex is GitException or IOException or UnauthorizedAccessException) {
			return CommandResult.Failure($"Couldn't delete session '{target.Label}': {ex.Message}");
		}
	}
}
