using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using HarmonyLib;
using UnityEngine;

namespace ProfLog
{
    public struct WorldState
    {
        public int Players;
        public int Zombies;
        public int Entities;
        public int Chunks;
        public int ChunkGameObjects;
        public int PathQueue;
        public int PathFinished;
        public int SaveDirChunks;
        public int SaveBacklog;
        public int GenQueue;
        public int Observers;
        public int TargetFps;
        public long HeapBytes;
        public long RssBytes;
        public int Gc0;
        public int Gc1;
        public int Gc2;
        public int Day;
        public int Hour;
        public int Groups;
        public int GroupedChunks;
        public int ResetRequests;
        public int ProtLevels;
        public PoolState Pools;
        public long MonoHeapBytes;
        public long MonoUsedBytes;
        public long NativeReservedBytes;
        public long NativeAllocBytes;
        public long NativeUnusedBytes;
    }

    public static class StateReader
    {
        private const BindingFlags AllFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

        private static readonly Dictionary<string, MemberInfo> memberCache = new Dictionary<string, MemberInfo>(64);
        private static readonly object cacheLock = new object();
        private static readonly List<string> missing = new List<string>();
        private static int pageSize = 4096;

        private static MemberInfo Resolve(Type t, string name)
        {
            return Resolve(t, name, true);
        }

        private static MemberInfo Resolve(Type t, string name, bool record)
        {
            string key = t.FullName + "|" + name;
            lock (cacheLock)
            {
                MemberInfo cached;
                if (memberCache.TryGetValue(key, out cached))
                {
                    return cached;
                }

                MemberInfo found = null;
                Type cur = t;
                while (cur != null && found == null)
                {
                    found = cur.GetField(name, AllFlags);
                    if (found == null)
                    {
                        found = cur.GetProperty(name, AllFlags);
                    }
                    cur = cur.BaseType;
                }

                memberCache[key] = found;
                if (found == null && record && !missing.Contains(key))
                {
                    missing.Add(key);
                }
                return found;
            }
        }

        private static object Get(object obj, string name)
        {
            return Get(obj, name, true);
        }

        private static object Get(object obj, string name, bool record)
        {
            if (obj == null)
            {
                return null;
            }
            try
            {
                MemberInfo m = Resolve(obj.GetType(), name, record);
                FieldInfo f = m as FieldInfo;
                if (f != null)
                {
                    return f.GetValue(obj);
                }
                PropertyInfo p = m as PropertyInfo;
                if (p != null && p.CanRead)
                {
                    return p.GetValue(obj, null);
                }
            }
            catch
            {
            }
            return null;
        }

        private static int CountOf(object o)
        {
            if (o == null)
            {
                return -1;
            }
            try
            {
                ICollection c = o as ICollection;
                if (c != null)
                {
                    return c.Count;
                }
                object n = Get(o, "Count", false);
                if (n is int)
                {
                    return (int)n;
                }
                object list = Get(o, "list", false);
                if (list != null)
                {
                    ICollection lc = list as ICollection;
                    if (lc != null)
                    {
                        return lc.Count;
                    }
                }
            }
            catch
            {
            }
            return -1;
        }

        public static object FindRegionFileManager()
        {
            try
            {
                GameManager gm = GameManager.Instance;
                if (gm == null || gm.World == null || gm.World.ChunkCache == null)
                {
                    return null;
                }
                object provider = Get(gm.World.ChunkCache, "ChunkProvider", false);
                if (provider == null)
                {
                    return null;
                }
                return Get(provider, "m_RegionFileManager", false);
            }
            catch
            {
                return null;
            }
        }

        public static string MissingMembers()
        {
            lock (cacheLock)
            {
                if (missing.Count == 0)
                {
                    return "none";
                }
                return string.Join(", ", missing.ToArray());
            }
        }

        public static long ReadRss()
        {
            try
            {
                string s = File.ReadAllText("/proc/self/statm");
                string[] parts = s.Split(' ');
                if (parts.Length > 1)
                {
                    long pages;
                    if (long.TryParse(parts[1], out pages))
                    {
                        return pages * pageSize;
                    }
                }
            }
            catch
            {
            }
            return -1L;
        }

        public static WorldState Read()
        {
            WorldState s = default(WorldState);
            s.Players = -1;
            s.Zombies = -1;
            s.Entities = -1;
            s.Chunks = -1;
            s.ChunkGameObjects = -1;
            s.PathQueue = -1;
            s.PathFinished = -1;
            s.SaveDirChunks = -1;
            s.SaveBacklog = -1;
            s.GenQueue = -1;
            s.Observers = -1;
            s.TargetFps = -1;
            s.Day = -1;
            s.Hour = -1;
            s.Groups = CullProbe.Groups();
            s.GroupedChunks = CullProbe.GroupedChunks();
            s.ResetRequests = CullProbe.ResetRequests();
            s.ProtLevels = CullProbe.ProtLevels();
            s.Pools = PoolProbe.Read();

            try
            {
                s.HeapBytes = GC.GetTotalMemory(false);
                s.Gc0 = GC.CollectionCount(0);
                s.Gc1 = GC.CollectionCount(1);
                s.Gc2 = GC.CollectionCount(2);
                s.RssBytes = ReadRss();
                s.MonoHeapBytes = UnityEngine.Profiling.Profiler.GetMonoHeapSizeLong();
                s.MonoUsedBytes = UnityEngine.Profiling.Profiler.GetMonoUsedSizeLong();
                s.NativeReservedBytes = UnityEngine.Profiling.Profiler.GetTotalReservedMemoryLong();
                s.NativeAllocBytes = UnityEngine.Profiling.Profiler.GetTotalAllocatedMemoryLong();
                s.NativeUnusedBytes = UnityEngine.Profiling.Profiler.GetTotalUnusedReservedMemoryLong();
                s.TargetFps = Application.targetFrameRate;
            }
            catch
            {
            }

            GameManager gm = GameManager.Instance;
            if (gm == null)
            {
                return s;
            }

            World w = gm.World;
            if (w == null)
            {
                return s;
            }

            try
            {
                if (w.Players != null)
                {
                    s.Players = w.Players.Count;
                }
                if (w.Entities != null)
                {
                    s.Entities = w.Entities.Count;
                }
                s.Zombies = GameStats.GetInt(EnumGameStats.EnemyCount);
                ulong wt = w.worldTime;
                s.Day = (int)(wt / 24000UL) + 1;
                s.Hour = (int)(wt % 24000UL / 1000UL);
            }
            catch
            {
            }

            try
            {
                object cache = w.ChunkCache;
                if (cache != null)
                {
                    s.Chunks = CountOf(Get(cache, "chunks"));
                    if (s.Chunks < 0)
                    {
                        s.Chunks = CountOf(Get(cache, "chunkKeys"));
                    }

                    object provider = Get(cache, "ChunkProvider");
                    if (provider != null)
                    {
                        s.GenQueue = CountOf(Get(provider, "m_ChunkQueue"));

                        object rfm = Get(provider, "m_RegionFileManager");
                        if (rfm != null)
                        {
                            s.SaveDirChunks = CountOf(Get(rfm, "chunksInSaveDir"));
                            int a = CountOf(Get(rfm, "chunksToSave"));
                            int b = CountOf(Get(rfm, "chunkMemoryStreamsToSave"));
                            s.SaveBacklog = ((a < 0) ? 0 : a) + ((b < 0) ? 0 : b);
                        }
                    }
                }
            }
            catch
            {
            }

            try
            {
                object cm = w.m_ChunkManager;
                if (cm != null)
                {
                    s.ChunkGameObjects = CountOf(Get(cm, "m_UsedChunkGameObjects"));
                    s.Observers = CountOf(Get(cm, "m_ObservedEntities"));
                }
            }
            catch
            {
            }

            try
            {
                GamePath.PathFinderThread pf = GamePath.PathFinderThread.Instance;
                if (pf != null)
                {
                    s.PathQueue = pf.GetQueueCount();
                    s.PathFinished = pf.GetFinishedCount();
                }
            }
            catch
            {
            }

            return s;
        }

        public static string Environment()
        {
            StringBuilder sb = new StringBuilder(512);
            try
            {
                sb.Append("processorCount=").Append(SystemInfo.processorCount);
                sb.Append(" targetFrameRate=").Append(Application.targetFrameRate);
                sb.Append(" stopwatchFreq=").Append(System.Diagnostics.Stopwatch.Frequency);
                sb.Append(" isHighRes=").Append(System.Diagnostics.Stopwatch.IsHighResolution);
            }
            catch
            {
            }

            try
            {
                Type ju = AccessTools.TypeByName("Unity.Jobs.LowLevel.Unsafe.JobsUtility");
                if (ju != null)
                {
                    PropertyInfo p = AccessTools.Property(ju, "JobWorkerCount");
                    if (p != null)
                    {
                        sb.Append(" jobWorkerCount=").Append(p.GetValue(null, null));
                    }
                    PropertyInfo pm = AccessTools.Property(ju, "JobWorkerMaximumCount");
                    if (pm != null)
                    {
                        sb.Append(" jobWorkerMax=").Append(pm.GetValue(null, null));
                    }
                }
            }
            catch
            {
            }

            try
            {
                Type ap = AccessTools.TypeByName("AstarPath");
                if (ap != null)
                {
                    FieldInfo active = AccessTools.Field(ap, "active");
                    object inst = (active != null) ? active.GetValue(null) : null;
                    if (inst != null)
                    {
                        FieldInfo tc = AccessTools.Field(ap, "threadCount");
                        if (tc != null)
                        {
                            sb.Append(" astarThreadCount=").Append(tc.GetValue(inst));
                        }
                        PropertyInfo mt = AccessTools.Property(ap, "IsUsingMultithreading");
                        if (mt != null)
                        {
                            sb.Append(" astarMultithreaded=").Append(mt.GetValue(inst, null));
                        }
                    }
                    else
                    {
                        sb.Append(" astar=inactive");
                    }
                }
            }
            catch
            {
            }

            try
            {
                object provider = null;
                if (GameManager.Instance != null && GameManager.Instance.World != null && GameManager.Instance.World.ChunkCache != null)
                {
                    provider = Get(GameManager.Instance.World.ChunkCache, "ChunkProvider");
                }
                sb.Append(" chunkProvider=").Append((provider != null) ? provider.GetType().Name : "none");
            }
            catch
            {
            }

            try
            {
                sb.Append(" MaxChunkAge=").Append(GamePrefs.GetInt(EnumGamePrefs.MaxChunkAge));
                sb.Append(" EnableMapRendering=").Append(GamePrefs.GetBool(EnumGamePrefs.EnableMapRendering));
                sb.Append(" DynamicMeshEnabled=").Append(GamePrefs.GetBool(EnumGamePrefs.DynamicMeshEnabled));
                sb.Append(" ViewDistance=").Append(GamePrefs.GetInt(EnumGamePrefs.ServerMaxAllowedViewDistance));
                sb.Append(" MaxSpawnedZombies=").Append(GamePrefs.GetInt(EnumGamePrefs.MaxSpawnedZombies));
                sb.Append(" MaxSpawnedAnimals=").Append(GamePrefs.GetInt(EnumGamePrefs.MaxSpawnedAnimals));
            }
            catch
            {
            }

            return sb.ToString();
        }
    }
}
