namespace AndroidManager.Messages;

internal static class WhatsAppWebPaths
{
    public const string WebUrl = "https://web.whatsapp.com/";

    public static string UserDataDirectory =>
        System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AndroidManager",
            "WhatsAppWeb");
}
