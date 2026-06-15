using Weavie.Core.Documents;
using Weavie.Core.FileSystem;
using Xunit;

namespace Weavie.Core.Tests;

public sealed class InMemoryDocumentModelTests
{
    [Fact]
    public void NewModel_IsNotDirty()
    {
        var model = new InMemoryDocumentModel("/f", "x", new InMemoryFileSystem());
        Assert.False(model.IsDirty);
    }

    [Fact]
    public void ApplyEdit_MakesDirty_SaveClearsDirty()
    {
        var fs = new InMemoryFileSystem();
        var model = new InMemoryDocumentModel("/f", "x", fs);

        model.ApplyEdit(TextEdit.Insert(new Position(1, 2), "y"));
        Assert.True(model.IsDirty);
        Assert.Equal("xy", model.GetText());

        model.Save();
        Assert.False(model.IsDirty);
        Assert.Equal("xy", fs.ReadAllText("/f"));
    }

    [Fact]
    public void OpenFromDisk_LoadsExistingContents()
    {
        var fs = new InMemoryFileSystem();
        fs.WriteAllText("/f", "on disk");
        var model = InMemoryDocumentModel.OpenFromDisk("/f", fs);
        Assert.Equal("on disk", model.GetText());
    }

    [Fact]
    public void OpenFromDisk_MissingFile_IsEmpty()
    {
        var model = InMemoryDocumentModel.OpenFromDisk("/missing", new InMemoryFileSystem());
        Assert.Equal(string.Empty, model.GetText());
    }

    [Fact]
    public void Selection_RoundTrips()
    {
        var model = new InMemoryDocumentModel("/f", "abc", new InMemoryFileSystem());
        var selection = new TextRange(new Position(1, 1), new Position(1, 3));
        model.Selection = selection;
        Assert.Equal(selection, model.Selection);
    }
}
