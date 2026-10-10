using KompasMcp.Contracts;

namespace KompasMcp.Domain.Geometry;

/// <summary>One mass-centre scalar as it came back from the kernel: either a value, or a NAMED reason it
/// did not read.</summary>
/// <remarks>INVARIANT: an unread value is <c>null</c> WITH a reason — never a zero, which would be
/// indistinguishable from a measured zero moment (a symmetric body really does have zero centrifugal
/// moments). History: docs/decisions/geometry.md#inertia</remarks>
public readonly record struct InertiaReading(double? Value, string? Failure)
{
    public bool Read => Value is not null;

    public static InertiaReading Ok(double value) => new(value, null);

    public static InertiaReading Failed(string reason) => new(null, reason);

    /// <summary>A finite value is a reading; anything else is a NAMED failure.</summary>
    public static InertiaReading From(double value, string field) =>
        double.IsFinite(value)
            ? Ok(value)
            : Failed($"{field}_not_finite — свойство {field} вернуло нечисловое значение");
}

/// <summary>Direction of one principal central axis as it came back from the kernel.</summary>
public readonly record struct InertiaAxisReading(double[]? Vector, string? Failure)
{
    public static InertiaAxisReading Ok(double[] vector) => new(vector, null);

    public static InertiaAxisReading Failed(string reason) => new(null, reason);
}

/// <summary>Assembly of the <c>inertia</c> block of <c>kompas_measure</c> — a PURE function, so the
/// "null with a reason instead of a zero" rule and the mandatory unit/system fields are testable without
/// KOMPAS.</summary>
/// <remarks>INVARIANT: <c>units</c> and <c>system</c> are mandatory — one and the same name <c>Jx</c>
/// denotes three different quantities in three systems, so a bare number is not a measurement.
/// INVARIANT: every group that stayed null is named in <c>notes</c>.
/// History: docs/decisions/geometry.md#inertia</remarks>
public static class InertiaBlock
{
    public static InertiaDto Build(
        string units,
        string system,
        InertiaReading jx,
        InertiaReading jy,
        InertiaReading jz,
        InertiaReading jxjy,
        InertiaReading jxz,
        InertiaReading jyz,
        InertiaReading jx0z,
        InertiaReading jy0z,
        InertiaReading jx0y,
        InertiaReading jx0,
        InertiaReading jy0,
        InertiaReading jz0,
        InertiaAxisReading axisX,
        InertiaAxisReading axisY,
        InertiaAxisReading axisZ)
    {
        if (string.IsNullOrWhiteSpace(units))
        {
            throw new ArgumentException("Единицы моментов инерции обязательны: без них число не измерение.",
                nameof(units));
        }

        if (string.IsNullOrWhiteSpace(system))
        {
            throw new ArgumentException("Система координат моментов обязательна: одно и то же имя Jx означает "
                + "разные величины в разных системах.", nameof(system));
        }

        var notes = new List<string>();
        var axesNotes = new List<string>();

        Collect(notes, jx, jy, jz, jxjy, jxz, jyz, jx0z, jy0z, jx0y, jx0, jy0, jz0);
        CollectAxis(axesNotes, axisX);
        CollectAxis(axesNotes, axisY);
        CollectAxis(axesNotes, axisZ);
        notes.AddRange(axesNotes);

        return new InertiaDto
        {
            Units = units,
            System = system,
            Axial = new InertiaAxialDto { Jx = jx.Value, Jy = jy.Value, Jz = jz.Value },
            Centrifugal = new InertiaCentrifugalDto { Jxjy = jxjy.Value, Jxz = jxz.Value, Jyz = jyz.Value },
            Plane = new InertiaPlaneDto { Jx0z = jx0z.Value, Jy0z = jy0z.Value, Jx0y = jx0y.Value },
            Principal = new InertiaPrincipalDto
            {
                System = PrincipalSystem,
                Jx0 = jx0.Value,
                Jy0 = jy0.Value,
                Jz0 = jz0.Value,
            },
            PrincipalAxes = new InertiaAxesDto
            {
                X = axisX.Vector,
                Y = axisY.Vector,
                Z = axisZ.Vector,
                Notes = axesNotes,
            },
            Notes = notes,
        };
    }

    /// <summary>Name of the principal central system — the system of the <c>principal</c> group, which is
    /// NOT the system of the axial/centrifugal/plane groups.</summary>
    public const string PrincipalSystem = "principal";

    /// <summary>Name of the central system — the system of the axial, centrifugal and plane groups.</summary>
    public const string CentralSystem = "central";

    private static void Collect(List<string> notes, params InertiaReading[] readings)
    {
        foreach (var reading in readings)
        {
            if (!reading.Read && reading.Failure is { Length: > 0 } failure)
            {
                notes.Add(failure);
            }
        }
    }

    private static void CollectAxis(List<string> notes, InertiaAxisReading reading)
    {
        if (reading.Vector is null && reading.Failure is { Length: > 0 } failure)
        {
            notes.Add(failure);
        }
    }
}
