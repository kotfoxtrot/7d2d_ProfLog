using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace ProfLog
{
    public static class Sampler
    {
        private const long ClkTck = 100L;

        public static volatile int IntervalSec = 10;
        public static volatile int SummaryEvery = 6;
        public static volatile bool Running;

        public static string ProbesPath = "";
        public static string ThreadsPath = "";
        public static string SummaryPath = "";
        public static string PoolsPath = "";
        public static string NativePath = "";
        public static string NamesPath = "";
        public static volatile bool PoolDetail = true;
        public static long Rows;

        private static Thread worker;
        private static volatile bool stop;
        private static StreamWriter probesWriter;
        private static StreamWriter threadsWriter;
        private static StreamWriter summaryWriter;
        private static StreamWriter poolsWriter;
        private static StreamWriter nativeWriter;
        private static StreamWriter namesWriter;
        private static long nativeSeen;
        private static int[] poolClassBuf;

        private static readonly long[] tickBuf = new long[(int)Pr.MAX];
        private static readonly long[] callBuf = new long[(int)Pr.MAX];
        private static readonly long[] extraBuf = new long[(int)Ex.MAX];
        private static readonly Dictionary<int, long> prevCpu = new Dictionary<int, long>(256);
        private static readonly StringBuilder sb = new StringBuilder(8192);
        private static readonly int MainTid = Process.GetCurrentProcess().Id;
        private static double mainCpuMs;

        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public static void Start()
        {
            if (worker != null)
            {
                return;
            }
            stop = false;
            worker = new Thread(Loop);
            worker.IsBackground = true;
            worker.Name = "ProfLogSampler";
            worker.Priority = ThreadPriority.BelowNormal;
            worker.Start();
        }

        public static void Stop()
        {
            stop = true;
        }

        public static void FlushNow()
        {
            try
            {
                if (probesWriter != null)
                {
                    probesWriter.Flush();
                }
                if (threadsWriter != null)
                {
                    threadsWriter.Flush();
                }
                if (summaryWriter != null)
                {
                    summaryWriter.Flush();
                }
                if (poolsWriter != null)
                {
                    poolsWriter.Flush();
                }
                if (nativeWriter != null)
                {
                    nativeWriter.Flush();
                }
                if (namesWriter != null)
                {
                    namesWriter.Flush();
                }
            }
            catch
            {
            }
        }

        public static void Say(string line)
        {
            Log.Out("[ProfLog] " + line);
            try
            {
                if (summaryWriter != null)
                {
                    summaryWriter.WriteLine(DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss", Inv) + " " + line);
                }
            }
            catch
            {
            }
        }

        private static string ResolveDir()
        {
            string baseDir = null;
            try
            {
                baseDir = GameIO.GetSaveGameDir();
            }
            catch
            {
            }
            if (string.IsNullOrEmpty(baseDir))
            {
                try
                {
                    baseDir = GameIO.GetUserGameDataDir();
                }
                catch
                {
                }
            }
            if (string.IsNullOrEmpty(baseDir))
            {
                baseDir = ".";
            }
            string dir = Path.Combine(baseDir, "ProfLog");
            try
            {
                if (!Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }
            }
            catch
            {
                dir = baseDir;
            }
            return dir;
        }

        private static void OpenWriters()
        {
            string dir = ResolveDir();
            string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", Inv);
            ProbesPath = Path.Combine(dir, "proflog_probes_" + stamp + ".tsv");
            ThreadsPath = Path.Combine(dir, "proflog_threads_" + stamp + ".tsv");
            SummaryPath = Path.Combine(dir, "proflog_summary_" + stamp + ".log");
            PoolsPath = Path.Combine(dir, "proflog_pools_" + stamp + ".tsv");
            NativePath = Path.Combine(dir, "proflog_native_" + stamp + ".tsv");
            NamesPath = Path.Combine(dir, "proflog_names_" + stamp + ".tsv");

            probesWriter = new StreamWriter(new FileStream(ProbesPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false));
            threadsWriter = new StreamWriter(new FileStream(ThreadsPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false));
            summaryWriter = new StreamWriter(new FileStream(SummaryPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false));
            poolsWriter = new StreamWriter(new FileStream(PoolsPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false));
            nativeWriter = new StreamWriter(new FileStream(NativePath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false));
            namesWriter = new StreamWriter(new FileStream(NamesPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false));
            summaryWriter.WriteLine(DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss", Inv) + " env: " + StateReader.Environment());

            probesWriter.WriteLine("# ProfLog probes");
            probesWriter.WriteLine("# " + StateReader.Environment());
            probesWriter.WriteLine("# columns <name>_n = calls in interval, <name>_ms = total wall ms inside that call site in interval");
            probesWriter.WriteLine("# main-thread probes nest: GmUpdate > UpdateTick > WorldTick|TickEntities|CopyChunks|SendChunks|GroundAlign");
            probesWriter.WriteLine("# TickEntities > TickEntity = full per-entity cost incl. subclass overrides, this is the reliable per-entity total");
            probesWriter.WriteLine("# zombies are EntityZombie:EntityHuman:EntityEnemy:EntityAlive, so HumanUpdateLive is the outer live tick and OnUpdateLive is only its base part");
            probesWriter.WriteLine("# PlayerUpdateLive is the same boundary for players; OnUpdateLive_n counts every EntityAlive incl. those two");
            probesWriter.WriteLine("# UpdateTasks > MoveHelper|PathCalc; UpdateTasks and CanBeSeen both reach VoxelRaycast");
            probesWriter.WriteLine("# UpdateTasks is patched on EntityAlive only; EntityDrone EntityVehicle EntityVulture EntityBandit EntityEnemyAnimal override it and are not counted there");
            probesWriter.WriteLine("# WorldTick > BlockTicker|ChunkTeTick|AiDirector|SleeperTick; SendChunks > NetChunkSetup");
            probesWriter.WriteLine("# worker-thread probes (not on main): CullExpired UpdateProtection DoSaveChunks LightChunk RegenChunk GenChunk TakeSnapshot RemoveChunks");
            probesWriter.WriteLine("# DoSaveChunks > CullExpired > UpdateProtection|RemoveChunks; RemoveChunks also counted as ChunksRemoved");
            probesWriter.WriteLine("# frame_ms_mean = GmUpdate_ms / GmUpdate_n; server frame cap is 20 fps = 50 ms");
            probesWriter.WriteLine("# heap_mb rss_mb gc0 gc1 gc2 are absolute at sample time, not deltas");
            probesWriter.WriteLine("# CullExpired decomposition: CullExpired_ms = CullLockWait + UpdateProtection + scan + RemoveChunks");
            probesWriter.WriteLine("# CullLockWait_ms is a separate prefix that times acquiring lock(chunksInSaveDir) just before the real method does");
            probesWriter.WriteLine("# CullKeys CullResetReq CullProtLevels are SUMS over CullExpired calls in the interval, divide by CullLockWait_n for the mean");
            probesWriter.WriteLine("# CullProtDirty counts how many CullExpired calls found protectionLevelsDirty set");
            probesWriter.WriteLine("# IsChunkInSave and GetChunkSyncRfm are the victims of that lock, they can run on any thread including main");
            probesWriter.WriteLine("# FrameWall_ms/_n = true wall time between consecutive GameManager.Update entries, gaps over 2s are dropped");
            probesWriter.WriteLine("# UpdateOuter wraps GameManager.Update with prefix Priority.First and postfix Priority.Last, so every other mod patch on Update is inside it");
            probesWriter.WriteLine("# UpdateOuter_ms - GmUpdate_ms = cost of all foreign postfixes on GameManager.Update, PluginManager publishes GameUpdateEvent there");
            probesWriter.WriteLine("# FrameWall - UpdateOuter - LateUpdate = engine work plus frame-cap sleep outside GameManager, join with the main thread row in the threads TSV to split those two");
            probesWriter.WriteLine("# MultiBlockMain is MultiBlockManager.MainThreadUpdate called from World.OnUpdateTick, also outermost, so MaxChunkAgeDeadlockFix DrainMainThread and its TryEnter(chunksInSaveDir,20) are inside it");
            probesWriter.WriteLine("# pooled_arrays_mb = bytes currently parked in the MemoryPools free lists, reachable from statics so no GC can reclaim them");
            probesWriter.WriteLine("# pooled_arrays_hw_mb is the same figure the vanilla mem pools command prints, it counts emptied list slots too so it is a high-water mark");
            probesWriter.WriteLine("# arr_*_mb split pooled_arrays_mb per element type; pool_* are MemoryPooledObject free-list sizes; inst_* are the static InstanceCount counters");
            probesWriter.WriteLine("# MemoryPools free lists are only emptied by MemoryPools.Cleanup, which vanilla calls on player disconnect and 8s after the server empties");
            probesWriter.WriteLine("# mono_heap_mb is the Boehm heap committed by mono, mono_used_mb the part in use; their difference is committed but free, which RSS still counts");
            probesWriter.WriteLine("# native_res_mb and native_alloc_mb are Unity native memory; these five come from UnityEngine.Profiling.Profiler and read 0 if the player build has the profiler compiled out");
            probesWriter.WriteLine("# nat_* come from the native object census, which runs on the main thread every NativeProbe.IntervalSec, not every sample, so nat_age_s says how stale they are");
            probesWriter.WriteLine("# nat_*_n are live UnityEngine.Object counts from Resources.FindObjectsOfTypeAll, nat_*_mb their native size from Profiler.GetRuntimeMemorySizeLong");
            probesWriter.WriteLine("# these objects are released only by Object.Destroy, AssetBundle.Unload or Resources.UnloadUnusedAssets, and a dedicated server never calls the last one");
            probesWriter.WriteLine("# nat_bundles = AssetBundleManager.dictAssetBundleRefs count; the manager has no per-asset cache, every Get calls AssetBundle.LoadAsset and the asset stays resident");

            sb.Length = 0;
            sb.Append("ts\tuptime_s\tinterval_s\tframes\tframe_ms_mean");
            for (int i = 0; i < (int)Pr.MAX; i++)
            {
                sb.Append('\t').Append(Counters.Names[i]).Append("_n");
                sb.Append('\t').Append(Counters.Names[i]).Append("_ms");
            }
            for (int i = 0; i < (int)Ex.MAX; i++)
            {
                sb.Append('\t').Append(Counters.ExtraNames[i]);
            }
            sb.Append("\tplayers\tzombies\tentities\tchunks\tcgo\tobservers\tpath_q\tpath_done\tsave_dir_chunks\tsave_backlog\tgen_queue\ttarget_fps\theap_mb\trss_mb\tgc0\tgc1\tgc2\tday\thour\tgroups\tgrouped_chunks\treset_req\tprot_levels\tpooled_arrays_mb\tpooled_arrays_hw_mb\tcbl_cache_mb\tarr_v2_mb\tarr_v3_mb\tarr_v4_mb\tarr_col_mb\tarr_int_mb\tarr_u16_mb\tarr_f32_mb\tarr_byte_mb\tpool_objs\tpool_chunks\tpool_cbl\tpool_cbc\tpool_cgl\tpool_vml\tpool_ms\tinst_chunk\tinst_cbl\tinst_cbc\tinst_vml\tmono_heap_mb\tmono_used_mb\tnative_res_mb\tnative_alloc_mb\tnative_unused_mb\tnat_age_s\tnat_census_ms\tnat_bundles\tnat_total_n\tnat_total_mb\tnat_mesh_n\tnat_mesh_mb\tnat_tex_n\tnat_tex_mb\tnat_mat_n\tnat_mat_mb\tnat_shader_n\tnat_go_n\tnat_tr_n\tnat_animclip_n\tnat_animclip_mb");
            probesWriter.WriteLine(sb.ToString());
            probesWriter.Flush();

            threadsWriter.WriteLine("# ProfLog threads, cpu_ms from /proc/self/task utime+stime, clk_tck assumed " + ClkTck);
            threadsWriter.WriteLine("# cpu_pct is percent of ONE core over the interval");
            threadsWriter.WriteLine("ts\tuptime_s\tinterval_s\ttid\tname\tcpu_ms\tcpu_pct");
            threadsWriter.Flush();

            PoolProbe.Init();
            poolsWriter.WriteLine("# ProfLog MemoryPools free lists, one row per array pool per sample");
            poolsWriter.WriteLine("# pool = element type of MemoryPooledArray<T>; elem_size = bytes per element");
            poolsWriter.WriteLine("# c<N> = number of arrays of size class N parked in the free list right now");
            poolsWriter.WriteLine("# bytes = sum over classes of c<N> * N * elem_size, this is what the pool actually retains");
            poolsWriter.WriteLine("# hw_bytes = the figure the vanilla mem pools command reports, it counts emptied list slots so it is a high-water mark");
            poolsWriter.WriteLine("# these arrays are reachable from static MemoryPools fields, no collection can reclaim them until MemoryPools.Cleanup runs");
            sb.Length = 0;
            sb.Append("ts\tuptime_s\tpool\telem_size\tarrays\tbytes\thw_bytes");
            for (int i = 0; i < PoolProbe.ClassCount; i++)
            {
                sb.Append("\tc").Append(PoolProbe.SizeClass(i));
            }
            poolsWriter.WriteLine(sb.ToString());
            poolsWriter.Flush();

            NativeProbe.Init();
            nativeWriter.WriteLine("# ProfLog native object census, one row per Unity type per census");
            nativeWriter.WriteLine("# a census walks Resources.FindObjectsOfTypeAll for each type and sizes each object with Profiler.GetRuntimeMemorySizeLong");
            nativeWriter.WriteLine("# it runs on the main thread, not on the sampler thread, and only every NativeProbe.IntervalSec seconds because the walk is expensive");
            nativeWriter.WriteLine("# count = live objects of that type; bytes = their native memory; sized=0 means the type had more objects than SizeCap so bytes were not summed");
            nativeWriter.WriteLine("# d_count and d_bytes are deltas from the previous census, this is what identifies a leaking type");
            nativeWriter.WriteLine("# scan_ms is the cost of that single type, census_ms the cost of the whole pass");
            nativeWriter.WriteLine("# bundles = loaded AssetBundles; UL ships about 6.7 GB of them and assets pulled from a bundle stay resident until the bundle is unloaded");
            nativeWriter.WriteLine("# sized: 0 = bytes not summed, 1 = every object sized, 2 = sampled and extrapolated because count exceeded SizeCap");
            nativeWriter.WriteLine("# instances = objects whose name carries Unity's (Instance) suffix, so they are runtime copies rather than loaded assets; -1 means not measured for that type");
            nativeWriter.WriteLine("ts\tuptime_s\tcensus\tcensus_ms\tbundles\ttype\tcount\tbytes\td_count\td_bytes\tsized\tinstances\tscan_ms");
            nativeWriter.Flush();

            namesWriter.WriteLine("# ProfLog native object name histogram for one type, chosen by NativeProbe.HistType");
            namesWriter.WriteLine("# name is the object name with every trailing (Instance) suffix stripped, so runtime copies group under the asset they came from");
            namesWriter.WriteLine("# count is objects sharing that name; d_count is the change since the previous census, which is what names the leaking asset");
            namesWriter.WriteLine("# est=1 means the type had more objects than HistCap, so the histogram was sampled with a fixed stride and counts were scaled up");
            namesWriter.WriteLine("ts\tuptime_s\tcensus\ttype\trank\tname\tcount\td_count\test");
            namesWriter.Flush();

            Log.Out("[ProfLog] probes  -> " + ProbesPath);
            Log.Out("[ProfLog] threads -> " + ThreadsPath);
            Log.Out("[ProfLog] summary -> " + SummaryPath);
            Log.Out("[ProfLog] pools   -> " + PoolsPath);
            Log.Out("[ProfLog] native  -> " + NativePath);
            Log.Out("[ProfLog] names   -> " + NamesPath);
            Log.Out("[ProfLog] native census " + NativeProbe.Describe() + ", missing: " + NativeProbe.MissingTypes());
        }

        private static void Loop()
        {
            try
            {
                OpenWriters();
            }
            catch (Exception ex)
            {
                Log.Error("[ProfLog] cannot open output: " + ex);
                return;
            }

            Running = true;
            Stopwatch total = Stopwatch.StartNew();
            Stopwatch iv = Stopwatch.StartNew();
            long sinceSummary = 0L;

            while (!stop)
            {
                int sleepMs = IntervalSec * 1000;
                if (sleepMs < 1000)
                {
                    sleepMs = 1000;
                }
                Thread.Sleep(sleepMs);

                double intervalSec = iv.Elapsed.TotalSeconds;
                iv.Restart();
                if (intervalSec <= 0.0)
                {
                    continue;
                }

                try
                {
                    Counters.Snapshot(tickBuf, callBuf, extraBuf);
                    WorldState st = StateReader.Read();
                    WriteProbeRow(total.Elapsed.TotalSeconds, intervalSec, st);
                    WriteThreadRows(total.Elapsed.TotalSeconds, intervalSec);
                    WritePoolRows(total.Elapsed.TotalSeconds);
                    WriteNativeRows(total.Elapsed.TotalSeconds);
                    probesWriter.Flush();
                    threadsWriter.Flush();
                    summaryWriter.Flush();
                    poolsWriter.Flush();
                    nativeWriter.Flush();
                    namesWriter.Flush();
                    Rows++;

                    if (Rows == 1L)
                    {
                        Say("unresolved members: " + StateReader.MissingMembers());
                    }

                    sinceSummary++;
                    if (SummaryEvery > 0 && sinceSummary >= SummaryEvery)
                    {
                        sinceSummary = 0L;
                        WriteSummary(intervalSec, st);
                    }
                }
                catch (Exception ex)
                {
                    Log.Warning("[ProfLog] sample failed: " + ex.Message);
                }
            }

            Running = false;
            FlushNow();
            try
            {
                probesWriter.Dispose();
                threadsWriter.Dispose();
                summaryWriter.Dispose();
                poolsWriter.Dispose();
                nativeWriter.Dispose();
                namesWriter.Dispose();
            }
            catch
            {
            }
            probesWriter = null;
            threadsWriter = null;
            summaryWriter = null;
            poolsWriter = null;
            nativeWriter = null;
            namesWriter = null;
            worker = null;
        }

        private static double Ms(int i)
        {
            return tickBuf[i] * Counters.TicksToMs;
        }

        private static void WriteProbeRow(double uptime, double intervalSec, WorldState st)
        {
            sb.Length = 0;
            sb.Append(DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss", Inv));
            sb.Append('\t').Append(uptime.ToString("F1", Inv));
            sb.Append('\t').Append(intervalSec.ToString("F2", Inv));

            long frames = callBuf[(int)Pr.GmUpdate];
            double frameMs = (frames > 0L) ? (Ms((int)Pr.GmUpdate) / frames) : 0.0;
            sb.Append('\t').Append(frames);
            sb.Append('\t').Append(frameMs.ToString("F3", Inv));

            for (int i = 0; i < (int)Pr.MAX; i++)
            {
                sb.Append('\t').Append(callBuf[i]);
                sb.Append('\t').Append(Ms(i).ToString("F3", Inv));
            }
            for (int i = 0; i < (int)Ex.MAX; i++)
            {
                sb.Append('\t').Append(extraBuf[i]);
            }

            sb.Append('\t').Append(st.Players);
            sb.Append('\t').Append(st.Zombies);
            sb.Append('\t').Append(st.Entities);
            sb.Append('\t').Append(st.Chunks);
            sb.Append('\t').Append(st.ChunkGameObjects);
            sb.Append('\t').Append(st.Observers);
            sb.Append('\t').Append(st.PathQueue);
            sb.Append('\t').Append(st.PathFinished);
            sb.Append('\t').Append(st.SaveDirChunks);
            sb.Append('\t').Append(st.SaveBacklog);
            sb.Append('\t').Append(st.GenQueue);
            sb.Append('\t').Append(st.TargetFps);
            sb.Append('\t').Append((st.HeapBytes / 1048576L));
            sb.Append('\t').Append((st.RssBytes / 1048576L));
            sb.Append('\t').Append(st.Gc0);
            sb.Append('\t').Append(st.Gc1);
            sb.Append('\t').Append(st.Gc2);
            sb.Append('\t').Append(st.Day);
            sb.Append('\t').Append(st.Hour);
            sb.Append('\t').Append(st.Groups);
            sb.Append('\t').Append(st.GroupedChunks);
            sb.Append('\t').Append(st.ResetRequests);
            sb.Append('\t').Append(st.ProtLevels);
            AppendMb(st.Pools.ArraysBytes);
            AppendMb(st.Pools.ArraysHwBytes);
            AppendMb(st.Pools.CblCacheBytes);
            AppendMb(st.Pools.V2Bytes);
            AppendMb(st.Pools.V3Bytes);
            AppendMb(st.Pools.V4Bytes);
            AppendMb(st.Pools.ColBytes);
            AppendMb(st.Pools.IntBytes);
            AppendMb(st.Pools.U16Bytes);
            AppendMb(st.Pools.F32Bytes);
            AppendMb(st.Pools.ByteBytes);
            sb.Append('\t').Append(st.Pools.PoolObjects);
            sb.Append('\t').Append(st.Pools.PoolChunks);
            sb.Append('\t').Append(st.Pools.PoolCbl);
            sb.Append('\t').Append(st.Pools.PoolCbc);
            sb.Append('\t').Append(st.Pools.PoolCgl);
            sb.Append('\t').Append(st.Pools.PoolVml);
            sb.Append('\t').Append(st.Pools.PoolMs);
            sb.Append('\t').Append(st.Pools.InstChunk);
            sb.Append('\t').Append(st.Pools.InstCbl);
            sb.Append('\t').Append(st.Pools.InstCbc);
            sb.Append('\t').Append(st.Pools.InstVml);
            AppendMb(st.MonoHeapBytes);
            AppendMb(st.MonoUsedBytes);
            AppendMb(st.NativeReservedBytes);
            AppendMb(st.NativeAllocBytes);
            AppendMb(st.NativeUnusedBytes);

            long natCount;
            long natBytes;
            int natBundles;
            double natCensusMs;
            double natAge;
            NativeProbe.Snapshot(out natCount, out natBytes, out natBundles, out natCensusMs, out natAge);
            sb.Append('\t').Append((natAge < 0.0 ? -1.0 : natAge).ToString("F0", Inv));
            sb.Append('\t').Append(natCensusMs.ToString("F0", Inv));
            sb.Append('\t').Append(natBundles);
            sb.Append('\t').Append(natCount);
            AppendMb(natBytes);
            sb.Append('\t').Append(NativeProbe.CountOf(NativeProbe.IdxMesh));
            AppendMb(NativeProbe.BytesOf(NativeProbe.IdxMesh));
            sb.Append('\t').Append(NativeProbe.CountOf(NativeProbe.IdxTex2d));
            AppendMb(NativeProbe.BytesOf(NativeProbe.IdxTex2d));
            sb.Append('\t').Append(NativeProbe.CountOf(NativeProbe.IdxMaterial));
            AppendMb(NativeProbe.BytesOf(NativeProbe.IdxMaterial));
            sb.Append('\t').Append(NativeProbe.CountOf(NativeProbe.IdxShader));
            sb.Append('\t').Append(NativeProbe.CountOf(NativeProbe.IdxGameObject));
            sb.Append('\t').Append(NativeProbe.CountOf(NativeProbe.IdxTransform));
            sb.Append('\t').Append(NativeProbe.CountOf(NativeProbe.IdxAnimClip));
            AppendMb(NativeProbe.BytesOf(NativeProbe.IdxAnimClip));

            probesWriter.WriteLine(sb.ToString());
        }

        private static void WriteNativeRows(double uptime)
        {
            if (nativeWriter == null || !NativeProbe.Available)
            {
                return;
            }
            long seq = NativeProbe.Censuses;
            if (seq == nativeSeen)
            {
                return;
            }
            nativeSeen = seq;

            string ts = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss", Inv);
            string up = uptime.ToString("F1", Inv);
            string cms = NativeProbe.LastCensusMs.ToString("F1", Inv);
            int bundles = NativeProbe.Bundles;

            for (int i = 0; i < NativeProbe.TypeCount; i++)
            {
                long count;
                long bytes;
                long dCount;
                long dBytes;
                bool sized;
                double scanMs;
                if (!NativeProbe.CopyRow(i, out count, out bytes, out dCount, out dBytes, out sized, out scanMs))
                {
                    continue;
                }
                sb.Length = 0;
                sb.Append(ts);
                sb.Append('\t').Append(up);
                sb.Append('\t').Append(seq);
                sb.Append('\t').Append(cms);
                sb.Append('\t').Append(bundles);
                sb.Append('\t').Append(NativeProbe.TypeName(i));
                sb.Append('\t').Append(count);
                sb.Append('\t').Append(bytes);
                sb.Append('\t').Append(dCount);
                sb.Append('\t').Append(dBytes);
                sb.Append('\t').Append(NativeProbe.SizedFlag(i));
                sb.Append('\t').Append(NativeProbe.InstancesOf(i));
                sb.Append('\t').Append(scanMs.ToString("F1", Inv));
                nativeWriter.WriteLine(sb.ToString());
            }

            WriteNameRows(ts, up, seq);
        }

        private static void WriteNameRows(string ts, string up, long seq)
        {
            if (namesWriter == null || NativeProbe.HistUsed == 0)
            {
                return;
            }
            string type = NativeProbe.HistTypeResolved;
            int est = NativeProbe.HistEstimated ? 1 : 0;
            for (int rank = 0; rank < NativeProbe.HistUsed; rank++)
            {
                string name;
                long count;
                long dCount;
                if (!NativeProbe.HistRow(rank, out name, out count, out dCount))
                {
                    break;
                }
                sb.Length = 0;
                sb.Append(ts);
                sb.Append('\t').Append(up);
                sb.Append('\t').Append(seq);
                sb.Append('\t').Append(type);
                sb.Append('\t').Append(rank);
                sb.Append('\t').Append(Clean(name));
                sb.Append('\t').Append(count);
                sb.Append('\t').Append(dCount);
                sb.Append('\t').Append(est);
                namesWriter.WriteLine(sb.ToString());
            }
        }

        private static string Clean(string s)
        {
            if (string.IsNullOrEmpty(s))
            {
                return "(unnamed)";
            }
            if (s.IndexOf('\t') < 0 && s.IndexOf('\n') < 0 && s.IndexOf('\r') < 0)
            {
                return s;
            }
            return s.Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ');
        }

        private static void WritePoolRows(double uptime)
        {
            if (!PoolDetail || poolsWriter == null)
            {
                return;
            }
            int classes = PoolProbe.ClassCount;
            if (classes <= 0)
            {
                return;
            }
            if (poolClassBuf == null || poolClassBuf.Length != classes)
            {
                poolClassBuf = new int[classes];
            }

            string ts = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss", Inv);
            string up = uptime.ToString("F1", Inv);

            for (int i = 0; i < PoolProbe.PoolCount; i++)
            {
                long bytes;
                long hw;
                int arrays;
                if (!PoolProbe.CopyCounts(i, poolClassBuf, out bytes, out hw, out arrays))
                {
                    continue;
                }

                sb.Length = 0;
                sb.Append(ts);
                sb.Append('\t').Append(up);
                sb.Append('\t').Append(PoolProbe.PoolName(i));
                sb.Append('\t').Append(PoolProbe.ElemSize(i));
                sb.Append('\t').Append(arrays);
                sb.Append('\t').Append(bytes);
                sb.Append('\t').Append(hw);
                for (int k = 0; k < classes; k++)
                {
                    sb.Append('\t').Append(poolClassBuf[k]);
                }
                poolsWriter.WriteLine(sb.ToString());
            }
        }

        private static void AppendMb(long bytes)
        {
            sb.Append('\t').Append((bytes / 1048576.0).ToString("F1", Inv));
        }

        private static void WriteThreadRows(double uptime, double intervalSec)
        {
            string[] dirs;
            try
            {
                dirs = Directory.GetDirectories("/proc/self/task");
            }
            catch
            {
                return;
            }

            string ts = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss", Inv);
            string up = uptime.ToString("F1", Inv);
            string ivs = intervalSec.ToString("F2", Inv);

            for (int i = 0; i < dirs.Length; i++)
            {
                string d = dirs[i];
                int tid;
                string leaf = Path.GetFileName(d);
                if (!int.TryParse(leaf, out tid))
                {
                    continue;
                }

                long jiffies;
                if (!ReadThreadCpu(d, out jiffies))
                {
                    continue;
                }

                long prev;
                bool had = prevCpu.TryGetValue(tid, out prev);
                prevCpu[tid] = jiffies;
                if (!had)
                {
                    continue;
                }

                long delta = jiffies - prev;
                if (delta < 0L)
                {
                    delta = 0L;
                }
                double cpuMs = delta * 1000.0 / ClkTck;
                if (tid == MainTid)
                {
                    mainCpuMs = cpuMs;
                }
                if (cpuMs < 1.0)
                {
                    continue;
                }
                double pct = cpuMs / (intervalSec * 1000.0) * 100.0;

                string name = ReadComm(d);
                sb.Length = 0;
                sb.Append(ts).Append('\t').Append(up).Append('\t').Append(ivs);
                sb.Append('\t').Append(tid).Append('\t').Append(name);
                sb.Append('\t').Append(cpuMs.ToString("F1", Inv));
                sb.Append('\t').Append(pct.ToString("F2", Inv));
                threadsWriter.WriteLine(sb.ToString());
            }
        }

        private static bool ReadThreadCpu(string dir, out long jiffies)
        {
            jiffies = 0L;
            try
            {
                string s = File.ReadAllText(Path.Combine(dir, "stat"));
                int close = s.LastIndexOf(')');
                if (close < 0 || close + 2 >= s.Length)
                {
                    return false;
                }
                string[] f = s.Substring(close + 2).Split(' ');
                if (f.Length < 13)
                {
                    return false;
                }
                long u, k;
                if (!long.TryParse(f[11], out u) || !long.TryParse(f[12], out k))
                {
                    return false;
                }
                jiffies = u + k;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static string ReadComm(string dir)
        {
            try
            {
                return File.ReadAllText(Path.Combine(dir, "comm")).Trim().Replace('\t', ' ');
            }
            catch
            {
                return "?";
            }
        }

        private static void WriteSummary(double intervalSec, WorldState st)
        {
            long frames = callBuf[(int)Pr.GmUpdate];
            double workMs = (frames > 0L) ? (Ms((int)Pr.GmUpdate) / frames) : 0.0;
            double fps = (intervalSec > 0.0) ? (frames / intervalSec) : 0.0;
            double budgetPct = (st.TargetFps > 0) ? (workMs * st.TargetFps / 10.0) : 0.0;

            Say(string.Format(Inv,
                "work={0:F2}ms/frame ({1:F1}% of budget) fps={2:F1} frames={3}/{4:F0}s ply={5} zom={6} ent={7} chk={8} cgo={9} pathq={10} sdchunks={11} savebl={12} genq={13} heap={14}MB rss={15}MB",
                workMs, budgetPct, fps, frames, intervalSec, st.Players, st.Zombies, st.Entities, st.Chunks,
                st.ChunkGameObjects, st.PathQueue, st.SaveDirChunks, st.SaveBacklog, st.GenQueue,
                st.HeapBytes / 1048576L, st.RssBytes / 1048576L));

            Say("main ms/frame: " + TopString(0, Counters.MainProbeCount, frames));
            Say("worker ms/s:   " + TopString(Counters.MainProbeCount, (int)Pr.MAX, intervalSec));
            SayFrame(intervalSec);
            SayCull(intervalSec, st);
            Say(PoolProbe.SummaryLine(st.Pools));
            Say(PoolProbe.TopClassesLine(6));
            Say(NativeProbe.SummaryLine());
            Say(NativeProbe.TopLine());
            Say(NativeProbe.HistLine(10));
            Say(NativeProbe.BundleLine());
            Say(string.Format(Inv, "mem: heap={0}MB monoHeap={1}MB monoUsed={2}MB nativeRes={3}MB nativeAlloc={4}MB nativeUnused={5}MB rss={6}MB",
                st.HeapBytes / 1048576L, st.MonoHeapBytes / 1048576L, st.MonoUsedBytes / 1048576L,
                st.NativeReservedBytes / 1048576L, st.NativeAllocBytes / 1048576L, st.NativeUnusedBytes / 1048576L, st.RssBytes / 1048576L));
        }

        private static void SayFrame(double intervalSec)
        {
            long wn = callBuf[(int)Pr.FrameWall];
            long on = callBuf[(int)Pr.UpdateOuter];
            long gn = callBuf[(int)Pr.GmUpdate];
            if (wn <= 0L || on <= 0L || gn <= 0L)
            {
                return;
            }

            double wall = Ms((int)Pr.FrameWall) / wn;
            double outer = Ms((int)Pr.UpdateOuter) / on;
            double gm = Ms((int)Pr.GmUpdate) / gn;
            double late = (callBuf[(int)Pr.LateUpdate] > 0L) ? (Ms((int)Pr.LateUpdate) / callBuf[(int)Pr.LateUpdate]) : 0.0;
            double mbm = (callBuf[(int)Pr.MultiBlockMain] > 0L) ? (Ms((int)Pr.MultiBlockMain) / callBuf[(int)Pr.MultiBlockMain]) : 0.0;
            double postfix = outer - gm;
            double offUpdate = wall - outer - late;
            double cpuPerFrame = (on > 0L) ? (mainCpuMs / on) : 0.0;
            double idle = wall - cpuPerFrame;

            Say(string.Format(Inv,
                "frame: wall={0:F2}ms mainCpu={1:F2}ms idle={2:F2}ms | gmUpdate={3:F2} modPostfix={4:F2} lateUpdate={5:F2} offUpdate={6:F2} | mbMainThread={7:F3}ms x{8:F1}/frame",
                wall, cpuPerFrame, idle, gm, postfix, late, offUpdate,
                mbm, (double)callBuf[(int)Pr.MultiBlockMain] / gn));
        }

        private static void SayCull(double intervalSec, WorldState st)
        {
            long n = callBuf[(int)Pr.CullExpired];
            if (n <= 0L)
            {
                return;
            }
            double total = Ms((int)Pr.CullExpired);
            double wait = Ms((int)Pr.CullLockWait);
            double prot = Ms((int)Pr.UpdateProtection);
            double rem = Ms((int)Pr.RemoveChunks);
            double scan = total - wait - prot - rem;
            double keys = extraBuf[(int)Ex.CullKeys] / (double)n;
            double nsPerKey = (keys > 0.0) ? (scan / n) * 1e6 / keys : 0.0;

            Say(string.Format(Inv,
                "cull: {0:F2}ms/call x{1:F2}/s = {2:F0}ms/s | wait={3:F2} prot={4:F2} rem={5:F2} scan={6:F2} ({7:F0}ns/key over {8:F0} keys)",
                total / n, n / intervalSec, total / intervalSec,
                wait / n, prot / n, rem / n, scan / n, nsPerKey, keys));

            Say(string.Format(Inv,
                "cull ctx: protDirty={0}/{1} resetReq={2:F0} protLevels={3:F0} groups={4} groupedChunks={5} lockHeld={6:F1}% victims: isInSave={7:F1}/s getSync={8:F1}/s",
                extraBuf[(int)Ex.CullProtDirty], n,
                extraBuf[(int)Ex.CullResetReq] / (double)n, extraBuf[(int)Ex.CullProtLevels] / (double)n,
                st.Groups, st.GroupedChunks, 100.0 * total / (intervalSec * 1000.0),
                callBuf[(int)Pr.IsChunkInSave] / intervalSec, callBuf[(int)Pr.GetChunkSyncRfm] / intervalSec));
        }

        private static string TopString(int from, int to, double divisor)
        {
            int n = to - from;
            int[] idx = new int[n];
            for (int i = 0; i < n; i++)
            {
                idx[i] = from + i;
            }
            for (int i = 0; i < n - 1; i++)
            {
                for (int j = i + 1; j < n; j++)
                {
                    if (tickBuf[idx[j]] > tickBuf[idx[i]])
                    {
                        int t = idx[i];
                        idx[i] = idx[j];
                        idx[j] = t;
                    }
                }
            }

            StringBuilder o = new StringBuilder(256);
            double div = (divisor > 0.0) ? divisor : 1.0;
            int shown = 0;
            for (int i = 0; i < n && shown < 8; i++)
            {
                int k = idx[i];
                if (tickBuf[k] <= 0L)
                {
                    break;
                }
                if (shown > 0)
                {
                    o.Append("  ");
                }
                o.Append(Counters.Names[k]).Append('=');
                o.Append((Ms(k) / div).ToString("F3", Inv)).Append("ms");
                o.Append('/').Append((callBuf[k] / div).ToString("F1", Inv)).Append('n');
                shown++;
            }
            if (shown == 0)
            {
                o.Append("(idle)");
            }
            return o.ToString();
        }

        public static string Status()
        {
            StringBuilder o = new StringBuilder(256);
            o.Append("enabled=").Append(Counters.On);
            o.Append(" sampler=").Append(Running);
            o.Append(" interval=").Append(IntervalSec).Append("s");
            o.Append(" summaryEvery=").Append(SummaryEvery);
            o.Append(" rows=").Append(Rows);
            o.Append(" patched=").Append(PatchTable.Applied.Count);
            o.Append(" failed=").Append(PatchTable.Failed.Count);
            return o.ToString();
        }
    }
}
