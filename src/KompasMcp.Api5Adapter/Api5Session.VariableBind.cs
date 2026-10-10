using System.Runtime.InteropServices;
using Kompas6API5;
using KompasAPI7;
using KompasMcp.Contracts;
using KompasMcp.Domain.Geometry;

namespace KompasMcp.Api5Adapter;

/// <summary>Variable creation and parameter binding - block G3. Read-back, not echo: every response field
/// comes from the document AFTER the write, and every refusal is named.</summary>
/// <remarks>DOC: <c>IPart7.AddVariable(Name, Value, Note)</c> creates a top-component variable (the third
/// argument is a NOTE); <c>IsVariableNameValid</c> checks the name; <c>External</c> and <c>Expression</c> are
/// read/write. A feature's parameters come from <c>ksEntity.GetFeature()</c> →
/// <c>ksFeature.VariableCollection</c>; a parameter is bound by writing a variable name into its
/// <c>Expression</c>. LIMIT: KOMPAS evaluates expressions; the server never executes them as code.
/// History: docs/decisions/variables-material.md#g3-route</remarks>
public sealed partial class Api5Session
{
    /// <summary>Create an external variable of the top component of an open part.</summary>
    /// <remarks>INVARIANT: name validity and the absence of an existing variable are checked BEFORE any
    /// write; an empty expression is refused before COM. The response is built from a freshly fetched
    /// collection; "created but not external" and "created but missing from the collection" are NAMED
    /// refusals with their consequences, never a false success.
    /// History: docs/decisions/variables-material.md#g3-route</remarks>
    public CreateVariableResult CreateVariable(CreateVariableCommand command)
    {
        var document = RequirePartDocument(command.DocumentId, command.ExpectedRevision);
        var notes = new List<string>();

        if (string.IsNullOrWhiteSpace(command.Name))
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "name переменной не может быть пустым: имя - это адрес переменной.",
                RetryPolicy.Never);
        }

        if (!double.IsFinite(command.Value))
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "value должно быть конечным числом (не NaN и не бесконечность).",
                RetryPolicy.Never,
                details: new Dictionary<string, object?>
                {
                    ["value"] = double.IsNaN(command.Value) ? "NaN" : "Infinity",
                });
        }

        // INVARIANT: an empty expression is NOT a supported kernel state (MEASURED: the write reports
        // success, the old expression stays and the variable leaves the collection). When none is given a
        // constant expression equal to the value is used - the documented «число или константа» form.
        string expression;
        if (command.Expression is null)
        {
            expression = VariableBindRules.ConstantExpression(command.Value);
        }
        else if (string.IsNullOrWhiteSpace(command.Expression))
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "expression не может быть пустым: пустое выражение ядром не поддерживается (запись сообщает "
                + "об успехе, выражение остаётся прежним, переменная покидает коллекцию).",
                RetryPolicy.Never);
        }
        else
        {
            expression = command.Expression;
        }

        // The flag is nullable in the contract so an omitted field means "external" (the documented
        // external-variable flag), while an explicit false is honoured.
        var external = command.External ?? true;

        var bridge = BridgeFor(document);
        var container = bridge.ContainerFor(document.Document3D, document.Id, document.Revision);
        if (container is not IPart7 part7)
        {
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                "Маршрут API7 не построил верхний компонент как IPart7: "
                + (bridge.BridgeFailure ?? "причина не известна") + ". Переменная не создавалась.",
                RetryPolicy.ReacquireContext);
        }

        // DOC: ipart7_isvariablenamevalid.html - «Проверить допустимость создания новой переменной с
        // данным именем»; TRUE - имя допустимо, FALSE - недопустимо. Checked BEFORE any write.
        bool nameValid;
        try
        {
            nameValid = part7.IsVariableNameValid(command.Name);
        }
        catch (COMException ex)
        {
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                "IPart7.IsVariableNameValid прервался: " + ex.Message + ". Проверка имени не выполнена, "
                + "переменная не создавалась.",
                RetryPolicy.ReacquireContext);
        }

        if (!nameValid)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                $"Имя «{command.Name}» недопустимо для переменной: разрешены латинские буквы, цифры и «_», "
                + "первый символ - буква или «_» (ядро ответило IsVariableNameValid=false). Переменная не "
                + "создавалась.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?> { ["name"] = command.Name });
        }

        // INVARIANT: an existing variable of the same name is REFUSED, not overwritten.
        var existing = TryVariableCollection(document, notes, out var collectionFailure);
        if (existing is null)
        {
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                "Коллекция внешних переменных детали не получена: "
                + (collectionFailure ?? "причина не установлена")
                + ". Проверить существование имени невозможно, переменная не создавалась.",
                RetryPolicy.ReacquireContext,
                details: new Dictionary<string, object?> { ["document_id"] = document.Id });
        }

        if (FindVariable(existing, command.Name) is not null)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                $"Переменная с именем «{command.Name}» уже существует у верхнего компонента: создание "
                + "отклонено, перезапись не выполняется.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?> { ["name"] = command.Name, ["existing"] = true });
        }

        IVariable7? made;
        try
        {
            made = part7.AddVariable(command.Name, command.Value, command.Note ?? string.Empty) as IVariable7;
        }
        catch (COMException ex)
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "IPart7.AddVariable прервался: " + ex.Message + ". Переменная не подтверждена; состояние "
                + "перечитывается вызовом kompas_list_variables.",
                RetryPolicy.ReacquireContext,
                partialEffects: true,
                details: new Dictionary<string, object?> { ["name"] = command.Name });
        }

        if (made is null)
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "IPart7.AddVariable вернул null: переменная НЕ создана (справка обещает указатель на "
                + "IVariable7). Ложный успех не подставляется.",
                RetryPolicy.ReacquireContext,
                details: new Dictionary<string, object?>
                {
                    ["name"] = command.Name,
                    ["partial_effects"] = "none",
                });
        }

        try
        {
            made.External = external;
        }
        catch (COMException ex)
        {
            notes.Add("external_write_failed - " + ex.Message);
        }

        try
        {
            made.Expression = expression;
        }
        catch (COMException ex)
        {
            notes.Add("expression_write_failed - " + ex.Message);
        }

        // MEASURED: the model is driven by the variable's EXPRESSION and applied by the DOCUMENT rebuild;
        // ksPart.RebuildModel does not move variable-driven geometry on the installed build.
        bool? rebuildResult = null;
        try
        {
            rebuildResult = document.Require3D().RebuildDocument();
        }
        catch (COMException ex)
        {
            notes.Add("rebuild_failed - RebuildDocument() прервался: " + ex.Message);
        }

        var revisionBefore = document.Revision;

        // CONFIRMATION IS A RE-READ from a FRESHLY FETCHED collection, not from the object written to.
        var afterCollection = TryVariableCollection(document, notes, out var afterFailure);
        var after = afterCollection is null ? null : FindVariable(afterCollection, command.Name);
        if (afterCollection is null)
        {
            notes.Add("readback_collection_failed - " + (afterFailure ?? "причина не установлена"));
        }

        if (after is null)
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                $"Переменная «{command.Name}» не найдена в коллекции верхнего компонента после создания: "
                + "AddVariable не дал подтверждённого состояния. Последствия названы: переменная могла быть "
                + "создана и не попасть в массив внешних переменных либо исчезнуть из-за негодного выражения.",
                RetryPolicy.ReacquireContext,
                partialEffects: true,
                details: new Dictionary<string, object?>
                {
                    ["name"] = command.Name,
                    ["revision_after"] = document.Revision,
                });
        }

        var (_, nameRead) = SafeField(() => after.name, notes, "name");
        var (_, valueRead) = SafeField(() => after.value, notes, "value");
        var (_, expressionRead) = SafeField(() => after.Expression, notes, "Expression");
        var (_, externalRead) = SafeBoolField(() => after.external, notes, "external");
        var (_, noteRead) = SafeField(() => after.note, notes, "note");

        // INVARIANT: "created but not external" is a NAMED failure with its consequence, not a success -
        // an internal variable is invisible to kompas_list_variables and cannot drive a parameter.
        if (external && externalRead != true)
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                $"Переменная «{command.Name}» создана, но НЕ является внешней (external прочитан как "
                + BoolText(externalRead) + "): она не видна в коллекции внешних переменных и не может "
                + "управлять параметром. Это названное последствие, а не успех.",
                RetryPolicy.ReacquireContext,
                partialEffects: true,
                details: new Dictionary<string, object?>
                {
                    ["name"] = command.Name,
                    ["external_read"] = externalRead,
                    ["revision_after"] = document.Revision,
                });
        }

        var readBackVerified = string.Equals(nameRead, command.Name, StringComparison.Ordinal)
            && valueRead is double value && VariableBindRules.SameValue(value, command.Value)
            && string.Equals(expressionRead, expression, StringComparison.Ordinal);
        if (!readBackVerified)
        {
            notes.Add("read_back_unconfirmed - прочитано имя «" + (nameRead ?? "не прочитано") + "», значение "
                + Num(valueRead) + ", выражение «" + (expressionRead ?? "не прочитано") + "»");
        }

        if (rebuildResult != true)
        {
            notes.Add("rebuild_outcode="
                + (rebuildResult is null ? "not_returned" : rebuildResult.Value.ToString())
                + " - исход вызова НЕ подтверждает, что модель приняла изменение");
        }

        BumpRevision(document, "var.create");

        var checks = new List<NamedCheck>
        {
            new("name_read_back", string.Equals(nameRead, command.Name, StringComparison.Ordinal),
                Observed: nameRead, Expected: command.Name),
            new("value_read_back", valueRead is double v && VariableBindRules.SameValue(v, command.Value),
                Observed: Num(valueRead), Expected: Num(command.Value)),
            new("expression_read_back", string.Equals(expressionRead, expression, StringComparison.Ordinal),
                Observed: expressionRead, Expected: expression),
            new("external_read_back", !external || externalRead == true,
                Observed: BoolText(externalRead), Expected: external.ToString()),
        };

        return new CreateVariableResult(
            command.Name, command.Value, expression, external,
            nameRead, valueRead, expressionRead, externalRead, noteRead,
            readBackVerified, revisionBefore, document.Revision,
            new VerificationDto(VerificationLevel.StructureChecked, checks, new List<string>
            {
                "geometry_not_measured - создание переменной геометрию не меняет, объём здесь не измеряется",
            }),
            notes);
    }

    /// <summary>Read the parameter variables of one feature (operation).</summary>
    /// <remarks>DOC: <c>ksentity_getfeature.html</c>, <c>ksfeature_variablecollection.html</c>.
    /// INVARIANT: an unread field is null with a reason, never 0 or an empty string; a feature with no
    /// parameters is an EMPTY list with a reason, not a refusal.
    /// History: docs/decisions/variables-material.md#g3-route</remarks>
    public ListFeatureParametersResult ListFeatureParameters(ListFeatureParametersCommand command)
    {
        var document = RequirePartDocument(command.DocumentId);
        var notes = new List<string>();
        var (document2, entity) = RequireFeatureEntity(command.FeatureRef);

        var feature = GetFeatureOf(entity, notes);
        if (feature is null)
        {
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                $"У признака «{command.FeatureRef}» не получен ksFeature через ksEntity.GetFeature(): "
                + "перечислить переменные-параметры операции нечем.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?>
                {
                    ["document_id"] = document2.Id,
                    ["feature_ref"] = command.FeatureRef,
                });
        }

        var collection = FeatureVariableCollection(feature, notes);
        if (collection is null)
        {
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                $"ksFeature.VariableCollection признака «{command.FeatureRef}» не дал ksVariableCollection: "
                + "массив переменных-параметров не получен.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?>
                {
                    ["document_id"] = document2.Id,
                    ["feature_ref"] = command.FeatureRef,
                });
        }

        var featureName = SafeString(() => feature.name, notes, "feature.name");
        var enumerated = EnumerateVariables(collection, notes);
        if (enumerated is null)
        {
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                $"ksVariableCollection.GetCount() признака «{command.FeatureRef}» не прочитан: размер "
                + "массива переменных-параметров не установлен, пустой список не подставляется.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?>
                {
                    ["document_id"] = document2.Id,
                    ["feature_ref"] = command.FeatureRef,
                });
        }

        var rows = new List<FeatureParameterRowDto>();
        for (var i = 0; i < enumerated.Count; i++)
        {
            var variable = enumerated[i];
            if (variable is null)
            {
                rows.Add(new FeatureParameterRowDto(i, null, null, null, null, null, null));
                continue;
            }

            var name = SafeString(() => variable.name, notes, $"name[{i}]");
            var displayName = SafeString(() => variable.displayName, notes, $"displayName[{i}]");
            var parameterNote = SafeString(() => variable.parameterNote, notes, $"parameterNote[{i}]");
            var value = SafeNullableDouble(() => variable.value, notes, $"value[{i}]");
            var expression = SafeString(() => variable.Expression, notes, $"Expression[{i}]");
            var external = SafeBoolField(() => variable.external, notes, $"external[{i}]").Value;
            rows.Add(new FeatureParameterRowDto(i, name, displayName, parameterNote, value, expression, external));
        }

        if (rows.Count == 0)
        {
            // An empty parameter array IS a valid state and is reported as such - with the count, so it
            // cannot be confused with a failed read.
            notes.Add("feature_parameters_empty - у признака нет переменных-параметров: массив существует "
                + "и пуст");
        }

        return new ListFeatureParametersResult(
            command.FeatureRef,
            featureName,
            rows,
            rows.Count,
            document2.Revision,
            notes);
    }

    /// <summary>Bind one parameter of a feature to an expression, addressed by exact parameter name.</summary>
    /// <remarks>INVARIANT: the address is the EXACT <c>name</c> of the parameter (never value or position);
    /// zero or several exact matches is INVALID_ARGUMENT with the list of names and NO write. An empty
    /// expression is refused before COM. «Written» and «applied» are separate: the expression is re-read and
    /// the value re-computed from a freshly fetched collection after the document rebuild, and a mismatch is
    /// a NAMED refusal with the model state.
    /// History: docs/decisions/variables-material.md#g3-route</remarks>
    public BindParameterResult BindParameter(BindParameterCommand command)
    {
        var document = RequirePartDocument(command.DocumentId, command.ExpectedRevision);
        var notes = new List<string>();

        if (string.IsNullOrWhiteSpace(command.ParameterName))
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "parameter_name не может быть пустым: адрес параметра - его точное имя.",
                RetryPolicy.Never);
        }

        if (string.IsNullOrWhiteSpace(command.Expression))
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "expression не может быть пустым: пустое выражение ядром не поддерживается. Снятие привязки "
                + "выполняется числовой константой (например \"10\").",
                RetryPolicy.Never);
        }

        var (document2, entity) = RequireFeatureEntity(command.FeatureRef);
        var feature = GetFeatureOf(entity, notes);
        if (feature is null)
        {
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                $"У признака «{command.FeatureRef}» не получен ksFeature через ksEntity.GetFeature(): "
                + "адресовать параметр нечем.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?>
                {
                    ["document_id"] = document2.Id,
                    ["feature_ref"] = command.FeatureRef,
                });
        }

        var collection = FeatureVariableCollection(feature, notes);
        if (collection is null)
        {
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                $"ksFeature.VariableCollection признака «{command.FeatureRef}» не дал ksVariableCollection: "
                + "параметры операции не прочитаны, запись не выполнялась.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?>
                {
                    ["document_id"] = document2.Id,
                    ["feature_ref"] = command.FeatureRef,
                });
        }

        var before = EnumerateVariables(collection, notes);
        if (before is null)
        {
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                $"ksVariableCollection.GetCount() признака «{command.FeatureRef}» не прочитан: адресовать "
                + "параметр по имени невозможно, запись не выполнялась.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?>
                {
                    ["document_id"] = document2.Id,
                    ["feature_ref"] = command.FeatureRef,
                });
        }

        // INVARIANT: the address is the EXACT name; an unread name never matches, and a match count other
        // than one is a refusal with the list of available names, never "the first one".
        var names = new List<string?>(before.Count);
        var parameters = new List<ksVariable?>(before.Count);
        for (var i = 0; i < before.Count; i++)
        {
            var variable = before[i];
            parameters.Add(variable);
            names.Add(variable is null ? null : SafeString(() => variable.name, notes, $"name[{i}]"));
        }

        var address = VariableBindRules.SelectParameter(names, command.ParameterName);
        if (address.Verdict != ParameterAddressVerdict.Found)
        {
            var available = names.Where(n => n is not null).ToList();
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                address.Verdict == ParameterAddressVerdict.NotFound
                    ? $"Параметр с точным именем «{command.ParameterName}» не найден у признака "
                      + $"«{command.FeatureRef}». Доступные имена: {string.Join(", ", available)}. Запись НЕ "
                      + "выполнялась."
                    : $"Точное имя «{command.ParameterName}» встретилось более одного раза у признака "
                      + $"«{command.FeatureRef}» — адрес неоднозначен. Доступные имена: "
                      + $"{string.Join(", ", available)}. Запись НЕ выполнялась.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?>
                {
                    ["parameter_name"] = command.ParameterName,
                    ["feature_ref"] = command.FeatureRef,
                    ["available_names"] = available,
                    ["match_verdict"] = address.Verdict.ToString(),
                    ["partial_effects"] = "none",
                });
        }

        var target = parameters[address.Index]!;
        var parameterNote = SafeString(() => target.parameterNote, notes, "parameterNote");
        var revisionBefore = document.Revision;
        var volumeBefore = ReadModelState(document).VolumeMm3;

        try
        {
            target.Expression = command.Expression;
        }
        catch (COMException ex)
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                $"Запись Expression параметра «{command.ParameterName}» прервалась: {ex.Message}. Привязка "
                + "не подтверждена; состояние перечитывается вызовом kompas_list_feature_parameters.",
                RetryPolicy.ReacquireContext,
                partialEffects: true,
                details: new Dictionary<string, object?>
                {
                    ["parameter_name"] = command.ParameterName,
                    ["revision_after"] = document.Revision,
                });
        }

        // MEASURED: the DOCUMENT rebuild is the call that carries a variable-driven change into the model;
        // ksPart.RebuildModel does not move it. DOC: ksdocument3d_rebuilddocument.html.
        bool? rebuildResult = null;
        try
        {
            rebuildResult = document.Require3D().RebuildDocument();
        }
        catch (COMException ex)
        {
            notes.Add("rebuild_failed - RebuildDocument() прервался: " + ex.Message);
        }

        // CONFIRMATION IS A RE-READ of the feature AND the collection, freshly fetched.
        var afterFeature = GetFeatureOf(entity, notes);
        var afterCollection = afterFeature is null ? null : FeatureVariableCollection(afterFeature, notes);
        var after = afterCollection is null ? null : EnumerateVariables(afterCollection, notes);

        string? expressionAfter = null;
        double? valueAfter = null;
        var foundAfter = false;
        if (after is not null)
        {
            for (var i = 0; i < after.Count; i++)
            {
                var variable = after[i];
                if (variable is null)
                {
                    continue;
                }

                if (!string.Equals(SafeString(() => variable.name, notes, $"name_after[{i}]"),
                        command.ParameterName, StringComparison.Ordinal))
                {
                    continue;
                }

                foundAfter = true;
                expressionAfter = SafeString(() => variable.Expression, notes, "Expression_after");
                valueAfter = SafeNullableDouble(() => variable.value, notes, "value_after");
                break;
            }
        }

        var volumeAfter = ReadModelState(document).VolumeMm3;
        var volumeDelta = volumeBefore is double vb && volumeAfter is double va ? va - vb : (double?)null;
        var rebuildSucceeded = rebuildResult == true;

        var expressionMatched = string.Equals(expressionAfter, command.Expression, StringComparison.Ordinal);
        var applied = foundAfter && expressionMatched && valueAfter is not null && rebuildSucceeded;

        if (!applied)
        {
            notes.Add("bind_unconfirmed - записано «" + command.Expression + "», прочитано «"
                + (expressionAfter ?? "не прочитано") + "», значение " + Num(valueAfter)
                + ", перестройка " + BoolText(rebuildSucceeded));
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "Привязка параметра НЕ подтверждена: "
                + (foundAfter
                    ? "выражение прочиталось как «" + (expressionAfter ?? "не прочитано") + "»"
                    : "параметр не найден после записи")
                + (valueAfter is null ? ", значение не вычислилось" : string.Empty)
                + (rebuildSucceeded ? string.Empty : ", перестроение документа не удалось")
                + ". Возможная причина - ссылка на несуществующую переменную: ядро не принимает или не "
                + "вычисляет такое выражение. Состояние модели названо полем revision_after.",
                RetryPolicy.ReacquireContext,
                partialEffects: true,
                details: new Dictionary<string, object?>
                {
                    ["parameter_name"] = command.ParameterName,
                    ["requested_expression"] = command.Expression,
                    ["expression_read_back"] = expressionAfter,
                    ["value_after"] = valueAfter,
                    ["rebuild_succeeded"] = rebuildSucceeded,
                    ["revision_after"] = document.Revision,
                });
        }

        // The declared expectation is a geometry check of a change that HAPPENED: a mismatch is a failed
        // check and a warning, never a refusal (docs/decisions/adapter-core.md#declared-expectation-rule).
        var declared = DeclaredExpectation.Evaluate(
            command.ExpectedVolumeMm3, volumeAfter, () => ReadModelState(document).VolumeMm3);
        bool? expectedMatched = declared.Verdict switch
        {
            DeclaredExpectationVerdict.NotDeclared => null,
            DeclaredExpectationVerdict.Confirmed => true,
            DeclaredExpectationVerdict.NotConfirmed => false,
            _ => null,
        };

        var unverified = new List<string>
        {
            "single_body_volume - измерен СУММАРНЫЙ объём всех тел модели; объём отдельного тела здесь "
            + "не измеряется",
        };
        if (declared.IsUnverifiable)
        {
            unverified.Insert(0, DeclaredExpectation.UnverifiableReason("объём модели после привязки", declared));
        }
        else if (declared.IsNotConfirmed)
        {
            unverified.Insert(0, DeclaredExpectation.NotConfirmedReason("объём модели после привязки", declared));
        }

        BumpRevision(document, "feat.bind_parameter");

        var checks = new List<NamedCheck>
        {
            new("expression_read_back", expressionMatched,
                Observed: expressionAfter, Expected: command.Expression),
            new("value_computed", valueAfter is not null, Observed: Num(valueAfter)),
            new("rebuild_succeeded", rebuildSucceeded, Observed: rebuildResult?.ToString()),
            DeclaredExpectation.Check("volume_after_bind", declared),
        };

        return new BindParameterResult(
            command.FeatureRef,
            command.ParameterName,
            parameterNote,
            command.Expression,
            expressionAfter,
            valueAfter,
            rebuildSucceeded,
            volumeBefore,
            volumeAfter,
            volumeDelta,
            command.ExpectedVolumeMm3,
            expectedMatched,
            revisionBefore,
            document.Revision,
            new VerificationDto(
                DeclaredExpectation.CapLevel(VerificationLevel.GeometryChecked, declared),
                checks,
                unverified),
            notes,
            DeclaredExpectation.Warnings(declared));
    }

    /// <summary>The API5 <c>ksFeature</c> of a model object, or null with a named reason.</summary>
    /// <remarks>DOC: <c>ksentity_getfeature.html</c> - <c>GetFeature()</c> returns «объект дерева, связанный
    /// с данным объектом». A failure to obtain it is reported, never treated as "no parameters".</remarks>
    private static ksFeature? GetFeatureOf(ksEntity entity, List<string> notes)
    {
        try
        {
            return entity.GetFeature() as ksFeature;
        }
        catch (COMException ex)
        {
            notes.Add("get_feature_failed - ksEntity.GetFeature() прервался: " + ex.Message);
            return null;
        }
    }

    /// <summary>The variable array of a feature through the documented <c>ksFeature.VariableCollection</c>.
    /// </summary>
    /// <remarks>DOC: <c>ksfeature_variablecollection.html</c> - <c>VariableCollection()</c> returns «указатель
    /// на интерфейс ksVariableCollection или IVariableCollection»; note: changes do not reach the model until
    /// <c>ksPart::RebuildModel</c>.</remarks>
    private static ksVariableCollection? FeatureVariableCollection(ksFeature feature, List<string> notes)
    {
        try
        {
            return feature.VariableCollection as ksVariableCollection;
        }
        catch (COMException ex)
        {
            notes.Add("variable_collection_failed - ksFeature.VariableCollection прервался: " + ex.Message);
            return null;
        }
    }

    /// <summary>Enumerate a variable collection as a list of possibly-unread handles; null when the COUNT
    /// itself was not read (so the caller can tell "empty" from "not read").</summary>
    private static List<ksVariable?>? EnumerateVariables(ksVariableCollection collection, List<string> notes)
    {
        int count;
        try
        {
            count = collection.GetCount();
        }
        catch (COMException ex)
        {
            notes.Add("GetCount_failed - ksVariableCollection.GetCount() прервался: " + ex.Message);
            return null;
        }

        var items = new List<ksVariable?>(Math.Max(0, count));
        for (var i = 0; i < Math.Max(0, count); i++)
        {
            try
            {
                var item = collection.GetByIndex(i) as ksVariable;
                if (item is null)
                {
                    notes.Add($"GetByIndex({i}) не вернул ksVariable");
                }

                items.Add(item);
            }
            catch (COMException ex)
            {
                notes.Add($"GetByIndex({i}) прервался: {ex.Message}");
                items.Add(null);
            }
        }

        return items;
    }

    /// <summary>Read a boolean field, keeping "not read" apart from "false".</summary>
    private static (VariableFieldState State, bool? Value) SafeBoolField(
        Func<bool> read, List<string> notes, string field)
    {
        try
        {
            return (VariableFieldState.Read, read());
        }
        catch (COMException ex)
        {
            notes.Add(field + "_not_read - " + ex.Message);
            return (VariableFieldState.NotRead, null);
        }
    }

    private static string BoolText(bool? value) => value is null ? "не прочитано" : value.Value.ToString();
}
