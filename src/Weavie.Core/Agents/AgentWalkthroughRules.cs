using Weavie.Core.FileSystem;
using static Weavie.Core.Agents.AgentBlockRules;

namespace Weavie.Core.Agents;

/// <summary>
/// Checks walkthrough tool requests against the files they cite and applies updates at the commit. Every refusal
/// names the path into the arguments and the real text, so the agent can correct itself.
/// </summary>
public static class AgentWalkthroughRules {
	/// <summary>The tool that publishes a walkthrough.</summary>
	public const string PublishTool = "publishWalkthrough";
	/// <summary>Where an explanation can come from.</summary>
	public static readonly IReadOnlyList<string> SourceKinds = ["spec", "pr", "commit", "reading"];
	/// <summary>What an update did to a stop that exists.</summary>
	public static readonly IReadOnlyList<string> Effects = ["changed", "affected"];

	/// <summary>Checks a new walkthrough and the code it cites.</summary>
	public static AgentBlockOperation.Show Publish(AgentWalkthroughRequest request, AgentBlock? current, WalkthroughReads reads) {
		ArgumentNullException.ThrowIfNull(request);
		ArgumentNullException.ThrowIfNull(reads);
		Id(request.Id, "id");
		if (current is not null) Refuse($"Block '{request.Id}' is already shown; change it with {UpdaterOf(current)}.");
		if (request.Change is not null) Refuse($"'change' says what an update changed; it is for {UpdateWalkthroughTool}.");
		var stops = Items(request.Stops ?? [], AgentWalkthroughLimits.Stops, "stops");
		if (stops.Count == 0) Refuse("A walkthrough needs at least one stop.");
		Unique(stops.Select(stop => stop.Id), "stops ids");
		var cited = new Cited(reads);
		var walkthrough = new AgentWalkthrough {
			Root = reads.Root,
			Problem = Required(request.Problem, "problem", AgentWalkthroughLimits.Text),
			Gist = Required(request.Gist, "gist", AgentWalkthroughLimits.Text),
			Example = Optional(request.Example, "example", AgentWalkthroughLimits.Text),
			Closing = Optional(request.Closing, "closing", AgentWalkthroughLimits.Text),
			Stops = [.. stops.Select((stop, index) => New(stop, $"stops[{index}]", cited))],
			Files = cited.Files,
		};
		Example(walkthrough.Example, walkthrough.Stops.Where(stop => stop.State is not null).Select(stop => stop.Id));
		var block = new AgentBlock {
			Id = request.Id,
			Title = Required(request.Title, "title", AgentWalkthroughLimits.Title),
			Walkthrough = walkthrough,
		};
		return new AgentBlockOperation.Show(block, null, cited.Sources);
	}

	/// <summary>Checks an update: one change per stop, then every cited file recorded as it stands.</summary>
	public static AgentBlockOperation.Walkthrough Update(AgentWalkthroughRequest request, AgentBlock? current, WalkthroughReads reads) {
		ArgumentNullException.ThrowIfNull(request);
		ArgumentNullException.ThrowIfNull(reads);
		if (current is null) Refuse($"Walkthrough '{request.Id}' does not exist in this conversation. Publish it with {PublishTool}.");
		var walkthrough = current!.Walkthrough ?? Fail<AgentWalkthrough>($"Block '{request.Id}' is a {current.Kind}; change it with {UpdateTool}.");
		if (request.Title is not null) Refuse($"'title' is set when publishing; {UpdateWalkthroughTool} keeps it.");
		var cited = new Cited(reads);
		var changes = new List<AgentWalkthroughChange> {
			new AgentWalkthroughChange.SetText(
				Given(request.Problem, "problem", AgentWalkthroughLimits.Text),
				Given(request.Gist, "gist", AgentWalkthroughLimits.Text),
				Given(request.Example, "example", AgentWalkthroughLimits.Text),
				Given(request.Closing, "closing", AgentWalkthroughLimits.Text),
				Required(request.Change, "change", AgentWalkthroughLimits.Text)),
		};
		var stops = Items(request.Stops ?? [], AgentWalkthroughLimits.Stops, "stops");
		Unique(stops.Select(stop => stop.Id), "stops ids");
		var known = walkthrough.Stops.Select(stop => stop.Id).ToHashSet(StringComparer.Ordinal);
		foreach (var (stop, index) in stops.Select((stop, index) => (stop, index))) {
			string at = $"stops[{index}]";
			if (known.Contains(stop.Id)) {
				changes.Add(Patch(stop, at, cited));
				continue;
			}
			if (stop.Name is null || stop.Title is null || stop.Lead is null || stop.Code is null) {
				Refuse($"{at}: stop '{stop.Id}' does not exist, so it needs 'name', 'title', 'lead' and 'code' to be added.");
			}
			if (stop.After is { } after && !known.Contains(after)) Refuse($"{at}.after: there is no stop '{after}' before this one.");
			var stopAdded = New(stop, at, cited) with { Effect = "added", Note = Optional(stop.Note, at + ".note", AgentWalkthroughLimits.Title) };
			changes.Add(new AgentWalkthroughChange.AddStop(stopAdded, stop.After));
			known.Add(stop.Id);
		}
		if (known.Count > AgentWalkthroughLimits.Stops) {
			Refuse($"The walkthrough would have {known.Count} stops; the limit is {AgentWalkthroughLimits.Stops}.");
		}
		Example(request.Example ?? walkthrough.Example, stops.Where(stop => stop.State is not null).Select(stop => stop.Id));
		changes.Add(new AgentWalkthroughChange.RecordFiles(Record(walkthrough.Files, cited)));
		return new AgentBlockOperation.Walkthrough(request.Id, changes, cited.Sources);
	}

	/// <summary>The walkthrough after <paramref name="operation"/>, or null when a change no longer applies.</summary>
	public static AgentBlock? Apply(AgentBlock? current, AgentBlockOperation.Walkthrough operation, long nowMs) {
		ArgumentNullException.ThrowIfNull(operation);
		if (current?.Walkthrough is not { } walkthrough) return null;
		foreach (var change in operation.Changes) {
			var stops = walkthrough.Stops.ToList();
			switch (change) {
				case AgentWalkthroughChange.SetText text:
					walkthrough = walkthrough with {
						Problem = text.Problem ?? walkthrough.Problem,
						Gist = text.Gist ?? walkthrough.Gist,
						Example = text.Example ?? walkthrough.Example,
						Closing = text.Closing ?? walkthrough.Closing,
						Change = text.Change,
					};
					break;
				case AgentWalkthroughChange.AddStop add:
					int at = add.After is null ? stops.Count : stops.FindIndex(stop => stop.Id == add.After) + 1;
					if (at == 0 || stops.Any(stop => stop.Id == add.Stop.Id)) return null;
					stops.Insert(at, add.Stop);
					walkthrough = walkthrough with { Stops = stops };
					break;
				case AgentWalkthroughChange.PatchStop patch:
					int index = stops.FindIndex(stop => stop.Id == patch.Request.Id);
					if (index < 0) return null;
					stops[index] = Patched(stops[index], patch);
					walkthrough = walkthrough with { Stops = stops };
					break;
				case AgentWalkthroughChange.RecordFiles record:
					var files = walkthrough.Files.ToList();
					foreach (var file in record.Files) {
						int known = files.FindIndex(existing => existing.Path == file.Path);
						if (known < 0) files.Add(file);
						else files[known] = files[known] with { After = file.After };
					}
					walkthrough = walkthrough with { Files = files };
					break;
			}
		}
		return current with { Walkthrough = walkthrough, UpdatedAtMs = nowMs };
	}

	private static AgentWalkthroughChange.PatchStop Patch(AgentWalkthroughStopRequest stop, string at, Cited cited) {
		OneOf(stop.Effect, Effects, at + ".effect");
		if (stop.After is not null) Refuse($"{at}.after places an added stop; stop '{stop.Id}' exists.");
		var request = stop with {
			Name = Given(stop.Name, at + ".name", AgentWalkthroughLimits.Name),
			Title = Given(stop.Title, at + ".title", AgentWalkthroughLimits.Title),
			Lead = Given(stop.Lead, at + ".lead", AgentWalkthroughLimits.Text),
			State = State(stop.State, at + ".state"),
			Why = Why(stop.Why, at + ".why"),
			Predict = Predict(stop.Predict, at + ".predict"),
			Note = Required(stop.Note, at + ".note", AgentWalkthroughLimits.Title),
		};
		return new AgentWalkthroughChange.PatchStop(request, stop.Code is null ? null : Code(stop.Code, at + ".code", cited));
	}

	// A changed stop keeps what each reworded note said before, so the page can show what it was.
	private static AgentWalkthroughStop Patched(AgentWalkthroughStop stop, AgentWalkthroughChange.PatchStop patch) {
		var request = patch.Request;
		var code = patch.Code is null ? stop.Code : [.. patch.Code.Select(block => block with {
			Notes = [.. (block.Notes ?? []).Select(note => {
				var old = stop.Code.Where(earlier => earlier.Path == block.Path).SelectMany(earlier => earlier.Notes ?? [])
					.FirstOrDefault(earlier => earlier.Token == note.Token);
				string? was = old?.Was ?? old?.Text;
				return note with { Was = was == note.Text ? null : was };
			})],
		})];
		return stop with {
			Name = request.Name ?? stop.Name,
			Title = request.Title ?? stop.Title,
			Lead = request.Lead ?? stop.Lead,
			Code = code,
			State = request.State ?? stop.State,
			Why = request.Why ?? stop.Why,
			Predict = request.Predict ?? stop.Predict,
			Effect = stop.Effect == "added" ? "added" : request.Effect,
			Note = request.Note,
		};
	}

	private static AgentWalkthroughStop New(AgentWalkthroughStopRequest stop, string at, Cited cited) {
		Id(stop.Id, at + ".id");
		return new AgentWalkthroughStop {
			Id = stop.Id,
			Name = Required(stop.Name, at + ".name", AgentWalkthroughLimits.Name),
			Title = Required(stop.Title, at + ".title", AgentWalkthroughLimits.Title),
			Lead = Required(stop.Lead, at + ".lead", AgentWalkthroughLimits.Text),
			Code = Code(stop.Code ?? Fail<IReadOnlyList<AgentWalkthroughCode>>($"{at}.code is required."), at + ".code", cited),
			State = State(stop.State, at + ".state"),
			Why = Why(stop.Why, at + ".why"),
			Predict = Predict(stop.Predict, at + ".predict"),
		};
	}

	private static IReadOnlyList<AgentWalkthroughCode> Code(IReadOnlyList<AgentWalkthroughCode> blocks, string at, Cited cited) {
		var list = Items(blocks, AgentWalkthroughLimits.Blocks, at);
		if (list.Count == 0) Refuse($"{at} needs at least one code block.");
		return [.. list.Select((block, index) => Block(block, $"{at}[{index}]", cited))];
	}

	private static AgentWalkthroughCode Block(AgentWalkthroughCode block, string at, Cited cited) {
		var file = cited.Cite(block.Path, at + ".path");
		var (from, to) = (block.From, block.To);
		if (from < 1 || to < from || to > file.Lines.Length) {
			Refuse($"{at}: lines {from}-{to} are outside '{file.Path}', which has {file.Lines.Length} line(s).");
		}
		if (to - from + 1 > AgentWalkthroughLimits.Lines) {
			Refuse($"{at} shows {to - from + 1} lines; the limit is {AgentWalkthroughLimits.Lines}. Fold what is not the point, or split the block.");
		}
		var folds = Items(block.Folds ?? [], AgentWalkthroughLimits.Folds, at + ".folds").OrderBy(fold => fold.From).ToList();
		for (int index = 0; index < folds.Count; index++) {
			var fold = folds[index];
			string path = $"{at}.folds[{index}]";
			if (fold.From <= from || fold.To > to || fold.To < fold.From) {
				Refuse($"{path}: lines {fold.From}-{fold.To} must lie inside the block's lines {from}-{to}, after its first line.");
			}
			if (index > 0 && fold.From <= folds[index - 1].To + 1) {
				Refuse($"{path}: lines {fold.From}-{fold.To} touch the fold at lines {folds[index - 1].From}-{folds[index - 1].To}; join them.");
			}
			folds[index] = fold with { Label = Required(fold.Label, path + ".label", AgentWalkthroughLimits.Title) };
		}
		var notes = Items(block.Notes ?? [], AgentWalkthroughLimits.Notes, at + ".notes").Select((note, index) => {
			string path = $"{at}.notes[{index}]";
			if (note.Line < from || note.Line > to) Refuse($"{path}: line {note.Line} is outside the block's lines {from}-{to}.");
			if (folds.FirstOrDefault(fold => note.Line >= fold.From && note.Line <= fold.To) is { } folded) {
				Refuse($"{path}: line {note.Line} is inside the fold at lines {folded.From}-{folded.To}. A note sits on a line that is shown.");
			}
			if (note.Token.Length == 0) Refuse($"{path}.token must not be empty.");
			string line = file.Lines[note.Line - 1];
			if (!line.Contains(note.Token, StringComparison.Ordinal)) {
				var holding = Enumerable.Range(from, to - from + 1).Where(n => file.Lines[n - 1].Contains(note.Token, StringComparison.Ordinal)).ToList();
				Refuse($"{path}: line {note.Line} of '{file.Path}' does not contain '{note.Token}'. Line {note.Line} is \"{line.Trim()}\". "
					+ (holding.Count == 0 ? $"'{note.Token}' is not in lines {from}-{to}." : $"'{note.Token}' is on line {string.Join(", ", holding)}."));
			}
			return note with { Text = Required(note.Text, path + ".text", AgentWalkthroughLimits.Title), Was = null };
		}).ToList();
		return block with { Path = file.Path, Source = file.Hash, Folds = folds, Notes = notes };
	}

	private static AgentWalkthroughState? State(AgentWalkthroughState? state, string at) => state is null ? null : new AgentWalkthroughState {
		Cells = [.. Items(state.Cells, AgentWalkthroughLimits.Cells, at + ".cells").Select((cell, index) => new AgentWalkthroughCell {
			Label = Required(cell.Label, $"{at}.cells[{index}].label", AgentWalkthroughLimits.Name),
			Value = Optional(cell.Value, $"{at}.cells[{index}].value", AgentWalkthroughLimits.Value) ?? "",
		})],
		Note = Optional(state.Note, at + ".note", AgentWalkthroughLimits.Text),
	};

	private static AgentWalkthroughWhy? Why(AgentWalkthroughWhy? why, string at) {
		if (why is null) return null;
		string kind = OneOf(why.Source.Kind, SourceKinds, at + ".source.kind");
		string? reference = Optional(why.Source.Ref, at + ".source.ref", AgentWalkthroughLimits.Title);
		if ((kind == "reading") != (reference is null)) {
			Refuse(kind == "reading" ? $"{at}.source: a reading has no 'ref'." : $"{at}.source.ref is required for a {kind}.");
		}
		return new AgentWalkthroughWhy {
			Title = Required(why.Title, at + ".title", AgentWalkthroughLimits.Title),
			Text = Required(why.Text, at + ".text", AgentWalkthroughLimits.Text),
			Source = new AgentWalkthroughSource { Kind = kind, Ref = reference },
		};
	}

	private static AgentWalkthroughPredict? Predict(AgentWalkthroughPredict? predict, string at) => predict is null ? null : new AgentWalkthroughPredict {
		Question = Required(predict.Question, at + ".question", AgentWalkthroughLimits.Text),
		Answer = Required(predict.Answer, at + ".answer", AgentWalkthroughLimits.Text),
	};

	// Each file the walkthrough cites, recorded as it stands; a file that can no longer be read records as empty.
	private static IReadOnlyList<AgentWalkthroughFile> Record(IReadOnlyList<AgentWalkthroughFile> files, Cited cited) =>
		[.. files.Select(file => file.Path).Union(cited.Files.Select(file => file.Path)).Select(path => {
			string after = cited.Record(path)?.Hash ?? "";
			var first = cited.Files.FirstOrDefault(file => file.Path == path);
			return new AgentWalkthroughFile { Path = path, Before = first?.Before ?? after, After = after, Late = first?.Late ?? false };
		})];

	private static void Example(string? example, IEnumerable<string> stopsWithState) {
		if (example is null && stopsWithState.FirstOrDefault() is { } stop) {
			Refuse($"Stop '{stop}' has a 'state', which follows the walkthrough's 'example'; give the example.");
		}
	}

	private static void Id(string id, string at) {
		if (!BlockId().IsMatch(id)) Refuse($"{at} must be {IdRule}.");
	}

	// A field an update leaves out keeps its value; one it gives must say something.
	private static string? Given(string? text, string at, int limit) => text is null ? null : Required(text, at, limit);

	private static string Required(string? text, string at, int limit) =>
		Optional(text, at, limit) is { Length: > 0 } given ? given : Fail<string>($"{at} is required.");

	private static string? Optional(string? text, string at, int limit) => text is { Length: var length } && length > limit
		? Fail<string>($"{at} has {length:N0} characters; the limit is {limit:N0}.")
		: text?.Trim();

	// The files one call cites: the copies to keep, by hash, and each path's first copy.
	private sealed class Cited(WalkthroughReads reads) {
		public Dictionary<string, string> Sources { get; } = new(StringComparer.Ordinal);
		public List<AgentWalkthroughFile> Files { get; } = [];

		public WorktreeTextFile Cite(string path, string at) {
			var cited = reads.Cite(path, at);
			Keep(cited.File);
			if (Files.All(file => file.Path != cited.File.Path)) {
				Files.Add(new AgentWalkthroughFile { Path = cited.File.Path, Before = cited.File.Hash, Late = cited.Late });
			}
			return cited.File;
		}

		public WorktreeTextFile? Record(string path) {
			var file = reads.Record(path)?.File;
			if (file is not null) Keep(file);
			return file;
		}

		private void Keep(WorktreeTextFile file) => Sources[file.Hash] = file.Text;
	}
}
