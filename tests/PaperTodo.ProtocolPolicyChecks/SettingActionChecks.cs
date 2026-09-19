using System.Collections;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using PaperTodo.Plugin;

internal static partial class Program
{
    private static void CheckSettingActionBehavior(Assembly host)
    {
        var count = 0;
        void Check(bool value, string message)
        {
            Assert(value, message);
            count++;
        }
        var registry = RequireType(host, "PaperTodo.PaperBodyPluginRegistry");
        var manifestType = RequireType(host, "PaperTodo.PaperBodyPluginManifest");
        var settingType = RequireType(host, "PaperTodo.PaperBodyPluginSettingManifest");
        var validate = registry.GetMethod("ValidateSettings", BindingFlags.Static | BindingFlags.NonPublic)!;
        object NewSetting(string action)
        {
            var setting = Activator.CreateInstance(settingType, nonPublic: true)!;
            settingType.GetProperty("Id")!.SetValue(setting, "testButton");
            settingType.GetProperty("Type")!.SetValue(setting, "action");
            settingType.GetProperty("Action")!.SetValue(setting, action);
            return setting;
        }
        object NewManifest(object setting, bool runtime)
        {
            var manifest = Activator.CreateInstance(manifestType, nonPublic: true)!;
            manifestType.GetProperty("ApiVersion")!.SetValue(manifest, "2.2");
            var settings = Array.CreateInstance(settingType, 1);
            settings.SetValue(setting, 0);
            manifestType.GetProperty("Settings")!.SetValue(manifest, settings);
            manifestType.GetProperty("Capabilities")!.SetValue(manifest,
                runtime ? new[] { "runtime" } : Array.Empty<string>());
            return manifest;
        }
        bool Valid(object setting, bool runtime)
        {
            try
            {
                validate.Invoke(null, [NewManifest(setting, runtime)]);
                return true;
            }
            catch (TargetInvocationException ex) when (ex.InnerException is InvalidDataException)
            {
                return false;
            }
        }
        foreach (var action in new[] { "paper.show", "paper.hide", "paper.toggle", "paper.expand", "paper.collapse", "paper.activate" })
            Check(Valid(NewSetting(action), runtime: false), "Built-in paper actions must not require Runtime.");
        foreach (var action in new[] { "weather.refresh", "account.login", "sync.run-now", new string('a', 80) })
        {
            Check(Valid(NewSetting(action), runtime: true), "Custom actions must be accepted with Runtime.");
            Check(!Valid(NewSetting(action), runtime: false), "Custom actions must require Runtime.");
        }
        foreach (var action in new[] { "", " ", "paper.unknown", "PAPER.unknown", "bad action", "刷新", new string('a', 81) })
            Check(!Valid(NewSetting(action), runtime: true), "Invalid/reserved custom action must be rejected.");
        foreach (var value in new[] { "null", "false", "0", "\"value\"" })
        {
            var setting = NewSetting("weather.refresh");
            using var document = JsonDocument.Parse(value);
            settingType.GetProperty("Default")!.SetValue(setting, document.RootElement.Clone());
            Check(!Valid(setting, runtime: true), "Action must not declare a stored default.");
        }

        // Real DataStore serialization: ordinary values survive, command metadata never becomes data.
        var custom = NewSetting("weather.refresh");
        var manifestForStore = NewManifest(custom, runtime: true);
        var toggle = Activator.CreateInstance(settingType, nonPublic: true)!;
        settingType.GetProperty("Id")!.SetValue(toggle, "enabled");
        settingType.GetProperty("Type")!.SetValue(toggle, "boolean");
        settingType.GetProperty("Default")!.SetValue(toggle, JsonSerializer.SerializeToElement(true));
        var mixed = Array.CreateInstance(settingType, 2);
        mixed.SetValue(custom, 0);
        mixed.SetValue(toggle, 1);
        manifestType.GetProperty("Settings")!.SetValue(manifestForStore, mixed);
        validate.Invoke(null, [manifestForStore]);
        var descriptorType = RequireType(host, "PaperTodo.PaperBodyPluginDescriptor");
        var descriptor = RuntimeHelpers.GetUninitializedObject(descriptorType);
        descriptorType.GetProperty("Id")!.SetValue(descriptor, "sample.action-check");
        descriptorType.GetProperty("Manifest")!.SetValue(descriptor, manifestForStore);
        var root = Path.Combine(Path.GetTempPath(), $"PaperTodo.ActionChecks.{Guid.NewGuid():N}");
        var storeType = RequireType(host, "PaperTodo.PaperBodyPluginDataStore");
        var constructor = storeType.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Single(c => c.GetParameters().Length == 1 && c.GetParameters()[0].ParameterType == typeof(string));
        var arguments = constructor.GetParameters().Select(p => p.HasDefaultValue ? p.DefaultValue : null).ToArray();
        arguments[0] = root;
        var store = constructor.Invoke(arguments);
        try
        {
            using var data = JsonDocument.Parse((string)storeType.GetMethod("GetSettingsJson")!.Invoke(store, [descriptor])!);
            Check(data.RootElement.GetProperty("enabled").GetBoolean(), "Real setting defaults must remain available.");
            Check(!data.RootElement.TryGetProperty("testButton", out _), "Action IDs must not appear in settings JSON.");
            foreach (var method in new[] { "GetSettingValue", "SetSettingValue" })
            {
                try
                {
                    object?[] args = method == "GetSettingValue"
                        ? [descriptor, custom]
                        : [descriptor, custom, JsonSerializer.SerializeToElement("not data")];
                    storeType.GetMethod(method)!.Invoke(store, args);
                    Check(false, "A command must not be read/written as a stored value.");
                }
                catch (TargetInvocationException ex) when (ex.InnerException is InvalidOperationException)
                {
                    count++;
                }
            }
        }
        finally
        {
            ((IDisposable)store).Dispose();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }

        // Exercise the actual shared dispatcher with isolated Runtime leases, not source-shape checks.
        var controllerType = RequireType(host, "PaperTodo.AppController");
        var controller = RuntimeHelpers.GetUninitializedObject(controllerType);
        var leasesField = controllerType.GetField("_pluginShortcutRuntimes", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var leases = (IDictionary)Activator.CreateInstance(leasesField.FieldType)!;
        leasesField.SetValue(controller, leases);
        // No global hotkeys are needed to deliver a settings action.
        SetInstanceField(controllerType, controller, "_pluginShortcutRecordingCommandId", "test-recording");
        var registrationType = controllerType.GetNestedType("PluginShortcutRegistration", BindingFlags.NonPublic)!;
        var runtimeType = controllerType.GetNestedType("PluginGlobalShortcutRuntime", BindingFlags.NonPublic)!;
        object Registration(string provider, string settingId, string actionId) =>
            Activator.CreateInstance(registrationType, ["unused", provider, settingId, actionId])!;
        object Lease(Guid id, string provider, Func<bool> active, Action<PaperShortcutActionInvocation> handler) =>
            Activator.CreateInstance(runtimeType, [id, provider, active, handler])!;
        var execute = controllerType.GetMethod("ExecutePluginShortcut", BindingFlags.Instance | BindingFlags.NonPublic)!;
        bool Run(string provider, string settingId, string actionId) =>
            (bool)execute.Invoke(controller, [Registration(provider, settingId, actionId)])!;
        var calls = new List<PaperShortcutActionInvocation>();
        var active = true;
        var oldId = Guid.NewGuid();
        leases.Add("a", Lease(oldId, "a", () => active, calls.Add));
        Check(Run("a", "button", "weather.refresh"), "Button action must reach a live handler without a hotkey.");
        Check(Run("a", "shortcut", "weather.refresh"), "Shortcut must reach the same handler.");
        Check(calls.Count == 2 && calls[0].SettingId == "button" && calls[1].SettingId == "shortcut" &&
            calls.All(c => c.ActionId == "weather.refresh"), "Action and source setting IDs must be preserved.");
        Check(!Run("b", "button", "weather.refresh") && calls.Count == 2,
            "Actions must never cross provider boundaries or be accepted without a handler.");
        active = false;
        Check(!Run("a", "button", "weather.refresh") && calls.Count == 2 && !leases.Contains("a"),
            "Inactive Runtime must be revoked and receive no late action.");
        var newId = Guid.NewGuid();
        leases.Add("a", Lease(newId, "a", () => true, calls.Add));
        controllerType.GetMethod("RemovePluginGlobalShortcutRuntime", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(controller, [oldId, "a"]);
        Check(leases.Contains("a") && Run("a", "afterRestart", "weather.refresh"),
            "Disposing an old Runtime must not remove the new Runtime handler.");
        leases["a"] = Lease(newId, "a", () => true, _ => throw new InvalidOperationException("expected test failure"));
        Check(!Run("a", "button", "weather.refresh"), "A plugin exception must not escape into settings UI.");
        var lifecycle = controllerType.GetField("_lifecycleState", BindingFlags.Instance | BindingFlags.NonPublic)!;
        lifecycle.SetValue(controller, Enum.Parse(lifecycle.FieldType, "Exiting"));
        leases["a"] = Lease(newId, "a", () => true, calls.Add);
        var beforeExit = calls.Count;
        Check(!Run("a", "button", "weather.refresh") && calls.Count == beforeExit,
            "No action may be delivered after host shutdown begins.");
        Console.WriteLine($"Settings actions: {count} behavior checks passed.");
    }
}
