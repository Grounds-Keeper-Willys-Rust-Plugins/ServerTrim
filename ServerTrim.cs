using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.AI;

namespace Oxide.Plugins
{
    [Info("ServerTrim", "Lucien", "0.2.0")]
    [Description("Stops work the dedicated server does every frame for nothing: Unity navmesh obstacles on a Gen2-navmesh server, animation constraints on horses, and the landing check of locked crates that never land")]
    public class ServerTrim : RustPlugin
    {
        // Each trim was found with PerfProbe 0.4.x (perfprobe.loop / perfprobe.components / perfprobe.behaviours) on a
        // 4500 map with 135,000 entities. Everything is undone on unload. See README.ServerTrim.md.
        //
        //  1. Unity NavMeshObstacle components (loot barrels, crates, tunnel loot strips, shipping containers...) cut
        //     holes in the Unity navmesh so AI can walk around them. A server started with -useNewNavmesh never builds
        //     that navmesh, yet Unity visits every enabled obstacle each frame: 207 ms/s for 10,500 of them. Only acted
        //     on when the Unity navmesh is off; on a Unity-navmesh server the obstacles are doing real work.
        //  2. Unity animation constraints (component type -> prefab substrings, configurable). Default: the
        //     RotationConstraint on each ridable horse's four hoof-pad bones, a visual rig the client runs for itself:
        //     112-158 ms/s for 45 horses. The pads carry no collider and no server code reads them.
        //  3. HackableLockedCrate.LandCheck: a dropped locked crate raycasts 1 m down every 15 ms until the ray hits
        //     ground, then plays a dust effect and stops. A crate resting where the ray misses checks forever, and
        //     again after every restart (PostServerLoad re-arms it). The landed flag only gates the dust effect, so the
        //     check is stopped after a few seconds: ~8 ms/s for 4 such crates.
        private class Cfg
        {
            [JsonProperty("Disable Unity NavMeshObstacle components when the server runs the Gen2 navmesh (-useNewNavmesh); ignored on a Unity-navmesh server")]
            public bool NavMeshObstacles = true;

            [JsonProperty("Stop a dropped locked crate's landing check after this many seconds (0 = never; the check only triggers a dust effect)")]
            public float CrateLandCheckSeconds = 10f;

            [JsonProperty("Trims: Unity component type -> prefab short-name substrings it is switched off on (an empty list means every prefab)")]
            public Dictionary<string, List<string>> Trims = new Dictionary<string, List<string>>
            {
                { "UnityEngine.Animations.RotationConstraint", new List<string> { "ridablehorse" } },
            };
        }
        private Cfg _cfg;
        protected override void LoadDefaultConfig() { _cfg = new Cfg(); SaveConfig(); }
        protected override void LoadConfig()
        {
            base.LoadConfig();
            try { _cfg = Config.ReadObject<Cfg>(); } catch { _cfg = null; }
            if (_cfg == null) _cfg = new Cfg();
            if (_cfg.Trims == null) _cfg.Trims = new Dictionary<string, List<string>>();
            SaveConfig();
        }
        protected override void SaveConfig() => Config.WriteObject(_cfg, true);

        // A component trim: switch off every enabled Behaviour of Type on prefabs matching one of Prefabs
        private class Trim { public string Label; public Type Type; public List<string> Prefabs; public int AtLoad, AtSpawn; }
        private readonly List<Trim> _trims = new List<Trim>();
        private readonly List<Behaviour> _off = new List<Behaviour>();
        private readonly Dictionary<uint, Trim[]> _prefabTrims = new Dictionary<uint, Trim[]>();   // per prefab: which trims apply
        private bool _active;

        // Crate landing checks
        private int _cratesSeen, _crateChecksStopped;
        private readonly HashSet<HackableLockedCrate> _cratesPending = new HashSet<HackableLockedCrate>();

        private static Type FindType(string name)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type t = null;
                try { t = asm.GetType(name, false); } catch { }
                if (t != null) return t;
            }
            return null;
        }

        private void OnServerInitialized()
        {
            if (_cfg.NavMeshObstacles)
            {
                if (ConVar.AI.useUnityNavmesh)
                    Puts("NavMeshObstacles: this server runs the Unity navmesh, which uses the obstacles; left alone");
                else
                    _trims.Add(new Trim { Label = "NavMeshObstacle (Gen2 navmesh server)", Type = typeof(NavMeshObstacle), Prefabs = new List<string>() });
            }
            foreach (var kv in _cfg.Trims)
            {
                var t = FindType(kv.Key);
                if (t == null || !typeof(Behaviour).IsAssignableFrom(t)) { PrintWarning($"{kv.Key}: not a Unity Behaviour type on this build, skipped"); continue; }
                _trims.Add(new Trim { Label = t.Name, Type = t, Prefabs = kv.Value ?? new List<string>() });
            }

            var sw = System.Diagnostics.Stopwatch.StartNew();
            foreach (var trim in _trims)
            {
                var all = UnityEngine.Object.FindObjectsByType(trim.Type, FindObjectsInactive.Include, FindObjectsSortMode.None);
                foreach (var o in all)
                {
                    var b = o as Behaviour;
                    if (b == null || !b.enabled || !Matches(trim, b.GetComponentInParent<BaseEntity>())) continue;
                    b.enabled = false; _off.Add(b); trim.AtLoad++;
                }
            }
            _active = true;
            if (_trims.Count > 0)
                Puts($"trimmed at load in {sw.Elapsed.TotalMilliseconds:F0} ms: " + string.Join("; ", _trims.Select(t => $"{t.Label} x{t.AtLoad}{(t.Prefabs.Count > 0 ? $" on [{string.Join(",", t.Prefabs)}]" : string.Empty)}")));

            if (_cfg.CrateLandCheckSeconds > 0f)
            {
                // crates loaded from the save had their check re-armed by the game before plugins load
                int n = 0;
                foreach (var net in BaseNetworkable.serverEntities)
                {
                    var crate = net as HackableLockedCrate;
                    if (crate != null) { WatchCrate(crate); n++; }
                }
                Puts($"locked crates: {n} found at load, landing checks stop after {_cfg.CrateLandCheckSeconds:0} s");
            }
        }

        private static bool Matches(Trim trim, BaseEntity owner)
        {
            if (trim.Prefabs.Count == 0) return true;
            if (owner == null) return false;
            string name = owner.ShortPrefabName ?? string.Empty;
            foreach (var p in trim.Prefabs) if (name.IndexOf(p, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        private void OnEntitySpawned(BaseNetworkable e)
        {
            if (!_active || e == null) return;
            var ent = e as BaseEntity; if (ent == null) return;

            var crate = ent as HackableLockedCrate;
            if (crate != null && _cfg.CrateLandCheckSeconds > 0f) WatchCrate(crate);

            if (_trims.Count == 0) return;
            Trim[] applies;
            if (!_prefabTrims.TryGetValue(e.prefabID, out applies))
            {
                var found = new List<Trim>();
                foreach (var t in _trims)
                    if (Matches(t, ent) && ent.GetComponentsInChildren(t.Type, true).Length > 0) found.Add(t);
                applies = found.ToArray();
                _prefabTrims[e.prefabID] = applies;   // memo: most spawns then cost one lookup
            }
            if (applies.Length == 0) return;
            foreach (var trim in applies)
            {
                var comps = ent.GetComponentsInChildren(trim.Type, true);
                foreach (var c in comps) { var b = c as Behaviour; if (b != null && b.enabled) { b.enabled = false; _off.Add(b); trim.AtSpawn++; } }
            }
            if (_off.Count > 50000) _off.RemoveAll(b => b == null);   // drop references to despawned ones now and then
        }

        // Gives the crate CrateLandCheckSeconds to land the normal way, then stops the check if it is still running
        private void WatchCrate(HackableLockedCrate crate)
        {
            if (!_cratesPending.Add(crate)) return;
            _cratesSeen++;
            timer.Once(_cfg.CrateLandCheckSeconds, () =>
            {
                _cratesPending.Remove(crate);
                if (crate == null || crate.IsDestroyed) return;
                if (crate.IsInvoking(crate.LandCheck)) { crate.CancelInvoke(crate.LandCheck); _crateChecksStopped++; }
            });
        }

        private void Unload()
        {
            int n = 0;
            foreach (var b in _off) if (b != null) { b.enabled = true; n++; }
            _off.Clear(); _active = false;
            if (n > 0) Puts($"restored {n} components");
            // stopped crate checks are not restarted: they only ever play a dust effect, and the game re-arms them after a restart
        }

        [ConsoleCommand("servertrim.stats")]
        private void CmdStats(ConsoleSystem.Arg arg)
        {
            if (!(arg.IsRcon || arg.IsAdmin || (arg.Player()?.IsAdmin ?? false))) return;
            arg.ReplyWith($"ServerTrim {(_active ? "active" : "inactive")}: " +
                          (_trims.Count > 0 ? string.Join("; ", _trims.Select(t => $"{t.Label}{(t.Prefabs.Count > 0 ? $" on [{string.Join(",", t.Prefabs)}]" : string.Empty)}: {t.AtLoad} at load + {t.AtSpawn} at spawn")) : "no component trims") +
                          $"; {_off.Count} components held, {_prefabTrims.Count} prefabs memoised; locked crates watched {_cratesSeen}, landing checks stopped {_crateChecksStopped}" +
                          (_cfg.CrateLandCheckSeconds > 0f ? $" (after {_cfg.CrateLandCheckSeconds:0} s)" : " (off)"));
        }
    }
}
