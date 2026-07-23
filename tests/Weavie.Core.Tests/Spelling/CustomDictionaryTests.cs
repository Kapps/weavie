using Weavie.Core.Spelling;
using Xunit;

namespace Weavie.Core.Tests;

public sealed class CustomDictionaryTests : IDisposable {
	private readonly string _directory = Path.Combine(Path.GetTempPath(), "weavie-spelling-tests", Guid.NewGuid().ToString("N"));

	public void Dispose() {
		try {
			Directory.Delete(_directory, recursive: true);
		} catch (IOException) {
		} catch (UnauthorizedAccessException) {
		}
	}

	[Fact]
	public void Constructor_DoesNotCreateProjectDirectoryUntilAdd() {
		string project = Path.Combine(_directory, "project");
		Directory.CreateDirectory(project);
		string dictionaryPath = Path.Combine(project, ".weavie", "dictionary.txt");

		using var dictionary = CustomDictionary.ForProject(project, enableWatcher: false);

		Assert.False(File.Exists(dictionaryPath));
		Assert.False(Directory.Exists(Path.GetDirectoryName(dictionaryPath)!));
	}

	[Fact]
	public void Add_NormalizesDeduplicatesAndSortsPersistedWords() {
		string dictionaryPath = Path.Combine(_directory, "dictionary.txt");
		using var dictionary = new CustomDictionary(dictionaryPath, enableWatcher: false);

		dictionary.Add("Zulu");
		dictionary.Add("alpha");
		dictionary.Add("O’Reilly");
		dictionary.Add("ALPHA");

		Assert.Equal(["alpha", "O'Reilly", "Zulu"], File.ReadAllLines(dictionaryPath));
		Assert.True(dictionary.Contains("o'reilly"));
		Assert.True(dictionary.Contains("O’Reilly"));
	}

	[Fact]
	public void Reload_UsesExternalEditsAndRaisesChanged() {
		string dictionaryPath = Path.Combine(_directory, "dictionary.txt");
		Directory.CreateDirectory(_directory);
		File.WriteAllText(dictionaryPath, "first\n");
		using var dictionary = new CustomDictionary(dictionaryPath, enableWatcher: false);
		int changed = 0;
		dictionary.Changed += () => changed++;

		File.WriteAllText(dictionaryPath, "second\n");
		dictionary.Reload();

		Assert.False(dictionary.Contains("first"));
		Assert.True(dictionary.Contains("second"));
		Assert.Equal(1, changed);
	}

	[Fact]
	public void Constructor_RetainsEmptySnapshotForMalformedDictionaryWords() {
		string dictionaryPath = Path.Combine(_directory, "dictionary.txt");
		Directory.CreateDirectory(_directory);
		File.WriteAllText(dictionaryPath, "valid\nnot a word\n");

		using var dictionary = new CustomDictionary(dictionaryPath, enableWatcher: false);

		Assert.Empty(dictionary.Words);
		Assert.Contains("Line 2", Assert.IsType<SpellDictionaryException>(dictionary.LastLoadError).Message);
	}

	[Fact]
	public void Reload_RetainsLastGoodSnapshotAndSignalsRepair() {
		string dictionaryPath = Path.Combine(_directory, "dictionary.txt");
		Directory.CreateDirectory(_directory);
		File.WriteAllText(dictionaryPath, "valid\n");
		using var dictionary = new CustomDictionary(dictionaryPath, enableWatcher: false);
		var errors = new List<SpellDictionaryException?>();
		dictionary.LoadErrorChanged += errors.Add;

		File.WriteAllText(dictionaryPath, "not a word\n");
		Assert.Throws<SpellDictionaryException>(dictionary.Reload);

		Assert.True(dictionary.Contains("valid"));
		Assert.NotNull(dictionary.LastLoadError);
		Assert.Single(errors);

		File.WriteAllText(dictionaryPath, "repaired\n");
		dictionary.Reload();

		Assert.False(dictionary.Contains("valid"));
		Assert.True(dictionary.Contains("repaired"));
		Assert.Null(dictionary.LastLoadError);
		Assert.Equal([false, true], errors.Select(error => error is null));
	}

	[Fact]
	public void Add_ReportsAnUnreadableExternalEditWithoutDiscardingTheLastGoodSnapshot() {
		string dictionaryPath = Path.Combine(_directory, "dictionary.txt");
		Directory.CreateDirectory(_directory);
		File.WriteAllText(dictionaryPath, "valid\n");
		using var dictionary = new CustomDictionary(dictionaryPath, enableWatcher: false);

		File.WriteAllText(dictionaryPath, "not a word\n");

		Assert.Throws<SpellDictionaryException>(() => dictionary.Add("newword"));
		Assert.True(dictionary.Contains("valid"));
		Assert.NotNull(dictionary.LastLoadError);
	}

	[Fact]
	public void Add_AtomicallyReplacesTheDictionaryAndCleansItsTemporaryFile() {
		string dictionaryPath = Path.Combine(_directory, "dictionary.txt");
		Directory.CreateDirectory(_directory);
		File.WriteAllText(dictionaryPath, "beta\n");
		using var dictionary = new CustomDictionary(dictionaryPath, enableWatcher: false);

		dictionary.Add("alpha");

		Assert.Equal(["alpha", "beta"], File.ReadAllLines(dictionaryPath));
		Assert.Empty(Directory.EnumerateFiles(_directory, ".dictionary.txt.*.tmp"));
	}

	[Fact]
	public void UserDictionary_PreservesAFileSymlinkWhenAddingWords() {
		Directory.CreateDirectory(_directory);
		string target = Path.Combine(_directory, "target.txt");
		string dictionaryPath = Path.Combine(_directory, "dictionary.txt");
		File.WriteAllText(target, "first\n");
		File.CreateSymbolicLink(dictionaryPath, target);
		using var dictionary = new CustomDictionary(dictionaryPath, enableWatcher: false);

		dictionary.Add("second");

		Assert.NotNull(new FileInfo(dictionaryPath).LinkTarget);
		Assert.Equal(["first", "second"], File.ReadAllLines(target));
	}

	[Fact]
	public void ProjectDictionary_RejectsLinkedDictionaryDirectory() {
		string project = Path.Combine(_directory, "project");
		string outside = Path.Combine(_directory, "outside");
		Directory.CreateDirectory(project);
		Directory.CreateDirectory(outside);
		Directory.CreateSymbolicLink(Path.Combine(project, ".weavie"), outside);
		using var dictionary = CustomDictionary.ForProject(project, enableWatcher: false);

		Assert.NotNull(dictionary.LastLoadError);
		Assert.Empty(dictionary.Words);
		Assert.Throws<SpellDictionaryException>(() => dictionary.Add("escapedword"));
		Assert.False(File.Exists(Path.Combine(outside, "dictionary.txt")));
	}

	[Fact]
	public void ProjectDictionary_RejectsLinkedBackingFile() {
		string project = Path.Combine(_directory, "project");
		string outside = Path.Combine(_directory, "outside");
		Directory.CreateDirectory(Path.Combine(project, ".weavie"));
		Directory.CreateDirectory(outside);
		string outsideFile = Path.Combine(outside, "dictionary.txt");
		File.WriteAllText(outsideFile, "outsideword\n");
		File.CreateSymbolicLink(Path.Combine(project, ".weavie", "dictionary.txt"), outsideFile);
		using var dictionary = CustomDictionary.ForProject(project, enableWatcher: false);

		Assert.NotNull(dictionary.LastLoadError);
		Assert.False(dictionary.Contains("outsideword"));
		Assert.Throws<SpellDictionaryException>(() => dictionary.Add("escapedword"));
		Assert.Equal("outsideword\n", File.ReadAllText(outsideFile));
	}

	[Fact]
	public void ProjectDictionary_RejectsLinkedWorkspaceRoot() {
		string realProject = Path.Combine(_directory, "real-project");
		string linkedProject = Path.Combine(_directory, "linked-project");
		Directory.CreateDirectory(realProject);
		Directory.CreateSymbolicLink(linkedProject, realProject);
		using var dictionary = CustomDictionary.ForProject(linkedProject, enableWatcher: false);

		Assert.NotNull(dictionary.LastLoadError);
		Assert.Throws<SpellDictionaryException>(() => dictionary.Add("escapedword"));
		Assert.False(Directory.Exists(Path.Combine(realProject, ".weavie")));
	}
}
