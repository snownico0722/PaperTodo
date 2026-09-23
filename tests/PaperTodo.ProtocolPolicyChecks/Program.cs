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
            CheckShortcutReservations(host);
            CheckShortcutValidation(host);
            CheckRuntimeTransitions(host);
            CheckCapabilityNormalization(host);
            CheckSettingsLayoutManifest(host);
            CheckSettingActionBehavior(host);
            CheckHiddenStartupVisibility(host);
            CheckWebRuntimeRequestRouting(host);
            CheckWebRuntimeBridgeBehavior(host);
            CheckRuntimeApiCompatibility(host, abstractions);
            CheckWebBodyNavigationIdentity(host);
            CheckWebMiniSurfaceRecoveryBridge(host);
            CheckTopBarApiCompatibility(host, abstractions);
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

    private static void CheckShortcutReservations(Assembly host)
    {
        var managerType = RequireType(host, "PaperTodo.GlobalHotkeyManager");
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

    private static void CheckRuntimeApiCompatibility(Assembly host, Assembly abstractions)
    {
        var context = RequireType(abstractions, "PaperTodo.Plugin.PaperPluginRuntimeContext");
        var papers = RequireType(abstractions, "PaperTodo.Plugin.IPaperPluginRuntimePapers");
        var runtimeState = RequireType(abstractions, "PaperTodo.Plugin.IPaperPluginRuntimeState");
        var bodyContext = RequireType(abstractions, "PaperTodo.Plugin.PaperBodyContext");
        var runtimeClient = RequireType(abstractions, "PaperTodo.Plugin.IPaperPluginRuntimeClient");

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
    }

    private static void CheckWebBodyNavigationIdentity(Assembly host)
    {
        var body = RequireType(host, "PaperTodo.WebPaperBodySession");
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

    }


    private static void CheckWebMiniSurfaceRecoveryBridge(Assembly host)
    {
        var body = RequireType(host, "PaperTodo.WebPaperBodySession");
        var miniHost = body.GetNestedType(
            "WebPluginMiniViewHost",
            BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                "Web mini host type was not found.");
        var buildBridge = miniHost.GetMethod(
            "BuildMiniBridgeScript",
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                "Web mini bridge builder was not found.");

        const string expectedOrigin = "https://mini-policy.test";
        var bridgeScript = buildBridge.Invoke(null, [expectedOrigin, true]) as string
            ?? throw new InvalidOperationException(
                "Web mini bridge builder returned no script.");

        var harness = """
            'use strict';
            const fs = require('node:fs');
            const vm = require('node:vm');
            const posted = [];
            const hostListeners = [];
            const interactive = {
              getBoundingClientRect() {
                return { left: 10, top: 20, right: 50, bottom: 60 };
              }
            };
            const documentObject = {
              readyState: 'complete',
              documentElement: {},
              querySelectorAll(selector) {
                return selector === '[data-papertodo-interactive]' ? [interactive] : [];
              },
              addEventListener() {}
            };
            const windowObject = {
              location: { origin: 'https://mini-policy.test' },
              innerWidth: 100,
              innerHeight: 100,
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
            globalThis.document = documentObject;
            globalThis.MutationObserver = class MutationObserver { observe() {} };
            globalThis.getComputedStyle = () => ({
              display: 'block',
              visibility: 'visible',
              pointerEvents: 'auto'
            });
            globalThis.requestAnimationFrame = callback => { callback(); return 1; };
            globalThis.CustomEvent = class CustomEvent {
              constructor(type, init) { this.type = type; this.detail = init?.detail; }
            };

            const bridge = fs.readFileSync(process.argv[2], 'utf8');
            vm.runInThisContext(bridge, { filename: 'WebPaperBodySession.Mini.bridge.js' });

            const initialRegions = posted.filter(value => value?.type === 'miniInteractiveRegions');
            if (initialRegions.length !== 1) {
              throw new Error('Initial interactive regions were not published exactly once.');
            }

            for (const listener of hostListeners) {
              listener({ data: { type: 'miniSurfacePresentProbe', token: 'probe-token' } });
            }

            const regionIndexes = posted
              .map((value, index) => value?.type === 'miniInteractiveRegions' ? index : -1)
              .filter(index => index >= 0);
            const probeIndex = posted.findIndex(value =>
              value?.type === 'miniSurfacePresentProbeResult' &&
              value?.payload?.token === 'probe-token');
            process.stdout.write(JSON.stringify({
              regionCount: regionIndexes.length,
              finalRegionIndex: regionIndexes.at(-1),
              probeIndex
            }));
            """;

        var temporaryRoot = Path.Combine(
            Path.GetTempPath(),
            $"PaperTodo.WebMiniRecoveryChecks.{Guid.NewGuid():N}");
        var harnessPath = Path.Combine(temporaryRoot, "mini-recovery-harness.cjs");
        var bridgePath = Path.Combine(temporaryRoot, "WebPaperBodySession.Mini.bridge.js");
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
                    "Could not start Node.js for the Web mini recovery check.");
            var standardOutput = process.StandardOutput.ReadToEndAsync();
            var standardError = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(15_000))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                throw new TimeoutException(
                    "The Web mini recovery check timed out after 15 seconds.");
            }
            Assert(Task.WaitAll([standardOutput, standardError], 5_000),
                "Node.js output did not close after the Web mini recovery check exited.");
            var error = standardError.GetAwaiter().GetResult();
            Assert(process.ExitCode == 0,
                $"The generated Web mini bridge failed in Node.js: {error}");

            using var result = JsonDocument.Parse(
                standardOutput.GetAwaiter().GetResult());
            var root = result.RootElement;
            Assert(root.GetProperty("regionCount").GetInt32() == 2,
                "Surface recovery must republish unchanged interactive regions.");
            Assert(
                root.GetProperty("finalRegionIndex").GetInt32() <
                root.GetProperty("probeIndex").GetInt32(),
                "Interactive regions must reach the host before surface recovery completes.");
        }
        finally
        {
            try { Directory.Delete(temporaryRoot, recursive: true); } catch { }
        }
    }

    private static void CheckTopBarApiCompatibility(Assembly host, Assembly abstractions)
    {
        var action = RequireType(abstractions, "PaperTodo.Plugin.PaperTopBarAction");
        Assert(action.GetProperty("Priority")?.PropertyType == typeof(int),
            "PaperTopBarAction.Priority was not found.");

    }

    private static void CheckPluginRuntimePersistenceGuards(Assembly host)
    {
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
