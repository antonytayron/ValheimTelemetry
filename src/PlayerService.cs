using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using BepInEx;

namespace ValheimTelemetry
{
    internal static class PlayerService
    {
        private const int MaxEvents = 1000;
        private static readonly object SnapshotLock = new object();
        private static readonly Queue<PlayerEvent> Events =
            new Queue<PlayerEvent>();
        private static Dictionary<string, PlayerSnapshot> _snapshots =
            new Dictionary<string, PlayerSnapshot>();
        private static readonly Dictionary<string, bool> LastDeathState =
            new Dictionary<string, bool>();
        private static readonly Dictionary<string, long> DeathCounts =
            new Dictionary<string, long>();
        private static readonly Dictionary<string, string> PlayerNames =
            new Dictionary<string, string>();
        private static string _persistencePath;
        private static bool _persistenceAvailable;
        private static long _deathsTotal;
        private static long _nextEventId;
        private static bool _hasSnapshotBaseline;

        internal static void Initialize()
        {
            lock (SnapshotLock)
            {
                _snapshots = new Dictionary<string, PlayerSnapshot>();
                Events.Clear();
                _nextEventId = 0;
                _hasSnapshotBaseline = false;
                LastDeathState.Clear();
                DeathCounts.Clear();
                PlayerNames.Clear();
                Interlocked.Exchange(ref _deathsTotal, 0);
                LoadPersistentData();
            }
        }

        // Runs on Unity's main thread only.
        internal static void UpdateSnapshots()
        {
            try
            {
                if (ZNet.instance == null || !ZNet.instance.IsServer())
                {
                    ReplaceSnapshots(
                        new Dictionary<string, PlayerSnapshot>(),
                        false);
                    return;
                }

                List<ZNet.PlayerInfo> online = ZNet.instance.GetPlayerList();
                if (online == null)
                    online = new List<ZNet.PlayerInfo>();

                var result = new Dictionary<string, PlayerSnapshot>();
                bool persistenceChanged = false;

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
                    string name = string.IsNullOrEmpty(info.m_name)
                        ? "Unknown"
                        : info.m_name;
                    long deaths = TrackDeaths(
                        id,
                        name,
                        zdo,
                        ref persistenceChanged);

                    result[id] = CreateSnapshot(
                        id,
                        name,
                        zdo,
                        deaths);
                }

                if (persistenceChanged)
                    SavePersistentData();

                ReplaceSnapshots(result, true);
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

        private static long TrackDeaths(
            string id,
            string name,
            ZDO zdo,
            ref bool persistenceChanged)
        {
            bool hasDeadState = false;
            bool isDead = false;
            if (zdo != null)
            {
                try
                {
                    isDead = zdo.GetBool(ZDOVars.s_dead, false);
                    hasDeadState = true;
                }
                catch
                {
                }
            }

            lock (SnapshotLock)
            {
                long deaths;
                if (!DeathCounts.TryGetValue(id, out deaths))
                {
                    DeathCounts[id] = 0;
                    persistenceChanged = true;
                }

                string previousName;
                if (!PlayerNames.TryGetValue(id, out previousName) ||
                    !string.Equals(
                        previousName,
                        name,
                        StringComparison.Ordinal))
                {
                    PlayerNames[id] = name;
                    persistenceChanged = true;
                }

                if (hasDeadState)
                {
                    bool wasDead;
                    if (LastDeathState.TryGetValue(id, out wasDead) &&
                        !wasDead && isDead)
                    {
                        deaths = DeathCounts[id] + 1;
                        DeathCounts[id] = deaths;
                        Interlocked.Increment(ref _deathsTotal);
                        persistenceChanged = true;
                    }

                    LastDeathState[id] = isDead;
                }

                DeathCounts.TryGetValue(id, out deaths);
                return deaths;
            }
        }

        private static void LoadPersistentData()
        {
            try
            {
                _persistencePath = Path.Combine(
                    Paths.ConfigPath,
                    Plugin.PluginGuid + ".players");

                if (!File.Exists(_persistencePath))
                {
                    _persistenceAvailable = true;
                    return;
                }

                string[] lines = File.ReadAllLines(_persistencePath);
                if (lines.Length == 0 ||
                    lines[0] != "# ValheimTelemetry player data v1")
                {
                    throw new InvalidDataException(
                        "Unrecognized player data file format.");
                }

                var loadedDeaths = new Dictionary<string, long>();
                var loadedNames = new Dictionary<string, string>();
                long totalDeaths = 0;

                for (int i = 1; i < lines.Length; i++)
                {
                    if (string.IsNullOrWhiteSpace(lines[i]))
                        continue;

                    string[] fields = lines[i].Split('\t');
                    long deaths;
                    if (fields.Length != 3 ||
                        !long.TryParse(
                            fields[2],
                            NumberStyles.None,
                            CultureInfo.InvariantCulture,
                            out deaths) ||
                        deaths < 0)
                    {
                        throw new InvalidDataException(
                            "Invalid player data at line " + (i + 1) + ".");
                    }

                    string id = Encoding.UTF8.GetString(
                        Convert.FromBase64String(fields[0]));
                    string name = Encoding.UTF8.GetString(
                        Convert.FromBase64String(fields[1]));

                    if (string.IsNullOrWhiteSpace(id) ||
                        loadedDeaths.ContainsKey(id))
                    {
                        throw new InvalidDataException(
                            "Invalid or duplicate player ID at line " +
                            (i + 1) + ".");
                    }

                    loadedDeaths.Add(id, deaths);
                    loadedNames.Add(id, name);
                    totalDeaths = checked(totalDeaths + deaths);
                }

                foreach (KeyValuePair<string, long> entry in loadedDeaths)
                    DeathCounts.Add(entry.Key, entry.Value);

                foreach (KeyValuePair<string, string> entry in loadedNames)
                    PlayerNames.Add(entry.Key, entry.Value);

                Interlocked.Exchange(ref _deathsTotal, totalDeaths);
                _persistenceAvailable = true;
            }
            catch (Exception ex)
            {
                _persistenceAvailable = false;
                Plugin.Log.LogError(
                    $"Unable to load persisted player data; the data file will not be overwritten: {ex}");
            }
        }

        private static void SavePersistentData()
        {
            if (!_persistenceAvailable ||
                string.IsNullOrEmpty(_persistencePath))
            {
                return;
            }

            string contents;
            lock (SnapshotLock)
            {
                var sb = new StringBuilder();
                sb.AppendLine("# ValheimTelemetry player data v1");

                foreach (KeyValuePair<string, long> entry in DeathCounts)
                {
                    string name;
                    PlayerNames.TryGetValue(entry.Key, out name);

                    sb.Append(Convert.ToBase64String(
                            Encoding.UTF8.GetBytes(entry.Key)))
                      .Append('\t')
                      .Append(Convert.ToBase64String(
                            Encoding.UTF8.GetBytes(name ?? string.Empty)))
                      .Append('\t')
                      .Append(entry.Value.ToString(
                            CultureInfo.InvariantCulture))
                      .AppendLine();
                }

                contents = sb.ToString();
            }

            try
            {
                string temporaryPath = _persistencePath + ".tmp";
                File.WriteAllText(
                    temporaryPath,
                    contents,
                    new UTF8Encoding(false));

                if (File.Exists(_persistencePath))
                    File.Replace(temporaryPath, _persistencePath, null);
                else
                    File.Move(temporaryPath, _persistencePath);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError(
                    $"Unable to save persisted player data: {ex}");
            }
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

        internal static List<PlayerDeathSnapshot> GetDeathRecords()
        {
            var result = new List<PlayerDeathSnapshot>();

            lock (SnapshotLock)
            {
                foreach (KeyValuePair<string, long> entry in DeathCounts)
                {
                    string name;
                    PlayerNames.TryGetValue(entry.Key, out name);
                    result.Add(new PlayerDeathSnapshot
                    {
                        Id = entry.Key,
                        Name = string.IsNullOrEmpty(name) ? "Unknown" : name,
                        Deaths = entry.Value
                    });
                }
            }

            return result;
        }

        internal static List<PlayerEvent> GetEventsAfter(
            long afterId,
            out long earliestId,
            out long latestId)
        {
            var result = new List<PlayerEvent>();

            lock (SnapshotLock)
            {
                latestId = _nextEventId;
                earliestId = Events.Count > 0
                    ? Events.Peek().Id
                    : _nextEventId + 1;

                foreach (PlayerEvent playerEvent in Events)
                {
                    if (playerEvent.Id > afterId)
                        result.Add(playerEvent);
                }
            }

            return result;
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
                Events.Clear();
                _nextEventId = 0;
                _hasSnapshotBaseline = false;
                LastDeathState.Clear();
                DeathCounts.Clear();
                PlayerNames.Clear();
                Interlocked.Exchange(ref _deathsTotal, 0);
            }
        }

        private static void ReplaceSnapshots(
            Dictionary<string, PlayerSnapshot> snapshots,
            bool authoritative)
        {
            lock (SnapshotLock)
            {
                if (!authoritative)
                {
                    _hasSnapshotBaseline = false;
                }
                else if (_hasSnapshotBaseline)
                {
                    foreach (KeyValuePair<string, PlayerSnapshot> previous
                        in _snapshots)
                    {
                        if (!snapshots.ContainsKey(previous.Key))
                            AddEvent("player_left", previous.Value);
                    }

                    foreach (KeyValuePair<string, PlayerSnapshot> current
                        in snapshots)
                    {
                        if (!_snapshots.ContainsKey(current.Key))
                            AddEvent("player_joined", current.Value);
                    }
                }
                else
                {
                    _hasSnapshotBaseline = true;
                }

                _snapshots = snapshots;
            }
        }

        private static void AddEvent(
            string type,
            PlayerSnapshot player)
        {
            Events.Enqueue(new PlayerEvent
            {
                Id = ++_nextEventId,
                Type = type,
                PlayerId = player.Id,
                PlayerName = player.Name,
                Timestamp = DateTime.UtcNow.ToString(
                    "o",
                    CultureInfo.InvariantCulture)
            });

            while (Events.Count > MaxEvents)
                Events.Dequeue();
        }

    }

    internal sealed class PlayerDeathSnapshot
    {
        public string Id;
        public string Name;
        public long Deaths;
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

    internal sealed class PlayerEvent
    {
        public long Id;
        public string Type;
        public string PlayerId;
        public string PlayerName;
        public string Timestamp;
    }
}
