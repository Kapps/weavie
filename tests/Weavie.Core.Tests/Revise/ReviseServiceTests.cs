using System.Text.Json.Serialization.Metadata;
using Weavie.Core.Changes;
using Weavie.Core.FileActivity;
using Weavie.Core.FileSystem;
using Weavie.Core.Inference;
using Weavie.Core.Revise;
using Xunit;

namespace Weavie.Core.Tests;

/// <summary>
/// The revision lifecycle in Core: one batched query per request, a per-region write the editor can refuse, and an
/// in-flight set that is published on acceptance and emptied on every terminal outcome.
/// </summary>
public sealed class ReviseServiceTests {
	private const string Original = "// one\n// two\n// three";
	private static readonly InferenceOwner Owner = new() { Workspace = "/w" };

	private static SessionChangeTracker Staged(IFileSystem fileSystem) {
		fileSystem.WriteAllText("/w/a.cs", "code\n");
		var tracker = new SessionChangeTracker(
			fileSystem, NoopFileActivitySink.Instance, "/w", path => path.StartsWith("/w", StringComparison.Ordinal));
		tracker.CaptureBaseline("/w/a.cs");
		fileSystem.WriteAllText("/w/a.cs", "// one\n// two\n// three\ncode\n");
		tracker.RecordChange("/w/a.cs");
		return tracker;
	}

	private static ReviseTarget Target() =>
		new() { Path = "/w/a.cs", Range = new LineRange(1, 4), OriginalText = Original };

	private static Task<IReadOnlyList<ReviseResult>> Run(ReviseService service, params ReviseTarget[] targets) =>
		service.RunAsync(Owner, targets, "Shorten it.", InferenceInvocationOrigin.UserInitiated, CancellationToken.None);

	[Fact]
	public async Task RunAsync_Applied_WritesRevisionAndEmptiesInFlight() {
		var fileSystem = new InMemoryFileSystem();
		var tracker = Staged(fileSystem);
		var surface = new FakeSurface();
		var service = new ReviseService(new FakeInference(() => Reply((1, "// short"))), tracker, surface);

		var results = await Run(service, Target());

		Assert.Equal(ReviseOutcome.Applied, Assert.Single(results).Outcome);
		Assert.Equal("// short\ncode\n", fileSystem.ReadAllText("/w/a.cs"));
		Assert.Empty(service.InFlight);
		// Published once with the region in flight, then once empty when it retired.
		Assert.Single(surface.Published[0]);
		Assert.Empty(surface.Published[^1]);
		Assert.Empty(surface.Failures);
	}

	[Fact]
	public async Task RunAsync_QueryFailed_ReportsEveryRegionAndWritesNothing() {
		var fileSystem = new InMemoryFileSystem();
		var service = new ReviseService(
			new FakeInference(() => new InferenceFailure<ReviseQueryOutput> {
				Kind = InferenceFailureKind.TimedOut,
				Detail = "the attempt exceeded its time budget",
			}),
			Staged(fileSystem),
			new FakeSurface());

		var results = await Run(service, Target());

		var result = Assert.Single(results);
		Assert.Equal(ReviseOutcome.QueryFailed, result.Outcome);
		Assert.Equal("the attempt exceeded its time budget", result.Reason);
		Assert.Equal("// one\n// two\n// three\ncode\n", fileSystem.ReadAllText("/w/a.cs"));
	}

	[Fact]
	public async Task RunAsync_RailThrows_StillReachesTheUser() {
		var fileSystem = new InMemoryFileSystem();
		var surface = new FakeSurface();
		var service = new ReviseService(
			new FakeInference(() => throw new ArgumentException("bounds must be positive")),
			Staged(fileSystem),
			surface);

		// The host runs this detached, so an escaping throw would leave the tint vanishing with no edit, no
		// toast, and no trace the user can see.
		var results = await Run(service, Target());

		Assert.Equal(ReviseOutcome.QueryFailed, Assert.Single(results).Outcome);
		Assert.Equal("bounds must be positive", Assert.Single(surface.Failures));
		Assert.Empty(service.InFlight);
	}

	[Fact]
	public async Task RunAsync_ConfirmThrows_ReportsFailureWithoutWriting() {
		var fileSystem = new InMemoryFileSystem();
		var surface = new FakeSurface { Confirm = () => throw new InvalidOperationException("editor probe failed") };
		var service = new ReviseService(new FakeInference(() => Reply((1, "// short"))), Staged(fileSystem), surface);

		Assert.Equal(ReviseOutcome.WriteFailed, Assert.Single(await Run(service, Target())).Outcome);
		Assert.Equal("editor probe failed", Assert.Single(surface.Failures));
		Assert.Equal(Original + "\ncode\n", fileSystem.ReadAllText("/w/a.cs"));
		Assert.Empty(service.InFlight);
		Assert.Empty(surface.Published[^1]);
	}

	[Fact]
	public async Task RunAsync_CancelledDuringConfirmation_RetiresWithoutWritingOrReportingFailure() {
		using var cancellation = new CancellationTokenSource();
		var fileSystem = new InMemoryFileSystem();
		var surface = new FakeSurface { Confirm = cancellation.Cancel };
		var service = new ReviseService(new FakeInference(() => Reply((1, "// short"))), Staged(fileSystem), surface);

		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.RunAsync(
			Owner, [Target()], "Shorten it.", InferenceInvocationOrigin.UserInitiated, cancellation.Token));

		Assert.Equal(Original + "\ncode\n", fileSystem.ReadAllText("/w/a.cs"));
		Assert.Empty(surface.Failures);
		Assert.Empty(service.InFlight);
		Assert.Empty(surface.Published[^1]);
	}

	[Fact]
	public async Task RunAsync_OverlappingTarget_RefusedWithoutQuerying() {
		var fileSystem = new InMemoryFileSystem();
		var inference = new FakeInference(() => Reply((1, "// short")));
		var surface = new FakeSurface();
		var service = new ReviseService(inference, Staged(fileSystem), surface);

		// The second target covers lines the first already claims, so only one region reaches the model.
		var results = await Run(
			service, Target(), new ReviseTarget { Path = "/w/a.cs", Range = new LineRange(2, 3), OriginalText = "// two" });

		Assert.Equal(ReviseOutcome.Applied, results[0].Outcome);
		Assert.Equal(ReviseOutcome.AlreadyInFlight, results[1].Outcome);
		Assert.Equal(1, inference.Calls);
		// The refusal reaches the user; the caller discards the results, so the surface is the only channel.
		Assert.Equal("that region is already being revised", Assert.Single(surface.Failures));
	}

	[Fact]
	public async Task RunAsync_SurfaceRefuses_WritesNothing() {
		var fileSystem = new InMemoryFileSystem();
		var surface = new FakeSurface { Refusal = "unsaved changes" };
		var service = new ReviseService(new FakeInference(() => Reply((1, "// short"))), Staged(fileSystem), surface);

		var results = await Run(service, Target());

		var result = Assert.Single(results);
		Assert.Equal(ReviseOutcome.Declined, result.Outcome);
		Assert.Equal("unsaved changes", result.Reason);
		Assert.Equal("// one\n// two\n// three\ncode\n", fileSystem.ReadAllText("/w/a.cs"));
		Assert.Equal("unsaved changes", Assert.Single(surface.Failures));
	}

	[Fact]
	public async Task RunAsync_GuardMismatch_ReportsChanged() {
		var fileSystem = new InMemoryFileSystem();
		var tracker = Staged(fileSystem);
		// The region's text changes while the query is in flight, so the captured guard no longer matches.
		var service = new ReviseService(
			new FakeInference(() => {
				fileSystem.WriteAllText("/w/a.cs", "// mine\n// two\n// three\ncode\n");
				return Reply((1, "// short"));
			}),
			tracker,
			new FakeSurface());

		var results = await Run(service, Target());

		Assert.Equal(ReviseOutcome.Changed, Assert.Single(results).Outcome);
		Assert.Equal("// mine\n// two\n// three\ncode\n", fileSystem.ReadAllText("/w/a.cs"));
	}

	[Fact]
	public async Task RunAsync_MissingId_NotProposed() {
		var fileSystem = new InMemoryFileSystem();
		var service = new ReviseService(new FakeInference(() => Reply((99, "// short"))), Staged(fileSystem), new FakeSurface());

		var results = await Run(service, Target());

		Assert.Equal(ReviseOutcome.NotProposed, Assert.Single(results).Outcome);
		Assert.Equal("// one\n// two\n// three\ncode\n", fileSystem.ReadAllText("/w/a.cs"));
	}

	[Fact]
	public async Task RunAsync_DuplicateId_NotProposed() {
		var fileSystem = new InMemoryFileSystem();
		var service = new ReviseService(
			new FakeInference(() => Reply((1, "// short"), (1, "// other"))), Staged(fileSystem), new FakeSurface());

		// Nothing in the reply says which entry belongs to the region, so neither is usable.
		var results = await Run(service, Target());

		Assert.Equal(ReviseOutcome.NotProposed, Assert.Single(results).Outcome);
		Assert.Equal("// one\n// two\n// three\ncode\n", fileSystem.ReadAllText("/w/a.cs"));
	}

	[Fact]
	public async Task RunAsync_IdenticalText_UnchangedWithNoWrite() {
		var fileSystem = new InMemoryFileSystem();
		var surface = new FakeSurface { Refusal = "would have been asked" };
		var service = new ReviseService(new FakeInference(() => Reply((1, Original))), Staged(fileSystem), surface);

		var results = await Run(service, Target());

		// A no-op never reaches the editor for confirmation, but the user is still told why nothing changed —
		// the tint vanishing with the text untouched would otherwise read as a silent failure.
		Assert.Equal(ReviseOutcome.Unchanged, Assert.Single(results).Outcome);
		Assert.Equal("the model returned it unchanged", Assert.Single(surface.Failures));
	}

	[Fact]
	public async Task RunAsync_InstructionTravelsInsideTheTypedInput() {
		var fileSystem = new InMemoryFileSystem();
		var inference = new FakeInference(() => Reply((1, "// short")));

		await Run(new ReviseService(inference, Staged(fileSystem), new FakeSurface()), Target());

		// The instruction and the region text ride inside the serialized input, behind the untrusted-data framing.
		Assert.Contains("Shorten it.", inference.LastPrompt, StringComparison.Ordinal);
		Assert.Contains("// one", inference.LastPrompt, StringComparison.Ordinal);
	}

	private static InferenceSuccess<ReviseQueryOutput> Reply(params (int Id, string Text)[] regions) => new() {
		Value = new ReviseQueryOutput {
			Regions = [.. regions.Select(region => new ReviseQueryRevision { Id = region.Id, Text = region.Text })],
		},
		Receipt = new InferenceReceipt {
			ProviderId = "claude",
			Category = InferenceModelCategory.Utility,
			ModelId = "test",
			Duration = TimeSpan.Zero,
		},
	};

	private sealed class FakeInference : IInferenceService {
		private readonly Func<InferenceResult<ReviseQueryOutput>> _reply;

		public FakeInference(Func<InferenceResult<ReviseQueryOutput>> reply) {
			_reply = reply;
		}

		public int Calls { get; private set; }

		public string LastPrompt { get; private set; } = string.Empty;

		public Task<InferenceResult<TResponse>> QueryAsync<TResponse>(
			InferenceOwner owner,
			InferenceModelCategory category,
			InferenceInput input,
			JsonTypeInfo<TResponse> responseType,
			InferenceQueryOptions options,
			CancellationToken ct) {
			Calls++;
			LastPrompt = input.Prompt;
			return Task.FromResult((InferenceResult<TResponse>)(object)_reply());
		}
	}

	private sealed class FakeSurface : IReviseSurface {
		public List<IReadOnlyList<ReviseRegion>> Published { get; } = [];

		public List<string> Failures { get; } = [];

		public string? Refusal { get; init; }

		public Action Confirm { get; init; } = () => { };

		public void Publish(IReadOnlyList<ReviseRegion> inFlight) => Published.Add(inFlight);

		public Task<string?> ConfirmAsync(ReviseRegion region, CancellationToken cancellationToken) {
			Confirm();
			return Task.FromResult(Refusal);
		}

		public void Failed(ReviseRegion region, string reason) => Failures.Add(reason);
	}
}
