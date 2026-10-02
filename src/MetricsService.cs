using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEngine;

namespace ValheimTelemetry
{
    internal static class MetricsService
    {
        private static readonly object Lock = new object();
        private static readonly Stopwatch CpuClock = Stopwatch.StartNew();

        private static double _cpuPercent;
        private static long _processMemoryBytes;
        private static long _systemMemoryTotalBytes;
        private static long _systemMemoryAvailableBytes;

        private static long _rxBytes;
        private static long _txBytes;
        private static double _rxBytesPerSecond;
        private static double _txBytesPerSecond;
        private static long _previousRxBytes;
        private static long _previousTxBytes;
        private static double _previousSampleSeconds;

        private static int _worldDay;
        private static double _worldTimeSeconds;
        private static double _dayTimeSeconds;
        private static int _worldPortals;

        private static int _onlinePlayers;
        private static int _playerLimit;
        private static bool _publicServer;
        private static bool _networkReady;

        internal static void Initialize()
        {
            lock (Lock)
            {
                _cpuPercent = 0;
                _processMemoryBytes = 0;
                _systemMemoryTotalBytes = 0;
                _systemMemoryAvailableBytes = 0;
                _rxBytes = 0;
                _txBytes = 0;
                _rxBytesPerSecond = 0;
                _txBytesPerSecond = 0;
                _previousRxBytes = 0;
                _previousTxBytes = 0;
                _previousSampleSeconds = 0;
                _worldDay = 0;
                _worldTimeSeconds = 0;
                _dayTimeSeconds = 0;
                _worldPortals = 0;
                _onlinePlayers = 0;
                _playerLimit = 0;
                _publicServer = false;
                _networkReady = false;
            }
        }

        // Runs on Unity's main thread.
        internal static void Update()
        {
            try
            {
                UpdateProcessMetrics();
                UpdateNetworkMetrics();
                UpdateWorldMetrics();
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"Metrics update failed: {ex.Message}");
            }
        }

        private static void UpdateProcessMetrics()
        {
            Process process = null;

            try
            {
                process = Process.GetCurrentProcess();

                double now = CpuClock.Elapsed.TotalSeconds;
                double previous = _previousSampleSeconds;

                TimeSpan cpu = process.TotalProcessorTime;

                if (previous > 0 && now > previous)
                {
                    // Store previous CPU time in a separate static field via
                    // the field below. This avoids DateTime/system clock jumps.
                    double deltaCpu = cpu.TotalSeconds - _previousCpuSeconds;
                    double deltaWall = now - previous;

                    if (deltaCpu >= 0 && deltaWall > 0)
                    {
                        int processors = Math.Max(
                            1,
                            Environment.ProcessorCount);

                        _cpuPercent =
                            (deltaCpu / deltaWall) /
                            processors * 100.0;
                    }
                }

                _previousCpuSeconds = cpu.TotalSeconds;
                _previousSampleSeconds = now;
                _processMemoryBytes = process.WorkingSet64;

                ReadProcMemory();
            }
            finally
            {
                if (process != null)
                    process.Dispose();
            }

            lock (Lock)
            {
                // Values were updated in fields above; lock only protects
                // HTTP readers from seeing a partially updated snapshot.
            }
        }

        private static double _previousCpuSeconds;

        private static void ReadProcMemory()
        {
            try
            {
                if (!File.Exists("/proc/meminfo"))
                    return;

                long totalKb = 0;
                long availableKb = 0;

                foreach (string line in File.ReadAllLines("/proc/meminfo"))
                {
                    if (line.StartsWith("MemTotal:", StringComparison.Ordinal))
                        totalKb = ParseKb(line);

                    else if (line.StartsWith("MemAvailable:", StringComparison.Ordinal))
                        availableKb = ParseKb(line);
                }

                _systemMemoryTotalBytes = totalKb * 1024L;
                _systemMemoryAvailableBytes = availableKb * 1024L;
            }
            catch
            {
            }
        }

        private static long ParseKb(string line)
        {
            string[] parts = line.Split(
                new[] { ' ', '\t' },
                StringSplitOptions.RemoveEmptyEntries);

            long value;

            if (parts.Length >= 2 &&
                long.TryParse(
                    parts[1],
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out value))
            {
                return value;
            }

            return 0;
        }

        private static void UpdateNetworkMetrics()
        {
            long rx = 0;
            long tx = 0;

            try
            {
                if (File.Exists("/proc/net/dev"))
                {
                    foreach (string line in File.ReadAllLines("/proc/net/dev"))
                    {
                        int colon = line.IndexOf(':');
                        if (colon < 0)
                            continue;

                        string iface =
                            line.Substring(0, colon).Trim();

                        if (iface == "lo")
                            continue;

                        string[] fields =
                            line.Substring(colon + 1)
                                .Split(
                                    new[] { ' ', '\t' },
                                    StringSplitOptions.RemoveEmptyEntries);

                        if (fields.Length < 9)
                            continue;

                        long ifaceRx;
                        long ifaceTx;

                        if (long.TryParse(
                            fields[0],
                            NumberStyles.Integer,
                            CultureInfo.InvariantCulture,
                            out ifaceRx))
                        {
                            rx += ifaceRx;
                        }

                        if (long.TryParse(
                            fields[8],
                            NumberStyles.Integer,
                            CultureInfo.InvariantCulture,
                            out ifaceTx))
                        {
                            tx += ifaceTx;
                        }
                    }
                }
            }
            catch
            {
            }

            double now = CpuClock.Elapsed.TotalSeconds;

            if (_previousNetworkSampleSeconds > 0 &&
                now > _previousNetworkSampleSeconds)
            {
                double interval =
                    now - _previousNetworkSampleSeconds;

                if (rx >= _previousRxBytes)
                    _rxBytesPerSecond =
                        (rx - _previousRxBytes) / interval;

                if (tx >= _previousTxBytes)
                    _txBytesPerSecond =
                        (tx - _previousTxBytes) / interval;
            }

            _previousRxBytes = rx;
            _previousTxBytes = tx;
            _previousNetworkSampleSeconds = now;

            _rxBytes = rx;
            _txBytes = tx;
        }

        private static double _previousNetworkSampleSeconds;

        private static void UpdateWorldMetrics()
        {
            if (ZNet.instance == null)
                return;

            try
            {
                _networkReady =
                    ZNet.instance.IsServer();

                _onlinePlayers =
                    PlayerService.GetPlayers().Count;

                _worldTimeSeconds =
                    ZNet.instance.GetTimeSeconds();

                _dayTimeSeconds =
                    ZNet.instance.GetWrappedDayTimeSeconds();

                _worldPortals = ZDOMan.instance != null
                    ? ZDOMan.instance.GetPortalList().Count
                    : 0;

                if (EnvMan.instance != null)
                    _worldDay = EnvMan.instance.GetDay();

                _playerLimit =
                    ReadPrivateInt(
                        typeof(ZNet),
                        "m_serverPlayerLimit",
                        0);

                _publicServer =
                    ReadPrivateStaticBool(
                        typeof(ZNet),
                        "m_publicServer",
                        false);
            }
            catch
            {
            }
        }

        private static int ReadPrivateInt(
            Type type,
            string field,
            int fallback)
        {
            try
            {
                FieldInfo info =
                    type.GetField(
                        field,
                        BindingFlags.Static |
                        BindingFlags.Instance |
                        BindingFlags.Public |
                        BindingFlags.NonPublic);

                if (info == null)
                    return fallback;

                object target = info.IsStatic
                    ? null
                    : (object)ZNet.instance;

                object value = info.GetValue(target);

                return value is int
                    ? (int)value
                    : fallback;
            }
            catch
            {
                return fallback;
            }
        }

        private static bool ReadPrivateStaticBool(
            Type type,
            string field,
            bool fallback)
        {
            try
            {
                FieldInfo info =
                    type.GetField(
                        field,
                        BindingFlags.Static |
                        BindingFlags.NonPublic |
                        BindingFlags.Public);

                if (info == null)
                    return fallback;

                object value = info.GetValue(null);

                return value is bool
                    ? (bool)value
                    : fallback;
            }
            catch
            {
                return fallback;
            }
        }

        internal static string ToPrometheus()
        {
            var sb = new StringBuilder();

            AppendHelpType(sb, "valheim_server_info",
                "Static Valheim server information.", "gauge");
            sb.Append("valheim_server_info{plugin_version=\"")
              .Append(Escape(Plugin.PluginVersion))
              .Append("\"} 1\n");

            AppendGauge(sb, "valheim_players_online",
                "Number of online players.", _onlinePlayers);

            AppendGauge(sb, "valheim_server_player_limit",
                "Configured player limit.", _playerLimit);

            AppendGauge(sb, "valheim_server_public",
                "Whether the Valheim server is configured as public.",
                _publicServer ? 1 : 0);

            AppendGauge(sb, "valheim_network_ready",
                "Whether the Valheim server network is ready.",
                _networkReady ? 1 : 0);

            AppendGauge(sb, "valheim_process_cpu_percent",
                "Valheim process CPU usage as percent of total host CPU.",
                _cpuPercent);

            AppendGauge(sb, "valheim_process_memory_bytes",
                "Valheim process working set in bytes.",
                _processMemoryBytes);

            AppendGauge(sb, "valheim_system_memory_total_bytes",
                "System memory total in bytes.",
                _systemMemoryTotalBytes);

            AppendGauge(sb, "valheim_system_memory_available_bytes",
                "System memory available in bytes.",
                _systemMemoryAvailableBytes);

            AppendCounter(sb, "valheim_network_receive_bytes_total",
                "Bytes received by non-loopback interfaces.",
                _rxBytes);

            AppendCounter(sb, "valheim_network_transmit_bytes_total",
                "Bytes transmitted by non-loopback interfaces.",
                _txBytes);

            AppendGauge(sb, "valheim_network_receive_bytes_per_second",
                "Current aggregate receive rate.",
                _rxBytesPerSecond);

            AppendGauge(sb, "valheim_network_transmit_bytes_per_second",
                "Current aggregate transmit rate.",
                _txBytesPerSecond);

            AppendGauge(sb, "valheim_world_day",
                "Current Valheim world day.",
                _worldDay);

            AppendGauge(sb, "valheim_world_time_seconds",
                "Seconds elapsed since world start.",
                _worldTimeSeconds);

            AppendGauge(sb, "valheim_world_day_time_seconds",
                "Current time within the Valheim day.",
                _dayTimeSeconds);

            AppendGauge(sb, "valheim_world_portals",
                "Number of portal ZDOs currently tracked by the server.",
                _worldPortals);

            AppendCounter(sb, "valheim_player_deaths_total",
                "Player deaths observed since this plugin started.",
                PlayerService.GetDeathsTotal());

            List<PlayerSnapshot> players =
                PlayerService.GetPlayers();

            AppendGauge(sb, "valheim_player_count",
                "Number of player telemetry snapshots.",
                players.Count);

            AppendHelpType(sb, "valheim_player_info",
                "Online Valheim player information.", "gauge");

            AppendHelpType(sb, "valheim_player_health",
                "Current player health.", "gauge");

            AppendHelpType(sb, "valheim_player_health_max",
                "Maximum player health.", "gauge");

            foreach (PlayerSnapshot player in players)
            {
                string id = Escape(player.Id);
                string name = Escape(player.Name);

                sb.Append("valheim_player_info{player_id=\"")
                  .Append(id)
                  .Append("\",name=\"")
                  .Append(name)
                  .Append("\"} 1\n");

                AppendPlayerGauge(
                    sb, "valheim_player_health",
                    player.Id, player.Health.Current);

                AppendPlayerGauge(
                    sb, "valheim_player_health_max",
                    player.Id, player.Health.Max);
            }

            return sb.ToString();
        }

        private static void AppendPlayerGauge(
            StringBuilder sb,
            string name,
            string playerId,
            double value)
        {
            sb.Append(name)
              .Append("{player_id=\"")
              .Append(Escape(playerId))
              .Append("\"} ")
              .Append(Number(value))
              .Append('\n');
        }

        private static void AppendGauge(
            StringBuilder sb,
            string name,
            string help,
            double value)
        {
            AppendHelpType(sb, name, help, "gauge");
            sb.Append(name)
              .Append(' ')
              .Append(Number(value))
              .Append('\n');
        }

        private static void AppendCounter(
            StringBuilder sb,
            string name,
            string help,
            double value)
        {
            AppendHelpType(sb, name, help, "counter");
            sb.Append(name)
              .Append(' ')
              .Append(Number(value))
              .Append('\n');
        }

        private static void AppendHelpType(
            StringBuilder sb,
            string name,
            string help,
            string type)
        {
            sb.Append("# HELP ")
              .Append(name)
              .Append(' ')
              .Append(help)
              .Append('\n');

            sb.Append("# TYPE ")
              .Append(name)
              .Append(' ')
              .Append(type)
              .Append('\n');
        }

        private static string Number(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
                return "0";

            return value.ToString(
                "0.######",
                CultureInfo.InvariantCulture);
        }

        private static string Escape(string value)
        {
            if (value == null)
                return string.Empty;

            return value
                .Replace("\\", "\\\\")
                .Replace("\"", "\\\"")
                .Replace("\n", "\\n")
                .Replace("\r", "\\r");
        }

        internal static void Clear()
        {
            Initialize();
        }
    }
}
