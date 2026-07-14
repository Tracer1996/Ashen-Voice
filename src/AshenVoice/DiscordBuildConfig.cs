namespace AshenVoice;

internal static class DiscordBuildConfig
{
    // Replaced by the GitHub Actions workflow from the DISCORD_CLIENT_ID repository variable.
    public const string ClientId = "__DISCORD_CLIENT_ID__";

    public const string RedirectUri = "http://127.0.0.1:53682/callback/";

    public static bool IsConfigured =>
        ClientId.Length is >= 17 and <= 20 && ClientId.All(char.IsDigit);
}
