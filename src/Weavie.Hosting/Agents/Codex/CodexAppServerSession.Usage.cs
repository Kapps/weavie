using System.Text.Json;
using Weavie.Core.Agents;
using Weavie.Core.Agents.Codex;

namespace Weavie.Hosting.Agents.Codex;

/// <summary>Normalizes Codex thread usage and account limits for the structured-session status surface.</summary>
public sealed partial class CodexAppServerSession : IStructuredAgentUsage {
	private AgentContextWindowUsage? _contextWindowUsage;
	private CodexRateLimits? _rateLimits;
	private long? _totalTokens;
	private long _usageGeneration;

	/// <inheritdoc/>
	public event Action<AgentUsageState>? UsageStateChanged;

	/// <inheritdoc/>
	public AgentUsageState UsageState {
		get {
			lock (_gate) {
				return new(_contextWindowUsage, _totalTokens, _rateLimits?.Windows ?? []);
			}
		}
	}

	private void ObserveUsage(long generation, string method, JsonElement notification) {
		if (method == "thread/tokenUsage/updated") {
			ApplyThreadUsage(generation, notification);
		} else if (method == "account/rateLimits/updated") {
			ApplyRateLimitUpdate(generation, notification);
		}
	}

	private void ApplyThreadUsage(long generation, JsonElement notification) {
		if (!CodexUsageAdapter.TryReadThreadUsage(notification, out var usage)) {
			return;
		}

		lock (_gate) {
			if (generation != _usageGeneration
				|| _threadId is { Length: > 0 } threadId
				&& !string.Equals(threadId, usage.ThreadId, StringComparison.Ordinal)) {
				return;
			}
			_contextWindowUsage = usage.ContextWindow;
			_totalTokens = usage.TotalTokens;
		}
		RaiseUsageState();
	}

	private void ApplyRateLimitUpdate(long generation, JsonElement notification) {
		if (!CodexUsageAdapter.TryReadRateLimitUpdate(notification, out var update)) {
			return;
		}

		lock (_gate) {
			if (generation != _usageGeneration) {
				return;
			}
			var current = _rateLimits;
			if (current is { Id.Length: > 0 }
				&& update.Id.Length > 0
				&& !string.Equals(current.Id, update.Id, StringComparison.Ordinal)) {
				return;
			}
			if (current is { Id.Length: 0 } && update.Id.Length > 0) {
				current = WithId(current, update.Id);
			}
			if (update.Id.Length == 0 && current is { } baseline) {
				update = WithId(update, baseline.Id);
			}
			_rateLimits = MergeRateLimits(current, update);
		}
		RaiseUsageState();
	}

	private async Task LoadRateLimitsAsync(long generation) {
		long request = NextRequest();
		JsonElement result;
		try {
			result = await _client.RequestAsync(
				request,
				CodexAppServerProtocol.AccountRateLimitsRead(request),
				CancellationToken.None).ConfigureAwait(false);
		} catch (Exception ex) when (ex is CodexRequestException or IOException) {
			return;
		}
		if (!CodexUsageAdapter.TryReadRateLimitSnapshot(result, out var snapshot)) {
			throw new JsonException("Codex app-server returned an invalid account rate-limit snapshot.");
		}

		if (ApplyRateLimitSnapshot(generation, snapshot)) {
			RaiseUsageState();
		}
	}

	private bool ApplyRateLimitSnapshot(long generation, CodexRateLimits snapshot) {
		lock (_gate) {
			if (generation != _usageGeneration) {
				return false;
			}
			_rateLimits = _rateLimits is { } pending
				&& (pending.Id.Length == 0 || string.Equals(pending.Id, snapshot.Id, StringComparison.Ordinal))
				? MergeRateLimits(snapshot, WithId(pending, snapshot.Id))
				: MergeRateLimits(null, snapshot);
			return true;
		}
	}

	private void ResetUsage(long generation) {
		lock (_gate) {
			_usageGeneration = generation;
			_contextWindowUsage = null;
			_rateLimits = null;
			_totalTokens = null;
		}
		RaiseUsageState();
	}

	private void RaiseUsageState() => UsageStateChanged?.Invoke(UsageState);

	private static CodexRateLimits MergeRateLimits(CodexRateLimits? baseline, CodexRateLimits update) {
		string? label = update.Label ?? baseline?.Label;
		return update with {
			Label = label,
			Windows = MergeLimits(baseline?.Windows ?? [], update.Windows)
				.Select(window => window with { Label = label })
				.ToArray(),
		};
	}

	private static IReadOnlyList<AgentRateLimitUsage> MergeLimits(
		IReadOnlyList<AgentRateLimitUsage> baseline,
		IReadOnlyList<AgentRateLimitUsage> updates) {
		var merged = baseline.ToDictionary(limit => limit.Id, StringComparer.Ordinal);
		foreach (var update in updates) {
			merged.TryGetValue(update.Id, out var current);
			merged[update.Id] = update with {
				WindowMinutes = update.WindowMinutes ?? current?.WindowMinutes,
				ResetsAt = update.ResetsAt ?? current?.ResetsAt,
			};
		}
		return [.. merged.Values];
	}

	private static CodexRateLimits WithId(CodexRateLimits limits, string id) =>
		new(id, limits.Label, limits.Windows.Select(window => window with {
			Id = id + window.Id[window.Id.LastIndexOf(':')..],
		}).ToArray());
}
