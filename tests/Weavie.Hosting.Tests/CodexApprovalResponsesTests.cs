using System.Text.Json;
using Weavie.Hosting.Agents.Codex;
using Xunit;

namespace Weavie.Hosting.Tests;

public sealed class CodexApprovalResponsesTests {
	[Theory]
	[InlineData("item/commandExecution/requestApproval")]
	[InlineData("item/fileChange/requestApproval")]
	[InlineData("item/permissions/requestApproval")]
	public void IsPermissionApproval_IncludesOnlyPermissionRequests(string method) =>
		Assert.True(CodexApprovalResponses.IsPermissionApproval(method));

	[Fact]
	public void IsPermissionApproval_ExcludesMcpElicitation() {
		Assert.False(CodexApprovalResponses.IsPermissionApproval("mcpServer/elicitation/request"));
		Assert.True(CodexApprovalResponses.CanResolve("mcpServer/elicitation/request"));
	}

	[Fact]
	public void Build_AcceptsCommandApproval() {
		using var doc = JsonDocument.Parse("{} ");
		var request = new CodexServerRequest(
			"approval-1",
			"approval-1",
			"item/commandExecution/requestApproval",
			doc.RootElement.Clone());

		string json = JsonSerializer.Serialize(CodexApprovalResponses.Build(request, "accept"));

		using var result = JsonDocument.Parse(json);
		Assert.Equal("accept", result.RootElement.GetProperty("decision").GetString());
	}
}
