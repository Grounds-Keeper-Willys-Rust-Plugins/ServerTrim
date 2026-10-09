# ServerTrim

A small Carbon/Oxide plugin that stops a Rust server doing work every frame that nothing uses.

A dedicated server runs the same Unity scene as the game, minus the graphics. Some of what Unity
keeps running only matters on a player's computer, or only matters for a navigation system the
server is not using. ServerTrim switches those pieces off. It changes nothing players can see or
touch, and everything it switches off is switched back on when the plugin unloads.

On Chernobyl (a 4500 map with about 135,000 entities) the three trims together took the server
from about 113 to about 206 frames per second.

## What it trims

### 1. Navigation obstacles nobody navigates around

**What they are.** Loot barrels, crates, oil barrels, tunnel loot strips, shipping containers
and similar objects carry a Unity `NavMeshObstacle` component. Its job is to cut a hole in Unity's
navigation mesh so that NPCs walk around the object instead of through it.

**Why they cost frames for nothing.** A server started with `-useNewNavmesh` uses Facepunch's own
navigation mesh ("Gen2") instead of Unity's, and never builds the Unity one. The obstacles are
still there, and Unity still visits every enabled one on every frame. On Chernobyl that was about
10,500 obstacles costing 207 ms of every second, roughly 1.7 ms of each frame.

**What ServerTrim does.** It disables every obstacle component when it loads, and the ones on
anything spawned later. The objects themselves stay: their colliders are untouched, so players,
bullets and physics behave as before. It only does this when the server runs the Gen2 navigation
mesh. On a server using Unity's navigation the obstacles are doing real work, so it leaves them alone.

**Result on Chernobyl:** 113 to 161 frames per second.

### 2. Animation constraints on horses

**What they are.** Each ridable horse has four `RotationConstraint` components, one on each hoof-pad
bone. They keep the hooves angled correctly while the horse animates.

**Why they cost frames for nothing.** That is a visual detail. Every player's game runs its own copy
of the horse's skeleton to draw it; the server's copy is never seen. The hoof-pad bones carry no
collider, and no server code reads the constraints. Unity still updated all of them every frame:
180 constraints on 45 horses cost 112 to 158 ms per second.

**What ServerTrim does.** It disables those constraints on every horse, now and as horses spawn.
The list is a config setting, so other components can be added (see Config).

**Result on Chernobyl:** 162 to 206 frames per second.

### 3. Locked crates that never "land"

**What happens.** When a locked crate is dropped (by the Chinook, or by a plugin), the game starts a
check that fires a ray 1 metre straight down every 15 milliseconds. When the ray hits ground it plays
a puff of dust, marks the crate as landed and stops checking.

**The bug.** A crate that comes to rest where that ray hits nothing (on an object the ray ignores,
in water, or slightly more than 1 metre above what it rests on) never counts as landed. Its check
keeps running for as long as the crate exists, and starts again after every server restart. On
Chernobyl about four crates were stuck like this, costing about 8 ms per second.

**What ServerTrim does.** It gives every dropped crate 10 seconds to land the normal way, then stops
its check if it is still running. The landed flag only controls the dust effect, so the only thing a
stuck crate loses is a puff of dust it was never going to get. Hacking, looting and the crate's timer
are not affected.

**Tested** by dropping a crate and holding it 5 metres in the air, where the ray can never reach
ground: its check was still running after 5 seconds and had stopped by 13.

## Commands

| Command | What it shows |
|---|---|
| `servertrim.stats` | What each trim switched off at load and since, how many prefabs are remembered, and how many crate checks were stopped. |

## Config

`ServerTrim.json` in the config folder:

| Setting | Default | What it does |
|---|---|---|
| Disable Unity NavMeshObstacle components when the server runs the Gen2 navmesh | `true` | Trim 1. Ignored on a server using Unity's navigation. |
| Stop a dropped locked crate's landing check after this many seconds | `10` | Trim 3. `0` turns it off. |
| Trims: Unity component type -> prefab short-name substrings | `RotationConstraint` on `ridablehorse` | Trim 2, and any others you add. The key is a full Unity component type name; the list holds parts of prefab names it applies to. An empty list means every prefab. |

To add a trim, find the cost first with PerfProbe: `perfprobe.loop 30` shows which Unity system is
busy, and `perfprobe.components <Type>` shows which prefabs carry that component and whether the
object it sits on has a collider. Only switch off components the server provably never reads.

## Install

Copy `ServerTrim.cs` into the server's `plugins` folder (`carbon/plugins` or `oxide/plugins`). It
loads straight away and prints what it switched off.

## Overhead and safety

- At load it searches the scene once per trim (1 to 40 ms on a big map). After that, each spawned
  entity costs one dictionary lookup, because ServerTrim remembers which prefabs carry a trimmed
  component.
- Unloading restores every component it switched off. Stopped crate checks are not restarted, since
  they only ever play a dust effect; the game re-arms them itself after a restart.
- Nothing is changed on disk or in the save.

## Versions

- **0.2.0**: navigation obstacles (moved here from NavCompat 0.2.0) and the locked-crate landing check.
- **0.1.0**: configurable component trims, defaulting to the horse hoof-pad constraints.
