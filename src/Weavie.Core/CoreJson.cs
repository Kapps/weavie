using System.Text.Json;
using System.Text.Json.Serialization;
using Weavie.Core.Changes;
using Weavie.Core.Corrections;
using Weavie.Core.Review;
using Weavie.Core.Shell;
using Weavie.Core.Sources;
using Weavie.Core.Spelling;

namespace Weavie.Core;

/// <summary>Source-generated contracts for Core's ad-hoc JSON with default options (setting values, records).</summary>
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(long))]
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(CorrectionRecord))]
internal sealed partial class CoreJson : JsonSerializerContext;

/// <summary>Source-generated contracts for the camelCase payloads Core builds for the page.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(TurnChangesWire))]
[JsonSerializable(typeof(TurnDiffWire))]
[JsonSerializable(typeof(ReviewHistoryWire))]
[JsonSerializable(typeof(ShellConfigWire))]
[JsonSerializable(typeof(SpellLocales))]
internal sealed partial class PageJson : JsonSerializerContext;

/// <summary>Source-generated contracts for snake_case third-party API request bodies (GitHub, Notion).</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(GitHubCommentDraft))]
[JsonSerializable(typeof(GitHubCommentBody))]
[JsonSerializable(typeof(NotionContentUpdate))]
internal sealed partial class ApiJson : JsonSerializerContext;
