using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using HarmonyLib;
using UnityEngine;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace Oxide.Plugins
{
    [Info("LootSpawnStagger", "Lucien", "0.3.1")]
    [Description("Paces vanilla respawning so it never lands in one frame: splits population refills into small chunks, caps spawn-group work per frame, and staggers junk pile / dive site lifetimes and monument puzzle resets.")]
    public class LootSpawnStagger : RustPlugin
    {
        private const string HarmonyId = "com.lucienmp.lootspawnstagger";

        private static LootSpawnStagger _instance;
        private PluginConfig _config;
        private Harmony _harmony;
        private GameObject _host;

        // Expected expiry (realtimeSinceStartup) for each junk pile we have rescheduled, by net ID
        private readonly Dictionary<ulong, float> _expiries = new Dictionary<ulong, float>();

        // RustNav builder thread count before we changed it (-1 = not changed)
        private int _originalRustNavThreads = -1;
        private Timer _rustNavTimer;

        #region Config

        private class PluginConfig
        {
            // Population refills (junk piles, dive sites, ores, collectables...) spawn this many entities
            // per call; the rest is queued. Queued spawns run one call at a time until this frame has
            // used PopulationBudgetMs. One junk pile costs ~8ms, so chunks of 3 made ~24ms frames.
            public int PopulationMaxPerCall = 1;
            public float PopulationBudgetMs = 4f;

            // Spread each population tick's refills evenly over this many seconds instead of spawning them
            // as fast as the budget allows. Every spawn makes RustNav rebuild ~15 navmesh tiles, and
            // ~900 tiles rebuilt at once each minute pushed server CPU from ~40% to ~55%.
            // Kept below spawn.tick_populations so one tick's refills finish before the next tick.
            public bool SpreadPopulationRefills = true;
            public float PopulationSpreadSeconds = 45f;

            // RustNav rebuilds navmesh tiles on its own threads (4 by default). On this server those
            // rebuild bursts crowded out Unity's job threads and caused 26-45ms main-thread stalls about
            // once a minute; 1 thread cut them by ~80%. Applied a while after startup, so the full navmesh
            // bake at boot still gets all its threads. Off by default: the plugin warns at startup instead.
            public bool ReduceRustNavThreads = false;
            public int RustNavThreadsAfterStartup = 1;
            public float RustNavThreadsDelaySeconds = 300f;

            // Spawn-group spawns (junk pile / monument / deep sea contents) are deferred to the following
            // frames once this frame has used GroupSpawnBudgetMs on them, or made this many calls.
            // Time, not just calls: one NPC group with weapons costs several ms per call.
            public float GroupSpawnBudgetMs = 8f;
            public int MaxGroupSpawnCallsPerFrame = 8;

            // A group spawn asking for more entities than this is split: this many now, the rest queued.
            // A single call for a whole tunnel group (several NPCs with weapons) took 10-25ms.
            public int GroupMaxPerCall = 1;

            // Upper bound on time spent draining deferred spawns each frame
            public float FrameBudgetMs = 6f;

            // Vanilla junk pile lifetime is 1800s +/- 30s; everything spawned together expires together
            public bool StaggerJunkPileLifetimes = true;
            public float MinLifetimeMinutes = 20f;
            public float MaxLifetimeMinutes = 40f;
            public bool SpreadExistingOnLoad = true;

            // Monument puzzle resets (tunnels, underwater labs, puzzle monuments) clear and refill their
            // spawn groups every ~30 min. Their clocks all start together, so they all reset together.
            public bool StaggerPuzzleResets = true;
        }

        protected override void LoadDefaultConfig()
        {
            _config = new PluginConfig();
            SaveConfig();
        }

        protected override void LoadConfig()
        {
            base.LoadConfig();
            try
            {
                _config = Config.ReadObject<PluginConfig>();
                if (_config == null)
                    throw new Exception("Config file is null");
            }
            catch
            {
                PrintWarning("Config invalid, generating new one.");
                LoadDefaultConfig();
            }

            _config.PopulationMaxPerCall = Mathf.Max(1, _config.PopulationMaxPerCall);
            _config.PopulationBudgetMs = Mathf.Max(1f, _config.PopulationBudgetMs);
            _config.MaxGroupSpawnCallsPerFrame = Mathf.Max(1, _config.MaxGroupSpawnCallsPerFrame);
            _config.GroupSpawnBudgetMs = Mathf.Max(1f, _config.GroupSpawnBudgetMs);
            _config.GroupMaxPerCall = Mathf.Max(1, _config.GroupMaxPerCall);
            _config.FrameBudgetMs = Mathf.Max(1f, _config.FrameBudgetMs);
            _config.PopulationSpreadSeconds = Mathf.Max(0f, _config.PopulationSpreadSeconds);
            _config.RustNavThreadsAfterStartup = Mathf.Max(1, _config.RustNavThreadsAfterStartup);
            _config.RustNavThreadsDelaySeconds = Mathf.Max(0f, _config.RustNavThreadsDelaySeconds);
            _config.MinLifetimeMinutes = Mathf.Max(5f, _config.MinLifetimeMinutes);
            _config.MaxLifetimeMinutes = Mathf.Max(_config.MinLifetimeMinutes + 1f, _config.MaxLifetimeMinutes);

            // Writes new settings into an existing config file (and drops renamed ones)
            SaveConfig();
        }

        protected override void SaveConfig() =>
            Config.WriteObject(_config, true);

        #endregion

        #region Lifecycle

        private void OnServerInitialized()
        {
            _instance = this;
            Pacer.Init(_config);

            // Patched here rather than in Init(), matching the other Harmony plugins on this server
            _harmony = new Harmony(HarmonyId);
            Pacer.PopulationPatched = TryPatch(typeof(SpawnPopulationBase), "Fill",
                new[] { typeof(SpawnHandler), typeof(SpawnDistribution), typeof(int), typeof(bool) },
                typeof(Pacer), nameof(Pacer.FillPrefix), null);
            Pacer.GroupPatched = Pacer.ResolveGroupSpawn() && TryPatch(typeof(SpawnGroup), "Spawn",
                new[] { typeof(int) }, typeof(Pacer), nameof(Pacer.GroupSpawnPrefix), nameof(Pacer.GroupSpawnPostfix));

            int puzzleResetsStaggered = 0;
            if (_config.StaggerPuzzleResets)
            {
                _canUseRadiationReset = AccessTools.Property(typeof(PuzzleReset), "canUseRadiationReset");
                if (TryPatch(typeof(PuzzleReset), "ResetTimer", Type.EmptyTypes, typeof(LootSpawnStagger), null, nameof(ResetTimerPostfix)))
                {
                    // Their clocks were started together at server startup (or by a BetterNpc load)
                    foreach (var reset in PuzzleReset.AllResets)
                        if (StaggerPuzzleReset(reset)) puzzleResetsStaggered++;
                }
            }

            if (_config.StaggerJunkPileLifetimes)
            {
                TryPatch(typeof(JunkPile), "StartTimeout", Type.EmptyTypes, typeof(LootSpawnStagger), null, nameof(StartTimeoutPostfix));

                if (_config.SpreadExistingOnLoad)
                {
                    var piles = BaseNetworkable.serverEntities.OfType<JunkPile>().ToList();
                    foreach (var pile in piles)
                        Reschedule(pile, UnityEngine.Random.Range(60f, _config.MaxLifetimeMinutes * 60f));
                    Puts($"Spread {piles.Count} existing junk piles / dive sites over the next 1-{_config.MaxLifetimeMinutes:0} min");
                }
            }

            _host = new GameObject("LootSpawnStagger");
            _host.AddComponent<PacerBehaviour>();
            Pacer.Active = true;

            if (_config.ReduceRustNavThreads)
            {
                // realtimeSinceStartup counts from server start, so a late plugin load applies at once
                float wait = _config.RustNavThreadsDelaySeconds - Time.realtimeSinceStartup;
                _rustNavTimer = timer.Once(Mathf.Max(1f, wait), ApplyRustNavThreads);
                Puts($"RustNav navmesh builder will be set to {_config.RustNavThreadsAfterStartup} thread(s) {(wait > 1f ? $"in {wait:0}s" : "now")}");
            }
            else
            {
                PrintWarning($"RustNav navmesh builder is using {ReadRustNavThreads()} thread(s). On this server, its rebuild bursts after each " +
                             "population tick caused a main-thread stall (26-45ms) and a CPU jump about once a minute; 1 thread cut the stalls by ~80%. " +
                             $"Set \"ReduceRustNavThreads\": true in config/LootSpawnStagger.json to use {_config.RustNavThreadsAfterStartup} thread(s) after startup.");
            }

            Puts($"Population refills: {(Pacer.PopulationPatched ? $"{_config.PopulationMaxPerCall} per call, {_config.PopulationBudgetMs:0}ms per frame" + (_config.SpreadPopulationRefills ? $", spread over {Pacer.SpreadSeconds():0}s" : "") : "NOT paced")}; " +
                 $"group spawns: {(Pacer.GroupPatched ? $"{_config.GroupMaxPerCall} per call, max {_config.GroupSpawnBudgetMs:0}ms or {_config.MaxGroupSpawnCallsPerFrame} calls per frame" : "NOT paced")}; " +
                 $"junk pile lifetimes: {(_config.StaggerJunkPileLifetimes ? $"{_config.MinLifetimeMinutes:0}-{_config.MaxLifetimeMinutes:0} min" : "vanilla")}; " +
                 $"puzzle resets: {(_config.StaggerPuzzleResets ? $"{puzzleResetsStaggered} staggered" : "vanilla")}. " +
                 "Use lootstagger.status.");
        }

        private void Unload()
        {
            // Put RustNav back to what it was before we changed it
            _rustNavTimer?.Destroy();
            if (_originalRustNavThreads > 0)
                ConsoleSystem.Run(ConsoleSystem.Option.Server.Quiet(), "rustnav.setnumthreads", _originalRustNavThreads);

            // Anything still queued is left to vanilla: the next population/group tick refills it
            Pacer.Active = false;
            if (_host != null)
                UnityEngine.Object.Destroy(_host);
            _harmony?.UnpatchAll(HarmonyId);
            Pacer.Clear();
            _instance = null;
        }

        // prefixName / postfixName may be null when that side is not needed
        private bool TryPatch(Type type, string methodName, Type[] args, Type patchOwner, string prefixName, string postfixName)
        {
            var target = AccessTools.Method(type, methodName, args);
            if (target == null)
            {
                PrintWarning($"{type.Name}.{methodName} not found; that part of the pacing is disabled");
                return false;
            }

            try
            {
                _harmony.Patch(target,
                    prefix: prefixName == null ? null : new HarmonyMethod(patchOwner, prefixName),
                    postfix: postfixName == null ? null : new HarmonyMethod(patchOwner, postfixName));
                return true;
            }
            catch (Exception e)
            {
                PrintWarning($"Could not patch {type.Name}.{methodName}: {e.Message}");
                return false;
            }
        }

        #endregion

        #region Junk pile lifetimes

        // Fallback in case vanilla schedules TimeOut somewhere other than StartTimeout
        private void OnEntitySpawned(JunkPile pile)
        {
            if (!_config.StaggerJunkPileLifetimes) return;
            timer.Once(1f, () =>
            {
                if (pile == null || pile.IsDestroyed || pile.net == null) return;
                if (!_expiries.ContainsKey(pile.net.ID.Value))
                    Reschedule(pile, RandomLifetime());
            });
        }

        private void OnEntityKill(JunkPile pile)
        {
            if (pile?.net != null)
                _expiries.Remove(pile.net.ID.Value);
        }

        // Only the first StartTimeout per pile is replaced; vanilla's 30s re-arm while players are
        // nearby goes through TimeOut itself and is left alone
        private static void StartTimeoutPostfix(JunkPile __instance)
        {
            // Runs inside vanilla JunkPile.ServerInit: never throw into it; vanilla timing stays on error
            try
            {
                if (_instance == null || __instance == null || __instance.net == null) return;
                if (_instance._expiries.ContainsKey(__instance.net.ID.Value)) return;
                _instance.Reschedule(__instance, _instance.RandomLifetime());
            }
            catch (Exception e)
            {
                Pacer.ReportPatchError(nameof(StartTimeoutPostfix), e);
            }
        }

        private float RandomLifetime() =>
            UnityEngine.Random.Range(_config.MinLifetimeMinutes * 60f, _config.MaxLifetimeMinutes * 60f);

        private void Reschedule(JunkPile pile, float seconds)
        {
            if (pile == null || pile.IsDestroyed || pile.net == null) return;
            pile.CancelInvoke(pile.TimeOut);
            pile.Invoke(pile.TimeOut, seconds);
            _expiries[pile.net.ID.Value] = Time.realtimeSinceStartup + seconds;
        }

        #endregion

        #region RustNav threads

        private void ApplyRustNavThreads()
        {
            int current = ReadRustNavThreads();
            if (current == _config.RustNavThreadsAfterStartup) return;

            _originalRustNavThreads = current;
            ConsoleSystem.Run(ConsoleSystem.Option.Server.Quiet(), "rustnav.setnumthreads", _config.RustNavThreadsAfterStartup);
            Puts($"RustNav navmesh builder threads: {current} -> {ReadRustNavThreads()}");
        }

        // -1 if the convar is missing on this build
        private int ReadRustNavThreads()
        {
            string reply = ConsoleSystem.Run(ConsoleSystem.Option.Server.Quiet(), "rustnav.numthreads") ?? string.Empty;
            var match = Regex.Match(reply, @"\d+");
            return match.Success ? int.Parse(match.Value) : -1;
        }

        #endregion

        #region Puzzle reset timers

        private static PropertyInfo _canUseRadiationReset;

        // PuzzleReset.ResetTimer() zeroes a monument's reset clock. The game calls it for every monument
        // at startup and BetterNpc calls it for every monument when it loads, so they all reset in the
        // same few seconds every ~30 min (29 spike frames of 50-94ms seen at once). Start each clock at
        // a random point of its cycle instead; after that each monument keeps its own rhythm.
        private static void ResetTimerPostfix(PuzzleReset __instance)
        {
            // Runs inside vanilla PuzzleReset.ResetTimer: never throw into it
            try
            {
                if (_instance == null || !_instance._config.StaggerPuzzleResets) return;
                _instance.StaggerPuzzleReset(__instance);
            }
            catch (Exception e)
            {
                Pacer.ReportPatchError(nameof(ResetTimerPostfix), e);
            }
        }

        private bool StaggerPuzzleReset(PuzzleReset reset)
        {
            if (reset == null) return false;
            float spacing = reset.GetResetSpacing();
            if (float.IsInfinity(spacing) || float.IsNaN(spacing) || spacing <= 0f) return false;

            // Radiation-reset monuments only count down while a player is near; leave them alone
            if (_canUseRadiationReset != null && _canUseRadiationReset.GetValue(reset) is bool radiation && radiation) return false;

            reset.resetTimeElapsed = UnityEngine.Random.Range(0f, spacing);
            return true;
        }

        #endregion

        #region Commands

        [ConsoleCommand("lootstagger.status")]
        private void CmdStatus(ConsoleSystem.Arg arg)
        {
            if (arg.Connection != null && !arg.IsAdmin) return;

            var sb = new StringBuilder();
            sb.AppendLine(Pacer.Status());
            sb.AppendLine($"RustNav builder threads: {ReadRustNavThreads()} (ReduceRustNavThreads {(_config.ReduceRustNavThreads ? $"on, target {_config.RustNavThreadsAfterStartup}" : "off")})");

            if (_config.StaggerJunkPileLifetimes)
            {
                var live = BaseNetworkable.serverEntities.OfType<JunkPile>().Where(p => p.net != null).ToList();
                var liveIds = new HashSet<ulong>(live.Select(p => p.net.ID.Value));
                foreach (var stale in _expiries.Keys.Where(id => !liveIds.Contains(id)).ToList())
                    _expiries.Remove(stale);

                foreach (var group in live.GroupBy(PileKind).OrderBy(g => g.Key))
                    sb.AppendLine($"{group.Key}: {group.Count()} live, {group.Count(p => _expiries.ContainsKey(p.net.ID.Value))} staggered");

                // Remaining lifetimes in 5-minute buckets; a flat spread means evenly paced refills
                var buckets = new SortedDictionary<int, int>();
                int overdue = 0;
                float now = Time.realtimeSinceStartup;
                foreach (var expiry in _expiries.Values)
                {
                    float minutes = (expiry - now) / 60f;
                    if (minutes < 0f) { overdue++; continue; }
                    int bucket = (int)(minutes / 5f) * 5;
                    buckets[bucket] = buckets.TryGetValue(bucket, out var c) ? c + 1 : 1;
                }
                int scale = Math.Max(1, buckets.Values.DefaultIfEmpty(0).Max() / 40);
                sb.AppendLine("Remaining junk pile lifetimes:");
                foreach (var kv in buckets)
                    sb.AppendLine($"  {kv.Key,2}-{kv.Key + 5,2} min: {kv.Value,4} {new string('#', kv.Value / scale)}");
                if (overdue > 0)
                    sb.AppendLine($"  overdue: {overdue,4} (held by vanilla while players are nearby)");
            }

            if (_config.StaggerPuzzleResets)
            {
                // Minutes until each monument's next reset (ignores time paused while players are near)
                var resetBuckets = new SortedDictionary<int, int>();
                int monuments = 0;
                foreach (var reset in PuzzleReset.AllResets)
                {
                    if (reset == null) continue;
                    float spacing = reset.GetResetSpacing();
                    if (float.IsInfinity(spacing) || spacing <= 0f) continue;
                    monuments++;
                    int bucket = (int)(Mathf.Max(0f, spacing - reset.resetTimeElapsed) / 60f / 5f) * 5;
                    resetBuckets[bucket] = resetBuckets.TryGetValue(bucket, out var c) ? c + 1 : 1;
                }
                sb.AppendLine($"Puzzle resets ({monuments} monuments), next reset due in:");
                foreach (var kv in resetBuckets)
                    sb.AppendLine($"  {kv.Key,2}-{kv.Key + 5,2} min: {kv.Value,4} {new string('#', Math.Min(kv.Value, 60))}");
            }

            arg.ReplyWith(sb.ToString());
        }

        private string PileKind(JunkPile pile) =>
            pile is DiveSite ? "dive sites" : pile is JunkPileWater ? "water junk piles" : "land junk piles";

        #endregion

        #region Pacer

        private class PacerBehaviour : MonoBehaviour
        {
            private void Update() => Pacer.OnFrame();
        }

        private static class Pacer
        {
            public static bool Active;
            public static bool PopulationPatched;
            public static bool GroupPatched;

            private static PluginConfig _config;
            private static bool _draining;
            private static int _groupCallsThisFrame;
            private static double _groupMsThisFrame;
            private static readonly double TickToMs = 1000.0 / Stopwatch.Frequency;
            private static MethodInfo _groupSpawn;

            private class PendingFill
            {
                public SpawnHandler Handler;
                public SpawnDistribution Distribution;
                public int Remaining;
            }

            private struct PendingGroup
            {
                public SpawnGroup Group;
                public int Count;
            }

            // Populations are served round-robin so one big refill cannot starve the others
            private static readonly Dictionary<SpawnPopulationBase, PendingFill> PendingFills = new Dictionary<SpawnPopulationBase, PendingFill>();
            private static readonly List<SpawnPopulationBase> FillOrder = new List<SpawnPopulationBase>();
            private static readonly Queue<PendingGroup> PendingGroups = new Queue<PendingGroup>();

            private static long _popSpawnsDeferred, _chunksRun, _groupCallsDeferred, _groupCallsReplayed;
            private static int _peakPopQueue, _peakGroupQueue;

            // Spreading: queued population spawns are paced to finish by _spreadDeadline
            private static int _queuedPopSpawns;
            private static float _spreadDeadline, _nextPopCallAt;

            // Never longer than one population tick, so a tick's refills finish before the next one
            public static float SpreadSeconds() =>
                Mathf.Clamp(_config.PopulationSpreadSeconds, 0f, ConVar.Spawn.tick_populations * 0.9f);

            public static void Init(PluginConfig config)
            {
                _config = config;
                Clear();
            }

            public static void Clear()
            {
                PendingFills.Clear();
                FillOrder.Clear();
                PendingGroups.Clear();
                _draining = false;
                _queuedPopSpawns = 0;
                _spreadDeadline = _nextPopCallAt = 0f;
            }

            public static bool ResolveGroupSpawn()
            {
                _groupSpawn = AccessTools.Method(typeof(SpawnGroup), "Spawn", new[] { typeof(int) });
                return _groupSpawn != null;
            }

            // SpawnPopulationBase.Fill: spawn only the first chunk now, queue the rest
            // Patches run inside vanilla spawning, so they must never throw: on any error the call goes ahead
            // unchanged (vanilla behaviour) and the error is logged once
            public static void FillPrefix(SpawnPopulationBase __instance, SpawnHandler spawnHandler, SpawnDistribution distribution, ref int numToFill, bool initialSpawn)
            {
                try
                {
                    FillPrefixInner(__instance, spawnHandler, distribution, ref numToFill, initialSpawn);
                }
                catch (Exception e)
                {
                    ReportPatchError(nameof(FillPrefix), e);
                }
            }

            private static void FillPrefixInner(SpawnPopulationBase __instance, SpawnHandler spawnHandler, SpawnDistribution distribution, ref int numToFill, bool initialSpawn)
            {
                if (!Active || _draining || initialSpawn || numToFill <= _config.PopulationMaxPerCall) return;

                if (!PendingFills.TryGetValue(__instance, out var pending))
                {
                    pending = new PendingFill();
                    PendingFills[__instance] = pending;
                    FillOrder.Add(__instance);
                }

                // The latest vanilla tick has the freshest deficit, so it replaces any older remainder
                int previous = pending.Remaining;
                pending.Handler = spawnHandler;
                pending.Distribution = distribution;
                pending.Remaining = numToFill - _config.PopulationMaxPerCall;
                _queuedPopSpawns = Math.Max(0, _queuedPopSpawns + pending.Remaining - previous);
                _popSpawnsDeferred += pending.Remaining;
                _peakPopQueue = Math.Max(_peakPopQueue, _queuedPopSpawns);

                if (_config.SpreadPopulationRefills)
                    _spreadDeadline = Mathf.Max(_spreadDeadline, Time.realtimeSinceStartup + SpreadSeconds());

                numToFill = _config.PopulationMaxPerCall;
            }

            // SpawnGroup.Spawn(int): once this frame has spent its group budget (time or calls), defer
            // the call to a later frame. The first call each frame always runs, so nothing can stall.
            public static bool GroupSpawnPrefix(SpawnGroup __instance, ref int numToSpawn, out long __state)
            {
                __state = 0;
                try
                {
                    return GroupSpawnPrefixInner(__instance, ref numToSpawn, ref __state);
                }
                catch (Exception e)
                {
                    ReportPatchError(nameof(GroupSpawnPrefix), e);
                    return true;
                }
            }

            private static bool GroupSpawnPrefixInner(SpawnGroup __instance, ref int numToSpawn, ref long __state)
            {
                if (!Active) return true;
                if (_draining)
                {
                    __state = Stopwatch.GetTimestamp();
                    return true;
                }

                bool overBudget = _groupCallsThisFrame > 0 &&
                                  (_groupMsThisFrame >= _config.GroupSpawnBudgetMs || _groupCallsThisFrame >= _config.MaxGroupSpawnCallsPerFrame);
                if (!overBudget)
                {
                    // Spawn a few now and queue the rest; vanilla Spawn(n) re-checks free space on each
                    // call, so splitting only changes timing
                    if (numToSpawn > _config.GroupMaxPerCall)
                    {
                        EnqueueGroup(__instance, numToSpawn - _config.GroupMaxPerCall);
                        numToSpawn = _config.GroupMaxPerCall;
                    }
                    _groupCallsThisFrame++;
                    __state = Stopwatch.GetTimestamp();
                    return true;
                }

                EnqueueGroup(__instance, numToSpawn);
                return false;
            }

            // Queued as single-entity calls, so each replay is as small as possible
            private static void EnqueueGroup(SpawnGroup group, int count)
            {
                for (int i = 0; i < count; i++)
                    PendingGroups.Enqueue(new PendingGroup { Group = group, Count = 1 });
                _groupCallsDeferred += count;
                _peakGroupQueue = Math.Max(_peakGroupQueue, PendingGroups.Count);
            }

            // Adds the time each group spawn actually took to this frame's budget
            public static void GroupSpawnPostfix(long __state)
            {
                if (__state != 0)
                    _groupMsThisFrame += (Stopwatch.GetTimestamp() - __state) * TickToMs;
            }

            private static readonly HashSet<string> ReportedErrors = new HashSet<string>();

            public static void ReportPatchError(string where, Exception e)
            {
                try
                {
                    if (ReportedErrors.Add(where))
                        _instance?.PrintError($"{where} failed; vanilla behaviour used instead (reported once): {e}");
                }
                catch
                {
                    // Never let error reporting itself throw into game code
                }
            }

            // Runs in Update, before this frame's spawn coroutines
            public static void OnFrame()
            {
                _groupCallsThisFrame = 0;
                _groupMsThisFrame = 0;
                if (!Active) return;

                try
                {
                    var sw = Stopwatch.StartNew();

                    // Group contents first: their parents already exist and look empty until filled.
                    // Replays count against the same group budget as live calls.
                    while (PendingGroups.Count > 0 && _groupCallsThisFrame < _config.MaxGroupSpawnCallsPerFrame &&
                           _groupMsThisFrame < _config.GroupSpawnBudgetMs &&
                           sw.Elapsed.TotalMilliseconds < _config.FrameBudgetMs)
                    {
                        var pending = PendingGroups.Dequeue();
                        if (pending.Group == null) continue;
                        _groupCallsThisFrame++;
                        Replay(() => _groupSpawn.Invoke(pending.Group, new object[] { pending.Count }));
                        _groupCallsReplayed++;
                    }

                    // Population spawns: one call at a time until this frame's population budget is used.
                    // When spreading, calls are also paced so the queue finishes by the deadline instead
                    // of as fast as possible (each spawn triggers navmesh rebuilds on RustNav's threads).
                    var popTimer = Stopwatch.StartNew();
                    float now = Time.realtimeSinceStartup;
                    int calls = 0;
                    while (FillOrder.Count > 0 && calls < 100 &&
                           (calls == 0 || popTimer.Elapsed.TotalMilliseconds < _config.PopulationBudgetMs))
                    {
                        if (_config.SpreadPopulationRefills)
                        {
                            if (now < _nextPopCallAt) break;
                            // Spread what is left evenly over the time left; if behind, catch up
                            float interval = _queuedPopSpawns > 0 ? Mathf.Max(0f, _spreadDeadline - now) / _queuedPopSpawns : 0f;
                            _nextPopCallAt = Mathf.Max(_nextPopCallAt, now - 1f) + interval;
                        }
                        RunNextChunk();
                        calls++;
                    }
                }
                catch (Exception e)
                {
                    _instance?.PrintError("Pacing error, clearing queues: " + e);
                    Clear();
                }
            }

            private static void RunNextChunk()
            {
                var population = FillOrder[0];
                FillOrder.RemoveAt(0);
                if (!PendingFills.TryGetValue(population, out var pending)) return;

                // Re-check the real deficit: other spawns may have filled it meanwhile
                int deficit = population == null || pending.Distribution == null
                    ? 0
                    : population.GetTargetCount(pending.Distribution) - pending.Distribution.Count;
                int count = Math.Min(Math.Min(pending.Remaining, _config.PopulationMaxPerCall), deficit);

                if (count > 0)
                {
                    Replay(() => population.Fill(pending.Handler, pending.Distribution, count, false));
                    pending.Remaining -= count;
                    _queuedPopSpawns = Math.Max(0, _queuedPopSpawns - count);
                    _chunksRun++;
                }

                if (count <= 0 || pending.Remaining <= 0)
                {
                    _queuedPopSpawns = Math.Max(0, _queuedPopSpawns - Math.Max(0, pending.Remaining));
                    PendingFills.Remove(population);
                }
                else
                    FillOrder.Add(population);
            }

            private static void Replay(Action action)
            {
                _draining = true;
                try
                {
                    action();
                }
                catch (Exception e)
                {
                    _instance?.PrintWarning("Deferred spawn failed: " + (e.InnerException ?? e).Message);
                }
                finally
                {
                    _draining = false;
                }
            }

            public static string Status()
            {
                var sb = new StringBuilder();
                sb.AppendLine($"LootSpawnStagger {(Active ? "active" : "inactive")}: population refills {(PopulationPatched ? "paced" : "NOT paced")}, group spawns {(GroupPatched ? "paced" : "NOT paced")}");
                sb.AppendLine($"Population: {_popSpawnsDeferred:n0} spawns deferred, {_chunksRun:n0} chunks run, queued now {_queuedPopSpawns} across {PendingFills.Count} population(s), peak {_peakPopQueue}; " +
                              (_config.SpreadPopulationRefills ? $"spread over {SpreadSeconds():0}s" : "not spread"));
                sb.Append($"Groups: {_groupCallsDeferred:n0} calls deferred, {_groupCallsReplayed:n0} replayed, queued now {PendingGroups.Count}, peak {_peakGroupQueue}");
                if (PendingFills.Count > 0)
                    sb.Append("\n  pending: " + string.Join(", ", PendingFills.Select(kv => $"{(kv.Key == null ? "?" : kv.Key.name)} {kv.Value.Remaining}")));
                return sb.ToString();
            }
        }

        #endregion
    }
}
