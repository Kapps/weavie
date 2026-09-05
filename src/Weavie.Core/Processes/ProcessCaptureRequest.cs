using System.Collections.Immutable;

namespace Weavie.Core.Processes;

/// <summary>One child process to run to completion with its output captured.</summary>
public sealed record ProcessCaptureRequest {
	/// <summary>The executable to run, resolved against <c>PATH</c> when it is not a full path.</summary>
	public required string FileName { get; init; }

	/// <summary>Arguments for the executable, each escaped by the runtime — never a command line to re-parse.</summary>
	public required IReadOnlyList<string> Arguments { get; init; }

	/// <summary>Working directory for the child; empty inherits this process's.</summary>
	public string WorkingDirectory { get; init; } = string.Empty;

	/// <summary>Environment variables applied on top of the inherited environment.</summary>
	public IReadOnlyDictionary<string, string> Environment { get; init; } = ImmutableDictionary<string, string>.Empty;

	/// <summary>Text written to the child's stdin, which is then closed; empty hands it an immediate end of input.</summary>
	public string StandardInput { get; init; } = string.Empty;
}
