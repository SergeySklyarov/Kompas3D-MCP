namespace KompasMcp.Domain.References;

/// <summary>One instance as the address search sees it: the source file and the placement matrix.</summary>
/// <remarks>An unread value stays <c>null</c>: "not read" and "read and matched" are different claims.
/// <paramref name="Matrix"/> is 16 numbers of the 4×4 rigid transform, or <c>null</c> if not read.</remarks>
public sealed record ComponentInstance(string? SourcePath, IReadOnlyList<double>? Matrix);

/// <summary>The address found for one record: the index in the flat collection, or <c>null</c> with a
/// reason.</summary>
public sealed record ComponentAddressVerdict(int? Index, string Detail);

/// <summary>The component-address rule — a PURE function of documented instance properties, no KOMPAS.</summary>
/// <remarks>INVARIANT: the address is the INDEX in the flat <c>ksDocument3D.PartCollection(true)</c>, FOUND
/// by matching the API7 record with a collection element, never counted — MEASURED: an excluded instance is
/// in the collection but not in <c>IPart7.PartsEx</c>, so a counter shifts every later address onto a
/// FOREIGN component. DOC: <c>kspart_filename.html</c>, <c>ipart7_filename.html</c>,
/// <c>kspart_getplacement.html</c> → <c>ksPlacement.GetMatrix3D</c>, <c>ipart7_getsummmatrix.html</c>,
/// <c>kspartcollection_getbyindex.html</c>. LIMIT: two instances of one file at one placement are
/// indistinguishable; they get NO address, not a guessed one. History: docs/decisions/assembly.md#identity</remarks>
public static class ComponentAddress
{
    /// <summary>The rule, in the words a refusal quotes.</summary>
    public const string Rule =
        "Адрес компонента — индекс в плоском ksDocument3D.PartCollection(true), и он НАЙДЕН "
        + "сопоставлением записи с элементом коллекции по полному пути файла-источника и размещению, а "
        + "не посчитан: IPart7.PartsEx не содержит исключённого из расчёта экземпляра, поэтому счётчик "
        + "сдвигает все следующие адреса на чужой компонент.";

    /// <summary>Find the index of <paramref name="record"/> among <paramref name="collection"/>.</summary>
    /// <returns>The index, or <c>null</c> when no element matches unambiguously; the detail names which
    /// stage decided it, so "not found" is distinguishable from "found twice".</returns>
    public static ComponentAddressVerdict Locate(
        ComponentInstance record, IReadOnlyList<ComponentInstance> collection)
    {
        var total = collection.Count;
        if (string.IsNullOrWhiteSpace(record.SourcePath))
        {
            return new ComponentAddressVerdict(null,
                $"адрес не назначен: файл-источник записи не прочитан, сопоставлять нечем "
                + $"(экземпляров в коллекции {total})");
        }

        var bySource = new List<int>();
        for (var index = 0; index < total; index++)
        {
            if (SameSource(record.SourcePath, collection[index].SourcePath))
            {
                bySource.Add(index);
            }
        }

        if (bySource.Count == 0)
        {
            return new ComponentAddressVerdict(null,
                $"адрес не назначен: по полному пути «{record.SourcePath}» в коллекции нет ни одного "
                + $"экземпляра (всего {total})");
        }

        if (bySource.Count == 1)
        {
            var only = bySource[0];
            var matrix = collection[only].Matrix;
            if (SameMatrix(record.Matrix, matrix))
            {
                return new ComponentAddressVerdict(only,
                    $"адрес {only} — единственный экземпляр с этим путём, размещение совпало");
            }

            if (record.Matrix is null || matrix is null)
            {
                return new ComponentAddressVerdict(only,
                    $"адрес {only} — единственный экземпляр с этим путём; размещение не сверено "
                    + "(не прочитано с одной из сторон)");
            }

            return new ComponentAddressVerdict(null,
                $"адрес не назначен: единственный экземпляр с этим путём ({only}) стоит по ДРУГОМУ "
                + "размещению — сопоставление не подтверждено");
        }

        var byPlacement = bySource.Where(i => SameMatrix(record.Matrix, collection[i].Matrix)).ToList();
        if (byPlacement.Count == 1)
        {
            return new ComponentAddressVerdict(byPlacement[0],
                $"адрес {byPlacement[0]} — из {bySource.Count} экземпляров с этим путём размещение "
                + "совпало у одного");
        }

        return new ComponentAddressVerdict(null,
            $"адрес не назначен: с этим путём {bySource.Count} экземпляра(ов), размещение совпало у "
            + $"{byPlacement.Count} — однозначного сопоставления нет");
    }

    /// <summary>Whether two values name the same FILE: the FULL path, normalised, case-insensitive.</summary>
    /// <remarks>INVARIANT: file NAMES are never compared — two different files of one name are exactly the
    /// pair a name comparison confirms as "the same component". A path that cannot be normalised falls
    /// back to the raw strings, so an odd value yields "differs", not an exception.</remarks>
    public static bool SameSource(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return false;
        }

        try
        {
            return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>Whether two 4×4 matrices are the same placement, within the tolerance the project uses for
    /// every matrix comparison.</summary>
    /// <remarks>MEASURED: the tolerance is 0.01 mm absolute or 1e-6 relative, whichever is larger — the
    /// same one <c>MatrixMatchesRequest</c> uses for a written placement.</remarks>
    public static bool SameMatrix(IReadOnlyList<double>? a, IReadOnlyList<double>? b)
    {
        if (a is null || b is null || a.Count < 16 || b.Count < 16)
        {
            return false;
        }

        for (var i = 0; i < 16; i++)
        {
            if (Math.Abs(a[i] - b[i]) > Math.Max(0.01, Math.Abs(a[i]) * 1e-6))
            {
                return false;
            }
        }

        return true;
    }
}
