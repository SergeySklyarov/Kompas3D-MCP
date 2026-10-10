using KompasMcp.Contracts;

namespace KompasMcp.Domain.Applications;

/// <summary>A NAMED refusal of <c>attach</c> when no KOMPAS instance is available: the code, the message
/// and a machine-readable reason.</summary>
public sealed record AttachRefusal(string Code, string ReasonCode, string Message);

/// <summary>Why <c>attach</c> found no KOMPAS instance — a PURE function of the observed counts.</summary>
/// <remarks>INVARIANT: "no KOMPAS entry in the ROT" is NOT ambiguity. <c>AMBIGUOUS_APPLICATION</c> is
/// reserved for the case where KOMPAS instances really are more than one; zero instances is "not found",
/// and saying "ambiguous" sent the caller looking for a second instance that was never there.
/// INVARIANT: "the process is not running" and "the process runs but is not registered in the ROT" are
/// different diagnoses with different remedies, and both are named.
/// History: docs/decisions/adapter-core.md#attach-diagnosis</remarks>
public static class AttachDiagnosis
{
    /// <summary>The KOMPAS process is not running at all.</summary>
    public const string ProcessNotRunning = "kompas_process_not_running";

    /// <summary>KOMPAS runs, but no process is registered in the ROT under the KOMPAS ProgID.</summary>
    public const string ProcessNotInRot = "kompas_process_not_in_rot";

    /// <summary>The ROT enumeration returned nothing at all.</summary>
    public const string RotEmpty = "rot_empty";

    /// <summary>The code carried by this refusal — the "application unavailable" code, NOT
    /// <see cref="ErrorCodes.AmbiguousApplication"/>, which stays for more than one instance.</summary>
    public const string Code = ErrorCodes.ApplicationDisconnected;

    /// <summary>Build the refusal for "no attachable KOMPAS instance".</summary>
    /// <param name="kompasProcessCount">How many <c>KOMPAS.exe</c> processes are running.</param>
    /// <param name="rotTotalEntries">How many entries the ROT enumeration returned in total.</param>
    /// <param name="rotKompasEntries">How many of them are scoped to the KOMPAS ProgID (the candidates).</param>
    public static AttachRefusal NoInstance(int kompasProcessCount, int rotTotalEntries, int rotKompasEntries)
    {
        var counts = $"Процессов КОМПАС: {kompasProcessCount}; записей ROT, сопоставимых с КОМПАС: "
            + $"{rotKompasEntries}; всего записей ROT: {rotTotalEntries}.";

        if (rotTotalEntries == 0)
        {
            return new AttachRefusal(Code, RotEmpty,
                "Экземпляр КОМПАС для attach не найден: перечисление ROT не вернуло ни одной записи " +
                "вообще. " + counts + " Attach не выполнен; используйте mode=launch.");
        }

        if (kompasProcessCount == 0)
        {
            return new AttachRefusal(Code, ProcessNotRunning,
                "Экземпляр КОМПАС для attach не найден: процесс КОМПАС не запущен. " + counts +
                " Attach не выполнен; запустите КОМПАС либо используйте mode=launch.");
        }

        return new AttachRefusal(Code, ProcessNotInRot,
            "Экземпляр КОМПАС для attach не найден: процессы КОМПАС запущены, но ни один не " +
            "зарегистрирован в ROT под ProgID КОМПАС. " + counts + " Типичные причины — другой " +
            "пользователь или недостаточный уровень прав. Attach не выполнен; используйте mode=launch.");
    }
}
