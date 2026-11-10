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

	/// <summary>
	/// Applies <paramref name="values"/> in order, after every change already queued, without remembering any of them as
	/// the user's default. Completes once the agent confirms each one; faults with <see cref="AgentControlException"/>
	/// when one isn't advertised at that moment, or when the conversation is replaced first.
	/// </summary>
	Task ApplyControlsAsync(IReadOnlyList<AgentControlValue> values);
}

/// <summary>One value for one provider-advertised control axis.</summary>
public sealed record AgentControlValue(string Axis, string Value);

/// <summary>A control change the agent couldn't take, with a message naming what it advertises.</summary>
public sealed class AgentControlException(string message) : Exception(message);
