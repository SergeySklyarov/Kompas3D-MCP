using System.Text;

namespace KompasMcp.Domain.Files;

/// <summary>Итог снятия контрольной копии: путь к копии либо НАЗВАННАЯ причина, почему копии нет.</summary>
/// <remarks>
/// «Копии нет» и «копия снята» — разные состояния, и пустое поле их не различает: причина
/// называется прямо, а не оставляется на догадку вызывающего.
/// </remarks>
public sealed record ControlCopyResult(bool Made, string? Path, string? Reason);

/// <summary>
/// Контрольные копии файла документа: снимаются ПЕРЕД мутацией и возвращаются на место при сбое
/// (<c>dep.foundation</c>, действие <c>negative_tests</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Копия кладётся В СЛУЖЕБНЫЙ КАТАЛОГ, а не рядом с документом.</b> До 05.10.2026 копия
/// создавалась в подпапке <c>control-copies</c> РЯДОМ С ДОКУМЕНТОМ, и это была запись в папку
/// пользователя: документ из корня «только чтение» или из чужой модели получал рядом с собой
/// новый каталог с копией всего файла на каждой мутации, а восстановление ПЕРЕЗАПИСЫВАЛО САМ ФАЙЛ
/// документа. Ни Хост, ни Worker не проверяли, что папка документа лежит в записываемом корне
/// (дефект H4 ревью 05.10.2026). Служебный каталог задаётся вызывающим (тот же разряд, что журнал
/// операций), поэтому запись идёт туда, где серверу писать разрешено, и никогда — в папку модели.
/// </para>
/// <para>
/// <b>Имя копии несёт документ и ревизию.</b> Копия на ревизии 7 и копия на ревизии 8 — разные
/// файлы, и перезапись одной другой скрыла бы именно то, что нужно восстановить.
/// </para>
/// <para>
/// <b>Чего копия НЕ делает.</b> Она не откатывает модель в памяти КОМПАСа: восстановление
/// возвращает ФАЙЛ к состоянию до мутации, а открытый документ продолжает жить своей жизнью.
/// Поэтому вызывающий обязан назвать это прямо, а не выдать восстановление файла за откат модели.
/// </para>
/// <para>
/// <b>Рост каталога ограничен?</b> Нет: копия на каждую мутацию — осознанная цена возможности
/// вернуть файл. Ограничение вводится оператором удалением каталога, а не молчаливой перезаписью
/// одной копии другой, которая превратила бы «вернуть состояние» в «вернуть последнее».
/// </para>
/// </remarks>
public sealed class DocumentControlCopies
{
    /// <summary>Имя подпапки внутри служебного каталога. Одно на все копии прогона.</summary>
    public const string FolderName = "control-copies";

    /// <summary>Служебный каталог, в который разрешено писать копии.</summary>
    public string RootDirectory { get; }

    /// <param name="rootDirectory">
    /// Записываемый служебный каталог (рядом с журналом операций). Обязателен: умолчание «рядом с
    /// документом» и есть тот дефект, который здесь исправлен.
    /// </param>
    public DocumentControlCopies(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        RootDirectory = System.IO.Path.GetFullPath(rootDirectory);
    }

    /// <summary>Каталог копий: служебный корень плюс <see cref="FolderName"/>.</summary>
    public string Folder => System.IO.Path.Combine(RootDirectory, FolderName);

    /// <summary>
    /// Снять копию файла документа перед мутацией. Возвращает копию либо причину, почему её нет;
    /// отсутствие файла на диске — НАЗВАННОЕ состояние, а не ошибка: у только что созданного
    /// документа файла ещё нет, и «копия не снята» здесь честнее пустого пути.
    /// </summary>
    public ControlCopyResult Before(string? documentPath, string documentId, long revision)
    {
        if (string.IsNullOrWhiteSpace(documentPath))
        {
            return new ControlCopyResult(false, null, "документ не имеет файла на диске: копировать нечего");
        }

        if (!File.Exists(documentPath))
        {
            return new ControlCopyResult(false, null,
                $"файла '{documentPath}' на диске нет: копировать нечего (документ ещё не сохранён)");
        }

        try
        {
            var folder = Folder;
            Directory.CreateDirectory(folder);

            // Имя файла документа в имени копии сохраняет читаемость; документ и ревизия в имени —
            // уникальность. Документ в служебный каталог НЕ копируется «рядом с оригиналом»: имя
            // может повториться у двух документов из разных папок, поэтому к нему добавлен
            // documentId.
            var name = $"{documentId}-r{revision}-{System.IO.Path.GetFileName(documentPath)}";
            var target = System.IO.Path.Combine(folder, name);
            File.Copy(documentPath, target, overwrite: true);
            return new ControlCopyResult(true, target, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or NotSupportedException or ArgumentException)
        {
            return new ControlCopyResult(false, null,
                $"копия не снята: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Вернуть файл документа к состоянию до мутации. Возвращает <c>null</c> при успехе либо
    /// названную причину отказа: неудавшееся восстановление обязано быть видно, а не проглочено.
    /// </summary>
    /// <remarks>
    /// Восстановление — ЭТО ЗАПИСЬ В ФАЙЛ ДОКУМЕНТА, поэтому оно делается только когда файл
    /// доступен для записи. Документ из корня «только чтение» не перезаписывается: отказ называется
    /// причиной, а не превращается в попытку записи, которая либо пройдёт в обход политики, либо
    /// упадёт исключением без объяснения.
    /// </remarks>
    public string? Restore(string? documentPath, string? copyPath)
    {
        if (string.IsNullOrWhiteSpace(documentPath) || string.IsNullOrWhiteSpace(copyPath))
        {
            return "восстановление не выполнено: нет пути документа или копии";
        }

        if (!File.Exists(copyPath))
        {
            return $"восстановление не выполнено: копии '{copyPath}' на диске нет";
        }

        if (!CanWrite(documentPath))
        {
            return $"восстановление не выполнено: файл документа '{documentPath}' недоступен для " +
                   "записи (документ вне записываемого корня или защищён) — возвращать файл к " +
                   "состоянию до мутации нельзя";
        }

        try
        {
            File.Copy(copyPath, documentPath, overwrite: true);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or NotSupportedException or ArgumentException)
        {
            return $"восстановление не выполнено: {ex.GetType().Name}: {ex.Message}";
        }
    }

    /// <summary>
    /// Доступен ли файл для записи. Проверяется ОТКРЫТИЕМ на запись, а не признаком «только
    /// чтение»: признак не учитывает ни права каталога, ни политику сервера, а открытие — это ровно
    /// то действие, которое восстановление и собирается выполнить.
    /// </summary>
    private static bool CanWrite(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                // Файла нет — его и восстанавливать некуда: File.Copy создал бы новый файл в
                // каталоге документа, то есть сделал бы запись там, где просили только мутацию.
                return false;
            }

            using var stream = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return stream.CanWrite;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or NotSupportedException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// Описание копии для ответа клиенту. Строка, а не молчание: «копии нет» — тоже утверждение, и
    /// оно должно быть произнесено, иначе отсутствие поля неотличимо от забытой записи.
    /// </summary>
    public static string Describe(ControlCopyResult copy)
    {
        var text = new StringBuilder();
        text.Append(copy.Made ? "снята" : "не снята");
        if (copy.Path is not null)
        {
            text.Append(": ").Append(copy.Path);
        }

        if (copy.Reason is not null)
        {
            text.Append(" (").Append(copy.Reason).Append(')');
        }

        return text.ToString();
    }
}
