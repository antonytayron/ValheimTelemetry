using System;
using BepInEx;
using BepInEx.Logging;
using UnityEngine;

namespace ValheimTelemetry
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "br.com.midgard.valheimtelemetry";
        public const string PluginName = "Valheim Telemetry";
        public const string PluginVersion = "0.2.0";

        internal static ManualLogSource Log { get; private set; }

        private RestServer _restServer;
        private float _nextPlayerSnapshot;
        private float _nextMetricsSnapshot;

        private const float PlayerSnapshotInterval = 0.25f;
        private const float MetricsSnapshotInterval = 1.0f;

        private void Awake()
        {
            Log = Logger;

            try
            {
                TelemetryConfig.Load(Config);
                PlayerService.Initialize();
                MetricsService.Initialize();

                _restServer = new RestServer(
                    TelemetryConfig.BindAddress.Value,
                    TelemetryConfig.Port.Value,
                    TelemetryConfig.ApiKey.Value);

                _restServer.Start();

                _nextPlayerSnapshot = 0f;
                _nextMetricsSnapshot = 0f;

                Log.LogInfo(
                    $"{PluginName} {PluginVersion} loaded. " +
                    $"REST API: http://{TelemetryConfig.BindAddress.Value}:" +
                    $"{TelemetryConfig.Port.Value}/api/v1 " +
                    $"Metrics: /metrics");
            }
            catch (Exception ex)
            {
                Log.LogError($"Failed to start {PluginName}: {ex}");
            }
        }

        private void Update()
        {
            try
            {
                float now = Time.unscaledTime;

                if (now >= _nextPlayerSnapshot)
                {
                    _nextPlayerSnapshot = now + PlayerSnapshotInterval;
                    PlayerService.UpdateSnapshots();
                }

                if (now >= _nextMetricsSnapshot)
                {
                    _nextMetricsSnapshot = now + MetricsSnapshotInterval;
                    MetricsService.Update();
                }
            }
            catch (Exception ex)
            {
                Log.LogError($"Telemetry update failed: {ex}");
            }
        }

        private void OnDestroy()
        {
            try
            {
                if (_restServer != null)
                {
                    _restServer.Stop();
                    _restServer = null;
                }

                PlayerService.Clear();
                MetricsService.Clear();
            }
            catch (Exception ex)
            {
                Log.LogError($"Error stopping {PluginName}: {ex}");
            }
        }
    }
}
