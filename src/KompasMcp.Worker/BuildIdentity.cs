using System.Reflection;
using System.Text.Json.Nodes;

namespace KompasMcp.Worker;

/// <summary>Reports which build of each KompasMcp assembly this Worker actually loaded.</summary> <remarks>The Host
/// launches <c>KompasMcp.Worker.exe</c> from its own output folder without referencing it (ADR-001: no COM in the
/// Host), so MSBuild never refreshed that copy and a STALE copy silently ran; this report makes a mismatch observable
/// from one <c>kompas_health</c> call.
/// History: docs/decisions/worker-ipc.md#build-identity</remarks>
internal static class BuildIdentity
{
    /// <summary>Assemblies whose version determines behaviour. <c>KompasMcp.Host</c> is absent: the Worker cannot see
    /// the Host's assembly, and the Host reports its own identity separately.</summary>
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
            // Already-loaded assemblies only: Assembly.Load would itself change what is loaded,
            // and the point is to report the deployment as it stands.
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

            // The file timestamp reveals a stale copy: minutes apart is normal, days apart is the defect.
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
