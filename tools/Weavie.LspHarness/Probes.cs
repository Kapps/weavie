namespace Weavie.LspHarness;

/// <summary>
/// A per-language test fixture for the LSP harness: a small source file with a deliberate type error
/// (to exercise diagnostics), positions to probe hover and completion, and any project files the
/// server needs to recognize a project (tsconfig / .csproj / go.mod). Keyed by the recipe id so the
/// one harness drives every language through the same WS↔stdio bridge.
/// </summary>
internal sealed record LanguageProbe {
	public required string LanguageId { get; init; }
	public required string MainFileName { get; init; }
	public required string Source { get; init; }
	public required int HoverLine { get; init; }
	public required int HoverChar { get; init; }
	public required int CompletionLine { get; init; }
	public required int CompletionChar { get; init; }
	public IReadOnlyDictionary<string, string> ProjectFiles { get; init; } = new Dictionary<string, string>();

	public static LanguageProbe? For(string selector) => selector switch {
		"typescript" => TypeScript,
		"csharp" => CSharp,
		"go" => Go,
		_ => null,
	};

	// const greeting error → diagnostic; `add` → hover (1,9); inside body → completion (2,10).
	private static LanguageProbe TypeScript => new() {
		LanguageId = "typescript",
		MainFileName = "sample.ts",
		Source =
			"const greeting: number = \"hello\";\n" +
			"function add(a: number, b: number): number {\n" +
			"  return a + b;\n" +
			"}\n" +
			"class Point {\n" +
			"  x = 0;\n" +
			"  y = 0;\n" +
			"}\n",
		HoverLine = 1,
		HoverChar = 9,
		CompletionLine = 2,
		CompletionChar = 10,
		ProjectFiles = new Dictionary<string, string> {
			["tsconfig.json"] =
				"{\n  \"compilerOptions\": { \"strict\": true, \"target\": \"ESNext\", \"module\": \"ESNext\", \"moduleResolution\": \"Bundler\", \"noEmit\": true }\n}\n",
		},
	};

	// `Y = "hello"` → diagnostic; `Add` (line 5) → hover; inside body (line 6) → completion.
	private static LanguageProbe CSharp => new() {
		LanguageId = "csharp",
		MainFileName = "Program.cs",
		Source =
			"namespace Weavie.Harness;\n" +     // 0
			"\n" +                              // 1
			"public class Point {\n" +          // 2
			"    public int X = 0;\n" +         // 3
			"    public int Y = \"hello\";\n" + // 4  <- error
			"    public int Add(int a, int b) {\n" + // 5
			"        return a + b;\n" +         // 6
			"    }\n" +                         // 7
			"}\n",                              // 8
		HoverLine = 5,
		HoverChar = 16, // on "Add"
		CompletionLine = 6,
		CompletionChar = 16, // after "a"
		ProjectFiles = new Dictionary<string, string> {
			["Harness.csproj"] =
				"<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <TargetFramework>net10.0</TargetFramework>\n    <Nullable>enable</Nullable>\n  </PropertyGroup>\n</Project>\n",
		},
	};

	// `var x int = "hello"` → diagnostic; `add` (line 4) → hover; `fmt.` (line 10) → completion.
	private static LanguageProbe Go => new() {
		LanguageId = "go",
		MainFileName = "main.go",
		Source =
			"package main\n" +              // 0
			"\n" +                          // 1
			"import \"fmt\"\n" +            // 2
			"\n" +                          // 3
			"func add(a int, b int) int {\n" + // 4
			"\treturn a + b\n" +            // 5
			"}\n" +                         // 6
			"\n" +                          // 7
			"func main() {\n" +             // 8
			"\tvar x int = \"hello\"\n" +   // 9  <- error
			"\tfmt.Println(add(x, 2))\n" +  // 10
			"}\n",                          // 11
		HoverLine = 4,
		HoverChar = 6, // on "add"
		CompletionLine = 10,
		CompletionChar = 5, // after "fmt."
		ProjectFiles = new Dictionary<string, string> {
			["go.mod"] = "module weavieharness\n\ngo 1.21\n",
		},
	};
}
