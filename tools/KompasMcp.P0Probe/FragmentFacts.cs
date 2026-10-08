using System.Globalization;
using Kompas6API5;
using KompasMcp.Api5Adapter;

namespace KompasMcp.P0Probe;

/// <summary>FRW — build the REFERENCE fragment file (<c>.frw</c>) that the fragment-insertion tool is
/// accepted against, by a documented route.</summary>
/// <remarks>
/// DOC: <c>ksdocumentparam_type.html</c> / <c>doctype.html</c> — <c>lt_DocFragment = 3</c> is the
/// document type of a fragment; <c>ksDocument2D::ksCreateDocument</c> creates it and
/// <c>ksDocument2D::ksSaveDocument</c> writes the file. The contour is drawn with the documented
/// <c>ksLineSeg</c>, so its area is known analytically: a regular 12-gon of radius 25 mm,
/// <c>0,5·12·25²·sin(30°)</c> = 1875 mm².
/// WHY A PROBE AND NOT THE SERVER: the server refuses to create a fragment document (it belongs to no
/// order of the coverage plan), and its only 2D drawing route is the sketch editor — so the reference
/// artefact is built here, once, and committed next to the acceptance data.
/// History: docs/decisions/probes.md#fragment-reference
/// </remarks>
internal static class FragmentFacts
{
    private const short DocTypeFragment = 3;

    private const int Sides = 12;

    private const double Radius = 25.0;

    /// <summary>Area of the reference contour, mm² — the analytic figure the file is built to carry.</summary>
    public static double ReferenceAreaMm2 => 0.5 * Sides * Radius * Radius * Math.Sin(2 * Math.PI / Sides);

    public static void Run(ProbeReport report, ProbeOptions options)
    {
        var app = ProbeSession.App;
        if (app is null)
        {
            var skipped = report.Begin("FRW.0", "Эталонный файл фрагмента");
            skipped.Verdict = Verdict.Skipped;
            skipped.Conclusion = "Нет подключения к КОМПАС: шаг пропущен.";
            return;
        }

        var step = report.Begin(
            "FRW.1",
            "Создание .frw с замкнутым контуром известной площади",
            "Строится ли фрагмент документированным маршрутом и читается ли файл обратно?");

        var path = Path.Combine(ProbeSession.WorkDir, "g2-reference-contour.frw");
        try
        {
            var param = app.GetParamStruct(KompasStructTypes.DocumentParam) as ksDocumentParam;
            if (param is null)
            {
                step.Fail("KompasObject.GetParamStruct(ko_DocumentParam) не вернул ksDocumentParam.");
                return;
            }

            param.Init();
            param.type = DocTypeFragment;
            param.regime = 1;
            param.comment = "G2 reference contour";

            var document = app.Document2D() as ksDocument2D;
            if (document is null)
            {
                step.Fail("KompasObject.Document2D() не вернул ksDocument2D.");
                return;
            }

            if (!document.ksCreateDocument(param))
            {
                step.Fail("ksDocument2D.ksCreateDocument(lt_DocFragment) вернул false.");
                return;
            }

            step.Observe("Документ фрагмента создан (lt_DocFragment = {0}).", DocTypeFragment);

            for (var i = 0; i < Sides; i++)
            {
                var a0 = 2 * Math.PI * i / Sides;
                var a1 = 2 * Math.PI * (i + 1) / Sides;
                document.ksLineSeg(
                    Radius * Math.Cos(a0), Radius * Math.Sin(a0),
                    Radius * Math.Cos(a1), Radius * Math.Sin(a1), 1);
            }

            step.Observe("Контур: правильный {0}-угольник R = {1} мм, площадь {2} мм².",
                Sides, Radius, ReferenceAreaMm2.ToString("R", CultureInfo.InvariantCulture));

            var saved = document.ksSaveDocument(path);
            step.Data["path"] = path;
            step.Data["saved"] = saved;
            step.Data["exists"] = File.Exists(path);
            step.Data["area_mm2"] = ReferenceAreaMm2;

            if (!saved || !File.Exists(path))
            {
                step.Fail("ksSaveDocument не записал файл фрагмента.");
                return;
            }

            var info = new FileInfo(path);
            step.Data["byte_length"] = info.Length;
            step.Artifacts.Add(path);
            step.Pass($"Файл фрагмента записан: {info.Length} байт.");
        }
        catch (Exception ex)
        {
            step.Fail($"Создание фрагмента бросило {ex.GetType().Name}: {ex.Message}");
        }
    }
}
