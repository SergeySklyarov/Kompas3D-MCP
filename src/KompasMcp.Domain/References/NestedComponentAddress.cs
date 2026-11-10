namespace KompasMcp.Domain.References;

/// <summary>What a nested reference may do with the instance its path resolved to.</summary>
public enum NestedAddressVerdictKind
{
    /// <summary>The path names the instance the reference was minted for, and it is editable.</summary>
    Addressable,

    /// <summary>The path resolved, but not to that instance: the address is out of date.</summary>
    StaleReference,

    /// <summary>The instance is there, but the kernel does not allow it to be edited in this state.</summary>
    CapabilityUnavailable,
}

/// <summary>The decision and the words an answer quotes from it.</summary>
public sealed record NestedAddressVerdict(NestedAddressVerdictKind Kind, string Detail, string? Unconfirmed = null);

/// <summary>Whether a resolved nested instance may be written — a PURE rule over documented properties, no
/// KOMPAS.</summary>
/// <remarks>INVARIANT: an index addresses a POSITION, and a position survives a structure change that the
/// reference does not, so the instance is CONFIRMED by <c>IPart7.Reference</c> before a write: a different
/// number is a different instance and the write would land on a NEIGHBOUR.
/// MEASURED: that number survives our own writes (placement and source replacement), so it is a usable
/// confirmation rather than a snapshot that goes stale by itself.
/// DOC: <c>ksloadstateenum.html</c> — «Компонент не загружен — не отображается в окне документа.
/// Редактировать нельзя»; only <c>ksLCompletely</c> («Загружен полностью ... Можно редактировать») allows
/// an edit. LIMIT: an UNREAD number or load state is not a mismatch — it is named as unconfirmed, because
/// "not read" and "read and different" are different claims.
/// History: docs/decisions/assembly-ext.md#nested-implemented</remarks>
public static class NestedComponentAddress
{
    /// <summary>The ONE load state the help allows editing in (<c>ksLCompletely</c> = 0).</summary>
    public const string EditableLoadState = "ksLCompletely";

    /// <summary>Decide from the minted number, the number the path resolved to and the load state.</summary>
    /// <param name="mintedReference"><c>IPart7.Reference</c> the reference was issued with.</param>
    /// <param name="resolvedReference"><c>IPart7.Reference</c> of the instance the path leads to.</param>
    /// <param name="loadState">Name of <c>ksLoadStateEnum</c> as read, or <c>null</c> if not read.</param>
    public static NestedAddressVerdict Decide(
        int? mintedReference, int? resolvedReference, string? loadState)
    {
        if (mintedReference is { } minted && resolvedReference is { } resolved && minted != resolved)
        {
            return new NestedAddressVerdict(
                NestedAddressVerdictKind.StaleReference,
                $"Адрес вложенного компонента НЕ подтверждён: путь приводит к экземпляру с "
                + $"IPart7.Reference={resolved}, а ссылка выпущена для {minted}. Правка по "
                + "неподтверждённому адресу попала бы в ЧУЖОЙ компонент — перечитайте "
                + "kompas_list_components и возьмите свежую ссылку.");
        }

        if (loadState is { Length: > 0 } state
            && !string.Equals(state, EditableLoadState, StringComparison.Ordinal))
        {
            return new NestedAddressVerdict(
                NestedAddressVerdictKind.CapabilityUnavailable,
                $"Компонент загружен не полностью (LoadState={state}): правка его размещения и источника "
                + "этим маршрутом не выполняется. Справка разрешает редактирование только в состоянии "
                + $"{EditableLoadState} («Загружен полностью»).");
        }

        var unconfirmed = resolvedReference is null
            ? "nested_instance_reference_unread — IPart7.Reference разрешённого экземпляра не прочитан: "
              + "адрес подтверждён только путём индексов, номер экземпляра не сверен"
            : loadState is null
                ? "nested_load_state_unread — LoadState разрешённого экземпляра не прочитан: право на "
                  + "правку не подтверждено, а принято по умолчанию"
                : null;

        return new NestedAddressVerdict(
            NestedAddressVerdictKind.Addressable,
            resolvedReference is { } confirmed
                ? $"путь разрешён IPart7.Parts→IParts7.Part по уровням; экземпляр сверен по "
                  + $"IPart7.Reference={confirmed}"
                : "путь разрешён IPart7.Parts→IParts7.Part по уровням; номер экземпляра не сверен",
            unconfirmed);
    }
}
