namespace AndroidManager.Device.Parsing;

public static class AdbConnectResult
{
    public static bool IsSuccess(string? adbOutput)
    {
        if (string.IsNullOrWhiteSpace(adbOutput))
            return false;

        return adbOutput.Contains("connected", StringComparison.OrdinalIgnoreCase)
               || adbOutput.Contains("already", StringComparison.OrdinalIgnoreCase);
    }
}
