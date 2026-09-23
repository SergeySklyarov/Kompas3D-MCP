using System.Diagnostics;
using Kompas6API5;
using Kompas6Constants3D;

namespace KompasMcp.Api7Probe;

/// <summary>
/// Проверка готового файла: открыть <c>.m3d</c> в НОВОМ сеансе и прочитать его геометрию.
/// </summary>
/// <remarks>
/// <para>
/// <b>Зачем это нужно отдельно от опыта F.</b> Опыт F строит признак и сам же его измеряет. Это
/// законная проба маршрута, но она не отвечает на вопрос «что лежит в файле»: если зонд ошибся в
/// чтении или мерил не тот документ, опыт этого не покажет (класс дефекта 14 — измерение после
/// предмета). Сохранённый файл читает <b>другой процесс</b>, который не знает, как файл строился, и
/// которому нечем ошибиться: он видит только то, что записала КОМПАС.
/// </para>
/// <para>
/// <b>Почему это не «проба ради пробы».</b> Утверждение, которое готовится к записи в адаптер,
/// звучит как «маршрут полного оборота даёт полный цилиндр». Пока оно подтверждено только тем же
/// инструментом, что его построил, — это утверждение о зонде. Открытие файла сторонним читателем
/// переводит его в утверждение о продукте.
/// </para>
/// <para>
/// <b>Ожидания задаются ДО чтения</b>, а не подбираются под результат: R20 H40, полный оборот —
/// весь цилиндр <c>V = π·20²·40 = 50265.4824574366…</c> мм³, габарит 40×40×40.
/// </para>
/// </remarks>
internal sealed class M3dVerificationProbe
{
    private const double RadiusMm = 20d;
    private const double HeightMm = 40d;

    private static double FullTurnVolume => Math.PI * RadiusMm * RadiusMm * HeightMm;

    private static double HalfTurnVolume => FullTurnVolume / 2d;

    private static double Tolerance(double expectation) => Math.Max(0.01d, 1e-6d * Math.Abs(expectation));

    private readonly ProbeReport _report;
    private readonly Options _options;
    private readonly HashSet<int> _pidsBefore = new();

    private KompasObject _app = null!;
    private int _ownPid;

    public M3dVerificationProbe(ProbeReport report, Options options)
    {
        _report = report;
        _options = options;
    }

    public static void Flush(ProbeReport report, Options options)
    {
        report.Flush(
            Path.Combine(options.ReportDir, "m3d-verification.json"),
            Path.Combine(options.ReportDir, "m3d-verification.md"));
    }

    public void Run(string path)
    {
        foreach (var process in Process.GetProcessesByName("KOMPAS"))
        {
            _pidsBefore.Add(process.Id);
            process.Dispose();
        }

        Launch();
        try
        {
            Verify(path);
        }
        finally
        {
            Shutdown();
        }
    }

    private void Launch()
    {
        var step = _report.Begin("V.0", "Новый сеанс КОМПАС для чтения файла",
            "Читает ли файл процесс, который его не строил?");
        var clock = Stopwatch.StartNew();
        _app = (KompasObject)Activator.CreateInstance(Type.GetTypeFromProgID("KOMPAS.Application.5")!)!;
        _app.Visible = false;
        _ownPid = Process.GetProcessesByName("KOMPAS")
            .Select(p =>
            {
                var pid = p.Id;
                p.Dispose();
                return pid;
            })
            .FirstOrDefault(pid => !_pidsBefore.Contains(pid));
        clock.Stop();

        step.Observe("создан экземпляр API5 за " + clock.ElapsedMilliseconds + " мс");
        step.Observe("свой процесс: " + (_ownPid == 0 ? "не определён" : _ownPid.ToString()));
        step.Pass("сеанс поднят");
    }

    private void Verify(string path)
    {
        var step = _report.Begin("V.1", "Геометрия сохранённого файла",
            "Что на самом деле записала КОМПАС — полный цилиндр или половину?");

        // The expectations are stated before anything is opened, so a reading can only confirm or
        // refute them; it cannot define them.
        step.Observe("ожидание ДО чтения: полный оборот R20 H40 → V=" + Api5.Num(FullTurnVolume)
            + ", габарит 40×40×40; полуоборот → V=" + Api5.Num(HalfTurnVolume) + ", габарит 40×40×20");

        if (!File.Exists(path))
        {
            step.Fail("Файл не найден: " + path);
            return;
        }

        var info = new FileInfo(path);
        step.Observe("файл: " + info.FullName + ", " + info.Length + " байт, изменён "
            + info.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss"));

        ksDocument3D? doc = null;
        try
        {
            // Opening a file is not creating a document: Open(path, true) with no prior Create is the
            // measured route (probe defect 7 was asking the wrong thing here).
            doc = (ksDocument3D)_app.Document3D();
            if (!doc.Open(path, true))
            {
                step.Fail("Open()=False");
                return;
            }

            var part = doc.GetPart(-1) as ksPart;
            if (part is null)
            {
                step.Fail("деталь не получена");
                return;
            }

            var volume = Api5.Volume(part);
            var bodies = Api5.BodyCount(part);

            // The tree is read as well: the claim being verified is not only "there is a mass" but
            // "there is a NATIVE rotation feature there". A mass without the feature would be a
            // different product claim.
            var features = ReadFeatures(doc, step);

            var classified = ClassifyVolumeOf(volume);
            step.Observe("прочитано из файла: тел=" + bodies + ", V=" + Api5.Num(volume)
                + " (" + classified + ")");
            step.Observe("форма: " + DescribeShape(part, step));

            var verdict = volume is not null && Math.Abs(volume.Value - FullTurnVolume) <= Tolerance(FullTurnVolume);
            var isHalf = volume is not null && Math.Abs(volume.Value - HalfTurnVolume) <= Tolerance(HalfTurnVolume);

            if (verdict)
            {
                step.Pass("файл содержит ПОЛНЫЙ цилиндр R20 H40 — маршрут полного оборота записан в модель");
            }
            else if (isHalf)
            {
                step.Fail("файл содержит ПОЛОВИНУ цилиндра: записанный угол не дал полного оборота");
            }
            else
            {
                step.Fail("объём не совпал ни с полным цилиндром, ни с половиной");
            }

            step.Data["volume"] = volume;
            step.Data["bodies"] = bodies;
            step.Data["features"] = features;
            step.Data["path"] = path;
        }
        catch (Exception ex)
        {
            step.Fail("чтение файла бросило " + ex.GetType().Name + ": " + ex.Message);
            step.Errors.Add(ex.ToString());
        }
        finally
        {
            try
            {
                doc?.close();
            }
            catch (Exception)
            {
                // Not this step's subject.
            }
        }
    }

    /// <summary>
    /// Reads the feature tree so the verification states what KIND of object carries the mass, not
    /// only how much mass there is.
    /// </summary>
    private static List<string> ReadFeatures(ksDocument3D doc, ProbeStep step)
    {
        var found = new List<string>();
        try
        {
            if (doc.GetPart(-1) is not ksPart part || part.GetFeature() is not ksFeature root)
            {
                step.Observe("дерево: part.GetFeature() не дал корень");
                return found;
            }

            // The tree walk is the measured route (`SubFeatureCollection(true, false)`), not a second
            // way of asking the same question.
            if (root.SubFeatureCollection(true, false) is not ksFeatureCollection tree)
            {
                step.Observe("дерево: SubFeatureCollection(true,false) вернул не коллекцию");
                return found;
            }

            for (var i = 0; i < tree.GetCount(); i++)
            {
                var element = tree.GetByIndex(i);
                // `ksFeature` from this collection was measured to refuse `as ksEntity`, and it has no
                // Name member; the measured way to name a tree element is RuntimeName (R.5).
                found.Add("[" + i + "] " + Api5.RuntimeName(element));
            }

            step.Observe("элементов в дереве: " + found.Count
                + (found.Count == 0 ? string.Empty : " — " + string.Join(", ", found)));
        }
        catch (Exception ex)
        {
            step.Observe("чтение дерева бросило " + ex.GetType().Name + ": " + ex.Message);
        }

        return found;
    }

    private static string DescribeShape(ksPart part, ProbeStep step)
    {
        try
        {
            if (part.GetMainBody() is not ksBody body)
            {
                return "тела нет";
            }

            var bounds = body.GetGabarit(out var x1, out var y1, out var z1, out var x2, out var y2, out var z2)
                ? "габарит x[" + Api5.Num(x1) + "," + Api5.Num(x2) + "] y[" + Api5.Num(y1) + ","
                    + Api5.Num(y2) + "] z[" + Api5.Num(z1) + "," + Api5.Num(z2) + "]"
                : "габарит не прочитан";

            var cylinders = new List<string>();
            if (body.FaceCollection() is ksFaceCollection faces)
            {
                for (var f = 0; f < faces.GetCount(); f++)
                {
                    if (faces.GetByIndex(f) is not ksFaceDefinition face)
                    {
                        continue;
                    }

                    try
                    {
                        if (face.GetSurface() is ksSurface surface && surface.IsCylinder()
                            && surface.GetSurfaceParam() is ksCylinderParam cylinder)
                        {
                            cylinders.Add("r=" + Api5.Num(cylinder.radius) + " h=" + Api5.Num(cylinder.height));
                        }
                    }
                    catch (System.Runtime.InteropServices.COMException)
                    {
                        // A face whose parameters КОМПАС withheld is not a reason to abort the walk.
                    }
                }
            }

            return "цилиндрических граней " + cylinders.Count
                + (cylinders.Count == 0 ? string.Empty : " (" + string.Join("; ", cylinders) + ")")
                + ", " + bounds;
        }
        catch (Exception ex)
        {
            step.Observe("чтение формы бросило " + ex.GetType().Name + ": " + ex.Message);
            return "форма не прочитана";
        }
    }

    private static string ClassifyVolumeOf(double? volume)
    {
        if (volume is null)
        {
            return "не прочитано (тела нет?)";
        }

        if (Math.Abs(volume.Value - FullTurnVolume) <= Tolerance(FullTurnVolume))
        {
            return "ПОЛНЫЙ цилиндр π·r²·h="
                + FullTurnVolume.ToString("0.############", System.Globalization.CultureInfo.InvariantCulture);
        }

        if (Math.Abs(volume.Value - HalfTurnVolume) <= Tolerance(HalfTurnVolume))
        {
            return "ПОЛОВИНА цилиндра π·r²·h/2";
        }

        return "ни полный, ни половина";
    }

    private void Shutdown()
    {
        var step = _report.Begin("V.Z", "Завершение сеанса",
            "Свой процесс уходит после Quit()?");
        try
        {
            _app.Quit();
        }
        catch (Exception ex)
        {
            step.Observe("Quit() бросил " + ex.GetType().Name + ": " + ex.Message);
        }

        // `Quit()` is asynchronous: the local server may still be winding down when the call returns.
        // Sampling the process list once therefore reports a teardown race as "the process outlived
        // Quit()" — the instrument describing itself instead of the product. The honest measurement is
        // to wait for the process to leave, with a bounded timeout, and to distinguish "still there"
        // from "did not leave at all".
        var waited = 0;
        const int stepMs = 250;
        const int limitMs = 10000;
        while (waited < limitMs && OwnProcessAlive())
        {
            System.Threading.Thread.Sleep(stepMs);
            waited += stepMs;
        }

        if (OwnProcessAlive())
        {
            step.Fail("свой процесс " + _ownPid + " пережил Quit() и не ушёл за " + (limitMs / 1000) + " с");
        }
        else
        {
            step.Pass("свой процесс завершён" + (waited == 0 ? string.Empty : " через " + waited + " мс")
                + ", чужие не тронуты");
        }
    }

    private bool OwnProcessAlive()
    {
        if (_ownPid == 0)
        {
            return false;
        }

        foreach (var p in Process.GetProcessesByName("KOMPAS"))
        {
            var pid = p.Id;
            p.Dispose();
            if (pid == _ownPid)
            {
                return true;
            }
        }

        return false;
    }
}
