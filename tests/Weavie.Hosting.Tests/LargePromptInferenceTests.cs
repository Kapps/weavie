using System.Text.Json;
using Weavie.AgentClientProtocol;
using Weavie.Core.Configuration;
using Weavie.Core.FileSystem;
using Weavie.Core.Inference;
using Weavie.Core.Sessions;
using Weavie.Hosting.Inference;
using Weavie.Hosting.Inference.Claude;
using Xunit;

namespace Weavie.Hosting.Tests;

public sealed class LargePromptInferenceTests : IDisposable {
	private readonly TempDirectory _dir = new("weavie-large-inference");
	private readonly SettingsStore _settings;

	public LargePromptInferenceTests() {
		_settings = CoreSettings.CreateStore(_dir.Combine("settings.toml"), enableWatcher: false);
	}

	[Theory]
	[InlineData(false, false)]
	[InlineData(false, true)]
	[InlineData(true, false)]
	[InlineData(true, true)]
	public async Task EntireLargePromptReachesTheChildBeforeItAcceptsOrRejects(bool acp, bool reject) {
		string prompt = "start 🧶\n" + new string('界', 1024 * 1024) + "\nend of prompt 🧶";
		if (reject) _dir.WriteFile("inference-reject", "reject");
		IInferenceProvider provider = acp ? Acp() : Claude();
		var request = new InferenceProviderRequest {
			Category = InferenceModelCategory.Utility,
			Profile = new InferenceProviderProfile { Model = "", Effort = "", FastMode = InferenceFastMode.Inherit },
			Workspace = _dir.Path,
			Prompt = prompt,
			Images = [],
			OutputSchemaJson = "{\"type\":\"object\",\"properties\":{\"accept\":{\"type\":\"number\"},\"revise\":{\"type\":\"number\"}}}",
			MaxOutputBytes = 1024,
		};

		var result = await provider.QueryInferenceAsync(request, CancellationToken.None);

		string received = await File.ReadAllTextAsync(_dir.Combine("inference-receipt.txt"));
		if (acp) {
			Assert.StartsWith(prompt, received, StringComparison.Ordinal);
			Assert.EndsWith(request.OutputSchemaJson, received, StringComparison.Ordinal);
		} else {
			Assert.Equal(prompt, received);
		}
		if (reject) {
			var failure = Assert.IsType<InferenceProviderFailure>(result);
			Assert.Equal(InferenceFailureKind.ProviderUnavailable, failure.Kind);
			Assert.Contains(acp ? "provider rejected" : "exited with code 7", failure.Detail, StringComparison.Ordinal);
		} else {
			var success = Assert.IsType<InferenceProviderSuccess>(result);
			using var output = JsonDocument.Parse(success.OutputJson);
			Assert.Equal(1, output.RootElement.GetProperty("accept").GetDouble());
		}
	}

	private AcpAgentProvider Acp() => new(
		new AcpAgentDefinition {
			Id = "fake",
			Name = "Fake ACP",
			Command = AcpAgentSessionFixture.ExecutablePath("tools", "Weavie.FakeAcp", "weavie-fake-acp"),
			Arguments = ["inference", "workflow"],
			Environment = new Dictionary<string, string>(StringComparer.Ordinal),
		},
		new AcpSessionStore(_dir.Combine("sessions")),
		new AcpControlStore(new LocalFileSystem(), _dir.Combine("controls")),
		_ => { });

	private ClaudeCliInference Claude() {
		_settings.Set(CoreSettings.ClaudePath, JsonSerializer.SerializeToElement(TestNode.Command));
		string script = _dir.WriteFile("inference.mjs", """
			import { writeFileSync, existsSync } from 'node:fs';
			process.stdin.setEncoding('utf8');
			let prompt = '';
			for await (const chunk of process.stdin) prompt += chunk;
			writeFileSync('inference-receipt.txt', prompt, 'utf8');
			if (existsSync('inference-reject')) process.exitCode = 7;
			else console.log(JSON.stringify({ is_error: false, structured_output: { accept: 1, revise: 0 } }));
			""");
		return new ClaudeCliInference(_settings, new ScriptRunner(script), _dir.Combine("images"));
	}

	private sealed class ScriptRunner(string script) : IAgentCliProcessRunner {
		public Task<AgentCliProcessResult> RunAsync(AgentCliProcessRequest request, CancellationToken ct) =>
			new AgentCliProcessRunner().RunAsync(request with { Arguments = [script, .. request.Arguments] }, ct);
	}

	public void Dispose() {
		_settings.Dispose();
		_dir.Dispose();
	}
}
