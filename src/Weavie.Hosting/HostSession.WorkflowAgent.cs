using Weavie.Core.Agents;
using Weavie.Core.Sessions;
using Weavie.Core.Workflows;
using Weavie.Workflows;

namespace Weavie.Hosting;

// The run's persisted hold decides the model; stage opens, releases, and picks take one gate.
// See docs/specs/workflow-agent-selection.md.
public sealed partial class HostSession {
	private readonly SemaphoreSlim _controlGate = new(1, 1);
	private readonly IdleTurnDelivery<WorkflowRun> _workflowRelease;

	private IdleTurnDelivery<WorkflowRun> CreateWorkflowRelease() => new(
		Status,
		new HashSet<SessionStatus> { SessionStatus.Idle, SessionStatus.Waiting },
		_ => ReleaseWorkflowAsync(),
		(_, ex) => Notify("error", ReturnFailed + ex.Message));

	// A conversation that reopened since the run ended already runs on the user's own values.
	private void WireWorkflowHold() {
		if (Workflow.Current is { IsFinished: true, Hold: not null } ended) Workflow.SetHold(ended.Id, null);
		Workflow.Changed += () => {
			if (Workflow.Current is { IsFinished: true, Hold: not null } run) _workflowRelease.Queue(run);
		};
	}

	// The opening stage's agent takes effect before its turn is sent, all or nothing: a stage that can't open returns
	// the conversation to the session's own values and pauses the run unsent. A stage naming none returns it too.
	private async Task<bool> OpenStageAsync(WorkflowKickoff kickoff) {
		await _controlGate.WaitAsync().ConfigureAwait(false);
		try {
			if (!Workflow.Owes(kickoff) || Workflow.Current is not { } run) return false;
			string? refusal = null;
			if (Workflow.AgentOf(kickoff.Opens) is not { } agent) {
				if (run.Hold is not { } own) return true;
				refusal = await ReturnAsync(run.Id, own, kickoff.Opens, $"{kickoff.Opens} runs").ConfigureAwait(false);
			} else {
				try {
					await TakeAgentAsync(run, kickoff.Opens, agent).ConfigureAwait(false);
				} catch (Exception ex) {
					refusal = Workflow.Current is { Hold: { } held }
						&& await ReturnAsync(run.Id, held, kickoff.Opens, $"{kickoff.Opens} didn't open, back").ConfigureAwait(false) is { } failed
						? $"{ex.Message} {failed}"
						: ex.Message;
				}
			}

			if (refusal is not null) Workflow.RefuseOpen(kickoff, refusal);
			return refusal is null;
		} finally {
			_controlGate.Release();
		}
	}

	private async Task TakeAgentAsync(WorkflowRun run, string stage, WorkflowAgent agent) {
		var axes = StageControls.ControlState.Axes;
		var held = new Dictionary<string, string>(StringComparer.Ordinal);
		if (WorkflowAgentControls.Model(stage, agent, axes) is { } model) held[model.Axis] = model.Value;
		foreach (var (axis, value) in agent.Options) held[axis] = value;
		var hold = new WorkflowHold { Agent = agent.Name, Stage = stage, Own = run.Hold?.Own ?? WorkflowAgentControls.Own(axes), Held = held };
		if (Workflow.SetHold(run.Id, hold)) await HoldAsync(hold, stage, agent, $"{stage} runs").ConfigureAwait(false);
	}

	// Returns the conversation to the session's own values and holds nothing; the failure when it couldn't, holding on.
	private async Task<string?> ReturnAsync(string runId, WorkflowHold hold, string stage, string subject) {
		try {
			await HoldAsync(hold, stage, null, subject).ConfigureAwait(false);
			Workflow.SetHold(runId, null);
			return null;
		} catch (Exception ex) {
			return ReturnFailed + ex.Message;
		}
	}

	private const string ReturnFailed = "Weavie couldn't return the agent to the session's own settings: ";

	private async Task ReleaseWorkflowAsync() {
		await _controlGate.WaitAsync().ConfigureAwait(false);
		try {
			if (Workflow.Current is not { IsFinished: true, Hold: { } hold } run) return;
			if (await ReturnAsync(run.Id, hold, run.Stage, $"{run.WorkflowId} ended, back").ConfigureAwait(false) is not { } failed) return;
			Workflow.SetHold(run.Id, null);
			Notify("error", failed);
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
		Agent.Controls is not { } controls ? throw new AgentControlException("This session's agent has no controls a stage can set.")
		: !controls.ControlState.Ready ? throw new AgentControlException("The agent's conversation isn't ready for a model change.")
		: controls;

	// A held value wins and was checked when the stage opened; an own value the agent doesn't advertise now doesn't apply.
	private static IEnumerable<AgentControlValue> Targets(
		Dictionary<string, string> own, IReadOnlyDictionary<string, string> held, IEnumerable<AgentControlAxis> axes) =>
		axes.Where(WorkflowAgentControls.Changeable).SelectMany<AgentControlAxis, AgentControlValue>(axis =>
			held.TryGetValue(axis.Id, out string? value) ? [new(axis.Id, value)]
			: own.TryGetValue(axis.Id, out value) && axis.Options.Any(o => o.Id == value) ? [new(axis.Id, value)]
			: []);

	/// <summary>
	/// A pick in the pane's controls: while a live run's open stage holds values it changes that stage only; otherwise
	/// it is the session's own value, remembered as the user's default. A stage still opening counts as not open.
	/// </summary>
	internal async Task SetAgentControlAsync(string axis, string value) {
		if (Agent.Controls is not { } controls) return;
		await _controlGate.WaitAsync().ConfigureAwait(false);
		try {
			if (Workflow.Current is { Hold: { } hold } run && controls.ControlState.Axes.FirstOrDefault(a => a.Id == axis) is { } picked
				&& WorkflowAgentControls.Changeable(picked)) {
				if (!run.IsFinished && hold.Stage == run.Stage && run.Kickoff is not { Opens.Length: > 0 }) {
					Workflow.SetHold(run.Id, hold with { Held = new(hold.Held, StringComparer.Ordinal) { [axis] = value } });
					try {
						await controls.ApplyControlsAsync([new(axis, value)]).ConfigureAwait(false);
					} catch {
						Workflow.SetHold(run.Id, hold);
						throw;
					}
					return;
				}

				Workflow.SetHold(run.Id, hold with { Own = new(hold.Own, StringComparer.Ordinal) { [axis] = value } });
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
