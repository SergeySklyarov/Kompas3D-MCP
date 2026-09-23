using System.Runtime.InteropServices;
using Kompas6API5;

namespace KompasMcp.Api5Adapter;

/// <summary>
/// Видимость экземпляра КОМПАС и его документов.
/// </summary>
/// <remarks>
/// Файл появился после измеренного дефекта: <c>kompas_connect(mode=launch, make_visible=true)</c>
/// отвечал <c>visible=true</c>, а окно оставалось скрытым. Причин было три, и все они про
/// подмену проверки, а не про COM:
/// <list type="number">
/// <item><c>MakeVisible</c> доходил из контракта до адаптера и нигде не применялся;</item>
/// <item>поле <c>Visible</c> в ответе вычислялось как <c>ProcessIdOf(Application) is not null</c>,
/// то есть как «по HWND удаётся достать PID». Скрытое окно HWND имеет, поэтому проверка
/// была ложноположительной по построению;</item>
/// <item>создание и открытие документов жёстко просили невидимый режим
/// (<c>Create(true,…)</c>, <c>Open(path, true)</c>), так что показ приложения сам по себе
/// ничего не делал бы видимым.</item>
/// </list>
///
/// Правило, которое здесь действует: о показе сообщается только по наблюдению, а не по факту
/// вызова. Значит видимость проверяется двумя независимыми способами — свойством COM
/// (<c>KompasObject.Visible</c>) и Windows (<c>IsWindowVisible</c> по главному окну), — а режим
/// документа перечитывается у самого документа (<c>ksDocument3D.invisibleMode</c>), а не
/// выводится из того, что мы попросили.
/// </remarks>
public sealed partial class Api5Session
{
    /// <summary>
    /// Наблюдённое состояние окна приложения: что говорит COM, что говорит Windows, и досталось ли
    /// вообще окно. Поля разделены намеренно — сведение их в одно «visible» и породило дефект.
    /// </summary>
    public sealed record WindowObservation(
        bool? ComProperty,
        long WindowHandle,
        bool? WindowVisible,
        IReadOnlyList<string> VisibleChildTitles,
        string? Error)
    {
        /// <summary>Видимо тогда и только тогда, когда оба независимых наблюдения говорят «да».</summary>
        public bool Visible => ComProperty == true && WindowVisible == true;

        public bool Observed => ComProperty is not null || WindowVisible is not null;
    }

    /// <summary>Опрашивает приложение, не полагаясь ни на одно из подтверждений косвенно.</summary>
    public static WindowObservation ObserveApplicationWindow(KompasObject application)
    {
        bool? com = null;
        long handle = 0;
        bool? win32 = null;
        var titles = new List<string>();
        string? error = null;

        try
        {
            com = application.Visible;
        }
        catch (Exception ex)
        {
            error = "Visible: " + ex.GetType().Name;
        }

        try
        {
            handle = application.ksGetHWindow();
        }
        catch (Exception ex)
        {
            error ??= "ksGetHWindow: " + ex.GetType().Name;
        }

        if (handle != 0)
        {
            var hwnd = new IntPtr(handle);
            try
            {
                // Отдельно и независимо от COM-свойства: именно эта пара не даёт «PID есть»
                // выдать за «окно видно».
                win32 = NativeMethods.IsWindowVisible(hwnd);
                titles = NativeMethods.VisibleChildWindowTitles(hwnd) ?? new List<string>();
            }
            catch (Exception ex)
            {
                error ??= "IsWindowVisible: " + ex.GetType().Name;
            }
        }

        return new WindowObservation(com, handle, win32, titles, error);
    }

    /// <summary>
    /// Просит показать или скрыть экземпляр и проверяет, что получилось. Ничего не предполагает
    /// о результате: возвращает наблюдение после попытки, а не признак «мы вызвали set».
    /// </summary>
    /// <remarks>
    /// После показа окно может ещё не успеть перестроиться, поэтому наблюдение берётся с
    /// небольшим ожиданием: без него первый же запуск давал бы visible=false на корректном
    /// действии (состояние гонки вместо дефекта).
    /// </remarks>
    private static WindowObservation ApplyApplicationVisibility(KompasObject application, bool wantVisible)
    {
        try
        {
            application.Visible = wantVisible;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            // Отказ показа не отменяет сеанс: пользователь получит фактическое состояние и код
            // ошибки в ответе, а не «успех» с другим смыслом.
            return new WindowObservation(null, 0, null, Array.Empty<string>(),
                "set_Visible: " + ex.GetType().Name + ": " + ex.Message);
        }

        for (var attempt = 0; attempt < 20; attempt++)
        {
            var observed = ObserveApplicationWindow(application);
            if (observed.Visible == wantVisible)
            {
                return observed;
            }

            Thread.Sleep(100);
        }

        return ObserveApplicationWindow(application);
    }

    /// <summary>
    /// Режим видимости документа: то, что документ действительно сообщает о себе.
    /// </summary>
    /// <remarks>
    /// <c>ksDocument3D.invisibleMode</c> — только чтение; это единственный доступный способ
    /// спросить сам документ, а не вспомнить, что мы просили при Create/Open.
    /// </remarks>
    private static bool? ObserveDocumentVisible(ksDocument3D document)
    {
        try
        {
            return !document.invisibleMode;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Делает документ активным и, только если он видим, обновляет изображение.
    /// </summary>
    /// <remarks>
    /// Возвращает коды наблюдений, а не «успех»: <c>SetActive()</c> отвечает Boolean, а соглашение
    /// о возвращаемом значении <c>ksRefreshActiveWindow()</c> (Int32) не калибровано, поэтому он
    /// записывается как есть и не участвует в решении о том, показан документ или нет.
    /// Камера не трогается намеренно: <c>ZoomPrevNextOrAll</c> в этом коде не вызывается ни при
    /// каких условиях (см. Api5SessionVisibilityGuardTests), потому что сброс вида после каждой
    /// операции — это ровно то, что запрещено требованием.
    /// </remarks>
    private static (bool? Activated, object? Refresh) PresentDocument(
        KompasObject application, ksDocument3D document, bool documentVisible)
    {
        bool? activated = null;
        try
        {
            activated = document.SetActive();
        }
        catch (Exception)
        {
            activated = null;
        }

        if (!documentVisible)
        {
            return (activated, null);
        }

        object? refresh;
        try
        {
            refresh = application.ksRefreshActiveWindow();
        }
        catch (Exception ex)
        {
            refresh = ex.GetType().Name;
        }

        return (activated, refresh);
    }

    /// <summary>
    /// Обновление вида после мутации над видимым документом. Вызывается из общего потока
    /// <see cref="BumpRevision"/>, поэтому не зависит от того, какая именно операция меняла модель.
    /// </summary>
    private void RefreshViewAfterMutation(DocumentEntry document)
    {
        if (!document.DocumentsVisible)
        {
            // Скрытый режим остаётся ровно как был: никакого оконного трафика. Это и регресс-защита
            // (число вызовов в скрытом режиме не меняется), и смысл требования «скрытый режим
            // сохраняется и проверяется отдельно».
            return;
        }

        if (!_applications.TryGetValue(document.ApplicationId, out var application))
        {
            return;
        }

        try
        {
            application.Application.ksRefreshActiveWindow();
        }
        catch (Exception)
        {
            // Вид — не результат модели: неудача перерисовки не должна превращать успешную
            // геометрическую операцию в ошибку.
        }
    }
}
