using System.Text.Json.Nodes;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;

namespace Weavie.Workflows;

/// <summary>A 1-based line and column in a workflow file.</summary>
public readonly record struct WorkflowPosition(int Line, int Column);

/// <summary>A YAML document read as a JSON tree whose scalars are all strings, with each node's source position.</summary>
internal sealed record YamlDocument {
	/// <summary>The document root; a null scalar reads as <see langword="null"/>.</summary>
	public required JsonNode? Root { get; init; }

	/// <summary>The position of each node by path (<c>stages[2].do</c>); a mapping value is keyed at its key.</summary>
	public required IReadOnlyDictionary<string, WorkflowPosition> Positions { get; init; }
}

/// <summary>A workflow file that isn't the YAML the engine accepts.</summary>
internal sealed class WorkflowFormatException(string message, WorkflowPosition position) : Exception(message) {
	/// <summary>Where the problem is.</summary>
	public WorkflowPosition Position { get; } = position;
}

/// <summary>Reads one YAML document with YamlDotNet's event parser, which is reflection-free and so AOT-safe.</summary>
internal static class YamlNodeReader {
	/// <summary>Reads <paramref name="text"/>; every scalar stays a string so <c>yes</c>/<c>3</c> keep their spelling.</summary>
	public static YamlDocument Read(string text) {
		var positions = new Dictionary<string, WorkflowPosition>(StringComparer.Ordinal);
		try {
			var parser = new Parser(new StringReader(text));
			parser.Consume<StreamStart>();
			if (parser.Accept<StreamEnd>(out _)) {
				throw new WorkflowFormatException("The file is empty.", new WorkflowPosition(1, 1));
			}

			parser.Consume<DocumentStart>();
			var root = ReadNode(parser, string.Empty, positions);
			parser.Consume<DocumentEnd>();
			if (!parser.Accept<StreamEnd>(out _)) {
				throw new WorkflowFormatException("A workflow file holds exactly one YAML document.", At(parser.Current!.Start));
			}

			return new YamlDocument { Root = root, Positions = positions };
		} catch (YamlException ex) {
			throw new WorkflowFormatException(ex.Message, At(ex.Start));
		}
	}

	private static JsonNode? ReadNode(IParser parser, string path, Dictionary<string, WorkflowPosition> positions) {
		var current = parser.Current!;
		positions.TryAdd(path, At(current.Start));
		if (current is AnchorAlias) {
			throw new WorkflowFormatException("YAML aliases aren't supported.", At(current.Start));
		}

		if (current is NodeEvent node && (!node.Anchor.IsEmpty || !node.Tag.IsEmpty)) {
			throw new WorkflowFormatException("YAML anchors and tags aren't supported.", At(current.Start));
		}

		parser.MoveNext();
		return current switch {
			Scalar scalar => scalar.Style == ScalarStyle.Plain && scalar.Value is "" or "~" or "null"
								? null
								: JsonValue.Create(scalar.Value),
			SequenceStart => ReadSequence(parser, path, positions),
			MappingStart => ReadMapping(parser, path, positions),
			_ => throw new WorkflowFormatException($"Unexpected YAML {current.GetType().Name}.", At(current.Start)),
		};
	}

	private static JsonArray ReadSequence(IParser parser, string path, Dictionary<string, WorkflowPosition> positions) {
		var array = new JsonArray();
		for (int i = 0; !parser.TryConsume<SequenceEnd>(out _); i++) {
			array.Add(ReadNode(parser, $"{path}[{i}]", positions));
		}

		return array;
	}

	private static JsonObject ReadMapping(IParser parser, string path, Dictionary<string, WorkflowPosition> positions) {
		var obj = new JsonObject();
		while (!parser.TryConsume<MappingEnd>(out _)) {
			if (parser.Current is not Scalar key) {
				throw new WorkflowFormatException("Mapping keys must be plain text.", At(parser.Current!.Start));
			}

			if (obj.ContainsKey(key.Value)) {
				throw new WorkflowFormatException($"Duplicate key \"{key.Value}\".", At(key.Start));
			}

			string keyPath = path.Length == 0 ? key.Value : $"{path}.{key.Value}";
			positions[keyPath] = At(key.Start);
			parser.MoveNext();
			obj.Add(key.Value, ReadNode(parser, keyPath, positions));
		}

		return obj;
	}

	private static WorkflowPosition At(Mark mark) => new((int)mark.Line, (int)mark.Column);
}
