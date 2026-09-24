using System.Collections;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;

internal static partial class Program
{
    private static void CheckHiddenStartupVisibility(Assembly host)
    {
        const string provider = "sample.hidden-check";
        var controllerType = RequireType(host, "PaperTodo.AppController");
        var registryType = RequireType(host, "PaperTodo.PaperBodyPluginRegistry");
        var descriptorType = RequireType(host, "PaperTodo.PaperBodyPluginDescriptor");
        var manifestType = RequireType(host, "PaperTodo.PaperBodyPluginManifest");
        var settingType = RequireType(host, "PaperTodo.PaperBodyPluginSettingManifest");
        var startupType = RequireType(host, "PaperTodo.PaperBodyPluginStartupManifest");
        var stateType = RequireType(host, "PaperTodo.AppState");
        var paperType = RequireType(host, "PaperTodo.PaperData");
        var storeType = RequireType(host, "PaperTodo.PaperBodyPluginDataStore");
        static void Set(object obj, string key, object value) => obj.GetType().GetProperty(key)!.SetValue(obj, value);
        static object New(Type type) => Activator.CreateInstance(type, nonPublic: true)!;
        var manifest = New(manifestType);
        var startup = New(startupType);
        Set(startup, "EnabledSetting", "autoStart");
        Set(startup, "Presentation", "hidden");
        Set(startup, "InstanceKey", "main");
        Set(manifest, "StartupPaper", startup);
        var enabled = New(settingType);
        Set(enabled, "Id", "autoStart");
        Set(enabled, "Type", "boolean");
        Set(enabled, "Default", JsonSerializer.SerializeToElement(true));
        var settings = Array.CreateInstance(settingType, 1);
        settings.SetValue(enabled, 0);
        Set(manifest, "Settings", settings);
        var descriptor = RuntimeHelpers.GetUninitializedObject(descriptorType);
        Set(descriptor, "Id", provider);
        Set(descriptor, "DisplayName", "Hidden startup test");
        Set(descriptor, "Manifest", manifest);
        var registry = RuntimeHelpers.GetUninitializedObject(registryType);
        var descriptorsField = registryType.GetField("_descriptors", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var descriptors = (IDictionary)Activator.CreateInstance(descriptorsField.FieldType)!;
        descriptors.Add(provider, descriptor);
        descriptorsField.SetValue(registry, descriptors);
        var root = Path.Combine(Path.GetTempPath(), $"PaperTodo.HiddenChecks.{Guid.NewGuid():N}");
        var constructor = storeType.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Single(c => c.GetParameters().Length == 1 && c.GetParameters()[0].ParameterType == typeof(string));
        var store = constructor.Invoke([root]);
        SetInstanceField(registryType, registry, "_dataStore", store);
        var controller = RuntimeHelpers.GetUninitializedObject(controllerType);
        SetInstanceField(controllerType, controller, "_paperBodyPlugins", registry);
        SetInstanceField(controllerType, controller, "_suppressDirty", true);
        var state = New(stateType);
        Set(controller, "State", state);
        var papers = (IList)stateType.GetProperty("Papers")!.GetValue(state)!;
        var paper = New(paperType);
        Set(paper, "Type", "note");
        Set(paper, "BodyProviderId", provider);
        Set(paper, "StartupOwnerPluginId", provider);
        Set(paper, "StartupInstanceKey", "main");
        papers.Add(paper);
        var method = controllerType.GetMethod("ApplyHiddenPluginStartupPaperVisibility", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var commandType = method.GetParameters()[0].ParameterType;
        bool Visible() => (bool)paperType.GetProperty("IsVisible")!.GetValue(paper)!;
        void Apply(string command) => method.Invoke(controller, [Enum.Parse(commandType, command)]);
        try
        {
            Set(paper, "IsVisible", true);
            Apply("None");
            Assert(!Visible(), "Normal startup must hide its hidden startup owner before restoring surfaces.");
            foreach (var command in new[] { "Show", "Toggle" })
            {
                Set(paper, "IsVisible", true);
                Apply(command);
                Assert(Visible(), "Explicit startup visibility commands must not be pre-empted by hidden defaults.");
            }
            Set(paper, "IsVisible", true);
            Set(paper, "BodyProviderId", "different.provider");
            Apply("None");
            Assert(Visible(), "A user-repurposed paper must not be hidden by its previous plugin owner.");
            Set(paper, "BodyProviderId", provider);
            Set(enabled, "Default", JsonSerializer.SerializeToElement(false));
            Apply("None");
            Assert(Visible(), "Disabled auto-start must not impose hidden startup policy.");
            Console.WriteLine("Hidden startup: 5 behavior checks passed.");
        }
        finally
        {
            ((IDisposable)store).Dispose();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
