using Weavie.AgentClientProtocol;
using Weavie.Core.Agents;
using Weavie.Core.FileSystem;
using Weavie.Core.Sessions;
using Xunit;

namespace Weavie.Hosting.Tests;

public sealed class AcpConsultTests : IDisposable {
	private readonly TempDirectory _workspace = new("weavie-acp-consult-tests");

	public void Dispose() => _workspace.Dispose();

	[Fact]
	public async Task ConsultsInTheWorktreeWithTheRequestedModelAndNoWeavieSurface() {
		var result = Assert.IsType<AgentConsultSuccess>(await Consult("ok", "astra"));

		Assert.Equal("astra", result.ModelId);
		Assert.Equal(
			$"cwd={Path.GetFullPath(_workspace.Path)};mcp=0;fs=False;terminal=False;model=astra;framed=True;asked=Review it.",
			result.Reply);
		Assert.Empty(result.Denied);
		Assert.Contains(result.Controls.Single().Options, option => option.Id == "astra");
	}

	[Fact]
	public async Task KeepsTheAgentDefaultWhenNoModelIsRequested() {
		var result = Assert.IsType<AgentConsultSuccess>(await Consult("ok", string.Empty));

		Assert.Equal("fake-model", result.ModelId);
	}

	[Fact]
	public async Task UnadvertisedModelFailsListingWhatIsAdvertised() {
		var result = Assert.IsType<AgentConsultFailure>(await Consult("ok", "nova"));

		Assert.Contains("'nova'", result.Detail, StringComparison.Ordinal);
		Assert.Contains("fake-model (Fake Model), astra (Astra)", result.Detail, StringComparison.Ordinal);
		Assert.NotEmpty(result.Controls);
	}

	[Fact]
	public async Task RejectsPermissionRequestsWithTheAgentsOwnRejectOnceOption() {
		var result = Assert.IsType<AgentConsultSuccess>(await Consult("permission", string.Empty));

		Assert.Equal("permission=no", result.Reply);
		Assert.Equal(["Run the tests"], result.Denied);
	}

	[Fact]
	public async Task EarlyStopFails() {
		var result = Assert.IsType<AgentConsultFailure>(await Consult("max-tokens", string.Empty));

		Assert.Contains("'max_tokens'", result.Detail, StringComparison.Ordinal);
	}

	[Fact]
	public async Task AuthenticationDemandFailsNamingTheFix() {
		var result = Assert.IsType<AgentConsultFailure>(await Consult("auth", string.Empty));

		Assert.Contains("requires authentication", result.Detail, StringComparison.Ordinal);
	}

	[Fact]
	public async Task CancellationStopsAHungConsult() {
		using var cancel = new CancellationTokenSource();
		var consult = Consult("hang", string.Empty, cancel.Token);
		await Task.Delay(200);
		await cancel.CancelAsync();

		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => consult);
	}

	[Fact]
	public async Task ProbeReturnsTheAdvertisedControls() {
		var controls = await Provider("ok").ProbeControlsAsync(CancellationToken.None);

		Assert.Equal(["fake-model", "astra"], controls.Single().Options.Select(option => option.Id));
	}

	[Fact]
	public async Task ProbeThrowsWhenTheAgentDemandsAuthentication() {
		var error = await Assert.ThrowsAsync<InvalidOperationException>(
			() => Provider("auth").ProbeControlsAsync(CancellationToken.None));

		Assert.Contains("requires authentication", error.Message, StringComparison.Ordinal);
	}

	private Task<AgentConsultOutcome> Consult(string variant, string model) =>
		Consult(variant, model, CancellationToken.None);

	private Task<AgentConsultOutcome> Consult(string variant, string model, CancellationToken ct) =>
		Provider(variant).ConsultAsync(
			new AgentConsultRequest { Workspace = _workspace.Path, Model = model, Prompt = "Review it." },
			ct);

	private static AcpAgentProvider Provider(string variant) => new(
		new AcpAgentDefinition {
			Id = "fake",
			Name = "Fake ACP",
			Command = AcpAgentSessionFixture.ExecutablePath("tools", "Weavie.FakeAcp", "weavie-fake-acp"),
			Arguments = ["consult", variant],
			Environment = new Dictionary<string, string>(StringComparer.Ordinal),
		},
		new AcpSessionStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("n"))),
		new AcpControlStore(new LocalFileSystem(), Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("n"))),
		_ => { });
}
