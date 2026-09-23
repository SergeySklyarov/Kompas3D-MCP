using System.Reflection;
using System.Text.Json.Nodes;

namespace KompasMcp.Worker;

/// <summary>
/// Reports which build of each KompasMcp assembly this Worker actually loaded.
/// </summary>
/// <remarks>
/// <para>
/// This exists because of a defect that cost three separate debugging sessions. The Host launches
/// <c>KompasMcp.Worker.exe</c> from its own output folder and does not reference the Worker or the
/// adapter as assemblies (ADR-001: no COM in the Host). MSBuild therefore does not refresh those
/// copies in the Host folder, and a stale copy silently ran alongside a current Host. The symptom
/// was never a crash: it was a correct Host answering with wrong tool responses, and once it was a
/// newly added JSON field that simply did not appear — indistinguishable from a null the code had
/// computed, because <c>KompJson</c> serialises nulls rather than dropping them.
/// </para>
/// <para>
/// The build step is fixed at the source (the copy target now carries <c>$(Platform)</c> and fails
/// loudly when the Worker output is absent). This report is the second layer: it makes a mismatch
/// observable from a single <c>kompas_health</c> call instead of requiring a filesystem
/// investigation. Two timestamps and a module path answer the question "which binary is running?"
/// without a debugger.
/// </para>
/// </remarks>
internal static class BuildIdentity
{
    /// <summary>
    /// Assemblies whose version determines behaviour. <c>KompasMcp.Host</c> is deliberately absent:
    /// the Worker cannot see the Host's assembly, and the Host reports its own identity separately.
    /// </summary>
    private static readonly string[] Tracked =
    [
        "KompasMcp.Worker",
        "KompasMcp.Api5Adapter",
        "KompasMcp.Contracts",
        "KompasMcp.Domain",
    ];

    public static JsonObject Collect()
    {
        var assemblies = new JsonObject();
        foreach (var name in Tracked)
        {
            assemblies[name] = Describe(name);
        }

        return new JsonObject
        {
            ["process_path"] = Environment.ProcessPath ?? "(unknown)",
            ["base_directory"] = AppContext.BaseDirectory,
            ["assemblies"] = assemblies,
        };
    }

    private static JsonObject Describe(string simpleName)
    {
        try
        {
            // Already-loaded assemblies only. Deliberately not Assembly.Load: loading a probe would
            // itself change what is loaded, and the point is to report the deployment as it stands.
            var loaded = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => string.Equals(a.GetName().Name, simpleName, StringComparison.Ordinal));

            if (loaded is null)
            {
                return new JsonObject { ["loaded"] = false };
            }

            var location = loaded.Location;
            var described = new JsonObject
            {
                ["loaded"] = true,
                ["location"] = location,
                ["informational_version"] = loaded.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
            };

            // The file timestamp is the field that actually reveals a stale copy: two assemblies
            // built minutes apart are normal, five days apart is the defect this class exists for.
            try
            {
                described["last_write_utc"] = File.Exists(location)
                    ? File.GetLastWriteTimeUtc(location).ToString("O")
                    : null;
            }
            catch (IOException)
            {
                described["last_write_utc"] = null;
            }
            catch (UnauthorizedAccessException)
            {
                described["last_write_utc"] = null;
            }

            return described;
        }
        catch (Exception ex) when (ex is FileNotFoundException or FileLoadException or BadImageFormatException)
        {
            // An unreadable assembly is a fact worth reporting, not a reason to fail health.
            return new JsonObject { ["loaded"] = false, ["error"] = ex.GetType().Name };
        }
    }
}
