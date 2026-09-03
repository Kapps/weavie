namespace Weavie.LspHarness;

/// <summary>
/// A per-language test fixture for the LSP harness: a small source file with a deliberate type error (for
/// diagnostics), positions to probe hover and completion, and any project files the server needs (tsconfig /
/// .csproj / go.mod / pyproject.toml / Cargo.toml). Keyed by recipe id so one harness drives every language
/// through the same bridge.
/// </summary>
internal sealed record LanguageProbe {
	public required string LanguageId { get; init; }
	public required string MainFileName { get; init; }
	public required string Source { get; init; }
	public required int HoverLine { get; init; }
	public required int HoverChar { get; init; }
	public required int CompletionLine { get; init; }
	public required int CompletionChar { get; init; }
	public bool WaitForDiagnosticsBeforeRequests { get; init; }
	public IReadOnlyDictionary<string, string> ProjectFiles { get; init; } = new Dictionary<string, string>();

	public static LanguageProbe? For(string selector) => selector switch {
		"typescript" => TypeScript,
		"csharp" => CSharp,
		"go" => Go,
		"python" => Python,
		"rust" => Rust,
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

	// `greeting` is a type error; `add` → hover; inside its body → completion.
	private static LanguageProbe Python => new() {
		LanguageId = "python",
		MainFileName = "sample.py",
		Source =
			"def add(a: int, b: int) -> int:\n" + // 0
			"    return a + b\n" +                 // 1
			"\n" +                                  // 2
			"greeting: int = \"hello\"\n" +       // 3  <- error
			"\n" +                                  // 4
			"class Point:\n" +                     // 5
			"    x: int = 0\n",                     // 6
		HoverLine = 0,
		HoverChar = 5, // on "add"
		CompletionLine = 1,
		CompletionChar = 12, // inside the expression
		ProjectFiles = new Dictionary<string, string> {
			["pyproject.toml"] = "[tool.pyright]\ntypeCheckingMode = \"strict\"\n",
		},
	};

	// `greeting` is a type error; `add` → hover; inside its body → completion.
	private static LanguageProbe Rust => new() {
		LanguageId = "rust",
		MainFileName = "src/lib.rs",
		Source =
			"pub fn add(a: i32, b: i32) -> i32 {\n" + // 0
			"    a + b\n" +                            // 1
			"}\n" +                                   // 2
			"\n" +                                     // 3
			"pub fn broken() {\n" +                  // 4
			"    let greeting: i32 = \"hello\";\n" + // 5  <- error
			"}\n",                                    // 6
		HoverLine = 0,
		HoverChar = 8, // on "add"
		CompletionLine = 1,
		CompletionChar = 5, // after "a"
		WaitForDiagnosticsBeforeRequests = true,
		ProjectFiles = new Dictionary<string, string> {
			["Cargo.toml"] = "[package]\nname = \"weavie-harness\"\nversion = \"0.1.0\"\nedition = \"2024\"\n",
		},
	};
}
