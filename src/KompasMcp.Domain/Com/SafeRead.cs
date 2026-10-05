using System.Runtime.InteropServices;

namespace KompasMcp.Domain.Com;

/// <summary>
/// Результат чтения значения с ЯВНЫМ различением «прочитано» и «не прочитано».
/// </summary>
/// <remarks>
/// Заведён потому, что прежний помощник возвращал <c>default</c>. Для значимых типов это подставляло
/// ИЗВЕСТНОЕ значение вместо НЕИЗВЕСТНОГО: непрочитанный <c>bool</c> становился <c>false</c> и позже
/// упаковывался в <c>bool?</c> как «прочитано false», непрочитанный <c>int</c> — нулём, непрочитанный
/// enum — первым своим значением. Измерено 05.10.2026: непрочитанная фиксация компонента была
/// неотличима от честно прочитанного «не зафиксирован».
/// </remarks>
public readonly record struct ReadResult<T>(bool Ok, T Value);

/// <summary>
/// Безопасное чтение COM-значения: исключение чтения даёт «НЕ ПРОЧИТАНО», а не подставленное
/// значение. Живёт в Domain (без COM-типов КОМПАСа), поэтому проверяется модульным тестом.
/// </summary>
public static class SafeRead
{
    /// <summary>Исключения, при которых чтение считается несостоявшимся, а не «значение по умолчанию».</summary>
    private static bool Recoverable(Exception ex) =>
        ex is COMException or InvalidCastException or InvalidOperationException;

    /// <summary>Прочитать значение, сохранив признак состоявшегося чтения.</summary>
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

    /// <summary>
    /// Чтение ССЫЛОЧНОГО значения. <c>null</c> означает и «прочитано null», и «не прочитано» — для
    /// ссылок это приемлемо, потому что null и есть законное «нечего адресовать». Где различие важно,
    /// читающий берёт <see cref="TryRead{T}"/> и проверяет <see cref="ReadResult{T}.Ok"/>.
    /// </summary>
    public static T? Ref<T>(Func<T?> read) where T : class =>
        TryRead(read) is { Ok: true } result ? result.Value : null;

    public static string? Text(Func<string?> read) => Ref(read);

    /// <summary>Чтение bool: <c>null</c> — НЕ ПРОЧИТАНО, <c>false</c> — прочитано как false.</summary>
    public static bool? Bool(Func<bool> read) => TryRead(read) is { Ok: true } result ? result.Value : null;

    /// <summary>Чтение уже-необязательного bool (напр. <c>obj?.Valid</c>): <c>null</c> — не прочитано.</summary>
    public static bool? Bool(Func<bool?> read) => TryRead(read) is { Ok: true } result ? result.Value : null;

    public static int? Int(Func<int> read) => TryRead(read) is { Ok: true } result ? result.Value : null;

    public static long? Long(Func<long> read) => TryRead(read) is { Ok: true } result ? result.Value : null;

    public static double? Double(Func<double> read) =>
        TryRead(read) is { Ok: true } result && double.IsFinite(result.Value) ? result.Value : null;

    /// <summary>Имя значения перечисления; «не прочитано» печатается СЛОВОМ, а не первым значением.</summary>
    public static string EnumName<T>(Func<T> read) where T : struct, Enum =>
        TryRead(read) is { Ok: true } result ? result.Value.ToString() : "unread";

    /// <summary>Уже-необязательное значение перечисления: <c>null</c> — НЕ прочитано.</summary>
    public static T? EnumOrNull<T>(Func<T?> read) where T : struct, Enum =>
        TryRead(read) is { Ok: true } result ? result.Value : null;
}
