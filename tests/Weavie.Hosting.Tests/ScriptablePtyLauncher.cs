using Weavie.Core.Terminal;

namespace Weavie.Hosting.Tests;

/// <summary>
/// An <see cref="IPtyLauncher"/> that never spawns a real child: it records the resolved claude session args
/// and hands back a <see cref="ScriptableTerminal"/> the test drives directly (emit output, observe resizes).
/// Shared by the terminal-controller test suites.
/// </summary>
internal sealed class ScriptablePtyLauncher : IPtyLauncher {
	/// <summary>The claude session args (<c>--resume</c>/<c>--session-id</c> pair) of the most recent resolve.</summary>
	public IReadOnlyList<string> LastClaudeSessionArguments { get; private set; } = [];

	/// <summary>The terminal handed to the most recent launch, for the test to drive.</summary>
	public ScriptableTerminal? LastTerminal { get; private set; }

	/// <inheritdoc/>
	public ITerminal CreateTerminal() => LastTerminal = new ScriptableTerminal();

	/// <inheritdoc/>
	public PtyLaunch Resolve(PtyLaunchRequest request) {
		LastClaudeSessionArguments = request.ClaudeSessionArguments;
		return new PtyLaunch {
			Command = "noop",
			Arguments = request.BuildClaudeArguments(),
			RemoveEnvironment = [],
			Environment = new Dictionary<string, string>(StringComparer.Ordinal),
		};
	}
}

/// <summary>An <see cref="ITerminal"/> that never spawns a child but lets the test raise its output/exit events.</summary>
internal sealed class ScriptableTerminal : ITerminal {
	private readonly List<(int Columns, int Rows)> _resizes = [];

	public event Action<byte[]>? Output;
	public event Action<int>? Exited;

	public bool IsRunning { get; private set; }

	public bool HasForegroundJob => false;

	/// <summary>Every <see cref="Resize"/> call in order, so a test can assert the reattach nudge.</summary>
	public IReadOnlyList<(int Columns, int Rows)> Resizes => _resizes;

	public void Start(TerminalStartInfo startInfo) {
		IsRunning = true;
		_ = Exited; // ITerminal requires the event; this fake never exits on its own (CS0067 suppression)
	}

	public void Write(byte[] data) {
		// no child to write to
	}

	public void Resize(int columns, int rows) => _resizes.Add((columns, rows));

	public void Dispose() => IsRunning = false;

	/// <summary>Raises the <see cref="Output"/> event with <paramref name="data"/>, as a live child would.</summary>
	public void EmitOutput(byte[] data) => Output?.Invoke(data);
}
