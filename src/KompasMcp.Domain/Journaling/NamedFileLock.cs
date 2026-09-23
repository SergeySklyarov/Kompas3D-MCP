using System.Security.Cryptography;
using System.Text;

namespace KompasMcp.Domain.Journaling;

/// <summary>
/// Именованная межпроцессная блокировка, привязанная к ПУТИ ФАЙЛА.
/// </summary>
/// <remarks>
/// <para>
/// ЗАЧЕМ, ЕСЛИ ЗАПИСЬ ИДЁТ ОДНИМ ВЫЗОВОМ. Измерено 21.09.2026 пробой
/// <c>scratch/_append_probe</c> на двух процессах по 200 записей: <c>FileMode.Append</c> с одной
/// записью байтов НЕ атомарен — <b>381 запись из 400</b>. Потерянные записи не рвут строки: каждая
/// из них цела, но 19 из них перезаписаны, потому что дескриптор запоминает позицию конца файла в
/// момент ОТКРЫТИЯ. Один вызов <c>Write</c> без блокировки проблему не решает — <c>openhandle</c>
/// дал 1 запись из 400. С блокировкой: 400/400, и то же на 3 и 4 процессах (6 прогонов, 0 потерь).
/// </para>
/// <para>
/// ЦЕНА МОЛЧАНИЯ ЗДЕСЬ НЕСИММЕТРИЧНА. Журнал операций — механизм безопасности: потерянная запись
/// означает, что повтор после перезапуска выглядит как «не выполнялось», то есть мутация может быть
/// применена ВТОРОЙ раз. Поэтому запись журнала идёт под блокировкой, а не «почти всегда целой».
/// </para>
/// <para>
/// ПОЧЕМУ ИМЯ, А НЕ БЛОКИРОВКА ФАЙЛА. Блокировка файла на запись запретила бы читать журнал
/// работающему серверу и прибору — именно этот дефект и разбирается нарядом. Именованная блокировка
/// сериализует ЗАПИСЬ, оставляя ЧТЕНИЕ свободным.
/// </para>
/// </remarks>
public sealed class NamedFileLock : IDisposable
{
    private readonly Mutex _mutex;
    private bool _held;

    private NamedFileLock(Mutex mutex) => _mutex = mutex;

    /// <summary>
    /// Блокировка для файла. Имя выводится из ПОЛНОГО пути (регистр не важен: Windows), поэтому два
    /// процесса с одним файлом получают одну блокировку, а с разными — разные.
    /// </summary>
    public static NamedFileLock For(string filePath, string purpose)
    {
        var normalized = Path.GetFullPath(filePath).ToLowerInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))[..16];
        return new NamedFileLock(new Mutex(initiallyOwned: false, name: $"Local\\kompas-mcp-{purpose}-{hash}"));
    }

    /// <summary>
    /// Взять блокировку. Заброшенная блокировка (владелец убит) считается взятой: иначе один
    /// упавший процесс запретил бы писать журнал навсегда.
    /// </summary>
    public bool Enter(TimeSpan timeout)
    {
        try
        {
            _held = _mutex.WaitOne(timeout);
        }
        catch (AbandonedMutexException)
        {
            _held = true;
        }

        return _held;
    }

    /// <summary>Отпустить блокировку, если она наша. Молчание здесь — не признак успеха: не наша блокировка не отпускается.</summary>
    public void Exit()
    {
        if (!_held)
        {
            return;
        }

        _held = false;
        try
        {
            _mutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // Блокировка принадлежит другому потоку — отпускать её нельзя и не нужно.
        }
    }

    public void Dispose()
    {
        Exit();
        _mutex.Dispose();
    }
}
