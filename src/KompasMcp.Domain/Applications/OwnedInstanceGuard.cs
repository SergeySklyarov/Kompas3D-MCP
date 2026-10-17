using KompasMcp.Contracts;

namespace KompasMcp.Domain.Applications;

/// <summary>Which KOMPAS instance a job object may guard, and how a refusal to guard it is worded.</summary>
/// <remarks>INVARIANT: only an instance this server launched is guarded. An attached instance belongs to
/// the user, so putting it into a job whose last handle kills its members would turn our own exit into
/// the death of somebody else's document.
/// INVARIANT: a refusal is NAMED with the Win32 step and code, never swallowed: the session keeps
/// working exactly as before, and the client is told that a hard kill may leave the instance alive.
/// History: docs/decisions/host.md#client-shutdown</remarks>
public static class OwnedInstanceGuard
{
    /// <summary>Whether this session's instance goes into the kill-on-close job.</summary>
    public static bool ShouldGuard(ApplicationOwnership ownership) =>
        ownership == ApplicationOwnership.Launched;

    /// <summary>The named refusal: which Win32 step failed, with which code, and what it costs.</summary>
    public static string FailureText(string stage, int win32Error) =>
        $"собственный экземпляр КОМПАС не взят под объект задания: шаг {stage} отказал с кодом Win32 " +
        $"{win32Error}. Сеанс работает как раньше, но жёсткое снятие сервера (снятие дерева процессов " +
        "Хоста) может оставить экземпляр живым и без спасательной копии.";
}
