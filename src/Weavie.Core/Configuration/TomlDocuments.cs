using Tomlyn;
using Tomlyn.Model;
using Tomlyn.Parsing;
using Tomlyn.Serialization;
using Tomlyn.Syntax;

namespace Weavie.Core.Configuration;

/// <summary>Parses TOML into Tomlyn's lossless syntax tree and its untyped model, through source-generated metadata.</summary>
internal static class TomlDocuments {
	public static DocumentSyntax Parse(string text, string sourceName) => SyntaxParser.Parse(text, sourceName, validate: true);

	public static TomlTable ToModel(string text) => TomlSerializer.Deserialize(text, TomlModelContext.Default.TomlTable) ?? [];
}

[TomlSerializable(typeof(TomlTable))]
internal sealed partial class TomlModelContext : TomlSerializerContext;
