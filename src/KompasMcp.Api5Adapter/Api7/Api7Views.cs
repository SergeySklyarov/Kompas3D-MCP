using KompasAPI7;

namespace KompasMcp.Api5Adapter.Api7;

/// <summary>The views of a graphic document, taken from the interface the HELP declares them on.</summary>
/// <remarks>DOC: <c>ikompasdocument2d_viewsandlayersmanager.html</c> — «ViewsAndLayersManager -
/// Возвращает менеджер видов и слоев документа», a read-only property of <c>IKompasDocument2D</c>.
/// <c>ikompasdocument2d.html</c> — «Данный интерфейс является базовым для интерфейсов графических
/// документов КОМПАС API - чертежа и фрагмента» и «Интерфейс может быть получен с помощью метода
/// IUnknown QueryInterface от ... интерфейса фрагмента IFragmentDocument», so ONE route serves both
/// kinds. INVARIANT: the property is read from the interface that documents it, never from the one the
/// shipped wrapper happens to re-declare it on. History: docs/decisions/drawings.md#views-route</remarks>
internal static class Api7Views
{
    /// <summary>The view collection of a drawing or a fragment; <c>null</c> when the object answers
    /// neither, so the caller names its own refusal.</summary>
    public static IViews? Of(object? document)
    {
        if (document is not IKompasDocument2D document2D)
        {
            return null;
        }

        return (document2D.ViewsAndLayersManager as IViewsAndLayersManager)?.Views as IViews;
    }
}
