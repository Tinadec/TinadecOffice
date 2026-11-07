using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;

namespace TinadecTools.Tools.Desktop;

/// <summary>
/// Minimal UI Automation COM interop plus the enumeration facade behind desktop_find_element.
///
/// The vtable slot order below is VERIFIED, not recalled: it was dumped from CsWin32-generated
/// bindings (official Windows metadata) and proven at runtime against real windows. It is the
/// OPPOSITE of what the MSDN docs page layout suggests: SetFocus/GetRuntimeId/FindFirst/FindAll
/// come FIRST (slots 0-3 after IUnknown) and the Current* property getters start at slot 17,
/// so CurrentName is slot 20 and CurrentBoundingRectangle slot 40. Reordering anything here
/// corrupts vtable calls silently. GUIDs are registry-verified (HKCR\Interface\{...}).
/// Kept as a hand-declared minimal slice so the product carries no extra package dependency.
/// </summary>
internal static class DesktopAutomation
{
    /// <summary>One enumerated element, with everything desktop_click-by-element needs.</summary>
    internal sealed record ElementInfo(
        string? Name,
        string? AutomationId,
        int ControlType,
        string? ClassName,
        int Left,
        int Top,
        int Right,
        int Bottom,
        nint NativeWindowHandle,
        bool IsEnabled,
        bool IsOffscreen)
    {
        public int Width => Right - Left;
        public int Height => Bottom - Top;
        public int CenterX => (Left + Right) / 2;
        public int CenterY => (Top + Bottom) / 2;
    }

    internal static string ControlTypeName(int typeId) => typeId switch
    {
        50000 => "Button", 50002 => "CheckBox", 50003 => "ComboBox", 50004 => "Edit",
        50007 => "ListItem", 50008 => "List", 50009 => "Menu", 50010 => "MenuBar",
        50011 => "MenuItem", 50013 => "RadioButton", 50018 => "Tab", 50019 => "TabItem",
        50020 => "Text", 50021 => "ToolBar", 50022 => "ToolTip", 50023 => "Tree",
        50024 => "TreeItem", 50025 => "Custom", 50026 => "Group", 50028 => "DataGrid",
        50029 => "DataItem", 50030 => "Document", 50031 => "SplitButton", 50032 => "Window",
        50033 => "Pane", 50034 => "Header", 50035 => "HeaderItem", 50036 => "Table",
        50037 => "TitleBar", 50038 => "Separator", 50001 => "Calendar", 50005 => "Hyperlink",
        50006 => "Image", 50012 => "ProgressBar", 50014 => "ScrollBar", 50015 => "Slider",
        50016 => "Spinner", 50017 => "StatusBar", 50027 => "Thumb",
        _ => $"Type{typeId}",
    };

    // ── cross-process call guard ────────────────────────────────────────────────

    /// <summary>
    /// Hard cap for one cross-process UIA call batch. Cross-process UIA is synchronous COM
    /// into the target's process: a busy or hung provider blocks the call FOREVER
    /// (probe-measured: a Value call against an unresponsive element stuck for 4.5 minutes,
    /// with no timeout of any kind in UIA itself). Every entry point of this class runs its
    /// COM batch under this cap so the tool host can never lose a thread to a frozen
    /// application. Mutable so tests can pin the guard behavior quickly.
    /// </summary>
    internal static TimeSpan UiaCallTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Thrown when a cross-process UIA batch exceeded <see cref="UiaCallTimeout"/>. The
    /// batch is ABANDONED, never assumed to have landed - callers surface it as the stable
    /// desktop_uia_timeout error so the model re-reads the target's state instead of
    /// trusting that the action happened.
    /// </summary>
    internal sealed class UiaCallTimeoutException : TimeoutException
    {
        public UiaCallTimeoutException(string operation)
            : base($"a cross-process UI Automation call ({operation}) did not return within {UiaCallTimeout.TotalSeconds:0}s; the target window's process is busy or hung. The call was ABANDONED - do not assume the action landed; once the target responds, check its state (desktop_read_text / desktop_find_element) before retrying.")
        {
        }
    }

    /// <summary>
    /// Runs one UIA call batch on a fresh MTA worker bounded by <see cref="UiaCallTimeout"/>.
    /// On expiry the worker is abandoned - a blocked COM call cannot be cancelled, so that
    /// thread stays parked inside the provider until the target process answers or dies -
    /// and the caller gets <see cref="UiaCallTimeoutException"/> instead of losing its own
    /// thread (the TinadecTools host serves every tool; one frozen app must not freeze the
    /// agent). MTA is deliberate: the multithreaded apartment is process-wide, so element
    /// proxies created by one worker stay usable by every later worker.
    /// </summary>
    internal static T RunOnUiaWorker<T>(string operation, Func<T> batch)
    {
        var value = default(T);
        Exception? failure = null;
        var worker = new Thread(() =>
        {
            try { value = batch(); }
            catch (Exception exception) { failure = exception; }
        });
        worker.IsBackground = true;
        worker.SetApartmentState(ApartmentState.MTA);
        worker.Start();
        if (!worker.Join(UiaCallTimeout))
            throw new UiaCallTimeoutException(operation);
        if (failure is not null)
            ExceptionDispatchInfo.Capture(failure).Throw();
        return value!;
    }

    /// <summary>Maps a friendly control type name (as returned to the model) back to its id, or null.</summary>
    internal static int? ControlTypeId(string name) => name.ToLowerInvariant() switch
    {
        "button" => 50000, "calendar" => 50001, "checkbox" => 50002, "combobox" => 50003,
        "edit" => 50004, "hyperlink" => 50005, "image" => 50006, "listitem" => 50007,
        "list" => 50008, "menu" => 50009, "menubar" => 50010, "menuitem" => 50011,
        "progressbar" => 50012, "radiobutton" => 50013, "scrollbar" => 50014, "slider" => 50015,
        "spinner" => 50016, "statusbar" => 50017, "tab" => 50018, "tabitem" => 50019,
        "text" => 50020, "toolbar" => 50021, "tooltip" => 50022, "tree" => 50023,
        "treeitem" => 50024, "custom" => 50025, "group" => 50026, "thumb" => 50027,
        "datagrid" => 50028, "dataitem" => 50029, "document" => 50030, "splitbutton" => 50031,
        "window" => 50032, "pane" => 50033, "header" => 50034, "headeritem" => 50035,
        "table" => 50036, "titlebar" => 50037, "separator" => 50038,
        _ => null,
    };

    /// <summary>
    /// Enumerates the descendant elements of a window through UI Automation and returns the
    /// bounded, filtered page (each entry keeps its live COM element for the click paths)
    /// plus the total match count. Paging via offset; offscreen=false hides elements
    /// outside the visible area. scroll=true pages VIRTUALIZED lists (explorer's file
    /// view realizes only the visible items): when nothing matches, the vertically
    /// scrollable container is scrolled a page at a time and the enumeration retried,
    /// until the target is realized or the bottom is reached (the return then says how
    /// many scroll steps ran, so "not found" can be told apart from "not realized yet").
    /// Works for windows on any desktop of this session (probe-measured, including
    /// TinadecIsolated). A UIA failure is reported as a stable error code by the caller,
    /// never as a half-empty list.
    /// </summary>
    internal static (string? Error, (ElementInfo Info, IUIAutomationElement Raw)[] Page, int Total, int ScrollSteps) FindElements(
        nint windowHandle,
        string? nameContains,
        string? automationIdContains,
        string? controlType,
        string? classNameContains,
        int limit,
        int offset = 0,
        bool includeOffscreen = true,
        bool scroll = false)
    {
        var (error, page, total) = FindOnce(windowHandle, nameContains, automationIdContains, controlType, classNameContains, limit, offset, includeOffscreen);
        if (!scroll || error is not null || total > 0) return (error, page, total, 0);

        // the deep scroll: page EVERY vertically scrollable container (explorer has the
        // navigation tree AND the file list - the first one alone is not the target) until
        // the filter matches something or all of them stop moving at their bottoms
        // (virtualized lists realize their items only around the viewport)
        for (var step = 1; step <= 40; step++)
        {
            var containers = FindScrollContainers(windowHandle);
            if (containers.Count == 0) return (null, [], 0, step - 1);
            var moved = false;
            foreach (var container in containers)
            {
                try
                {
                    // one batch per container: percent read + scroll + percent read; a
                    // UiaCallTimeoutException propagates and aborts the whole deep scroll
                    // (a target hung here will hang the retry enumeration just the same)
                    var (before, after) = RunOnUiaWorker("list scroll", () =>
                    {
                        var beforePercent = container.CurrentVerticalScrollPercent();
                        container.Scroll(2 /* NoAmount horizontal */, 3 /* LargeIncrement vertical */);
                        return (beforePercent, container.CurrentVerticalScrollPercent());
                    });
                    moved |= Math.Abs(after - before) >= 0.01;
                }
                catch (COMException)
                {
                    // a disconnected marshaler - this container is done
                }
            }
            Thread.Sleep(200); // let the virtualization realize the new viewport
            (error, page, total) = FindOnce(windowHandle, nameContains, automationIdContains, controlType, classNameContains, limit, offset, includeOffscreen);
            if (error is not null || total > 0) return (error, page, total, step);
            if (!moved) return (null, [], 0, step); // every container hit its bottom
        }
        return (null, [], 0, 40);
    }

    /// <summary>Legacy tuple shape for callers that do not care about scroll steps.</summary>
    internal static (string? Error, (ElementInfo Info, IUIAutomationElement Raw)[] Page, int Total) FindElements(
        nint windowHandle,
        string? nameContains,
        string? automationIdContains,
        string? controlType,
        string? classNameContains,
        int limit,
        int offset = 0,
        bool includeOffscreen = true)
    {
        var (error, page, total, _) = FindElements(windowHandle, nameContains, automationIdContains, controlType, classNameContains, limit, offset, includeOffscreen, scroll: false);
        return (error, page, total);
    }

    private static (string? Error, (ElementInfo Info, IUIAutomationElement Raw)[] Page, int Total) FindOnce(
        nint windowHandle, string? nameContains, string? automationIdContains, string? controlType, string? classNameContains, int limit, int offset, bool includeOffscreen)
    {
        try
        {
            return RunOnUiaWorker("element tree enumeration", () =>
            {
                var root = Automation.ElementFromHandle(windowHandle);
                var raw = root.FindAll(TreeScope.Descendants, Automation.CreateTrueCondition());
                var typeId = controlType is null ? null : ControlTypeId(controlType);
                if (controlType is not null && typeId is null)
                    return ($"unknown control_type '{controlType}' (use names like Button, MenuItem, Edit)", Array.Empty<(ElementInfo, IUIAutomationElement)>(), 0);

                var matches = new List<(ElementInfo, IUIAutomationElement)>();
                for (var i = 0; i < raw.Length; i++)
                {
                    var element = raw.GetElement(i);
                    var elementName = element.CurrentName();
                    var elementAutomationId = element.CurrentAutomationId();
                    var elementClassName = element.CurrentClassName();
                    if (nameContains is not null && !Contains(elementName, nameContains)) continue;
                    if (automationIdContains is not null && !Contains(elementAutomationId, automationIdContains)) continue;
                    if (classNameContains is not null && !Contains(elementClassName, classNameContains)) continue;
                    if (typeId is { } expected && element.CurrentControlType() != expected) continue;
                    var rect = element.CurrentBoundingRectangle();
                    var info = new ElementInfo(
                        elementName, elementAutomationId, element.CurrentControlType(), elementClassName,
                        rect.Left, rect.Top, rect.Right, rect.Bottom,
                        element.CurrentNativeWindowHandle(),
                        element.CurrentIsEnabled(),
                        element.CurrentIsOffscreen());
                    if (!includeOffscreen && info.IsOffscreen) continue;
                    matches.Add((info, element));
                }

                var page = matches.Skip(Math.Max(0, offset)).Take(limit).ToArray();
                return (null, page, matches.Count);
            });
        }
        catch (Exception exception) when (exception is COMException or InvalidOperationException)
        {
            return ($"UI Automation enumeration failed: {exception.Message}", [], 0);
        }
    }

    /// <summary>Every vertically scrollable descendant (explorer: the navigation tree AND
    /// the file list) - the deep scroll pages all of them, since which one holds the
    /// target is not knowable up front.</summary>
    private static List<IUIAutomationScrollPattern> FindScrollContainers(nint windowHandle)
    {
        try
        {
            return RunOnUiaWorker("scroll container discovery", () =>
            {
                var containers = new List<IUIAutomationScrollPattern>();
                var root = Automation.ElementFromHandle(windowHandle);
                var raw = root.FindAll(TreeScope.Descendants, Automation.CreateTrueCondition());
                for (var i = 0; i < raw.Length; i++)
                {
                    var element = raw.GetElement(i);
                    if (!TryPattern(element, ScrollPatternId, ScrollPatternIid, out IUIAutomationScrollPattern scroll)) continue;
                    try
                    {
                        if (scroll.CurrentVerticallyScrollable()) containers.Add(scroll);
                    }
                    catch (COMException)
                    {
                        // a disconnected marshaler - skip this candidate
                    }
                }
                return containers;
            });
        }
        catch (Exception exception) when (exception is COMException or InvalidOperationException)
        {
            // enumeration failed - nothing scrollable was collected
            return [];
        }
    }

    /// <summary>Outcome of trying the Invoke pattern on an element.</summary>
    internal enum InvokeOutcome
    {
        Invoked,
        NotInvokable,
        ElementGone,
        Failed,
    }

    /// <summary>
    /// The semantic click for elements without a native window handle (WPF / UWP / WinUI):
    /// UIA's Invoke pattern performs the control's default action with no coordinates
    /// involved at all, so no DPI space can shift it. Measured need: notepad's and
    /// explorer's virtual buttons all report class names but no hwnds.
    /// </summary>
    internal static InvokeOutcome TryInvoke(IUIAutomationElement element) =>
        RunOnUiaWorker("Invoke pattern", () =>
        {
            try
            {
                element.GetCurrentPatternAs(InvokePatternId, in InvokePatternIid, out var pattern);
                if (pattern == 0) return InvokeOutcome.NotInvokable;
                var invoke = (IUIAutomationInvokePattern)Marshal.GetObjectForIUnknown(pattern);
                invoke.Invoke();
                return InvokeOutcome.Invoked;
            }
            catch (COMException exception) when (exception.HResult == unchecked((int)0x80131505) || exception.Message.Contains("not available", StringComparison.OrdinalIgnoreCase))
            {
                return InvokeOutcome.ElementGone;
            }
            catch (COMException)
            {
                return InvokeOutcome.Failed;
            }
        });

    private const int InvokePatternId = 10000;
    private const int ValuePatternId = 10002;
    private const int RangeValuePatternId = 10003;
    private const int ScrollPatternId = 10004;
    private const int ExpandCollapsePatternId = 10005;
    private const int SelectionItemPatternId = 10010;
    private const int ScrollItemPatternId = 10015;
    private const int TogglePatternId = 10016;

    private static readonly Guid InvokePatternIid = new("fb377fbe-8ea6-46d5-9c73-6499642d3059");
    private static readonly Guid ValuePatternIid = new("a94cd8b1-0844-4cd6-9d2d-640537ab39e9");
    private static readonly Guid ExpandCollapsePatternIid = new("619be086-1f4e-4ee4-bafa-210128738730");
    private static readonly Guid SelectionItemPatternIid = new("a8efa66a-0fda-421a-9194-38021f3578ea");
    private static readonly Guid ScrollItemPatternIid = new("b488300f-d015-4f19-9c29-bb595e3645ef");
    private static readonly Guid TogglePatternIid = new("94cf8058-9b8d-4ab9-8bfd-4cd0a33c8c70");
    private static readonly Guid RangeValuePatternIid = new("59213f4f-7346-49e5-b120-80555987a148");
    private static readonly Guid ScrollPatternIid = new("88f4d42a-e881-459d-a77c-73bbbb7e02dc");

    /// <summary>
    /// Sets a range-capable element's value (sliders, progress, zoom) through the
    /// RangeValue pattern, returning the value the element reports afterwards - the
    /// read-back is the evidence, never the setter's silent success.
    /// </summary>
    internal static (bool Set, double? ReadBack, string? Error) TrySetRangeValue(IUIAutomationElement element, double value) =>
        RunOnUiaWorker<(bool Set, double? ReadBack, string? Error)>("RangeValue set", () =>
        {
            if (!TryPattern(element, RangeValuePatternId, RangeValuePatternIid, out IUIAutomationRangeValuePattern range))
                return (false, null, "the element does not support the RangeValue pattern (not a slider/progress control)");
            try
            {
                range.SetValue(value);
                return (true, range.CurrentValue(), null);
            }
            catch (Exception exception) when (exception is COMException or InvalidOperationException)
            {
                return (false, null, $"setting the value failed ({exception.Message}) - the control may be read-only or out of range");
            }
        });

    /// <summary>Reads a range-capable element's current value, or null when it has no RangeValue pattern.</summary>
    internal static double? CurrentRangeValue(IUIAutomationElement element) =>
        RunOnUiaWorker<double?>("RangeValue read", () =>
            TryPattern(element, RangeValuePatternId, RangeValuePatternIid, out IUIAutomationRangeValuePattern range) ? range.CurrentValue() : null);

    /// <summary>
    /// The pattern-aware click for virtual elements, in stability order: Invoke (the
    /// control's own default action) > Toggle (checkbox/radio semantics) > SelectionItem
    /// (list/tab/tree items) > ExpandCollapse (dropdowns open). A pattern that exists but
    /// fails to act (marshaler disconnection, disabled control) degrades to the next one
    /// instead of failing the whole click. Returns which pattern did the job so the result
    /// is auditable; null means none worked - the caller falls back to the rect path.
    /// </summary>
    internal static string? ClickByPattern(IUIAutomationElement element) =>
        RunOnUiaWorker("pattern click", () =>
        {
            if (TryPattern(element, InvokePatternId, InvokePatternIid, out IUIAutomationInvokePattern invoke) && Act(() => invoke.Invoke()))
                return "uia-invoke";
            if (TryPattern(element, TogglePatternId, TogglePatternIid, out IUIAutomationTogglePattern toggle) && Act(() => toggle.Toggle()))
                return "uia-toggle";
            if (TryPattern(element, SelectionItemPatternId, SelectionItemPatternIid, out IUIAutomationSelectionItemPattern select) && Act(() => select.Select()))
                return "uia-select";
            if (TryPattern(element, ExpandCollapsePatternId, ExpandCollapsePatternIid, out IUIAutomationExpandCollapsePattern expand) && Act(() => expand.Expand()))
                return "uia-expand";
            return null;
        });

    /// <summary>Debug form: also reports why each pattern was skipped (probe tooling).</summary>
    internal static string? ClickByPattern(IUIAutomationElement element, out string trace)
    {
        var steps = new List<string>();
        string? result = null;
        (result, steps) = RunOnUiaWorker("pattern click (traced)", () =>
        {
            var traceSteps = new List<string>();
            string? acted = null;
            if (TryPattern(element, InvokePatternId, InvokePatternIid, out IUIAutomationInvokePattern invoke))
            {
                var ok = Act(() => invoke.Invoke(), out var error);
                traceSteps.Add($"invoke:{(ok ? "ok" : error)}");
                if (ok) acted = "uia-invoke";
            }
            else traceSteps.Add("invoke:absent");
            if (acted is null && TryPattern(element, TogglePatternId, TogglePatternIid, out IUIAutomationTogglePattern toggle))
            {
                var ok = Act(() => toggle.Toggle(), out var error);
                traceSteps.Add($"toggle:{(ok ? "ok" : error)}");
                if (ok) acted = "uia-toggle";
            }
            else if (acted is null) traceSteps.Add("toggle:absent");
            if (acted is null && TryPattern(element, SelectionItemPatternId, SelectionItemPatternIid, out IUIAutomationSelectionItemPattern select))
            {
                var ok = Act(() => select.Select(), out var error);
                traceSteps.Add($"select:{(ok ? "ok" : error)}");
                if (ok) acted = "uia-select";
            }
            else if (acted is null) traceSteps.Add("select:absent");
            if (acted is null && TryPattern(element, ExpandCollapsePatternId, ExpandCollapsePatternIid, out IUIAutomationExpandCollapsePattern expand))
            {
                var ok = Act(() => expand.Expand(), out var error);
                traceSteps.Add($"expand:{(ok ? "ok" : error)}");
                if (ok) acted = "uia-expand";
            }
            else if (acted is null) traceSteps.Add("expand:absent");
            return (acted, traceSteps);
        });
        trace = string.Join(" | ", steps);
        return result;
    }

    private static bool Act(Action action, out string? error)
    {
        try
        {
            action();
            error = null;
            return true;
        }
        catch (Exception exception) when (exception is COMException or InvalidOperationException)
        {
            error = exception.GetType().Name;
            return false;
        }
    }

    /// <summary>A pattern action that throws (disconnected RCW, busy provider) counts as
    /// that pattern not working; the next pattern gets its chance.</summary>
    private static bool Act(Action action)
    {
        try
        {
            action();
            return true;
        }
        catch (Exception exception) when (exception is COMException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>Sets an element's text through the Value pattern (edit fields and documents);
    /// returns false when the element is not value-capable or is read-only. This is the exact
    /// call shape the probe measured hanging for 4.5 minutes against an unresponsive element,
    /// so the whole batch runs under the cross-process guard.</summary>
    internal static bool TrySetValue(IUIAutomationElement element, string text) =>
        RunOnUiaWorker("Value set", () =>
        {
            if (!TryPattern(element, ValuePatternId, ValuePatternIid, out IUIAutomationValuePattern value))
                return false;
            if (value.CurrentIsReadOnly())
                return false;
            value.SetValue(text);
            return true;
        });

    /// <summary>Reads an element's current value through the Value pattern, or null.</summary>
    internal static string? CurrentValue(IUIAutomationElement element) =>
        RunOnUiaWorker<string?>("Value read", () =>
            TryPattern(element, ValuePatternId, ValuePatternIid, out IUIAutomationValuePattern value) ? value.CurrentValue() : null);

    /// <summary>The element's toggle state (0 off, 1 on, 2 indeterminate), or null when it has no Toggle pattern.</summary>
    internal static int? CurrentToggleState(IUIAutomationElement element) =>
        RunOnUiaWorker<int?>("ToggleState read", () =>
            TryPattern(element, TogglePatternId, TogglePatternIid, out IUIAutomationTogglePattern toggle) ? toggle.CurrentToggleState() : null);

    /// <summary>Whether the element is currently selected, or null when it has no SelectionItem pattern.</summary>
    internal static bool? CurrentIsSelected(IUIAutomationElement element) =>
        RunOnUiaWorker<bool?>("SelectionItem read", () =>
            TryPattern(element, SelectionItemPatternId, SelectionItemPatternIid, out IUIAutomationSelectionItemPattern select) ? select.CurrentIsSelected() : null);

    /// <summary>
    /// Scrolls an element into view through the ScrollItem pattern (its scroll container
    /// brings it into the visible area). Returns false when the element does not support it.
    /// </summary>
    internal static bool TryScrollIntoView(IUIAutomationElement element) =>
        RunOnUiaWorker("ScrollIntoView", () =>
        {
            if (!TryPattern(element, ScrollItemPatternId, ScrollItemPatternIid, out IUIAutomationScrollItemPattern scroll))
                return false;
            scroll.ScrollIntoView();
            return true;
        });

    /// <summary>Debug (probe tooling): which of the six known pattern IDs the element
    /// answers with a non-null interface (each with its OWN iid - QI with a mismatched
    /// iid crashes some in-proc providers hard).</summary>
    internal static List<string> SupportedPatternIds(IUIAutomationElement element) =>
        RunOnUiaWorker("pattern availability probe", () =>
        {
            var checks = new (string Name, int Id, Guid Iid)[]
            {
                ("invoke", InvokePatternId, InvokePatternIid),
                ("value", ValuePatternId, ValuePatternIid),
                ("expand", ExpandCollapsePatternId, ExpandCollapsePatternIid),
                ("select", SelectionItemPatternId, SelectionItemPatternIid),
                ("toggle", TogglePatternId, TogglePatternIid),
            };
            var supported = new List<string>();
            foreach (var (name, id, iid) in checks)
            {
                try
                {
                    element.GetCurrentPatternAs(id, in iid, out var pointer);
                    if (pointer != 0) supported.Add($"{name}({id})");
                }
                catch (COMException)
                {
                    // not an answer for this element
                }
            }
            return supported;
        });

    private static bool TryPattern<T>(IUIAutomationElement element, int patternId, in Guid iid, out T pattern) where T : class
    {
        try
        {
            element.GetCurrentPatternAs(patternId, in iid, out var pointer);
            if (pointer == 0)
            {
                pattern = null!;
                return false;
            }
            pattern = (T)Marshal.GetObjectForIUnknown(pointer);
            return true;
        }
        catch (COMException)
        {
            pattern = null!;
            return false;
        }
    }

    private static bool Contains(string? text, string fragment) =>
        text is not null && text.Contains(fragment, StringComparison.OrdinalIgnoreCase);

    // ── the verified interop ────────────────────────────────────────────────────

    private static IUIAutomation? _automation;
    private static IUIAutomation Automation => _automation ??= (IUIAutomation)new CUIAutomation();

    [ComImport, Guid("ff48dba4-60ef-4201-aa87-54103eef594e")]
    private class CUIAutomation { }

    [ComImport, Guid("30cbe57d-d9d0-452a-ab13-7ac5ac4825ee"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomation
    {
        void _0(); // CompareElements
        void _1(); // CompareRuntimeIds
        void _2(); // GetRootElement
        IUIAutomationElement ElementFromHandle(nint handle); // 3
        void _4();
        void _5();
        void _6();
        void _7();
        void _8();
        void _9();
        void _10();
        void _11();
        void _12();
        void _13();
        void _14();
        void _15();
        void _16();
        void _17();
        nint CreateTrueCondition(); // 18
    }

    [ComImport, Guid("d22108aa-8ac5-49a5-837b-37bbb3d7591e"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IUIAutomationElement
    {
        void SetFocus();                                                     // 0
        void _1();  // GetRuntimeId
        void _2();  // FindFirst
        IUIAutomationElementArray FindAll(TreeScope scope, nint condition);  // 3
        void _4();  // FindFirstBuildCache
        void _5();  // FindAllBuildCache
        void _6();  // BuildUpdatedCache
        /// <summary>Property read path (slot 7). VARIANT out marshals to bool/int/string
        /// or null for VT_EMPTY. Used by the IsPatternAvailable property ids (30100+) -
        /// the cross-check against GetCurrentPatternAs when a pattern looks absent.</summary>
        void GetCurrentPropertyValue(int propertyId, [MarshalAs(UnmanagedType.Struct)] out object? value);
        void _8();  // GetCurrentPropertyValueEx
        void _9();  // GetCachedPropertyValue
        void _10(); // GetCachedPropertyValueEx
        void GetCurrentPatternAs(int patternId, in Guid riid, out nint patternObject); // 11
        void _12(); // GetCachedPatternAs
        void _13(); // GetCurrentPattern
        void _14(); // GetCachedPattern
        void _15(); // GetCachedParent
        void _16(); // GetCachedChildren
        [PreserveSig] int get_CurrentProcessId(out int id); // 17
        int CurrentControlType();                           // 18
        string CurrentLocalizedControlType();               // 19
        string CurrentName();                               // 20
        void _21(); // AcceleratorKey
        void _22(); // AccessKey
        void _23(); // HasKeyboardFocus
        void _24(); // IsKeyboardFocusable
        bool CurrentIsEnabled();                            // 25
        string CurrentAutomationId();                       // 26
        string CurrentClassName();                          // 27
        void _28(); // HelpText
        void _29(); // Culture
        void _30(); // IsControlElement
        void _31(); // IsContentElement
        void _32(); // IsPassword
        nint CurrentNativeWindowHandle();                   // 33
        void _34(); // ItemType
        bool CurrentIsOffscreen();                          // 35
        void _36(); // Orientation
        void _37(); // FrameworkId
        void _38(); // IsRequiredForForm
        void _39(); // ItemStatus
        UiaRect CurrentBoundingRectangle();                 // 40
    }

    [ComImport, Guid("14314595-b4bc-4055-95f2-58f2e42c9855"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IUIAutomationElementArray
    {
        int Length { get; }
        IUIAutomationElement GetElement(int index);
    }

    [ComImport, Guid("fb377fbe-8ea6-46d5-9c73-6499642d3059"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IUIAutomationInvokePattern
    {
        void Invoke();
    }

    // The pattern interfaces below follow the method order CsWin32 dumped from official
    // metadata (probe/uiainterop, round 11); unused members keep their slot as placeholders.

    [ComImport, Guid("94cf8058-9b8d-4ab9-8bfd-4cd0a33c8c70"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IUIAutomationTogglePattern
    {
        void Toggle();
        int CurrentToggleState();
        void _cachedState();
    }

    [ComImport, Guid("a94cd8b1-0844-4cd6-9d2d-640537ab39e9"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IUIAutomationValuePattern
    {
        void SetValue(string value);
        string CurrentValue();
        bool CurrentIsReadOnly();
        void _cachedValue();
        void _cachedReadOnly();
    }

    [ComImport, Guid("a8efa66a-0fda-421a-9194-38021f3578ea"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IUIAutomationSelectionItemPattern
    {
        void Select();
        void _addToSelection();
        void _removeFromSelection();
        bool CurrentIsSelected();
        void _currentContainer();
        void _cachedIsSelected();
        void _cachedContainer();
    }

    [ComImport, Guid("619be086-1f4e-4ee4-bafa-210128738730"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IUIAutomationExpandCollapsePattern
    {
        void Expand();
        void Collapse();
        int CurrentExpandCollapseState();
        void _cachedState();
    }

    /// <summary>Scroll pattern for list containers (vtable per CsWin32 metadata export,
    /// probe/uiainterop round 13). ScrollAmount: LargeDecrement=0, SmallDecrement=1,
    /// NoAmount=2, LargeIncrement=3, SmallIncrement=4.</summary>
    [ComImport, Guid("88f4d42a-e881-459d-a77c-73bbbb7e02dc"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IUIAutomationScrollPattern
    {
        void Scroll(int horizontalAmount, int verticalAmount);
        void SetScrollPercent(double horizontalPercent, double verticalPercent);
        double CurrentHorizontalScrollPercent();
        double CurrentVerticalScrollPercent();
        double CurrentHorizontalViewSize();
        double CurrentVerticalViewSize();
        bool CurrentHorizontallyScrollable();
        bool CurrentVerticallyScrollable();
        void _cachedHorizontalPercent();
        void _cachedVerticalPercent();
        void _cachedHorizontalViewSize();
        void _cachedVerticalViewSize();
        void _cachedHorizontallyScrollable();
        void _cachedVerticallyScrollable();
    }

    [ComImport, Guid("b488300f-d015-4f19-9c29-bb595e3645ef"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IUIAutomationScrollItemPattern
    {
        void ScrollIntoView();
    }

    [ComImport, Guid("59213f4f-7346-49e5-b120-80555987a148"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IUIAutomationRangeValuePattern
    {
        void SetValue(double value);
        double CurrentValue();
        bool CurrentIsReadOnly();
        void _cachedValue();
        void _cachedReadOnly();
    }

    internal enum TreeScope
    {
        Element = 1,
        Children = 2,
        Descendants = 4,
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct UiaRect
    {
        public int Left, Top, Right, Bottom;
    }
}


