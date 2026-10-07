using System.Text.Json;
using System.Text.Json.Serialization;

namespace Weavie.Hosting.Tests;

internal sealed record OpenFileProbe(string Path, int? Line, bool Preview, bool Scratch, string Intent);

internal sealed record ValueProbe(int Value);

internal sealed record TitleProbe(string Title);

/// <summary>Contracts for payloads only tests publish onto the bus (web defaults, like the production wire).</summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(OpenFileProbe))]
[JsonSerializable(typeof(ValueProbe))]
[JsonSerializable(typeof(TitleProbe))]
internal sealed partial class TestJson : JsonSerializerContext;
