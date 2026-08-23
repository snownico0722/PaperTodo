using PaperTodo.Plugin;

namespace PaperTodo.Plugin.ReviewArchive;

public sealed class ReviewArchivePlugin : IPaperBodyPlugin
{
    public string Id => "sample.review-archive.native";
    public string DisplayName => "待办复盘记录池";
    public string Description => "实时记录待办生命周期、提醒变化和完成趋势，长期保存并导出 Excel 可直接打开的 CSV。";
    public Version Version => new(1, 2, 0);
    public string ApiVersion => "2.0";
    public int StateVersion => 1;
    public PaperBodyCapabilities Capabilities => PaperBodyCapabilities.TextZoom;
    public PaperBodyRuntimeRequirements RuntimeRequirements =>
        PaperBodyRuntimeRequirements.BackgroundUpdates;

    public IPaperBodySession Create(PaperBodyContext context) =>
        new ReviewArchiveSession(context);
}
