namespace Weavie.Core.Workflows;

/// <summary>Where a workflow file lives; a more specific scope overrides a less specific one with the same id.</summary>
public enum WorkflowScope {
	/// <summary>Shipped with Weavie.</summary>
	BuiltIn,

	/// <summary>The user's own, for every project (<c>~/.weavie/workflows</c>).</summary>
	Personal,

	/// <summary>This repository's (<c>.weavie/workflows</c>).</summary>
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
}

/// <summary>
/// Every workflow visible from one worktree: built-in, personal, and repository files, re-read on each call so
/// an edit shows at once.
/// </summary>
public sealed class WorkflowCatalog(string worktree, string personalDirectory) : IWorkflowRegistry {
	private const string Extension = ".yaml";
	private const string BuiltInResourcePrefix = "Weavie.Core.Workflows.BuiltIn.";

	/// <inheritdoc/>
	public IReadOnlyList<WorkflowCatalogEntry> List() {
		var entries = BuiltIns()
			.Concat(Directory(personalDirectory, WorkflowScope.Personal))
			.Concat(Directory(System.IO.Path.Combine(worktree, ".weavie", "workflows"), WorkflowScope.Repository))
			.ToList();
		return [.. entries
			.GroupBy(entry => entry.Id, StringComparer.Ordinal)
			.OrderBy(group => group.Key, StringComparer.Ordinal)
			.SelectMany(group => {
				var ordered = group.OrderByDescending(entry => entry.Scope).ToList();
				return ordered.Select((entry, i) => i == 0 ? entry : entry with { ShadowedBy = ordered[0].Scope });
			})];
	}

	/// <summary>The winning definition for <paramref name="id"/>; throws when it is missing or broken.</summary>
	public WorkflowDefinition Resolve(string id) {
		var entry = List().FirstOrDefault(e => e.Id == id && e.ShadowedBy is null)
			?? throw new WorkflowException($"No workflow \"{id}\" exists. Call listWorkflows for the available ids.");
		return entry.Definition ?? throw new WorkflowException(
			$"Workflow \"{id}\" ({entry.Path}) is invalid: {string.Join("; ", entry.Diagnostics)}");
	}

	/// <inheritdoc/>
	public IReadOnlyList<WorkflowDiagnostic> Validate(string path) {
		string full = System.IO.Path.GetFullPath(System.IO.Path.Combine(worktree, path));
		if (!full.EndsWith(Extension, StringComparison.Ordinal)) {
			throw new WorkflowException($"Workflow files end in {Extension}: {full}");
		}

		if (!File.Exists(full)) {
			throw new WorkflowException($"No file at {full}.");
		}

		return WorkflowReader.Read(File.ReadAllText(full), System.IO.Path.GetFileNameWithoutExtension(full)).Diagnostics;
	}

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
				Diagnostics = [new WorkflowDiagnostic { Path = "", Position = new WorkflowPosition(1, 1), Message = ex.Message }],
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

/// <summary>A workflow request Weavie refuses, with a message meant for the agent or user.</summary>
public sealed class WorkflowException(string message) : Exception(message);
