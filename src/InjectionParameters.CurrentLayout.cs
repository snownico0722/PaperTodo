namespace PaperTodo;

internal static partial class InjectionParameters
{
    internal static readonly InjectionParameter<double> NoteLineSpacingFactor =
        InjectionParameter<double>.Dynamic("note.editor.lineSpacingFactor", "笔记行距倍率",
            "覆盖小、中、大档计算出的笔记行距倍率，编辑和阅读使用同一排版。",
            defaultDescription: "由当前笔记行距档位计算", recommendedMinimum: 0.75, recommendedMaximum: 2);
    internal static readonly InjectionParameter<double> TodoItemMargin =
        InjectionParameter<double>.Dynamic("todo.layout.itemVerticalMarginDip", "待办条目上下边距",
            "覆盖待办条目上下各自的外边距，保留同一条待办内部的换行排版。",
            unit: "DIP", defaultDescription: "由当前待办间距档位计算", recommendedMinimum: 0, recommendedMaximum: 12);
    internal static readonly InjectionParameter<double> EdgeIconOnlyMinimumWidth =
        new("capsule.edge.iconOnlyMinWidthDip", 0, "纯图标贴边胶囊最小宽度",
            "标题设为零字时，内置贴边胶囊的最小宽度，包含内侧阴影留白。",
            unit: "DIP", application: "restart", recommendedMinimum: 0);
    internal static readonly InjectionParameter<double> BottomBarHorizontalInset =
        new("paper.bottomBar.horizontalInsetDip", 12, "纸片底栏两侧留白",
            "待办和笔记的插件底栏到左右边缘各自的距离。",
            unit: "DIP", application: "restart", recommendedMinimum: 0);
    internal static readonly InjectionParameter<double> BottomBarBottomInset =
        new("paper.bottomBar.bottomInsetDip", 4, "纸片底栏底部留白",
            "待办和笔记的插件底栏到纸片底边的距离。",
            unit: "DIP", application: "restart", recommendedMinimum: 0);
    internal static readonly InjectionParameter<double> BottomBarItemGap =
        new("paper.bottomBar.itemGapDip", 2, "纸片底栏项间距",
            "底栏同一组内相邻两项之间的留白。",
            unit: "DIP", application: "restart", recommendedMinimum: 0);
    internal static readonly InjectionParameter<double> BottomBarGroupGap =
        new("paper.bottomBar.groupGapDip", 8, "纸片底栏组间距",
            "底栏维持左右两组时，两组之间至少保留的距离。",
            unit: "DIP", application: "restart", recommendedMinimum: 0);
    internal static readonly InjectionParameter<double> BottomBarFontSize =
        new("paper.bottomBar.fontDip", 10.5, "纸片底栏基础字号",
            "底栏文字和图标的基础尺寸，仍受整体字号比例影响。",
            unit: "DIP", application: "restart", recommendedMinimum: 6);
}
