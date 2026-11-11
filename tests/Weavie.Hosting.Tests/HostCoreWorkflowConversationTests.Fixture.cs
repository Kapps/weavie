using System.Text.Json;
using Weavie.AgentClientProtocol;
using Weavie.Core.Agents;
using Weavie.Core.FileSystem;
using Weavie.Core.Sessions;
using Weavie.TestSupport;
using Xunit;
using static Weavie.Hosting.Tests.HostCoreWorkflowTests;

namespace Weavie.Hosting.Tests;

public sealed partial class HostCoreWorkflowConversationTests {

	private const string Review = """
		id: conversation
		description: Review in an owned conversation.
		stages:
		  - id: review
		    run: { context: CONTEXT, prompt: review the change, outputs: { note: text } }
		    then:
		      ask: "Accept {{review.note}}?"
		      by: user
		      choices: { approve: publish, revise: review }
		  - id: publish
		    run: { prompt: "publish {{review.note}}" }
		""";

	private const string Completion = "mcp:completeStage:{\"outputs\":{\"note\":\"reviewed change\"},\"summary\":\"Checked concurrency\"}";

	private sealed class ConversationFixture(TestHost host, TempDirectory state) : IAsyncDisposable {
		public TestHost Host { get; } = host;
		public HostSession Session => Host.Session("conversation");
		public string State(string provider) => state.Combine(provider, "fake-acp-state");
		public IReadOnlyList<JsonElement> Messages => Host.Bridge.PostedEvents(Session.Address, "agent", "pane");

		public static async Task<ConversationFixture> StartAsync(string source) {
			var fixture = await CreateAsync(CommitFlow, "model-effort");
			Assert.True((await fixture.Host.CreateSessionAsync(new NewSessionRequest {
				Branch = "conversation",
				Base = "main",
				AgentProviderId = "conversation-acp",
			})).Ok);
			await Eventually(() => fixture.Session.Agent.Controls!.ControlState.Ready);
			File.WriteAllText(Path.Combine(fixture.Session.WorkspaceRoot, ".weavie", "workflows", "conversation.yaml"), source);
			fixture.Session.SendAgentPrompt("main context before review");
			await fixture.ReplyAsync(null, "echo: main context before review");
			return fixture;
		}

		public static async Task<ConversationFixture> NewWorkflowAsync(string context) {
			var fixture = await CreateAsync(repo => {
				CommitFlow(repo);
				File.WriteAllText(Path.Combine(repo, ".weavie", "workflows", "conversation.yaml"), Review.Replace("CONTEXT", context, StringComparison.Ordinal));
				TempGitRepo.Run(repo, "add", ".");
				TempGitRepo.Run(repo, "commit", "-m", "conversation workflow");
			}, "model-effort");
			Assert.True((await fixture.Host.CreateSessionAsync(new NewSessionRequest {
				Branch = "conversation",
				Base = "main",
				AgentProviderId = "conversation-acp",
				Workflow = "conversation",
			})).Ok);
			return fixture;
		}

		private static async Task<ConversationFixture> CreateAsync(Action<string> prepare, string mainMode) {
			var state = new TempDirectory("weavie-workflow-conversation");
			var host = await HostCoreWorkflowTests.StartAsync(new DecisionStub("yes", 0.9), prepare);
			var fixture = new ConversationFixture(host, state);
			fixture.Register("conversation-acp", mainMode);
			return fixture;
		}


		private void Register(string id, string mode) {
			Host.AgentProviders.Register(new AcpAgentProvider(
				new AcpAgentDefinition {
					Id = id,
					Name = id,
					Command = AcpAgentSessionFixture.ExecutablePath("tools", "Weavie.FakeAcp", "weavie-fake-acp"),
					Arguments = [],
					Environment = new Dictionary<string, string> {
						["WEAVIE_FAKE_ACP_MODE"] = mode,
						["WEAVIE_ROOT"] = state.Combine(id),
					},
				}, Host.AcpSessions, new AcpControlStore(new LocalFileSystem(), state.Combine(id + "-controls.json")), _ => { }));
		}

		public async Task<string> StartReviewAsync() {
			Session.Workflow.Start("conversation", Json("{}"));
			return await WaitForReviewAsync();
		}

		public async Task<string> StartReviewAsync(Weavie.Workflows.WorkflowStartPrompt prompt) {
			Session.Workflow.Start(Session.Workflow.Prepare("conversation", Json("{}"), null, prompt));
			return await WaitForReviewAsync();
		}

		public async Task<string> WaitForReviewAsync() {
			var started = await MessageAsync(message => Type(message) == "side-conversation-started" && Text(message) == "review");
			string id = Conversation(started)!;
			await ReplyAsync(id, "echo: review the change");
			return id;
		}

		public void Send(string id, string text) => Session.Agent.SideConversations!.ReplyAside(id, AgentTurnSubmission.Prompt(text));

		public Task<JsonElement> ReplyAsync(string? id, string text) => MessageAsync(message =>
			Type(message) == "item-completed" && Conversation(message) == id && Text(message).Contains(text, StringComparison.Ordinal));

		public async Task<JsonElement> MessageAsync(Func<JsonElement, bool> matches) {
			try {
				await Eventually(() => Messages.Any(matches));
			} catch (Xunit.Sdk.XunitException) {
				throw new Xunit.Sdk.XunitException($"Expected pane message was not emitted. Workflow: {Session.Workflow.Current?.Phase}; {Session.Workflow.Current?.PausedReason}. Received:\n" + string.Join("\n", Messages.Select(message =>
					$"{Type(message)} {Conversation(message)} {Text(message)}")));
			}
			return Messages.Last(matches);
		}

		public async Task ClosedAsync(string id) => await MessageAsync(message =>
			Type(message) == "side-conversation-closed" && Conversation(message) == id);

		public async ValueTask DisposeAsync() {
			await Host.DisposeAsync();
			state.Dispose();
		}
	}

	private static string Type(JsonElement message) => message.GetProperty("type").GetString()!;
	private static string Text(JsonElement message) => message.GetProperty("text").GetString() ?? "";
	private static string? Conversation(JsonElement message) => message.GetProperty("conversationId").GetString();
	private static string Thread(JsonElement message) => message.GetProperty("threadId").GetString()!;
	private static string Model(HostSession session) => session.Agent.Controls!.ControlState.Axes.Single(axis => axis.Id == "model").Value;
}
