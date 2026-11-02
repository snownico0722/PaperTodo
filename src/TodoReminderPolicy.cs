namespace PaperTodo;

internal static class TodoReminderPolicy
{
    public static bool IsPending(PaperItem item) =>
        !item.Done && !item.ReminderTriggered && item.ReminderAt.HasValue;

    public static bool IsDue(PaperItem item, DateTimeOffset now) =>
        IsPending(item) && item.ReminderAt <= now;

    // A batch balloon represents every due item; opening one paper represents only its first item.
    public static int DeliveredCount(int dueCount, bool openedFirst, bool balloonSurfaced) =>
        balloonSurfaced ? dueCount : openedFirst ? Math.Min(1, dueCount) : 0;
}
