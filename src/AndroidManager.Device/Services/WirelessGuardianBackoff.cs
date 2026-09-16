namespace AndroidManager.Device.Services;

public static class WirelessGuardianBackoff
{
    private static readonly int[] DelaysSeconds = [1, 2, 4, 8, 15, 30, 60];

    public static TimeSpan DelayForAttempt(int attempt)
    {
        var idx = Math.Clamp(attempt - 1, 0, DelaysSeconds.Length - 1);
        return TimeSpan.FromSeconds(DelaysSeconds[idx]);
    }

    public static TimeSpan FastDelayForCompanionOnline(int attempt) =>
        TimeSpan.FromSeconds(Math.Min(2 * attempt, 8));
}
