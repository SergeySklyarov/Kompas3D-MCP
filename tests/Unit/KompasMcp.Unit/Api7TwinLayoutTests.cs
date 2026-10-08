using System.Reflection;
using System.Runtime.InteropServices;
using Xunit;

namespace KompasMcp.Unit;

/// <summary>The declared layout of the two API7 twins the gap route calls through.</summary>
/// <remarks>INVARIANT: a <c>[ComImport]</c> twin reproduces a VTABLE, so a member that moves in the
/// declaration moves in the vtable and the call reaches a different function of the same interface.
/// MEASURED: the runtime call that would report a slot (<c>Marshal.GetComSlotForMethodInfo</c>) is not
/// declared by this runtime - it is a .NET Framework API - so the slot is derived by the rule the
/// instrument documents: the declaration order IS the vtable order, member i of a dual interface
/// sitting in slot 7 + i. The live reconciliation row checks that rule against the type library.
/// History: docs/decisions/assembly.md#api7-twin</remarks>
public sealed class Api7TwinLayoutTests
{
    /// <summary>Slots IUnknown (3) and IDispatch (4) occupy in front of a dual interface's members.</summary>
    private const int DualBaseSlots = 7;

    private const string Part7Iid = "fa4a5fde-a08c-4f5a-8c04-98395ba44307";
    private const string Measurement3DIid = "f59ff609-45c2-4afb-b3c4-08184b9900ff";

    /// <summary>Every member of <c>IPart7Twin</c> in declaration order, which IS its vtable order.</summary>
    private static readonly string[] Part7Members =
    {
        "get_Parent", "get_Application", "get_Type", "get_Reference", "get_Name", "set_Name",
        "set_Hidden", "get_Hidden", "Update", "get_Valid", "get_Part", "get_ModelObjectType",
        "get_Owner", "get_Marking", "set_Marking", "get_FileName", "set_FileName", "set_Standard",
        "get_Standard", "set_Fixed", "get_Fixed", "get_Detail", "get_Mass", "get_Density",
        "get_Material", "SetMaterial", "get_Parts", "get_VariableTable", "get_PartsEx",
        "get_InstanceCount", "SelectByPoint", "TransferObjects", "Load", "Unload", "get_LoadState",
        "get_DefaultObject", "IsVariableNameValid", "AddVariable", "RebuildModel", "get_ReadOnly",
        "set_ReadOnly", "get_StaffVisible", "set_StaffVisible", "SaveAs", "FindObject",
        "set_CreateSpcObjects", "get_CreateSpcObjects", "set_IsLocal", "get_IsLocal",
        "GetOpenDocumentParam", "BeginEdit", "EndEdit", "FindObjectsByPoint", "get_HatchParam",
        "get_UniqueNum", "set_UniqueNum", "ChangeObjectLinks", "get_IsLayoutGeometry",
        "set_IsLayoutGeometry", "get_IsBillet", "set_IsBillet", "get_Placement", "UpdatePlacement",
        "get_SpecRough", "set_LeftHandedCS", "get_LeftHandedCS", "MirroringPlacement",
        "DestroySubassembly", "GetMaxSag", "get_MateConstraints", "GetBodyById", "get_UserFolders",
        "set_ToleranceRecalcType", "get_ToleranceRecalcType", "set_UserToleranceRecalcId",
        "get_UserToleranceRecalcId", "set_UserToleranceRecalcName", "get_UserToleranceRecalcName",
        "set_UseDummy", "get_UseDummy", "set_DummyFileName", "get_DummyFileName",
        "get_DummyEmbodimentIndex", "GetDummyEmbodimentMarking", "SetDummyEmbodiment", "UnloadEx",
        "OpenSourceDocument", "get_ZonesManager", "set_InheritExclude", "get_InheritExclude",
        "get_PartsGroupNumber", "set_PartsGroupNumber", "IsLocalResultExist", "set_RevealComposition",
        "get_RevealComposition", "set_InheritVisible", "get_InheritVisible", "TransformPoint",
        "TransformPoints", "GetSummMatrix", "FindBody", "GetSimilarInstances", "FindSimilarObject",
        "FindObjectsByPointEx", "SelectByPointEx", "FindObjectsByPointWithParam",
        "get_DoubleClickEditable", "set_DoubleClickEditable", "set_NeedRebuild", "get_NeedRebuild",
        "get_PropertyObjectEditable", "set_PropertyObjectEditable", "VisualCreateObject",
        "RunCreateObjectProcess", "VisualEditObject", "GetGabarit", "get_Measurement3D",
    };

    /// <summary>Every member of <c>IMeasurement3DTwin</c>, in the same order.</summary>
    private static readonly string[] Measurement3DMembers =
    {
        "get_Parent", "get_Application", "get_Type", "get_Reference", "get_Object1", "set_Object1",
        "get_Object2", "set_Object2", "get_ExtendObject1", "get_ExtendObject2", "get_Briefly",
        "set_Briefly", "Calculate", "get_Lmin", "get_Lmax", "get_LNormal", "get_IsAngleValid",
        "get_Angle", "GetMinPoint1", "GetMinPoint2",
    };

    /// <summary>The members the adapter calls, with the slot read from the installed type library.</summary>
    private static readonly (string Member, int Slot)[] Part7Used = { ("get_Measurement3D", 123) };

    /// <summary>The members the adapter calls on the measurement service, with their slots.</summary>
    private static readonly (string Member, int Slot)[] Measurement3DUsed =
    {
        ("get_Object1", 11), ("set_Object1", 12), ("get_Object2", 13), ("set_Object2", 14),
        ("Calculate", 19), ("get_Lmin", 20), ("get_IsAngleValid", 23), ("get_Angle", 24),
        ("GetMinPoint1", 25), ("GetMinPoint2", 26),
    };

    [Fact]
    public void Part7Twin_DeclaresTheMeasuredLayout()
    {
        var type = Twin("IPart7Twin");

        AssertLayout(type, "Part7Iid", Part7Iid, Part7Members, Part7Used);
    }

    [Fact]
    public void Measurement3DTwin_DeclaresTheMeasuredLayout()
    {
        var type = Twin("IMeasurement3DTwin");

        AssertLayout(type, "Measurement3DIid", Measurement3DIid, Measurement3DMembers, Measurement3DUsed);
    }

    /// <summary>The twin's identity, its member ORDER and the slots of the members actually called.</summary>
    /// <remarks>The order is compared in full: an exchange of two members leaves every other slot in
    /// place, so a check limited to the called members would not notice it.
    /// INVARIANT: the identity is taken from the adapter's own constant AND from the measured value, so
    /// editing one without the other is refused.</remarks>
    private static void AssertLayout(
        Type type, string iidConstant, string iid, string[] expected, (string Member, int Slot)[] used)
    {
        var attribute = Assert.Single(type.GetCustomAttributes(typeof(InterfaceTypeAttribute), false));
        Assert.Equal(ComInterfaceType.InterfaceIsDual, ((InterfaceTypeAttribute)attribute).Value);

        // "D" (dashed), the form the twin's own constants are written in, so a mismatch is readable.
        Assert.Equal(iid, type.GUID.ToString("D"));
        Assert.Equal(iid, Constant(iidConstant));

        var declared = type.GetMethods()
            .Where(method => method.DeclaringType == type)
            .OrderBy(method => method.MetadataToken)
            .Select(method => method.Name)
            .ToArray();
        Assert.Equal(expected, declared);

        var slots = declared
            .Select((name, index) => (name, slot: DualBaseSlots + index))
            .ToDictionary(row => row.name, row => row.slot, StringComparer.Ordinal);
        foreach (var (member, slot) in used)
        {
            Assert.True(slots.ContainsKey(member), $"{type.Name}: члена «{member}» в двойнике нет");
            Assert.Equal(slot, slots[member]);
        }
    }

    /// <summary>The twin interface of the built adapter, with the vendor interop made resolvable.</summary>
    /// <remarks>MEASURED: the adapter references the vendor interop with <c>Private=false</c>, so the
    /// types of a twin cannot be loaded without the installation's own directory. The adapter's public
    /// resolver is asked for it rather than a path being guessed here.
    /// LIMIT: a machine without the installation fails this test by name. That is the honest outcome:
    /// an unverified layout is not a passing one.</remarks>
    private static Type Twin(string name)
    {
        var adapter = Assembly.LoadFrom(AdapterPath());
        var resolver = adapter.GetType("KompasMcp.Api5Adapter.KompasInteropResolver");
        Assert.NotNull(resolver);
        var installed = resolver!.GetMethod("TryInstall")!.Invoke(null, new object?[] { null });
        Assert.True(installed is true,
            "интероп КОМПАС не найден: раскладку двойника проверить нечем, и это НЕ «пройдено»");

        var type = adapter.GetType("KompasMcp.Api5Adapter.Api7." + name);
        Assert.NotNull(type);
        return type!;
    }

    /// <summary>The value of a public constant of the twin declarations.</summary>
    private static string Constant(string field)
    {
        var adapter = Assembly.LoadFrom(AdapterPath());
        var owner = adapter.GetType("KompasMcp.Api5Adapter.Api7.Api7InteropTwin");
        Assert.NotNull(owner);
        var value = owner!.GetField(field, BindingFlags.Public | BindingFlags.Static)?.GetRawConstantValue();
        Assert.True(value is string, $"у двойника нет константы «{field}»");
        return (string)value!;
    }

    /// <summary>The adapter assembly built in the configuration the tests are running in.</summary>
    private static string AdapterPath()
    {
        // The test output path carries both facts: "...\bin\x64\<Configuration>\<framework>\", and the
        // configuration is the segment right after "x64".
        var segments = new DirectoryInfo(AppContext.BaseDirectory).FullName
            .Split(Path.DirectorySeparatorChar);
        var platform = Array.FindIndex(segments,
            segment => string.Equals(segment, "x64", StringComparison.OrdinalIgnoreCase));
        Assert.True(platform >= 0 && platform + 1 < segments.Length,
            $"конфигурация сборки не выведена из {AppContext.BaseDirectory}");
        var configuration = segments[platform + 1];

        var path = Path.Combine(RepoRoot(), "src", "KompasMcp.Api5Adapter", "bin", "x64",
            configuration, "net10.0-windows", "KompasMcp.Api5Adapter.dll");
        Assert.True(File.Exists(path),
            $"сборка адаптера не найдена: {path}. Соберите решение той же конфигурацией: без неё " +
            "раскладку двойника проверить нечем, и это НЕ «пройдено»");
        return path;
    }

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "KompasMcp.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException(
            "KompasMcp.sln не найден выше " + AppContext.BaseDirectory);
    }
}
