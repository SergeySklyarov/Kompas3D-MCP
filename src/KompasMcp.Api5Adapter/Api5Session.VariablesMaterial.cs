using System.Globalization;
using System.Runtime.InteropServices;
using Kompas6API5;
using KompasMcp.Contracts;

namespace KompasMcp.Api5Adapter;

/// <summary>Variable and material operations — block VM. Read-back, not echo: every field of a response is
/// taken from the document AFTER the write, and every refusal is named rather than substituted.
/// History: docs/decisions/variables-material.md#adapter</remarks>
public sealed partial class Api5Session
{
    /// <summary>Unit the SDK page NAMES for the raw density reading of a part.</summary>
    /// <remarks>DOC: <c>kspart_getdensity.html</c> and <c>imassinertiaparam7_density.html</c> both name
    /// «плотность (г/куб.мм)». MEASURED: neither getter returns that unit — both return the value in g/cm3
    /// (steel 7850 kg/m3 reads 7.85, and the kernel's own mass agrees) — and no v24 source establishes
    /// g/cm3. The raw reading is therefore published with THIS unit as the documented one, and the
    /// normalized value is not published at all.
    /// History: docs/decisions/variables-material.md#units</remarks>
    private const string DocumentedRawDensityUnit = "g/mm3";

    /// <summary>Status of the published density: the reading exists, its unit is NOT confirmed.</summary>
    private const string DensityUnitUnconfirmed = "unconfirmed";

    /// <summary>Read the external variables of the top component of an open part.</summary>
    /// <remarks>DOC: <c>kspart_variablecollection.html</c> — <c>IPart.VariableCollection()</c> returns «указатель
    /// на интерфейс массива внешних переменных»; enumeration through <c>GetCount</c>/<c>GetByIndex</c>,
    /// exact search through <c>GetByName(name, testFullName, testIgnoreCase)</c>.
    /// INVARIANT: the collection is read from the DOCUMENT resolved here, so a reopened document yields its own
    /// variables and never a cached collection; and an ABSENT collection is a named failure, never an empty
    /// list — merged, "no collection" would pass for "no variables".
    /// History: docs/decisions/variables-material.md#read-variables</remarks>
    public ListVariablesResult ListVariables(ListVariablesCommand command)
    {
        var document = RequirePartDocument(command.DocumentId);
        var notes = new List<string>();
        var rows = new List<VariableRowDto>();

        var collection = TryVariableCollection(document, notes, out var collectionFailure);
        if (collection is null)
        {
            // NOT an empty list: the collection was not obtained, and that is a different fact.
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                "Коллекция внешних переменных детали не получена: " +
                (collectionFailure ?? "причина не установлена") +
                ". Пустой список переменных не подставляется — это два разных состояния.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?>
                {
                    ["document_id"] = document.Id,
                    ["failure"] = collectionFailure,
                });
        }

        int total;
        try
        {
            total = collection.GetCount();
        }
        catch (COMException ex)
        {
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                $"ksVariableCollection.GetCount() прервался: {ex.Message}. Размер коллекции не прочитан.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?> { ["document_id"] = document.Id });
        }

        var limit = command.Limit <= 0 ? 0 : command.Limit;
        var toRead = Math.Min(limit, total);
        for (var index = 0; index < toRead; index++)
        {
            var row = ReadVariableAt(document, collection, index, notes);
            if (row is not null)
            {
                rows.Add(row);
            }
        }

        if (toRead < total)
        {
            notes.Add($"truncated — прочитано {toRead} из {total} переменных (limit={command.Limit})");
        }

        if (total == 0)
        {
            // A genuinely empty collection IS a valid state and is reported as such — with the count, so it
            // cannot be confused with a failed read.
            notes.Add("collection_empty — коллекция существует и пуста: у верхнего компонента нет внешних переменных");
        }

        return new ListVariablesResult(
            rows,
            total,
            toRead,
            Truncated: toRead < total,
            Scope: "external/top_part",
            Revision: document.Revision,
            Diagnostics: notes);
    }

    /// <summary>Change the value or the expression of one external variable, addressed by exact name.</summary>
    /// <remarks>INVARIANT: before any COM write the document, the revision and the presence of THAT variable
    /// are checked; a bad address never becomes a partial write.
    /// DOC: <c>GetByName(name, TRUE, FALSE)</c> — matched FULL and case-sensitively, because the help gives an
    /// explicit mode rather than a default; <c>value</c>/<c>Expression</c> are read/write, and
    /// <c>RebuildModel</c> must run before the change reaches the model.
    /// INVARIANT (value mode): a variable with a NON-EMPTY expression is refused and its expression returned.
    /// History: docs/decisions/variables-material.md#set-variable</remarks>
    public SetVariableResult SetVariable(SetVariableCommand command)
    {
        var document = RequirePartDocument(command.DocumentId, command.ExpectedRevision);
        var notes = new List<string>();

        var byValue = command.Value is not null;
        var byExpression = command.Expression is not null;
        if (byValue == byExpression)
        {
            // The contract refusal happens before COM: exactly one mode must be chosen.
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                byValue
                    ? "Укажите ровно одно из двух: value или expression. Оба заданы одновременно."
                    : "Укажите ровно одно из двух: value или expression. Ни одно не задано.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?>
                {
                    ["value_present"] = byValue,
                    ["expression_present"] = byExpression,
                });
        }

        if (command.Value is { } numeric && !double.IsFinite(numeric))
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "value должно быть конечным числом (не NaN и не бесконечность).",
                RetryPolicy.Never,
                details: new Dictionary<string, object?> { ["value"] = double.IsNaN(numeric) ? "NaN" : "Infinity" });
        }

        if (string.IsNullOrWhiteSpace(command.Name))
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "name переменной не может быть пустым: имя — это адрес переменной.",
                RetryPolicy.Never);
        }

        var collection = TryVariableCollection(document, notes, out var collectionFailure);
        if (collection is null)
        {
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                "Коллекция внешних переменных детали не получена: " + (collectionFailure ?? "причина не установлена") +
                ". Изменение переменной невозможно.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?> { ["document_id"] = document.Id });
        }

        var before = FindVariable(collection, command.Name);
        if (before is null)
        {
            throw new KompasContractException(
                ErrorCodes.StaleReference,
                $"Переменная с именем «{command.Name}» не найдена во внешних переменных верхнего компонента. " +
                "Имя сравнивается ПОЛНОСТЬЮ и с учётом регистра; поиск подстрокой не выполняется.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?>
                {
                    ["name"] = command.Name,
                    ["document_id"] = document.Id,
                    ["match"] = "full_name_case_sensitive",
                });
        }

        var (expressionBeforeState, expressionBefore) = ReadExpressionField(before, notes);
        var (_, valueBefore) = ReadValueField(before, notes);

        // INVARIANT: "Expression read and empty" and "Expression NOT read" are different states. If the
        // string could not be read, whether the variable is governed by a formula is UNKNOWN, so a numeric
        // write could destroy one silently. The write is refused BEFORE any mutation; the refusal names the
        // read failure rather than treating the unknown as empty.
        if (byValue && expressionBeforeState != VariableFieldState.Read)
        {
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                $"Выражение переменной «{command.Name}» НЕ ПРОЧИТАНО, поэтому числовая запись отклонена ДО " +
                "мутации: неизвестно, управляется ли переменная выражением, и запись value могла бы " +
                "уничтожить формулу. Причина — в diagnostics; повторите чтение (list_variables).",
                RetryPolicy.ReacquireContext,
                details: new Dictionary<string, object?>
                {
                    ["name"] = command.Name,
                    ["expression_state"] = expressionBeforeState.ToString(),
                    ["mode"] = "value",
                    ["partial_effect"] = "none",
                });
        }

        if (byValue && !string.IsNullOrEmpty(expressionBefore) && !VariableExpression.IsPlainConstant(expressionBefore))
        {
            // REFUSAL, not an implicit overwrite: the expression COMPUTES the value, and destroying it would
            // change the model in a way the caller did not ask for. A plain constant is excluded on purpose —
            // it IS the value, and the kernel updates it to the number written (MEASURED).
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                $"Переменная «{command.Name}» управляется выражением «{expressionBefore}», поэтому запись " +
                "числового value отклонена: она уничтожила бы выражение. Задайте expression (в том числе " +
                "пустую строку, если выражение нужно снять) или измените переменную, от которой выражение " +
                "зависит.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?>
                {
                    ["name"] = command.Name,
                    ["current_expression"] = expressionBefore,
                    ["mode"] = "value",
                });
        }

        var revisionBefore = document.Revision;
        var mode = byValue ? "value" : "expression";

        if (byValue)
        {
            try
            {
                // MEASURED: the model is driven by the variable's EXPRESSION. Writing ksVariable.value
                // alone leaves the expression — and the geometry — untouched (depth stayed 10 after
                // value=20 plus a document rebuild), while writing the expression moves both. So for a
                // variable whose expression is a plain constant (the documented «число» assignment) the
                // constant is updated to the requested number; a variable with no expression takes the
                // plain value write. The interop surfaces both as settable properties.
                if (expressionBeforeState == VariableFieldState.Read
                    && VariableExpression.IsPlainConstant(expressionBefore))
                {
                    before.Expression = command.Value!.Value.ToString("R", CultureInfo.InvariantCulture);
                }
                else
                {
                    before.value = command.Value!.Value;
                }
            }
            catch (COMException ex)
            {
                throw new KompasContractException(
                    ErrorCodes.GeometryFailed,
                    $"Запись value переменной «{command.Name}» прервалась: {ex.Message}. " +
                    "Изменение не подтверждено; состояние модели перечитывается вызовом list_variables.",
                    RetryPolicy.ReacquireContext,
                    details: new Dictionary<string, object?>
                    {
                        ["name"] = command.Name,
                        ["partial_effect"] = "unknown",
                    });
            }
        }
        else
        {
            try
            {
                before.Expression = command.Expression!;
            }
            catch (COMException ex)
            {
                throw new KompasContractException(
                    ErrorCodes.GeometryFailed,
                    $"Запись Expression переменной «{command.Name}» прервалась: {ex.Message}. " +
                    "Изменение не подтверждено; состояние модели перечитывается вызовом list_variables.",
                    RetryPolicy.ReacquireContext,
                    details: new Dictionary<string, object?>
                    {
                        ["name"] = command.Name,
                        ["partial_effect"] = "unknown",
                    });
            }
        }

        // MEASURED: ksPart.RebuildModel() is DOCUMENTED as the call that carries external variables into the
        // model («Если изменены значения внешних переменных компонента, то они будут переданы в модель»), but
        // on the installed build it does NOT move variable-driven geometry, and after a document rebuild it
        // puts the old depth back. ksDocument3D.RebuildDocument() is the call that does. Both were measured on
        // a part whose extrusion depth references the variable, volume read after each call.
        bool? rebuildResult = null;
        try
        {
            rebuildResult = document.Require3D().RebuildDocument();
        }
        catch (COMException ex)
        {
            notes.Add("rebuild_failed — RebuildDocument() прервался: " + ex.Message);
        }

        if (rebuildResult != true)
        {
            notes.Add("rebuild_outcode=" + (rebuildResult is null ? "not_returned" : rebuildResult.Value.ToString()) +
                      " — исход вызова НЕ подтверждает, что модель приняла новое значение");
        }

        // CONFIRMATION IS A RE-READ from a FRESHLY FETCHED collection, not from the object written to.
        var afterCollection = TryVariableCollection(document, notes, out var afterFailure);
        var after = afterCollection is null ? null : FindVariable(afterCollection, command.Name);
        if (afterCollection is null)
        {
            notes.Add("readback_collection_failed — " + (afterFailure ?? "причина не установлена"));
        }
        else if (after is null)
        {
            notes.Add("readback_missing — переменная не найдена в коллекции после применения");
        }

        var expressionAfterState = VariableFieldState.NotRead;
        var valueAfterState = VariableFieldState.NotRead;
        string? expressionAfter = null;
        double? valueAfter = null;
        if (after is not null)
        {
            (expressionAfterState, expressionAfter) = ReadExpressionField(after, notes);
            (valueAfterState, valueAfter) = ReadValueField(after, notes);
        }

        // CONFIRMATION IS A DECISION over the re-read facts, not the presence of the object: a value that did
        // not take, a field that was not read and a false apply outcode each withhold confirmation by name.
        var decision = VariableWriteConfirmation.Decide(new VariableWriteFacts
        {
            Mode = mode,
            RequestedValue = command.Value,
            RequestedExpression = command.Expression,
            ExpressionAfterState = expressionAfterState,
            ExpressionAfter = expressionAfter,
            ValueAfterState = valueAfterState,
            ValueAfter = valueAfter,
            ApplyResult = rebuildResult,
        });
        if (!decision.Confirmed)
        {
            notes.Add("read_back_unconfirmed — запись не подтверждена чтением: " +
                      string.Join(", ", decision.Reasons));
        }

        BumpRevision(document, "var." + mode);

        return new SetVariableResult(
            command.Name,
            expressionBefore,
            valueBefore,
            expressionAfter,
            valueAfter,
            mode,
            rebuildResult,
            decision.Confirmed,
            revisionBefore,
            document.Revision,
            notes);
    }

    /// <summary>Read the material name and the RAW density reading of the top component of an open part.</summary>
    /// <remarks>DOC: <c>kspart_material.html</c> — «Обозначение материала можно получить только у детали»;
    /// <c>kspart_getdensity.html</c> — «0 — в случае неудачи (если компонент — не деталь)», and the return is
    /// named «плотность (г/куб.мм)». MEASURED: the installed build returns the value in g/cm3 instead, and so
    /// does <c>IMassInertiaParam7.Density</c>, whose page names g/mm3 as well. No v24 source establishes
    /// g/cm3, so the normalized value is NOT published (the field is null and the status is named).
    /// INVARIANT: the name read and the density read are reported SEPARATELY — a successful name read does not
    /// imply a successful density read. A density of exactly 0 is the documented FAILURE signal, not a
    /// measured zero density, and the server's reference table is never substituted for it.
    /// History: docs/decisions/variables-material.md#units</remarks>
    public GetMaterialResult GetMaterial(GetMaterialCommand command)
    {
        var document = RequirePartDocument(command.DocumentId);
        var notes = new List<string>();

        string? materialName = null;
        var nameRead = false;
        try
        {
            materialName = RequireTopPart(document).material;
            nameRead = true;
        }
        catch (COMException ex)
        {
            notes.Add("material_read_failed — ksPart.material прервался: " + ex.Message);
        }

        double? rawDensity = null;
        var densityRead = false;
        try
        {
            var raw = RequireTopPart(document).density;
            if (raw > 0)
            {
                rawDensity = raw;
                densityRead = true;
                notes.Add("density_unit_unconfirmed — сырое чтение " + raw.ToString("R", CultureInfo.InvariantCulture)
                    + " сохранено, но его единица НЕ подтверждена документацией: справка называет г/куб.мм, "
                    + "ядро отдаёт значение, согласованное с г/куб.см (измерено на двух плотностях), и "
                    + "официального источника г/куб.см нет. Нормализованная плотность не публикуется.");
            }
            else
            {
                // DOC: 0 is the documented failure outcode. It is NOT a measured density of zero.
                notes.Add("density_read_failed — GetDensity() вернул 0, что документировано как НЕУДАЧА " +
                          "(компонент — не деталь). Плотность из справочника сервера НЕ подставлена.");
            }
        }
        catch (COMException ex)
        {
            notes.Add("density_read_failed — GetDensity() прервался: " + ex.Message);
        }

        return new GetMaterialResult(
            materialName,
            nameRead,
            rawDensity,
            densityRead,
            DocumentedRawDensityUnit,
            DensityUnitUnconfirmed,
            DensityNormalizedKgPerM3: null,
            document.Revision,
            notes);
    }

    /// <summary>Assign material name and explicit density to the top component of a part.</summary>
    /// <remarks>DOC: <c>kspart_setmaterial.html</c> — <c>SetMaterial(name, density)</c>, density in
    /// <b>g/cm3</b>, notes: must be a part, must not be a library model or a standard element, in a part acts
    /// on the TOP component, «изменение материала вступает в силу после вызова метода ksPart::Update».
    /// INVARIANT: the response is built from a RE-READ after <c>Update</c>, never by echoing the request; the
    /// requested values are carried in their own fields so that a mismatch is visible rather than smoothed.
    /// INVARIANT: the CONFIRMED (material name, call outcodes) and the UNCONFIRMED (physical density, whose
    /// reading unit is not established) are reported apart; the raw numeric equality is a diagnostic only.
    /// History: docs/decisions/variables-material.md#material</remarks>
    public SetMaterialResult SetMaterial(SetMaterialCommand command)
    {
        var document = RequirePartDocument(command.DocumentId, command.ExpectedRevision);
        var notes = new List<string>();

        if (string.IsNullOrWhiteSpace(command.MaterialName))
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "material_name не может быть пустым: материал задаётся обозначением из справочника.",
                RetryPolicy.Never);
        }

        if (!double.IsFinite(command.DensityKgPerM3) || command.DensityKgPerM3 <= 0)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "density_kg_per_m3 должно быть положительным конечным числом.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?>
                {
                    ["density_kg_per_m3"] = double.IsFinite(command.DensityKgPerM3)
                        ? command.DensityKgPerM3
                        : (double.IsNaN(command.DensityKgPerM3) ? "NaN" : "Infinity"),
                });
        }

        var part = RequireTopPart(document);
        var revisionBefore = document.Revision;

        // DOC: the call takes g/cm3. MEASURED: the kernel takes exactly that unit (7.85 written for steel
        // reads back as 7.85), so the caller's kg/m3 goes through the one documented conversion.
        var densityGPerCm3 = DensityUnits.KgPerM3ToGramsPerCm3(command.DensityKgPerM3);
        notes.Add("density_written_g_per_cm3=" + densityGPerCm3.ToString("R", CultureInfo.InvariantCulture) +
                  " — единица, документированная для SetMaterial");

        bool? setReturned = null;
        try
        {
            setReturned = part.SetMaterial(command.MaterialName, densityGPerCm3);
        }
        catch (COMException ex)
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                $"ksPart.SetMaterial прервался: {ex.Message}. Материал не подтверждён; " +
                "состояние перечитывается вызовом get_material.",
                RetryPolicy.ReacquireContext,
                details: new Dictionary<string, object?> { ["material_name"] = command.MaterialName });
        }

        if (setReturned != true)
        {
            notes.Add("set_material_returned=" + (setReturned is null ? "not_returned" : setReturned.Value.ToString()) +
                      " — метод не подтвердил установку; состояние читается дальше");
        }

        // DOC: «Изменение материала вступает в силу после вызова метода ksPart::Update».
        bool? updateReturned = null;
        try
        {
            updateReturned = part.Update();
        }
        catch (COMException ex)
        {
            notes.Add("update_failed — ksPart.Update() прервался: " + ex.Message);
        }

        string? readName = null;
        var nameMatches = false;
        try
        {
            readName = part.material;
            nameMatches = string.Equals(readName, command.MaterialName, StringComparison.Ordinal);
        }
        catch (COMException ex)
        {
            notes.Add("material_reread_failed — ksPart.material прервался: " + ex.Message);
        }

        // DIAGNOSTIC, NOT CONFIRMATION. The re-read raw value is compared with the written raw value as a
        // NUMERIC equality check only. The unit of the reading is NOT established — the page names g/mm3, the
        // kernel returns a value consistent with g/cm3, and no v24 source confirms either — so equal numbers
        // are a fact about two numbers, not proof that the assigned PHYSICAL density took. What the response
        // CONFIRMS is the material name and the outcodes of the calls; the density is reported unconfirmed.
        double? readRaw = null;
        var rawNumericMatches = false;
        try
        {
            var raw = part.density;
            if (raw > 0)
            {
                readRaw = raw;
                rawNumericMatches = Math.Abs(raw - densityGPerCm3) < 0.5 / 1000.0;
                notes.Add("density_unit_unconfirmed — плотность перечитана сырым показанием и сравнена с "
                          + "записанным только как ЧИСЛОВОЕ равенство (диагностика, не подтверждение): единица "
                          + "чтения не подтверждена документом (см. get_material), поэтому назначенная "
                          + "ФИЗИЧЕСКАЯ плотность чтением НЕ подтверждена, а нормализованное значение не "
                          + "публикуется. Подтверждены имя материала и исходы вызовов.");
            }
            else
            {
                notes.Add("density_reread_failed — GetDensity() вернул 0 (документированная НЕУДАЧА), "
                          + "запрошенная плотность не подтверждена чтением");
            }
        }
        catch (COMException ex)
        {
            notes.Add("density_reread_failed — GetDensity() прервался: " + ex.Message);
        }

        if (!nameMatches)
        {
            notes.Add("name_mismatch — запрошено «" + command.MaterialName + "», прочитано «" +
                      (readName ?? "не прочитано") + "»");
        }

        BumpRevision(document, "mat.set");

        return new SetMaterialResult(
            command.MaterialName,
            command.DensityKgPerM3,
            densityGPerCm3,
            readName,
            nameMatches,
            readRaw,
            DocumentedRawDensityUnit,
            DensityUnitUnconfirmed,
            DensityNormalizedKgPerM3: null,
            rawNumericMatches,
            setReturned,
            updateReturned,
            revisionBefore,
            document.Revision,
            notes);
    }

    /// <summary>Resolve a document as a PART — the top component the documented routes act on.</summary>
    /// <remarks>INVARIANT: a wrong document kind is a NAMED refusal before COM, not an unhandled COM
    /// exception. LIMIT: assemblies are out of scope for this block, so `kind=assembly` is refused by name
    /// rather than served through a route the help documents for inserted parts.
    /// History: docs/decisions/variables-material.md#addressing</remarks>
    private DocumentEntry RequirePartDocument(string documentId)
    {
        var document = RequireDocument(documentId);
        if (document.Kind != DocumentKind.Part)
        {
            throw new KompasContractException(
                ErrorCodes.WrongDocumentKind,
                $"Документ «{document.Id}» имеет тип {document.Kind}: переменные и материал верхнего " +
                "компонента читаются и меняются только у самостоятельной детали (kind=part).",
                RetryPolicy.Never,
                details: new Dictionary<string, object?>
                {
                    ["kind"] = document.Kind.ToString(),
                    ["required_kind"] = "part",
                });
        }

        return document;
    }

    private DocumentEntry RequirePartDocument(string documentId, long expectedRevision)
    {
        var document = RequirePartDocument(documentId);
        if (document.Revision != expectedRevision)
        {
            throw new KompasContractException(
                ErrorCodes.RevisionConflict,
                $"Операция запрошена для ревизии {expectedRevision}, у документа {document.Revision}.",
                RetryPolicy.ReacquireContext,
                details: new Dictionary<string, object?>
                {
                    ["requested_revision"] = expectedRevision,
                    ["current_revision"] = document.Revision,
                });
        }

        return document;
    }

    /// <summary>The top component of an open part as <c>ksPart</c>, or a named refusal.</summary>
    private ksPart RequireTopPart(DocumentEntry document) => document.Part
        ?? throw new KompasContractException(
            ErrorCodes.CapabilityUnavailable,
            $"Верхний компонент документа «{document.Id}» не доступен как ksPart: маршрут переменных и " +
            "материала требует открытую деталь.",
            RetryPolicy.Never,
            details: new Dictionary<string, object?> { ["document_id"] = document.Id });

    /// <summary>Obtain the documented array of external variables, naming a failure instead of returning
    /// null silently.</summary>
    /// <remarks>DOC: <c>kspart_variablecollection.html</c> — <c>IPart.VariableCollection()</c>.
    /// INVARIANT: this helper never turns a failure into an empty collection; the caller decides, and the
    /// reason travels in `failure`.</remarks>
    private ksVariableCollection? TryVariableCollection(
        DocumentEntry document, List<string> notes, out string? failure)
    {
        failure = null;
        try
        {
            var part = RequireTopPart(document);
            if (part.VariableCollection() is ksVariableCollection collection)
            {
                return collection;
            }

            failure = "VariableCollection() не вернул ksVariableCollection";
        }
        catch (COMException ex)
        {
            failure = "VariableCollection() прервался: " + ex.Message;
        }
        catch (KompasContractException ex)
        {
            failure = ex.Message;
        }

        notes.Add("variable_collection_failed — " + failure);
        return null;
    }

    /// <summary>Find a variable by FULL name, case-sensitively — the documented exact-address mode.</summary>
    /// <remarks>DOC: <c>ksvariablecollection_getbyname.html</c> — <c>testFullName = TRUE</c> means the name is
    /// full, <c>testIgnoreCase = FALSE</c> means case matters. The substring mode
    /// (<c>testFullName = FALSE</c>) is deliberately not used: it would resolve a DIFFERENT variable than the
    /// one named, and a mutation would land on the wrong object.</remarks>
    private static ksVariable? FindVariable(ksVariableCollection collection, string name)
    {
        try
        {
            return collection.GetByName(name, true, false) as ksVariable;
        }
        catch (COMException)
        {
            // A COM failure here means "not resolved", and the caller reports a named refusal. It is NOT
            // reported as "found".
            return null;
        }
    }

    /// <summary>Read one variable by index, naming an unread field rather than filling it.</summary>
    private static VariableRowDto? ReadVariableAt(
        DocumentEntry document, ksVariableCollection collection, int index, List<string> notes)
    {
        ksVariable? variable;
        try
        {
            variable = collection.GetByIndex(index) as ksVariable;
        }
        catch (COMException ex)
        {
            notes.Add($"GetByIndex({index}) прервался: {ex.Message}");
            return null;
        }

        if (variable is null)
        {
            notes.Add($"GetByIndex({index}) не вернул ksVariable");
            return null;
        }

        var name = SafeString(() => variable.name, notes, $"name[{index}]");
        var value = SafeNullableDouble(() => variable.value, notes, $"value[{index}]");
        var expression = SafeString(() => variable.Expression, notes, $"Expression[{index}]");

        // The help names no flag for "has an active expression". It is DERIVED from the read string, and the
        // derivation is NAMED as a derivation — not presented as an API field.
        bool? hasExpression = expression is null ? null : !string.IsNullOrEmpty(expression);
        if (hasExpression is true)
        {
            notes.Add($"has_expression[{index}] — производный признак: строка Expression непуста");
        }

        var displayName = SafeString(() => variable.displayName, notes, $"displayName[{index}]");
        var note = SafeString(() => variable.note, notes, $"note[{index}]");

        return new VariableRowDto(
            name ?? $"<не прочитано: индекс {index}>",
            value,
            expression,
            hasExpression,
            displayName,
            note);
    }

    /// <summary>Read <c>Expression</c> with its READ STATE kept apart from its value.</summary>
    /// <remarks>INVARIANT: a COM failure yields <see cref="VariableFieldState.NotRead"/>, never a null that
    /// would be indistinguishable from "read and empty". History: docs/decisions/variables-material.md</remarks>
    private static (VariableFieldState State, string? Value) ReadExpressionField(
        ksVariable variable, List<string> notes)
        => SafeField(() => variable.Expression, notes, "Expression");

    /// <summary>Read <c>value</c> with its READ STATE kept apart from its value.</summary>
    private static (VariableFieldState State, double? Value) ReadValueField(
        ksVariable variable, List<string> notes)
        => SafeField(() => variable.value, notes, "value");

    private static (VariableFieldState State, string? Value) SafeField(
        Func<string?> read, List<string> notes, string field)
    {
        try
        {
            return (VariableFieldState.Read, read());
        }
        catch (COMException ex)
        {
            notes.Add(field + "_not_read — " + ex.Message);
            return (VariableFieldState.NotRead, null);
        }
    }

    private static (VariableFieldState State, double? Value) SafeField(
        Func<double> read, List<string> notes, string field)
    {
        try
        {
            return (VariableFieldState.Read, read());
        }
        catch (COMException ex)
        {
            notes.Add(field + "_not_read — " + ex.Message);
            return (VariableFieldState.NotRead, null);
        }
    }

    private static string? SafeString(Func<string?> read, List<string> notes, string field)
    {
        try
        {
            return read();
        }
        catch (COMException ex)
        {
            notes.Add(field + "_not_read — " + ex.Message);
            return null;
        }
    }

    private static double? SafeNullableDouble(Func<double> read, List<string> notes, string field)
    {
        try
        {
            return read();
        }
        catch (COMException ex)
        {
            notes.Add(field + "_not_read — " + ex.Message);
            return null;
        }
    }
}
