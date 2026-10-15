using System.Runtime.InteropServices;
using KompasAPI7;
using KompasMcp.Contracts;

namespace KompasMcp.Api5Adapter;

/// <summary>The ONE place a refused call's error is read, for every mutation, not just the extrusion.</summary>
/// <remarks>DOC: the library-program code is <c>KompasObject::ksReturnResult</c> /
/// <c>ksStrResult</c> (<c>kompasobject_ksreturnresult.html</c>, <c>kompasobject_ksstrresult.html</c>) -
/// «Код ошибки … при выполнении библиотечной программы»; it is not tied to one method by the help.
/// DOC: the system's own error object is <c>IApplication::KompasError</c> (<c>iapplication_kompaserror.html</c>)
/// → <c>IKompasError</c> with the read-only properties <c>Code</c>, <c>Description</c>, <c>Error3D</c>
/// (<c>ikompaserror.html</c>, <c>ikompaserror_code.html</c>, <c>ikompaserror_description.html</c>,
/// <c>ikompaserror_error3d.html</c>).
/// INVARIANT: the documented fields are published AS THEY ARE READ. The help does not tie
/// <c>IKompasError</c> to a specific call's refusal (it is «информация о последней ошибке системы
/// КОМПАС»), and the measured (Code, Description) pairs do not match either documented table, so no
/// code is decoded into a cause here.
/// INVARIANT: an unreadable route returns a REASON, never a zero — "KOMPAS returned no code" and "we
/// could not read the code" are different facts.
/// History: docs/decisions/adapter-core.md#kompas-error-route</remarks>
public sealed partial class Api5Session
{
    /// <summary>What a refused call answered, from both documented routes, with every unreadable part
    /// named instead of dropped.</summary>
    internal readonly record struct KompasFailure(
        int? ResultCode,
        string? ResultText,
        string? ResultUnavailable,
        int? ErrorCode,
        string? ErrorDescription,
        bool? ErrorIs3D,
        string? ErrorUnavailable);

    /// <summary>Read both documented error routes after a refused call.</summary>
    /// <remarks>ORDER (MEASURED): both routes read the SAME error store, and each text read clears it, so
    /// only one text survives. The table selector and the code are read FIRST, then the library-program
    /// pair, then the system description last — that way no code is lost and the extrusion's own snapshot
    /// fields keep the meaning they already had. The system object is read through the API7 view of the
    /// SAME application; a bridge that does not build is named, not guessed around.
    /// History: docs/decisions/adapter-core.md#kompas-error-route</remarks>
    private KompasFailure ReadKompasFailure(DocumentEntry document)
    {
        var (errorCode, is3D, errorUnavailable) = ReadKompasErrorHead(document);
        var (code, text, unavailable) = ReadKompasResult(document);
        var description = ReadKompasErrorDescription(document);
        return new KompasFailure(code, text, unavailable, errorCode, description, is3D, errorUnavailable);
    }

    /// <summary>The system error's code and table selector, read as-is through
    /// <c>IApplication.KompasError</c> BEFORE the library-program pair.</summary>
    /// <remarks>DOC: <c>ikompaserror_error3d.html</c> - <c>Error3D</c> TRUE means the code belongs to
    /// the 3D table (<c>errortype3d.html</c>), FALSE to the graphical one (<c>errorcodes.html</c>);
    /// the two overlap, so the table is chosen by this flag and by nothing else.
    /// MEASURED: the pairs actually observed match NEITHER table — 28 «Ошибочный параметр»,
    /// 54 «Невозможно выполнить операцию», and 49 «Слишком большая толщина стенки…» reported as a 3D
    /// error although 49 is in neither table — so the flag is published, never used to decode.
    /// History: docs/decisions/adapter-core.md#kompas-error-route</remarks>
    private (int? Code, bool? Is3D, string? Unavailable) ReadKompasErrorHead(DocumentEntry document)
    {
        var (error, unavailable) = KompasErrorOf(document);
        if (error is null)
        {
            return (null, null, unavailable);
        }

        try
        {
            return (error.Code, error.Error3D, null);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return (null, null, "IKompasError не прочитался: " + ex.Message);
        }
    }

    /// <summary>The system error's description, read LAST: reading it clears the error (the help says so
    /// on <c>ikompaserror_description.html</c>), so it must not run before a code that is still wanted.
    /// MEASURED: by this point the library-program text has already been read and the store is empty, so
    /// an empty description here is a reading, not an absence of the route.
    /// History: docs/decisions/adapter-core.md#kompas-error-route</remarks>
    private string? ReadKompasErrorDescription(DocumentEntry document)
    {
        var (error, unavailable) = KompasErrorOf(document);
        if (error is null)
        {
            return null;
        }

        try
        {
            return error.Description;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return null;
        }
    }

    /// <summary>The system error object of the API7 view of this application, with the reason when the
    /// route does not build. INVARIANT: a missing route is NAMED, never silently null.</summary>
    private (IKompasError? Error, string? Unavailable) KompasErrorOf(DocumentEntry document)
    {
        IApplication? application7;
        try
        {
            application7 = BridgeFor(document).Application();
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or KompasContractException)
        {
            return (null, "маршрут API7 не построился: " + ex.Message);
        }

        if (application7 is null)
        {
            return (null, "маршрут API7 не построился: "
                + (BridgeFor(document).BridgeFailure ?? "причина не названа"));
        }

        try
        {
            var error = application7.KompasError;
            return error is null
                ? (null, "IApplication.KompasError вернул null")
                : (error, null);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return (null, "IKompasError не прочитался: " + ex.Message);
        }
    }

    /// <summary>Clear a previous NON-FATAL KOMPAS error before a call whose code will be read.</summary>
    /// <remarks>DOC: KompasObject::ksResultNULL (kompasobject_ksresultnull.html) - «Обнулить результат
    /// работы библиотеки, если ошибка не фатальная»; returns 1 when an error was cleared. Without it a
    /// code left by an earlier call would be read as this refusal's reason.
    /// INVARIANT: a failed reset is not fatal and is not reported as one - the code read afterwards is
    /// then merely less trustworthy, and ksStrResult's note says it clears the non-fatal flag too.
    /// History: docs/decisions/adapter-features.md#create-false-snapshot</remarks>
    private void ClearKompasResult(DocumentEntry document)
    {
        try
        {
            RequireApplication(document.ApplicationId).Application.ksResultNULL();
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or KompasContractException)
        {
            // Nothing to report here: the refusal itself is what the caller must see, and an unreadable
            // reset only weakens the code read next - it does not make the operation fail differently.
        }
    }

    /// <summary>The KOMPAS result code and message of a call that has just returned.</summary>
    /// <remarks>DOC: KompasObject::ksReturnResult and KompasObject::ksStrResult
    /// (help.ascon.ru/KOMPAS_SDK/24/ru-RU/kompasobject_ksreturnresult.html,
    /// .../kompasobject_ksstrresult.html) - "Код ошибки ... при выполнении библиотечной программы",
    /// "Ошибка с номером >0 не является фатальной. Отрицательный номер ошибки приводит к завершению
    /// программы", text via ksStrResult. INVARIANT: an unreadable route returns a REASON, never a zero -
    /// "KOMPAS returned no code" and "we could not read the code" are different facts.
    /// History: docs/decisions/adapter-features.md#create-false-snapshot</remarks>
    private (int? Code, string? Text, string? Unavailable) ReadKompasResult(DocumentEntry document)
    {
        try
        {
            var application = RequireApplication(document.ApplicationId).Application;
            var code = application.ksReturnResult();
            string? text;
            try
            {
                text = application.ksStrResult();
            }
            catch (Exception ex) when (ex is COMException or InvalidCastException)
            {
                text = null;
            }

            return (code, text, null);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or KompasContractException)
        {
            return (null, null, "не прочитано: " + ex.Message);
        }
    }

    /// <summary>The library-program message that goes next to the code, with a zero code handled as what it
    /// is. MEASURED: after a refused <c>SetSketch</c> with <c>ksReturnResult = 0</c>, <c>ksStrResult</c>
    /// still answered «Успешное завершение» — the text of an EARLIER call. Publishing it bare inside a
    /// refusal would read as a success, so a non-empty text under a zero code is named as a leftover
    /// instead of being repeated as this refusal's message.
    /// History: docs/decisions/adapter-sketch.md#set-sketch-refusal</summary>
    private static string ResultTextOf(int? code, string? text)
    {
        if (code == 0)
        {
            return string.IsNullOrWhiteSpace(text)
                ? "КОМПАС кода ошибки не вернул — текста нет"
                : $"код ошибки не назван (ksReturnResult = 0); ksStrResult вернул «{text.Trim()}» — "
                    + "это текст предыдущего вызова, к этому отказу он не относится";
        }

        return string.IsNullOrWhiteSpace(text)
            ? "код ошибки прочитан, строка сообщения пуста"
            : text;
    }

    /// <summary>The refusal's reason line as the client sees it. INVARIANT: "ПРИЧИНА НЕ УСТАНОВЛЕНА" is
    /// kept ONLY for the case where KOMPAS returned no code at all - a named code is the cause, and saying
    /// "not established" next to it would be false.</summary>
    private static string RefusalReason(int? code, string? text, string? unavailable)
    {
        if (code is int named && named != 0)
        {
            var message = string.IsNullOrWhiteSpace(text) ? string.Empty : $" («{text.Trim()}»)";
            return $"КОМПАС вернул код ошибки {named}{message}.";
        }

        if (code == 0)
        {
            return "ПРИЧИНА НЕ УСТАНОВЛЕНА: КОМПАС кода ошибки не вернул (ksReturnResult = 0).";
        }

        return "ПРИЧИНА НЕ УСТАНОВЛЕНА: код ошибки КОМПАС прочитать не удалось ("
            + (unavailable ?? "причина не названа") + ").";
    }

    /// <summary>Write a refused call's error into the refusal's <c>details</c>, under the names the
    /// extrusion already publishes plus the documented <c>IKompasError</c> fields.</summary>
    /// <remarks>INVARIANT: every key is written on every refusal, so a client can rely on the shape; an
    /// unreadable part is a NAMED reason, never a missing key and never a zero.
    /// History: docs/decisions/adapter-core.md#kompas-error-route</remarks>
    private static void AddKompasFailure(IDictionary<string, object?> details, KompasFailure failure)
    {
        details["kompas_result_code"] = failure.ResultCode.HasValue
            ? failure.ResultCode.Value
            : failure.ResultUnavailable ?? "не прочитано: причина не названа";
        details["kompas_result_text"] = ResultTextOf(failure.ResultCode, failure.ResultText);
        AddKompasErrorFields(details, failure);
    }

    /// <summary>A refusal's <c>details</c> with the refused call's error appended. Returns the same
    /// dictionary so a call site can hand it straight to the exception.</summary>
    private Dictionary<string, object?> CodeDetails(
        Dictionary<string, object?> details, DocumentEntry document)
    {
        AddKompasFailure(details, ReadKompasFailure(document));
        return details;
    }

    /// <summary>The documented <c>IKompasError</c> fields alone, for a refusal that already publishes the
    /// library-program code under its own names.</summary>
    private static void AddKompasErrorFields(IDictionary<string, object?> details, KompasFailure failure)
    {
        details["kompas_error_code"] = failure.ErrorCode.HasValue
            ? failure.ErrorCode.Value
            : failure.ErrorUnavailable ?? "не прочитано: причина не названа";
        details["kompas_error_description"] = failure.ErrorDescription is { Length: > 0 } named
            ? named
            : failure.ErrorUnavailable is null
                ? "пусто: чтение ksStrResult уже сбросило тот же сбой (маршруты читают одно хранилище)"
                : "не прочитано: " + failure.ErrorUnavailable;
        details["kompas_error_is_3d"] = failure.ErrorIs3D.HasValue
            ? failure.ErrorIs3D.Value
            : failure.ErrorUnavailable ?? "не прочитано: причина не названа";
        details["kompas_error_note"] =
            "поля IKompasError — последняя ошибка системы КОМПАС, справка не связывает её с этим "
            + "вызовом; значение кода не расшифровывается (см. docs/04 §4.59)";
    }
}
