using System.Text;
using System.Text.Json;
using Weavie.Core.Corrections;
using Weavie.Core.FileSystem;
using Xunit;

namespace Weavie.Core.Tests.Corrections;

/// <summary>
/// The corrections-analysis query: the repository instructions it carries (ad-hoc inference has no tools, so the
/// prompt is the model's only view of the existing rules) and the bounds it declares.
/// </summary>
public sealed class CorrectionLessonsTests {
	private const string Root = "/repo";

	[Fact]
	public void ReadInstructions_CarriesBothRootInstructionFiles() {
		var fs = new InMemoryFileSystem();
		fs.WriteAllText("/repo/AGENTS.md", "no fallbacks");
		fs.WriteAllText("/repo/CLAUDE.md", "minimize lines");

		string instructions = CorrectionLessons.ReadInstructions(fs, Root);

		Assert.Contains("# AGENTS.md\n\nno fallbacks", instructions, StringComparison.Ordinal);
		Assert.Contains("# CLAUDE.md\n\nminimize lines", instructions, StringComparison.Ordinal);
	}

	[Fact]
	public void ReadInstructions_WithNoInstructionFiles_IsEmpty() =>
		Assert.Equal(string.Empty, CorrectionLessons.ReadInstructions(new InMemoryFileSystem(), Root));

	[Fact]
	public void ReadInstructions_IsBoundedToItsContextBudget() {
		var fs = new InMemoryFileSystem();
		fs.WriteAllText("/repo/AGENTS.md", new string('r', CorrectionLessons.MaxInstructionsBytes * 2));

		string instructions = CorrectionLessons.ReadInstructions(fs, Root);

		Assert.True(Encoding.UTF8.GetByteCount(instructions) <= CorrectionLessons.MaxInstructionsBytes);
		Assert.EndsWith("…[truncated]", instructions, StringComparison.Ordinal);
	}

	[Fact]
	public void BuildPrompt_CarriesTheCorrectionsAndInstructionsAsJsonInput() {
		string prompt = CorrectionLessons.BuildPrompt(new CorrectionLessonsInput {
			ExistingInstructions = "no fallbacks",
			Corrections = [
				new CorrectionRecord {
					Prompt = "add a cache",
					Files = [new CorrectionFile { Path = "a.cs", Delta = "-cached\n+live" }],
				},
			],
		});

		const string marker = "Input JSON:\n";
		var input = JsonDocument.Parse(prompt[(prompt.IndexOf(marker, StringComparison.Ordinal) + marker.Length)..]).RootElement;
		Assert.Equal("no fallbacks", input.GetProperty("existingInstructions").GetString());
		var correction = Assert.Single(input.GetProperty("corrections").EnumerateArray());
		Assert.Equal("add a cache", correction.GetProperty("prompt").GetString());
		Assert.Equal("-cached\n+live", Assert.Single(correction.GetProperty("files").EnumerateArray()).GetProperty("delta").GetString());
	}

	[Fact]
	public void QueryOptions_AreNeverTrippedByAFullRingAndFullInstructions() {
		// The declared prompt bound is derived from the ring cap and the instructions cap, so a normal corpus can
		// never be rejected as oversized — a bound the feature could routinely hit would be a hidden failure.
		var options = CorrectionLessons.QueryOptions;

		Assert.True(options.MaxPromptBytes > (2 * CorrectionCorpus.MaxBytes) + (2 * CorrectionLessons.MaxInstructionsBytes));
		Assert.Equal(0, options.MaxImageCount);
	}
}
