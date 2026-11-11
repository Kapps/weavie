namespace Weavie.Workflows;

/// <summary>Where a workflow file lives; a more specific scope overrides a less specific one with the same id.</summary>
public enum WorkflowScope {
	/// <summary>Shipped with the host.</summary>
	BuiltIn,

	/// <summary>The user's own, for every project.</summary>
	Personal,

	/// <summary>This repository's.</summary>
	Repository,
}

/// <summary>One workflow file the catalog found, valid or not.</summary>
public sealed record WorkflowCatalogEntry {
	/// <summary>The workflow id: the file name without <c>.yaml</c>.</summary>
	public required string Id { get; init; }

	/// <summary>Where the file lives.</summary>
	public required WorkflowScope Scope { get; init; }

	/// <summary>The file's path; built-ins read <c>built-in:&lt;id&gt;.yaml</c>.</summary>
	public required string Path { get; init; }

	/// <summary>The definition, or <see langword="null"/> when the file is broken.</summary>
	public required WorkflowDefinition? Definition { get; init; }

	/// <summary>Why the file is broken; empty when valid.</summary>
	public required IReadOnlyList<WorkflowDiagnostic> Diagnostics { get; init; }

	/// <summary>The scope whose same-id file overrides this one, or <see langword="null"/> when this one wins.</summary>
	public required WorkflowScope? ShadowedBy { get; init; }

	/// <summary>Why the file can't run, as one line; empty when valid.</summary>
	public string Error => string.Join("; ", Diagnostics.Where(d => !d.IsWarning));

	/// <summary>
	/// Whether a start from a prompt alone offers this workflow: it wins its id and declares no inputs. A broken
	/// winner is offered so its <see cref="Error"/> can be shown.
	/// </summary>
	public bool OfferedForPrompt => ShadowedBy is null && Definition is not { Inputs.Count: > 0 };
}

/// <summary>
/// Every workflow visible from one worktree: built-in, personal, and repository files, re-read on each call so
/// an edit shows at once. File inputs resolve against <paramref name="worktree"/>.
/// </summary>
public sealed class WorkflowCatalog(string worktree, string personalDirectory, string repositoryDirectory) {
	private const string Extension = ".yaml";
	private const string BuiltInResourcePrefix = "Weavie.Workflows.BuiltIn.";

	/// <summary>Every workflow file visible from the worktree, including shadowed and broken ones.</summary>
	public IReadOnlyList<WorkflowCatalogEntry> List() {
		var entries = BuiltIns()
			.Concat(Directory(personalDirectory, WorkflowScope.Personal))
			.Concat(Directory(repositoryDirectory, WorkflowScope.Repository))
			.ToList();
		return [.. entries
			.GroupBy(entry => entry.Id, StringComparer.Ordinal)
			.OrderBy(group => group.Key, StringComparer.Ordinal)
			.SelectMany(group => {
				var ordered = group.OrderByDescending(entry => entry.Scope).ToList();
				return ordered.Select((entry, i) => i == 0 ? entry : entry with { ShadowedBy = ordered[0].Scope });
			})];
	}

	/// <summary>Resolves <paramref name="id"/> once and reads <paramref name="inputs"/> against this worktree.</summary>
	public WorkflowLaunch Prepare(string id, System.Text.Json.JsonElement inputs) => Prepare(id, inputs, null, WorkflowStartPrompt.None);

	/// <summary>Prepares a start with <paramref name="prompt"/>, reading inputs with access to the inherited run documents directory.</summary>
	public WorkflowLaunch Prepare(string id, System.Text.Json.JsonElement inputs, string? documentsDirectory, WorkflowStartPrompt prompt) {
		ArgumentNullException.ThrowIfNull(prompt);
		var (entry, definition) = Winner(id);
		if (definition.UsesPrompt && prompt.IsEmpty) {
			throw new WorkflowException($"Workflow {id} can't start: it uses {{{{run.prompt}}}}, and no prompt was given.");
		}

		return new WorkflowLaunch(definition, entry.Path, WorkflowTransitions.ReadInputs(definition, inputs, new WorkflowFileScope(worktree, documentsDirectory)), prompt);
	}

	private WorkflowCatalogEntry Resolve(string id) {
		var winners = List().Where(e => e.ShadowedBy is null).ToList();
		return winners.FirstOrDefault(e => e.Id == id)
			?? throw new WorkflowException($"No workflow \"{id}\" exists; the workflows are: {string.Join(", ", winners.Select(e => e.Id))}.");
	}

	private (WorkflowCatalogEntry Entry, WorkflowDefinition Definition) Winner(string id) {
		var entry = Resolve(id);
		return (entry, entry.Definition ?? throw new WorkflowException(
			$"Workflow \"{id}\" ({entry.Path}) is invalid: {entry.Error}"));
	}

	/// <summary>The problems in the saved workflow that wins for <paramref name="id"/>; empty when valid.</summary>
	public IReadOnlyList<WorkflowDiagnostic> Validate(string id) => Resolve(id).Diagnostics;

	/// <summary>The problems in proposed workflow <paramref name="text"/>; no file is touched.</summary>
	public IReadOnlyList<WorkflowDiagnostic> ValidateText(string text) => WorkflowReader.ReadProposal(text).Diagnostics;

	/// <summary>Copies the valid workflow <paramref name="id"/> into the more specific <paramref name="scope"/>; returns the new path.</summary>
	public string Fork(string id, WorkflowScope scope) {
		var (entry, definition) = Winner(id);
		if (scope <= entry.Scope) {
			throw new WorkflowException($"Workflow \"{id}\" is already {entry.Scope}, so a {scope} copy would not override it; edit {entry.Path} instead.");
		}

		string folder = Folder(scope);
		System.IO.Directory.CreateDirectory(folder);
		string path = System.IO.Path.Combine(folder, id + Extension);
		using var writer = new StreamWriter(File.Open(path, FileMode.CreateNew, FileAccess.Write));
		writer.Write(definition.SourceText);
		return path;
	}

	/// <summary>The folder holding <paramref name="scope"/>'s workflow files, which may not exist yet.</summary>
	public string Folder(WorkflowScope scope) => scope switch {
		WorkflowScope.Personal => personalDirectory,
		WorkflowScope.Repository => repositoryDirectory,
		_ => throw new WorkflowException("Built-in workflows have no folder."),
	};

	private static IEnumerable<WorkflowCatalogEntry> Directory(string directory, WorkflowScope scope) =>
		System.IO.Directory.Exists(directory)
			? System.IO.Directory.EnumerateFiles(directory, "*" + Extension)
				.Select(file => FileEntry(file, scope))
			: [];

	private static WorkflowCatalogEntry FileEntry(string file, WorkflowScope scope) {
		string id = System.IO.Path.GetFileNameWithoutExtension(file);
		try {
			return Entry(id, scope, file, File.ReadAllText(file));
		} catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
			return new WorkflowCatalogEntry {
				Id = id,
				Scope = scope,
				Path = file,
				Definition = null,
				ShadowedBy = null,
				Diagnostics = [WorkflowDiagnostic.Error("", new WorkflowPosition(1, 1), ex.Message)],
			};
		}
	}

	private static IEnumerable<WorkflowCatalogEntry> BuiltIns() {
		var assembly = typeof(WorkflowCatalog).Assembly;
		foreach (string name in assembly.GetManifestResourceNames().Where(n => n.StartsWith(BuiltInResourcePrefix, StringComparison.Ordinal))) {
			using var reader = new StreamReader(assembly.GetManifestResourceStream(name)!);
			string id = name[BuiltInResourcePrefix.Length..^Extension.Length];
			yield return Entry(id, WorkflowScope.BuiltIn, $"built-in:{id}{Extension}", reader.ReadToEnd());
		}
	}

	private static WorkflowCatalogEntry Entry(string id, WorkflowScope scope, string path, string text) {
		var read = WorkflowReader.Read(text, id);
		return new WorkflowCatalogEntry {
			Id = id,
			Scope = scope,
			Path = path,
			Definition = read.Definition,
			Diagnostics = read.Diagnostics,
			ShadowedBy = null,
		};
	}
}

/// <summary>A resolved workflow ready to start: its definition, where it came from, its read inputs, and its prompt.</summary>
public sealed record WorkflowLaunch(
	WorkflowDefinition Definition, string SourcePath, Dictionary<string, WorkflowValue> Inputs, WorkflowStartPrompt Prompt);

/// <summary>A workflow request the engine refuses, with a message meant for the agent or user.</summary>
public sealed class WorkflowException(string message) : Exception(message);
