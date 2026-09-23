using System.Runtime.InteropServices;
using Kompas6API5;
using KompasMcp.Contracts;
using KompasMcp.Contracts.Ipc;
using KompasMcp.Domain.References;

namespace KompasMcp.Api5Adapter;

/// <summary>
/// Оболочка — тонкостенный элемент (docs/05 SM-13, очередь B5).
/// </summary>
/// <remarks>
/// <para>
/// <b>Маршрут — документированный API5, и он же измерен числом.</b> Страница
/// <c>ksshelldefinition.html</c> («Тонкостенная оболочка (Интерфейсы ksShellDefinition,
/// IShellDefinition)») описывает интерфейс, который «можно получить, используя метод интерфейса
/// элемента модели <c>ksEntity::GetDefinition</c>»; состав — свойства <c>thickness</c> и
/// <c>thinType</c> и метод <c>FaceArray()</c>, возвращающий «динамический массив удаляемых граней
/// <c>ksEntityCollection</c>». Тип объекта — <c>o3d_shellOperation = 43</c> (<c>obj3dtype.html</c>).
/// </para>
/// <para>
/// <b>Направление стенки: документация и измерение согласны.</b>
/// <c>ksshelldefinition_thintype.html</c> задаёт буквально: «<c>TRUE</c> — внутрь, <c>FALSE</c> —
/// наружу». Измерено на коробе 100×80×10 с удалённой верхней гранью при <c>t = 2</c>:
/// <c>thinType = true</c> → <c>21631.999999999996</c> мм³ (полость 96×76×8),
/// <c>thinType = false</c> → <c>24832.000000000022</c> мм³ (тело 104×84×12 минус 100×80×10).
/// Оба числа совпадают с аналитикой, поэтому соответствие установлено дважды — страницей и объёмом.
/// </para>
/// <para>
/// <b>Пустой список удаляемых граней отвергается ДО COM, и это измеренный отказ, а не запрет по
/// вкусу.</b> Ожидание «пустой список даёт замкнутую оболочку <c>36224</c>» не подтвердилось
/// <b>ни на одном из двух API</b>: API5 (шаг B5.6) дал <c>80000</c>, API7 (шаг B5.10, четыре
/// постановки) — <c>79999.99999999999</c> при <b>6 гранях</b>, ровно как у исходного короба, тогда
/// как открытая оболочка даёт <c>21632</c> при <b>11</b> гранях. То есть <c>Create()/Update()</c>
/// возвращают <c>true</c> («принято»), а тело не меняется («не применено»). Выдавать такой исход за
/// построенную оболочку запрещено, поэтому вызов с пустым списком граней именованно отвергается.
/// </para>
/// <para>
/// <b>Что считается доказательством.</b> <c>Create() = true</c> и <c>Update() = true</c> — это
/// «принято». Геометрия подтверждается объёмом против аналитического ожидания вызывающего и числом
/// граней; без ожидания уровень честно остаётся <c>call_returned</c>.
/// </para>
/// </remarks>
public partial class Api5Session
{
    /// <summary>Оболочка — <c>o3d_shellOperation</c>.</summary>
    private const short ShellOperation = 43;

    /// <summary>Оболочка: постоянная толщина стенки по удаляемым граням (SM-13).</summary>
    public ShellResult Shell(ShellCommand command)
    {
        ValidateShellCommand(command);

        var document = RequireDocument(command.DocumentId);

        // Грани обязаны принадлежать ТОЙ ЖЕ детали: ссылка из чужого документа дала бы либо отказ
        // ядра, либо — хуже — молчаливо чужую геометрию, а этого здесь не проверял никто.
        var faces = new List<ksFaceDefinition>(command.FaceRefs.Count);
        foreach (var faceRef in command.FaceRefs)
        {
            if (!References.TryGet(faceRef, out var stored) || stored is null)
            {
                throw new KompasContractException(
                    ErrorCodes.StaleReference,
                    $"Ссылка на грань '{faceRef}' не найдена в реестре ссылок.",
                    RetryPolicy.ReacquireContext);
            }

            if (!string.Equals(stored.DocumentId, document.Id, StringComparison.Ordinal))
            {
                throw new KompasContractException(
                    ErrorCodes.InvalidArgument,
                    $"Грань '{faceRef}' принадлежит документу {stored.DocumentId}, а оболочка " +
                    $"строится в {document.Id}. Грани из чужой детали не переносятся.",
                    RetryPolicy.Never,
                    details: new Dictionary<string, object?>
                    {
                        ["face_document_id"] = stored.DocumentId,
                        ["shell_document_id"] = document.Id,
                    });
            }

            if (stored.Revision != document.Revision)
            {
                throw new KompasContractException(
                    ErrorCodes.StaleReference,
                    $"Грань '{faceRef}' выпущена для ревизии {stored.Revision}, у документа уже " +
                    $"{document.Revision}.",
                    RetryPolicy.ReacquireContext,
                    details: new Dictionary<string, object?>
                    {
                        ["reference_revision"] = stored.Revision,
                        ["current_revision"] = document.Revision,
                    });
            }

            if (stored.Payload is not ksFaceDefinition face)
            {
                throw new KompasContractException(
                    ErrorCodes.InvalidArgument,
                    $"Ссылка '{faceRef}' указывает не на грань (kind={stored.Kind}).",
                    details: new Dictionary<string, object?> { ["kind"] = stored.Kind });
            }

            faces.Add(face);
        }

        var part = document.PartNow();
        var volumeBefore = ReadVolume(document);
        var bodiesBefore = CountBodies(document);
        var facesBefore = CountFaces(document);

        if (part.NewEntity(ShellOperation) is not ksEntity entity
            || entity.GetDefinition() is not ksShellDefinition definition)
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "Определение оболочки не получено: NewEntity(43) вернул объект, у которого нет " +
                "ksShellDefinition. Признак не создавался.",
                RetryPolicy.ReacquireContext);
        }

        definition.thickness = command.ThicknessMm;
        definition.thinType = command.ThinDirection == ShellThinDirection.Inward;

        var attached = AttachFaces(definition, faces);
        if (attached != faces.Count)
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "Удаляемые грани присоединены не полностью: " + attached + " из " + faces.Count +
                ". FaceArray() не привёлся к ksEntityCollection либо Add() вернул false. " +
                "Оболочка не создавалась.",
                RetryPolicy.ReacquireContext,
                details: new Dictionary<string, object?>
                {
                    ["attached_faces"] = attached,
                    ["requested_faces"] = faces.Count,
                });
        }

        var created = SafeBool(entity.Create);
        if (created != true)
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "Create() оболочки вернул " + (created is null ? "ошибку вызова" : "false") +
                ": тело не построено. Толщина, несовместимая с габаритом, даёт именно этот исход, " +
                "и он отказ, а не частичный результат.",
                RetryPolicy.ReacquireContext,
                partialEffects: true,
                details: new Dictionary<string, object?> { ["named_code"] = "GEOMETRY_FAILED" });
        }

        SafeBool(entity.Update);
        part.RebuildModel();
        document.Document.RebuildDocument();

        var reference = References.Register("feature", document.Id, document.Revision, entity);
        BumpRevision(document, "shell.create");

        var volumeAfter = ReadVolume(document);
        var bodiesAfter = CountBodies(document);
        var facesAfter = CountFaces(document);

        var checks = new List<NamedCheck>
        {
            new("operation_created", created == true, "Create() вернул true"),
            new("faces_attached", attached == faces.Count,
                "присоединено граней: " + attached + " из " + faces.Count),
            new("body_count_unchanged", bodiesAfter == bodiesBefore,
                "тел " + bodiesBefore + " → " + bodiesAfter),
        };

        // Число граней — ВТОРОЙ независимый признак рядом с объёмом, и он важен именно потому, что
        // измеренный отказ «пустой список граней» выглядел как успех по одному объёму: тело не
        // менялось, но Create/Update отвечали true. Здесь признак применён, и граней стало больше.
        checks.Add(new NamedCheck("face_count_grew", facesAfter > facesBefore,
            Observed: facesBefore + " → " + facesAfter,
            Expected: "у оболочки появились внутренние грани полости"));

        // Оболочка УМЕНЬШАЕТ объём: материал снимается с внутренней стороны (внутрь) или тело
        // остаётся снаружи (наружу). Направление «наружу» при удалённой грани объём увеличивает,
        // поэтому проверка формулируется как «объём изменился», а не «уменьшился»: иначе честный
        // результат направления «наружу» читался бы как отказ.
        var volumeChanged = volumeBefore is double before && volumeAfter is double after
                            && Math.Abs(after - before) > 1e-6;
        checks.Add(new NamedCheck("volume_changed", volumeChanged,
            Observed: Num(volumeBefore) + " → " + Num(volumeAfter) + " мм³",
            Expected: "объём изменился относительно исходного тела"));

        var unverified = new List<string>();
        var geometryConfirmed = false;
        if (command.ExpectedVolumeMm3 is { } expected)
        {
            var observed = volumeAfter;
            var matches = observed is { } value
                          && Math.Abs(value - expected) <= VolumeToleranceMm3(expected);
            checks.Add(new NamedCheck("expected_volume", matches,
                "объём " + Num(observed) + " мм³", "ожидание " + Num(expected) + " мм³"));
            geometryConfirmed = matches;
        }
        else
        {
            unverified.Add("expected_volume_not_supplied — без аналитического ожидания объёма " +
                           "геометрия оболочки не подтверждена числом");
        }

        unverified.Add("tangent_faces_not_supported — IShell.SetFaces(Faces, TangentFaces) в API7 " +
                       "принимает признак касательных граней, но у API5 ksShellDefinition такого " +
                       "члена нет; выбор касательных граней группой (SM-13.shell.mode_tangent_faces) " +
                       "здесь не выражается и не выдаётся за выполненный");
        unverified.Add("variable_thickness_out_of_scope — переменная толщина по граням вне " +
                       "обязательного объёма этапа (OQ-A13)");
        unverified.Add("dependent_features_not_enumerated — сохранность зависимых признаков здесь не " +
                       "проверяется; для этого существует отдельная приёмочная строка");

        return new ShellResult(
            ToDto(reference, entity.name),
            command.ThicknessMm,
            command.ThinDirection.ToString(),
            faces.Count,
            bodiesAfter,
            volumeAfter,
            part.GetMainBody() is ksBody mainBody ? ReadBodyBox(mainBody) : null,
            new VerificationDto(
                geometryConfirmed ? VerificationLevel.GeometryChecked : VerificationLevel.CallReturned,
                checks,
                unverified),
            new List<string>());
    }

    /// <summary>
    /// Присоединить удаляемые грани к определению. Возвращает число присоединённых: меньше
    /// запрошенного означает, что маршрут не построился, и это отказ, а не «часть граней».
    /// </summary>
    private static int AttachFaces(ksShellDefinition definition, IReadOnlyList<ksFaceDefinition> faces)
    {
        object? holder;
        try
        {
            holder = definition.FaceArray();
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return 0;
        }

        if (holder is not ksEntityCollection collection)
        {
            return 0;
        }

        var attached = 0;
        foreach (var face in faces)
        {
            if (SafeBool(() => collection.Add(face)) == true)
            {
                attached++;
            }
        }

        return attached;
    }

    /// <summary>
    /// Правила «поле ↔ возможность», отвергающие вызов ДО обращения к COM. Цена ошибки здесь
    /// несимметрична: лишний отказ виден сразу, а принятое и проигнорированное число доживает до
    /// приёмки, выглядя как выполненная операция.
    /// </summary>
    private static void ValidateShellCommand(ShellCommand command)
    {
        if (string.IsNullOrWhiteSpace(command.DocumentId))
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "Документ не задан: оболочка строится в конкретной детали.",
                RetryPolicy.Never);
        }

        if (!double.IsFinite(command.ThicknessMm) || command.ThicknessMm <= 0d)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "Толщина оболочки должна быть положительным конечным числом, а получено " +
                Num(command.ThicknessMm) + " мм.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?> { ["thickness_mm"] = command.ThicknessMm });
        }

        if (command.FaceRefs.Count == 0)
        {
            // Отказ ОСНОВАН НА ИЗМЕРЕНИИ, а не на осторожности: пустой список граней не даёт
            // замкнутой оболочки ни на API5 (B5.6: 80000), ни на API7 (B5.10: 80000 при 6 гранях),
            // хотя Create()/Update() отвечают true. Принять такой вызов значило бы вернуть
            // вызывающему «оболочка построена» там, где тело не изменилось.
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "Список удаляемых граней пуст. Измерено 20.09.2026 на обоих API: при пустом списке " +
                "операция принимается (Create/Update = true), но тело не меняется — объём остаётся " +
                "80000, число граней 6, как у исходного тела, тогда как открытая оболочка даёт " +
                "21632 при 11 гранях. Замкнутая оболочка пустым списком граней не выражается; " +
                "укажите хотя бы одну снимаемую грань.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?>
                {
                    ["measured_closed_volume_mm3"] = 80000d,
                    ["measured_closed_face_count"] = 6,
                    ["measured_open_volume_mm3"] = 21632d,
                    ["measured_open_face_count"] = 11,
                });
        }

        if (command.FaceRefs.Count != command.FaceRefs.Distinct(StringComparer.Ordinal).Count())
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "Список удаляемых граней содержит повторы: одна и та же грань указана дважды, " +
                "и «сколько граней снято» стало бы неотличимо от «сколько раз её назвали».",
                RetryPolicy.Never);
        }

        if (command.TangentFaces)
        {
            // Параметр ОБЪЯВЛЕН в схеме (иначе он был бы невидим для продукта), но ЗДЕСЬ не
            // выполняется: члена «касательные грани» у API5 ksShellDefinition нет вовсе, а этот
            // инструмент идёт маршрутом API5. Принять true и промолчать значило бы вернуть успех
            // за работу, которой не было, — ровно тот дефект, который контракт запрещает.
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                "tangent_faces = true не выполняется: у API5 ksShellDefinition члена «касательные " +
                "грани» нет вовсе (маршрут этого инструмента — API5 o3d_shellOperation=43), а " +
                "касательные грани объявлены только у API7 IShell.SetFaces(Faces, TangentFaces). " +
                "Режим SM-13.shell.mode_tangent_faces в обязательный объём этого этапа не входит. " +
                "Передайте false или не передавайте поле.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?>
                {
                    ["tangent_faces"] = true,
                    ["api5_member_present"] = false,
                    ["api7_member"] = "IShell.SetFaces(Faces, TangentFaces)",
                });
        }
    }
}
