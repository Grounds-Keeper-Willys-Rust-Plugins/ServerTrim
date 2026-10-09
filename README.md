# ServerTrim

Two small Carbon/Oxide plugins that make a Rust server lag less without changing what players see.
One switches off work the server does every frame for nothing; the other spreads out work the
server has to do but crams into single frames.

| Plugin | What it does | Details |
|---|---|---|
| **ServerTrim** | Switches off per-frame work nothing uses: Unity navigation obstacles on servers running the Gen2 navmesh (`-useNewNavmesh`), animation constraints on horse hooves, and the landing check of locked crates that never land. On Chernobyl it took the server from about 113 to about 206 frames per second. | [README.ServerTrim.md](README.ServerTrim.md) |
| **LootSpawnStagger** | Paces vanilla respawning so it never lands in one frame: splits population refills and spawn-group calls into small pieces with a per-frame time budget, gives junk piles and dive sites random lifetimes, and starts monument puzzle reset clocks at random points. Optionally limits the navmesh builder's threads. | [README.LootSpawnStagger.md](README.LootSpawnStagger.md) |

They are independent: install either or both.

## Finding what to trim: PerfProbe

Both plugins came out of measuring a live server with
[PerfProbe](https://github.com/Grounds-Keeper-Willys-Rust-Plugins/PerfProbe), a separate plugin
that shows where a server's frame time goes. It records what was running in each slow frame
(game methods, plugin hooks, timers, garbage collection, entity spawns and kills), keeps a
per-second timeline, and can time Unity's own systems, every script `Update`, and the components
behind a busy engine system.

Use it before and after installing these plugins to see what they change on your own map, and to
find the next thing worth trimming: `perfprobe.loop` shows which Unity system is busy, and
`perfprobe.components <Type>` shows which prefabs carry a component.

## Install

Copy the `.cs` file into the server's `plugins` folder (`carbon/plugins` or `oxide/plugins`). Each
plugin loads straight away and prints what it changed. Each
README lists the plugin's commands, config, and what unloading puts back.

## License

GPL-3.0. See [LICENSE](LICENSE).
