using System;
using System.Collections.Generic;
using System.Threading;

namespace ValheimTelemetry
{
    internal static class PlayerService
    {
        private static readonly object SnapshotLock = new object();
        private static Dictionary<string, PlayerSnapshot> _snapshots =
            new Dictionary<string, PlayerSnapshot>();
        private static readonly Dictionary<string, bool> LastDeathState =
            new Dictionary<string, bool>();
        private static readonly Dictionary<string, long> DeathCounts =
            new Dictionary<string, long>();
        private static long _deathsTotal;

        internal static void Initialize()
        {
            lock (SnapshotLock)
            {
                _snapshots = new Dictionary<string, PlayerSnapshot>();
                LastDeathState.Clear();
                DeathCounts.Clear();
                Interlocked.Exchange(ref _deathsTotal, 0);
            }
        }

        // Runs on Unity's main thread only.
        internal static void UpdateSnapshots()
        {
            try
            {
                if (ZNet.instance == null || !ZNet.instance.IsServer())
                {
                    ReplaceSnapshots(new Dictionary<string, PlayerSnapshot>());
                    return;
                }

                List<ZNet.PlayerInfo> online = ZNet.instance.GetPlayerList();
                if (online == null)
                    online = new List<ZNet.PlayerInfo>();

                var result = new Dictionary<string, PlayerSnapshot>();

                foreach (ZNet.PlayerInfo info in online)
                {
                    long playerId = info.m_characterID.UserID;
                    ZDO zdo = null;

                    try
                    {
                        zdo = ZDOMan.instance != null
                            ? ZDOMan.instance.GetZDO(info.m_characterID)
                            : null;

                        if (zdo != null)
                        {
                            long storedPlayerId =
                                zdo.GetLong(ZDOVars.s_playerID, playerId);

                            if (storedPlayerId != 0)
                                playerId = storedPlayerId;
                        }
                    }
                    catch
                    {
                    }

                    string id = playerId != 0
                        ? playerId.ToString()
                        : info.m_characterID.ToString();
                    long deaths = TrackDeaths(id, zdo);

                    result[id] = CreateSnapshot(
                        id,
                        info.m_name,
                        zdo,
                        deaths);
                }

                ReplaceSnapshots(result);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"Unable to update player snapshots: {ex}");
            }
        }

        private static PlayerSnapshot CreateSnapshot(
            string id,
            string name,
            ZDO zdo,
            long deaths)
        {
            return new PlayerSnapshot
            {
                Id = id,
                Name = string.IsNullOrEmpty(name) ? "Unknown" : name,
                Deaths = deaths,
                Health = HealthFromZdo(zdo)
            };
        }

        private static ResourceSnapshot HealthFromZdo(ZDO zdo)
        {
            if (zdo == null)
                return Unavailable();

            try
            {
                float maxHealth = zdo.GetFloat(ZDOVars.s_maxHealth, 0f);
                float health = zdo.GetFloat(ZDOVars.s_health, maxHealth);

                if (float.IsNaN(health) || float.IsInfinity(health) ||
                    float.IsNaN(maxHealth) || float.IsInfinity(maxHealth) ||
                    maxHealth <= 0f)
                {
                    return Unavailable();
                }

                return new ResourceSnapshot
                {
                    Current = health,
                    Max = maxHealth
                };
            }
            catch
            {
                return Unavailable();
            }
        }

        private static long TrackDeaths(string id, ZDO zdo)
        {
            long deaths;
            DeathCounts.TryGetValue(id, out deaths);

            if (zdo == null)
                return deaths;

            bool isDead;
            try
            {
                isDead = zdo.GetBool(ZDOVars.s_dead, false);
            }
            catch
            {
                return deaths;
            }

            bool wasDead;
            if (LastDeathState.TryGetValue(id, out wasDead))
            {
                if (!wasDead && isDead)
                {
                    deaths++;
                    DeathCounts[id] = deaths;
                    Interlocked.Increment(ref _deathsTotal);
                }
            }
            else if (!DeathCounts.ContainsKey(id))
            {
                DeathCounts[id] = 0;
            }

            LastDeathState[id] = isDead;
            DeathCounts.TryGetValue(id, out deaths);
            return deaths;
        }

        private static ResourceSnapshot Unavailable()
        {
            return new ResourceSnapshot
            {
                Current = 0f,
                Max = 0f
            };
        }

        internal static List<PlayerSnapshot> GetPlayers()
        {
            lock (SnapshotLock)
                return new List<PlayerSnapshot>(_snapshots.Values);
        }

        internal static long GetDeathsTotal()
        {
            return Interlocked.Read(ref _deathsTotal);
        }

        internal static bool TryGetPlayer(
            string id,
            out PlayerSnapshot snapshot)
        {
            snapshot = null;

            if (string.IsNullOrWhiteSpace(id))
                return false;

            lock (SnapshotLock)
                return _snapshots.TryGetValue(id, out snapshot);
        }

        internal static void Clear()
        {
            lock (SnapshotLock)
            {
                _snapshots = new Dictionary<string, PlayerSnapshot>();
                LastDeathState.Clear();
                DeathCounts.Clear();
                Interlocked.Exchange(ref _deathsTotal, 0);
            }
        }

        private static void ReplaceSnapshots(
            Dictionary<string, PlayerSnapshot> snapshots)
        {
            lock (SnapshotLock)
                _snapshots = snapshots;
        }

    }

    internal sealed class PlayerSnapshot
    {
        public string Id;
        public string Name;
        public long Deaths;
        public ResourceSnapshot Health;
    }

    internal sealed class ResourceSnapshot
    {
        public float Current;
        public float Max;
    }
}
