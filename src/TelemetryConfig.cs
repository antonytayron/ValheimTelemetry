using BepInEx.Configuration;

namespace ValheimTelemetry
{
    internal static class TelemetryConfig
    {
        internal static ConfigEntry<string> BindAddress;
        internal static ConfigEntry<int> Port;
        internal static ConfigEntry<string> ApiKey;

        internal static void Load(ConfigFile config)
        {
            BindAddress = config.Bind(
                "HTTP",
                "BindAddress",
                "127.0.0.1",
                "Address on which the REST API listens. Use 0.0.0.0 to expose it to the network.");

            Port = config.Bind(
                "HTTP",
                "Port",
                8765,
                "TCP port used by the REST API.");

            ApiKey = config.Bind(
                "HTTP",
                "ApiKey",
                "",
                "Optional API key. If empty, authentication is disabled. " +
                "Clients may send X-API-Key or Authorization: Bearer <key>.");
        }
    }
}
