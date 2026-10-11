using Weavie.Core.Inference;

namespace Weavie.Hosting;

// Each session's rail emoji: one automatic utility query per slot, run one at a time and persisted with the slot.
// With automatic inference off nothing runs and the chip keeps its hue + monogram.
public sealed partial class HostCore {
	private const string SessionEmojiNoticeKey = "session-emoji";
	private readonly Lock _emojiGate = new();
	private readonly HashSet<SessionSlot> _emojiAttempted = [];
	private readonly CancellationTokenSource _emojiStop = new();
	private bool _emojiRunning;
	// Reconcile publishes worktree slots before restore copies their saved emoji, so runs wait for restore.
	private bool _emojiSlotsRestored;

	private void StartSessionEmojis() {
		lock (_emojiGate) {
			_emojiSlotsRestored = true;
		}

		ScheduleSessionEmojis();
	}

	private void ScheduleSessionEmojis() {
		lock (_emojiGate) {
			if (_emojiRunning || !_emojiSlotsRestored || !AutomaticInferenceEnabled()) {
				return;
			}

			_emojiRunning = true;
		}

		_ = Task.Run(PickSessionEmojisAsync);
	}

	private async Task PickSessionEmojisAsync() {
		try {
			while (NextSlotNeedingEmoji() is { } slot && await PickSessionEmojiAsync(slot).ConfigureAwait(false)) {
			}
		} catch (OperationCanceledException) when (_emojiStop.IsCancellationRequested) {
		} catch (Exception ex) {
			EndSessionEmojiRun();
			Log($"[sessions] picking session emojis failed: {ex}");
			_ui.Post(() => Notify("error", $"Couldn't pick session emojis: {ex.Message}", SessionEmojiNoticeKey));
		}
	}

	// Claims the next slot with no emoji (once per run, so a failure isn't retried in a loop), or ends the run.
	private SessionSlot? NextSlotNeedingEmoji() {
		lock (_emojiGate) {
			var slot = AutomaticInferenceEnabled() && !_emojiStop.IsCancellationRequested
				? _sessions?.Slots.FirstOrDefault(candidate => candidate.Emoji is null && _emojiAttempted.Add(candidate))
				: null;
			_emojiRunning = slot is not null;
			return slot;
		}
	}

	private void EndSessionEmojiRun() {
		lock (_emojiGate) {
			_emojiRunning = false;
		}
	}

	// Picks one slot's emoji; false ends the run because inference was switched off mid-run.
	private async Task<bool> PickSessionEmojiAsync(SessionSlot slot) {
		var result = await _inference.QueryAsync(
			new InferenceOwner { Workspace = slot.WorktreePath },
			InferenceModelCategory.Utility,
			new InferenceInput { Prompt = SessionEmojiInference.BuildPrompt(slot.Label), Images = [] },
			SessionEmojiInference.ResponseType,
			SessionEmojiInference.QueryOptions,
			_emojiStop.Token).ConfigureAwait(false);
		switch (result) {
			case InferenceSuccess<SessionEmojiInferenceOutput> { Value.Emoji: var emoji }
				when SessionEmojiInference.IsSingleEmoji(emoji):
				await _ui.InvokeAsync(() => {
					if (ReferenceEquals(_sessions?.Find(slot.Id), slot)) {
						slot.Emoji = emoji;
						PersistSessionState();
						PushSessionList();
					}
					return Task.CompletedTask;
				}, _emojiStop.Token).ConfigureAwait(false);
				return true;
			case InferenceSuccess<SessionEmojiInferenceOutput> { Value.Emoji: var emoji }:
				NotifySessionEmojiFailure(slot, $"the reply '{emoji}' isn't one emoji.");
				return true;
			case InferenceFailure<SessionEmojiInferenceOutput> {
				Kind: InferenceFailureKind.Disabled or InferenceFailureKind.PolicyDenied,
			}:
				// Leave the slot for the run that starts when inference is back on.
				lock (_emojiGate) {
					_emojiAttempted.Remove(slot);
					_emojiRunning = false;
				}
				return false;
			case InferenceFailure<SessionEmojiInferenceOutput> failure:
				NotifySessionEmojiFailure(slot, failure.Detail);
				return true;
			default:
				throw new InvalidOperationException("Session emoji inference returned an unknown result type.");
		}
	}

	private void NotifySessionEmojiFailure(SessionSlot slot, string reason) =>
		_ui.Post(() => Notify("warn", $"Couldn't pick an emoji for '{slot.Label}': {reason}", SessionEmojiNoticeKey));
}
