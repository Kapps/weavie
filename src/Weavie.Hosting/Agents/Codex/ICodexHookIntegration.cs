using Weavie.Core.Agents;

namespace Weavie.Hosting.Agents.Codex;

internal interface ICodexHookIntegration : IAsyncDisposable {
	IReadOnlyList<string> GlobalArguments { get; }

	IReadOnlyList<string> AppServerArguments { get; }

	IReadOnlyDictionary<string, string> Environment { get; }

	IReadOnlyList<AgentPaneMessage> StartupMessages { get; }
}
