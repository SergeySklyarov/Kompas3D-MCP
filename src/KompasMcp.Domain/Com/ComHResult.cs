using System.Runtime.InteropServices;

namespace KompasMcp.Domain.Com;

/// <summary>The COM HRESULTs this project reasons about, written as the published literals rather than
/// remembered decimals. Anything not listed here is reported verbatim and treated as an
/// unknown outcome — an unrecognised HRESULT must never be laundered into a retryable error.</summary>
/// <remarks>WHY IT LIVES IN DOMAIN. It is pure classification over an <c>int</c> and over two
/// <c>System.Runtime.InteropServices</c> exception types — no KOMPAS type crosses it — so it belongs where
/// a unit test can execute it, next to <see cref="SafeRead"/>. A classification that can only be read as
/// source is a claim about text, not a measured property.
/// History: docs/decisions/adapter-core.md#disconnect-classification</remarks>
public static class ComHResult
{
    public const int S_OK = 0;

    /// <summary>Call denied because the callee is still parsing input; retry is legitimate.</summary>
    public const int RPC_E_CALL_REJECTED = unchecked((int)0x80010001);

    /// <summary>Server rejected the call for retry — the canonical "come back later".</summary>
    public const int RPC_E_SERVERCALL_RETRYLATER = unchecked((int)0x8001010A);

    /// <summary>Call rejected by the message filter.</summary>
    public const int RPC_E_SERVERCALL_FILTERING = unchecked((int)0x8001010B);

    /// <summary>Out-of-process server too busy to accept the call.</summary>
    public const int RPC_E_SERVERBUSY = unchecked((int)0x800101DD);

    /// <summary>The object has disconnected: the reference is dead and the call outcome is unknowable.</summary>
    public const int RPC_E_DISCONNECTED = unchecked((int)0x80010108);

    /// <summary>The server process died. DOC: winerror.h <c>RPC_E_SERVER_DIED</c> = 0x80010007.</summary>
    /// <remarks>The value is the published one, not a remembered decimal. A former constant here carried the
    /// name <c>RPC_E_SERVERDIED</c> with the value 0x80010110, which winerror.h defines as
    /// <c>RPC_E_VERSION_MISMATCH</c>: the classifier recognised an RPC version clash and never recognised a
    /// dead server. MEASURED against the local Windows SDK header.
    /// History: docs/decisions/adapter-core.md#disconnect-classification</remarks>
    public const int RPC_E_SERVER_DIED = unchecked((int)0x80010007);

    /// <summary>The server died and the call did not execute. DOC: <c>RPC_E_SERVER_DIED_DNE</c>.</summary>
    public const int RPC_E_SERVER_DIED_DNE = unchecked((int)0x80010012);

    /// <summary>The RPC server is unavailable — the client-side error a killed local server raises.
    /// DOC: winerror.h <c>RPC_S_SERVER_UNAVAILABLE</c> = 1722L, so the HRESULT is 0x800706BA.</summary>
    public const int RPC_S_SERVER_UNAVAILABLE = unchecked((int)0x800706BA);

    /// <summary>The remote procedure call failed. DOC: winerror.h <c>RPC_S_CALL_FAILED</c> = 1726L.</summary>
    public const int RPC_S_CALL_FAILED = unchecked((int)0x800706BE);

    /// <summary>Server process could not be started. LIMIT: deliberately NOT part of
    /// <see cref="IsDisconnected"/>. It describes STARTING a server, not losing a live one, and this
    /// project classifies it as <c>LICENSE_UNAVAILABLE</c>; folding it in would silently reclassify a
    /// refused launch. History: docs/decisions/adapter-core.md#disconnect-classification</summary>
    public const int CO_E_SERVER_EXEC_FAILURE = unchecked((int)0x80080005);

    /// <summary>ProgID could not be parsed.</summary>
    public const int CO_E_CLASSSTRING = unchecked((int)0x800401F3);

    /// <summary>Class not registered.</summary>
    public const int REGDB_E_CLASSNOTREG = unchecked((int)0x80040154);

    /// <summary>Late-bound member not found — normally an interface/version mismatch.</summary>
    public const int DISP_E_MEMBERNOTFOUND = unchecked((int)0x80020003);

    public static bool IsBusy(int code) =>
        code is RPC_E_CALL_REJECTED
            or RPC_E_SERVERCALL_RETRYLATER
            or RPC_E_SERVERCALL_FILTERING
            or RPC_E_SERVERBUSY;

    /// <summary>Whether the code means "a live server can no longer be reached".</summary>
    /// <remarks>INVARIANT: the set is exactly the codes that describe a LOST CONNECTION, so a caller may
    /// treat them as "the reference is dead and the call outcome is unknowable". A code outside the set is
    /// reported verbatim; widening this set reclassifies refusals elsewhere (a launch failure would stop
    /// being <c>LICENSE_UNAVAILABLE</c>), which is why <c>CO_E_SERVER_EXEC_FAILURE</c> is excluded.
    /// History: docs/decisions/adapter-core.md#disconnect-classification</remarks>
    public static bool IsDisconnected(int code) =>
        code is RPC_E_DISCONNECTED
            or RPC_E_SERVER_DIED
            or RPC_E_SERVER_DIED_DNE
            or RPC_S_SERVER_UNAVAILABLE
            or RPC_S_CALL_FAILED;

    /// <summary>Whether the exception carries a connection loss. An RCW already separated from its
    /// underlying object carries no COM HRESULT at all and is a lost connection too.</summary>
    public static bool IsDisconnected(Exception ex) =>
        ex is InvalidComObjectException || (From(ex) is int code && IsDisconnected(code));

    public static bool IsRegistrationFailure(int code) =>
        code is CO_E_CLASSSTRING or REGDB_E_CLASSNOTREG;

    public static string Name(int code) => code switch
    {
        RPC_E_CALL_REJECTED => "RPC_E_CALL_REJECTED",
        RPC_E_SERVERCALL_RETRYLATER => "RPC_E_SERVERCALL_RETRYLATER",
        RPC_E_SERVERCALL_FILTERING => "RPC_E_SERVERCALL_FILTERING",
        RPC_E_SERVERBUSY => "RPC_E_SERVERBUSY",
        RPC_E_DISCONNECTED => "RPC_E_DISCONNECTED",
        RPC_E_SERVER_DIED => "RPC_E_SERVER_DIED",
        RPC_E_SERVER_DIED_DNE => "RPC_E_SERVER_DIED_DNE",
        RPC_S_SERVER_UNAVAILABLE => "RPC_S_SERVER_UNAVAILABLE",
        RPC_S_CALL_FAILED => "RPC_S_CALL_FAILED",
        CO_E_SERVER_EXEC_FAILURE => "CO_E_SERVER_EXEC_FAILURE",
        CO_E_CLASSSTRING => "CO_E_CLASSSTRING",
        REGDB_E_CLASSNOTREG => "REGDB_E_CLASSNOTREG",
        DISP_E_MEMBERNOTFOUND => "DISP_E_MEMBERNOTFOUND",
        _ => $"0x{code:X8}",
    };

    /// <summary>Extract the HRESULT from an exception, or null when it carries none.</summary>
    public static int? From(Exception ex) => ex switch
    {
        COMException com => com.HResult,
        InvalidComObjectException => unchecked((int)0x80131501),
        _ => null,
    };
}
