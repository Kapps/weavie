using Weavie.Core.Changes;
using Weavie.Core.Inference;

namespace Weavie.Core.Revise;

/// <summary>
/// Runs a session's revisions: one batched query for the whole request, then a guarded per-region write the editor
/// holding the file can refuse. A region is published as in flight from the moment it is accepted until it reaches
/// a terminal outcome.
/// </summary>
public sealed class ReviseService {
	private readonly IInferenceService _inference;
	private readonly SessionChangeTracker _changes;
	private readonly IReviseSurface _surface;
	private readonly object _gate = new();
	private readonly List<ReviseRegion> _inFlight = [];
	private int _lastId;

	/// <summary>Creates the service over the session's inference rail, change tracker, and client surface.</summary>
	/// <param name="inference">The typed inference rail.</param>
	/// <param name="changes">The session's change tracker, which owns the guarded write.</param>
	/// <param name="surface">Where in-flight state and failures reach the user.</param>
	public ReviseService(IInferenceService inference, SessionChangeTracker changes, IReviseSurface surface) {
		ArgumentNullException.ThrowIfNull(inference);
		ArgumentNullException.ThrowIfNull(changes);
		ArgumentNullException.ThrowIfNull(surface);
		_inference = inference;
		_changes = changes;
		_surface = surface;
	}

	/// <summary>The regions currently being revised.</summary>
	public IReadOnlyList<ReviseRegion> InFlight {
		get {
			lock (_gate) {
				return [.. _inFlight];
			}
		}
	}

	/// <summary>
	/// Revises every target against <paramref name="instruction"/> in one query, returning each one's outcome. A
	/// target overlapping a region already in flight is refused before the query runs.
	/// </summary>
	/// <param name="owner">The session whose worktree owns the query.</param>
	/// <param name="targets">The regions to revise.</param>
	/// <param name="instruction">What to do to every region's text.</param>
	/// <param name="origin">Whether a person initiated this revision.</param>
	/// <param name="cancellationToken">Cancels the query without writing anything.</param>
	public async Task<IReadOnlyList<ReviseResult>> RunAsync(
		InferenceOwner owner,
		IReadOnlyList<ReviseTarget> targets,
		string instruction,
		InferenceInvocationOrigin origin,
		CancellationToken cancellationToken) {
		ArgumentNullException.ThrowIfNull(owner);
		ArgumentNullException.ThrowIfNull(targets);
		ArgumentException.ThrowIfNullOrWhiteSpace(instruction);

		var minted = new List<ReviseRegion>();
		var refused = new Dictionary<int, ReviseResult>();
		var accepted = new List<ReviseRegion>();
		lock (_gate) {
			foreach (var target in targets) {
				var region = new ReviseRegion {
					Id = ++_lastId,
					Path = target.Path,
					Range = target.Range,
					OriginalText = target.OriginalText,
				};
				minted.Add(region);
				if (_inFlight.Concat(accepted).Any(other => Overlaps(other, region))) {
					refused[region.Id] = Fail(
						region, ReviseOutcome.AlreadyInFlight, "that region is already being revised");
					continue;
				}

				accepted.Add(region);
			}

			_inFlight.AddRange(accepted);
		}

		if (accepted.Count == 0) {
			return [.. minted.Select(region => refused[region.Id])];
		}

		Publish();
		Dictionary<int, ReviseResult> revised;
		try {
			revised = (await ReviseAsync(owner, accepted, instruction, origin, cancellationToken))
				.ToDictionary(result => result.Region.Id);
		} finally {
			RetireAll(accepted);
		}

		// Results follow the caller's target order, never the order regions happened to finish in.
		return [.. minted.Select(region =>
			refused.TryGetValue(region.Id, out var result) ? result : revised[region.Id])];
	}

	private async Task<IReadOnlyList<ReviseResult>> ReviseAsync(
		InferenceOwner owner,
		IReadOnlyList<ReviseRegion> accepted,
		string instruction,
		InferenceInvocationOrigin origin,
		CancellationToken cancellationToken) {
		var input = new ReviseQueryInput {
			Instruction = instruction,
			Regions = [.. accepted.Select(region => new ReviseQueryRegion {
				Id = region.Id, Path = region.Path, Text = region.OriginalText,
			})],
		};
		InferenceResult<ReviseQueryOutput> query;
		try {
			query = await _inference.QueryAsync(
				owner,
				InferenceModelCategory.Utility,
				new InferenceInput { Prompt = ReviseQuery.BuildPrompt(input), Images = [] },
				ReviseQuery.ResponseType,
				ReviseQuery.OptionsFor(origin),
				cancellationToken);
		} catch (Exception ex) when (ex is not OperationCanceledException) {
			// The caller runs this detached, so an escaping throw would reach no one: the tint would vanish with
			// no edit and no explanation. Cancellation is not a failure and still propagates.
			return [.. accepted.Select(region => Fail(region, ReviseOutcome.QueryFailed, ex.Message))];
		}

		if (query is InferenceFailure<ReviseQueryOutput> failure) {
			return [.. accepted.Select(region => Fail(region, ReviseOutcome.QueryFailed, failure.Detail))];
		}

		// A repeated id makes both entries unusable: nothing in the reply says which one belongs to the region.
		var revisions = new Dictionary<int, string?>();
		foreach (var revision in ((InferenceSuccess<ReviseQueryOutput>)query).Value.Regions) {
			revisions[revision.Id] = revisions.ContainsKey(revision.Id) ? null : revision.Text;
		}

		var results = new List<ReviseResult>();
		foreach (var region in accepted) {
			results.Add(await CommitAsync(region, revisions, cancellationToken));
			Retire(region);
		}

		return results;
	}

	private async Task<ReviseResult> CommitAsync(
		ReviseRegion region,
		IReadOnlyDictionary<int, string?> revisions,
		CancellationToken cancellationToken) {
		if (!revisions.TryGetValue(region.Id, out string? replacement) || replacement is null) {
			return Fail(region, ReviseOutcome.NotProposed, "the model returned no usable revision for it");
		}

		if (string.Equals(replacement, region.OriginalText, StringComparison.Ordinal)) {
			// The tint vanishing with the text unchanged reads as a silent failure unless we say why.
			return Fail(region, ReviseOutcome.Unchanged, "the model returned it unchanged");
		}

		if (await _surface.ConfirmAsync(region, cancellationToken) is { } refusal) {
			return Fail(region, ReviseOutcome.Declined, refusal);
		}

		try {
			return _changes.ApplyRevision(region.Path, region.Range, region.OriginalText, replacement) switch {
				ReviseApplyOutcome.Applied => new ReviseResult(region, ReviseOutcome.Applied, string.Empty),
				_ => Fail(region, ReviseOutcome.Changed, "the file changed while it was being revised"),
			};
		} catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
			return Fail(region, ReviseOutcome.WriteFailed, ex.Message);
		}
	}

	private ReviseResult Fail(ReviseRegion region, ReviseOutcome outcome, string reason) {
		_surface.Failed(region, reason);
		return new ReviseResult(region, outcome, reason);
	}

	// Two regions collide when they cover overlapping lines of the same file.
	private static bool Overlaps(ReviseRegion left, ReviseRegion right) =>
		string.Equals(left.Path, right.Path, StringComparison.Ordinal)
		&& left.Range.Start < right.Range.EndExclusive
		&& right.Range.Start < left.Range.EndExclusive;

	private void Retire(ReviseRegion region) => RetireAll([region]);

	// Snapshot inside the lock that mutates the set: two concurrent runs publishing stale snapshots out of order
	// would leave the client tinting a region that already finished.
	private void RetireAll(IReadOnlyList<ReviseRegion> regions) {
		ReviseRegion[] remaining;
		lock (_gate) {
			if (_inFlight.RemoveAll(region => regions.Any(retired => retired.Id == region.Id)) == 0) {
				return;
			}

			remaining = [.. _inFlight];
		}

		_surface.Publish(remaining);
	}

	private void Publish() => _surface.Publish(InFlight);
}
