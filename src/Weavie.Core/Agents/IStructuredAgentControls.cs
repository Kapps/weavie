namespace Weavie.Core.Agents;

/// <summary>
/// A structured session's live, user-adjustable controls (model, approvals, sandbox) and its slash surface.
/// A sibling capability to <see cref="IStructuredAgentSession"/>: the host probes for it with <c>is</c> and a
/// provider that has nothing to offer simply does not implement it, so the web never learns a provider concept.
/// </summary>
public interface IStructuredAgentControls {
	/// <summary>The current control axes and slash entries for this session.</summary>
	AgentControlState ControlState { get; }

	/// <summary>Raised when the control state changes — options load, a value is applied, or skills change.</summary>
	event Action<AgentControlState> ControlStateChanged;

	/// <summary>Applies <paramref name="value"/> to the axis identified by <paramref name="axis"/>, live for this session.</summary>
	void SetControl(string axis, string value);
}
