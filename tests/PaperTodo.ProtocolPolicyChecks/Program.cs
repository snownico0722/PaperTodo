using System.IO;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

internal static partial class Program
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
            CheckSettingActionBehavior(host);
            CheckHiddenStartupVisibility(host);
            CheckProtocolBoundaries(host);
            CheckSharedWebInfrastructure(host);
            CheckWebRuntimeRequestRouting(host);
            CheckWebRuntimeBridgeBehavior(host);
            CheckUnifiedPluginRuntime(host, abstractions);
            CheckWebBodyNavigationIdentity(host);
            CheckManifestRuntimeAndMiniContracts(host);
            CheckGlobalTopBarPriority(host, abstractions);
            CheckPluginRuntimeSettings(host, abstractions);
            CheckProtocol21Contributions(host, abstractions);
            CheckPluginRuntimePersistenceGuards(host);
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
            controller.GetNestedType("PluginRuntimeSlot", BindingFlags.NonPublic) != null,
            "Plugin app runtime must use one provider slot state object.");
        Assert(
            controller.GetField("_pluginRuntimeSlots", BindingFlags.Instance | BindingFlags.NonPublic) != null,
            "Plugin app runtime slot dictionary was not found.");

        var lifetime = controller.GetNestedType("PluginRuntimeLifetime", BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("PluginRuntimeLifetime was not found.");
        Assert(lifetime.GetField("_active", BindingFlags.Instance | BindingFlags.NonPublic)?.FieldType == typeof(int),
            "App runtime lifetime must expose one atomic integer active token to worker-side APIs.");
        Assert(lifetime.GetMethod("TryDeactivate", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) != null,
            "App runtime lifetime must support atomic revocation before teardown.");

        var obsoleteParallelState = new[]
        {
            "_pluginRuntimes",
            "_pluginRuntimeStarts",
            "_pluginRuntimeStartFailures",
            "_pluginRuntimeStartFailureCounts",
            "_pluginRuntimeRetryTokens",
            "_pluginRuntimeRestartRequests"
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
        var stateType = controller.GetNestedType("PluginRuntimeState", BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("PluginRuntimeState was not found.");
        var transitions = controller.GetNestedType("PluginRuntimeTransitions", BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("PluginRuntimeTransitions was not found.");

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
        Assert(
            transitions.GetMethod(
                "DescriptorChanged",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic) == null,
            "Runtime transitions must not retain a descriptor-change hot-reload recovery path.");

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
            new[] { " RUNTIME ", "textzoom", "noteLinks", "runtime" });
        normalize.Invoke(null, new[] { canonicalManifest });
        var values = (string[]?)capabilities.GetValue(canonicalManifest) ?? [];
        Assert(values.SequenceEqual(new[] { "runtime", "textZoom", "noteLinks" }),
            "Capability normalization did not produce one canonical representation.");
    }

    private static void CheckSettingsLayoutManifest(Assembly host)
    {
        var registryType = RequireType(host, "PaperTodo.PaperBodyPluginRegistry");
        var manifestType = RequireType(host, "PaperTodo.PaperBodyPluginManifest");
        var settingType = RequireType(host, "PaperTodo.PaperBodyPluginSettingManifest");
        var categoryType = RequireType(host, "PaperTodo.PaperBodyPluginSettingCategoryManifest");

        Assert(
            manifestType.GetProperty("AdvancedSettings")?.PropertyType == typeof(bool),
            "Plugin manifest must expose explicit advancedSettings opt-in metadata.");
        Assert(
            manifestType.GetProperty("PrimarySettings")?.PropertyType == typeof(int?),
            "Plugin manifest must expose optional primarySettings metadata.");
        Assert(
            manifestType.GetProperty("SettingCategories")?.PropertyType == categoryType.MakeArrayType(),
            "Plugin manifest must expose settingCategories metadata.");
        Assert(
            settingType.GetProperty("Quick")?.PropertyType == typeof(bool),
            "Legacy inline settings must retain per-setting quick metadata.");
        Assert(
            settingType.GetProperty("Category")?.PropertyType == typeof(string),
            "Advanced plugin settings must expose an optional category name.");
        Assert(
            settingType.GetProperty("Action")?.PropertyType == typeof(string),
            "Plugin settings must expose host-owned paper action metadata for action buttons.");
        Assert(
            categoryType.GetProperty("Name")?.PropertyType == typeof(string) &&
            categoryType.GetProperty("Column")?.PropertyType == typeof(string),
            "Setting categories must carry their display name and optional column placement.");

        var supported = registryType.GetField(
            "SupportedPluginApiVersion",
            BindingFlags.Static | BindingFlags.NonPublic)?.GetRawConstantValue()?.ToString();
        var minimum = registryType.GetField(
            "MinimumPluginApiVersion",
            BindingFlags.Static | BindingFlags.NonPublic)?.GetRawConstantValue()?.ToString();
        Assert(supported == "2.2" && minimum == "2.1",
            "The plugin host must expose Protocol 2.2 while retaining 2.1 compatibility.");

        var validateApi = registryType.GetMethod(
            "ValidateManifestApiVersion",
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("ValidateManifestApiVersion was not found.");
        validateApi.Invoke(null, new object[] { "2.1" });
        validateApi.Invoke(null, new object[] { "2.2" });
        foreach (var rejected in new[] { "2.0", "2.3", "3.0" })
        {
            try
            {
                validateApi.Invoke(null, new object[] { rejected });
                throw new InvalidOperationException($"Unsupported Protocol {rejected} was accepted.");
            }
            catch (TargetInvocationException ex) when (ex.InnerException is InvalidDataException)
            {
            }
        }

        var validateFeatures = registryType.GetMethod(
            "ValidateProtocolFeatures",
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("ValidateProtocolFeatures was not found.");
        var permissionsProperty = manifestType.GetProperty("Permissions")
            ?? throw new InvalidOperationException("Manifest Permissions property was not found.");
        var settingsPermissionManifest = Activator.CreateInstance(manifestType, nonPublic: true)
            ?? throw new InvalidOperationException("Could not create settings-permission manifest.");
        manifestType.GetProperty("ApiVersion")!.SetValue(settingsPermissionManifest, "2.1");
        permissionsProperty.SetValue(settingsPermissionManifest, new[] { "settings.read" });
        try
        {
            validateFeatures.Invoke(null, [settingsPermissionManifest]);
            throw new InvalidOperationException("Protocol 2.1 unexpectedly accepted settings.read.");
        }
        catch (TargetInvocationException ex) when (ex.InnerException is InvalidDataException)
        {
        }
        manifestType.GetProperty("ApiVersion")!.SetValue(settingsPermissionManifest, "2.2");
        validateFeatures.Invoke(null, [settingsPermissionManifest]);

        var validateSettings = registryType.GetMethod(
            "ValidateSettings",
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("ValidateSettings was not found.");
        var settingsProperty = manifestType.GetProperty("Settings")
            ?? throw new InvalidOperationException("Manifest Settings property was not found.");

        var apiVersionProperty = manifestType.GetProperty("ApiVersion")
            ?? throw new InvalidOperationException("Manifest ApiVersion property was not found.");
        var actionManifest = Activator.CreateInstance(manifestType, nonPublic: true)
            ?? throw new InvalidOperationException("Could not create action manifest.");
        apiVersionProperty.SetValue(actionManifest, "2.2");
        var actionSetting = Activator.CreateInstance(settingType, nonPublic: true)
            ?? throw new InvalidOperationException("Could not create action setting.");
        settingType.GetProperty("Id")!.SetValue(actionSetting, "editPrompt");
        settingType.GetProperty("Type")!.SetValue(actionSetting, "action");
        settingType.GetProperty("Name")!.SetValue(actionSetting, "Edit prompt");
        settingType.GetProperty("Action")!.SetValue(actionSetting, "paper.expand");
        var actionSettings = Array.CreateInstance(settingType, 1);
        actionSettings.SetValue(actionSetting, 0);
        settingsProperty.SetValue(actionManifest, actionSettings);
        validateSettings.Invoke(null, [actionManifest]);
        Assert(
            string.Equals(
                settingType.GetProperty("Action")!.GetValue(actionSetting)?.ToString(),
                "paper.expand",
                StringComparison.Ordinal),
            "A Protocol 2.2 paper.expand action setting must validate without becoming stored settings data.");

        apiVersionProperty.SetValue(actionManifest, "2.1");
        try
        {
            validateSettings.Invoke(null, [actionManifest]);
            throw new InvalidOperationException("Protocol 2.1 unexpectedly accepted an action setting.");
        }
        catch (TargetInvocationException ex) when (ex.InnerException is InvalidDataException)
        {
        }
        apiVersionProperty.SetValue(actionManifest, "2.2");

        var startupProperty = manifestType.GetProperty("StartupPaper")
            ?? throw new InvalidOperationException("Manifest StartupPaper property was not found.");
        var startupType = startupProperty.PropertyType;
        var validateStartup = registryType.GetMethod(
            "ValidateStartupPaper",
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("ValidateStartupPaper was not found.");
        var hiddenManifest = Activator.CreateInstance(manifestType, nonPublic: true)
            ?? throw new InvalidOperationException("Could not create hidden startup manifest.");
        apiVersionProperty.SetValue(hiddenManifest, "2.2");
        var enabledSetting = Activator.CreateInstance(settingType, nonPublic: true)
            ?? throw new InvalidOperationException("Could not create startup enable setting.");
        settingType.GetProperty("Id")!.SetValue(enabledSetting, "autoStart");
        settingType.GetProperty("Type")!.SetValue(enabledSetting, "boolean");
        settingType.GetProperty("Name")!.SetValue(enabledSetting, "Enable on startup");
        var hiddenSettings = Array.CreateInstance(settingType, 1);
        hiddenSettings.SetValue(enabledSetting, 0);
        settingsProperty.SetValue(hiddenManifest, hiddenSettings);
        var startup = Activator.CreateInstance(startupType, nonPublic: true)
            ?? throw new InvalidOperationException("Could not create startup paper manifest.");
        startupType.GetProperty("EnabledSetting")!.SetValue(startup, "autoStart");
        startupType.GetProperty("InstanceKey")!.SetValue(startup, "main");
        startupType.GetProperty("Presentation")!.SetValue(startup, "hidden");
        startupProperty.SetValue(hiddenManifest, startup);
        validateStartup.Invoke(null, [hiddenManifest]);
        Assert(
            string.Equals(
                startupType.GetProperty("Presentation")!.GetValue(startup)?.ToString(),
                "hidden",
                StringComparison.Ordinal),
            "Protocol 2.2 startupPaper.presentation=hidden must be a valid Runtime-owning hidden startup mode.");

        apiVersionProperty.SetValue(hiddenManifest, "2.1");
        try
        {
            validateStartup.Invoke(null, [hiddenManifest]);
            throw new InvalidOperationException("Protocol 2.1 unexpectedly accepted hidden startup presentation.");
        }
        catch (TargetInvocationException ex) when (ex.InnerException is InvalidDataException)
        {
        }

        var controller = RequireType(host, "PaperTodo.AppController");
        Assert(
            controller.GetMethod("BuildPluginActionSetting", BindingFlags.Instance | BindingFlags.NonPublic) != null &&
            controller.GetMethod("ApplyHiddenPluginStartupPaperVisibility", BindingFlags.Instance | BindingFlags.NonPublic) != null,
            "Host UI/startup must expose the action-button and hidden-startup execution paths.");
    }

    private static void CheckProtocolBoundaries(Assembly host)
    {
        var hostApi = RequireType(host, "PaperTodo.PaperBodyPluginHostApi");
        var controller = RequireType(host, "PaperTodo.AppController");
        var registry = RequireType(host, "PaperTodo.PaperBodyPluginRegistry");
        Assert(
            hostApi.GetMethod("EnsurePresentationProtocol", BindingFlags.Instance | BindingFlags.NonPublic) == null,
            "Protocol 2.2 compatibility must not restore the removed Protocol 2.0 presentation gate.");
        Assert(
            controller.GetMethod("EnsurePluginTopBarProtocol", BindingFlags.Instance | BindingFlags.NonPublic) == null,
            "Protocol 2.2 compatibility must not restore the removed Protocol 2.0 top-bar gate.");
        Assert(
            registry.GetMethod("ApiAtLeast", BindingFlags.Static | BindingFlags.NonPublic) != null,
            "Protocol 2.2 feature gates need one shared major.minor comparison helper.");
    }

    private static void CheckSharedWebInfrastructure(Assembly host)
    {
        var infrastructure = RequireType(host, "PaperTodo.WebPluginRuntimeInfrastructure");
        var runtime = RequireType(host, "PaperTodo.WebPluginRuntime");
        Assert(
            infrastructure.GetProperty("JsonOptions", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic) != null,
            "Shared Web runtime serialization policy was not found.");
        Assert(
            runtime.GetField("JsonOptions", BindingFlags.Static | BindingFlags.NonPublic) == null,
            "WebPluginRuntime still owns a duplicate JSON bridge policy.");
        Assert(
            runtime.GetField("_startupReady", BindingFlags.Instance | BindingFlags.NonPublic) != null,
            "Web app runtime must wait for document readiness before it enters Running.");
    }

    private static void CheckWebRuntimeRequestRouting(Assembly host)
    {
        var runtime = RequireType(host, "PaperTodo.WebPluginRuntime");
        var resolveRoute = runtime.GetMethod(
            "ResolveHostRequestRoute",
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                "Web Runtime host request route policy was not found.");

        string Resolve(string payload, string method)
        {
            using var document = JsonDocument.Parse(payload);
            return resolveRoute.Invoke(null, [document.RootElement, method])?.ToString()
                ?? throw new InvalidOperationException("Web Runtime request route was not resolved.");
        }

        void AssertInvalidTarget(string payload)
        {
            try
            {
                Resolve(payload, "papers.list");
                throw new InvalidOperationException("Invalid Web Runtime target was accepted.");
            }
            catch (TargetInvocationException ex)
            {
                var code = ex.InnerException?.GetType().GetProperty("Code")
                    ?.GetValue(ex.InnerException)?.ToString();
                Assert(code == "invalid_params",
                    "Invalid Web Runtime targets must fail with invalid_params.");
            }
        }

        Assert(Resolve("{\"target\":\"workspace\"}", "papers.list") == "Workspace",
            "papertodo.workspace papers.list must reach the Workspace API.");
        Assert(Resolve("{}", "papers.list") == "RuntimePapersList",
            "Unmarked papers.list must retain provider Runtime Paper routing.");
        Assert(Resolve("{}", "papers.get") == "Workspace" &&
               Resolve("{}", "todos.list") == "Workspace",
            "Unmarked Workspace-only methods must retain the existing Workspace fallback.");
        Assert(Resolve("{\"target\":\"workspace\"}", "papers.setTitle") == "Workspace",
            "Workspace transport must not fall back to a same-named Runtime-only method.");
        AssertInvalidTarget("{\"target\":\"future-target\"}");
        AssertInvalidTarget("{\"target\":42}");
        AssertInvalidTarget("{\"target\":null}");
    }

    private static void CheckWebRuntimeBridgeBehavior(Assembly host)
    {
        var runtimeType = RequireType(host, "PaperTodo.WebPluginRuntime");
        var buildBridge = runtimeType.GetMethod(
            "BuildBridgeScript",
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                "Web Runtime bridge builder was not found.");
        var executeHostRequest = runtimeType.GetMethod(
            "ExecuteHostRequest",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                "Web Runtime host-request dispatcher was not found.");

        const string expectedOrigin = "https://protocol-policy.test";
        var bridgeScript = buildBridge.Invoke(null, [expectedOrigin]) as string
            ?? throw new InvalidOperationException(
                "Web Runtime bridge builder returned no script.");
        var harness = """
            'use strict';
            const fs = require('node:fs');
            const vm = require('node:vm');
            const posted = [];
            const hostListeners = [];
            const windowObject = {
              location: { origin: 'https://protocol-policy.test' },
              chrome: {
                webview: {
                  postMessage(value) { posted.push(value); },
                  addEventListener(type, listener) {
                    if (type === 'message') hostListeners.push(listener);
                  }
                }
              },
              addEventListener() {},
              dispatchEvent() {}
            };
            windowObject.top = windowObject;
            globalThis.window = windowObject;
            globalThis.location = windowObject.location;
            globalThis.CustomEvent = class CustomEvent {
              constructor(type, init) { this.type = type; this.detail = init?.detail; }
            };
            const bridge = fs.readFileSync(process.argv[2], 'utf8');
            vm.runInThisContext(bridge, { filename: 'WebPluginRuntime.bridge.js' });
            if (!window.papertodo || window.papertodo.surface !== 'runtime') {
              throw new Error('The generated Web Runtime bridge did not install.');
            }
            void window.papertodo.workspace.request('papers.list').catch(() => {});
            const beforeInitialize = posted.length;
            for (const listener of hostListeners) listener({ data: { type: 'initialize' } });
            const queued = posted.shift();
            void window.papertodo.workspace.request('papers.list').catch(() => {});
            const direct = posted.shift();
            process.stdout.write(JSON.stringify({ beforeInitialize, queued, direct }));
            """;

        var temporaryRoot = Path.Combine(
            Path.GetTempPath(),
            $"PaperTodo.ProtocolPolicyChecks.{Guid.NewGuid():N}");
        var harnessPath = Path.Combine(temporaryRoot, "bridge-harness.cjs");
        var bridgePath = Path.Combine(temporaryRoot, "WebPluginRuntime.bridge.js");
        Directory.CreateDirectory(temporaryRoot);
        File.WriteAllText(harnessPath, harness, new UTF8Encoding(false));
        File.WriteAllText(bridgePath, bridgeScript, new UTF8Encoding(false));
        try
        {
            var startInfo = new ProcessStartInfo("node")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            startInfo.ArgumentList.Add(harnessPath);
            startInfo.ArgumentList.Add(bridgePath);
            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException(
                    "Could not start Node.js for the Web Runtime bridge behavior check.");
            var standardOutput = process.StandardOutput.ReadToEndAsync();
            var standardError = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(15_000))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                throw new TimeoutException(
                    "The Web Runtime bridge behavior check timed out after 15 seconds.");
            }
            Assert(Task.WaitAll([standardOutput, standardError], 5_000),
                "Node.js output did not close after the bridge behavior check exited.");
            var error = standardError.GetAwaiter().GetResult();
            Assert(process.ExitCode == 0,
                $"The generated Web Runtime bridge failed in Node.js: {error}");

            using var result = JsonDocument.Parse(
                standardOutput.GetAwaiter().GetResult());
            var root = result.RootElement;
            Assert(root.GetProperty("beforeInitialize").GetInt32() == 0,
                "A Workspace request escaped before Web Runtime initialization.");
            var queued = ReadBridgePayload(root, "queued");
            var direct = ReadBridgePayload(root, "direct");
            AssertWorkspaceRequest(queued, "queued");
            AssertWorkspaceRequest(direct, "direct");

            var deniedWorkspace = CreatePermissionDeniedWorkspace(host);
            var runtime = RuntimeHelpers.GetUninitializedObject(runtimeType);
            SetInstanceField(runtimeType, runtime, "_workspace", deniedWorkspace);
            AssertPermissionDenied(executeHostRequest, runtime, queued, "queued");
            AssertPermissionDenied(executeHostRequest, runtime, direct, "direct");
        }
        finally
        {
            try { Directory.Delete(temporaryRoot, recursive: true); } catch { }
        }
    }

    private static JsonElement ReadBridgePayload(JsonElement root, string name)
    {
        var message = root.GetProperty(name);
        Assert(
            message.ValueKind == JsonValueKind.Object &&
            message.TryGetProperty("type", out var type) &&
            type.ValueKind == JsonValueKind.String &&
            string.Equals(type.GetString(), "hostRequest", StringComparison.Ordinal) &&
            message.TryGetProperty("payload", out _),
            $"The {name} Web bridge message was not a host request.");
        return message.GetProperty("payload").Clone();
    }

    private static object CreatePermissionDeniedWorkspace(Assembly host)
    {
        var hostApiType = RequireType(host, "PaperTodo.PaperBodyPluginHostApi");
        var constructor = hostApiType.GetConstructors(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Single(value => value.GetParameters().Length == 7);
        return constructor.Invoke([
            null,
            null,
            null,
            "protocol-policy-check",
            Array.Empty<string>(),
            new Func<bool>(() => true),
            new Func<bool>(() => true)
        ]);
    }

    private static void AssertWorkspaceRequest(JsonElement payload, string stage)
    {
        Assert(
            payload.ValueKind == JsonValueKind.Object &&
            payload.TryGetProperty("target", out var target) &&
            target.ValueKind == JsonValueKind.String &&
            string.Equals(target.GetString(), "workspace", StringComparison.Ordinal),
            $"The {stage} Workspace bridge payload lost target: 'workspace'.");
        Assert(
            payload.TryGetProperty("method", out var method) &&
            method.ValueKind == JsonValueKind.String &&
            string.Equals(method.GetString(), "papers.list", StringComparison.Ordinal),
            $"The {stage} Workspace bridge payload changed its method.");
    }

    private static void AssertPermissionDenied(
        MethodInfo executeHostRequest,
        object runtime,
        JsonElement payload,
        string stage)
    {
        try
        {
            executeHostRequest.Invoke(runtime, [payload]);
            throw new InvalidOperationException(
                $"The {stage} Workspace request bypassed plugin permission checks.");
        }
        catch (TargetInvocationException ex)
        {
            var code = ex.InnerException?.GetType().GetProperty("Code")
                ?.GetValue(ex.InnerException)?.ToString();
            Assert(code == "permission_denied",
                $"The {stage} Workspace request did not reach the permission boundary.");
        }
    }

    private static void SetInstanceField(
        Type type,
        object instance,
        string name,
        object value)
    {
        var field = type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Required runtime field was not found: {name}");
        field.SetValue(instance, value);
    }

    private static void CheckUnifiedPluginRuntime(Assembly host, Assembly abstractions)
    {
        var controller = RequireType(host, "PaperTodo.AppController");
        var webRuntime = RequireType(host, "PaperTodo.WebPluginRuntime");
        var manifest = RequireType(host, "PaperTodo.PaperBodyPluginManifest");
        var context = RequireType(abstractions, "PaperTodo.Plugin.PaperPluginRuntimeContext");
        var papers = RequireType(abstractions, "PaperTodo.Plugin.IPaperPluginRuntimePapers");
        var runtimeState = RequireType(abstractions, "PaperTodo.Plugin.IPaperPluginRuntimeState");
        var bodyContext = RequireType(abstractions, "PaperTodo.Plugin.PaperBodyContext");
        var runtimeClient = RequireType(abstractions, "PaperTodo.Plugin.IPaperPluginRuntimeClient");

        Assert(
            controller.GetField("_pluginRuntimeSlots", BindingFlags.Instance | BindingFlags.NonPublic) != null,
            "Provider Runtime must retain one provider-keyed lifecycle slot dictionary.");
        Assert(
            controller.GetField("_webPaperRuntimeSlots", BindingFlags.Instance | BindingFlags.NonPublic) == null,
            "Host-managed per-Paper Web Runtime slots must not return.");
        Assert(host.GetType("PaperTodo.WebPaperRuntime", throwOnError: false) == null,
            "WebPaperRuntime must not return; Web uses the one provider Runtime.");
        Assert(manifest.GetProperty("PaperRuntime") == null &&
               manifest.GetProperty("PaperRuntimePath") == null,
            "paperRuntime manifest fields must not return.");
        Assert(manifest.GetProperty("Requires") == null,
            "Body requires/backgroundUpdates must not return as a host lifecycle mode.");
        Assert(abstractions.GetType("PaperTodo.Plugin.PaperBodyRuntimeRequirements", throwOnError: false) == null,
            "PaperBodyRuntimeRequirements must stay deleted; guaranteed background work belongs to Runtime.");
        Assert(context.GetProperty("Papers")?.PropertyType == papers &&
               context.GetProperty("State")?.PropertyType == runtimeState,
            "The provider Runtime must own logical Paper routing and provider-scoped backend state.");
        Assert(bodyContext.GetProperty("Runtime")?.PropertyType == runtimeClient,
            "Body/Mini frontends must address the one provider Runtime through a thin client.");
        Assert(papers.GetMethod("List") != null &&
               papers.GetMethod("SetHeaderText") != null &&
               papers.GetMethod("SetCapsulePresentation") != null &&
               papers.GetMethod("PostToBody") != null,
            "Provider Runtime Paper routing is incomplete.");
        Assert(webRuntime.GetField("_papers", BindingFlags.Instance | BindingFlags.NonPublic) != null &&
               webRuntime.GetField("_state", BindingFlags.Instance | BindingFlags.NonPublic) != null,
            "The Web provider Runtime must use the same logical Paper/state contract as Native.");
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
        Assert(manifest.GetProperty("PaperRuntime") == null &&
               manifest.GetProperty("PaperRuntimePath") == null,
            "Retired Web per-Paper runtime manifest fields must stay deleted.");
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

    private static void CheckPluginRuntimePersistenceGuards(Assembly host)
    {
        var dataStore = RequireType(host, "PaperTodo.PaperBodyPluginDataStore");
        var paperLimit = dataStore.GetField(
            "MaximumPaperStateBytes",
            BindingFlags.Static | BindingFlags.NonPublic)?.GetRawConstantValue();
        var runtimeLimit = dataStore.GetField(
            "MaximumPluginRuntimeStateBytes",
            BindingFlags.Static | BindingFlags.NonPublic)?.GetRawConstantValue();
        Assert(
            paperLimit is int paperBytes && paperBytes == 10 * 1024 * 1024,
            "Each Paper frontend state must allow exactly 10 MiB.");
        Assert(
            runtimeLimit is int runtimeBytes && runtimeBytes == 20 * 1024 * 1024,
            "Each provider PluginRuntime state must allow exactly 20 MiB.");

        var controller = RequireType(host, "PaperTodo.AppController");
        var versionGuard = controller.GetMethod(
            "PluginRuntimeStateVersionIsSupported",
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                "PluginRuntime state downgrade guard was not found.");
        Assert((bool)(versionGuard.Invoke(null, new object[] { 2, 2 }) ?? false),
            "Equal PluginRuntime state versions must be accepted.");
        Assert((bool)(versionGuard.Invoke(null, new object[] { 1, 2 }) ?? false),
            "Older PluginRuntime state must remain readable for plugin-owned migration.");
        Assert(!(bool)(versionGuard.Invoke(null, new object[] { 3, 2 }) ?? true),
            "Newer PluginRuntime state must be rejected instead of being downgraded.");

        var webRuntime = RequireType(host, "PaperTodo.WebPluginRuntime");
        Assert(
            webRuntime.GetProperty(
                "CanAcceptPaperMessages",
                BindingFlags.Instance | BindingFlags.NonPublic) != null,
            "Web PluginRuntime must expose document readiness to prevent false-success message loss.");
    }

    private static void CheckPluginRuntimeSettings(Assembly host, Assembly abstractions)
    {
        var settings = RequireType(abstractions, "PaperTodo.Plugin.IPaperPluginRuntimeSettings");
        Assert(settings.GetProperty("Json")?.PropertyType == typeof(string) &&
               settings.GetMethod("Subscribe") != null,
            "Provider Runtime settings must expose current JSON and change subscription.");

        var context = RequireType(abstractions, "PaperTodo.Plugin.PaperPluginRuntimeContext");
        Assert(context.GetProperty("Settings")?.PropertyType == settings &&
               context.GetProperty("State") != null &&
               context.GetProperty("Papers") != null,
            "PaperPluginRuntimeContext must expose Settings, backend State and logical Papers.");

        var controller = RequireType(host, "PaperTodo.AppController");
        Assert(
            controller.GetMethod(
                "RetryFailedPluginRuntimeAfterSettingsChanged",
                BindingFlags.Instance | BindingFlags.NonPublic) != null,
            "A Failed/Backoff app runtime must have a settings-change recovery path.");
    }

    private static void CheckProtocol21Contributions(Assembly host, Assembly abstractions)
    {
        var todoSnapshot = RequireType(abstractions, "PaperTodo.Plugin.TodoSnapshot");
        Assert(
            todoSnapshot.GetConstructors().Any(ctor => ctor.GetParameters().Length == 9),
            "Protocol 2.1 must preserve the existing nine-parameter TodoSnapshot constructor.");
        Assert(
            todoSnapshot.GetProperty("LinkedPathIsDirectory")?.PropertyType == typeof(bool?),
            "Protocol 2.1 must expose LinkedPathIsDirectory without changing TodoSnapshot's positional constructor.");

        var context = RequireType(abstractions, "PaperTodo.Plugin.PaperPluginRuntimeContext");
        var todoActions = RequireType(abstractions, "PaperTodo.Plugin.IPaperPluginRuntimeTodoActions");
        var topBarLabels = RequireType(abstractions, "PaperTodo.Plugin.IPaperPluginRuntimeTopBarLabels");
        Assert(context.GetProperty("TodoActions")?.PropertyType == todoActions,
            "Protocol 2.1 Runtime context must expose host-rendered Todo actions.");
        Assert(context.GetProperty("TopBarLabels")?.PropertyType == topBarLabels,
            "Protocol 2.1 Runtime context must expose host-rendered top-bar labels.");

        var controller = RequireType(host, "PaperTodo.AppController");
        Assert(
            controller.GetMethod("SetPluginTodoActions", BindingFlags.Instance | BindingFlags.NonPublic) != null &&
            controller.GetMethod("InvokePluginTodoAction", BindingFlags.Instance | BindingFlags.NonPublic) != null,
            "Protocol 2.1 Todo action registration/dispatch is incomplete.");
        Assert(
            controller.GetMethod("SetPluginTopBarLabels", BindingFlags.Instance | BindingFlags.NonPublic) != null,
            "Protocol 2.1 top-bar label registration is incomplete.");

        var webRuntime = RequireType(host, "PaperTodo.WebPluginRuntime");
        Assert(
            webRuntime.GetMethod("SetTodoActions", BindingFlags.Instance | BindingFlags.NonPublic) != null &&
            webRuntime.GetMethod("SetTopBarLabels", BindingFlags.Instance | BindingFlags.NonPublic) != null,
            "Web Runtime must expose the same Protocol 2.1 contribution surfaces as Native.");
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
