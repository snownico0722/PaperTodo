using PaperTodo;

internal static partial class Program
{
    static Program()
    {
        EdgeOpenOriginPolicyChecks();

        const string title = "📌本周计划表";
        Check(title.Length > 6,
            "Unicode title fixture must exceed six UTF-16 code units");
        Check(!PaperTitles.ExceedsTitleLengthLimit(title, 6),
            "Six Unicode title elements remain valid for external writes");
        Check(PaperTitles.CleanCustomTitle(title, 6) == title,
            "External title validation stays aligned with title cleanup");
        Check(PaperTitles.ExceedsTitleLengthLimit(title + "新", 6),
            "A seventh Unicode title element exceeds the configured limit");
        Check(!PaperTitles.ExceedsTitleLengthLimit("123456 ", 6),
            "Trailing whitespace removed by title cleanup does not cause a false overflow");

        Check(PaperTitles.DefaultMaxTitleLength == 6,
            "New installations allow six full-width characters by default");
        Check(!PaperTitles.ExceedsTitleLengthLimit("abcdefghijkl", 6) &&
              PaperTitles.ExceedsTitleLengthLimit("abcdefghijklm", 6),
            "Twelve half-width Latin letters fit the same limit as six CJK characters");
        Check(PaperTitles.CleanCustomTitle("中文ABC123", 4) == "中文ABC1",
            "Mixed titles truncate at the weighted boundary");
        Check(PaperTitles.CleanCustomTitle("ＡA", 1) == "Ａ" &&
              PaperTitles.CleanCustomTitle("。A", 1) == "。",
            "Full-width Latin and punctuation occupy one whole character");
        Check(PaperTitles.CleanCustomTitle("👨‍👩‍👧‍👦x", 1) == "👨‍👩‍👧‍👦",
            "Emoji clusters count as one full-width character and remain intact");
        Check(PaperTitles.CleanCustomTitle("e\u0301e\u0301x", 1) == "e\u0301e\u0301",
            "Combining-character clusters count as two half-width letters");
        Check(!PaperTitles.ExceedsTitleLengthLimit(new string('x', 40), 20) &&
              PaperTitles.ExceedsTitleLengthLimit(new string('x', 41), 20) &&
              PaperTitles.MaxTitleInputCodeUnits == 40,
            "The hard limit allows forty half-width characters without an early WPF cutoff");
    }
}
