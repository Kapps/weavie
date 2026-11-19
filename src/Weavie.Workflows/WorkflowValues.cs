using System.Text.Json;

namespace Weavie.Workflows;

/// <summary>A typed workflow input or stage output: text, a list of items, or one value per entry of a repeated stage.</summary>
public sealed record WorkflowValue {
	/// <summary>The text of a text, url, or pr value; otherwise <see langword="null"/>.</summary>
	public string? Text { get; init; }

	/// <summary>The items of a list or files value; otherwise <see langword="null"/>.</summary>
	public IReadOnlyList<string>? Items { get; init; }

	/// <summary>A repeated stage's output: the value of each entry that reported it, in order; otherwise <see langword="null"/>.</summary>
	public IReadOnlyList<WorkflowEntryValue>? Entries { get; init; }

	/// <summary>An empty text value, rendered for an omitted optional input.</summary>
	public static WorkflowValue Empty { get; } = new() { Text = "" };

	/// <summary>The value as template text: a list renders one <c>- item</c> per line, entries one labelled block each.</summary>
	public string Render() => Entries is { } entries
		? string.Join("\n", entries.Select(entry => entry.Value.Text is { } text && !text.Contains('\n')
			? $"- {Label(entry.Item)}: {text}"
			: $"- {Label(entry.Item)}:\n{Indent(entry.Value.Render())}"))
		: Text ?? string.Join("\n", Items!.Select(item => $"- {item}"));

	/// <summary>The value as one flat list: a list's items, a text as one item, each entry's in order.</summary>
	public IReadOnlyList<string> Flat() => Entries is { } entries ? [.. entries.SelectMany(entry => entry.Value.Flat())]
		: Items ?? (Text is { Length: > 0 } text ? [text] : []);

	private static string Label(string item) => item.Split('\n')[0].Trim();

	private static string Indent(string text) => string.Join("\n", text.Split('\n').Select(line => "  " + line));
}

/// <summary>One entry's value of a repeated stage's output.</summary>
public sealed record WorkflowEntryValue {
	/// <summary>The entry's item.</summary>
	public required string Item { get; init; }

	/// <summary>The value that entry reported.</summary>
	public required WorkflowValue Value { get; init; }
}

/// <summary>Reads and type-checks workflow values.</summary>
internal static class WorkflowValues {
	/// <summary>Parses a type name from a workflow file (<c>files</c>, <c>pr</c>, …).</summary>
	public static WorkflowValueType ParseType(string name) => name switch {
		"text" => WorkflowValueType.Text,
		"url" => WorkflowValueType.Url,
		"list" => WorkflowValueType.List,
		"files" => WorkflowValueType.Files,
		"pr" => WorkflowValueType.Pr,
		_ => throw new ArgumentException($"Unknown workflow value type '{name}'.", nameof(name)),
	};

	/// <summary>
	/// Reads <paramref name="element"/> as <paramref name="type"/>, resolving files within <paramref name="files"/>,
	/// or explains why it doesn't fit.
	/// </summary>
	public static bool TryRead(
		JsonElement element, WorkflowValueType type, WorkflowFileScope files, out WorkflowValue value, out string error) {
		value = WorkflowValue.Empty;
		switch (type) {
			case WorkflowValueType.Text:
				if (element.ValueKind != JsonValueKind.String) {
					error = "expected text";
					return false;
				}

				value = new WorkflowValue { Text = element.GetString()! };
				break;
			case WorkflowValueType.Url:
			case WorkflowValueType.Pr:
				if (element.ValueKind != JsonValueKind.String || !IsWebUrl(element.GetString()!)) {
					error = "expected an absolute http(s) URL";
					return false;
				}

				value = new WorkflowValue { Text = element.GetString()! };
				break;
			default:
				if (!TryReadItems(element, out var items)) {
					error = "expected a list of text items";
					return false;
				}

				if (type == WorkflowValueType.Files && items.FirstOrDefault(item => !IsFile(files, item)) is { } missing) {
					error = $"\"{missing}\" isn't a non-empty file inside the worktree or run documents directory";
					return false;
				}

				value = new WorkflowValue { Items = items };
				break;
		}

		error = "";
		return true;
	}

	private static bool TryReadItems(JsonElement element, out IReadOnlyList<string> items) {
		items = element.ValueKind == JsonValueKind.Array
			&& element.EnumerateArray().All(item => item.ValueKind == JsonValueKind.String && item.GetString()!.Trim().Length > 0)
			? [.. element.EnumerateArray().Select(item => item.GetString()!)]
			: [];
		return element.ValueKind == JsonValueKind.Array && items.Count == element.GetArrayLength();
	}

	private static bool IsWebUrl(string text) =>
		Uri.TryCreate(text, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

	private static bool IsFile(WorkflowFileScope files, string path) {
		try {
			return files.TryResolve(path, out _, out string full)
				&& File.Exists(full) && new FileInfo(full).Length > 0;
		} catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
			return false;
		}
	}
}
