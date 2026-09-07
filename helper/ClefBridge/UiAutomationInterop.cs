using System.Runtime.InteropServices;

namespace ClefBridge;

// Minimal UI Automation COM projection (UIAutomationClient.h). Method order is
// the vtable order and must not change. Only the members Clef uses are typed;
// trailing members of each interface are omitted.

internal static class Uia
{
    public const int TreeScopeChildren = 2;
    public const int TreeScopeDescendants = 4;

    public const int ProcessIdProperty = 30002;
    public const int ControlTypeProperty = 30003;
    public const int NameProperty = 30005;
    public const int AutomationIdProperty = 30011;
    public const int ClassNameProperty = 30012;

    public const int InvokePattern = 10000;
    public const int SelectionItemPattern = 10010;
    public const int TogglePattern = 10015;
    public const int ExpandCollapsePattern = 10005;

    public const int ButtonControlType = 50000;

    public const int ToggleOn = 1;
}

[ComImport, Guid("FF48DBA4-60EF-4201-AA87-54103EEF594E")]
internal sealed class CUIAutomationComObject { }

[ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("30CBE57D-D9D0-452A-AB13-7AC5AC4825EE")]
internal interface IUIAutomation
{
    void CompareElements(IUIAutomationElement el1, IUIAutomationElement el2, out int areSame);
    void CompareRuntimeIds(IntPtr runtimeId1, IntPtr runtimeId2, out int areSame);
    void GetRootElement(out IUIAutomationElement root);
    void ElementFromHandle(IntPtr hwnd, out IUIAutomationElement element);
    void ElementFromPoint(long point, out IUIAutomationElement element);
    void GetFocusedElement(out IUIAutomationElement element);
    void GetRootElementBuildCache(IntPtr cacheRequest, out IUIAutomationElement root);
    void ElementFromHandleBuildCache(IntPtr hwnd, IntPtr cacheRequest, out IUIAutomationElement element);
    void ElementFromPointBuildCache(long point, IntPtr cacheRequest, out IUIAutomationElement element);
    void GetFocusedElementBuildCache(IntPtr cacheRequest, out IUIAutomationElement element);
    void CreateTreeWalker(IUIAutomationCondition condition, out IntPtr walker);
    void get_ControlViewWalker(out IntPtr walker);
    void get_ContentViewWalker(out IntPtr walker);
    void get_RawViewWalker(out IntPtr walker);
    void get_RawViewCondition(out IUIAutomationCondition condition);
    void get_ControlViewCondition(out IUIAutomationCondition condition);
    void get_ContentViewCondition(out IUIAutomationCondition condition);
    void CreateCacheRequest(out IntPtr cacheRequest);
    void CreateTrueCondition(out IUIAutomationCondition condition);
    void CreateFalseCondition(out IUIAutomationCondition condition);
    void CreatePropertyCondition(int propertyId, [MarshalAs(UnmanagedType.Struct)] object value, out IUIAutomationCondition condition);
    void CreatePropertyConditionEx(int propertyId, [MarshalAs(UnmanagedType.Struct)] object value, int flags, out IUIAutomationCondition condition);
    void CreateAndCondition(IUIAutomationCondition condition1, IUIAutomationCondition condition2, out IUIAutomationCondition condition);
}

[ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("352FFBA8-0973-437C-A61F-F64CAFD81DF9")]
internal interface IUIAutomationCondition { }

[ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("D22108AA-8AC5-49A5-837B-37BBB3D7591E")]
internal interface IUIAutomationElement
{
    void SetFocus();
    void GetRuntimeId(out IntPtr runtimeId);
    void FindFirst(int scope, IUIAutomationCondition condition, out IUIAutomationElement? found);
    void FindAll(int scope, IUIAutomationCondition condition, out IUIAutomationElementArray? found);
    void FindFirstBuildCache(int scope, IUIAutomationCondition condition, IntPtr cacheRequest, out IUIAutomationElement? found);
    void FindAllBuildCache(int scope, IUIAutomationCondition condition, IntPtr cacheRequest, out IUIAutomationElementArray? found);
    void BuildUpdatedCache(IntPtr cacheRequest, out IUIAutomationElement updated);
    void GetCurrentPropertyValue(int propertyId, [MarshalAs(UnmanagedType.Struct)] out object value);
    void GetCurrentPropertyValueEx(int propertyId, int ignoreDefaultValue, [MarshalAs(UnmanagedType.Struct)] out object value);
    void GetCachedPropertyValue(int propertyId, [MarshalAs(UnmanagedType.Struct)] out object value);
    void GetCachedPropertyValueEx(int propertyId, int ignoreDefaultValue, [MarshalAs(UnmanagedType.Struct)] out object value);
    void GetCurrentPatternAs(int patternId, ref Guid riid, out IntPtr pattern);
    void GetCachedPatternAs(int patternId, ref Guid riid, out IntPtr pattern);
    void GetCurrentPattern(int patternId, [MarshalAs(UnmanagedType.IUnknown)] out object? pattern);
    void GetCachedPattern(int patternId, [MarshalAs(UnmanagedType.IUnknown)] out object? pattern);
}

[ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("14314595-B4BC-4055-95F2-58F2E42C9855")]
internal interface IUIAutomationElementArray
{
    void get_Length(out int length);
    void GetElement(int index, out IUIAutomationElement element);
}

[ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("FB377FBE-8EA6-46D5-9C73-6499642D3059")]
internal interface IUIAutomationInvokePattern
{
    void Invoke();
}

[ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("94CF8058-9B8D-4AB9-8BFD-4CD0A33C8C70")]
internal interface IUIAutomationTogglePattern
{
    void Toggle();
    void get_CurrentToggleState(out int state);
}

[ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("A8EFA66A-0FDA-421A-9194-38021F3578EA")]
internal interface IUIAutomationSelectionItemPattern
{
    void Select();
    void AddToSelection();
    void RemoveFromSelection();
    void get_CurrentIsSelected(out int selected);
}

[ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("619BE086-1F4E-4EE4-BAFA-210128738730")]
internal interface IUIAutomationExpandCollapsePattern
{
    void Expand();
    void Collapse();
    void get_CurrentExpandCollapseState(out int state);
}
