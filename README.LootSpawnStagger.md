# LootSpawnStagger

A small Carbon/Oxide plugin that stops a Rust server doing all of its respawning in the same frame.

The game refills the map in bursts. Every minute it tops up loot, ores, junk piles and dive sites,
spawning everything that is missing in one go. Junk piles that spawned together expire together,
and monument puzzles that started their clocks together reset together. Each burst lands in one or
a few frames, and players feel those frames as lag spikes.

LootSpawnStagger does not change how much spawns, or where. It changes when: the same work is
spread over many frames, and timers that were all started at the same moment are started at
random points instead. Everything spawns as it would have; it just does not all happen at once.

## What it paces

### 1. Population refills

**What happens.** Every `spawn.tick_populations` seconds (60 by default) the game checks each
population (loot barrels, ores, collectables, junk piles, dive sites and so on) and spawns
everything that is missing in a single call. One junk pile costs about 8 ms to spawn, so a refill
of a few dozen is a frame of a hundred milliseconds or more. Each spawn also makes the navigation
mesh rebuild about 15 tiles around the new object on background threads; on a big map, about 900
tiles rebuilt at once every minute pushed server CPU from about 40% to about 55%.

**What LootSpawnStagger does.** It lets each refill spawn one entity straight away and queues the
rest. The queue is worked off a call at a time, with at most 4 ms of spawning per frame, spread
evenly over the next 45 seconds, so one minute's refills finish before the next minute's begin.
Before each queued spawn it checks the population is still short, so nothing is ever over-filled.
The map's first fill at server startup is not paced.

### 2. Spawn groups

**What happens.** Spawn groups fill monuments, junk piles and deep-sea sites with their contents,
and tunnels and labs with their NPCs. A single call can ask for several entities at once; one call
for a whole tunnel group (several NPCs with weapons) took 10 to 25 ms.

**What LootSpawnStagger does.** It splits each call into single spawns and caps the work per frame
at 8 ms or 8 calls, whichever comes first. Anything over that is moved to the following frames. The
first group call in a frame always runs, so nothing can get stuck in the queue.

### 3. Junk pile and dive site lifetimes

**What happens.** A junk pile or dive site lives 30 minutes, give or take 30 seconds. Everything
spawned in the same refill therefore disappears in the same few seconds, and the refill that
replaces it is another burst.

**What LootSpawnStagger does.** It gives each new junk pile and dive site a random lifetime between
20 and 40 minutes. When the plugin loads it also spreads the ones already on the map over the next
1 to 40 minutes. The game's own rule that keeps a pile alive while players are near it is left alone.

### 4. Monument puzzle resets

**What happens.** Tunnels, underwater labs and puzzle monuments clear and refill their spawn groups
about every 30 minutes. All of their clocks are started together when the server starts (and again
when BetterNpc loads), so every monument resets in the same few seconds. One such moment produced
29 spike frames of 50 to 94 ms.

**What LootSpawnStagger does.** It starts each monument's clock at a random point in its cycle, at
load and whenever the game restarts a clock. After that each monument keeps its own rhythm.
Monuments that only count down while a player is nearby (radiation resets) are left alone.

### 5. Navmesh builder threads (optional, off by default)

**What happens.** On a server started with `-useNewNavmesh`, Facepunch's navigation system
(RustNav) rebuilds navmesh tiles on 4 background threads. On a busy map the rebuild bursts after
each population tick crowded out Unity's own worker threads and caused 26 to 45 ms stalls on the
main thread about once a minute. Running the builder on 1 thread cut those stalls by about 80%.

**What LootSpawnStagger does.** Nothing, unless you turn it on: by default it only prints a warning
at startup saying how many threads the builder is using. With `ReduceRustNavThreads` on, it sets
the builder to `RustNavThreadsAfterStartup` threads once `RustNavThreadsDelaySeconds` have passed
since the server started, so the full navmesh bake at boot still gets every thread. Fewer threads
means new tiles take longer to appear, which is why it is a choice and not a default.

## What it looks like on a real server

`lootstagger.status` on Chernobyl (a 4500 map with about 138,000 entities) after 27 hours:

```
LootSpawnStagger active: population refills paced, group spawns paced
Population: 521,985 spawns deferred, 521,982 chunks run, queued now 3 across 1 population(s), peak 333; spread over 45s
Groups: 279,294 calls deferred, 278,802 replayed, queued now 0, peak 6789
dive sites: 127 live, 127 staggered
land junk piles: 787 live, 787 staggered
water junk piles: 123 live, 123 staggered
Remaining junk pile lifetimes:
   0- 5 min:  183 #############################################
   5-10 min:  185 ##############################################
  10-15 min:  167 #########################################
  15-20 min:  157 #######################################
  20-25 min:  149 #####################################
  25-30 min:   96 ########################
  30-35 min:   59 ##############
  35-40 min:   23 #####
  overdue:   18 (held by vanilla while players are nearby)
Puzzle resets (374 monuments), next reset due in:
   0- 5 min:   68 ############################################################
   5-10 min:   50 ##################################################
  10-15 min:   67 ############################################################
  15-20 min:   66 ############################################################
  20-25 min:   47 ###############################################
  25-30 min:   58 ##########################################################
  30-35 min:   17 #################
```

Without the plugin, the monument resets would sit in one bar, and the junk piles in a few tall bars,
one for each refill that spawned them.

## Commands

| Command | What it shows |
|---|---|
| `lootstagger.status` | Whether each part is active, how much has been deferred and replayed, what is queued now, the RustNav thread count, and histograms of when junk piles expire and monuments reset. |

## Config

`LootSpawnStagger.json` in the config folder. The defaults are the values used on Chernobyl.

| Setting | Default | What it does |
|---|---|---|
| `PopulationMaxPerCall` | `1` | Entities a population refill spawns per call; the rest are queued. |
| `PopulationBudgetMs` | `4` | Time per frame spent on queued population spawns. |
| `SpreadPopulationRefills` | `true` | Spread each refill evenly over `PopulationSpreadSeconds` instead of as fast as the budget allows. |
| `PopulationSpreadSeconds` | `45` | How long a refill is spread over. Always kept below `spawn.tick_populations`. |
| `GroupSpawnBudgetMs` | `8` | Time per frame spent on spawn-group calls before the rest move to later frames. |
| `MaxGroupSpawnCallsPerFrame` | `8` | Spawn-group calls per frame before the rest move to later frames. |
| `GroupMaxPerCall` | `1` | Entities a spawn-group call spawns at once; the rest are queued. |
| `FrameBudgetMs` | `6` | Upper limit on time spent working off queued spawn-group calls in one frame. |
| `StaggerJunkPileLifetimes` | `true` | Part 3 on or off. |
| `MinLifetimeMinutes` / `MaxLifetimeMinutes` | `20` / `40` | Range of random junk pile and dive site lifetimes. |
| `SpreadExistingOnLoad` | `true` | Also spread the junk piles already on the map when the plugin loads. |
| `StaggerPuzzleResets` | `true` | Part 4 on or off. |
| `ReduceRustNavThreads` | `false` | Part 5 on or off. |
| `RustNavThreadsAfterStartup` | `1` | Builder threads to use once part 5 applies. |
| `RustNavThreadsDelaySeconds` | `300` | Seconds after server start before part 5 applies. |

## Install

Copy `LootSpawnStagger.cs` into the server's `plugins` folder (`carbon/plugins` or `oxide/plugins`).
It loads straight away and prints one line summarising what it paces. It uses Harmony, which both
Carbon and Oxide include. Tested on Carbon (Chernobyl, Rust build 2634) and Oxide 2.0.7801.

## Overhead and safety

- Its own work is a queue check once per frame. The time it spends in a frame is the spawning the
  game would have done anyway, capped by the budgets above.
- It hooks four game methods. If any of them is missing on a future Rust build, that part is
  switched off with a warning and the rest keeps working. If anything inside a hook goes wrong, the
  game's normal behaviour is used for that call and the error is logged once.
- Unloading removes its hooks and puts the RustNav thread count back. Spawns still queued are left
  to the game's next refill. Junk piles and monuments keep the timings it gave them until they next
  expire or reset.
- Nothing is changed on disk or in the save.

## Versions

- **0.3.1**: current version.
