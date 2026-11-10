using System.Text.Json;
using Xunit;

namespace Weavie.Workflows.Tests;

public sealed class WorkflowPromptTests : IDisposable {
	private const string Flow = """
		id: flow
		description: test flow
		stages:
		  - id: a
		    run: { prompt: "plan" }
		  - id: b
		    if: "is {{run.prompt}} large?"
		    run: { prompt: "build: {{ run.prompt }}" }
		    then:
		      ask: "did {{run.prompt}} ship?"
		      by: user
		      choices: { yes: c, no: b }
		  - id: c
		    run: { skill: ship, args: "{{run.prompt}}" }
		""";

	private static readonly WorkflowStartPrompt Prompt = new("add a picker", [new(".png", "image/png", [1, 2, 3]), new(".jpg", "image/jpeg", [4])]);

	private readonly TempDirectory _dir = new("weavie-workflow-prompt");
	private readonly RunnerPort _port = new();
	private readonly MemoryRunStore _store = new();

	public WorkflowPromptTests() {
		_dir.WriteFile("worktree/.weavie/workflows/flow.yaml", Flow);
		_dir.WriteFile("worktree/.weavie/workflows/plain.yaml", "id: plain\ndescription: d\nstages:\n  - id: a\n    run: { prompt: \"go\" }\n");
	}

	public void Dispose() => _dir.Dispose();

	private WorkflowCatalog Catalog => TestWorkflows.Catalog(_dir.Combine("worktree"), _dir.Combine("personal"));

	private WorkflowRunner Runner() => Runner(id => _dir.Combine("documents", id));

	private WorkflowRunner Runner(Func<string, string> documents) {
		var runner = new WorkflowRunner(
			Catalog, _store, new HeldDecider(), new UnusedFileReader(), _port, () => 3, () => true, () => 0.8, _dir.Combine("worktree"), TestWorkflows.Words, documents);
		runner.Activate();
		return runner;
	}

	private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement;

	private WorkflowLaunch Launch() => Catalog.Prepare("flow", Json("{}"), null, Prompt);

	[Fact]
	public void TheDefinitionKnowsWhetherItUsesThePrompt() {
		Assert.True(Catalog.List().Single(e => e.Id == "flow").Definition!.UsesPrompt);
		Assert.False(Catalog.List().Single(e => e.Id == "plain").Definition!.UsesPrompt);
	}

	[Fact]
	public void AWorkflowThatUsesThePromptCannotStartWithoutOne() {
		string message = Assert.Throws<WorkflowException>(() => Catalog.Prepare("flow", Json("{}"))).Message;
		Assert.Contains("{{run.prompt}}", message);
		Assert.Equal("flow", Catalog.Prepare("flow", Json("{}"), null, new("", Prompt.Images)).Definition.Id);
		Assert.Equal("plain", Catalog.Prepare("plain", Json("{}")).Definition.Id);
	}

	[Fact]
	public void StartSavesTheImagesUnderTheRunAndRendersThePromptEverywhere() {
		using var runner = Runner();
		var run = runner.Start(Launch());
		Assert.Equal("add a picker", run.Prompt.Text);
		Assert.Equal(
			[Path.Combine("prompt", run.Id, "image-1.png"), Path.Combine("prompt", run.Id, "image-2.jpg")],
			run.Prompt.Images.Select(image => image.File));
		Assert.Equal([1, 2, 3], File.ReadAllBytes(Path.Combine(run.DocumentsDirectory, run.Prompt.Images[0].File)));

		runner.CompleteStage(Json("{}"), "planned");
		Assert.Equal("is add a picker large?", runner.Current!.Asked!.Question);
		_port.Turns.Clear();
		runner.Jump(run.Id, "b");
		Assert.Equal("build: add a picker", _port.Turns.Single().Turn.Text);
		Assert.True(runner.TryClaimKickoff(_port.Turns.Single().Turn));
		runner.CompleteStage(Json("{}"), "built");
		Assert.Equal("did add a picker ship?", runner.Current!.Asked!.Question);
		runner.Jump(run.Id, "c");
		Assert.Equal("/ship add a picker", _port.Turns[^1].Turn.Text);
	}

	[Fact]
	public void OnlyTheFirstDeliveredTurnThatUsesThePromptCarriesItsImages() {
		using var runner = Runner();
		var run = runner.Start(Launch());
		Assert.Empty(_port.Turns.Single().Images);

		runner.Jump(run.Id, "b");
		Assert.Equal(
			run.Prompt.Images.Select(image => (Path.Combine(run.DocumentsDirectory, image.File), image.Mime)),
			_port.Turns[^1].Images.Select(image => (image.Path, image.Mime)));

		// The turn was retired before it was sent, so the next one still owes the agent the images.
		runner.Jump(run.Id, "c");
		var (sent, images) = _port.Turns[^1];
		Assert.Equal(2, images.Count);
		Assert.True(runner.TryClaimKickoff(sent));

		runner.Jump(run.Id, "b");
		Assert.Empty(_port.Turns[^1].Images);
	}

	[Fact]
	public void AClaimedTurnThatNeverReachedTheAgentStillOwesItsImages() {
		using var runner = Runner();
		var run = runner.Start(Launch());
		runner.Jump(run.Id, "b");
		var (turn, _) = _port.Turns[^1];
		Assert.True(runner.TryClaimKickoff(turn));

		runner.FailTurn(turn, "the agent refused it");
		Assert.Equal((WorkflowPhase.Paused, false), (runner.Current!.Phase, runner.Current.PromptImagesSent));
		runner.Resume(run.Id);

		Assert.Equal(2, _port.Turns[^1].Images.Count);
	}

	[Fact]
	public void AnOwedTurnIsRedeliveredWithItsImagesAfterAReload() {
		string id;
		using (var runner = Runner()) {
			id = runner.Start(Launch()).Id;
			runner.Jump(id, "b");
		}

		_port.Turns.Clear();
		using var reloaded = Runner();
		var (owed, images) = _port.Turns.Single();
		Assert.Equal(2, images.Count);
		Assert.True(File.Exists(images[0].Path));
		Assert.True(reloaded.TryClaimKickoff(owed));
		Assert.True(reloaded.Current!.PromptImagesSent);
	}

	[Fact]
	public void ARunSharingAnInheritedFolderKeepsItsImagesApart() {
		string shared = _dir.Combine("shared");
		using var first = Runner(_ => shared);
		var parent = first.Start(Launch());
		first.Cancel(parent.Id);
		var child = first.Start(Launch());
		Assert.NotEqual(parent.Prompt.Images[0].File, child.Prompt.Images[0].File);
		Assert.True(File.Exists(Path.Combine(shared, parent.Prompt.Images[0].File)));
	}

	[Fact]
	public void AStartThatCannotSaveItsImagesDoesNotStart() {
		string documents = _dir.Combine("blocked");
		_dir.WriteFile("blocked/prompt", "a file where the folder must go");
		using var runner = Runner(_ => documents);
		Assert.Contains("Could not save the prompt's images", Assert.Throws<WorkflowException>(() => runner.Start(Launch())).Message);
		Assert.Null(runner.Current);
	}

	[Fact]
	public void ARunSavedBeforeRunsHadAPromptLoadsWithNone() {
		using var runner = Runner();
		var run = runner.Start(Catalog.Prepare("plain", Json("{}")));
		using var saved = JsonDocument.Parse(WorkflowRunDocument.Write(run));
		var before = saved.RootElement.GetProperty("run").EnumerateObject()
			.Where(member => member.Name is not ("prompt" or "promptImagesSent"))
			.ToDictionary(member => member.Name, member => member.Name == "kickoff" ? Json("""{"text":"go","command":"","opens":"a"}""") : member.Value);
		var loaded = WorkflowRunDocument.Read(JsonSerializer.Serialize(new Dictionary<string, object> { ["run"] = before }))!;
		Assert.Equal(WorkflowPrompt.None, loaded.Prompt);
		Assert.False(loaded.PromptImagesSent);
		Assert.False(loaded.Kickoff!.CarriesPromptImages);
	}
}
