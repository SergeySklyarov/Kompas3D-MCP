using System.Reflection;
using System.Text;
using Kompas6API5;
using Kompas6Constants;
using Kompas6Constants3D;
using KompasAPI7;

namespace SpecProbe;

/// <summary>Runs one stage of the specification probe and writes its evidence JSON.</summary>
/// <remarks>Stages run in SEPARATE processes: KOMPAS refuses to reopen a path that is still loaded, so
/// "persisted" can only be measured by a process that never held the writing session.
/// Usage: SpecProbe --stage &lt;name&gt; --out &lt;json&gt; --model &lt;m3d&gt; --spw &lt;spw&gt;
/// History: docs/decisions/bom.md#spec-ext</remarks>
internal static class Program
{
    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        ResolveInteropFromInstallation();

        var stage = Value(args, "--stage") ?? "build";
        var outPath = Value(args, "--out") ?? Path.Combine("scratch", "probe-spec.json");
        var model = Value(args, "--model") ?? Path.Combine("scratch", "spec-probe", "fixture", "spec-asm.m3d");
        var spw = Value(args, "--spw") ?? Path.Combine("scratch", "spec-probe", "fixture", "spec.spw");
        using var probe = new SpecProbe(outPath);
        return probe.Run(stage, model, spw);
    }

    private static string? Value(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    /// <summary>Loads the vendor interop from the installation: the project references it with
    /// <c>Private=false</c>, so nothing is copied next to the probe.</summary>
    /// <remarks>INVARIANT: the directory comes from the build, not from the current directory - a probe
    /// that guesses paths measures the guess. History: docs/decisions/bom.md#spec-ext</remarks>
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

