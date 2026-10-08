using System.Runtime.InteropServices;
using Kompas6Constants;
using Kompas6Constants3D;
using KompasAPI7;

namespace KompasMcp.Api5Adapter.Api7;

/// <summary>Typed twins of the two API7 interfaces the shipped wrapper does not declare.</summary>
/// <remarks>MEASURED: the shipped wrapper is older than the installed type library, so its <c>IPart7</c>
/// has no <c>Measurement3D</c> and there is no <c>IMeasurement3D</c>, while the installed type library
/// and the API7 server carry both. The layout is read from the interop generated from that library, in
/// metadata order, which for an imported dual interface IS the vtable order.
/// INVARIANT: typed vtable calls, never <c>IDispatch</c> (ADR-004); slot comments are 0-based, so the
/// vtable slot is 7 + N. LIMIT: only the slots up to and including the used member are declared.
/// History: docs/decisions/assembly.md#api7-twin</remarks>
internal static class Api7InteropTwin
{
    /// <summary>The IID of <c>IPart7</c>; the same value the shipped wrapper declares.</summary>
    public const string Part7Iid = "fa4a5fde-a08c-4f5a-8c04-98395ba44307";

    /// <summary>The IID of <c>IMeasurement3D</c>, taken from the installed TLB.</summary>
    public const string Measurement3DIid = "f59ff609-45c2-4afb-b3c4-08184b9900ff";
}


/// <summary>The API7 part interface as declared by the INSTALLED type library: the shipped wrapper's
/// 106 members plus the 11 appended members up to <c>Measurement3D</c>.</summary>
[ComImport]
[Guid(Api7InteropTwin.Part7Iid)]
[InterfaceType(ComInterfaceType.InterfaceIsDual)]
internal interface IPart7Twin
{
    IKompasAPIObject get_Parent();   // slot 0
    IApplication get_Application();   // slot 1
    KompasAPIObjectTypeEnum get_Type();   // slot 2
    int get_Reference();   // slot 3
    string get_Name();   // slot 4
    void set_Name(string PVal);   // slot 5
    void set_Hidden(bool PVal);   // slot 6
    bool get_Hidden();   // slot 7
    bool Update();   // slot 8
    bool get_Valid();   // slot 9
    Part7 get_Part();   // slot 10
    ksObj3dTypeEnum get_ModelObjectType();   // slot 11
    IFeature7 get_Owner();   // slot 12
    string get_Marking();   // slot 13
    void set_Marking(string PVal);   // slot 14
    string get_FileName();   // slot 15
    void set_FileName(string PVal);   // slot 16
    void set_Standard(bool PVal);   // slot 17
    bool get_Standard();   // slot 18
    void set_Fixed(bool PVal);   // slot 19
    bool get_Fixed();   // slot 20
    bool get_Detail();   // slot 21
    double get_Mass();   // slot 22
    double get_Density();   // slot 23
    string get_Material();   // slot 24
    bool SetMaterial(string Name, double Density);   // slot 25
    Parts7 get_Parts();   // slot 26
    VariableTable get_VariableTable();   // slot 27
    object get_PartsEx(object PartCollectionType);   // slot 28
    int get_InstanceCount(Part7 Part);   // slot 29
    object SelectByPoint(object Objects, double X, double Y, double Z);   // slot 30
    bool TransferObjects(object Objects, LocalCoordinateSystem Lcs, bool HoldPosition);   // slot 31
    bool Load(bool Full);   // slot 32
    bool Unload(bool Full);   // slot 33
    ksLoadStateEnum get_LoadState();   // slot 34
    IModelObject get_DefaultObject(ksObj3dTypeEnum Type);   // slot 35
    bool IsVariableNameValid(string Name);   // slot 36
    Variable7 AddVariable(string Name, double Value, string Note);   // slot 37
    bool RebuildModel(bool Redraw);   // slot 38
    ksPartAccessTypeEnum get_ReadOnly();   // slot 39
    void set_ReadOnly(ksPartAccessTypeEnum PVal);   // slot 40
    bool get_StaffVisible();   // slot 41
    void set_StaffVisible(bool PVal);   // slot 42
    bool SaveAs(string PathName);   // slot 43
    IModelObject FindObject(IModelObject Obj, Part7 SourcePart);   // slot 44
    void set_CreateSpcObjects(bool PVal);   // slot 45
    bool get_CreateSpcObjects();   // slot 46
    void set_IsLocal(bool PVal);   // slot 47
    bool get_IsLocal();   // slot 48
    OpenDocumentParam GetOpenDocumentParam();   // slot 49
    IKompasDocument3D BeginEdit(OpenDocumentParam Param);   // slot 50
    bool EndEdit(bool Rebuild);   // slot 51
    object FindObjectsByPoint(double X, double Y, double Z, bool FirstLevel);   // slot 52
    IHatchParam get_HatchParam();   // slot 53
    int get_UniqueNum(ksObj3dTypeEnum OType);   // slot 54
    void set_UniqueNum(ksObj3dTypeEnum OType, int Result);   // slot 55
    bool ChangeObjectLinks(object SourceObjs, object DestObjs, bool RebuildAll);   // slot 56
    bool get_IsLayoutGeometry();   // slot 57
    void set_IsLayoutGeometry(bool PVal);   // slot 58
    bool get_IsBillet();   // slot 59
    void set_IsBillet(bool PVal);   // slot 60
    Placement3D get_Placement();   // slot 61
    bool UpdatePlacement(bool Redraw);   // slot 62
    SpecRough3D get_SpecRough();   // slot 63
    void set_LeftHandedCS(bool PVal);   // slot 64
    bool get_LeftHandedCS();   // slot 65
    bool MirroringPlacement(ksObj3dTypeEnum Axis);   // slot 66
    bool DestroySubassembly();   // slot 67
    double GetMaxSag();   // slot 68
    IMateConstraints3D get_MateConstraints();   // slot 69
    Body7 GetBodyById(int BodyId);   // slot 70
    UserFolders get_UserFolders();   // slot 71
    void set_ToleranceRecalcType(ksToleranceRecalcsEnum PVal);   // slot 72
    ksToleranceRecalcsEnum get_ToleranceRecalcType();   // slot 73
    void set_UserToleranceRecalcId(int PVal);   // slot 74
    int get_UserToleranceRecalcId();   // slot 75
    void set_UserToleranceRecalcName(string PVal);   // slot 76
    string get_UserToleranceRecalcName();   // slot 77
    void set_UseDummy(bool PVal);   // slot 78
    bool get_UseDummy();   // slot 79
    void set_DummyFileName(string PVal);   // slot 80
    string get_DummyFileName();   // slot 81
    int get_DummyEmbodimentIndex();   // slot 82
    string GetDummyEmbodimentMarking(ksVariantMarkingTypeEnum MarkingType, bool AddSystemDelimer);   // slot 83
    bool SetDummyEmbodiment(object Index);   // slot 84
    bool UnloadEx(ksLoadStateEnum Type);   // slot 85
    IKompasDocument3D OpenSourceDocument(OpenDocumentParam Param);   // slot 86
    ZonesManager get_ZonesManager();   // slot 87
    void set_InheritExclude(bool PVal);   // slot 88
    bool get_InheritExclude();   // slot 89
    int get_PartsGroupNumber();   // slot 90
    void set_PartsGroupNumber(int PVal);   // slot 91
    bool IsLocalResultExist(bool Reqursive);   // slot 92
    void set_RevealComposition(bool PVal);   // slot 93
    bool get_RevealComposition();   // slot 94
    void set_InheritVisible(bool PVal);   // slot 95
    bool get_InheritVisible();   // slot 96
    bool TransformPoint(ref double X, ref double Y, ref double Z, Part7 Part);   // slot 97
    bool TransformPoints(ref object Points, Part7 Part);   // slot 98
    object GetSummMatrix(Part7 Part);   // slot 99
    Body7 FindBody(string UniqueMetaObjectKey);   // slot 100
    object GetSimilarInstances(Part7 Part);   // slot 101
    IModelObject FindSimilarObject(IModelObject Obj);   // slot 102
    object FindObjectsByPointEx(double X, double Y, double Z, bool FirstLevel, double Epsilon);   // slot 103
    object SelectByPointEx(object Objects, double X, double Y, double Z, double Epsilon);   // slot 104
    object FindObjectsByPointWithParam(double X, double Y, double Z, bool FirstLevel, double Epsilon, FindObject3DParameters FilterParam);   // slot 105
    bool get_DoubleClickEditable();   // slot 106
    void set_DoubleClickEditable(bool PVal);   // slot 107
    void set_NeedRebuild(bool PVal);   // slot 108
    bool get_NeedRebuild();   // slot 109
    bool get_PropertyObjectEditable();   // slot 110
    void set_PropertyObjectEditable(bool PVal);   // slot 111
    IModelObject VisualCreateObject(ksObj3dTypeEnum ObjectType);   // slot 112
    IModelObject RunCreateObjectProcess(ProcessTypeEnum ProcessType);   // slot 113
    bool VisualEditObject(IModelObject Object);   // slot 114
    bool GetGabarit(bool Full, bool Customizable, ref double X1, ref double Y1, ref double Z1, ref double X2, ref double Y2, ref double Z2);   // slot 115
    IMeasurement3DTwin get_Measurement3D();   // slot 116
}

/// <summary>The transient measurement service <c>IMeasurement3D</c> as declared by the installed type
/// library, up to and including <c>GetMinPoint2</c>.</summary>
[ComImport]
[Guid(Api7InteropTwin.Measurement3DIid)]
[InterfaceType(ComInterfaceType.InterfaceIsDual)]
internal interface IMeasurement3DTwin
{
    IKompasAPIObject get_Parent();   // slot 0
    IApplication get_Application();   // slot 1
    KompasAPIObjectTypeEnum get_Type();   // slot 2
    int get_Reference();   // slot 3
    IKompasAPIObject get_Object1();   // slot 4
    void set_Object1(IKompasAPIObject Result);   // slot 5
    IKompasAPIObject get_Object2();   // slot 6
    void set_Object2(IKompasAPIObject Result);   // slot 7
    bool get_ExtendObject1();   // slot 8
    bool get_ExtendObject2();   // slot 9
    bool get_Briefly();   // slot 10
    void set_Briefly(bool Result);   // slot 11
    ksMeasureResultEnum Calculate();   // slot 12
    double get_Lmin();   // slot 13
    double get_Lmax();   // slot 14
    double get_LNormal();   // slot 15
    bool get_IsAngleValid();   // slot 16
    double get_Angle();   // slot 17
    bool GetMinPoint1(ref double X, ref double Y, ref double Z);   // slot 18
    bool GetMinPoint2(ref double X, ref double Y, ref double Z);   // slot 19
}
