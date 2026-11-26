using System.IO.Compression;
using System.Text;
using KompasMcp.Contracts;

namespace KompasMcp.Domain.Files;

/// <summary>The extension that NAMES a document kind — one table for the rescue copy and for a save target.</summary>
/// <remarks>INVARIANT: the extension NAMES the kind, it does not decide what is written: KOMPAS writes a named
/// target by it, and with a VISIBLE application an assembly saved to a <c>.m3d</c> path lands as a PART
/// (FileType=4, no components) while the call answers success; a hidden application keeps the document's type.
/// MEASURED. So the written file's OWN marker is read after the write: a file of another kind is never a success,
/// and a target whose extension states another kind is named in <c>save_target_states_another_kind</c>.
/// DOC: «Файл детали имеет расширение m3d» and «Файл сборки имеет расширение a3d»
/// (<c>47_2_1_1_trekhmernye_modeli.html</c>); the «Допустимые расширения» column of
/// <c>ksrequestfilestypeenum.html</c> names frw, cdw, m3d, a3d, kdw as KOMPAS document files.
/// ASSUMPTION: a specification is <c>.spw</c> - no mirrored page states it; the name follows the extension the
/// <c>.spw</c> open route already uses. LIMIT: a text document (<c>.kdw</c>) has no <see cref="DocumentKind"/>.
/// History: docs/decisions/files.md#save-extension-kind</remarks>
public static class DocumentFileKind
{
    /// <summary>The extension of the kind, or <c>null</c> when the kind has none documented.</summary>
    public static string? ExtensionFor(DocumentKind kind) => kind switch
    {
        DocumentKind.Part => ".m3d",
        DocumentKind.Assembly => ".a3d",
        DocumentKind.Drawing => ".cdw",
        DocumentKind.Fragment => ".frw",
        DocumentKind.Specification => ".spw",
        _ => null,
    };

    /// <summary>Whether the path's extension states this document kind.</summary>
    /// <remarks>INVARIANT: compared case-insensitively, because Windows file names are; a path WITHOUT an
    /// extension does not state any kind and is therefore not a match.</remarks>
    public static bool MatchesSaveTarget(string path, DocumentKind kind) =>
        ExtensionFor(kind) is { } expected
        && string.Equals(Path.GetExtension(path), expected, StringComparison.OrdinalIgnoreCase);

    /// <summary>The kind the native container says it holds, or <c>null</c> when the marker cannot be read.</summary>
    /// <remarks>MEASURED: a native KOMPAS document is a ZIP container whose <c>FileInfo</c> member names the kind
    /// of its CONTENT — <c>Kompas.m3d</c> (part), <c>Kompas.a3d</c> (assembly), <c>Kompas.cdw</c> (drawing),
    /// <c>Kompas.frw</c> (fragment), <c>Kompas.spw</c> (specification) — and a file NAMED .m3d can hold an assembly.
    /// INVARIANT: the marker is read for VERIFICATION of what a save wrote; an unreadable or unknown marker is
    /// <c>null</c>, never a guess, so the caller names the kind as unproven instead of assuming it.
    /// LIMIT: the entry is an internal format, not a documented API.
    /// History: docs/decisions/files.md#save-extension-kind</remarks>
    public static DocumentKind? KindOfNativeFile(string path)
    {
        try
        {
            using var archive = ZipFile.OpenRead(path);
            var entry = archive.GetEntry("FileInfo");
            if (entry is null)
            {
                return null;
            }

            using var reader = new StreamReader(entry.Open(), Encoding.Unicode);
            string? name = null;
            while (reader.ReadLine() is { } line)
            {
                var separator = line.IndexOf('=');
                if (separator > 0
                    && line[..separator].Trim().Equals("FileTypeName", StringComparison.OrdinalIgnoreCase))
                {
                    name = line[(separator + 1)..].Trim();
                }
            }

            return name switch
            {
                "Kompas.m3d" => DocumentKind.Part,
                "Kompas.a3d" => DocumentKind.Assembly,
                "Kompas.cdw" => DocumentKind.Drawing,
                "Kompas.frw" => DocumentKind.Fragment,
                "Kompas.spw" => DocumentKind.Specification,
                _ => null,
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException
            or NotSupportedException or ArgumentException)
        {
            return null;
        }
    }
}
