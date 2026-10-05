using System.Runtime.InteropServices;

namespace KompasMcp.Domain.Com;

/// <summary>Result of reading a value with an EXPLICIT distinction between "read" and "not read".</summary>
/// <remarks>MEASURED: an unread component fixing was indistinguishable from an honestly read "not fixed".
/// A read that fails must report "not read", never a substituted <c>default</c> (an unread <c>bool</c> as
/// <c>false</c>, an unread <c>int</c> as 0, an unread enum as its first value).</remarks>
public readonly record struct ReadResult<T>(bool Ok, T Value);

/// <summary>Safe reading of a COM value: an exception yields "NOT READ", not a substituted value. Lives in
/// Domain (no KOMPAS COM types), so a unit test covers it.</summary>
public static class SafeRead
{
    /// <summary>Exceptions under which a read counts as failed, not as "the default value".</summary>
    private static bool Recoverable(Exception ex) =>
        ex is COMException or InvalidCastException or InvalidOperationException;

    public static ReadResult<T> TryRead<T>(Func<T> read)
    {
        try
        {
            return new ReadResult<T>(true, read());
        }
        catch (Exception ex) when (Recoverable(ex))
        {
            return new ReadResult<T>(false, default!);
        }
    }

    /// <summary>Read a REFERENCE value. <c>null</c> means both "read as null" and "not read" — acceptable for
    /// references, because null is a legitimate "nothing to address". Where the difference matters, use
    /// <see cref="TryRead{T}"/> and check <see cref="ReadResult{T}.Ok"/>.</summary>
    public static T? Ref<T>(Func<T?> read) where T : class =>
        TryRead(read) is { Ok: true } result ? result.Value : null;

    public static string? Text(Func<string?> read) => Ref(read);

    /// <summary>Read bool: <c>null</c> — NOT READ, <c>false</c> — read as false.</summary>
    public static bool? Bool(Func<bool> read) => TryRead(read) is { Ok: true } result ? result.Value : null;

    /// <summary>Read an already-nullable bool (e.g. <c>obj?.Valid</c>): <c>null</c> — not read.</summary>
    public static bool? Bool(Func<bool?> read) => TryRead(read) is { Ok: true } result ? result.Value : null;

    public static int? Int(Func<int> read) => TryRead(read) is { Ok: true } result ? result.Value : null;

    public static long? Long(Func<long> read) => TryRead(read) is { Ok: true } result ? result.Value : null;

    public static double? Double(Func<double> read) =>
        TryRead(read) is { Ok: true } result && double.IsFinite(result.Value) ? result.Value : null;

    /// <summary>The name of an enum value; "not read" is printed AS A WORD, not as the first enum value.</summary>
    public static string EnumName<T>(Func<T> read) where T : struct, Enum =>
        TryRead(read) is { Ok: true } result ? result.Value.ToString() : "unread";

    public static T? EnumOrNull<T>(Func<T?> read) where T : struct, Enum =>
        TryRead(read) is { Ok: true } result ? result.Value : null;
}
