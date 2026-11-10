using System.Runtime.InteropServices;
using KompasAPI7;

namespace AssemblyExtProbe;

/// <summary>Scenario D: what a NESTED component's address is made of, and what the API7 write reads back as.</summary>
/// <remarks>INVARIANT: a component inside a subassembly is addressed without a flat API5 index, which skips
/// nested rows. Answers (1) whether <c>IPart7.PartsEx</c> and recursive <c>IPart7.Parts</c> indices name the same
/// component, (2) whether the <c>IPart7.Placement</c> matrix is LOCAL to the parent or in document coordinates.
/// DOC: <c>ipart7_parts.html</c>, <c>iparts7_part.html</c>, <c>ipart7_partsex.html</c>, <c>ipart7_placement.html</c>,
/// <c>iplacement3d_getmatrix3d.html</c>, <c>ipart7_updateplacement.html</c>, <c>ipart7_filename.html</c>.
/// History: docs/decisions/assembly-ext.md#nested-components</remarks>
internal sealed partial class AssemblyExtProbe
{
    private void NestedAddressScenario()
    {
        var first = PartFile(Path.Combine(_work, "addr-part-a.m3d"), 5);
        var second = PartFile(Path.Combine(_work, "addr-part-b.m3d"), 8);
        var subPath = Path.Combine(_work, "addr-sub.m3d");
        var topPath = Path.Combine(_work, "addr-top.m3d");
        var replacedPath = Path.Combine(_work, "addr-part-c.m3d");
        var replacement = PartFile(replacedPath, 3);

        // The SUBASSEMBLY holds two different parts, so "which component" is decidable by file name.
        var (subDoc5, _, subTop) = NewDocument(isPart: false);
        Insert(subTop, first, Place(0, 0, 0));
        Insert(subTop, second, Place(30, 0, 0));
        subDoc5.RebuildDocument();
        if (!subDoc5.SaveAs(subPath))
        {
            Record("addr.sub_save", "файл подсборки записан", "SaveAs вернул false", "FAIL");
            return;
        }

        // The TOP assembly places that subassembly AWAY FROM THE ORIGIN: a local matrix and a summed one
        // differ only then, so the origin is what makes the two readings tell each other apart.
        var (topDoc5, _, top) = NewDocument(isPart: false);
        Insert(top, subPath, Place(100, 0, 0));
        Insert(top, second, Place(200, 0, 0));
        topDoc5.RebuildDocument();

        Record("addr.parts_ex_top", "верхний уровень: IPart7.PartsEx(ksAllParts)",
            DescribeChildren(top), "измерено", "подсборка в (100, 0, 0) и свободная деталь в (200, 0, 0)");

        // (1) THE ADDRESS. Which collection can an index be resolved through?
        var byPartsEx = ChildrenByPartsEx(top);
        var byParts = ChildrenByParts(top, limit: 6);
        Record("addr.partsex", "IPart7.PartsEx(ksAllParts) по индексам", byPartsEx, "измерено");
        Record("addr.parts", "IPart7.Parts → IParts7.Part(индекс) по индексам", byParts,
            "измерено");
        Record("addr.parts_vs_partsex", "индекс PartsEx и индекс Parts называют ОДИН компонент",
            SameIndexName(byPartsEx, byParts) ? "совпали по (Reference, FileName) при каждом индексе"
                : "РАСХОДЯТСЯ: " + byPartsEx + " | " + byParts,
            SameIndexName(byPartsEx, byParts) ? "PASS" : "FAIL", "PASS");

        var again = ChildrenByPartsEx(top);
        Record("addr.partsex_stable", "порядок PartsEx повторяется при втором чтении",
            again == byPartsEx ? "тот же порядок" : $"другой: {again}", "измерено");

        var sub7 = Child(top, 0);
        if (sub7 is null)
        {
            Record("addr.sub_read", "подсборка верхнего уровня прочитана", "IPart7 не получен", "FAIL");
            return;
        }

        var nestedByPartsEx = ChildrenByPartsEx(sub7);
        var nestedByParts = ChildrenByParts(sub7, limit: 6);
        Record("addr.nested_partsex", "вложенный уровень: PartsEx по индексам", nestedByPartsEx, "измерено");
        Record("addr.nested_parts", "вложенный уровень: Parts по индексам", nestedByParts, "измерено",
            "addr-part-a.m3d и addr-part-b.m3d");
        Record("addr.nested_correspondence", "вложенный уровень: индекс PartsEx = индекс Parts",
            SameIndexName(nestedByPartsEx, nestedByParts) ? "совпали" : "РАСХОДЯТСЯ", "измерено");

        // (2) LOCAL OR SUMMED. The write target is the component inside the subassembly, whose parent
        // stands at (100, 0, 0); a write of z = 20 lands at (100, 0, 20) if it is local.
        var nested = Child(sub7, 0);
        if (nested is null)
        {
            Record("addr.nested_read", "вложенный компонент прочитан", "IPart7 не получен", "FAIL");
            return;
        }

        Record("addr.summ_before", "суммарная матрица вложенного до правки",
            Origin(ComponentMatrix(sub7, nested)), "измерено", "(100, 0, 0)");
        Record("addr.local_before", "IPart7.Placement.GetMatrix3D вложенного до правки",
            Origin(LocalMatrix(nested)), "измерено", "(0, 0, 0), если матрица ЛОКАЛЬНАЯ");

        var init = nested.Placement is { } writePlacement && writePlacement.InitByMatrix3D(ToVariant(Place(0, 0, 20)));
        var updated = nested.UpdatePlacement(false);
        sub7.RebuildModel(false);
        top.RebuildModel(false);
        topDoc5.RebuildDocument();
        Record("addr.write", "IPlacement3D.InitByMatrix3D + IPart7.UpdatePlacement(false) вложенного",
            $"InitByMatrix3D={init}, UpdatePlacement={updated}", "измерено");

        var localAfter = LocalMatrix(nested);
        var summAfter = ComponentMatrix(sub7, nested);
        Record("addr.local_after", "IPart7.Placement.GetMatrix3D после записи (0, 0, 20)",
            Origin(localAfter), "измерено", "(0, 0, 20) = локальная матрица записана и прочитана");
        Record("addr.summ_after", "суммарная матрица после той же записи",
            Origin(summAfter), "измерено", "(100, 0, 20) = локальная пересчитана в документ");
        Record("addr.read_back_matches_request", "перечитанная ЛОКАЛЬНАЯ матрица равна записанной",
            localAfter is null ? "не читается" : Origin(localAfter),
            localAfter is not null && Origin(localAfter) == "(0, 0, 20)" ? "PASS" : "FAIL", "(0, 0, 20)");

        // A component that is NOT moved must keep its placement: the write must not hit a neighbour.
        var neighbour = Child(sub7, 1);
        Record("addr.neighbour_untouched", "соседний вложенный компонент не сдвинулся",
            neighbour is null ? "не читается" : Origin(ComponentMatrix(sub7, neighbour)), "измерено",
            "(130, 0, 0)");

        // (3) SAVE AND REOPEN. Only the TOP assembly is saved; where does the local placement live?
        for (var attempt = 0; attempt < 3; attempt++)
        {
            topDoc5.RebuildDocument();
            System.Threading.Thread.Sleep(300);
        }

        var subStampBefore = Stamp(subPath);
        topDoc5.Save();
        var savedPath = Path.Combine(_work, "addr-top-saved.m3d");
        var saved = topDoc5.SaveAs(savedPath);
        Record("addr.save", "сохранена только верхняя сборка", $"SaveAs={saved}", "измерено");

        if (!Reopen(savedPath, out _) || _doc7?.TopPart is not IPart7 top2)
        {
            Record("addr.reopen", "верхняя сборка открыта повторно", "не открылась", "FAIL");
            return;
        }

        var sub2 = Child(top2, 0);
        var nested2 = sub2 is null ? null : Child(sub2, 0);
        var local2 = nested2 is null ? null : LocalMatrix(nested2);
        Record("addr.reopen_local", "после сохранения верхней и переоткрытия: локальная матрица вложенного",
            Origin(local2), local2 is not null && Origin(local2) == "(0, 0, 20)" ? "PASS" : "FAIL",
            "(0, 0, 20)");
        Record("addr.reopen_summ", "после переоткрытия: суммарная матрица вложенного",
            sub2 is null || nested2 is null ? "не читается" : Origin(ComponentMatrix(sub2, nested2)),
            "измерено", "(100, 0, 20)");

        // (4) WHAT A FILE-STAMP CAN SEE. The subassembly file is the only file the top's own save may write.
        Record("addr.sub_file_stamp", "отметка файла подсборки до и после сохранения верхней",
            $"{subStampBefore} → {Stamp(subPath)}", "измерено",
            "изменение отметки = файл подсборки записан сохранением верхней");

        // (5) SOURCE REPLACEMENT of a nested component through IPart7.FileName + IModelObject.Update.
        if (nested2 is null || sub2 is null)
        {
            Record("addr.replace", "вложенный компонент для замены источника", "не прочитан", "FAIL");
            return;
        }

        var beforeName = Path.GetFileName(SafeText(() => nested2.FileName));
        var referenceBefore = SafeInt(() => nested2.Reference);
        nested2.FileName = replacement;
        var updateResult = nested2.Update();
        sub2.RebuildModel(false);
        top2.RebuildModel(false);
        _doc5?.RebuildDocument();
        var afterName = Path.GetFileName(SafeText(() => nested2.FileName));
        Record("addr.replace", "IPart7.FileName = <новый файл> + IModelObject.Update()",
            $"Update()={updateResult}, было «{beforeName}», стало «{afterName}»",
            afterName == Path.GetFileName(replacement) ? "PASS" : "FAIL", Path.GetFileName(replacement));
        Record("addr.replace_local_kept", "размещение вложенного после замены источника",
            Origin(LocalMatrix(nested2)), "измерено", "(0, 0, 20)");
        // THE ADDRESS MUST SURVIVE OUR OWN MUTATION: the product resolves a nested reference by the path of
        // indices, so the number behind an index has to stay the same instance after a source replacement.
        Record("addr.reference_after_replace", "IPart7.Reference вложенного до и после замены источника",
            $"{referenceBefore} → {SafeInt(() => nested2.Reference)}", "измерено",
            "то же число = экземпляр не пересоздан и путь указывает на него");
        Record("addr.parts_after_replace", "IPart7.Parts по индексам после замены источника",
            ChildrenByParts(sub2, limit: 6), "измерено", "addr-part-c.m3d на прежнем индексе");

        _doc5?.Save();
        var replacedTop = Path.Combine(_work, "addr-top-replaced.m3d");
        _doc5?.SaveAs(replacedTop);
        if (Reopen(replacedTop, out _) && _doc7?.TopPart is IPart7 top3 && Child(top3, 0) is { } sub3
            && Child(sub3, 0) is { } nested3)
        {
            Record("addr.replace_reopen", "замена источника вложенного после сохранения и переоткрытия",
                $"{Path.GetFileName(SafeText(() => nested3.FileName))}, размещение {Origin(LocalMatrix(nested3))}",
                Path.GetFileName(SafeText(() => nested3.FileName)) == Path.GetFileName(replacement) ? "PASS" : "FAIL",
                Path.GetFileName(replacement));
        }
        else
        {
            Record("addr.replace_reopen", "верхняя сборка открывается после замены", "не открылась", "FAIL");
        }

        Fact("addr_top", topPath);
        Fact("addr_replaced_top", replacedTop);
    }

    /// <summary>The LOCAL placement matrix of a component: <c>IPart7.Placement</c> →
    /// <c>IPlacement3D.GetMatrix3D</c>.</summary>
    private static double[]? LocalMatrix(IPart7 part)
    {
        try
        {
            return part.Placement is { } placement ? Matrix16(placement.GetMatrix3D()) : null;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Children by INDEX of <c>IPart7.PartsEx(ksAllParts)</c>.</summary>
    private static string ChildrenByPartsEx(IPart7 node)
    {
        var rows = new List<string>();
        try
        {
            var raw = node.get_PartsEx((object)(int)Kompas6Constants3D.ksPart7CollectionTypeEnum.ksAllParts);
            var items = raw switch
            {
                null => new List<IPart7>(),
                Array array => array.OfType<IPart7>().ToList(),
                IPart7 single => [single],
                _ => new List<IPart7>(),
            };
            for (var index = 0; index < items.Count; index++)
            {
                rows.Add($"[{index}] {Describe(items[index])}");
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            rows.Add($"{ex.GetType().Name}: {ex.Message}");
        }

        return string.Join("; ", rows);
    }

    /// <summary>Children by INDEX of the documented recursive <c>IPart7.Parts</c> →
    /// <c>IParts7.Part(index)</c>. The interface has NO count (<c>iparts7_props</c> lists only
    /// <c>Part</c>), so the probe walks indices until one answers null.</summary>
    private static string ChildrenByParts(IPart7 node, int limit)
    {
        var rows = new List<string>();
        try
        {
            var parts = node.Parts;
            for (var index = 0; index < limit; index++)
            {
                var child = parts?.get_Part(index) as IPart7;
                if (child is null)
                {
                    rows.Add($"[{index}] null");
                    break;
                }

                rows.Add($"[{index}] {Describe(child)}");
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            rows.Add($"{ex.GetType().Name}: {ex.Message}");
        }

        return string.Join("; ", rows);
    }

    private static string DescribeChildren(IPart7 node) => ChildrenByPartsEx(node);

    private static string Describe(IPart7 part) =>
        $"Reference={SafeInt(() => part.Reference)}, FileName={Path.GetFileName(SafeText(() => part.FileName))}, "
        + $"Name={SafeText(() => part.Name)}";

    private static string? SafeText(Func<string?> read)
    {
        try
        {
            return read();
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return null;
        }
    }

    private static string SafeInt(Func<int> read)
    {
        try
        {
            return read().ToString();
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return "не читается";
        }
    }

    /// <summary>Whether the two enumerations name the same component at every index — compared by the
    /// diagnostic number AND the source file, so a coincidence in one of them does not pass.</summary>
    private static bool SameIndexName(string byPartsEx, string byParts)
    {
        static List<string> Split(string text) =>
            text.Split("; ", StringSplitOptions.RemoveEmptyEntries)
                .Where(row => !row.EndsWith("null", StringComparison.Ordinal))
                .ToList();

        var left = Split(byPartsEx);
        var right = Split(byParts);
        if (left.Count == 0 || right.Count == 0 || left.Count != right.Count)
        {
            return false;
        }

        for (var index = 0; index < left.Count; index++)
        {
            var a = left[index];
            var b = right[index];
            if (!string.Equals(a, b, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }
}
