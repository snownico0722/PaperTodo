using System.Text;

namespace PaperTodo;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        Console.InputEncoding = Encoding.UTF8;
        Console.OutputEncoding = Encoding.UTF8;
        var interactive = args.Length == 0;
        try
        {
            if ((args.Length == 1 && args[0] == "self-test") ||
                (args.Length == 3 && args[0] == "self-test" && args[1] == "--fixture"))
            {
                SelfTests.Run(args.Length == 3 ? args[2] : null);
                return 0;
            }

            if (interactive)
            {
                Console.WriteLine("PaperTodo 手机传输工具 v0.1.0");
                Console.WriteLine("请先退出电脑端 PaperTodo。原始数据始终保持不变。");
                Console.WriteLine("1. 电脑 → 手机：打包 data.json 和笔记图片");
                Console.WriteLine("2. 手机 → 电脑：恢复传输包到一个新文件夹");
                Console.Write("选择 1 或 2：");
                var choice = Console.ReadLine()?.Trim();
                args = choice switch
                {
                    "1" => new[] { "export", ReadPath("电脑端数据文件夹"), ReadPath("要保存的 ZIP 文件路径") },
                    "2" => new[] { "restore", ReadPath("手机导出的 ZIP 文件路径"), ReadPath("新建恢复文件夹路径（不能已存在）") },
                    _ => throw new ArgumentException("请选择 1 或 2。")
                };
            }

            if (args.Length != 3 || args[0] is not ("export" or "restore"))
            {
                Console.WriteLine("用法：PaperTodoTransfer export <电脑数据文件夹> <输出.zip>");
                Console.WriteLine("      PaperTodoTransfer restore <输入.zip> <新建恢复文件夹>");
                Console.WriteLine("      PaperTodoTransfer self-test");
                return 2;
            }

            var result = args[0] == "export"
                ? Transfer.Export(args[1], args[2])
                : Transfer.Restore(args[1], args[2]);
            Console.WriteLine($"完成：{result.PaperCount} 张纸片，{result.ImageCount} 张图片。");
            Console.WriteLine($"保存至：{result.OutputPath}");
            if (result.MissingImageIds.Count > 0)
                Console.WriteLine($"提示：原笔记引用的图片缺失，已保留原文：{string.Join(", ", result.MissingImageIds)}");
            if (args[0] == "restore")
                Console.WriteLine("请先备份电脑端旧数据，再在 PaperTodo 完全退出后，将恢复文件夹内的数据文件复制到电脑端目录。");
            if (interactive)
            {
                Console.WriteLine("按回车关闭。");
                Console.ReadLine();
            }
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"操作未完成：{exception.Message}");
            if (interactive)
            {
                Console.Error.WriteLine("按回车关闭。");
                Console.ReadLine();
            }
            return 1;
        }
    }

    private static string ReadPath(string description)
    {
        Console.Write($"{description}：");
        return (Console.ReadLine() ?? "").Trim().Trim('"');
    }
}
