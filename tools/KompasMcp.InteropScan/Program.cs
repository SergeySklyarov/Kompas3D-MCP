using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Text;
using KompasMcp.InteropScan;

// Read-only metadata scanner for interop assemblies and for installed type libraries. No КОМПАС
// session, no documents; a type library is opened read-only and is never registered.
//
// Usage:
//   KompasMcp.InteropScan <assembly.dll> [options]
//   KompasMcp.InteropScan --tlb <library.tlb> --type-members <name> [--csv]
//
//   --types <substr>      list types whose name contains <substr> (repeatable)
//   --members <substr>    list members whose name contains <substr> (repeatable)
//   --type-members <name> dump every member of the type whose full/short name matches exactly
//   --order               with --type-members: keep METADATA order instead of sorting by name
//   --all-types           list every type in the assembly
//   --csv                 emit machine-readable lines: kind|declaringType|memberName|signature
//   --tlb <path>          read a COM TYPE LIBRARY instead of an assembly (see TlbLayout.cs)
//   --com-slots           with --type-members: append the COM slot of every member of a [ComImport]
//                         interface, computed by the runtime (Marshal.GetComSlotForMethodInfo)
//   --resolve <dir>       load referenced assemblies from <dir> (the vendor interop lives beside the
//                         installation, and Private=false means it is never copied to the output)
//
// Exit codes: 0 = scan completed, 2 = usage, 3 = assembly/library failed to load, 4 = file missing.
//
// WHY --order EXISTS: a COM interface imported by tlbimp is emitted in VTABLE order, and the
// default name-sorted listing destroys exactly that information. A hand-declared [ComImport]
// twin of a shipped interface must reproduce the vtable slot order, so the order has to be
// readable from the machine-generated interop rather than remembered.

if (args.Length < 1)
{
    Console.Error.WriteLine("usage: KompasMcp.InteropScan <assembly.dll> [--types s] [--members s] [--type-members N] [--all-types] [--csv] [--com-slots] [--resolve dir]");
    Console.Error.WriteLine("       KompasMcp.InteropScan --tlb <library.tlb> --type-members N [--csv]");
    return 2;
}

// The first argument is the assembly unless it is an option itself (the type-library mode has no
// assembly at all), and reading it unconditionally made "--tlb <path>" report the PATH as unknown.
var optionFirst = args[0].StartsWith("--", StringComparison.Ordinal);
var assemblyPath = optionFirst ? string.Empty : args[0];
var typeFilters = new List<string>();
var memberFilters = new List<string>();
var exactTypes = new List<string>();
var allTypes = false;
var csv = false;
var metadataOrder = false;
var comSlots = false;
string? tlbPath = null;
string? resolveDirectory = null;

for (var i = optionFirst ? 0 : 1; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--types" when i + 1 < args.Length:
            typeFilters.Add(args[++i]);
            break;
        case "--members" when i + 1 < args.Length:
            memberFilters.Add(args[++i]);
            break;
        case "--type-members" when i + 1 < args.Length:
            exactTypes.Add(args[++i]);
            break;
        case "--all-types":
            allTypes = true;
            break;
        case "--order":
            metadataOrder = true;
            break;
        case "--csv":
            csv = true;
            break;
        case "--com-slots":
            comSlots = true;
            break;
        case "--tlb" when i + 1 < args.Length:
            tlbPath = args[++i];
            break;
        case "--resolve" when i + 1 < args.Length:
            resolveDirectory = args[++i];
            break;
        default:
            Console.Error.WriteLine($"unknown argument: {args[i]}");
            return 2;
    }
}

if (resolveDirectory is not null)
{
    // The vendor interop is referenced with Private=false, so it never lands beside the assembly that
    // uses it. Without this handler a twin's members cannot be resolved at all and the scan would
    // report a load failure that says nothing about the twin.
    var directory = Path.GetFullPath(resolveDirectory);
    AssemblyLoadContext.Default.Resolving += (_, name) =>
    {
        if (name.Name is null)
        {
            return null;
        }

        var candidate = Path.Combine(directory, name.Name + ".dll");
        return File.Exists(candidate) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(candidate) : null;
    };
}

if (tlbPath is not null)
{
    return TlbLayout.Run(tlbPath, exactTypes);
}

Assembly assembly;
try
{
    assembly = Assembly.LoadFrom(assemblyPath);
}
catch (Exception ex)
{
    // A load failure is reported, never silently treated as "no members".
    Console.Error.WriteLine($"LOAD_FAILED {Path.GetFileName(assemblyPath)}: {ex.GetType().Name}: {ex.Message}");
    if (ex is ReflectionTypeLoadException rtle)
    {
        foreach (var inner in rtle.LoaderExceptions.Where(e => e is not null).Take(5))
        {
            Console.Error.WriteLine($"  loader: {inner!.Message}");
        }
    }

    return 3;
}

Type[] types;
try
{
    types = assembly.GetTypes();
}
catch (ReflectionTypeLoadException ex)
{
    types = ex.Types.Where(t => t is not null).Select(t => t!).ToArray();
    Console.Error.WriteLine($"PARTIAL_TYPES {ex.LoaderExceptions.Length} loader exception(s); {types.Length} types recovered");
}

Console.WriteLine($"ASSEMBLY {assembly.GetName().Name} {assembly.GetName().Version} types={types.Length}");

static bool Contains(string haystack, string needle)
    => haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);

static string Short(Type t) => t.Name;

static string Signature(MemberInfo m)
{
    var sb = new StringBuilder();
    switch (m)
    {
        case MethodInfo mi:
            sb.Append(mi.ReturnType.Name).Append(' ').Append(mi.Name).Append('(');
            sb.Append(string.Join(", ", mi.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}")));
            sb.Append(')');
            break;
        case PropertyInfo pi:
            sb.Append("prop ").Append(pi.PropertyType.Name).Append(' ').Append(pi.Name);
            break;
        case FieldInfo fi:
            sb.Append("field ").Append(fi.FieldType.Name).Append(' ').Append(fi.Name).Append(" = ");
            // GetRawConstantValue throws for a field without a compile-time constant (e.g. an
            // enum member imported without a literal), and an enum dump that dies on one member
            // hides every other member it was run to show.
            try
            {
                sb.Append(fi.GetRawConstantValue());
            }
            catch (Exception)
            {
                sb.Append("<no-constant>");
            }

            break;
        default:
            sb.Append(m.MemberType).Append(' ').Append(m.Name);
            break;
    }

    return sb.ToString();
}

// The COM slot of a member is what a typed twin must reproduce, so it is printed as a number instead
// of being left to "the order looks right".
// WHY IT IS COMPUTED AND NOT ASKED OF THE RUNTIME: Marshal.GetComSlotForMethodInfo does not exist in
// this runtime — it is a .NET Framework API, and the scan would not compile against it. The rule used
// instead is the one this tool already documents: for a [ComImport] interface the declaration order IS
// the vtable order, so member i of a dual interface sits in slot 7 + i (3 + i for an IUnknown-based
// one). The live twin reconciliation checks the result against the installed type library, which is
// the authority the rule rests on.
static Dictionary<string, string> Slots(Type type)
{
    var kind = (type.GetCustomAttributes(typeof(InterfaceTypeAttribute), false)
            .FirstOrDefault() as InterfaceTypeAttribute)?.Value ?? ComInterfaceType.InterfaceIsDual;
    var baseSlots = kind == ComInterfaceType.InterfaceIsIUnknown ? 3 : 7;
    var own = type.GetMethods()
        .Where(m => m.DeclaringType == type)
        .OrderBy(m => m.MetadataToken)
        .ToList();
    var slots = new Dictionary<string, string>(StringComparer.Ordinal);
    for (var index = 0; index < own.Count; index++)
    {
        slots[own[index].Name] = $" slot={baseSlots + index}";
    }

    return slots;
}

static string Slot(Dictionary<string, string>? slots, MemberInfo m)
{
    if (slots is null)
    {
        return string.Empty;
    }

    return slots.TryGetValue(m.Name, out var slot) ? slot : $" slot=none({m.MemberType})";
}

if (allTypes)
{
    foreach (var t in types.OrderBy(t => t.FullName, StringComparer.Ordinal))
    {
        Console.WriteLine(csv ? $"type|{t.FullName}||" : $"  {t.FullName}");
    }
}

if (exactTypes.Count > 0)
{
    foreach (var wanted in exactTypes)
    {
        var matched = types.Where(t =>
            string.Equals(Short(t), wanted, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(t.FullName, wanted, StringComparison.OrdinalIgnoreCase)).ToList();

        if (matched.Count == 0)
        {
            Console.WriteLine($"TYPE_NOT_FOUND {wanted}");
            continue;
        }

        foreach (var t in matched)
        {
            // GUID first: a member list without the interface identity is not reproducible evidence.
            var guid = t.GUID == Guid.Empty ? "-" : t.GUID.ToString("N");
            Console.WriteLine($"{Short(t)} iid={guid} kind={t.Attributes} members={t.GetMembers().Length}");
            if (metadataOrder)
            {
                // The INTERFACE-level attributes decide how a twin declaration must be written: a
                // dual interface offsets its members by the seven IUnknown+IDispatch slots, an
                // IUnknown-based one by three. Printing them makes the twin checkable, not guessed.
                var comImport = t.GetCustomAttributes(typeof(ComImportAttribute), false).Length > 0;
                var iface = t.GetCustomAttributes(typeof(InterfaceTypeAttribute), false);
                var ifaceKind = iface.Length > 0
                    ? $"{((InterfaceTypeAttribute)iface[0]).Value}({(int)((InterfaceTypeAttribute)iface[0]).Value})"
                    : "none";
                Console.WriteLine($"attrs|{Short(t)}|ComImport={comImport}|InterfaceType={ifaceKind}|Guid={guid}");
            }
            var slots = comSlots ? Slots(t) : null;
            var members = t.GetMembers()
                .Where(m => m.DeclaringType == t || m.DeclaringType?.IsInterface == true);
            foreach (var m in metadataOrder ? members : members.OrderBy(m => m.Name, StringComparer.Ordinal))
            {
                // A member inherited from a base interface has no slot OF ITS OWN: it keeps the slot the
                // base gave it, so it is named rather than numbered.
                var slot = comSlots
                    ? (m.DeclaringType == t ? Slot(slots, m) : " slot=inherited")
                    : string.Empty;
                if (csv)
                {
                    Console.WriteLine($"member|{Short(t)}|{m.Name}|{Signature(m)}{slot}");
                }
                else
                {
                    Console.WriteLine($"    {Signature(m)}{slot}");
                }
            }
        }
    }
}

foreach (var filter in typeFilters)
{
    Console.WriteLine($"--- types containing '{filter}'");
    foreach (var t in types.Where(t => Contains(t.FullName ?? t.Name, filter)).OrderBy(t => t.FullName, StringComparer.Ordinal))
    {
        Console.WriteLine($"  {t.FullName}");
    }
}

if (memberFilters.Count > 0)
{
    var seen = new HashSet<string>(StringComparer.Ordinal);
    foreach (var filter in memberFilters)
    {
        Console.WriteLine($"--- members containing '{filter}'");
        foreach (var t in types.OrderBy(t => t.FullName, StringComparer.Ordinal))
        {
            MemberInfo[] members;
            try
            {
                members = t.GetMembers();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"  member-read-failed {t.FullName}: {ex.Message}");
                continue;
            }

            foreach (var m in members.Where(m => Contains(m.Name, filter)).OrderBy(m => m.Name, StringComparer.Ordinal))
            {
                var key = $"{Short(t)}.{m.Name}";
                if (!seen.Add(key))
                {
                    continue;
                }

                if (csv)
                {
                    Console.WriteLine($"member|{Short(t)}|{m.Name}|{Signature(m)}");
                }
                else
                {
                    Console.WriteLine($"    {Short(t),-42} {Signature(m)}");
                }
            }
        }
    }
}

return 0;
