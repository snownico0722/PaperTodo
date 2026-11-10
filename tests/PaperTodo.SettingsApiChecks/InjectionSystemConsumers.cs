using System.Reflection;
using System.Text.Json;
using PaperTodo;

internal static partial class InjectionSystemRunner
{
    private static void HoverProfiles(AppController c)
    {
        // The JSON-to-slot checks alone cannot detect an incorrectly wired or stale cached profile.
        var profiles = new[] { "VeryLow", "Low", "Medium", "High", "VeryHigh" }
            .ToDictionary(name => char.ToLowerInvariant(name[0]) + name[1..], name =>
                typeof(EdgeCapsuleHoverIntentPredictor).GetField(name + "Profile", BindingFlags.Static | BindingFlags.NonPublic)!);
        var parameters = UserInjection.Read().Parameters.Where(p => p.Id.StartsWith("edge.hover.profile.", StringComparison.Ordinal)).ToArray();
        Check(parameters.Length == 90, "Review the actual consumer matrix after changing the profile catalog.");
        object ReadProfile(string name) => profiles[name].GetValue(null)!;
        Dictionary<string, string> Snapshot() => profiles.Keys.ToDictionary(name => name, name =>
        {
            var value = ReadProfile(name);
            return JsonSerializer.Serialize(value, value.GetType());
        });
        double Read(string id)
        {
            var parts = id.Split('.');
            var owner = ReadProfile(parts[3]);
            if (parts.Length == 6)
                owner = Property<object>(owner, parts[4] switch { "initial" => "Initial", "transfer" => "Transfer", "corridor" => "CorridorExit", _ => throw new Exception(id) });
            var member = parts[^1].Replace("DipPerMs", "DipPerMillisecond").Replace("Ms", "Milliseconds");
            return Property<double>(owner, char.ToUpperInvariant(member[0]) + member[1..]);
        }
        foreach (var parameter in parameters)
        {
            var before = Snapshot();
            var original = Read(parameter.Id);
            Check(parameter.DefaultValue!.Value.GetDouble() == original, parameter.Id + " default is wired to the wrong consumer.");
            var value = original == 0 ? 0.01 : original * 0.95;
            Set(c, (parameter.Id, value));
            Check(Math.Abs(Read(parameter.Id) - value) < 1e-9, parameter.Id + " cached consumer was not refreshed.");
            var after = Snapshot();
            var profile = parameter.Id.Split('.')[3];
            Check(before.Where(p => p.Key != profile).All(p => after[p.Key] == p.Value), parameter.Id + " leaked into another sensitivity profile.");
            Remove(c, parameter.Id);
            Check(Snapshot().All(p => before[p.Key] == p.Value), parameter.Id + " did not restore the original cached profile.");
        }
        Console.WriteLine("PASS actual cached hover profiles: all 90 fields, five profiles, hot apply, isolation and removal");
    }
}
