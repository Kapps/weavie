using Weavie.Core.Agents;
using Weavie.Core.Sessions;
using Xunit;

namespace Weavie.Core.Tests;

public sealed class ClaudeTranscriptReaderTests {
	[Fact]
	public void ProjectsTheLiveBranchOntoPromptsRepliesAndTools() {
		string jsonl = Lines(
			"""{"type":"user","uuid":"u1","parentUuid":null,"message":{"role":"user","content":"Fix the bug"}}""",
			"""{"type":"attachment","uuid":"a1","parentUuid":"u1"}""",
			"""{"type":"assistant","uuid":"x1","parentUuid":"a1","message":{"content":[{"type":"thinking","thinking":"hmm"}]}}""",
			"""{"type":"assistant","uuid":"x2","parentUuid":"x1","message":{"content":[{"type":"tool_use","name":"Bash","input":{"command":"ls","description":"List files"}}]}}""",
			"""{"type":"user","uuid":"r1","parentUuid":"x2","message":{"content":[{"type":"tool_result","tool_use_id":"t","content":"a.cs"}]}}""",
			"""{"type":"assistant","uuid":"s1","parentUuid":"r1","isSidechain":true,"message":{"content":[{"type":"text","text":"subagent"}]}}""",
			"""{"type":"user","uuid":"m1","parentUuid":"r1","isMeta":true,"message":{"content":[{"type":"text","text":"skill body"}]}}""",
			"""{"type":"assistant","uuid":"x3","parentUuid":"m1","message":{"content":[{"type":"text","text":"Fixed."}]}}""",
			"""{"type":"last-prompt","lastPrompt":"Fix the bug"}""");

		Assert.Equal([
			new(AgentTranscriptRole.User, "Fix the bug", null, null),
			new(AgentTranscriptRole.Tool, "Bash: List files", null, null),
			new AgentTranscriptEntry(AgentTranscriptRole.Agent, "Fixed.", null, null),
		], ClaudeTranscriptReader.Read(jsonl));
	}

	[Fact]
	public void SkipsAnAbandonedBranchAndFollowsCompaction() {
		string jsonl = Lines(
			"""{"type":"user","uuid":"u1","parentUuid":null,"message":{"content":"First"}}""",
			"""{"type":"assistant","uuid":"old","parentUuid":"u1","message":{"content":[{"type":"text","text":"Abandoned"}]}}""",
			"""{"type":"user","uuid":"c1","parentUuid":null,"logicalParentUuid":"u1","message":{"content":"Edited"}}""",
			"""{"type":"assistant","uuid":"x1","parentUuid":"c1","message":{"content":[{"type":"text","text":"Kept"}]}}""");

		Assert.Equal(["First", "Edited", "Kept"], ClaudeTranscriptReader.Read(jsonl).Select(entry => entry.Text));
	}

	[Fact]
	public void IgnoresALineStillBeingWritten() {
		string jsonl = Lines("""{"type":"user","uuid":"u1","message":{"content":"Done"}}""") + """{"type":"assis""";

		Assert.Equal(["Done"], ClaudeTranscriptReader.Read(jsonl).Select(entry => entry.Text));
	}

	[Fact]
	public void AMalformedCompleteLineThrows() =>
		Assert.Throws<InvalidDataException>(() => ClaudeTranscriptReader.Read(Lines("{not json")));

	private static string Lines(params string[] lines) => string.Concat(lines.Select(line => line + "\n"));
}
