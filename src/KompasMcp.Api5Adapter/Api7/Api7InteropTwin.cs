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

    /// <summary>The IID of <c>IKompasDocument2D1</c>; the same value the shipped wrapper declares.</summary>
    /// <remarks>MEASURED: the wrapper declares this interface too, but TRUNCATED — its member list ends
    /// at <c>ChangeObjectsOrder</c> (slot 44), while the installed <c>kAPI7.tlb</c> carries 74 members
    /// (slots 7…80). <c>ParametricMode</c> (slots 74 put / 75 get) is therefore in the TLB and in the
    /// interface's own vtable, but not in the wrapper's declaration — which is why the member is reached
    /// through this twin rather than through <c>KompasAPI7.IKompasDocument2D1</c>.</remarks>
    public const string Document2D1Iid = "fb57f1c4-44fe-4c73-9b15-87241e8735b5";
}


/// <summary><c>IKompasDocument2D1</c> as declared by the INSTALLED type library, up to and including
/// <c>get_ParametricMode</c> (slot 75).</summary>
/// <remarks>MEASURED: the layout was read from <c>Bin\kAPI7.tlb</c> (<c>--tlb --type-members
/// IKompasDocument2D1</c>), where the member list of this dual interface starts with the seven
/// <c>IUnknown</c>/<c>IDispatch</c> members, so the TLB's index 7 is vtable slot 7; <c>FUNCDESC.oVft</c>
/// carries the offset directly and agrees (slot 74 → oVft 592, slot 75 → oVft 600). The declaration is
/// FLAT — every member of the interface, in vtable order, with no base interface — so the adapter's rule
/// "member i of a dual interface sits in slot 7 + i" holds for it unchanged, exactly as for
/// <see cref="IPart7Twin"/>.
/// LIMIT, NAMED: the shipped wrapper does not declare five of the types the members above slot 44 use
/// (<c>ksDecomposeObjectLevelEnum</c>, <c>ksDrawingObjectParamTypeEnum</c>, <c>ksCheckAssociationResultEnum</c>,
/// <c>ksCheckObjectsTypeEnum</c>, <c>ParametrizationParam7</c>). Those parameters are declared as the
/// 4-byte value of the enum (ABI-identical for a vtable call) and as an interface pointer respectively,
/// rather than by inventing a twin for a type the installation may not even carry. None of those members
/// is called: the adapter calls <c>get_ParametricMode</c> only, and the PUT is declared solely because a
/// vtable is reproduced in order.
/// History: docs/decisions/assembly.md#api7-twin</remarks>
[ComImport]
[Guid(Api7InteropTwin.Document2D1Iid)]
[InterfaceType(ComInterfaceType.InterfaceIsDual)]
internal interface IKompasDocument2D1Twin
{
    MacroObject get_EditMacroObject();   // slot 7
    DrawingGroups get_DrawingGroups();   // slot 8
    DrawingGroups get_NamedGroups();   // slot 9
    DrawingGroup get_CurrentGroup();   // slot 10
    SelectionManager get_SelectionManager();   // slot 11
    ChooseManager get_ChooseManager();   // slot 12
    object CopyObjects(object Objects, ICopyObjectParam Params);   // slot 13
    object get_Variables(bool ExternalOnly);   // slot 14
    Variable7 get_Variable(bool External, object Index);   // slot 15
    int get_VariablesCount(bool External);   // slot 16
    bool UpdateVariables();   // slot 17
    VariableTable get_VariableTable();   // slot 18
    bool IsVariableNameValid(string Name);   // slot 19
    Variable7 AddVariable(string Name, double Value, string Note);   // slot 20
    bool RebuildDocument();   // slot 21
    bool CreateHyperLink(object Objects, ksHyperLinkTypeEnum Type, string Text, IDrawingObject LinkObject,
        int Level);   // slot 22
    object GetHyperLinkObjects(ksHyperLinkTypeEnum Type, IDrawingObject LinkObject, int Level,
        string Text);   // slot 23
    bool DeleteHyperLinks(object Objects);   // slot 24
    bool get_EditMacroVisibleRegime();   // slot 25
    IDrawingObject GetObjectById(long Id);   // slot 26
    Process2D get_LibProcess(ksProcess2DTypeEnum ProcessType);   // slot 27
    IDrawingObject FindObject(double X, double Y, double Limit, FindObjectParameters Param);   // slot 28
    object FindObjects(double X, double Y, double Limit, FindObjectParameters Param);   // slot 29
    object SelectObjects(ksRegionTypeEnum RegionType, double XMin, double YMin, double XMax,
        double YMax);   // slot 30
    object get_IntervalVariables();   // slot 31
    Variable7 AddIntervalVariable(string Name, double FirstValue, double SecondValue);   // slot 32
    Variable7 get_IntervalVariable(object Index);   // slot 33
    object get_UserFuncVariables();   // slot 34
    Variable7 AddUserFuncVariable(string Name, string Expression);   // slot 35
    Variable7 get_UserFuncVariable(object Index);   // slot 36
    bool ClearUndo();   // slot 37
    void set_EnableUndo(bool PVal);   // slot 38
    bool get_EnableUndo();   // slot 39
    void set_UndoContainer(bool PVal);   // slot 40
    bool get_UndoContainer();   // slot 41
    bool CompleteRebuildDocument();   // slot 42
    bool DestroyObjects(object Objects);   // slot 43
    bool ChangeObjectsOrder(object Objects, IDrawingObject OrderObject,
        ksChangeOrderType OrderType);   // slot 44
    bool get_LibraryEditMode();   // slot 45
    DrawingGroup DecomposeObject(IKompasAPIObject Object, int Level, double Arrow,
        int DecomposeType);   // slot 46
    IDrawingObject VisualCreateObject(DrawingObjectTypeEnum ObjectType);   // slot 47
    IDrawingObject RunCreateObjectProcess(ProcessTypeEnum ProcessType);   // slot 48
    bool VisualEditObject(IDrawingObject Object);   // slot 49
    double get_CursorLimit();   // slot 50
    bool ExecuteProcessOfDocumentFromFile(string FileName, ProcessTypeEnum ProcessType);   // slot 51
    bool GetObjectsGabarit(object Objects, ref double X1, ref double Y1, ref double X2,
        ref double Y2);   // slot 52
    bool CreateMatrix(double X, double Y, double Angle, double ScaleX, double ScaleY);   // slot 53
    bool DeleteMatrix();   // slot 54
    bool TransformPointIntoMatrix(double X, double Y, ref double Xn, ref double Yn);   // slot 55
    bool TransformPointFromMatrix(double X, double Y, ref double Xn, ref double Yn);   // slot 56
    bool TransformLengthIntoMatrix(ref double Length);   // slot 57
    bool TransformLengthFromMatrix(ref double Length);   // slot 58
    bool TransformAngleIntoMatrix(ref double Angle);   // slot 59
    bool TransformAngleFromMatrix(ref double Angle);   // slot 60
    bool TransformObjects(object Objects);   // slot 61
    bool ClearRegion(DrawingGroup ClearGroup, DrawingGroup RegionGroup, bool Inside);   // slot 62
    bool MoveObjects(object Objects, double DX, double DY);   // slot 63
    bool RotateObjects(object Objects, double X, double Y, double Angle);   // slot 64
    IKompasAPIObject SymmetryObjects(object Objects, double X1, double Y1, double X2, double Y2,
        bool Copy, bool TextSymmetry, bool CopyAttribytes);   // slot 65
    void set_TreeNeedRebuild(bool PVal);   // slot 66
    bool get_TreeNeedRebuild();   // slot 67
    void set_TreeNeedUpdateItems(bool PVal);   // slot 68
    bool get_TreeNeedUpdateItems();   // slot 69
    DrawingGroup ViewGetObjectsArea();   // slot 70
    int CheckAssociation(object DimensionTypes, int CheckType, object CheckStyle,
        ref int CompleteAssocCount, ref int IncompleteAssocCount);   // slot 71
    IKompasAPIObject CreateApproximationCurve(IDrawingObject Curve, double Epsilon, bool CurentLayer,
        double MaxRadius, bool Smooth);   // slot 72
    bool ParametrizeObjects(object Objects,
        [MarshalAs(UnmanagedType.Interface)] object Param);   // slot 73
    void set_ParametricMode(bool Enable);   // slot 74
    bool get_ParametricMode();   // slot 75
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
