using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace ValheimTelemetry
{
    internal static class JsonUtil
    {
        internal static string Health()
        {
            return "{\"status\":\"ok\",\"mod\":\"ValheimTelemetry\",\"version\":\"" +
                   Plugin.PluginVersion + "\"}";
        }

        internal static string Players()
        {
            List<PlayerSnapshot> players = PlayerService.GetPlayers();

            var sb = new StringBuilder();
            sb.Append("{\"count\":");
            sb.Append(players.Count);
            sb.Append(",\"players\":[");

            for (int i = 0; i < players.Count; i++)
            {
                if (i > 0)
                    sb.Append(',');

                AppendPlayer(sb, players[i]);
            }

            sb.Append("]}");
            return sb.ToString();
        }

        internal static string Player(PlayerSnapshot player)
        {
            var sb = new StringBuilder();
            AppendPlayer(sb, player);
            return sb.ToString();
        }

        private static void AppendPlayer(
            StringBuilder sb,
            PlayerSnapshot player)
        {
            sb.Append("{");

            sb.Append("\"id\":\"");
            AppendEscaped(sb, player.Id);
            sb.Append("\",");

            sb.Append("\"name\":\"");
            AppendEscaped(sb, player.Name);
            sb.Append("\",");

            sb.Append("\"deaths\":");
            sb.Append(player.Deaths);
            sb.Append(',');

            sb.Append("\"health\":");
            AppendResource(sb, player.Health);

            sb.Append("}");
        }

        private static void AppendResource(
            StringBuilder sb,
            ResourceSnapshot resource)
        {
            sb.Append("{");

            sb.Append("\"current\":");
            sb.Append(resource.Current.ToString(
                "0.###",
                CultureInfo.InvariantCulture));

            sb.Append(",");

            sb.Append("\"max\":");
            sb.Append(resource.Max.ToString(
                "0.###",
                CultureInfo.InvariantCulture));

            sb.Append("}");
        }

        private static void AppendEscaped(
            StringBuilder sb,
            string value)
        {
            if (value == null)
                return;

            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];

                switch (c)
                {
                    case '"':
                        sb.Append("\\\"");
                        break;

                    case '\\':
                        sb.Append("\\\\");
                        break;

                    case '\b':
                        sb.Append("\\b");
                        break;

                    case '\f':
                        sb.Append("\\f");
                        sb.Append("\\n");
                        break;

                    case '\r':
                        sb.Append("\\r");
                        break;

                    case '\t':
                        sb.Append("\\t");
                        break;

                    default:
                        if (c < 32)
                            sb.Append("\\u" +
                                ((int)c).ToString("x4"));
                        else
                            sb.Append(c);
                        break;
                }
            }
        }
    }
}
