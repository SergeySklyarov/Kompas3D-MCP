using System.Globalization;
using KompasMcp.Api5Adapter;
using KompasMcp.Contracts;
using KompasMcp.Contracts.Ipc;

namespace KompasMcp.P0Probe;

/// <summary>MAL — does the mate alignment change the component ORIENTATION, or only a stored number?</summary>
/// <remarks>WHY: the acceptance rows recorded the second component's TRANSLATION only, and a translation
/// cannot tell a mate oriented the other way from the one requested. This suite drives the PRODUCTION
/// adapter directly — not raw COM, not the Host — so that values the Host's measured map rejects can be
/// measured without editing that map to let a row pass. It measures the second component's full 4x4
/// matrix before and after, and <c>s = n1_asm . n2_asm</c> for the two mated faces, with the normals READ
/// from the source part. TWO PLACEMENTS — identity and 180 deg about X — so "closest" and a fixed value
/// cannot coincide by accident. History: docs/decisions/mates.md#alignment-orientation</remarks>
internal static class MateAlignFacts
{
    private const double PlateWidth = 100d;
    private const double PlateHeight = 80d;
    private const double PlateThickness = 10d;

    /// <summary>How close to +/-1 the dot product must be to call the faces aligned/anti-aligned.</summary>
    private const double DotTolerance = 0.999d;

    /// <summary>Dot product of the two mated face normals when the two placements are built.</summary>
    private static readonly string[] Placements = { "identity", "rot180x" };

    private static readonly (string Name, string Type, double? Param)[] Cases =
    {
        ("coincidence", "coincidence", null),
        ("parallel", "parallel", null),
        ("distance", "distance", 50d),
    };

    private static readonly string[] Values = { "opposite", "cooriented", "closest" };

    public static void Run(ProbeReport report, ProbeOptions options)
    {
        var step = report.Begin(
            "MAL.0",
            "Ориентация сопряжения: s = n1·n2 для запрошенного выравнивания",
            "Совпадает ли ориентация граней с перечитанным числом выравнивания, или число хранится отдельно от геометрии?");

        using var session = new Api5Session();
        ApplicationEntry? application = null;
        try
        {
            application = session.Connect(new ConnectCommand { Mode = ConnectMode.Launch });
            step.Observe($"сеанс: app={application.Id}, pid={application.ProcessId}, ownership={application.Ownership}.");

            var work = Path.Combine(options.WorkDir, "mate-align");
            Directory.CreateDirectory(work);
            var sourcePath = Path.Combine(work, "mate-align-source.m3d");

            var source = BuildSourcePart(session, application.Id, sourcePath, report, step);
            if (source is null)
            {
                return;
            }

            var faces = ReadFaceIndices(session, application.Id, sourcePath, report, step);
            if (faces is null)
            {
                step.Fail("грани для измерения не найдены по нормали — измерять нечего.");
                return;
            }

            var rows = new List<object>();
            foreach (var placement in Placements)
            {
                foreach (var (caseName, ctype, param) in Cases)
                {
                    foreach (var value in Values)
                    {
                        rows.Add(Measure(session, application.Id, sourcePath, faces.Value, placement,
                            caseName, ctype, param, value, report, step));
                    }
                }
            }

            step.Data["rows"] = rows;
            step.Data["dot_tolerance"] = DotTolerance;
            step.Pass($"измерено сочетаний: {rows.Count} ({Placements.Length} постановки x {Cases.Length} типа x {Values.Length} значения)");

            // The frame question is measured AFTER the table: the table does not depend on it, and a
            // failure here must not invalidate the orientation measurements already taken.
            MeasureNormalFrame(session, application.Id, sourcePath, report);
        }
        catch (Exception ex)
        {
            step.Errors.Add(ex.GetType().Name + ": " + ex.Message);
            step.Fail("Измерение не завершено.");
        }
        finally
        {
            if (application is not null)
            {
                session.Disconnect(application.Id, closeOwnedApplication: true);
            }
        }
    }

    /// <summary>Builds and saves the source plate 100x80x10 — the same geometry the acceptance rows use.</summary>
    private static string? BuildSourcePart(
        Api5Session session, string applicationId, string path, ProbeReport report, ProbeStep parent)
    {
        var step = report.Begin("MAL.1", "Деталь-источник: плита 100x80x10 на диске",
            "Есть ли файл, из которого вставлять компонент?");

        try
        {
            var document = session.CreateDocument(new CreateDocumentCommand
            {
                ApplicationId = applicationId,
                Kind = DocumentKind.Part,
                Name = "MAL-src",
            });

            var sketch = session.CreateSketch(new CreateSketchCommand
            {
                DocumentId = document.Id,
                Plane = new PlaneRefDto { Base = PlaneBase.Xy, OffsetMm = 0 },
                Name = "mal-sketch",
                ExpectedRevision = document.Revision,
            });

            session.EditSketch(new EditSketchCommand
            {
                SketchRef = sketch.Id,
                Mode = SketchEditMode.Append,
                ExpectedRevision = document.Revision,
                Entities = new[]
                {
                    new SketchEntityDto
                    {
                        Kind = SketchEntityKind.Rectangle,
                        StartMm = new double[] { -PlateWidth / 2, -PlateHeight / 2 },
                        WidthMm = PlateWidth,
                        HeightMm = PlateHeight,
                    },
                },
            });

            session.FinishSketch(new FinishSketchCommand { SketchRef = sketch.Id, RequireClosedProfile = false });

            var extrude = session.Extrude(new ExtrudeCommand
            {
                SketchRef = sketch.Id,
                Operation = ExtrudeOperation.Base,
                DepthMm = PlateThickness,
                Direction = ExtrudeDirection.Positive,
                ExpectedRevision = document.Revision,
            });

            var volume = extrude.VolumeMm3;
            var expected = PlateWidth * PlateHeight * PlateThickness;
            step.Data["volume_mm3"] = volume;
            step.Data["analytic_mm3"] = expected;
            step.Observe($"объём: {volume?.ToString("0.####", CultureInfo.InvariantCulture) ?? "null"} "
                + $"(аналитика {expected.ToString(CultureInfo.InvariantCulture)})");

            session.SaveDocument(document, path);
            session.CloseDocument(document, DirtyPolicy.Discard);

            if (!File.Exists(path))
            {
                step.Fail("файл-источник не записан: " + path);
                return null;
            }

            step.Pass("источник сохранён: " + path);
            return path;
        }
        catch (Exception ex)
        {
            step.Errors.Add(ex.GetType().Name + ": " + ex.Message);
            step.Fail("источник не построен.");
            return null;
        }
    }

    /// <summary>Face index and part-local normal of the +Z and -Z planes, found BY NORMAL, not assumed.</summary>
    private static (int PlusZ, double[] PlusZNormal, int MinusZ, double[] MinusZNormal)? ReadFaceIndices(
        Api5Session session, string applicationId, string sourcePath, ProbeReport report, ProbeStep parent)
    {
        var step = report.Begin("MAL.2", "Грани выбраны ПО НОРМАЛИ (верх +Z, низ -Z)",
            "Какие номера в FaceCollection соответствуют верхней и нижней граням плиты?");

        try
        {
            var document = session.OpenDocument(new OpenDocumentCommand
            {
                ApplicationId = applicationId,
                Path = sourcePath,
                Access = DocumentAccess.ReadOnly,
            });

            var bodies = session.ListBodies(new ListBodiesCommand { DocumentId = document.Id });
            int? plusZ = null;
            int? minusZ = null;
            double[]? plusZNormal = null;
            double[]? minusZNormal = null;
            if (bodies.Count > 0)
            {
                var topology = session.ReadTopology(new ReadTopologyCommand
                {
                    DocumentId = document.Id,
                    BodyRef = bodies[0].BodyRef,
                    Include = "faces",
                });

                for (var index = 0; index < topology.Faces.Count; index++)
                {
                    var face = topology.Faces[index];
                    var normal = face.NormalAtCenter;
                    if (face.SurfaceType != "plane" || normal is null || normal.Count < 3)
                    {
                        continue;
                    }

                    if (Math.Abs(normal[2] - 1d) <= 1e-6 && plusZ is null)
                    {
                        plusZ = index;
                        plusZNormal = new[] { normal[0], normal[1], normal[2] };
                    }

                    if (Math.Abs(normal[2] + 1d) <= 1e-6 && minusZ is null)
                    {
                        minusZ = index;
                        minusZNormal = new[] { normal[0], normal[1], normal[2] };
                    }
                }
            }

            session.CloseDocument(document, DirtyPolicy.Discard);

            step.Data["plus_z_face"] = plusZ;
            step.Data["minus_z_face"] = minusZ;
            step.Data["plus_z_normal"] = plusZNormal;
            step.Data["minus_z_normal"] = minusZNormal;
            step.Observe($"нормаль +Z -> грань {plusZ?.ToString(CultureInfo.InvariantCulture) ?? "не найдена"} "
                + $"{Describe(plusZNormal)}, нормаль -Z -> грань "
                + $"{minusZ?.ToString(CultureInfo.InvariantCulture) ?? "не найдена"} {Describe(minusZNormal)}");
            step.Observe("индекс = позиция в FaceCollection (GetByIndex): тот же маршрут, что у строк приёмки");

            if (plusZ is null || minusZ is null || plusZNormal is null || minusZNormal is null)
            {
                step.Fail("одна из граней по нормали не найдена.");
                return null;
            }

            step.Pass($"грани найдены: +Z {plusZ}, -Z {minusZ}");
            return (plusZ.Value, plusZNormal, minusZ.Value, minusZNormal);
        }
        catch (Exception ex)
        {
            step.Errors.Add(ex.GetType().Name + ": " + ex.Message);
            step.Fail("чтение граней не выполнено.");
            return null;
        }
    }

    /// <summary>Is a component face's normal read in the PART frame or in the ASSEMBLY frame?</summary>
    /// <remarks>WHY: a product that reports the mated faces' orientation must know whether to apply the
    /// component's rotation to the normal it reads. A box's normal set is invariant under every
    /// axis-aligned rotation, so the component is turned by 30 deg about X: in the part frame the
    /// normals stay axis-aligned, in the assembly frame they tilt. The measurement is decisive either way.
    /// </remarks>
    private static void MeasureNormalFrame(
        Api5Session session, string applicationId, string sourcePath, ProbeReport report)
    {
        var step = report.Begin("MAL.4", "Нормаль грани компонента: в координатах детали или сборки?",
            "Нужно ли применять поворот компонента к нормали, прочитанной у грани компонента?");

        DocumentEntry? assembly = null;
        try
        {
            assembly = session.CreateDocument(new CreateDocumentCommand
            {
                ApplicationId = applicationId,
                Kind = DocumentKind.Assembly,
                Name = "MAL-frame",
            });

            for (var index = 0; index < 2; index++)
            {
                session.InsertComponent(new InsertComponentCommand
                {
                    DocumentId = assembly.Id,
                    ExpectedRevision = assembly.Revision,
                    SourcePath = sourcePath,
                    Fixed = false,
                });
            }

            var rows = session.ListComponents(new ListComponentsCommand { DocumentId = assembly.Id }).Components;
            if (rows.Count != 2)
            {
                step.Fail($"компонентов {rows.Count}, ожидалось 2.");
                return;
            }

            const double angle = 30d * Math.PI / 180d;
            session.SetComponentPlacement(new SetComponentPlacementCommand
            {
                DocumentId = assembly.Id,
                ExpectedRevision = assembly.Revision,
                ComponentRef = rows[1].ComponentRef,
                Transform = new TransformDto
                {
                    OriginMm = new double[] { 150, 0, 0 },
                    XAxis = new double[] { 1, 0, 0 },
                    YAxis = new double[] { 0, Math.Cos(angle), Math.Sin(angle) },
                },
            });

            var normals = PlanarNormalsOfComponent(assembly, 1);
            var matrix = session.ListComponents(new ListComponentsCommand { DocumentId = assembly.Id })
                .Components[1].Matrix;

            step.Data["component_normals"] = normals;
            step.Data["component_matrix"] = matrix;
            step.Observe("нормали плоских граней компонента (поворот 30 град вокруг X): "
                + string.Join(", ", normals.Select(Describe)));

            var axisAligned = normals.Count > 0 && normals.All(n =>
                Math.Abs(Math.Abs(n[0]) + Math.Abs(n[1]) + Math.Abs(n[2]) - 1d) < 1e-6);
            step.Data["part_frame"] = axisAligned;
            if (axisAligned)
            {
                step.Pass("нормали остались осевыми -> нормаль читается в КООРДИНАТАХ ДЕТАЛИ, "
                    + "поворот компонента к ней ПРИМЕНЯТЬ");
            }
            else
            {
                step.Pass("нормали наклонились вместе с компонентом -> нормаль читается в КООРДИНАТАХ "
                    + "СБОРКИ, поворот компонента к ней применять НЕЛЬЗЯ");
            }
        }
        catch (Exception ex)
        {
            step.Errors.Add(ex.GetType().Name + ": " + ex.Message);
            step.Fail("рамка нормали не измерена.");
        }
        finally
        {
            if (assembly is not null)
            {
                try
                {
                    session.CloseDocument(assembly, DirtyPolicy.Discard);
                }
                catch (Exception ex)
                {
                    step.Observe("закрытие сборки бросило: " + ex.GetType().Name);
                }
            }
        }
    }

    /// <summary>Normals of the component's planar faces, read through the documented
    /// <c>ksPart.BodyCollection() → ksBody.FaceCollection() → ksFaceDefinition.GetSurface()</c>.</summary>
    private static List<double[]> PlanarNormalsOfComponent(DocumentEntry assembly, int ordinal)
    {
        var normals = new List<double[]>();
        if (assembly.Document3D.PartCollection(true) is not Kompas6API5.ksPartCollection parts
            || ordinal >= parts.GetCount()
            || parts.GetByIndex(ordinal) is not Kompas6API5.ksPart component
            || component.BodyCollection() is not Kompas6API5.ksBodyCollection bodies
            || bodies.GetCount() == 0
            || bodies.GetByIndex(0) is not Kompas6API5.ksBody body
            || body.FaceCollection() is not Kompas6API5.ksFaceCollection faces)
        {
            return normals;
        }

        for (var index = 0; index < faces.GetCount(); index++)
        {
            if (faces.GetByIndex(index) is not Kompas6API5.ksFaceDefinition face || !face.IsPlanar())
            {
                continue;
            }

            if (face.GetSurface() is not Kompas6API5.ksSurface surface)
            {
                continue;
            }

            var u = (surface.GetParamUMin() + surface.GetParamUMax()) / 2d;
            var v = (surface.GetParamVMin() + surface.GetParamVMax()) / 2d;
            if (!surface.GetNormal(u, v, out var x, out var y, out var z))
            {
                continue;
            }

            normals.Add(face.normalOrientation ? new[] { x, y, z } : new[] { -x, -y, -z });
        }

        return normals;
    }

    private static string Describe(double[]? normal) => normal is null
        ? "[]"
        : "[" + string.Join(", ", normal.Select(v => Math.Round(v, 4).ToString(CultureInfo.InvariantCulture))) + "]";

    /// <summary>One measurement: build the assembly, mate, and read the orientation back.</summary>
    private static object Measure(
        Api5Session session, string applicationId, string sourcePath,
        (int PlusZ, double[] PlusZNormal, int MinusZ, double[] MinusZNormal) faces,
        string placement, string caseName, string ctype, double? param, string value,
        ProbeReport report, ProbeStep parent)
    {
        var id = $"MAL.3.{placement}.{caseName}.{value}";
        var step = report.Begin(id, $"Сопряжение {ctype} / {value} / постановка {placement}",
            "Какая ориентация получилась и что перечитано из выравнивания?");

        var record = new Dictionary<string, object?>
        {
            ["placement"] = placement,
            ["constraint_type"] = ctype,
            ["requested"] = value,
        };

        DocumentEntry? assembly = null;
        try
        {
            assembly = session.CreateDocument(new CreateDocumentCommand
            {
                ApplicationId = applicationId,
                Kind = DocumentKind.Assembly,
                Name = $"MAL-{placement}-{caseName}-{value}",
            });

            for (var index = 0; index < 2; index++)
            {
                session.InsertComponent(new InsertComponentCommand
                {
                    DocumentId = assembly.Id,
                    ExpectedRevision = assembly.Revision,
                    SourcePath = sourcePath,
                    Fixed = false,
                });
            }

            var rows = session.ListComponents(new ListComponentsCommand { DocumentId = assembly.Id }).Components;
            if (rows.Count != 2)
            {
                step.Fail($"компонентов {rows.Count}, ожидалось 2.");
                return record;
            }

            // THE SECOND COMPONENT'S ORIENTATION BEFORE THE MATE IS PART OF THE EXPERIMENT: on the
            // identity placement the mated normals are anti-aligned, on the rotated one they are
            // aligned, so "closest" and a fixed value must diverge between the two placements.
            var second = rows[1];
            var transform = placement == "rot180x"
                ? new TransformDto
                {
                    OriginMm = new double[] { 150, 0, 0 },
                    XAxis = new double[] { 1, 0, 0 },
                    YAxis = new double[] { 0, -1, 0 },
                }
                : new TransformDto
                {
                    OriginMm = new double[] { 150, 0, 0 },
                    XAxis = new double[] { 1, 0, 0 },
                    YAxis = new double[] { 0, 1, 0 },
                };

            session.SetComponentPlacement(new SetComponentPlacementCommand
            {
                DocumentId = assembly.Id,
                ExpectedRevision = assembly.Revision,
                ComponentRef = second.ComponentRef,
                Transform = transform,
            });

            var before = session.ListComponents(new ListComponentsCommand { DocumentId = assembly.Id }).Components;
            if (before.Count != 2)
            {
                step.Fail($"после размещения компонентов {before.Count}, ожидалось 2.");
                return record;
            }

            var (n1Local, n2Local) = (faces.PlusZNormal, faces.MinusZNormal);
            var sBefore = DotInAssembly(before[0].Matrix, before[1].Matrix, n1Local, n2Local);
            record["dot_before"] = Round(sBefore);
            record["rotation_before"] = Rotation(before[1].Matrix);
            record["translation_before"] = Translation(before[1].Matrix);

            string? read = null;
            string? errorCode = null;
            string? errorDetails = null;
            try
            {
                var created = session.CreateMate(new CreateMateCommand
                {
                    DocumentId = assembly.Id,
                    ExpectedRevision = assembly.Revision,
                    ConstraintType = ctype,
                    FirstComponentRef = before[0].ComponentRef,
                    FirstFaceIndex = faces.PlusZ,
                    SecondComponentRef = before[1].ComponentRef,
                    SecondFaceIndex = faces.MinusZ,
                    Alignment = value,
                    ParamValue = param,
                });
                read = created.AlignmentRead;
                record["mate_count"] = created.MateCount;
            }
            catch (KompasContractException ex)
            {
                errorCode = ex.Code;
                errorDetails = ex.Message;
                read = ex.Details is not null && ex.Details.TryGetValue("alignment_read", out var reported)
                    ? reported?.ToString()
                    : null;
                record["partial_effects"] = ex.PartialEffects;
            }

            // THE MATRIX IS READ AFTER A REFUSAL TOO: the mate was already created, so the orientation
            // it produced is a fact about the model even when the call is reported as a failure.
            var after = session.ListComponents(new ListComponentsCommand { DocumentId = assembly.Id }).Components;
            var sAfter = after.Count == 2
                ? DotInAssembly(after[0].Matrix, after[1].Matrix, n1Local, n2Local)
                : (double?)null;

            record["read"] = read;
            record["error_code"] = errorCode;
            record["error_message"] = errorDetails;
            record["dot_after"] = Round(sAfter);
            record["rotation_after"] = Rotation(after.Count == 2 ? after[1].Matrix : null);
            record["translation_after"] = Translation(after.Count == 2 ? after[1].Matrix : null);
            record["mate_count_after"] = session.ListMates(new ListMatesCommand { DocumentId = assembly.Id }).Mates.Count;
            record["dot_class_after"] = Classify(sAfter);
            record["rotation_changed"] = !RotationEqual(
                record["rotation_before"] as double[], record["rotation_after"] as double[]);

            step.Data["row"] = record;
            step.Observe($"запрошено={value}, перечитано={read ?? "не читается"}, "
                + $"s до={Round(sBefore)?.ToString(CultureInfo.InvariantCulture) ?? "?"}, "
                + $"s после={Round(sAfter)?.ToString(CultureInfo.InvariantCulture) ?? "?"} "
                + $"({record["dot_class_after"]}), поворот изменился={record["rotation_changed"]}, "
                + $"перенос {Translation(before[1].Matrix)} -> {Translation(after.Count == 2 ? after[1].Matrix : null)}, "
                + $"код={errorCode ?? "нет"}");
            step.Pass($"сочетание измерено: s={Round(sAfter)?.ToString(CultureInfo.InvariantCulture) ?? "не прочитан"}");
            return record;
        }
        catch (Exception ex)
        {
            step.Errors.Add(ex.GetType().Name + ": " + ex.Message);
            step.Fail("сочетание не измерено.");
            record["exception"] = ex.GetType().Name + ": " + ex.Message;
            return record;
        }
        finally
        {
            if (assembly is not null)
            {
                try
                {
                    session.CloseDocument(assembly, DirtyPolicy.Discard);
                }
                catch (Exception ex)
                {
                    step.Observe("закрытие сборки бросило: " + ex.GetType().Name);
                }
            }
        }
    }

    /// <summary>The dot product of the two face normals in ASSEMBLY coordinates.</summary>
    /// <remarks>The assembly normal is the component's rotation block applied to the part-local normal:
    /// an arithmetic combination of two documented reads (<c>IPart7.GetSummMatrix</c> and the face
    /// normal), not a new API route. The layout is the measured <c>[X,0][Y,0][Z,0][translation,1]</c>.</remarks>
    private static double? DotInAssembly(
        IReadOnlyList<double>? matrix1, IReadOnlyList<double>? matrix2,
        double[] n1Local, double[] n2Local)
    {
        var n1 = ApplyRotation(matrix1, n1Local);
        var n2 = ApplyRotation(matrix2, n2Local);
        if (n1 is null || n2 is null)
        {
            return null;
        }

        var dot = n1[0] * n2[0] + n1[1] * n2[1] + n1[2] * n2[2];
        var length = Math.Sqrt(n1[0] * n1[0] + n1[1] * n1[1] + n1[2] * n1[2])
            * Math.Sqrt(n2[0] * n2[0] + n2[1] * n2[1] + n2[2] * n2[2]);
        return length <= 1e-9 ? null : dot / length;
    }

    private static double[]? ApplyRotation(IReadOnlyList<double>? matrix, double[] local)
    {
        if (matrix is null || matrix.Count < 16)
        {
            return null;
        }

        var result = new double[3];
        for (var row = 0; row < 3; row++)
        {
            result[row] = matrix[0 + row] * local[0] + matrix[4 + row] * local[1] + matrix[8 + row] * local[2];
        }

        return result;
    }

    private static double[]? Rotation(IReadOnlyList<double>? matrix) => matrix is null || matrix.Count < 16
        ? null
        : new[]
        {
            Round6(matrix[0]), Round6(matrix[1]), Round6(matrix[2]),
            Round6(matrix[4]), Round6(matrix[5]), Round6(matrix[6]),
            Round6(matrix[8]), Round6(matrix[9]), Round6(matrix[10]),
        };

    private static double[]? Translation(IReadOnlyList<double>? matrix) => matrix is null || matrix.Count < 16
        ? null
        : new[] { Math.Round(matrix[12], 3), Math.Round(matrix[13], 3), Math.Round(matrix[14], 3) };

    private static bool RotationEqual(double[]? a, double[]? b)
    {
        if (a is null || b is null || a.Length < 9 || b.Length < 9)
        {
            return false;
        }

        for (var i = 0; i < 9; i++)
        {
            if (Math.Abs(a[i] - b[i]) > 1e-3)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Names the measured orientation: aligned, anti-aligned, or neither (outside the tolerance).</summary>
    private static string Classify(double? dot) => dot switch
    {
        null => "не прочитан",
        _ when dot.Value >= DotTolerance => "cooriented(+1)",
        _ when dot.Value <= -DotTolerance => "opposite(-1)",
        _ => "ни то ни другое",
    };

    private static double Round6(double value) => Math.Round(value, 6);

    private static double? Round(double? value) => value is null ? null : Math.Round(value.Value, 6);
}
