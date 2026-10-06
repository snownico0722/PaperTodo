namespace PaperTodo;

internal static class AnimationTiming
{
    internal const double FastDurationScale = 0.60;

    private static bool UseFastDurations =>
        AppController.Current?.State is
        {
            EnableAnimations: true,
            FastAnimations: true
        };

    internal static int ScaleMilliseconds(int durationMilliseconds)
    {
        if (durationMilliseconds <= 0 || !UseFastDurations)
        {
            return durationMilliseconds;
        }

        return Math.Max(
            1,
            (int)Math.Round(
                durationMilliseconds * FastDurationScale,
                MidpointRounding.AwayFromZero));
    }

    internal static double ScaleMilliseconds(double durationMilliseconds)
    {
        if (durationMilliseconds <= 0 || !UseFastDurations)
        {
            return durationMilliseconds;
        }

        return Math.Max(1, durationMilliseconds * FastDurationScale);
    }
}
