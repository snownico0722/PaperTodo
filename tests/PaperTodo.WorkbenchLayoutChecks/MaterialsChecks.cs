using System.Text.Json;
using PaperTodo;

internal static partial class Program
{
    private static void MaterialsSourceProjection()
    {
        var document = new WorkspaceMaterialsDocument
        {
            Items = new()
            {
                ["material:link"] = new() { Id = "material:link", Kind = WorkspaceMaterialKinds.Link,
                    Title = "Research", Content = "Read later", Source = "https://example.com/article?draft=1" },
                ["material:file"] = new() { Id = "material:file", Kind = WorkspaceMaterialKinds.File,
                    Content = "Source document", Source = @"C:\Project files\draft.docx" }
            }
        };
        var projection = document.RenderMarkdown();
        Assert(projection == "## Research\n\nRead later\n\nhttps://example.com/article?draft=1\n\nSource document\n\nC:\\Project files\\draft.docx");
        document.Items["material:link"].Content = "Changed description";
        Assert(document.RenderMarkdown().Contains("Changed description\n\nhttps://example.com/article?draft=1", StringComparison.Ordinal));
    }

    private static void MaterialsRoundTrip()
    {
        var document = new WorkspaceMaterialsDocument
        {
            Items = new()
            {
                ["material:image"] = new() { Id = "material:image", Kind = WorkspaceMaterialKinds.Image,
                    Title = "Reference", Content = "![reference](i:001)" },
                ["material:node"] = new() { Id = "material:node", Kind = WorkspaceMaterialKinds.Node,
                    Content = "Short idea", Collapsed = true }
            },
            Layout = new()
            {
                Cards = new()
                {
                    ["material:image"] = new() { X = 42, Y = -12 },
                    ["material:node"] = new() { X = 500, Width = 180, Height = 96 },
                    ["paper"] = new() { X = -300 }
                }
            }
        };
        document.Normalize(["paper"]);
        var restored = JsonSerializer.Deserialize<WorkspaceMaterialsDocument>(JsonSerializer.Serialize(document))!;
        Assert(restored.Items.Count == 2 && restored.Items["material:node"].Collapsed);
        Assert(restored.Layout.Cards["material:node"].Width == 180 && restored.Layout.Cards["material:node"].Height == 96);
        Assert(restored.Layout.Cards["paper"].X == -300);
        Assert(restored.RenderMarkdown() == "## Reference\n\n![reference](i:001)\n\nShort idea");
        var copy = restored.Copy();
        copy.Items["material:node"].Content = "Edited idea";
        copy.Layout.Cards["material:node"].X = 999;
        Assert(restored.Items["material:node"].Content == "Short idea" && restored.Layout.Cards["material:node"].X == 500);
    }

    private static void MaterialsGroups()
    {
        var layout = new WorkbenchState
        {
            Groups = new()
            {
                ["one"] = new() { Id = "stale", Members = ["paper", "material:a", "missing", "paper"], Collapsed = true },
                ["two"] = new() { Members = ["material:a", "material:b"] },
                ["empty"] = new() { Members = ["missing"] }
            }
        };
        layout.Normalize(["paper", "material:a", "material:b"]);
        Assert(layout.Groups.Count == 2 && layout.Groups["one"].Id == "one" && layout.Groups["one"].Collapsed);
        Assert(layout.Groups["one"].Members.SequenceEqual(["paper", "material:a"]));
        Assert(layout.Groups["two"].Members.SequenceEqual(["material:b"]));
        var copy = layout.Copy(); copy.Groups["one"].Members.Clear();
        Assert(layout.Groups["one"].Members.Count == 2);
    }

    private static void MaterialsBranches()
    {
        var layout = new WorkbenchState
        {
            Connections =
            [
                new() { Id = "ab", From = "a", To = "b", IsBranch = true },
                new() { Id = "bc", From = "b", To = "c", IsBranch = true },
                new() { Id = "ca", From = "c", To = "a", IsBranch = true },
                new() { Id = "db", From = "d", To = "b", IsBranch = true },
                new() { Id = "duplicate", From = "a", To = "b", IsBranch = true },
                new() { Id = "dangling", From = "missing", To = "b" },
                new() { Id = "self", From = "a", To = "a" }
            ]
        };
        layout.Normalize(["a", "b", "c", "d"]);
        Assert(layout.Connections.Count == 4);
        Assert(layout.Connections.Single(c => c.Id == "ab").IsBranch && layout.Connections.Single(c => c.Id == "bc").IsBranch);
        Assert(!layout.Connections.Single(c => c.Id == "ca").IsBranch && !layout.Connections.Single(c => c.Id == "db").IsBranch);
        var before = JsonSerializer.Serialize(layout);
        layout.Normalize(["a", "b", "c", "d"]);
        Assert(JsonSerializer.Serialize(layout) == before, "Normalization changed an already normalized tree");
        var copy = layout.Copy(); copy.Connections[0].Label = "changed";
        Assert(layout.Connections[0].Label == "");
    }

    private static void MaterialsIdentityRepair()
    {
        var document = new WorkspaceMaterialsDocument
        {
            Items = new()
            {
                ["a"] = new() { Content = "unprefixed" },
                ["material:a"] = new() { Content = "canonical" }
            },
            Layout = new()
            {
                Cards = new() { ["a"] = new() { X = 10 }, ["material:a"] = new() { X = 20 } },
                Groups = new() { ["group"] = new() { Members = ["a", "material:a"] } },
                Connections = [new() { From = "a", To = "material:a" }]
            }
        };
        document.Normalize([]);
        Assert(document.Items["material:a"].Content == "canonical" && document.Items["material:a:2"].Content == "unprefixed");
        Assert(document.Layout.Cards["material:a"].X == 20 && document.Layout.Cards["material:a:2"].X == 10);
        Assert(document.Layout.Groups["group"].Members.SequenceEqual(["material:a:2", "material:a"]));
        Assert(document.Layout.Connections[0].From == "material:a:2" && document.Layout.Connections[0].To == "material:a");
    }
}
