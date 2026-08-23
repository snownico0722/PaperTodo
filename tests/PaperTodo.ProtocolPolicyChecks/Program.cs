using System.IO;
using System.Reflection;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        try
        {
            var host = Assembly.Load("PaperTodo");
            var abstractions = Assembly.Load("PaperTodo.Plugin.Abstractions");
            CheckSingleHotkeyAuthority(host);
            CheckShortcutValidation(host);
            CheckRuntimeSlotAuthority(host);
            CheckRuntimeTransitions(host);
            CheckCapabilityNormalization(host);
            CheckSettingsLayoutManifest(host);
            CheckProtocolBoundaries(host);
            CheckSharedWebInfrastructure(host);
            CheckWebBodyNavigationIdentity(host);
            CheckManifestRuntimeAndMiniContracts(host);
            CheckGlobalTopBarPriority(host, abstractions);
            CheckAppRuntimeSettings(host, abstractions);
            Console.WriteLine("PaperTodo protocol policy checks passed.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static void CheckSingleHotkeyAuthority(Assembly host)
    {
        var managerType = RequireType(host, "PaperTodo.GlobalHotkeyManager");
        var brokerType = RequireType(host, "PaperTodo.GlobalHotkeyBroker");
        var failureType = RequireType(host, "PaperTodo.GlobalShortcutRegistrationFailure");
        Assert(
            managerType.GetFields(BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic)
                .All(field => field.FieldType.FullName != "System.Windows.Interop.HwndSource"),
            "GlobalHotkeyManager must not own a native HwndSource; the broker is the single authority.");
        Assert(
            brokerType.GetFields(BindingFlags.Static | BindingFlags.NonPublic)
                .Any(field => field.FieldType.FullName == "System.Windows.Interop.HwndSource"),
            "GlobalHotkeyBroker must own the process-level native hotkey window.");
        Assert(Enum.GetNames(failureType).Contains("Conflict"),
            "Cross-owner shortcut conflicts need their own failure status.");
        Assert(Enum.GetNames(failureType).Contains("UnregistrationFailed"),
            "Native hotkey teardown failures need their own failure status.");
        Assert(
            brokerType.GetMethod("TryRestoreGesture", BindingFlags.Static | BindingFlags.NonPublic) != null,
            "The broker must be able to restore registrations during rollback.");
        Assert(
            brokerType.GetMethod("IsCommittedNativeBinding", BindingFlags.Static | BindingFlags.NonPublic) != null,
            "Native hotkey dispatch must validate rollback residue against the committed owner plan.");
        var nativeBinding = brokerType.GetNestedType("NativeBinding", BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("GlobalHotkeyBroker.NativeBinding was not found.");
        Assert(
            nativeBinding.GetProperty("Gesture") != null,
            "Native hotkey bindings must retain their exact gesture so stale rollback residue can be rejected.");

        var tryApply = managerType
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Single(method => method.Name == "TryApply" && method.GetParameters().Length == 6);
        var suspend = managerType.GetMethod(
            "Suspend",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("GlobalHotkeyManager.Suspend was not found.");

        var ownerA = Activator.CreateInstance(managerType, nonPublic: true)
            ?? throw new InvalidOperationException("Could not create hotkey owner A.");
        var ownerB = Activator.CreateInstance(managerType, nonPublic: true)
            ?? throw new InvalidOperationException("Could not create hotkey owner B.");

        try
        {
            const string gesture = "Ctrl+Alt+Shift+U";
            var first = ApplyReservation(tryApply, ownerA, "a", gesture);
            Assert(first.Applied,
                "An inactive configured command must be reservable without RegisterHotKey.");

            suspend.Invoke(ownerA, null);
            var conflict = ApplyReservation(tryApply, ownerB, "b", gesture);
            Assert(!conflict.Applied && conflict.Failure == "Conflict",
                "Suspending an owner must keep its configured reservation and report a real conflict.");

            ((IDisposable)ownerA).Dispose();
            var afterRemoval = ApplyReservation(tryApply, ownerB, "b", gesture);
            Assert(afterRemoval.Applied,
                "Removing an owner must release its configured reservation.");
        }
        finally
        {
            try { ((IDisposable)ownerA).Dispose(); } catch { }
            try { ((IDisposable)ownerB).Dispose(); } catch { }
        }
    }

    private static (bool Applied, string Failure) ApplyReservation(
        MethodInfo tryApply,
        object manager,
        string commandId,
        string gesture)
    {
        var bindings = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [commandId] = gesture
        };
        var failureType = tryApply.GetParameters()[5].ParameterType.GetElementType()
            ?? throw new InvalidOperationException("Could not resolve hotkey failure enum.");
        object?[] args =
        [
            bindings,
            Array.Empty<string>(),
            new[] { commandId },
            false,
            null,
            Activator.CreateInstance(failureType)
        ];
        var applied = (bool)(tryApply.Invoke(manager, args) ?? false);
        return (applied, args[5]?.ToString() ?? "");
    }

    private static void CheckShortcutValidation(Assembly host)
    {
        var gestureType = RequireType(host, "PaperTodo.ShortcutGesture");
        var tryParse = gestureType.GetMethod(
            "TryParse",
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("ShortcutGesture.TryParse was not found.");

        object?[] invalid = ["Ctrl+999", Activator.CreateInstance(gestureType)];
        Assert(!(bool)(tryParse.Invoke(null, invalid) ?? false),
            "Undefined numeric Key enum values must not parse as shortcuts.");

        object?[] valid = ["Ctrl+Alt+A", Activator.CreateInstance(gestureType)];
        Assert((bool)(tryParse.Invoke(null, valid) ?? false),
            "A normal defined shortcut stopped parsing.");
    }

    private static void CheckRuntimeSlotAuthority(Assembly host)
    {
        var controller = RequireType(host, "PaperTodo.AppController");
        Assert(
            controller.GetNestedType("PluginAppRuntimeSlot", BindingFlags.NonPublic) != null,
            "Plugin app runtime must use one provider slot state object.");
        Assert(
            controller.GetField("_pluginAppRuntimeSlots", BindingFlags.Instance | BindingFlags.NonPublic) != null,
            "Plugin app runtime slot dictionary was not found.");

        var lifetime = controller.GetNestedType("PluginAppRuntimeLifetime", BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("PluginAppRuntimeLifetime was not found.");
        Assert(lifetime.GetField("_active", BindingFlags.Instance | BindingFlags.NonPublic)?.FieldType == typeof(int),
            "App runtime lifetime must expose one atomic integer active token to worker-side APIs.");
        Assert(lifetime.GetMethod("TryDeactivate", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) != null,
            "App runtime lifetime must support atomic revocation before teardown.");

        var obsoleteParallelState = new[]
        {
            "_pluginAppRuntimes",
            "_pluginAppRuntimeStarts",
            "_pluginAppRuntimeStartFailures",
            "_pluginAppRuntimeStartFailureCounts",
            "_pluginAppRuntimeRetryTokens",
            "_pluginAppRuntimeRestartRequests"
        };
        foreach (var fieldName in obsoleteParallelState)
        {
            Assert(
                controller.GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic) == null,
                $"Obsolete parallel app-runtime state remains: {fieldName}");
        }
    }

    private static void CheckRuntimeTransitions(Assembly host)
    {
        var controller = RequireType(host, "PaperTodo.AppController");
        var stateType = controller.GetNestedType("PluginAppRuntimeState", BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("PluginAppRuntimeState was not found.");
        var transitions = controller.GetNestedType("PluginAppRuntimeTransitions", BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("PluginAppRuntimeTransitions was not found.");

        object State(string name) => Enum.Parse(stateType, name);
        string InvokeState(string methodName, params object[] args) =>
            (transitions.GetMethod(methodName, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                ?.Invoke(null, args)
                ?? throw new InvalidOperationException($"Runtime transition was not found: {methodName}"))
            .ToString()!;

        Assert(InvokeState("BeginStart", State("Stopped")) == "Starting",
            "Stopped must enter Starting when a runtime start begins.");
        Assert(InvokeState("StartSucceeded", State("Starting")) == "Running",
            "Starting must enter Running after successful creation.");
        Assert(InvokeState("StartFailed", 1, 3) == "Backoff",
            "The first runtime failure must enter Backoff.");
        Assert(InvokeState("StartFailed", 3, 3) == "Backoff",
            "The third bounded retry failure must still enter Backoff.");
        Assert(InvokeState("StartFailed", 4, 3) == "Failed",
            "The failure after all bounded retries must enter Failed.");
        Assert(InvokeState("RetryElapsed", State("Backoff")) == "Stopped",
            "Expired backoff must return to Stopped so reconcile can restart.");
        Assert(InvokeState("DescriptorChanged", State("Failed")) == "Stopped",
            "A changed plugin descriptor must reopen an explicit recovery path from Failed.");
        Assert(InvokeState("DescriptorChanged", State("Running")) == "Running",
            "A descriptor recovery signal must not disturb a healthy running runtime.");

        var runtimeMatches = transitions.GetMethod(
            "RuntimeMatches",
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("RuntimeMatches was not found.");
        var current = Guid.NewGuid();
        Assert((bool)(runtimeMatches.Invoke(null, [current, current]) ?? false),
            "The current runtime id must accept its own callback.");
        Assert(!(bool)(runtimeMatches.Invoke(null, [current, Guid.NewGuid()]) ?? true),
            "A stale runtime id must not be allowed to affect a newer runtime.");
    }

    private static void CheckCapabilityNormalization(Assembly host)
    {
        var registry = RequireType(host, "PaperTodo.PaperBodyPluginRegistry");
        var manifestType = RequireType(host, "PaperTodo.PaperBodyPluginManifest");
        var normalize = registry.GetMethod(
            "NormalizeProtocolFeatures",
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("NormalizeProtocolFeatures was not found.");
        var capabilities = manifestType.GetProperty("Capabilities")
            ?? throw new InvalidOperationException("Manifest Capabilities property was not found.");

        var typoManifest = Activator.CreateInstance(manifestType, nonPublic: true)
            ?? throw new InvalidOperationException("Could not create plugin manifest.");
        capabilities.SetValue(typoManifest, new[] { "appRunime" });
        try
        {
            normalize.Invoke(null, new[] { typoManifest });
            throw new InvalidOperationException("Unknown capability typo was silently accepted.");
        }
        catch (TargetInvocationException ex) when (ex.InnerException is InvalidDataException)
        {
        }

        var canonicalManifest = Activator.CreateInstance(manifestType, nonPublic: true)
            ?? throw new InvalidOperationException("Could not create canonical plugin manifest.");
        capabilities.SetValue(
            canonicalManifest,
            new[] { " APPRUNTIME ", "textzoom", "noteLinks", "appRuntime" });
        normalize.Invoke(null, new[] { canonicalManifest });
        var values = (string[]?)capabilities.GetValue(canonicalManifest) ?? [];
        Assert(values.SequenceEqual(new[] { "appRuntime", "textZoom", "noteLinks" }),
            "Capability normalization did not produce one canonical representation.");
    }

    private static void CheckSettingsLayoutManifest(Assembly host)
    {
        var manifestType = RequireType(host, "PaperTodo.PaperBodyPluginManifest");
        var settingType = RequireType(host, "PaperTodo.PaperBodyPluginSettingManifest");
        var categoryType = RequireType(host, "PaperTodo.PaperBodyPluginSettingCategoryManifest");

        Assert(
            manifestType.GetProperty("PrimarySettings")?.PropertyType == typeof(int?),
            "Plugin manifest must expose optional primarySettings metadata.");
        Assert(
            manifestType.GetProperty("SettingCategories")?.PropertyType == categoryType.MakeArrayType(),
            "Plugin manifest must expose settingCategories metadata.");
        Assert(
            settingType.GetProperty("Category")?.PropertyType == typeof(string),
            "Plugin settings must expose an optional category name.");
        Assert(
            settingType.GetProperty("Quick") == null,
            "Per-setting quick metadata must not remain in the 2.0 settings contract.");
        Assert(
            categoryType.GetProperty("Name")?.PropertyType == typeof(string) &&
            categoryType.GetProperty("Column")?.PropertyType == typeof(string),
            "Setting categories must carry their display name and optional column placement.");
    }

    private static void CheckProtocolBoundaries(Assembly host)
    {
        var hostApi = RequireType(host, "PaperTodo.PaperBodyPluginHostApi");
        Assert(
            hostApi.GetMethod("EnsurePresentationProtocol", BindingFlags.Instance | BindingFlags.NonPublic) != null,
            "Own-paper presentation lacks an explicit protocol-version gate.");
    }

    private static void CheckSharedWebInfrastructure(Assembly host)
    {
        var infrastructure = RequireType(host, "PaperTodo.WebPluginRuntimeInfrastructure");
        var appRuntime = RequireType(host, "PaperTodo.WebPluginAppRuntime");
        Assert(
            infrastructure.GetProperty("JsonOptions", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic) != null,
            "Shared Web runtime serialization policy was not found.");
        Assert(
            appRuntime.GetField("JsonOptions", BindingFlags.Static | BindingFlags.NonPublic) == null,
            "WebPluginAppRuntime still owns a duplicate JSON bridge policy.");
        Assert(
            appRuntime.GetField("_startupReady", BindingFlags.Instance | BindingFlags.NonPublic) != null,
            "Web app runtime must wait for document readiness before it enters Running.");
    }

    private static void CheckWebBodyNavigationIdentity(Assembly host)
    {
        var body = RequireType(host, "PaperTodo.WebPaperBodySession");
        Assert(
            body.GetField("_documentNavigationId", BindingFlags.Instance | BindingFlags.NonPublic)?.FieldType == typeof(ulong),
            "Web body navigation completion must be tied to the current NavigationId.");
        Assert(
            body.GetField("_hasDocumentNavigation", BindingFlags.Instance | BindingFlags.NonPublic)?.FieldType == typeof(bool),
            "Web body must track whether a current navigation identity exists.");

        var canAccept = body.GetMethod(
            "CanAcceptDocumentMessage",
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Web body document-message guard was not found.");
        Assert((bool)(canAccept.Invoke(null, ["saveState", false, false]) ?? false),
            "A departing Web document must still be allowed to flush its final state.");
        Assert(!(bool)(canAccept.Invoke(null, ["hostRequest", false, false]) ?? true),
            "A stale or navigating Web document must not keep Workspace mutation authority.");
        Assert((bool)(canAccept.Invoke(null, ["hostRequest", true, true]) ?? false),
            "The current ready plugin document must retain normal host-request authority.");

        Assert(
            body.GetMethod("TryOpenExternalNavigation", BindingFlags.Static | BindingFlags.NonPublic) != null,
            "Web body must have an explicit system-shell path for external top-level navigation.");
    }

    private static void CheckManifestRuntimeAndMiniContracts(Assembly host)
    {
        var manifest = RequireType(host, "PaperTodo.PaperBodyPluginManifest");
        Assert(manifest.GetProperty("Runtime") != null,
            "Web app runtime entry is not represented in the canonical parsed manifest.");
        Assert(manifest.GetProperty("RuntimePath") != null,
            "Web app runtime resolved path is not cached by plugin discovery.");
        Assert(manifest.GetProperty("MiniMaxSize") != null,
            "miniMaxSize is not represented in the canonical parsed manifest.");

        var paperWindow = RequireType(host, "PaperTodo.PaperWindow");
        Assert(
            paperWindow.GetNestedType("MiniMaximumManifestView", BindingFlags.NonPublic) == null,
            "PaperWindow still owns a second miniMaxSize manifest parser.");
    }

    private static void CheckGlobalTopBarPriority(Assembly host, Assembly abstractions)
    {
        var action = RequireType(abstractions, "PaperTodo.Plugin.PaperTopBarAction");
        Assert(action.GetProperty("Priority")?.PropertyType == typeof(int),
            "PaperTopBarAction.Priority was not found.");

        var controller = RequireType(host, "PaperTodo.AppController");
        var maximumGlobal = controller.GetField(
            "MaximumGlobalTopBarActions",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert(
            maximumGlobal?.IsLiteral == true &&
            maximumGlobal.GetRawConstantValue() is int limit &&
            limit == 256,
            "Global Top Bar must keep a broad but finite 256-action descriptor cap.");

        var window = RequireType(host, "PaperTodo.PaperWindow");
        Assert(
            window.GetField("_pluginTopBarActionElements", BindingFlags.Instance | BindingFlags.NonPublic) != null,
            "Top Bar must retain action scope per button so Global actions can be fitted individually.");
    }

    private static void CheckAppRuntimeSettings(Assembly host, Assembly abstractions)
    {
        var settings = RequireType(abstractions, "PaperTodo.Plugin.IPaperAppRuntimeSettings");
        Assert(settings.GetProperty("Json")?.PropertyType == typeof(string),
            "App runtime settings must expose the current normalized JSON.");

        var context = RequireType(abstractions, "PaperTodo.Plugin.PaperAppRuntimeContext");
        Assert(context.GetProperty("Settings")?.PropertyType == settings,
            "PaperAppRuntimeContext.Settings was not found.");

        var controller = RequireType(host, "PaperTodo.AppController");
        Assert(
            controller.GetMethod(
                "RetryFailedPluginAppRuntimeAfterSettingsChanged",
                BindingFlags.Instance | BindingFlags.NonPublic) != null,
            "A Failed/Backoff app runtime must have a settings-change recovery path.");
    }

    private static Type RequireType(Assembly assembly, string name) =>
        assembly.GetType(name, throwOnError: true)
        ?? throw new InvalidOperationException($"Type was not found: {name}");

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
