using System.Text;

namespace KompasMcp.Domain.Files;

/// <summary>Structural read of a STEP file (ISO 10303-21): the schema string of the header, the length
/// unit of the context and the number of B-rep solids — all from the bytes the converter wrote.</summary>
/// <remarks>INVARIANT: every value is a fact about the FILE; what the file does not carry is null with a
/// named aspect, never a default. DOC: ISO 10303-21 clause 8 (header, <c>FILE_SCHEMA</c>) and
/// ISO 10303-42 (solid model entities).
/// History: docs/decisions/geometry.md#step-metadata</remarks>
public static class StepFileInspector
{
    /// <summary>Solid entities of ISO 10303-42 the converter writes one per body.</summary>
    /// <remarks>ASSUMPTION: the list covers the B-rep solids — manifold, with voids, faceted. A body
    /// modelled as CSG, a swept solid or a boolean result would not be counted.
    /// History: docs/decisions/geometry.md#step-metadata</remarks>
    private static readonly string[] SolidEntities =
    {
        "MANIFOLD_SOLID_BREP", "BREP_WITH_VOIDS", "FACETED_BREP",
    };

    /// <summary>Assembly instance entity: one per placed occurrence of a component.</summary>
    private const string InstanceEntity = "NEXT_ASSEMBLY_USAGE_OCCURRENCE";

    public sealed record StepFacts(
        long ByteLength,
        bool HeaderParsed,
        string? Schema,
        string? Units,
        int? UniqueSolids,
        int? InstanceCount,
        IReadOnlyList<string> UnverifiedAspects);

    /// <param name="assembly">Whether the exported document is an assembly: the instance count then
    /// comes from the usage occurrences, not from the body count.</param>
    public static StepFacts Inspect(string path, bool assembly)
    {
        var info = new FileInfo(path);
        // LIMIT: the file is read whole; a streaming pass would be needed for exports far larger than
        // the models this server builds.
        var text = File.ReadAllText(path, Encoding.UTF8);

        var headerParsed = text.StartsWith("ISO-10303-21", StringComparison.Ordinal);
        var schema = HeaderString(text, "FILE_SCHEMA");
        var units = LengthUnit(text);
        var solids = CountEntityInstances(text, SolidEntities);
        int? instances = solids;
        if (assembly)
        {
            // A part holds one occurrence per body; an assembly states its occurrences explicitly, and
            // their absence is a fact about the file, not a zero.
            var placed = CountEntityInstances(text, new[] { InstanceEntity });
            instances = placed > 0 ? placed : null;
        }

        var unverified = new List<string>
        {
            "geometry_not_validated — проверена только структура файла, не форма",
        };
        if (schema is null)
        {
            unverified.Add("schema_not_found — строка FILE_SCHEMA в заголовке не прочитана");
        }

        if (units is null)
        {
            unverified.Add("units_not_read — единица длины не найдена в SI_UNIT/CONVERSION_BASED_UNIT файла");
        }

        if (assembly && instances is null)
        {
            unverified.Add("instance_count_not_read — в файле сборки нет " + InstanceEntity);
        }

        return new StepFacts(info.Length, headerParsed, schema, units, solids, instances, unverified);
    }

    /// <summary>The string argument of a header entry, as written in the file: the value inside the first
    /// pair of quotes after <paramref name="keyword"/>.</summary>
    private static string? HeaderString(string text, string keyword)
    {
        var at = text.IndexOf(keyword, StringComparison.Ordinal);
        if (at < 0)
        {
            return null;
        }

        var open = text.IndexOf('\'', at);
        if (open < 0)
        {
            return null;
        }

        var close = text.IndexOf('\'', open + 1);
        return close > open + 1 ? text[(open + 1)..close] : null;
    }

    /// <summary>Canonical name of the length unit, read from the entity instance that declares
    /// <c>LENGTH_UNIT</c>: <c>SI_UNIT(.MILLI.,.METRE.)</c> gives <c>mm</c>, a
    /// <c>CONVERSION_BASED_UNIT('INCH',…)</c> gives <c>inch</c>.</summary>
    private static string? LengthUnit(string text)
    {
        var at = text.IndexOf("LENGTH_UNIT", StringComparison.Ordinal);
        if (at < 0)
        {
            return null;
        }

        // The whole entity instance, not a window around the token: the angle and solid-angle units of
        // the same context are separate instances that also carry SI_UNIT. The instance starts at the
        // '#' of its assignment, found through the '=' that binds it to the name.
        var assign = text.LastIndexOf('=', at);
        var start = assign < 0 ? -1 : text.LastIndexOf('#', assign);
        var end = text.IndexOf(';', at);
        if (start < 0 || end < start)
        {
            return null;
        }

        var entity = text[start..end];
        var si = entity.IndexOf("SI_UNIT(", StringComparison.Ordinal);
        var conversion = entity.IndexOf("CONVERSION_BASED_UNIT(", StringComparison.Ordinal);
        if (si >= 0 && (conversion < 0 || si < conversion))
        {
            return SiUnitName(Arguments(entity, si + "SI_UNIT(".Length));
        }

        return conversion >= 0 ? ConversionUnitName(Arguments(entity, conversion + "CONVERSION_BASED_UNIT(".Length)) : null;
    }

    /// <summary>Comma-separated arguments of the call whose body starts at <paramref name="from"/>, up to
    /// its closing bracket. Nesting is not tracked: the unit calls carry no nested calls.</summary>
    private static string[] Arguments(string text, int from)
    {
        var close = text.IndexOf(')', from);
        return close < 0
            ? Array.Empty<string>()
            : text[from..close].Split(',', StringSplitOptions.TrimEntries);
    }

    private static string? SiUnitName(string[] arguments)
    {
        if (arguments.Length < 2)
        {
            return null;
        }

        var prefix = Symbol(arguments[0]);
        var name = arguments[1].Trim('.').ToLowerInvariant();
        return name == "metre" ? prefix + "m" : prefix + name;
    }

    private static string? ConversionUnitName(string[] arguments)
    {
        var quoted = arguments.Length > 0 ? arguments[0].Trim('\'') : string.Empty;
        return quoted.Length > 0 ? quoted.ToLowerInvariant() : null;
    }

    /// <summary>SI prefix as written (<c>.MILLI.</c>) to its symbol; <c>$</c> is "no prefix".</summary>
    private static string Symbol(string token) => token.Trim().Trim('.').ToUpperInvariant() switch
    {
        "EXA" => "E",
        "PETA" => "P",
        "TERA" => "T",
        "GIGA" => "G",
        "MEGA" => "M",
        "KILO" => "k",
        "HECTO" => "h",
        "DECA" => "da",
        "DECI" => "d",
        "CENTI" => "c",
        "MILLI" => "m",
        "MICRO" => "µ",
        "NANO" => "n",
        "PICO" => "p",
        "FEMTO" => "f",
        "ATTO" => "a",
        _ => string.Empty,
    };

    /// <summary>Instances of the named entities: an instance is the name right after <c>=</c>, so a name
    /// that merely appears inside a string argument is not counted.</summary>
    private static int CountEntityInstances(string text, IReadOnlyList<string> names)
    {
        var count = 0;
        foreach (var name in names)
        {
            var index = 0;
            while ((index = text.IndexOf(name, index, StringComparison.Ordinal)) >= 0)
            {
                var before = index - 1;
                while (before >= 0 && char.IsWhiteSpace(text[before]))
                {
                    before--;
                }

                if (before >= 0 && text[before] == '=')
                {
                    count++;
                }

                index += name.Length;
            }
        }

        return count;
    }
}
