using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using Kompas6API5;
using Kompas6Constants3D;
using KompasMcp.Contracts;
using KompasMcp.Contracts.Ipc;
using KompasMcp.Domain.Files;

namespace KompasMcp.Api5Adapter;

/// <summary>STL export through the documented additional-format route (block G5).</summary>
/// <remarks>DOC: <c>ksdocument3d_saveastoadditionformat.html</c> — «SaveAsToAdditionFormat - Сохранить
/// модель в файле формата SAT, XT, STEP, IGES, VRML, STL»; the format parameters come from
/// <c>ksDocument3D::AdditionFormatParam</c> (<c>ksdocument3d_additionformatparam.html</c>,
/// <c>ksadditionformatparam.html</c>). INVARIANT: the converter's boolean is NOT proof of a file — the
/// answer is built from the bytes on disk.
/// History: docs/decisions/geometry.md#stl</remarks>
public sealed partial class Api5Session
{
    /// <summary>Tolerance of the re-read tessellation parameters: the parameter object keeps reals, so an
    /// exact comparison reports "not applied" for a value that was applied. The level does not depend on
    /// it; the note <c>tessellation_parameters_not_applied</c> does.</summary>
    private const double TessellationToleranceMm = 1e-9;

    public ExportStlResultDto ExportStl(ExportStlCommand command)
    {
        // The write parameters (format code, binary flag, accuracy) are built by a PURE function so that
        // the mapping and the accuracy guard are testable without KOMPAS.
        StlExportPlan plan;
        try
        {
            plan = StlExportPlan.For(command.Binary, command.MaxEdgeLengthMm, command.NormalAngleDeg);
        }
        catch (ArgumentOutOfRangeException ex)
        {
            throw new KompasContractException(ErrorCodes.InvalidArgument, ex.Message, RetryPolicy.Never);
        }

        // INVARIANT: the format constant has to be a member of the vendor enumeration. STL is
        // (format_STL = 6), so on a correct build this guard never fires; it is here so that a format NOT
        // present in D3FormatConvType refuses with FORMAT_UNAVAILABLE BEFORE COM instead of writing a file
        // with an undefined format number. History: docs/decisions/geometry.md#stl
        if (!Enum.IsDefined(typeof(D3FormatConvType), plan.FormatCode))
        {
            throw new KompasContractException(
                ErrorCodes.FormatUnavailable,
                $"Формат STL (format_STL={plan.FormatCode}) отсутствует в перечислении D3FormatConvType этой "
                + "версии: документированного маршрута записи в него нет.",
                RetryPolicy.Never);
        }

        var document = RequireDocument(command.DocumentId);
        if (document.Kind != DocumentKind.Part)
        {
            // A drawing has no ksDocument3D at all, and an assembly is a different export target than the
            // one this tool publishes. Named before any COM call.
            throw new KompasContractException(
                ErrorCodes.WrongDocumentKind,
                $"Экспорт STL применим к детали; документ «{document.Id}» имеет тип {document.Kind}.",
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

        var destination = command.TargetPath;
        // The parent directory is created, exactly as kompas_export_step and kompas_export_drawing do:
        // one product, one answer to "what if the folder does not exist yet".
        Directory.CreateDirectory(Path.GetDirectoryName(destination) ?? ".");

        var wantedKind = plan.FormatBinary
            ? StlFileInspector.KindBinary
            : StlFileInspector.KindText;

        // INVARIANT: the request is not evidence of the file kind. MEASURED on the target build: the
        // kernel's formatBinary yields the OPPOSITE kind from what the help page states (true -> text),
        // so the server writes the documented value, CHECKS the file, and writes the other value once if
        // the kind disagrees. The request is honoured by measurement, never by trusting the flag.
        // History: docs/decisions/geometry.md#stl
        var writtenFlag = plan.FormatBinary;
        var formatBinaryReadBack = plan.FormatBinary;
        var readLength = plan.LengthMm;
        var readAngle = plan.AngleDeg;
        var kindMatches = false;
        var detectedKind = StlFileInspector.KindUnknown;
        StlFileInspector.StlFacts facts = null!;

        for (var attempt = 0; attempt < 2; attempt++)
        {
            var parameter = (ksAdditionFormatParam)document.Document3D.AdditionFormatParam();
            parameter.Init();
            parameter.format = (short)plan.FormatCode;
            parameter.formatBinary = writtenFlag;
            parameter.length = plan.LengthMm;
            parameter.angle = plan.AngleDeg;

            // Read the values BACK from the parameter object: an echo of the request would hide a setter
            // that did not take. A member that does not read keeps the requested value and is named below.
            readLength = ReadBack(() => parameter.length, plan.LengthMm);
            readAngle = ReadBack(() => parameter.angle, plan.AngleDeg);
            formatBinaryReadBack = ReadBack(() => parameter.formatBinary, writtenFlag);

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

            // INVARIANT: SaveAsToAdditionFormat = true with a missing or empty file is NOT success. A
            // silent "success" without a file does not exist — absence of bytes is a refusal, not a PASS.
            if (!saved || !File.Exists(destination) || new FileInfo(destination).Length == 0)
            {
                throw new KompasContractException(
                    ErrorCodes.ExportFailed,
                    "Экспорт STL не подтверждён: конвертер вернул отказ либо файл не создан или пуст.",
                    RetryPolicy.SameOperationId,
                    partialEffects: File.Exists(destination));
            }

            var detected = StlFileInspector.DetectKind(destination);
            detectedKind = detected;
            facts = StlFileInspector.Inspect(destination, detected);
            kindMatches = string.Equals(detected, wantedKind, StringComparison.Ordinal);
            if (kindMatches)
            {
                break;
            }

            if (attempt == 0)
            {
                writtenFlag = !writtenFlag;
            }
        }

        // The body box is the documented "solid bodies only" mode: STL carries tessellated SOLID bodies,
        // so the whole-document box (sketches and auxiliary geometry included) is the wrong comparison.
        var bodyBox = TryReadGabaritCorners(document, full: false, customizable: false, out var min, out var max)
            ? new BoundingBoxDto(min, max)
            : null;

        // INVARIANT: the box tolerance is DERIVED from the mesh step the FILE holds, never from the requested
        // one. MEASURED: the STL converter does not let length/angle change the mesh, so a tolerance taken
        // from the request judges the request instead of the artefact — a fine request failed the box check
        // on a file whose step was coarse. A chord over one step deviates from the surface by at most that
        // step, which is why the measured step is the honest tolerance. The requested value is only a
        // fallback, and that fallback is NAMED.
        var tolerance = facts.EdgeRead ? facts.MaxEdgeMm : plan.LengthMm;
        var boxCompared = facts.BoxRead && bodyBox is not null;
        double? deviation = null;
        if (boxCompared)
        {
            var largest = 0d;
            for (var axis = 0; axis < 3; axis++)
            {
                largest = Math.Max(largest, Math.Abs(facts.MinMm[axis] - bodyBox!.MinMm[axis]));
                largest = Math.Max(largest, Math.Abs(facts.MaxMm[axis] - bodyBox.MaxMm[axis]));
            }

            deviation = largest;
        }

        var unverified = new List<string>(facts.UnverifiedAspects);
        if (!facts.EdgeRead)
        {
            unverified.Add(
                "mesh_step_not_measured — шаг сетки из файла не прочитан (нет ни одного треугольника с "
                + "тремя вершинами), поэтому допуск сверки габарита взят из ЗАПРОШЕННОГО шага, который к "
                + "сетке STL не применяется");
        }

        if (!boxCompared)
        {
            unverified.Add("body_box_not_read — габарит тела или вершин триангуляции нет, габариты не сверены");
        }
        else if (deviation > tolerance)
        {
            unverified.Add(
                "triangle_box_differs_from_body — габарит триангуляции разошёлся с габаритом тела на "
                + $"{deviation.Value.ToString("0.####", CultureInfo.InvariantCulture)} мм при допуске "
                + $"{tolerance.ToString("0.####", CultureInfo.InvariantCulture)} мм");
        }

        if (!kindMatches)
        {
            unverified.Add(
                $"binary_flag_mismatch — запрошен вид файла «{wantedKind}», а на диске «{detectedKind}»: "
                + "признак formatBinary на этой сборке даёт вид, обратный справке, и запись выполнена с "
                + "противоположным значением");
        }

        // A re-read parameter is compared with a TOLERANCE, not by exact equality: the parameter object
        // stores reals, and an exact comparison published tessellation_parameters_not_applied for a value
        // the object had applied. The level does not depend on this note.
        if (Math.Abs(readLength - plan.LengthMm) > TessellationToleranceMm
            || Math.Abs(readAngle - plan.AngleDeg) > TessellationToleranceMm)
        {
            unverified.Add(
                "tessellation_parameters_not_applied — параметры триангуляции не удержались в объекте "
                + $"параметров: запрошено {plan.LengthMm.ToString("R", CultureInfo.InvariantCulture)}/"
                + $"{plan.AngleDeg.ToString("R", CultureInfo.InvariantCulture)}, прочитано "
                + $"{readLength.ToString("R", CultureInfo.InvariantCulture)}/"
                + $"{readAngle.ToString("R", CultureInfo.InvariantCulture)}");
        }

        // INVARIANT: the level never claims more than was checked. The box comparison reads the LIVE body
        // box and the FILE's vertices, but a matching box says nothing about the LOCAL profile: MEASURED on
        // the customer's grooved core, the mesh kept 0,700/0,420 mm grooves for a model of 0,6/0,3 mm while
        // the box matched within 0,05 mm. The level is therefore structure_checked, and the local profile is
        // NAMED as unverified — a client that needs the profile must compare it outside the server.
        // History: docs/decisions/geometry.md#stl
        var boxMatches = boxCompared && deviation <= tolerance;
        const VerificationLevel level = VerificationLevel.StructureChecked;

        unverified.Add(
            "stl_tessellation_not_applied — точность триангуляции к сетке STL не применяется: length и angle "
            + "записаны в объект параметров и прочитаны обратно, но число треугольников от них не зависит "
            + "(измерено: один и тот же файл при шаге 0,0001 и 10 мм; справка приписывает эти члены "
            + "интерфейсу дополнительных форматов, формату STL — нет). Запрошенные числа не выдаются за "
            + "достигнутую точность; шаг сетки измерен по файлу (" + MeshStepWord(facts, tolerance) + ")");

        unverified.Add(
            "local_profile_not_verified — габарит сетки не доказывает локальный профиль: сверка габарита "
            + "видит только крайние координаты, а глубина канавки или посадка грани на этой сетке могут "
            + "отличаться от модели. Отклонение вершин от аналитической поверхности документированным "
            + "маршрутом не читается, поэтому уровень ответа — structure_checked, а локальный профиль "
            + "сверяется вне сервера (например, по выгруженному STEP)");

        var warnings = new List<string>
        {
            "Точность STL не применена: max_edge_length_mm и normal_angle_deg записаны в параметры "
            + "экспорта, но сетку STL задаёт документ (измерено на v24). Шаг сетки в файле — "
            + MeshStepWord(facts, tolerance) + "; для печати или локального контроля профиля "
            + "триангулируйте выгруженный STEP внешним инструментом.",
        };

        // INVARIANT: the box is the BROADEST bound of the artefact, and the body box it is compared with is
        // conservative for trimmed analytic surfaces (MEASURED: a cut torus reported 23 mm while its mesh
        // spans 22,98238). Both facts belong to the answer, not to a comment.
        unverified.Add(
            "body_box_conservative — габарит тела читается ksPart.GetGabarit(full=false), который у "
            + "обрезанных аналитических поверхностей может быть больше точной оболочки (измерено: тело "
            + "23,0000 мм против сетки 22,98238 мм), поэтому совпадение габаритов не доказывает "
            + "совпадения геометрии");

        var checks = new List<NamedCheck>
        {
            new("file_created", true, Observed: facts.ByteLength.ToString(CultureInfo.InvariantCulture) + " байт"),
            new("triangle_count_read_from_file", facts.TriangleCount > 0,
                Observed: facts.TriangleCount.ToString(CultureInfo.InvariantCulture) + " (" + facts.CountSource + ")"),
            new("file_kind_matches_request", kindMatches, Observed: facts.CountSource),
            new("triangle_box_matches_body", boxMatches,
                Observed: deviation is null
                    ? "габариты не сверены"
                    : deviation.Value.ToString("0.####", CultureInfo.InvariantCulture) + " мм при допуске "
                      + tolerance.ToString("0.####", CultureInfo.InvariantCulture) + " мм (шаг сетки из файла)"),
        };

        return new ExportStlResultDto
        {
            TargetPath = destination,
            Bytes = facts.ByteLength,
            TriangleCount = facts.TriangleCount,
            TriangleCountSource = facts.CountSource,
            BinaryRequested = command.Binary,
            FileKindDetected = detectedKind,
            FormatBinaryWritten = formatBinaryReadBack,
            Format = StlExportPlan.FormatName,
            AppliedLengthMm = readLength,
            AppliedAngleDeg = readAngle,
            MeshStepMm = facts.EdgeRead ? facts.MaxEdgeMm : null,
            TriangleBox = facts.BoxRead ? new BoundingBoxDto(facts.MinMm, facts.MaxMm) : null,
            BodyBox = bodyBox,
            BoxDeviationMm = deviation,
            BoxToleranceMm = tolerance,
            ReachedLevel = level,
            Verification = new VerificationDto(level, checks, unverified),
            UnverifiedAspects = unverified,
            Warnings = warnings,
            SourceRevision = document.Revision,
        };
    }

    /// <summary>The measured mesh step in words; the requested step is named as the fallback it is.</summary>
    private static string MeshStepWord(StlFileInspector.StlFacts facts, double tolerance) => facts.EdgeRead
        ? "наибольшее ребро " + tolerance.ToString("0.####", CultureInfo.InvariantCulture) + " мм"
        : "не измерен, взят запрошенный шаг " + tolerance.ToString("0.####", CultureInfo.InvariantCulture) + " мм";

    /// <summary>A parameter member read back from the object; a member that does not read keeps the value
    /// that was requested and is named in <c>unverified_aspects</c> by the caller.</summary>
    private static double ReadBack(Func<double> reader, double fallback)
    {
        try
        {
            var value = reader();
            return double.IsFinite(value) ? value : fallback;
        }
        catch (Exception ex) when (ex is COMException or MissingMethodException or TargetInvocationException)
        {
            return fallback;
        }
    }

    private static bool ReadBack(Func<bool> reader, bool fallback)
    {
        try
        {
            return reader();
        }
        catch (Exception ex) when (ex is COMException or MissingMethodException or TargetInvocationException)
        {
            return fallback;
        }
    }
}
