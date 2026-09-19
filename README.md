# ProfLog

A read-only measurement mod for the 7 Days to Die dedicated server. It wraps the engine's hot call
sites with Harmony, and every N seconds samples the counters, the world state, per-thread process
CPU, the memory pools and Unity's native objects into TSV files.

> Server-side Harmony mod · 7 Days to Die dedicated server 3.x · no client download · read-only

## What it does

* times every call site in its patch table — 50 methods in this build — plus an outermost wrapper
  on `GameManager.Update` and a lock-wait prefix on `RegionFileManager.CullExpiredChunks`;
* records per-thread CPU from `/proc/self/task`, so "the main thread is busy" and "the main thread
  is asleep" stop being the same number;
* records world state: players, zombies, chunks, save directory size, pending resets, GC counters,
  heap and RSS;
* records what the `MemoryPools` free lists retain, per element type and per size class;
* censuses live `UnityEngine.Object` counts and their native memory per type, plus the loaded
  `AssetBundle`s, which is where a dedicated server's native memory goes and never comes back;
* writes summaries into the server log and into its own file.

It changes nothing. There are no switches that alter game behaviour, no cleanup, no eviction — the
only thing it can do to the server is cost frame time, and the one measurement that perturbs
anything (`lockprobe`) can be turned off while running.

## Requirements

| | |
|---|---|
| game | 7 Days to Die dedicated server **3.x** |
| dependency | `0_TFP_Harmony` (ships with the server) |
| clients | nothing to download, server-side only |
| config file | none — everything is set from the console |

Branches of this repository:

| branch | game version |
|---|---|
| [`3.x`](https://github.com/kotfoxtrot/7d2d_ProfLog/tree/3.x) | 3.0, 3.1, 3.2 |
| [`2.6`](https://github.com/kotfoxtrot/7d2d_ProfLog/tree/2.6) | 2.6 |

This mod is standalone: it does not need, and is not needed by, anything else. See
[Related mods](#related-mods) for what it is usually run next to.

## Install

### From a release

1. Download the archive for your game version from
   [Releases](https://github.com/kotfoxtrot/7d2d_ProfLog/releases).
2. Unpack it into `<server>/Mods/` so that you end up with `<server>/Mods/1_ProfLog/` containing
   `ProfLog.dll` and `ModInfo.xml`.
3. Restart the server.

### From source

```bash
git clone -b 3.x https://github.com/kotfoxtrot/7d2d_ProfLog.git
cd 7d2d_ProfLog
dotnet build -c Release -p:GameRoot=/path/to/server
```

`GameRoot` is the dedicated server root — the folder holding `7DaysToDieServer_Data/Managed` and
`Mods/0_TFP_Harmony`. Omit `-p:GameRoot` and the path baked into the `.csproj` is used. The build
references the game assemblies in place and never copies them.

Copy `bin/ProfLog.dll` and `ModInfo.xml` into `<server>/Mods/1_ProfLog/`.

### Folder name

The folder must start with `1_`. Mods are loaded in alphabetical order: `0_TFP_Harmony` provides
Harmony and has to come first. For a profiler the position also decides what it can see — the
`UpdateOuter` probe is only outermost with respect to mods whose patches are applied after it, so
`1_` directly after Harmony is what makes "everything else is inside this number" true.

## Commands

```
proflog                      status
proflog on | off             enable or disable counter recording
proflog interval <sec>       sampling interval, minimum 1 (default 10)
proflog summary <n>          write a log summary every n samples, 0 disables (default 6)
proflog flush                flush output files to disk
proflog where                print output file paths
proflog patches              list patched and failed call sites
proflog env                  print environment and settings line
proflog missing              list reflection members that failed to resolve
proflog reset                zero all counters

proflog cull                 RegionFileManager state snapshot
proflog cullbench [n]        benchmark the CullExpiredChunks loop on live data, n iterations (default 3)
proflog lockprobe on | off   the lock-wait measurement prefix

proflog pools                MemoryPools free-list snapshot with per size class breakdown
proflog pooldetail on | off  write the per size class pools TSV (default on)

proflog native               last native object census, counts and native bytes per Unity type
proflog native now           force a census right now, blocks the calling frame
proflog native on | off      the periodic census (default on)
proflog native every <sec>   seconds between censuses (default 300)
proflog native cap <n>       skip byte sizing for a type with more than n objects (default 300000)
proflog native names on|off  collect the top objects by native size (default on)

proflog bundles              list the AssetBundles currently held by AssetBundleManager
```

**The first thing to do after startup is `proflog patches`.** If anything in that list has failed,
the corresponding columns stay at zero, and you need to know that before you analyse anything.
`proflog missing` does the same for the reflection members the probes read state through.

## Where it writes

`<SaveGameDir>/ProfLog/`:

| file | contents |
|---|---|
| `proflog_probes_<stamp>.tsv` | one row per interval: every probe's call count and total ms, plus the world state and memory columns |
| `proflog_threads_<stamp>.tsv` | one row per thread per interval, CPU from `/proc/self/task` |
| `proflog_pools_<stamp>.tsv` | one row per array pool per interval, with the per size class breakdown (`pooldetail off` stops it) |
| `proflog_native_<stamp>.tsv` | one row per Unity type per census, with deltas from the previous census |
| `proflog_summary_<stamp>.log` | the same summaries that go to the main log, with timestamps |

Summaries are mirrored into the main server log with the `[ProfLog]` prefix. A separate file is
needed because on a production server `log/console/*.log` is often empty and the output otherwise
only lives in a tmux pane with limited scrollback.

Every file starts with `#` comment lines that document its own columns, so a TSV stays readable
without this README.

Volume over 12 h at a 10 s interval: probes ≈ 4–6 MB, threads ≈ 10–15 MB, summary ≈ 1 MB. The pools
file adds one row per array pool per sample; the native file adds one row per censused type per
census, and censuses default to one every 300 s.

## What each probe answers

### Frame boundaries

`GmUpdate` — work inside `GameManager.gmUpdate`, the server ceiling being 50 ms. This is **not** the
whole frame: by measurement the main thread spends roughly twice as much CPU outside it as inside.
Three probes break that gap down.

| probe | what it measures |
|---|---|
| `FrameWall` | the real frame time — the delta between entries into `GameManager.Update`. Gaps longer than 2 s are discarded so that world loading does not spoil the average |
| `UpdateOuter` | everything inside `GameManager.Update`: the body (`gmUpdate`) plus other mods' postfixes. Prefix is `Priority.First`, postfix is `Priority.Last`, so foreign patches are guaranteed to be inside |
| `MultiBlockMain` | `MultiBlockManager.MainThreadUpdate` from `World.OnUpdateTick`, also outermost — it covers `Deferred.DrainMainThread` from MaxChunkAgeDeadlockFix with its `TryEnter(chunksInSaveDir, 20)` |

The decomposition printed by the `frame:` line in the summary:

```
UpdateOuter − GmUpdate               = the cost of all foreign postfixes on GameManager.Update
FrameWall − UpdateOuter − LateUpdate = the Unity engine plus the frame limiter's sleep
FrameWall − mainCpu                  = how much the thread actually sleeps
```

`mainCpu` is taken from `/proc/self/task/<pid>/stat` over the same interval and divided by the frame
count. It is what separates "sleeping" from "computing something uncovered": if `offUpdate` is large
while `idle` is small, then real work is happening outside `GameManager.Update` — Physics, Mecanim,
foreign MonoBehaviours.

### Composition of the main thread

| probe | question it settles |
|---|---|
| `CopyChunks` | the hypothesis that uploading collision meshes is the biggest item on main. Budget in code is 25 ms |
| `NetChunkSetup` | chunk serialization for the network on main, and how many times per chunk with N observers |
| `SendChunks` | the outer boundary of the above |
| `SaveSnapMain` | the remainder of save serialization on main after the main path moved to a thread |
| `TickEntity` | the full cost of an entity per tick, including overrides — a reliable per-entity total |
| `HumanUpdateLive` / `PlayerUpdateLive` | separating zombies from players; `OnUpdateLive` is only the base part inside them |
| `UpdateTasks`, `MoveHelper`, `VoxelRaycast`, `CanBeSeen` | the real cost of PhysX rays on zombies, and whether it grows with the horde |
| `PathCalc` | the limit of 8 paths per frame in the ASP coroutine |
| `ChunkTeTick` | tile entity ticks across all active chunks |
| `MapRender` | `EncodeToPNG`/`LoadImage` on main |
| `StabilityStep` | a budget of 3 ms per 0.1 s — is it being exhausted |
| `BuffsTick`, `EntityActivity`, `NetDistrib`, `PowerUpdate`, `ProcessPkgs` | entity buffs, activity update, entity distribution, power grid, packet reception on main |
| `BlockTicker`, `LetBlocksFall`, `SleeperTick`, `AiDirector`, `DetermineChunks`, `GroundAlign`, `MainThreadTasks`, `WaterSimUpdate`, `LateUpdate` | the rest of the frame |

The difference between `GmUpdate_ms` and the sum of the nested probes is what the probes do not
cover: mods, the Unity part of the frame, Mecanim, physics.

### Worker threads

| probe | question it settles |
|---|---|
| `DoSaveChunks` | how many times per interval the save loop runs |
| `CullExpired` | how much of that is a pass over every saved chunk |
| `UpdateProtection` | rebuilding the protection map; it holds a read lock on `ChunkCluster`, i.e. it competes with main |
| `RemoveChunks` + `ChunksRemoved` | the actual volume of MaxChunkAge evictions |
| `TakeSnapshot` | chunk serialization on a thread, for comparison with `SaveSnapMain` |
| `RegenChunk` | how many chunks `ChunkRegeneration` puts through per interval and at what cost each |
| `LightChunk` | lighting in `ChunkCalc` |
| `GenChunk` | is `GenerateChunks` really idle |

The ratio `CullExpired_n / DoSaveChunks_n` should be ≈ 1 — that is exactly the test of the
hypothesis "a full pass for every saved chunk". `UpdateProtection_ms / CullExpired_ms` shows how
much of it is taken by rebuilding the protections.

### Decomposition of `CullExpiredChunks`

The measured cost of `CullExpiredChunks` on a production server is 17.5 ms per call, while a
standalone benchmark of the same logic over 90k keys gives ~1 ms of scan + ~1.2 ms of
`UpdateGroupTimestamps` + 1.58 ms of `UpdateChunkProtectionLevels` ≈ 3.8 ms. These probes account
for the difference, and the answer decides the fix: if it is the scan, a different loop or an index
is needed; if it is lock waiting or group recomputation, throttling is enough.

| probe | what it settles |
|---|---|
| `GroupTimestamps` | `UpdateGroupTimestamps` — a full recomputation of the maxima across all groups |
| `AddGrouped` | `AddGroupedChunks` — raises `groupTimestampsDirty`. Called when multiblocks are registered, i.e. on chunk load |
| `RemoveGrouped` | `RemoveGroupedChunks` |
| `RebuildGroups` | `RebuildChunkGroupsFromPOIs` — by the logs it fires 7 times a day, not only at startup |
| `OptimizeLayouts` | `RegionFileAccessMultipleChunks.OptimizeLayouts`, called from `DoSaveChunks` after `CullExpiredChunks` |
| `IsChunkInSave` | `isChunkInSaveDir` — a victim of the `chunksInSaveDir` lock |
| `GetChunkSyncRfm` | `RegionFileManager.GetChunkSync` — the second victim of the same lock |
| `CullLockWait` | the time to acquire `lock(chunksInSaveDir)` immediately before the method itself takes it |

In the summary, `IsChunkInSave` and `GetChunkSyncRfm` land in the worker section, but they can be
called from any thread, including the main one. That is an output grouping, not a claim about the
thread.

**How lock waiting is measured.** A separate prefix on `CullExpiredChunks` with `priority = Last`
(verified with Harmony 2.13 from `0_TFP_Harmony`: the `MaxChunkAgeDeadlockFix` marker prefix runs
first, ours returns control, their finalizer runs — there is no conflict). The prefix:

1. makes sure `saveLock` is already held (`Monitor.IsEntered`). That guarantees the documented
   acquisition order `saveLock` → `chunksInSaveDir`, the very one the game checks itself in
   `RemoveChunks`. If `saveLock` is not held, the measurement is skipped;
2. times a blocking `Monitor.Enter(chunksInSaveDir)`, takes the counters, releases.

This is the one probe that perturbs the system: between our `Exit` and the acquisition inside the
original, another thread may wedge in. Turn it off on the fly with `proflog lockprobe off`.

### State

`players zombies entities chunks cgo observers path_q path_done save_dir_chunks save_backlog
gen_queue target_fps heap_mb rss_mb gc0 gc1 gc2 day hour groups grouped_chunks reset_req
prot_levels`

`save_dir_chunks` — the size of `chunksInSaveDir`; the cost of `CullExpired` depends on it linearly.
`gen_queue` — the length of `m_ChunkQueue`, the answer to whether parallelizing generation makes
sense. `gc0/1/2` — absolute counters; deltas are computed between rows. `groups`,
`grouped_chunks`, `reset_req`, `prot_levels` are `chunkGroups.Count`, `GroupedLongsCount`,
`resetRequestedChunks.Count` and `chunkProtectionLevels.Count`.

`CullKeys`, `CullResetReq`, `CullProtLevels` are sums over all `CullExpired` calls in the interval;
for the average, divide by `CullLockWait_n`. `CullGroupDirty` and `CullProtDirty` count how many
calls found the corresponding dirty flag raised.

### Memory pools

`pooled_arrays_mb` is what the `MemoryPools` free lists currently retain. These arrays are reachable
from statics, so no GC can reclaim them, and vanilla only empties the lists in `MemoryPools.Cleanup`
— on player disconnect and 8 s after the server empties. `pooled_arrays_hw_mb` is the figure the
vanilla `mem pools` command prints; it counts emptied list slots too, so it is a high-water mark,
not a current size.

`arr_*_mb` split the retained bytes per element type, `pool_*` are the `MemoryPooledObject` free-list
sizes, `inst_*` the static `InstanceCount` counters. `mono_heap_mb` is the Boehm heap committed by
Mono and `mono_used_mb` the part in use; the difference is committed but free, which RSS still
counts.

`proflog pools` prints the same as a snapshot with the per size class breakdown, and
`proflog_pools_<stamp>.tsv` carries one row per array pool per sample, where `c<N>` is the number of
arrays of size class N parked in the free list right now.

### Native objects and bundles

A census walks `Resources.FindObjectsOfTypeAll` for 15 Unity types — `Mesh`, `Texture2D`,
`Texture2DArray`, `Cubemap`, `RenderTexture`, `Material`, `Shader`, `GameObject`, `Transform`,
`Sprite`, `AudioClip`, `AnimationClip`, `ScriptableObject`, `TextAsset`, `Font` — and sizes each
object with `Profiler.GetRuntimeMemorySizeLong`. It runs on the main thread, and only every
`proflog native every <sec>` seconds (300 by default) because the walk is expensive; `nat_age_s`
says how stale the `nat_*` columns in the probes row are.

These objects are released only by `Object.Destroy`, `AssetBundle.Unload` or
`Resources.UnloadUnusedAssets`, and a dedicated server never calls the last one. `d_count` and
`d_bytes` in the native TSV are the deltas from the previous census — that is what identifies a
leaking type. A type with more objects than `proflog native cap` is counted but not sized, and its
row says `sized=0`.

`nat_bundles` is `AssetBundleManager.dictAssetBundleRefs` count. The manager has no per-asset cache:
every `Get` calls `AssetBundle.LoadAsset` and the asset stays resident until the bundle is unloaded.
`proflog bundles` lists them.

`native_res_mb`, `native_alloc_mb` and `native_unused_mb` come from `UnityEngine.Profiling.Profiler`
and read 0 if the player build has the profiler compiled out.

## Summary output

Every `proflog summary <n>` samples (6 by default) a block goes to the log and to the summary file:
a headline `work=… fps=… ply=… chk=…` line, `main ms/frame:` and `worker ms/s:` top lists, the
`frame:` decomposition, two `cull:` lines, the `pools:` lines, the `native:` lines and a `mem:` line.

```
cull: 17.53ms/call x9.42/s = 165ms/s | wait=.. prot=.. gts=.. rem=.. scan=.. (..ns/key over .. keys)
cull ctx: protDirty=n/N gtsDirty=n/N resetReq=.. protLevels=.. groups=.. groupedChunks=.. lockHeld=..% victims: isInSave=../s getSync=../s
```

`scan` is the remainder: `CullExpired − CullLockWait − UpdateProtection − GroupTimestamps −
RemoveChunks`. If it is close to zero while `gts` is large, group recomputation is to blame and
throttling will do. If `scan` is large, a different loop is needed. If `wait` is large, the problem
is lock contention.

## `proflog cullbench [n]`

Runs five loop variants **on live data, inside the game's Mono, on the production CPU**:

```
raw iterate            a bare iteration over chunksInSaveDir, the baseline
V0 vanilla replica     an exact copy of what the game does (List.Contains + GetChunkTimestamp)
V1 +HashSet resetReq   resetRequestedChunks as a HashSet
V2 +KVP no relookup    iteration over KeyValuePair, without a repeated lookup into the same dictionary
V3 +min-deadline       V2 plus tracking of the nearest deadline
UpdateGroupTimestamps  a read-only copy of the per-group maxima recomputation
Dict<Group,uint> hash  the cost of a lookup by reference key (33 ns on this machine versus 6.6 for long)
```

Everything is read-only: results are collected into a local list, `RemoveChunks` is not called, and
no fields are modified. Each variant takes `saveLock` → `chunksInSaveDir` separately, so the hold
time is on the order of a single variant's duration rather than the whole run. The minimum of `n`
runs is taken (3 by default, 10 maximum). The output is mirrored into the main log with the
`cullbench|` prefix.

Run it under load, while there are people in the game — otherwise the numbers will be those of an
empty world.

`proflog cull` is the instant snapshot next to it: the sizes of all the collections involved, both
dirty flags, `maxChunkAge`, `maxBytes`, and the state of `lockprobe`.

## Caveats

- `clk_tck` in the threads file is assumed to be 100. On x86_64 Linux that is the case, but if
  absolute milliseconds matter, check `getconf CLK_TCK`.
- `UpdateTasks` is patched only on `EntityAlive`. `EntityDrone`, `EntityVehicle`, `EntityVulture`,
  `EntityBandit` and `EntityEnemyAnimal` override it and do not land in that column.
  For ordinary zombies (`EntityZombie`) there is no override, so the column is correct.
- The probes are nested inside one another; the columns must not be summed. The TSV header contains
  the nesting tree.
- The overhead of the wrappers themselves is included in the measured time. For rare calls that is
  noise; for `VoxelRaycast` at tens of thousands of calls per second it is on the order of tenths of
  a percent of the frame.
- A failed patch is not an error: the mod logs it, keeps running and leaves that column at zero.
  `proflog patches` and `proflog missing` are the authoritative list of what is actually being
  measured in this session.
- The native census blocks the frame it runs in. At the default 300 s interval that is one slow
  frame every five minutes; `proflog native off` removes it entirely.

## Related mods

Three server-side mods for the same dedicated server, same build layout, same `[Name]` log prefix:

| mod | what it is for |
|---|---|
| [MaxChunkAgeDeadlockFix](https://github.com/kotfoxtrot/7d2d_MaxChunkAgeDeadlockFix) | makes `MaxChunkAge` chunk reset safe by breaking the lock-order deadlock that freezes the server |
| [CullExpiredFix](https://github.com/kotfoxtrot/7d2d_CullExpiredFix) | makes that same reset cheap: `CullExpiredChunks` drops from 47% of one core to 0.05% |
| [ProfLog](https://github.com/kotfoxtrot/7d2d_ProfLog) | this mod — the profiler both of those were measured with |

This mod is what produced the numbers in the other two READMEs, and it is how you check them on your
own server: `CullExpired`, `CullLockWait`, `UpdateProtection`, `GroupTimestamps` and `RemoveChunks`
measure exactly what CullExpiredFix changes, and `MultiBlockMain` covers MaxChunkAgeDeadlockFix's
main-thread drain. It is useful next to them and not required by either.
