# ProfLog

A measurement mod for the 7DTD 3.1.0 dedicated server. Installs Harmony wrappers on 42 call sites, and
every N seconds samples the counters, the world state and per-thread process CPU, writing it all to TSV.

Build: `dotnet build -c Release` → `bin/ProfLog.dll`.
Install: `ProfLog.dll` + `ModInfo.xml` into `Mods/ProfLog/`.

## Where it writes

`<SaveGameDir>/ProfLog/`:

| file | contents |
|---|---|
| `proflog_probes_<stamp>.tsv` | one row per interval, all probes + world state |
| `proflog_threads_<stamp>.tsv` | one row per thread per interval, CPU from `/proc/self/task` |
| `proflog_summary_<stamp>.log` | the same summaries that go to the main log, with timestamps |

Summaries are mirrored into the main server log with the `[ProfLog]` prefix — same as `MaxChunkAgeDeadlockFix`.
A separate file is needed because on prod `log/console/*.log` is empty and the output only lives
in a tmux pane with a limited scrollback.

Volume over 12 h at a 10 s interval: probes ≈ 4–6 MB, threads ≈ 10–15 MB, summary ≈ 1 MB.

## Commands

```
proflog                 status
proflog on | off        enable/disable counter recording
proflog interval <sec>  sampling interval, minimum 1
proflog summary <n>     summary to the log every n samples, 0 to disable
proflog flush           flush files to disk
proflog where           file paths
proflog patches         what is patched and what is not
proflog env             environment and settings line
proflog reset           zero the counters
```

The first thing to do after startup is `proflog patches`. If anything in the list is failed,
the corresponding columns will be zero, and you need to know that before analysing anything.

## What each probe answers

### Frame boundaries

`GmUpdate` — work inside `GameManager.gmUpdate`, the server ceiling being 50 ms. This is **not** the whole
frame: by measurement the main thread spends roughly twice as much CPU outside it as inside. Three probes
break that gap down.

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

`mainCpu` is taken from `/proc/self/task/<pid>/stat` over the same interval and divided by the frame count.
It is what separates "sleeping" from "computing something uncovered": if `offUpdate` is large while `idle`
is small, then real work is happening outside `GameManager.Update` — Physics, Mecanim, foreign MonoBehaviours.

### Composition of the main thread

| probe | question it settles |
|---|---|
| `CopyChunks` | the hypothesis that uploading collision meshes is the biggest item on main. Budget in code is 25 ms |
| `NetChunkSetup` | A1: chunk serialization for the network on main, and how many times per chunk with N observers |
| `SendChunks` | the outer boundary of A1 |
| `SaveSnapMain` | A2: the remainder of save serialization on main after the main path moved to a thread |
| `TickEntity` | the full cost of an entity per tick, including overrides — a reliable per-entity total |
| `HumanUpdateLive` / `PlayerUpdateLive` | separating zombies from players; `OnUpdateLive` is only the base part inside them |
| `UpdateTasks`, `MoveHelper`, `VoxelRaycast`, `CanBeSeen` | B2: the real cost of PhysX rays on zombies, and whether it grows with the horde |
| `PathCalc` | the limit of 8 paths per frame in the ASP coroutine |
| `ChunkTeTick` | C3: tile entity ticks across all active chunks |
| `MapRender` | A6: `EncodeToPNG`/`LoadImage` on main |
| `StabilityStep` | A3: a budget of 3 ms per 0.1 s — is it being exhausted |
| `NetDistrib` | A8 |
| `PowerUpdate` | C4 |
| `ProcessPkgs` | packet reception on main |
| `BlockTicker`, `LetBlocksFall`, `SleeperTick`, `AiDirector`, `DetermineChunks`, `GroundAlign`, `MainThreadTasks`, `WaterSimUpdate`, `LateUpdate` | the rest of the frame |

The difference between `GmUpdate_ms` and the sum of the nested probes is what the probes do not cover:
mods, the Unity part of the frame, Mecanim, physics.

### Worker threads

| probe | question it settles |
|---|---|
| `DoSaveChunks` | how many times per interval the save loop runs |
| `CullExpired` | how much of that is a pass over every saved chunk |
| `UpdateProtection` | rebuilding the protection map; it holds a read lock on `ChunkCluster`, i.e. it competes with main |
| `RemoveChunks` + `ChunksRemoved` | the actual volume of MaxChunkAge evictions |
| `TakeSnapshot` | chunk serialization on a thread, for comparison with `SaveSnapMain` |
| `RegenChunk` | 20 % of a core in `ChunkRegeneration` — how many chunks that is and how many ms per chunk |
| `LightChunk` | lighting in `ChunkCalc` |
| `GenChunk` | A5: is `GenerateChunks` really idle |

The ratio `CullExpired_n / DoSaveChunks_n` should be ≈ 1 — that is exactly the test of the hypothesis
"a full pass for every saved chunk". `UpdateProtection_ms / CullExpired_ms` will show how much of it
is taken by rebuilding the protections.

### State

`players zombies entities chunks cgo observers path_q path_done save_dir_chunks save_backlog gen_queue heap_mb rss_mb gc0 gc1 gc2 day hour`

`save_dir_chunks` — the size of `chunksInSaveDir`; the cost of `CullExpired` depends on it linearly.
`gen_queue` — the length of `m_ChunkQueue`, the answer to whether parallelizing generation makes sense.
`gc0/1/2` — absolute counters; deltas are computed between rows.

## Caveats

- `clk_tck` in the threads file is assumed to be 100. On x86_64 Linux that is the case, but if absolute
  milliseconds matter, check `getconf CLK_TCK`.
- `UpdateTasks` is patched only on `EntityAlive`. `EntityDrone`, `EntityVehicle`, `EntityVulture`,
  `EntityBandit` and `EntityEnemyAnimal` override it and do not land in that column.
  For ordinary zombies (`EntityZombie`) there is no override, so the column is correct.
- The probes are nested inside one another; the columns must not be summed. The TSV header contains
  the nesting tree.
- The overhead of the wrappers themselves is included in the measured time. For rare calls that is noise;
  for `VoxelRaycast` at tens of thousands of calls per second it is on the order of tenths of a percent
  of the frame.

## v1.1 — decomposing CullExpiredChunks

Added for the sake of a single question: the measured cost of `CullExpiredChunks` on prod is 17.5 ms
per call, while a standalone benchmark of the same logic over 90k keys gives ~1 ms of scan + ~1.2 ms
of `UpdateGroupTimestamps` + 1.58 ms of `UpdateChunkProtectionLevels` ≈ 3.8 ms. The ~13.7 ms gap is
unexplained, and the choice of optimization depends on the answer: if it is the scan, a different loop
or an index is needed; if it is lock waiting or group recomputation, throttling is enough.

### New probes

| probe | what it settles |
|---|---|
| `GroupTimestamps` | `UpdateGroupTimestamps` — a full recomputation of the maxima across all groups. Suspect #1 |
| `AddGrouped` | `AddGroupedChunks` — raises `groupTimestampsDirty`. Called when multiblocks are registered, i.e. on chunk load |
| `RemoveGrouped` | `RemoveGroupedChunks` |
| `RebuildGroups` | `RebuildChunkGroupsFromPOIs` — by the logs it fires 7 times a day, not only at startup |
| `OptimizeLayouts` | `RegionFileAccessMultipleChunks.OptimizeLayouts`, called from `DoSaveChunks` after `CullExpiredChunks` |
| `IsChunkInSave` | `isChunkInSaveDir` — a victim of the `chunksInSaveDir` lock |
| `GetChunkSyncRfm` | `RegionFileManager.GetChunkSync` — the second victim of the same lock |
| `CullLockWait` | the time to acquire `lock(chunksInSaveDir)` immediately before the method itself takes it |

In the summary `IsChunkInSave` and `GetChunkSyncRfm` land in the worker section, but they can be called
from any thread, including the main one. That is an output grouping, not a claim about the thread.

### How lock waiting is measured

A separate prefix on `CullExpiredChunks` with `priority = Last` (verified against Harmony 2.13 from
`0_TFP_Harmony`: the `MaxChunkAgeDeadlockFix` marker prefix runs first, ours returns control, their
finalizer runs — there is no conflict). The prefix:

1. Makes sure `saveLock` is already held (`Monitor.IsEntered`). That guarantees the documented acquisition
   order `saveLock` → `chunksInSaveDir`, the very one the game checks itself in `RemoveChunks`. If
   `saveLock` is not held, the measurement is skipped.
2. Times a blocking `Monitor.Enter(chunksInSaveDir)`, takes the counters, releases.

The measurement perturbs the system slightly: between our `Exit` and the acquisition inside the original,
another thread may wedge in. It can be turned off on the fly: `proflog lockprobe off`.

### New state columns

`groups grouped_chunks reset_req prot_levels` — the sizes of `chunkGroups.Count`,
`GroupedLongsCount`, `resetRequestedChunks.Count`, `chunkProtectionLevels.Count`.

`CullKeys CullResetReq CullProtLevels` — sums over all `CullExpired` calls in the interval; for the average,
divide by `CullLockWait_n`. `CullGroupDirty` and `CullProtDirty` — how many calls found the corresponding
dirty flag raised.

### Decomposition in the summary

Every `SummaryEvery` samples two lines go to the log:

```
cull: 17.53ms/call x9.42/s = 165ms/s | wait=.. prot=.. gts=.. rem=.. scan=.. (..ns/key over .. keys)
cull ctx: protDirty=n/N gtsDirty=n/N resetReq=.. protLevels=.. groups=.. groupedChunks=.. lockHeld=..% victims: isInSave=../s getSync=../s
```

`scan` is the remainder: `CullExpired − CullLockWait − UpdateProtection − GroupTimestamps − RemoveChunks`.
If it turns out to be close to zero while `gts` is large, the group recomputation is to blame and throttling
will do. If `scan` is large, a different loop is needed. If `wait` is large, the problem is lock contention.

### `proflog cullbench [n]`

Runs five loop variants **on live data, inside the game's Mono, on the prod CPU**:

```
raw iterate            a bare iteration over chunksInSaveDir, the baseline
V0 vanilla replica     an exact copy of what the game does (List.Contains + GetChunkTimestamp)
V1 +HashSet resetReq   resetRequestedChunks as a HashSet
V2 +KVP no relookup    iteration over KeyValuePair, without a repeated lookup into the same dictionary
V3 +min-deadline       V2 plus tracking of the nearest deadline
UpdateGroupTimestamps  a read-only copy of the per-group maxima recomputation
Dict<Group,uint> hash  the cost of a lookup by reference key (33 ns on this machine versus 6.6 for long)
```

Everything is read-only: results are collected into a local list, `RemoveChunks` is not called, and no
fields are modified. Each variant takes `saveLock` → `chunksInSaveDir` separately, so the hold time is on
the order of a single variant's duration rather than the whole run. The minimum of `n` runs is taken
(3 by default, 10 maximum). The output is mirrored into the main log with the `cullbench|` prefix.

Run it under load, while there are people in the game — otherwise the numbers will be those of an empty world.

### `proflog cull`

An instant snapshot: the sizes of all the collections involved, both dirty flags, `maxChunkAge`,
`maxBytes`, and the state of `lockprobe`.
