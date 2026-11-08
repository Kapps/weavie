using System.Diagnostics;
using Weavie.Hosting.Messaging;
using Xunit;
using Xunit.Abstractions;

namespace Weavie.Hosting.Tests;

/// <summary>
/// Measures one host operation end to end: the git processes it starts in the repo, the messages it posts to the
/// page, and its wall-clock time (reported only — CI machines are too noisy to assert on it).
/// </summary>
internal sealed class OperationBudget(TestHost host, ITestOutputHelper output) : IDisposable {
	private readonly ProcessRecorder _processes = new(host.RepoRoot);

	/// <summary>Runs <paramref name="operation"/>, waits for the work it started to settle, and checks both budgets.</summary>
	public Task WithinAsync(string name, Func<Task> operation, int gitProcesses, int pageMessages) =>
		WithinAsync(name, operation, () => Task.CompletedTask, gitProcesses, pageMessages);

	/// <summary>
	/// As <see cref="WithinAsync(string, Func{Task}, int, int)"/> for an operation whose work starts after a refresh
	/// cooldown: <paramref name="lastProcess"/> names the process that finishes it, so the window waits for it.
	/// </summary>
	public Task WithinAsync(string name, Func<Task> operation, string lastProcess, int gitProcesses, int pageMessages) =>
		WithinAsync(name, operation, () => _processes.StartedAsync(lastProcess), gitProcesses, pageMessages);

	private async Task WithinAsync(string name, Func<Task> operation, Func<Task> finished, int gitProcesses, int pageMessages) {
		await _processes.SettleAsync();
		_processes.Take();
		int postedBefore = host.Bridge.Posted.Count;
		var clock = Stopwatch.StartNew();
		await operation();
		var answered = clock.Elapsed;
		await finished();
		await _processes.SettleAsync();
		var started = _processes.Take();
		string[] posted = [.. host.Bridge.Posted.Skip(postedBefore).Select(Describe)];

		string report = $"{name}: {started.Count} git processes, {posted.Length} page messages, answered in "
			+ $"{answered.TotalMilliseconds:F0}ms\n  processes:\n    {string.Join("\n    ", started)}\n  messages:\n    "
			+ string.Join("\n    ", posted.GroupBy(message => message).Select(group => $"{group.Count()}× {group.Key}"));
		output.WriteLine(report);
		Assert.True(started.Count <= gitProcesses, $"Over the git process budget of {gitProcesses}.\n{report}");
		Assert.True(posted.Length <= pageMessages, $"Over the page message budget of {pageMessages}.\n{report}");
	}

	private static string Describe(string json) =>
		MessageEnvelope.TryParse(json, out var envelope) && envelope is not null ? $"{envelope.Kind} {envelope.Feature}.{envelope.Name}" : "unparsed";

	public void Dispose() => _processes.Dispose();
}
