using Weavie.Core.Configuration;
using Weavie.Core.Editor;
using Weavie.Core.FileSystem;
using Weavie.Core.Mcp;

namespace Weavie.Core.Agents;

/// <summary>Provider features that Weavie may expose without guessing or silently degrading.</summary>
[Flags]
public enum AgentProviderCapabilities {
	/// <summary>The provider runs in the compatibility PTY pane.</summary>
	Terminal = 1,

	/// <summary>The provider connects to the standard Weavie capability registry.</summary>
	CapabilityRegistry = 2,

	/// <summary>The provider supplies IDE editor and diff integration.</summary>
	Ide = 4,

	/// <summary>The provider emits lifecycle and tool events.</summary>
	Events = 8,

	/// <summary>The provider reports its edit disposition.</summary>
	EditDisposition = 16,
}

/// <summary>Stable identity and capability metadata for an agent provider.</summary>
public sealed record AgentProviderInfo {
	/// <summary>The persistence-safe provider id.</summary>
	public required string Id { get; init; }

	/// <summary>The user-facing provider name.</summary>
	public required string Name { get; init; }

	/// <summary>The explicitly supported provider features.</summary>
	public required AgentProviderCapabilities Capabilities { get; init; }
}

/// <summary>Required provider-neutral dependencies for one worktree-scoped agent session.</summary>
public sealed record AgentSessionContext {
	/// <summary>The live settings store.</summary>
	public required SettingsStore Settings { get; init; }

	/// <summary>The worktree root.</summary>
	public required string Workspace { get; init; }

	/// <summary>The session filesystem.</summary>
	public required IFileSystem FileSystem { get; init; }

	/// <summary>The standard capability registry already started for this session.</summary>
	public required CapabilityRegistryHost Registry { get; init; }

	/// <summary>The provider's diff presentation surface.</summary>
	public required IDiffPresenter DiffPresenter { get; init; }

	/// <summary>The live editor context exposed to the provider.</summary>
	public required EditorStore Editor { get; init; }

	/// <summary>The host runtime identity.</summary>
	public required HostRuntimeInfo Runtime { get; init; }

	/// <summary>The synchronous normalized event sink.</summary>
	public required IAgentEventSink Events { get; init; }
}

/// <summary>Creates provider sessions without exposing provider protocols to the host composition.</summary>
public interface IAgentProvider {
	/// <summary>The provider identity.</summary>
	AgentProviderInfo Info { get; }

	/// <summary>Creates one live provider session.</summary>
	IAgentSession CreateSession(AgentSessionContext context);
}
