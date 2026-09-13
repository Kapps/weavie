using System.Text.Json;
using Weavie.Core.Commands;
using Weavie.Core.Mcp;

namespace Weavie.Hosting;

public sealed partial class HostCore {
	private void RegisterIssueReportingHandlers(HostSession session) {
		Register(IssueReportingCommands.ReportBug, IssueReportingPrompts.ReportBug);
		Register(IssueReportingCommands.RequestFeature, IssueReportingPrompts.RequestFeature);

		void Register(string commandId, McpPrompt prompt) =>
			session.Commands.RegisterHandler(commandId, (_, ct) => _ui.InvokeAsync(() => {
				session.PrefillAgentPrompt(prompt.Text);
				return Task.FromResult(CommandResult.Success(
					"Reporting instructions prefilled. Review them and send to the agent to prepare your report.",
					JsonSerializer.Serialize(new { prompt = prompt.Text })));
			}, ct));
	}
}
