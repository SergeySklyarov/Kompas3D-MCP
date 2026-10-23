using System.Reflection;
using System.Text;
using Kompas6API5;
using Kompas6Constants;
using Kompas6Constants3D;
using KompasAPI7;

namespace SheetMetalProbe;

/// <summary>Runs the sheet-metal probe and writes its evidence JSON.</summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        ResolveInteropFromInstallation();

        var outPath = args.Length > 0 && !args[0].StartsWith("--", StringComparison.Ordinal)
            ? args[0]
            : Path.Combine("scratch", "probe-sheet-metal.json");
        using var probe = new SheetMetalProbe(outPath);
        if (args.Contains("--inspect"))
        {
            return probe.RunInspect(args[args.Length - 1]);
        }

        return args.Contains("--persist") ? probe.RunPersist() : probe.Run();
    }

    /// <summary>Loads the vendor interop from the installation: the project references it with
    /// <c>Private=false</c>, so nothing is copied next to the probe.</summary>
    /// <remarks>INVARIANT: the directory comes from the build, not from the current directory — a probe
    /// that guesses paths measures the guess. History: docs/decisions/sheet-metal.md#probe</remarks>
    private static void ResolveInteropFromInstallation()
    {
        var directory = typeof(Program).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == "KompasInteropDir")?.Value;
        if (string.IsNullOrEmpty(directory))
        {
            return;
        }

        AppDomain.CurrentDomain.AssemblyResolve += (_, eventArgs) =>
        {
            var name = new AssemblyName(eventArgs.Name).Name;
            var candidate = Path.Combine(directory, name + ".dll");
            return name is not null && File.Exists(candidate) ? Assembly.LoadFrom(candidate) : null;
        };
    }
}
