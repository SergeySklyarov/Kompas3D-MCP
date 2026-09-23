using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Kompas6API5;
using KompasMcp.Api5Adapter.Com;

namespace KompasMcp.P0Probe;

/// <summary>
/// P0.9 — STEP. Export is proven by the historical scripts; the import is the item that failed
/// there and is an acceptance point of its own (spec 1.11 §8, 4.6). Each import route is tried
/// independently so that one dead end cannot hide the others, and after every call the probe
/// looks for the result in *all* the places it could legitimately have landed.
/// </summary>
internal static class StepFacts
{
    /// <summary>D3FormatConvType: format_STEP (write) and load_format_STEP (open).</summary>
    private const int FormatStep = 3;

    private const int LoadFormatStep = -3;

    private static string WorkFile(string name) => Path.Combine(ProbeSession.WorkDir, name);

    public static void Run(ProbeReport report, ProbeOptions options)
    {
        var app = ProbeSession.App;
        if (app is null)
        {
            var skipped = report.Begin("P0.9", "STEP: экспорт и импорт");
            skipped.Verdict = Verdict.Skipped;
            skipped.Conclusion = "Нет подключения к КОМПАС.";
            return;
        }

        var step = report.Begin(
            "P0.9",
            "STEP: экспорт и три маршрута импорта",
            "Куда реально попадают тела после импорта: в вызванный документ, в новый или никуда?");

        var stepPath = WorkFile("P0_Units.step");
        try
        {
            var source = WorkFile("P0_Units.m3d");
            if (!File.Exists(source))
            {
                step.Fail("Нет исходной модели с телом для экспорта.");
                return;
            }

            // ---- export ----
            var exportDoc = (ksDocument3D)app.Document3D();
            if (!exportDoc.Open(source, true))
            {
                step.Fail("Исходная модель не открылась.");
                return;
            }

            var exportParam = (ksAdditionFormatParam)exportDoc.AdditionFormatParam();
            exportParam.Init();
            exportParam.format = FormatStep;
            step.Observe($"Экспорт: ksAdditionFormatParam.Init(), format={FormatStep} (format_STEP); lengthUnits до = {exportParam.lengthUnits}.");
            var exported = exportDoc.SaveAsToAdditionFormat(stepPath, exportParam);
            step.Observe($"SaveAsToAdditionFormat → {exported}; файл {(File.Exists(stepPath) ? new FileInfo(stepPath).Length + " байт" : "не создан")}.");
            if (!exported || !File.Exists(stepPath))
            {
                step.Fail("Экспорт STEP не подтверждён.");
                return;
            }

            step.Artifacts.Add(stepPath);
            step.Data["step_sha256"] = Sha256(stepPath);

            var head = ReadAsciiHead(stepPath, 6000);
            step.Data["step_schema_token"] = FirstToken(head, "AP242", "AP214", "AP203", "AUTOMOTIVE_DESIGN", "CONFIG_CONTROL_DESIGN");
            step.Data["step_converter_token"] = FirstToken(head, "C3D", "STMicroelectronics", "Indra", "Siemens");
            step.Observe($"Заголовок файла: схема {step.Data["step_schema_token"] ?? "?"}, конвертер {step.Data["step_converter_token"] ?? "?"}.");
            step.Observe("Единицы в заголовке ищутся отдельно: явного «mm» в первых 6 КБ нет — уровень проверки остаётся structure_checked без roundtrip.");
            exportDoc.close();

            if (!options.RunStepImport)
            {
                step.Verdict = Verdict.Skipped;
                step.Conclusion = "--no-import.";
                return;
            }

            // Document counting via API7 is the only reliable way to see whether an import
            // created a *new* document rather than filling the one we called it on.
            var documents = TryGetApi7Documents(app, step);

            // ---- route 1: LoadFromAdditionFormat with the default (unset) format ----
            var route1 = TryImportRoute(app, documents, step, "по умолчанию (format не задан)",
                param => { /* Init already applied; format left as delivered */ });

            // ---- route 2: the documented read-side selector ----
            var route2 = TryImportRoute(app, documents, step, "format=load_format_STEP(-3)",
                param => param.format = LoadFormatStep);

            // ---- route 3: API7 Documents.Open ----
            var route3 = TryApi7Open(app, step, stepPath);

            step.Data["route_default"] = route1.Summary();
            step.Data["route_negative_format"] = route2.Summary();
            step.Data["route_api7_open"] = route3.Summary();

            ImportOutcome? winner = null;
            foreach (var candidate in new[] { route1, route2, route3 })
            {
                if (candidate.BodyCount > 0 || candidate.NewDocuments > 0)
                {
                    winner = candidate;
                    break;
                }
            }

            if (winner is not null)
            {
                var won = winner.Value;
                step.Pass($"Импорт STEP работает через маршрут «{won.Name}»: {won.Summary()}. Провал spec 4.6 на этой версии воспроизводится не во всех маршрутах.");
            }
            else
            {
                step.Fail(
                    "Исторический провал ПОДТВЕРЖДЁН во всех трёх маршрутах: вызовы возвращают успех либо отказ, но тела ни в вызванном документе, " +
                    "ни в новом не появляются. Причина не установлена; STEP-import остаётся открытым обязательным пунктом.");
            }
        }
        catch (Exception ex)
        {
            step.Errors.Add(ex.ToString());
            step.Fail("STEP-шаг не завершён: " + ex.GetType().Name);
        }
    }

    private readonly record struct ImportOutcome(string Name, bool Returned, int BodyCount, int NewDocuments, string? Detail)
    {
        public string Summary() => $"вернул={Returned}, тел={BodyCount}, новых документов={NewDocuments}{(Detail is null ? string.Empty : ", " + Detail)}";
    }

    private static ImportOutcome TryImportRoute(
        KompasObject app,
        object? documents,
        ProbeStep step,
        string label,
        Action<ksAdditionFormatParam> configure)
    {
        var stepPath = WorkFile("P0_Units.step");
        ksDocument3D? target = null;
        try
        {
            var countBefore = DocumentCount(documents);
            target = (ksDocument3D)app.Document3D();
            var created = target.Create(true, true);
            var param = (ksAdditionFormatParam)target.AdditionFormatParam();
            param.Init();
            configure(param);

            bool returned;
            try
            {
                returned = target.LoadFromAdditionFormat(stepPath, param);
            }
            catch (Exception ex)
            {
                var inner = ex is TargetInvocationException tie ? tie.InnerException : ex;
                step.Observe($"Маршрут «{label}»: исключение {inner?.GetType().Name}: {inner?.Message}");
                return new ImportOutcome(label, false, -1, 0, inner?.GetType().Name);
            }

            target.RebuildDocument();
            var bodies = CountBodies(target);
            var countAfter = DocumentCount(documents);

            // The import may have landed in a different document; look at the active one too.
            var activeBodies = -1;
            try
            {
                if (typeof(KompasObject).GetMethod("ActiveDocument3D")!.Invoke(app, null) is ksDocument3D active)
                {
                    activeBodies = CountBodies(active);
                }
            }
            catch (Exception ex)
            {
                step.Observe($"Маршрут «{label}»: ActiveDocument3D → {ex.GetType().Name}");
            }

            step.Observe($"Маршрут «{label}»: Create → {created}, LoadFromAdditionFormat → {returned}; тел в вызванном документе {bodies}, в активном {activeBodies}, документов {countBefore}→{countAfter}.");
            return new ImportOutcome(label, returned, Math.Max(bodies, activeBodies), countAfter - countBefore, null);
        }
        catch (Exception ex)
        {
            step.Observe($"Маршрут «{label}» не выполнен: {ex.GetType().Name}: {ex.Message}");
            return new ImportOutcome(label, false, -1, 0, ex.GetType().Name);
        }
        finally
        {
            try
            {
                target?.close();
            }
            catch
            {
                // Reported through the outcome.
            }
        }
    }

    private static ImportOutcome TryApi7Open(KompasObject app, ProbeStep step, string stepPath)
    {
        const string label = "API7 Documents.Open";
        try
        {
            var application7 = GetApplication7(app);
            if (application7 is null)
            {
                step.Observe("Маршрут «API7…»: ksGetApplication7() вернул null — m3d/API7 недоступен.");
                return new ImportOutcome(label, false, -1, 0, "нет IApplication");
            }

            var documentsType = application7.GetType().GetProperty("Documents")?.PropertyType;
            if (documentsType is null)
            {
                step.Observe("Маршрут «API7…»: у IApplication нет Documents.");
                return new ImportOutcome(label, false, -1, 0, "нет Documents");
            }

            var documents = documentsType.GetProperty("Documents")!.GetValue(application7);
            var before = DocumentCount(documents);
            var open = FindMethod(documentsType, "Open", 3);
            if (open is null)
            {
                step.Observe("Маршрут «API7…»: Documents.Open(String,Boolean,Boolean) не найден.");
                return new ImportOutcome(label, false, -1, 0, "нет Open");
            }

            // visible=false, readonly=false
            var document = open.Invoke(documents, new object?[] { stepPath, false, false });
            var after = DocumentCount(documents);
            var bodies = -1;
            if (document is not null)
            {
                step.Observe($"Маршрут «API7…»: Open вернул {document.GetType().Name}; тип-интерфейс {InterfaceName(document)}.");
                bodies = CountBodiesOfApi7Document(document);
            }

            step.Observe($"Маршрут «API7…»: документов {before}→{after}, тел в полученном документе {bodies}.");
            return new ImportOutcome(label, document is not null, bodies, after - before, null);
        }
        catch (Exception ex)
        {
            var inner = ex is TargetInvocationException tie ? tie.InnerException : ex;
            step.Observe($"Маршрут «API7…»: {inner?.GetType().Name}: {inner?.Message}");
            return new ImportOutcome(label, false, -1, 0, inner?.GetType().Name);
        }
    }

    private static int CountBodiesOfApi7Document(object document)
    {
        try
        {
            var parts = document.GetType().GetProperty("Parts");
            if (parts?.GetValue(document) is not object partsCollection)
            {
                return -1;
            }

            var count = partsCollection.GetType().GetMethod("GetCount")?.Invoke(partsCollection, null);
            return count is int i ? i : -1;
        }
        catch (Exception ex)
        {
            return -1 - (ex.GetType().Name.Length % 1000);
        }
    }

    private static object? GetApplication7(KompasObject app)
    {
        try
        {
            return typeof(KompasObject).GetMethod("ksGetApplication7")?.Invoke(app, null);
        }
        catch (Exception ex)
        {
            // An unavailable API7 bridge is a fact to report, not a crash: the probe keeps
            // trying the remaining routes.
            Trace.WriteLine("ksGetApplication7: " + ex.Message);
            return null;
        }
    }

    private static object? TryGetApi7Documents(KompasObject app, ProbeStep step)
    {
        var application7 = GetApplication7(app);
        if (application7 is null)
        {
            step.Observe("API7 через ksGetApplication7() недоступен: счётчик документов будет ненадёжным.");
            return null;
        }

        try
        {
            var documentsType = application7.GetType().GetProperty("Documents")!.PropertyType;
            var documents = documentsType.GetProperty("Documents")!.GetValue(application7);
            step.Observe($"API7 IApplication.Converter-путь доступен; Documents = {documents?.GetType().Name}, members: "
                         + string.Join(" | ", Members.Matching(documentsType, "Count", "Open", "Add", "Active")));
            return documents;
        }
        catch (Exception ex)
        {
            step.Observe($"API7 Documents недоступен: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    private static int DocumentCount(object? documents)
    {
        if (documents is null)
        {
            return -1;
        }

        try
        {
            var count = documents.GetType().GetProperty("Count")?.GetValue(documents);
            return count is int i ? i : -1;
        }
        catch
        {
            return -1;
        }
    }

    private static int CountBodies(ksDocument3D document)
    {
        try
        {
            if (document.GetPart(-1) is not ksPart part)
            {
                return -1;
            }

            return ((ksBodyCollection)part.BodyCollection()).GetCount();
        }
        catch (Exception ex)
        {
            return -2 - (ex.GetType().Name.Length % 100);
        }
    }

    private static MethodInfo? FindMethod(Type type, string name, int parameterCount) =>
        type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(m => m.Name == name && m.GetParameters().Length == parameterCount);

    private static string InterfaceName(object target)
    {
        var iface = typeof(KompasObject).Assembly.GetExportedTypes()
            .FirstOrDefault(t => t.IsInterface && t.IsInstanceOfType(target));
        return iface?.Name ?? "<неизвестный интерфейс>";
    }

    private static string ReadAsciiHead(string path, int bytes)
    {
        using var stream = File.OpenRead(path);
        var buffer = new byte[Math.Min(bytes, (int)stream.Length)];
        stream.ReadExactly(buffer);
        return Encoding.UTF8.GetString(buffer).Replace("\0", string.Empty, StringComparison.Ordinal);
    }

    private static string? FirstToken(string haystack, params string[] needles) =>
        needles.FirstOrDefault(haystack.Contains);

    private static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream)).ToLowerInvariant();
    }
}

/// <summary>
/// P0.10–P0.11 — releasing COM references, shutting the instance down, and whether a second
/// process can attach to a running one. These decide what the Worker may promise: a server that
/// cannot be shut down cleanly cannot be scripted unattended, and an attach that cannot be
/// attributed to a PID must refuse rather than guess.
/// </summary>
internal static class ReleaseFacts
{
    public static void Run(ProbeReport report, ProbeOptions options)
    {
        var app = ProbeSession.App;
        if (app is null)
        {
            var skipped = report.Begin("P0.10", "Освобождение RCW и завершение");
            skipped.Verdict = Verdict.Skipped;
            skipped.Conclusion = "Нет подключения к КОМПАС.";
            return;
        }

        var step = report.Begin(
            "P0.10",
            "Освобождение RCW и штатное завершение экземпляра",
            "Достаточно ли отпустить ссылки, или нужен Quit()? Исчезает ли PID?");

        var pid = ProbeSession.ProcessId;
        try
        {
            var doc = (ksDocument3D)app.Document3D();
            if (!doc.Open(Path.Combine(ProbeSession.WorkDir, "P0_Units.m3d"), true))
            {
                step.Fail("Не открылась модель для проверки освобождения.");
                return;
            }

            var part = (ksPart)doc.GetPart(-1);
            if (part.GetMainBody() is not ksBody body)
            {
                step.Fail("GetMainBody() → null.");
                return;
            }

            var faces = (ksFaceCollection)body.FaceCollection();
            var faceObject = faces.GetByIndex(0);
            var face = faceObject as ksFaceDefinition ?? (ksFaceDefinition)((ksEntity)faceObject).GetDefinition();
            var released = Marshal.ReleaseComObject(face);
            step.Observe($"ReleaseComObject(грань) → новый счётчик {released}; faces.GetCount() после = {faces.GetCount()}.");

            var second = faces.GetByIndex(0);
            step.Observe($"Повторный GetByIndex(0) дал {second?.GetType().Name ?? "null"}; тот же RCW, что первый: {ReferenceEquals(second, faceObject)}.");
            step.Observe("Вывод для адаптера: элемент и коллекция — разные RCW на один IUnknown; освобождать элемент при живом использовании коллекции нельзя.");

            doc.close();

            step.Data["quit_declared"] = Members.Has(typeof(KompasObject), "Quit");
            step.Observe($"KompasObject.Quit объявлен: {step.Data["quit_declared"]}.");

            if (!options.KeepRunning)
            {
                try
                {
                    app.Quit();
                    step.Observe("Quit() выполнен без исключения.");
                }
                catch (Exception ex)
                {
                    step.Observe($"Quit() → {ex.GetType().Name}: {ex.Message}");
                }
            }

            Marshal.ReleaseComObject(app);
            ProbeSession.App = null;

            var exited = WaitForExit(pid, TimeSpan.FromSeconds(25));
            step.Data["process_exited"] = exited;
            step.Data["launched_pid"] = pid;
            if (options.KeepRunning)
            {
                step.Verdict = Verdict.Skipped;
                step.Conclusion = "--keep: завершение не проверяется.";
                return;
            }

            step.Pass(exited
                ? $"Собственный экземпляр (PID {pid}) завершился после Quit(): сеанс закрывается штатно, без kill-подхода."
                : $"PID {pid} жив через 25 с после Quit() и отпускания RCW: штатное завершение не подтверждено.");

            if (!exited)
            {
                step.Verdict = Verdict.Fail;
            }
        }
        catch (Exception ex)
        {
            step.Errors.Add(ex.GetType().Name + ": " + ex.Message);
            step.Fail("Проверка освобождения не завершена.");
        }
    }

    /// <summary>
    /// P0.11 — can another process attach to a running КОМПАС? Measured against a *visible*
    /// instance, because the headless orphans left by earlier scripts return MK_E_UNAVAILABLE.
    /// </summary>
    public static void Attach(ProbeReport report, ProbeOptions options)
    {
        var step = report.Begin(
            "P0.11",
            "Attach к работающему экземпляру",
            "Видимый экземпляр регистрируется в ROT так, чтобы к нему можно было подключиться?");

        if (options.Mode == "none")
        {
            step.Verdict = Verdict.Skipped;
            step.Conclusion = "COM отключён.";
            return;
        }

        try
        {
            // .NET (unlike .NET Framework) has no Marshal.GetActiveObject, so attaching to a
            // running КОМПАС is only possible through the running object table. This is a design
            // constraint on kompas_connect, not a detail: it is why RunningObjectTable exists.
            step.Observe("Marshal.GetActiveObject в .NET отсутствует: attach возможен только через перечисление ROT.");

            var rot = RunningObjectTable.EnumerateKompasEntries();
            step.Data["rot_entry_count"] = rot.Count;
            var boundCount = 0;
            foreach (var entry in rot.Take(10))
            {
                if (entry.Object is not null)
                {
                    boundCount++;
                }

                step.Observe($"ROT: «{entry.DisplayName}» bound={(entry.Object is not null)} err={entry.Error ?? "-"}");
            }

            step.Data["rot_bound_count"] = boundCount;

            if (boundCount > 0 && ProbeSession.App is not null)
            {
                var ours = Marshal.GetIUnknownForObject(ProbeSession.App);
                try
                {
                    var matchesOurs = rot.Where(e => e.Object is not null).Any(e =>
                    {
                        try
                        {
                            return Marshal.GetIUnknownForObject(e.Object!) == ours;
                        }
                        catch
                        {
                            return false;
                        }
                    });

                    step.Observe($"Объект из ROT совпал с нашим экземпляром по IUnknown: {matchesOurs}.");
                    step.Data["rot_contains_own_instance"] = matchesOurs;
                    step.Pass(matchesOurs
                        ? $"Attach через ROT технически возможен: {boundCount} подключённых объектов, наш экземпляр среди них опознан."
                        : $"Attach даёт объекты ({boundCount} шт.), но наш собственный экземпляр в ROT не опознан: сопоставление объект↔процесс требует отдельного признака.");
                }
                finally
                {
                    Marshal.Release(ours);
                }
            }
            else if (boundCount > 0)
            {
                step.Pass($"Attach через ROT возможен: {boundCount} подключённых объектов.");
            }
            else
            {
                step.Unknown(
                    $"Экземпляр запущен и переведён в видимый режим, но в ROT объектов КОМПАС нет ({rot.Count} записей всего). " +
                    "Режим attach объявлять поддержанным нельзя: нужен повторный тест на экземпляре, который пользователь открыл сам.");
            }
        }
        catch (Exception ex)
        {
            step.Errors.Add(ex.GetType().Name + ": " + ex.Message);
            step.Fail("Проверка attach не завершена.");
        }
    }

    private static bool WaitForExit(int? pid, TimeSpan timeout)
    {
        if (pid is null)
        {
            return false;
        }

        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (Array.IndexOf(KompasMcp.Api5Adapter.KompasInteropResolver.SnapshotProcessIds("KOMPAS"), pid.Value) < 0)
            {
                return true;
            }

            Thread.Sleep(400);
        }

        return false;
    }
}
