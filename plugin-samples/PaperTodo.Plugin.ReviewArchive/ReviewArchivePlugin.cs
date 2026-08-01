using PaperTodo.Plugin;

namespace PaperTodo.Plugin.ReviewArchive;

public sealed class ReviewArchivePlugin : IPaperBodyPlugin
{
    public string Id => "sample.review-archive.native";
    public string DisplayName => "待办复盘记录池";
    public string Description => "监听待办创建、完成和删除，长期保存时间戳并导出 Excel 可直接打开的 CSV。";
    public Version Version => new(1, 0, 0);
    public string ApiVersion => "1.3";
    public int StateVersion => 1;
    public PaperBodyCapabilities Capabilities => PaperBodyCapabilities.TextZoom;
    public PaperBodyRuntimeRequirements RuntimeRequirements =>
        PaperBodyRuntimeRequirements.BackgroundUpdates;

    public IPaperBodySession Create(PaperBodyContext context) =>
        new ReviewArchiveSession(context);
}
