using System.Text.Json;
using Weavie.Core.Agents;
using Weavie.Core.FileSystem;
using Xunit;

namespace Weavie.Core.Tests;

public sealed class AgentWalkthroughRulesTests {
	private const string Claims = "class Claims {\n\tDictionary staged;\n\tstring Stage(op) {\n\t\tstring token = Random();\n\t\treturn token;\n\t}\n}\n";
	private readonly InMemoryFileSystem _files = new();
	private readonly HashSet<string> _edited = [];

	public AgentWalkthroughRulesTests() {
		_files.WriteAllText("/repo/Claims.cs", Claims);
	}

	private WalkthroughReads Reads() => new WalkthroughCapture(_files, "/repo", _edited.Contains).Begin();

	private static AgentWalkthroughRequest Request(string json) =>
		JsonSerializer.Deserialize(json, AgentBlockJson.Default.AgentWalkthroughRequest)!;

	private const string Published = """
		{"id":"claims","title":"How claims work","problem":"Two connections.","gist":"A ticket.","example":"showBlock p95",
		 "stops":[{"id":"stage","name":"Stage","title":"Holding","lead":"Read it.",
		   "code":[{"path":"Claims.cs","from":1,"to":7,"folds":[{"from":2,"to":2,"label":"the map"}],
		            "notes":[{"line":4,"token":"Random()","text":"16 random bytes"}]}],
		   "state":{"cells":[{"label":"Held","value":"p95"}]}}]}
		""";

	private AgentBlock Publish() {
		var show = AgentWalkthroughRules.Publish(Request(Published), null, Reads());
		return show.Block;
	}

	[Fact]
	public void PublishChecksEveryAnchorAndKeepsTheCopy() {
		_edited.Add("/repo/Claims.cs");
		var show = AgentWalkthroughRules.Publish(Request(Published), null, Reads());
		string hash = WorktreeTextFile.HashOf(Claims);

		var walkthrough = show.Block.Walkthrough!;
		Assert.Equal("walkthrough", show.Block.Kind);
		Assert.Equal(Claims, show.Sources[hash]);
		Assert.Equal(new AgentWalkthroughFile { Path = "Claims.cs", Before = hash, Late = true }, Assert.Single(walkthrough.Files));
		var code = Assert.Single(walkthrough.Stops[0].Code);
		Assert.Equal(hash, code.Source);
		Assert.Equal(4, Assert.Single(code.Notes!).Line);
	}

	[Theory]
	[InlineData("""{"line":4,"token":"Stage(","text":"t"}""", "stops[0].code[0].notes[0]: line 4 of 'Claims.cs' does not contain 'Stage('. Line 4 is \"string token = Random();\". 'Stage(' is on line 3.")]
	[InlineData("""{"line":2,"token":"staged","text":"t"}""", "stops[0].code[0].notes[0]: line 2 is inside the fold at lines 2-2. A note sits on a line that is shown.")]
	[InlineData("""{"line":9,"token":"x","text":"t"}""", "stops[0].code[0].notes[0]: line 9 is outside the block's lines 1-7.")]
	[InlineData("""{"line":4,"token":"Missing","text":"t"}""", "stops[0].code[0].notes[0]: line 4 of 'Claims.cs' does not contain 'Missing'. Line 4 is \"string token = Random();\". 'Missing' is not in lines 1-7.")]
	public void NotesAreCheckedAgainstTheirLine(string note, string message) {
		string json = Published.Replace("""{"line":4,"token":"Random()","text":"16 random bytes"}""", note, StringComparison.Ordinal);
		Assert.Equal(message, Assert.Throws<AgentBlockException>(() => AgentWalkthroughRules.Publish(Request(json), null, Reads())).Message);
	}

	[Theory]
	[InlineData("\"from\":1,\"to\":7", "\"from\":1,\"to\":30", "stops[0].code[0]: lines 1-30 are outside 'Claims.cs', which has 8 line(s).")]
	[InlineData("\"path\":\"Claims.cs\"", "\"path\":\"../etc/passwd\"", "stops[0].code[0].path: '../etc/passwd' isn't a file inside /repo.")]
	[InlineData("\"path\":\"Claims.cs\"", "\"path\":\"Gone.cs\"", "stops[0].code[0].path: 'Gone.cs' doesn't exist.")]
	[InlineData("{\"from\":2,\"to\":2,\"label\":\"the map\"}", "{\"from\":2,\"to\":2,\"label\":\"a\"},{\"from\":3,\"to\":3,\"label\":\"b\"}", "stops[0].code[0].folds[1]: lines 3-3 touch the fold at lines 2-2; join them.")]
	[InlineData("{\"from\":2,\"to\":2,\"label\":\"the map\"}", "{\"from\":0,\"to\":2,\"label\":\"a\"}", "stops[0].code[0].folds[0]: lines 0-2 must lie inside the block's lines 1-7, after its first line.")]
	[InlineData("\"example\":\"showBlock p95\",", "", "Stop 'stage' has a 'state', which follows the walkthrough's 'example'; give the example.")]
	[InlineData("\"name\":\"Stage\",", "", "stops[0].name is required.")]
	public void RefusalsNameThePathAndTheRealText(string from, string to, string message) {
		string json = Published.Replace(from, to, StringComparison.Ordinal);
		Assert.Equal(message, Assert.Throws<AgentBlockException>(() => AgentWalkthroughRules.Publish(Request(json), null, Reads())).Message);
	}

	[Fact]
	public void ReasonsFromAReadingCarryNoReference() {
		string json = Published.Replace("\"state\":", "\"why\":{\"title\":\"Why?\",\"text\":\"t\",\"source\":{\"kind\":\"reading\",\"ref\":\"x\"}},\"state\":", StringComparison.Ordinal);
		Assert.Equal("stops[0].why.source: a reading has no 'ref'.",
			Assert.Throws<AgentBlockException>(() => AgentWalkthroughRules.Publish(Request(json), null, Reads())).Message);
	}

	[Fact]
	public void AnUpdateChangesStopsAndRecordsEveryFileAsItStands() {
		var block = Publish();
		string edited = Claims.Replace("Random()", "Ticket()", StringComparison.Ordinal);
		_files.WriteAllText("/repo/Claims.cs", edited);
		var update = AgentWalkthroughRules.Update(Request("""
			{"id":"claims","change":"Tickets are named.",
			 "stops":[{"id":"stage","effect":"changed","note":"Was: random.",
			   "code":[{"path":"Claims.cs","from":3,"to":6,"notes":[{"line":4,"token":"Ticket()","text":"a named ticket"}]}]},
			  {"id":"take","after":"stage","name":"Take","title":"Taking","lead":"Read it.","code":[{"path":"Claims.cs","from":5,"to":5}]}]}
			"""), block, Reads());

		var next = AgentWalkthroughRules.Apply(block, update, 42)!;

		var walkthrough = next.Walkthrough!;
		Assert.Equal(42, next.UpdatedAtMs);
		Assert.Equal("Tickets are named.", walkthrough.Change);
		Assert.Equal(["stage", "take"], walkthrough.Stops.Select(stop => stop.Id));
		Assert.Equal(("changed", "added"), (walkthrough.Stops[0].Effect, walkthrough.Stops[1].Effect));
		Assert.Equal("Holding", walkthrough.Stops[0].Title);
		var file = Assert.Single(walkthrough.Files);
		Assert.Equal((WorktreeTextFile.HashOf(Claims), WorktreeTextFile.HashOf(edited)), (file.Before, file.After));
		Assert.Equal(edited, update.Sources[file.After!]);
		Assert.Null(Assert.Single(walkthrough.Stops[0].Code[0].Notes!).Was);
	}

	[Fact]
	public void ARewordedNoteKeepsWhatItSaidFirst() {
		var block = Publish();
		AgentBlock Reword(AgentBlock current, string text) => AgentWalkthroughRules.Apply(current, AgentWalkthroughRules.Update(Request($$"""
			{"id":"claims","change":"c","stops":[{"id":"stage","effect":"affected","note":"n",
			  "code":[{"path":"Claims.cs","from":1,"to":7,"notes":[{"line":4,"token":"Random()","text":"{{text}}"}]}]}]}
			"""), current, Reads()), 1)!;

		var once = Reword(block, "now named");
		var twice = Reword(once, "now numbered");

		Assert.Equal("16 random bytes", once.Walkthrough!.Stops[0].Code[0].Notes![0].Was);
		Assert.Equal("16 random bytes", twice.Walkthrough!.Stops[0].Code[0].Notes![0].Was);
		Assert.Null(Reword(block, "16 random bytes").Walkthrough!.Stops[0].Code[0].Notes![0].Was);
	}

	[Fact]
	public void ADeletedFileRecordsAsEmpty() {
		var block = Publish();
		_files.DeleteFile("/repo/Claims.cs");

		var next = AgentWalkthroughRules.Apply(block, AgentWalkthroughRules.Update(Request("""{"id":"claims","change":"Removed."}"""), block, Reads()), 1)!;

		Assert.Equal("", Assert.Single(next.Walkthrough!.Files).After);
	}

	[Theory]
	[InlineData("""{"id":"claims"}""", "change is required.")]
	[InlineData("""{"id":"other","change":"c"}""", "Walkthrough 'other' does not exist in this conversation. Publish it with publishWalkthrough.")]
	[InlineData("""{"id":"claims","change":"c","stops":[{"id":"stage","note":"n"}]}""", "stops[0].effect must be changed, affected.")]
	[InlineData("""{"id":"claims","change":"c","stops":[{"id":"stage","effect":"changed"}]}""", "stops[0].note is required.")]
	[InlineData("""{"id":"claims","change":"c","stops":[{"id":"gone","note":"n"}]}""", "stops[0]: stop 'gone' does not exist, so it needs 'name', 'title', 'lead' and 'code' to be added.")]
	[InlineData("""{"id":"claims","change":"c","stops":[{"id":"x","after":"nope","name":"n","title":"t","lead":"l","code":[{"path":"Claims.cs","from":1,"to":1}]}]}""", "stops[0].after: there is no stop 'nope' before this one.")]
	public void UpdateRefusalsSayWhatIsMissing(string json, string message) {
		var block = Publish();
		var request = Request(json);
		Assert.Equal(message, Assert.Throws<AgentBlockException>(() => AgentWalkthroughRules.Update(request, request.Id == block.Id ? block : null, Reads())).Message);
	}

	[Fact]
	public void AChangeToAStopThatVanishedDropsTheUpdate() {
		var block = Publish();
		var update = AgentWalkthroughRules.Update(Request("""{"id":"claims","change":"c","stops":[{"id":"stage","effect":"affected","note":"n"}]}"""), block, Reads());
		var without = block with { Walkthrough = block.Walkthrough! with { Stops = [] } };

		Assert.Null(AgentWalkthroughRules.Apply(without, update, 1));
		Assert.Null(AgentWalkthroughRules.Apply(null, update, 1));
	}

	[Fact]
	public void BlockToolsRefuseAWalkthroughByName() {
		var block = Publish();

		Assert.Equal("Block 'claims' is a walkthrough; change it with updateWalkthrough.",
			Assert.Throws<AgentBlockException>(() => AgentBlockRules.Validate(new() { Id = "claims", Append = new() { Keep = 1, Rows = [] } }, block, show: false)).Message);
		Assert.Equal("Block 'claims' is already shown; change it with updateWalkthrough.",
			Assert.Throws<AgentBlockException>(() => AgentWalkthroughRules.Publish(Request(Published), block, Reads())).Message);
		Assert.True(AgentBlockRules.NamesUpdate("mcp__weavie__updateWalkthrough"));
	}

	[Fact]
	public void ACitedFileHasASizeLimit() {
		_files.WriteAllText("/repo/Big.cs", new string('x', AgentWalkthroughLimits.FileBytes + 1));

		Assert.StartsWith("stops[0].code[0].path: 'Big.cs' is 2,097,153 bytes;",
			Assert.Throws<AgentBlockException>(() => AgentWalkthroughRules.Publish(Request(Published.Replace("Claims.cs", "Big.cs", StringComparison.Ordinal)), null, Reads())).Message);
	}
}
