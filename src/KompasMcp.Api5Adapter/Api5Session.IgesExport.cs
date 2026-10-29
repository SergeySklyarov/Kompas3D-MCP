using System.Runtime.InteropServices;
using Kompas6API5;
using Kompas6Constants3D;
using KompasMcp.Contracts;
using KompasMcp.Contracts.Ipc;
using KompasMcp.Domain.Files;

namespace KompasMcp.Api5Adapter;

/// <summary>IGES export through the documented additional-format route (block IGES).</summary>
/// <remarks>DOC: <c>ksdocument3d_saveastoadditionformat.html</c> — «SaveAsToAdditionFormat - Сохранить
/// модель в файле формата SAT, XT, STEP, IGES, VRML, STL»; <c>d3formatconvtype.html</c> —
/// <c>format_IGES = 4</c>. INVARIANT: the converter's boolean is NOT proof of a file, and the answer is
/// built from the bytes on disk. History: docs/decisions/geometry.md#iges</remarks>
public sealed partial class Api5Session
{
    /// <summary>D3FormatConvType.format_IGES — write-side selector.</summary>
    private const short FormatIges = 4;

    /// <summary>Extensions the caller may ask for; anything else is refused by field name before COM.</summary>
    private static readonly string[] IgesExtensions = { ".igs", ".iges" };

    public ExportIgesResultDto ExportIges(ExportIgesCommand command)
    {
        var extension = Path.GetExtension(command.OutputPath);
        if (!IgesExtensions.Any(known => string.Equals(known, extension, StringComparison.OrdinalIgnoreCase)))
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                $"Экспорт IGES принимает расширение .igs или .iges, а запрошено «{extension}».",
                RetryPolicy.Never,
                details: new Dictionary<string, object?> { ["field"] = "output_path", ["extension"] = extension });
        }

        // INVARIANT: the format constant has to be a member of the vendor enumeration, so a build without
        // it refuses with FORMAT_UNAVAILABLE before COM instead of writing an undefined format number.
        // MEASURED: Enum.IsDefined refuses a short for an int-backed enum, so the value is widened.
        if (!Enum.IsDefined(typeof(D3FormatConvType), (int)FormatIges))
        {
            throw new KompasContractException(
                ErrorCodes.FormatUnavailable,
                $"Формат IGES (format_IGES={FormatIges}) отсутствует в перечислении D3FormatConvType этой "
                + "версии: документированного маршрута записи в него нет.",
                RetryPolicy.Never);
        }

        var document = RequireDocument(command.DocumentId);
        if (document.Kind is not (DocumentKind.Part or DocumentKind.Assembly))
        {
            // A drawing has no ksDocument3D at all: named before any COM call, never a COM exception.
            throw new KompasContractException(
                ErrorCodes.WrongDocumentKind,
                $"Экспорт IGES применим к детали или сборке; документ «{document.Id}» имеет тип {document.Kind}.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?> { ["kind"] = document.Kind.ToString() });
        }

        if (command.ExpectedRevision != document.Revision)
        {
            throw new KompasContractException(
                ErrorCodes.RevisionConflict,
                $"Экспорт запрошен для ревизии {command.ExpectedRevision}, у документа {document.Revision}.",
                RetryPolicy.ReacquireContext,
                details: new Dictionary<string, object?>
                {
                    ["requested_revision"] = command.ExpectedRevision,
                    ["current_revision"] = document.Revision,
                });
        }

        var destination = command.OutputPath;
        // The parent directory is created, exactly as kompas_export_step and kompas_export_stl do.
        Directory.CreateDirectory(Path.GetDirectoryName(destination) ?? ".");

        var parameter = (ksAdditionFormatParam)document.Document3D.AdditionFormatParam();
        parameter.Init();
        parameter.format = FormatIges;

        bool saved;
        try
        {
            saved = document.Document3D.SaveAsToAdditionFormat(destination, parameter);
        }
        catch (COMException ex)
        {
            throw new KompasContractException(
                ErrorCodes.ExportFailed,
                $"SaveAsToAdditionFormat бросил исключение: {ex.Message}",
                RetryPolicy.SameOperationId,
                hresult: ex.HResult,
                partialEffects: File.Exists(destination));
        }

        // INVARIANT: true with a missing or empty file is NOT success — absence of bytes is a refusal.
        if (!saved || !File.Exists(destination) || new FileInfo(destination).Length == 0)
        {
            throw new KompasContractException(
                ErrorCodes.ExportFailed,
                "Экспорт IGES не подтверждён: конвертер вернул отказ либо файл не создан или пуст.",
                RetryPolicy.SameOperationId,
                partialEffects: File.Exists(destination));
        }

        var facts = IgesFileInspector.Inspect(destination);
        if (!facts.StructureParsed)
        {
            // The file exists but disagrees with its own declaration: a refusal that names the reason and
            // says the file system is not unchanged.
            throw new KompasContractException(
                ErrorCodes.ExportFailed,
                "Экспорт IGES записал файл, но разбор структуры не сошёлся: "
                + string.Join("; ", facts.UnverifiedAspects),
                RetryPolicy.SameOperationId,
                partialEffects: true,
                details: new Dictionary<string, object?>
                {
                    ["output_path"] = destination,
                    ["byte_length"] = facts.ByteLength,
                    ["entities"] = facts.EntityCount,
                    ["unverified_aspects"] = facts.UnverifiedAspects,
                });
        }

        return new ExportIgesResultDto
        {
            OutputPath = destination,
            ByteLength = facts.ByteLength,
            Sha256 = FileHash.Sha256(destination),
            EntityCount = facts.EntityCount,
            StartRecords = facts.StartRecords,
            GlobalRecords = facts.GlobalRecords,
            DirectoryRecords = facts.DirectoryRecords,
            ParameterRecords = facts.ParameterRecords,
            TerminateRecords = facts.TerminateRecords,
            UnitsFlag = facts.UnitsFlag,
            UnitsName = facts.UnitsName,
            UnitsMmPerUnit = facts.UnitsMmPerUnit,
            // Structure of the file only: the format carries no volume to compare against, and a
            // roundtrip import is not documented (load-side selector is out of this order).
            ReachedLevel = VerificationLevel.SyntaxChecked,
            UnverifiedAspects = facts.UnverifiedAspects.Concat(new[]
            {
                "geometry_not_validated — проверена только структура файла, не форма: объём и габарит "
                + "переносятся отдельной проверкой",
            }).ToArray(),
            SourceRevision = document.Revision,
        };
    }
}
