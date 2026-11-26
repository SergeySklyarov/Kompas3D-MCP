using KompasMcp.Contracts;
using KompasMcp.Domain.Files;
using Xunit;

namespace KompasMcp.Unit;

/// <summary>The extension that NAMES a document kind, and the save target it judges.</summary>
/// <remarks>MEASURED: KOMPAS writes a named target by its extension, and the kind it writes is not always the
/// document's: with a VISIBLE application an assembly saved to 'x.m3d' lands as a part (FileType=4) while the call
/// answers success. The written file's own marker decides the answer, and a target that states another kind is
/// named in save_target_states_another_kind.
/// History: docs/decisions/files.md#save-extension-kind</remarks>
public class DocumentFileKindTests
{
    [Theory]
    [InlineData(DocumentKind.Part, ".m3d")]
    [InlineData(DocumentKind.Assembly, ".a3d")]
    [InlineData(DocumentKind.Drawing, ".cdw")]
    [InlineData(DocumentKind.Fragment, ".frw")]
    [InlineData(DocumentKind.Specification, ".spw")]
    public void EveryKindHasOneDocumentedExtension(DocumentKind kind, string extension)
    {
        Assert.Equal(extension, DocumentFileKind.ExtensionFor(kind));
    }

    [Theory]
    [InlineData(DocumentKind.Part, @"C:\work\part.m3d", true)]
    [InlineData(DocumentKind.Part, @"C:\work\part.M3D", true)]
    [InlineData(DocumentKind.Assembly, @"C:\work\asm.a3d", true)]
    [InlineData(DocumentKind.Assembly, @"C:\work\asm.m3d", false)]
    [InlineData(DocumentKind.Assembly, @"C:\work\asm", false)]
    [InlineData(DocumentKind.Drawing, @"C:\work\sheet.m3d", false)]
    [InlineData(DocumentKind.Drawing, @"C:\work\sheet.cdw", true)]
    [InlineData(DocumentKind.Specification, @"C:\work\spec.spw", true)]
    [InlineData(DocumentKind.Specification, @"C:\work\spec.m3d", false)]
    public void ASaveTargetStatesTheKindByItsExtension(DocumentKind kind, string path, bool matches)
    {
        Assert.Equal(matches, DocumentFileKind.MatchesSaveTarget(path, kind));
    }

    /// <summary>The written file's OWN marker is the evidence of its kind — the name is only what was asked for.</summary>
    /// <remarks>MEASURED: the native container is a ZIP with a UTF-16 <c>FileInfo</c> member whose
    /// <c>FileTypeName</c> names the content, and a file NAMED .m3d can hold <c>Kompas.a3d</c>. An unreadable or
    /// unknown marker is <c>null</c>: the kind is unproven, never assumed.
    /// History: docs/decisions/files.md#save-extension-kind</remarks>
    [Theory]
    [InlineData("Kompas.m3d", DocumentKind.Part)]
    [InlineData("Kompas.a3d", DocumentKind.Assembly)]
    [InlineData("Kompas.cdw", DocumentKind.Drawing)]
    [InlineData("Kompas.frw", DocumentKind.Fragment)]
    [InlineData("Kompas.spw", DocumentKind.Specification)]
    public void TheNativeMarkerNamesTheKindOfTheContent(string marker, DocumentKind kind)
    {
        var path = WriteContainer(marker);

        Assert.Equal(kind, DocumentFileKind.KindOfNativeFile(path));
    }

    [Fact]
    public void AnUnknownOrMissingMarkerIsUnprovenRatherThanGuessed()
    {
        Assert.Null(DocumentFileKind.KindOfNativeFile(WriteContainer("Kompas.unknown")));
        Assert.Null(DocumentFileKind.KindOfNativeFile(WriteContainer(null)));
        Assert.Null(DocumentFileKind.KindOfNativeFile(Path.Combine(Path.GetTempPath(), "kompas-mcp-absent.bin")));
    }

    private static string WriteContainer(string? marker)
    {
        var path = Path.Combine(Path.GetTempPath(), "kompas-mcp-tests", Guid.NewGuid().ToString("N") + ".m3d");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var archive = System.IO.Compression.ZipFile.Open(path, System.IO.Compression.ZipArchiveMode.Create);
        if (marker is not null)
        {
            var entry = archive.CreateEntry("FileInfo");
            using var writer = new StreamWriter(entry.Open(), System.Text.Encoding.Unicode);
            writer.Write("[FileInfo]\nFileTypeName=" + marker + "\n");
        }

        return path;
    }
}
