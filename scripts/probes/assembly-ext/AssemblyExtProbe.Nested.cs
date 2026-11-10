using System.Runtime.InteropServices;
using Kompas6API5;
using Kompas6Constants3D;
using KompasAPI7;

namespace AssemblyExtProbe;

/// <summary>Scenario C: a component inside a subassembly - reading it, changing its placement and its
/// source file, and what has to be saved for the parent assembly to see the change.</summary>
/// <remarks>DOC: <c>ipart7_parts.html</c> (IPart7.Parts - the recursive collection), <c>ipart7_filename.html</c>
/// («Имя файла можно установить только у компонента, вставленного в сборку», effective after
/// <c>IModelObject::Update</c>), <c>ipart7_placement.html</c> (read-only property),
/// <c>ipart7_updateplacement.html</c>, <c>iplacement3d_initbymatrix3d.html</c>,
/// <c>kspart_beginedit.html</c> («работает только для компонента, вставленного в сборку», «только для
/// визуального документа»), <c>ipart7_beginedit.html</c>, <c>ksdocument3d_partcollection.html</c>.
/// INVARIANT: a save is taken only after the rebuild settles - a save raced against a rebuild would answer
/// with the OLD placement and blame the route.</remarks>
internal sealed partial class AssemblyExtProbe
{
    private void NestedScenario()
    {
        var nestedSource = PartFile(Path.Combine(_work, "nested-part.m3d"), 5);
        var replacement = PartFile(Path.Combine(_work, "nested-replacement.m3d"), 8);
        var subPath = Path.Combine(_work, "nested-sub.m3d");
        var topPath = Path.Combine(_work, "nested-top.m3d");

        var (subDoc5, _, subTop) = NewDocument(isPart: false);
        Insert(subTop, nestedSource, Place(0, 0, 0));
        subDoc5.RebuildDocument();
        if (!subDoc5.SaveAs(subPath))
        {
            Record("nested.sub_save", "сохранение файла подсборки", "SaveAs вернул false", "FAIL");
            return;
        }

        var subStampBefore = Stamp(subPath);
        var (topDoc5, _, top) = NewDocument(isPart: false);
        Insert(top, subPath, Place(0, 0, 0));
        Insert(top, replacement, Place(60, 0, 0));
        topDoc5.RebuildDocument();
        if (!topDoc5.SaveAs(topPath))
        {
            Record("nested.top_save", "сохранение верхней сборки", "SaveAs вернул false", "FAIL");
            return;
        }

        var flat = Part5Count();
        Record("nested.api5_flat", "ksDocument3D.PartCollection(true) перечисляет вложенный компонент",
            $"{flat} записей на 1 верхний компонент-подсборку, 1 вложенный и 1 свободную деталь",
            flat == 2 ? "нет: только верхний уровень" : $"да ({flat})", "измерено");

        var sub7 = Child(top, 0);
        var nested7 = sub7 is null ? null : Child(sub7, 0);
        if (sub7 is null || nested7 is null)
        {
            Record("nested.read", "вложенный компонент получен через IPart7.Parts → IPart7.Parts",
                $"подсборка={sub7 is not null}, вложенный={nested7 is not null}", "FAIL");
            return;
        }

        Record("nested.read", "вложенный компонент получен через IPart7.Parts → IPart7.Parts",
            $"файл: {Path.GetFileName(nested7.FileName)}, начало: {Origin(ComponentMatrix(sub7, nested7))}",
            "PASS", "nested-part.m3d в (0, 0, 0)");

        // 1. Placement of the NESTED component through the documented API7 write.
        var placed = MoveNested(sub7, nested7, Place(0, 0, 20), top, topDoc5);
        var afterWrite = Origin(ComponentMatrix(sub7, nested7));
        Record("nested.placement_write", "IPlacement3D.InitByMatrix3D + IPart7.UpdatePlacement вложенного",
            $"записано={placed}, перечитано: {afterWrite}", afterWrite.Contains("(0, 0, 20)") ? "PASS" : "FAIL",
            "(0, 0, 20)");

        // 2. Save ONLY the parent after the rebuild settles and reopen: the nested placement belongs to the
        //    subassembly, so this row says whether the parent's own save can carry it.
        var settle = Settle(top);
        topDoc5.Save();
        Record("nested.settle", "перестроение улеглось перед сохранением", settle, "измерено");

        var parentSaved = Path.Combine(_work, "nested-top-parent-saved.m3d");
        topDoc5.SaveAs(parentSaved);
        if (!Reopen(parentSaved, out _) || _doc7?.TopPart is not IPart7 top2)
        {
            Record("nested.reopen_after_parent_save", "верхняя сборка открывается повторно", "не открылась", "FAIL");
            return;
        }

        var sub2 = Child(top2, 0);
        var nested2 = sub2 is null ? null : Child(sub2, 0);
        var persistedByParent = nested2 is null ? "не читается" : Origin(ComponentMatrix(sub2!, nested2));
        Record("nested.parent_save_carries", "сохранение ТОЛЬКО верхней сборки переносит правку вложенного",
            $"перечитано: {persistedByParent}", persistedByParent.Contains("(0, 0, 20)") ? "PASS" : "FAIL",
            "(0, 0, 20)");
        Fact("nested_after_parent_save", persistedByParent);

        // 2a. WHERE the change went. The FILE STAMP is the evidence and not a reopen: a reopen may be served
        //     by the document KOMPAS already holds in memory, so it cannot say whether the file changed.
        Record("nested.sub_file_stamp", "файл подсборки на диске: до правки и после сохранения верхней сборки",
            $"{subStampBefore} → {Stamp(subPath)}", "измерено",
            "изменение отметки = файл записан; та же отметка = правка осталась в верхней сборке");

        // 2b. And what a fresh read of the subassembly file says.
        if (Reopen(subPath, out _) && _doc7?.TopPart is IPart7 subFileTop && Child(subFileTop, 0) is { } fileChild)
        {
            Record("nested.sub_file_after_parent_save", "начало компонента в САМОМ файле подсборки после сохранения верхней",
                Origin(ComponentMatrix(subFileTop, fileChild)), "измерено",
                "(0, 0, 0) - правка осталась в верхней сборке");
        }
        else
        {
            Record("nested.sub_file_after_parent_save", "файл подсборки открывается отдельно", "не открылся", "FAIL");
        }

        // 3. Subassembly as a DOCUMENT, and the source replacement: both need the parent open again.
        if (!Reopen(parentSaved, out _) || _doc7?.TopPart is not IPart7 top3)
        {
            Record("nested.work_reopen", "верхняя сборка открывается для третьего шага", "не открылась", "FAIL");
            return;
        }

        var sub3 = Child(top3, 0);
        var nested3 = sub3 is null ? null : Child(sub3, 0);
        if (sub3 is null || nested3 is null)
        {
            Record("nested.work_read", "вложенный компонент прочитан для третьего шага", "не прочитан", "FAIL");
            return;
        }

        Record("nested.sub_document", "подсборка как документ: IPart7.BeginEdit / ksPart.BeginEdit",
            DescribeSubAssemblyDocument(sub3), "измерено", "маршрут назван и его ответ записан");

        // 4. Replace the SOURCE of the nested component, save the parent and reopen.
        ReplaceSource(sub3, nested3, replacement, top3, parentSaved);
        Fact("nested_top", topPath);
    }

    /// <summary>Both documented ways to open the subassembly for editing, with what each returns.
    /// DOC: <c>ipart7_beginedit.html</c> (IOpenDocumentParam → IKompasDocument3D) and
    /// <c>kspart_beginedit.html</c> (no parameter → ksDocument3D, «только для визуального документа»).</summary>
    private string DescribeSubAssemblyDocument(IPart7 sub)
    {
        var described = new List<string>();
        try
        {
            var param = _app7?.Application
                ?? throw new InvalidOperationException("IApplication недоступен");
            var openParam = sub.GetOpenDocumentParam();
            if (openParam is IOpenDocumentParam settings)
            {
                settings.Visible = true;
            }

            described.Add(openParam is null
                ? "GetOpenDocumentParam() → null"
                : $"GetOpenDocumentParam() → {openParam.GetType().Name}, Visible=true");
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or InvalidOperationException)
        {
            described.Add($"GetOpenDocumentParam: {ex.GetType().Name}: {ex.Message}");
        }

        try
        {
            var opened = sub.BeginEdit(sub.GetOpenDocumentParam());
            described.Add(opened is null ? "IPart7.BeginEdit → null" : "IPart7.BeginEdit → IKompasDocument3D");
            if (opened is not null)
            {
                var ended = sub.EndEdit(true);
                described.Add($"EndEdit(true)={ended}");
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            described.Add($"IPart7.BeginEdit: {ex.GetType().Name}: {ex.Message}");
        }

        try
        {
            var part5 = Part5ByOrigin(0, 0, 0);
            var edit = part5?.BeginEdit();
            described.Add(edit is null
                ? "ksPart.BeginEdit() → null"
                : $"ksPart.BeginEdit() → {edit.GetType().Name}");
            if (edit is ksDocument3D subDocument)
            {
                described.Add($"документ подсборки: Save()={subDocument.Save()}, EndEdit()={part5!.EndEdit(true)}");
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            described.Add($"ksPart.BeginEdit: {ex.GetType().Name}: {ex.Message}");
        }

        return string.Join("; ", described);
    }

    /// <summary>Writes the nested component's placement and re-reads it through the parent's matrix.</summary>
    private bool MoveNested(IPart7 parent, IPart7 nested, double[] matrix, IPart7 top, ksDocument3D doc5)
    {
        try
        {
            if (nested.Placement is not { } placement || !placement.InitByMatrix3D(ToVariant(matrix)))
            {
                return false;
            }

            nested.UpdatePlacement(false);
            parent.RebuildModel(false);
            top.RebuildModel(false);
            doc5.RebuildDocument();
            return true;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            Record("nested.move_fault", "правка размещения вложенного компонента",
                $"{ex.GetType().Name}: {ex.Message}", "FAIL");
            return false;
        }
    }

    /// <summary>Rebuilds the document three times with a pause between attempts, then reports what was done.</summary>
    /// <remarks>MEASURED: <c>IPart7.NeedRebuild</c> is promised by <c>ipart7_needrebuild.html</c> but is ABSENT
    /// from <c>Interop.KompasAPI7.dll</c> (compile error CS1061), so there is no flag to poll. The wait is
    /// therefore a fixed, named budget instead of a condition that does not exist in this build; a save is
    /// still taken only after it.</remarks>
    private string Settle(IPart7 top)
    {
        _ = top;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            _doc5?.RebuildDocument();
            System.Threading.Thread.Sleep(300);
        }

        return "3 × RebuildDocument с паузой 300 мс (IPart7.NeedRebuild в интероп-сборке нет)";
    }

    /// <summary>Replaces the nested component's source file, saves the parent and checks what a reopen reads.</summary>
    private void ReplaceSource(IPart7 sub, IPart7 nested, string replacement, IPart7 top, string parentPath)
    {
        try
        {
            nested.FileName = replacement;
            var updated = nested.Update();
            sub.RebuildModel(false);
            top.RebuildModel(false);
            _doc5?.RebuildDocument();
            Settle(top);

            var nameNow = nested.FileName;
            Record("nested.replace", "смена файла-источника вложенного компонента (IPart7.FileName + Update)",
                $"Update()={updated}, файл={Path.GetFileName(nameNow)}",
                string.Equals(Path.GetFileName(nameNow), Path.GetFileName(replacement),
                    StringComparison.OrdinalIgnoreCase) ? "PASS" : "FAIL", Path.GetFileName(replacement));

            _doc5?.Save();
            var path = Path.Combine(Path.GetDirectoryName(parentPath)!, "nested-top-replaced.m3d");
            _doc5?.SaveAs(path);
            if (!Reopen(path, out _) || _doc7?.TopPart is not IPart7 top3)
            {
                Record("nested.replace_reopen", "сборка открывается после замены источника", "не открылась", "FAIL");
                return;
            }

            var sub3 = Child(top3, 0);
            var nested3 = sub3 is null ? null : Child(sub3, 0);
            var reopenedName = nested3 is null ? "не читается" : Path.GetFileName(nested3.FileName);
            Record("nested.replace_persisted", "замена источника вложенного компонента после сохранения и переоткрытия",
                $"{reopenedName}",
                string.Equals(reopenedName, Path.GetFileName(replacement), StringComparison.OrdinalIgnoreCase)
                    ? "PASS"
                    : "FAIL", Path.GetFileName(replacement));
            Fact("nested_replaced_top", path);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            Record("nested.replace", "смена файла-источника вложенного компонента",
                $"{ex.GetType().Name}: {ex.Message}", "FAIL");
        }
    }

    /// <summary>Size and last-write time of a file: the only witness of a WRITE that a reopen cannot fake.</summary>
    private static string Stamp(string path)
    {
        var info = new FileInfo(path);
        return info.Exists
            ? $"{info.Length}B {info.LastWriteTimeUtc:HH:mm:ss.fff}"
            : "файла нет";
    }

    /// <summary>The child component at <paramref name="index"/> of the given part, via <c>IPart7.Parts</c>.</summary>
    private static IPart7? Child(IPart7 parent, int index)
    {
        try
        {
            return parent.Parts?.get_Part(index) as IPart7;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return null;
        }
    }
}
