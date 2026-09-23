using System.Runtime.InteropServices;
using Kompas6API5;
using Kompas6Constants3D;
using KompasMcp.Api5Adapter.Api7;
using KompasMcp.Contracts;
using KompasMcp.Contracts.Ipc;
using KompasAPI7;

namespace KompasMcp.Api5Adapter;

/// <summary>
/// Вспомогательная геометрия детали как ОБЪЕКТЫ МОДЕЛИ: плоскости, оси, точки
/// (<c>kompas_create_aux_geometry</c>, <c>kompas_list_aux_geometry</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Зачем это отдельный инструмент, а не поле чужой операции.</b> Зависимости
/// <c>dep.refs.planes</c>, <c>dep.refs.axes</c> и <c>dep.refs.points_axes</c> требуют, чтобы
/// плоскость, ось и точка существовали в модели КАК ОБЪЕКТЫ, которые можно перечислить, прочитать
/// и пережить цикл сохранения. Прежние маршруты выражали их числами в аргументах чужого вызова
/// (ось вращения — двумя точками, опора эскиза — именем базовой плоскости), и это ИЗМЕРЕНО
/// записанными отказами: строка <c>DEP.DAX.01.discover</c> прежнего прогона прямо называет
/// положительный контроль — «ось задаётся двумя ЧИСЛОВЫМИ точками в аргументах, объекта нет».
/// Число в аргументе перечислению недоступно, поэтому требование перечислением и закрывается.
/// </para>
/// <para>
/// <b>Всё построение — документированный API7</b> (шаг 0 наряда продуктовых маршрутов, отчёт
/// <c>DEPENDENCIES_PRODUCT_ROUTES_STEP0_REPORT_20260921.md</c> §6.1–6.3); имена членов взяты
/// прибором <c>tools/KompasMcp.InteropScan</c> из поставленного interop'а целевой сборки, а не из
/// памяти. Расхождения справки с interop'ом перечислены в <see cref="Api7SketchEntities"/> и
/// <see cref="Api7AuxEnumeration"/>.
/// </para>
/// </remarks>
public sealed partial class Api5Session
{
    /// <summary>Поля, законные для каждого способа. Набор объявлен здесь, а не выведен из «не null»:
    /// поле, объявленное в контракте и не применённое, доживает до приёмки, выглядя как работа.</summary>
    private static readonly Dictionary<string, string[]> AuxAllowedFields = new(StringComparer.Ordinal)
    {
        ["plane/offset"] = ["document_id", "expected_revision", "kind", "mode", "offset_mm",
            "direction", "base_plane", "base_face_ref", "name"],
        ["plane/angle"] = ["document_id", "expected_revision", "kind", "mode", "angle_deg",
            "direction", "base_plane", "base_axis_ref", "name"],
        ["axis/by_2_points"] = ["document_id", "expected_revision", "kind", "mode",
            "point1_mm", "point2_mm", "name"],
        ["axis/by_face"] = ["document_id", "expected_revision", "kind", "mode", "face_ref", "name"],
        ["axis/by_edge"] = ["document_id", "expected_revision", "kind", "mode", "edge_ref", "name"],
        ["point/coordinates"] = ["document_id", "expected_revision", "kind", "mode",
            "coordinates_mm", "name"],
        ["point/displace"] = ["document_id", "expected_revision", "kind", "mode",
            "association_vertex_ref", "displacement_mm", "name"],
    };

    private static readonly Dictionary<string, ksObj3dTypeEnum> BasePlaneTypes = new(StringComparer.Ordinal)
    {
        ["xy"] = ksObj3dTypeEnum.o3d_planeXOY,
        ["xz"] = ksObj3dTypeEnum.o3d_planeXOZ,
        ["yz"] = ksObj3dTypeEnum.o3d_planeYOZ,
    };

    /// <summary>
    /// Типы ДЛЯ <c>ksPart.GetDefaultEntity</c> — тот же набор имён, но адресованный по типу
    /// стандартного объекта, а не по позиции в коллекции.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>ПОЧЕМУ ИМЕНОВАННАЯ ОПОРА БЕРЁТСЯ ЭТИМ МАРШРУТОМ, А НЕ ПЕРЕЧИСЛЕНИЕМ API7.</b> Измерено
    /// 21.09.2026 пробой <c>scratch/_probe_dpl_offset.py</c> на бинарях поставки
    /// <c>artifacts/publish-deproutes-20260921-b</c>: в детали, у которой уже есть тело
    /// (ревизия 4, плита 40×40×10 построена), <c>IAuxiliaryGeomContainer.GetPlanes3D</c> отдаёт
    /// <b>Count = 0</b> — стандартных плоскостей в коллекции <c>IPlanes3D</c> НЕТ вовсе, тогда как
    /// созданные инструментом плоскости в ней появляются (<c>plane_count</c>=1 после опоры на
    /// грань). Поэтому «найти xy перечислением» — не маршрут, а ожидание: перечислять нечего.
    /// </para>
    /// <para>
    /// Документированный маршрут к стандартной плоскости как ОБЪЕКТУ — <c>ksPart.GetDefaultEntity</c>
    /// по типу <c>o3d_planeXOY/XOZ/YOZ</c>; им уже пользуется создание эскиза
    /// (<c>Api5Session.Geometry.cs:ResolvePlaneEntity</c>), и этот маршрут измерен приёмкой на
    /// строках <c>DEP.DPL.01.discover</c> и всех режимах эскиза. Полученный объект переносится в
    /// API7 штатным <c>Api7Bridge.TransferTo7</c> и подставляется в <c>IPlane3DBy*.BasePlane</c>.
    /// </para>
    /// </remarks>
    private static readonly Dictionary<string, int> DefaultPlaneEntities = new(StringComparer.Ordinal)
    {
        ["xy"] = KompasObjectTypes.PlaneXoy,
        ["xz"] = KompasObjectTypes.PlaneXoz,
        ["yz"] = KompasObjectTypes.PlaneYoz,
    };

    /// <summary>
    /// Создать плоскость, ось либо точку как объект детали. Возвращает ПРОЧИТАННОЕ ИЗ МОДЕЛИ, а не
    /// пересказ запроса: числа берутся тем же маршрутом чтения, которым их увидит клиент.
    /// </summary>
    public AuxGeometryResult CreateAuxGeometry(CreateAuxGeometryCommand command)
    {
        var document = RequireDocument(command.DocumentId);
        var key = $"{command.Kind}/{command.Mode}";
        if (!AuxAllowedFields.ContainsKey(key))
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                $"Сочетание kind='{command.Kind}' и mode='{command.Mode}' не поддержано. " +
                $"Объявленные способы: {string.Join(", ", AuxAllowedFields.Keys)}.",
                details: new Dictionary<string, object?>
                {
                    ["supported"] = AuxAllowedFields.Keys.ToArray(),
                });
        }

        RejectForeignFields(command, AuxAllowedFields[key]);

        var bridge = BridgeFor(document);
        var (model, auxiliary, failure) = Api7AuxGeometry.Containers(bridge, document.PartNow());
        if (model is null || auxiliary is null)
        {
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                "Контейнеры API7 недостижимы: " + (failure ?? "причина не названа") +
                ". Вспомогательная геометрия создаётся только через них, поэтому догадка не выдаётся.",
                RetryPolicy.ReacquireContext,
                details: new Dictionary<string, object?> { ["containers"] = failure });
        }

        var diagnostics = new List<string> { $"Маршрут: {Api7AuxEnumeration.Route}." };
        string? referenceId = null;
        string? kind = null;
        string? mode = command.Mode;
        string? subKind = null;
        string? name = null;
        string? baseName = null;
        string? lineName = null;
        double[]? point = null;
        double[]? direction = null;
        double? angle = null;
        double? offset = null;
        bool? sign = null;

        switch (key)
        {
            case "plane/offset":
            {
                var support = ResolvePlaneSupport(document, bridge, command, diagnostics);
                var (plane, planeFailure) = Api7AuxGeometry.CreatePlaneByOffset(
                    RequirePlanes(auxiliary, diagnostics),
                    support,
                    command.OffsetMm ?? throw Missing("offset_mm", key),
                    command.Direction ?? true,
                    command.Name);
                if (plane is null)
                {
                    throw Geometry("смещённая плоскость не создана", planeFailure);
                }

                var read = Api7AuxGeometry.ReadPlane(plane, -1);
                referenceId = References.Register("plane", document.Id, document.Revision, plane).Id;
                kind = read.Kind;
                subKind = read.SubKind;
                name = read.Name;
                point = read.PointMm;
                direction = read.DirectionMm;
                offset = read.OffsetMm;
                sign = read.Direction;
                baseName = read.BaseName;
                diagnostics.AddRange(read.Notes);
                break;
            }

            case "plane/angle":
            {
                var support = ResolvePlaneSupport(document, bridge, command, diagnostics);
                var baseLine = ResolveAxisObject(document, bridge, command.BaseAxisRef, diagnostics);
                var (plane, planeFailure) = Api7AuxGeometry.CreatePlaneByAngle(
                    RequirePlanes(auxiliary, diagnostics),
                    support,
                    baseLine,
                    command.AngleDeg ?? throw Missing("angle_deg", key),
                    command.Direction ?? true,
                    command.Name);
                if (plane is null)
                {
                    throw Geometry("наклонная плоскость не создана", planeFailure);
                }

                var read = Api7AuxGeometry.ReadPlane(plane, -1);
                referenceId = References.Register("plane", document.Id, document.Revision, plane).Id;
                kind = read.Kind;
                subKind = read.SubKind;
                name = read.Name;
                point = read.PointMm;
                direction = read.DirectionMm;
                angle = read.AngleDeg;
                sign = read.Direction;
                baseName = read.BaseName;
                lineName = read.LineName;
                diagnostics.AddRange(read.Notes);
                break;
            }

            case "axis/by_2_points":
            {
                var (axis, axisFailure) = Api7AuxGeometry.CreateAxisBy2Points(
                    RequireAxes(auxiliary, diagnostics),
                    model,
                    command.Point1Mm ?? throw Missing("point1_mm", key),
                    command.Point2Mm ?? throw Missing("point2_mm", key),
                    command.Name);
                if (axis is null)
                {
                    throw Geometry("ось по двум точкам не создана", axisFailure);
                }

                var read = Api7AuxGeometry.ReadAxis(axis, -1);
                referenceId = References.Register("axis", document.Id, document.Revision, axis).Id;
                kind = read.Kind;
                subKind = read.SubKind;
                name = read.Name;
                point = read.PointMm;
                direction = read.DirectionMm;
                baseName = read.BaseName;
                diagnostics.AddRange(read.Notes);
                break;
            }

            case "axis/by_face":
            {
                var face = RequireModelObject(document, bridge, command.FaceRef, "face", "face_ref");
                var (axis, axisFailure) = Api7AuxGeometry.CreateAxisByFace(
                    RequireAxes(auxiliary, diagnostics), face, command.Name);
                if (axis is null)
                {
                    throw Geometry("ось по поверхности не создана", axisFailure);
                }

                var read = Api7AuxGeometry.ReadAxis(axis, -1);
                referenceId = References.Register("axis", document.Id, document.Revision, axis).Id;
                kind = read.Kind;
                subKind = read.SubKind;
                name = read.Name;
                baseName = read.BaseName;
                diagnostics.AddRange(read.Notes);
                break;
            }

            case "axis/by_edge":
            {
                var edge = RequireModelObject(document, bridge, command.EdgeRef, "edge", "edge_ref");
                var (axis, axisFailure) = Api7AuxGeometry.CreateAxisByEdge(
                    RequireAxes(auxiliary, diagnostics), edge, command.Name);
                if (axis is null)
                {
                    throw Geometry("ось по ребру не создана", axisFailure);
                }

                var read = Api7AuxGeometry.ReadAxis(axis, -1);
                referenceId = References.Register("axis", document.Id, document.Revision, axis).Id;
                kind = read.Kind;
                subKind = read.SubKind;
                name = read.Name;
                baseName = read.BaseName;
                diagnostics.AddRange(read.Notes);
                break;
            }

            case "point/coordinates":
            {
                var coordinates = command.CoordinatesMm ?? throw Missing("coordinates_mm", key);
                if (coordinates.Length != 3)
                {
                    throw new KompasContractException(
                        ErrorCodes.InvalidArgument,
                        $"coordinates_mm обязан нести ровно три числа, получено {coordinates.Length}.",
                        details: new Dictionary<string, object?> { ["length"] = coordinates.Length });
                }

                var (created, pointFailure) = Api7AuxGeometry.CreatePointByCoordinates(
                    model, coordinates[0], coordinates[1], coordinates[2], command.Name);
                if (created is null)
                {
                    throw Geometry("точка по координатам не создана", pointFailure);
                }

                var (readCoordinates, parameterType, association, notes) =
                    Api7AuxGeometry.ReadPoint(created);
                referenceId = References.Register("point", document.Id, document.Revision, created).Id;
                kind = "point";
                subKind = parameterType;
                name = created.Name;
                point = readCoordinates;
                baseName = association;
                diagnostics.AddRange(notes);
                break;
            }

            case "point/displace":
            {
                var vertex = RequireModelObject(
                    document, bridge, command.AssociationVertexRef, "face", "association_vertex_ref");
                var displacement = command.DisplacementMm ?? throw Missing("displacement_mm", key);
                if (displacement.Length != 3)
                {
                    throw new KompasContractException(
                        ErrorCodes.InvalidArgument,
                        $"displacement_mm обязан нести ровно три числа, получено {displacement.Length}.",
                        details: new Dictionary<string, object?> { ["length"] = displacement.Length });
                }

                var (created, pointFailure) = Api7AuxGeometry.CreatePointByDisplace(
                    model, vertex, displacement[0], displacement[1], displacement[2]);
                if (created is null)
                {
                    throw Geometry("точка смещением не создана", pointFailure);
                }

                var (readCoordinates, parameterType, association, notes) =
                    Api7AuxGeometry.ReadPoint(created);
                referenceId = References.Register("point", document.Id, document.Revision, created).Id;
                kind = "point";
                subKind = parameterType;
                name = created.Name;
                point = readCoordinates;
                baseName = association;
                diagnostics.AddRange(notes);
                break;
            }

            default:
                throw new KompasContractException(
                    ErrorCodes.InvalidArgument,
                    $"Способ '{key}' объявлен в наборе полей, но обработчика не имеет: " +
                    "это дефект сервера, а не отказ модели.",
                    details: new Dictionary<string, object?> { ["key"] = key });
        }

        // Перестроение обязательно: без него объект остаётся в контейнере, но модель его не знает
        // — измерено на соседних маршрутах (без Update() не меняется ни один режим отверстия).
        Api7Bridge.Rebuild(model, document.Document);
        BumpRevision(document, "aux." + key.Replace('/', '.'));

        var counts = AuxCounts(model, auxiliary);
        diagnostics.Add($"После создания: плоскостей {counts.Planes}, осей {counts.Axes}, точек {counts.Points}.");

        return new AuxGeometryResult(
            Kind: kind ?? command.Kind,
            Mode: mode ?? command.Mode,
            ReferenceId: referenceId,
            Name: name,
            SubKind: subKind,
            PointMm: point,
            DirectionMm: direction,
            AngleDeg: angle,
            OffsetMm: offset,
            Direction: sign,
            BaseName: baseName,
            LineName: lineName,
            PlaneCount: counts.Planes,
            AxisCount: counts.Axes,
            PointCount: counts.Points,
            Diagnostics: diagnostics);
    }

    /// <summary>
    /// Правка существующей плоскости как объекта детали: смещение, угол, знак, опора.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Поле обязано соответствовать ВИДУ плоскости, и это проверяется по прочитанному виду, а не
    /// по запросу.</b> У смещённой плоскости нет угла, у наклонной нет смещения; попытка записать
    /// чужое поле отвергается <c>INVALID_ARGUMENT</c> с перечнем допустимых. Молчаливое игнорирование
    /// дало бы ровно тот дефект, который этот наряд устраняет: принятое и не применённое поле.
    /// </para>
    /// <para>
    /// <b>Признак применения выводится из ПОВТОРНОГО ЧТЕНИЯ</b> тем же маршрутом, которым значение
    /// увидит клиент. Успешный код <c>Update()</c> применением не объявляется.
    /// </para>
    /// </remarks>
    public PlaneUpdateResult UpdatePlane(UpdatePlaneCommand command)
    {
        var document = RequireDocument(command.DocumentId);
        var bridge = BridgeFor(document);

        var stored = References.Require(command.PlaneRef, document.Id, document.Revision);
        if (stored.Payload is not object payload)
        {
            throw new KompasContractException(
                ErrorCodes.StaleReference,
                $"Ссылка '{command.PlaneRef}' не несёт объекта.",
                RetryPolicy.ReacquireContext);
        }

        if (bridge.TransferTo7(payload) is not IPlane3D plane)
        {
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                $"Объект ссылки '{command.PlaneRef}' (kind={stored.Kind}) не переносится в API7 как " +
                "IPlane3D: правка плоскости другим объектом не подменяется.",
                RetryPolicy.ReacquireContext,
                details: new Dictionary<string, object?> { ["kind"] = stored.Kind });
        }

        var diagnostics = new List<string> { $"Маршрут: {Api7AuxEnumeration.Route}." };
        var before = Api7AuxGeometry.ReadPlane(plane, -1);
        diagnostics.AddRange(before.Notes);

        // Вид берётся ИЗ МОДЕЛИ: строка запроса видом не является.
        var allowed = before.Kind switch
        {
            "offset" => new[] { "offset_mm", "direction", "base_plane" },
            "by_angle" => new[] { "angle_deg", "direction", "base_plane" },
            _ => Array.Empty<string>(),
        };

        if (allowed.Length == 0)
        {
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                $"Вид плоскости определён по ModelObjectType={before.SubKind}, и параметров построения " +
                "этот маршрут у него не читает. Правка не гадает о виде: она его измеряет.",
                RetryPolicy.ReacquireContext,
                details: new Dictionary<string, object?> { ["sub_kind"] = before.SubKind });
        }

        var sent = new List<string>();
        if (command.OffsetMm is not null) sent.Add("offset_mm");
        if (command.AngleDeg is not null) sent.Add("angle_deg");
        if (command.Direction is not null) sent.Add("direction");
        if (command.BasePlane is { Length: > 0 }) sent.Add("base_plane");

        var foreign = sent.Where(f => !allowed.Contains(f, StringComparer.Ordinal)).ToArray();
        if (foreign.Length > 0)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                $"Поля {string.Join(", ", foreign)} не принадлежат виду плоскости «{before.Kind}» " +
                $"(измерен по ModelObjectType={before.SubKind}). Его поля: {string.Join(", ", allowed)}.",
                details: new Dictionary<string, object?>
                {
                    ["foreign_fields"] = foreign,
                    ["allowed_fields"] = allowed,
                    ["plane_kind"] = before.Kind,
                });
        }

        if (sent.Count == 0)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "Ни одно поле правки не задано: правка не имеет предмета.",
                details: new Dictionary<string, object?> { ["allowed_fields"] = allowed });
        }

        IModelObject? baseObject = null;
        string? baseNameAssigned = null;
        if (command.BasePlane is { Length: > 0 } wantedBase)
        {
            baseObject = ResolveNamedBasePlane(document, bridge, wantedBase, diagnostics);
            baseNameAssigned = SafeName(baseObject);
        }

        var failure = Api7AuxGeometry.UpdatePlane(
            plane, command.OffsetMm, command.AngleDeg, command.Direction, baseObject);
        if (failure is not null)
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "Правка плоскости не применена: " + failure,
                RetryPolicy.SameOperationId,
                partialEffects: true,
                details: new Dictionary<string, object?> { ["failure"] = failure });
        }

        // Перестроение обязательно: без него объект изменён в контейнере, но модель его не знает
        // (измерено на соседних маршрутах: без Update() не меняется ни один режим отверстия).
        var (model, _, containersFailure) = Api7AuxGeometry.Containers(bridge, document.PartNow());
        if (model is null)
        {
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                "Контейнеры API7 недостижимы для перестроения: " + (containersFailure ?? "причина не названа"),
                RetryPolicy.ReacquireContext);
        }

        Api7Bridge.Rebuild(model, document.Document);
        BumpRevision(document, "aux.update_plane");

        var after = Api7AuxGeometry.ReadPlane(plane, -1);
        diagnostics.AddRange(after.Notes);

        var checks = new List<string>();
        var applied = true;
        if (command.OffsetMm is { } wantedOffset)
        {
            var ok = after.OffsetMm is { } got && Math.Abs(got - wantedOffset) <= 1e-9;
            applied &= ok;
            checks.Add($"offset_mm: запрошено {FormatNum(wantedOffset)}, прочитано {FormatNum(after.OffsetMm)}");
        }

        if (command.AngleDeg is { } wantedAngle)
        {
            var ok = after.AngleDeg is { } got && Math.Abs(got - wantedAngle) <= 1e-9;
            applied &= ok;
            checks.Add($"angle_deg: запрошено {FormatNum(wantedAngle)}, прочитано {FormatNum(after.AngleDeg)}");
        }

        if (command.Direction is { } wantedDirection)
        {
            var ok = after.Direction == wantedDirection;
            applied &= ok;
            checks.Add($"direction: запрошено {wantedDirection}, прочитано {after.Direction}");
        }

        if (baseNameAssigned is not null)
        {
            var ok = string.Equals(after.BaseName, baseNameAssigned, StringComparison.Ordinal);
            applied &= ok;
            checks.Add($"base_plane: назначено «{baseNameAssigned}», прочитано «{after.BaseName}»");
        }

        diagnostics.Add("Подтверждение правки — ПОВТОРНОЕ ЧТЕНИЕ, а не код возврата: " +
            string.Join("; ", checks) + ".");

        return new PlaneUpdateResult(
            ReferenceId: command.PlaneRef,
            Kind: after.Kind,
            SubKind: after.SubKind,
            Name: after.Name,
            OffsetMm: after.OffsetMm,
            AngleDeg: after.AngleDeg,
            Direction: after.Direction,
            BaseName: after.BaseName,
            PointMm: after.PointMm,
            DirectionMm: after.DirectionMm,
            Applied: applied,
            AppliedEvidence: string.Join("; ", checks),
            PlaneCount: Api7AuxGeometry.SafeCount(
                Api7AuxGeometry.Containers(bridge, document.PartNow()).Auxiliary?.Planes3D) ?? 0,
            Diagnostics: diagnostics);
    }

    private static string FormatNum(double? value) => value is { } v
        ? v.ToString("0.############", System.Globalization.CultureInfo.InvariantCulture)
        : "не прочитано";

    private static string? SafeName(IModelObject? model)
    {
        try
        {
            return model?.Name;
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException
            or InvalidCastException)
        {
            return null;
        }
    }

    /// <summary>Базовая плоскость ПО ИМЕНИ: единственная именованная опора, которую можно задать
    /// строкой. Вынесено отдельно, потому что этим пользуются и создание, и правка.</summary>
    /// <remarks>
    /// Берётся ДОКУМЕНТИРОВАННЫМ <c>ksPart.GetDefaultEntity</c>, а не перечислением
    /// <c>IPlanes3D</c>: измерено 21.09.2026 (<c>scratch/_probe_dpl_offset.py</c>), что стандартных
    /// плоскостей в этой коллекции нет — <c>Count</c>=0 в детали с готовым телом. Перечисление
    /// вернуло бы «не найдено» на исправном документе, то есть отказ прибора, выданный за отказ
    /// продукта. Другой объект вместо запрошенной опоры не подставляется.
    /// </remarks>
    private IModelObject ResolveNamedBasePlane(
        DocumentEntry document, Api7Bridge bridge, string wanted, List<string> diagnostics)
    {
        if (!DefaultPlaneEntities.TryGetValue(wanted, out var defaultType))
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                $"Базовая плоскость «{wanted}» не объявлена. Объявлены: {string.Join(", ", BasePlaneTypes.Keys)}.",
                details: new Dictionary<string, object?> { ["base_plane"] = wanted });
        }

        ksEntity? entity;
        try
        {
            entity = document.PartNow().GetDefaultEntity(KompasObjectTypes.Of(defaultType)) as ksEntity;
        }
        catch (COMException ex)
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                $"Стандартная плоскость «{wanted}» не получена: ksPart.GetDefaultEntity бросил " +
                $"HRESULT 0x{ex.HResult:X8}.",
                RetryPolicy.ReacquireContext,
                details: new Dictionary<string, object?> { ["base_plane"] = wanted });
        }

        if (entity is null)
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                $"Стандартная плоскость «{wanted}» не получена: ksPart.GetDefaultEntity вернул пусто " +
                "для своего же типа — опора не найдена, другой объект вместо неё не подставляется.",
                RetryPolicy.ReacquireContext,
                details: new Dictionary<string, object?> { ["base_plane"] = wanted });
        }

        var (plane, failure) = Api7SolidPlane.TryTransfer(entity, bridge);
        if (plane is null)
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                $"Стандартная плоскость «{wanted}» получена, но в API7 не переносится: " +
                (failure ?? "причина не названа"),
                RetryPolicy.ReacquireContext,
                details: new Dictionary<string, object?> { ["base_plane"] = wanted });
        }

        diagnostics.Add(
            $"Опора: стандартная плоскость «{wanted}» взята у ksPart.GetDefaultEntity по типу " +
            $"{BasePlaneTypes[wanted]} и перенесена в API7. Перечисление IPlanes3D стандартных " +
            "плоскостей не содержит (измерено: Count=0 в детали с телом) — поэтому опора берётся " +
            "по типу, а не поиском по коллекции.");
        return plane;
    }

    /// <summary>Перечислить и прочитать объекты вспомогательной геометрии.</summary>
    public AuxGeometryListResult ListAuxGeometry(ListAuxGeometryCommand command)
    {
        var document = RequireDocument(command.DocumentId);
        var bridge = BridgeFor(document);
        var (model, auxiliary, failure) = Api7AuxGeometry.Containers(bridge, document.PartNow());
        if (model is null || auxiliary is null)
        {
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                "Контейнеры API7 недостижимы: " + (failure ?? "причина не названа"),
                RetryPolicy.ReacquireContext,
                details: new Dictionary<string, object?> { ["containers"] = failure });
        }

        var limit = command.Limit ?? 200;
        if (limit <= 0)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                $"limit обязан быть положительным, получено {limit}.",
                details: new Dictionary<string, object?> { ["limit"] = limit });
        }

        var notes = new List<string>();
        var rows = new List<AuxGeometryRowDto>();
        var counts = new Dictionary<string, int?>(StringComparer.Ordinal);
        var include = command.Include;

        if (include is "planes" or "all")
        {
            var planes = RequirePlanes(auxiliary, notes);
            counts["planes"] = Api7AuxGeometry.SafeCount(planes);
            var (planeRows, planeFailure) = Api7AuxEnumeration.Planes(planes, limit);
            if (planeFailure is not null)
            {
                notes.Add(planeFailure);
            }

            rows.AddRange(planeRows.Select(ToDto));
        }

        if (include is "axes" or "all")
        {
            var axes = RequireAxes(auxiliary, notes);
            counts["axes"] = Api7AuxGeometry.SafeCount(axes);
            var (axisRows, axisFailure) = Api7AuxEnumeration.Axes(axes, limit);
            if (axisFailure is not null)
            {
                notes.Add(axisFailure);
            }

            rows.AddRange(axisRows.Select(ToDto));
        }

        if (include is "points" or "all")
        {
            var points = model.Points3D;
            if (points is null)
            {
                notes.Add("IModelContainer.Points3D → null: коллекция точек недостижима");
            }
            else
            {
                counts["points"] = Api7AuxGeometry.SafeCount(points);
                var (pointRows, pointFailure) = Api7AuxEnumeration.Points(points, limit);
                if (pointFailure is not null)
                {
                    notes.Add(pointFailure);
                }

                rows.AddRange(pointRows.Select(ToDto));
            }
        }

        // Адресация ИМЕНЕМ: справка документирует GetAxis3DByName/GetPoint3DByName, но в поставке
        // члена с подстрокой ByName НЕТ НИ ОДНОГО, поэтому имя разрешается перечислением. Отказ
        // «имени нет» и «имя не уникально» различаются, а не сливаются в пустой ответ.
        if (command.Name is { Length: > 0 } wanted)
        {
            var named = rows.Where(r => string.Equals(r.Name, wanted, StringComparison.Ordinal)).ToList();
            if (named.Count == 0)
            {
                throw new KompasContractException(
                    ErrorCodes.DocumentNotFound,
                    $"Объекта вспомогательной геометрии с именем «{wanted}» в документе нет.",
                    details: new Dictionary<string, object?> { ["name"] = wanted });
            }

            if (named.Count > 1)
            {
                throw new KompasContractException(
                    ErrorCodes.AmbiguousSelection,
                    $"Имя «{wanted}» не уникально: найдено объектов {named.Count}. Молчаливый выбор " +
                    "первого сделал бы адрес неоднозначным.",
                    details: new Dictionary<string, object?>
                    {
                        ["name"] = wanted,
                        ["matches"] = named.Count,
                    });
            }

            rows = named;
        }

        return new AuxGeometryListResult(rows, counts, Api7AuxEnumeration.Route, notes);
    }

    // ---------------------------------------------------------------------------------------------

    private static AuxGeometryRowDto ToDto(AuxGeomRow row) => new(
        row.Kind, row.Index, row.Name, row.SubKind, row.PointMm, row.DirectionMm,
        row.AngleDeg, row.OffsetMm, row.Direction, row.BaseName, row.LineName, row.Notes);

    private (int Planes, int Axes, int Points) AuxCounts(
        IModelContainer model, IAuxiliaryGeomContainer auxiliary)
        => (Api7AuxGeometry.SafeCount(auxiliary.Planes3D) ?? 0,
            Api7AuxGeometry.SafeCount(auxiliary.Axes3D) ?? 0,
            Api7AuxGeometry.SafeCount(model.Points3D) ?? 0);

    private IPlanes3D RequirePlanes(IAuxiliaryGeomContainer auxiliary, List<string> notes)
    {
        var planes = auxiliary.Planes3D;
        if (planes is null)
        {
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                "IAuxiliaryGeomContainer.Planes3D → null: коллекция плоскостей недостижима.",
                RetryPolicy.ReacquireContext);
        }

        return planes;
    }

    private IAxes3D RequireAxes(IAuxiliaryGeomContainer auxiliary, List<string> notes)
    {
        var axes = auxiliary.Axes3D;
        if (axes is null)
        {
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                "IAuxiliaryGeomContainer.Axes3D → null: коллекция осей недостижима.",
                RetryPolicy.ReacquireContext);
        }

        return axes;
    }

    /// <summary>
    /// Опора плоскости: базовая плоскость ПО ИМЕНИ либо ПЛОСКАЯ ГРАНЬ по ссылке. Ровно одна из
    /// двух: обе сразу — отказ, потому что «и то и то» не имеет определённого смысла.
    /// </summary>
    private IModelObject ResolvePlaneSupport(
        DocumentEntry document, Api7Bridge bridge, CreateAuxGeometryCommand command, List<string> diagnostics)
    {
        var hasPlane = command.BasePlane is { Length: > 0 };
        var hasFace = command.BaseFaceRef is { Length: > 0 };
        if (hasPlane && hasFace)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "Заданы и base_plane, и base_face_ref: опора плоскости задаётся ровно одним способом.",
                details: new Dictionary<string, object?>
                {
                    ["base_plane"] = command.BasePlane,
                    ["base_face_ref"] = command.BaseFaceRef,
                });
        }

        if (hasFace)
        {
            return RequireModelObject(document, bridge, command.BaseFaceRef, "face", "base_face_ref");
        }

        return ResolveNamedBasePlane(document, bridge, command.BasePlane ?? "xy", diagnostics);
    }

    /// <summary>
    /// Ссылка на объект модели (грань, ребро, вершина) → объект API7. Вид ссылки проверяется: ссылка
    /// на эскиз вместо грани — это <c>INVALID_ARGUMENT</c>, а не «грань не подошла».
    /// </summary>
    private IModelObject RequireModelObject(
        DocumentEntry document, Api7Bridge bridge, string? reference, string expectedKind, string field)
    {
        if (string.IsNullOrEmpty(reference))
        {
            throw Missing(field, "ссылка");
        }

        var stored = References.Require(reference, document.Id, document.Revision);
        if (stored.Payload is not object payload)
        {
            throw new KompasContractException(
                ErrorCodes.StaleReference,
                $"Ссылка '{reference}' не несёт объекта.",
                RetryPolicy.ReacquireContext);
        }

        if (bridge.TransferTo7(payload) is not IModelObject model)
        {
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                $"Объект ссылки '{reference}' (kind={stored.Kind}) не переносится в API7 как IModelObject.",
                RetryPolicy.ReacquireContext,
                details: new Dictionary<string, object?>
                {
                    ["kind"] = stored.Kind,
                    ["expected_kind"] = expectedKind,
                });
        }

        return model;
    }

    /// <summary>Ссылка на ОСЬ → объект API7. Базовая прямая наклонной плоскости — это ось.</summary>
    private IModelObject? ResolveAxisObject(
        DocumentEntry document, Api7Bridge bridge, string? reference, List<string> diagnostics)
    {
        if (string.IsNullOrEmpty(reference))
        {
            diagnostics.Add("Базовая прямая не задана: угол отсчитывается от базовой плоскости.");
            return null;
        }

        return RequireModelObject(document, bridge, reference, "axis", "base_axis_ref");
    }

    /// <summary>Отказ на поле чужого способа. Перечисляет и присланное, и допустимое.</summary>
    private static void RejectForeignFields(CreateAuxGeometryCommand command, string[] allowed)
    {
        var sent = new List<string>();
        if (command.OffsetMm is not null) sent.Add("offset_mm");
        if (command.AngleDeg is not null) sent.Add("angle_deg");
        if (command.Direction is not null) sent.Add("direction");
        if (command.BasePlane is not null) sent.Add("base_plane");
        if (command.BaseAxisRef is not null) sent.Add("base_axis_ref");
        if (command.BaseFaceRef is not null) sent.Add("base_face_ref");
        if (command.Point1Mm is not null) sent.Add("point1_mm");
        if (command.Point2Mm is not null) sent.Add("point2_mm");
        if (command.FaceRef is not null) sent.Add("face_ref");
        if (command.EdgeRef is not null) sent.Add("edge_ref");
        if (command.CoordinatesMm is not null) sent.Add("coordinates_mm");
        if (command.AssociationVertexRef is not null) sent.Add("association_vertex_ref");
        if (command.DisplacementMm is not null) sent.Add("displacement_mm");

        var foreign = sent.Where(f => !allowed.Contains(f, StringComparer.Ordinal)).ToArray();
        if (foreign.Length == 0)
        {
            return;
        }

        throw new KompasContractException(
            ErrorCodes.InvalidArgument,
            $"Поля {string.Join(", ", foreign)} не принадлежат способу '{command.Kind}/{command.Mode}'. " +
            $"Его поля: {string.Join(", ", allowed)}. Принятое и проигнорированное поле доживает до " +
            "приёмки, выглядя как выполненная правка, поэтому оно отвергается, а не игнорируется.",
            details: new Dictionary<string, object?>
            {
                ["foreign_fields"] = foreign,
                ["allowed_fields"] = allowed,
            });
    }

    private static KompasContractException Missing(string field, string key) => new(
        ErrorCodes.InvalidArgument,
        $"Для способа '{key}' обязательно поле {field}.",
        details: new Dictionary<string, object?> { ["missing_field"] = field });

    private static KompasContractException Geometry(string what, string? failure) => new(
        ErrorCodes.GeometryFailed,
        $"{what}: {failure ?? "причина не названа"}",
        RetryPolicy.SameOperationId,
        partialEffects: true,
        details: new Dictionary<string, object?> { ["failure"] = failure });
}
