using Weavie.Core.Agents;
using Weavie.Core.Sessions;
using Weavie.Core.Workflows;
using Weavie.Workflows;

namespace Weavie.Hosting;

// Which model the session's conversation runs on during a workflow run. The run's persisted hold is the only state:
// the session's own values, and the values the open stage holds over them. Opening a stage, releasing a finished run,
// and a pick in the pane's controls take one gate, so each sees the hold the last one left. See
// docs/specs/workflow-agent-selection.md.
public sealed partial class HostSession {
	private readonly SemaphoreSlim _controlGate = new(1, 1);
	private readonly IdleTurnDelivery<WorkflowRun> _workflowRelease;

	private IdleTurnDelivery<WorkflowRun> CreateWorkflowRelease() => new(
		Status,
		new HashSet<SessionStatus> { SessionStatus.Idle, SessionStatus.Waiting },
		_ => ReleaseWorkflowAsync(),
		(_, ex) => Notify("error", $"Weavie couldn't return the agent to the session's own settings: {ex.Message}"));

	// A conversation that reopened since the run ended already runs on the user's own values.
	private void WireWorkflowHold() {
		if (Workflow.Current is { IsFinished: true, Hold: not null }) Workflow.SetHold(null);
		Workflow.Changed += () => {
			if (Workflow.Current is { IsFinished: true, Hold: not null } run) _workflowRelease.Queue(run);
		};
	}

	// The opening stage's agent takes effect before its turn is sent; a stage that names none returns the
	// conversation to the session's own values.
	private async Task OpenStageAsync(WorkflowKickoff kickoff) {
		await _controlGate.WaitAsync().ConfigureAwait(false);
		try {
			if (!Workflow.Owes(kickoff) || Workflow.Current is not { } run) return;
			string subject = $"{kickoff.Opens} runs";
			if (Workflow.AgentOf(kickoff.Opens) is not { } agent) {
				if (run.Hold is not { } own) return;
				await HoldAsync(own, kickoff.Opens, null, subject).ConfigureAwait(false);
				Workflow.SetHold(null);
				return;
			}

			var axes = StageControls.ControlState.Axes;
			var held = new Dictionary<string, string>(StringComparer.Ordinal);
			if (WorkflowAgentControls.Model(kickoff.Opens, agent, axes) is { } model) held[model.Axis] = model.Value;
			foreach (var (axis, value) in agent.Options) held[axis] = value;
			var hold = new WorkflowHold { Agent = agent.Name, Own = run.Hold?.Own ?? WorkflowAgentControls.Own(axes), Held = held };
			Workflow.SetHold(hold);
			await HoldAsync(hold, kickoff.Opens, agent, subject).ConfigureAwait(false);
		} finally {
			_controlGate.Release();
		}
	}

	private async Task ReleaseWorkflowAsync() {
		await _controlGate.WaitAsync().ConfigureAwait(false);
		try {
			if (Workflow.Current is not { IsFinished: true, Hold: { } hold } run) return;
			try {
				await HoldAsync(hold, run.Stage, null, $"{run.WorkflowId} ended, back").ConfigureAwait(false);
			} finally {
				Workflow.SetHold(null);
			}
		} finally {
			_controlGate.Release();
		}
	}

	// Model first, because the options an agent advertises depend on it. Every axis the stage doesn't set returns to
	// the session's own value where the agent advertises it; without an agent, every axis does.
	private async Task HoldAsync(WorkflowHold hold, string stage, WorkflowAgent? agent, string subject) {
		var controls = StageControls;
		var before = controls.ControlState.Axes;
		var held = agent is null ? [] : hold.Held;
		var model = WorkflowAgentControls.ModelAxis(before);
		await controls.ApplyControlsAsync([.. Targets(hold.Own, held, before.Where(a => a == model))]).ConfigureAwait(false);
		var axes = controls.ControlState.Axes;
		if (agent is not null) WorkflowAgentControls.CheckOptions(stage, agent, axes);
		model = WorkflowAgentControls.ModelAxis(axes);
		await controls.ApplyControlsAsync([.. Targets(hold.Own, held, axes.Where(a => a != model))]).ConfigureAwait(false);
		var after = controls.ControlState.Axes;
		if (!after.Select(a => a.Value).SequenceEqual(before.Select(a => a.Value))) {
			Agent.Structured?.Notice(WorkflowAgentControls.Line(subject, after, held.Keys));
		}
	}

	private IStructuredAgentControls StageControls =>
		Agent.Controls ?? throw new AgentControlException("This session's agent has no controls a stage can set.");

	// A held value wins and was checked when the stage opened; an own value the agent doesn't advertise now doesn't apply.
	private static IEnumerable<AgentControlValue> Targets(
		Dictionary<string, string> own, IReadOnlyDictionary<string, string> held, IEnumerable<AgentControlAxis> axes) =>
		axes.Where(WorkflowAgentControls.Changeable).SelectMany<AgentControlAxis, AgentControlValue>(axis =>
			held.TryGetValue(axis.Id, out string? value) ? [new(axis.Id, value)]
			: own.TryGetValue(axis.Id, out value) && axis.Options.Any(o => o.Id == value) ? [new(axis.Id, value)]
			: []);

	/// <summary>
	/// A pick in the pane's controls: while a live run holds values it changes the open stage only; otherwise it is the
	/// session's own value, remembered as the user's default.
	/// </summary>
	internal async Task SetAgentControlAsync(string axis, string value) {
		if (Agent.Controls is not { } controls) return;
		await _controlGate.WaitAsync().ConfigureAwait(false);
		try {
			if (Workflow.Current is { Hold: { } hold } run && controls.ControlState.Axes.FirstOrDefault(a => a.Id == axis) is { } picked
				&& WorkflowAgentControls.Changeable(picked)) {
				if (!run.IsFinished) {
					Workflow.SetHold(hold with { Held = new(hold.Held, StringComparer.Ordinal) { [axis] = value } });
					try {
						await controls.ApplyControlsAsync([new(axis, value)]).ConfigureAwait(false);
					} catch {
						Workflow.SetHold(hold);
						throw;
					}
					return;
				}

				Workflow.SetHold(hold with { Own = new(hold.Own, StringComparer.Ordinal) { [axis] = value } });
			}

			controls.SetControl(axis, value);
		} finally {
			_controlGate.Release();
		}
	}

	private sealed class WorkflowControlHold(HostSession session) : IAgentControlHold {
		public IReadOnlyDictionary<string, string> Values() =>
			session.Workflow.Current is { IsFinished: false, Hold: { } hold } ? hold.Held : [];
	}
}
