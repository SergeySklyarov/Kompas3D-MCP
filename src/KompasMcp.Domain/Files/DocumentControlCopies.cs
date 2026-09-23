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
/// <b>Копия кладётся РЯДОМ С ДОКУМЕНТОМ</b>, в подпапку <c>control-copies</c>. Так её место
/// определено самим документом, а не настройкой, о которой можно забыть: документы прогона лежат в
/// папке прогона, и копии оказываются там же, где доказательства. Настройка каталога копий была бы
/// ещё одним умолчанием, которое расходится с поставкой, — а такие умолчания в этом проекте уже
/// расходились шесть раз.
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
/// </remarks>
public sealed class DocumentControlCopies
{
    /// <summary>Имя подпапки рядом с документом. Одно на все копии прогона.</summary>
    public const string FolderName = "control-copies";

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
            var folder = FolderFor(documentPath);
            Directory.CreateDirectory(folder);
            var name = $"{documentId}-r{revision}-{Path.GetFileName(documentPath)}";
            var target = Path.Combine(folder, name);
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

    /// <summary>Подпапка копий рядом с документом.</summary>
    public static string FolderFor(string documentPath)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(documentPath));
        return Path.Combine(string.IsNullOrEmpty(folder) ? "." : folder, FolderName);
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
