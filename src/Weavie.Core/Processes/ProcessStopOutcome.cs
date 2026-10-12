namespace Weavie.Core.Processes;

/// <summary>How a stopped child ended: on its own after being asked, or force-killed because it didn't.</summary>
public sealed record ProcessStopOutcome {
	private ProcessStopOutcome(string? forcedBecause) {
		ForcedBecause = forcedBecause;
	}

	/// <summary>The child ended through its owner's stop protocol, without escalating to a force-kill.</summary>
	public static ProcessStopOutcome Graceful { get; } = new((string?)null);

	/// <summary>The child was force-killed; <paramref name="because"/> completes "the child …" for the user.</summary>
	public static ProcessStopOutcome Forced(string because) {
		ArgumentException.ThrowIfNullOrEmpty(because);
		return new ProcessStopOutcome(because);
	}

	/// <summary>Why the child had to be force-killed (e.g. <c>didn't exit within 5s of closing its input</c>); null when graceful.</summary>
	public string? ForcedBecause { get; }

	/// <summary>Whether the child had to be force-killed.</summary>
	public bool WasForced => ForcedBecause is not null;
}
