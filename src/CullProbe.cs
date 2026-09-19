using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Threading;

namespace ProfLog
{
    public static class CullProbe
    {
        public static volatile bool LockProbe = true;
        public static RegionFileManager Rfm;

        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        private static RegionFileManager Resolve()
        {
            RegionFileManager r = Rfm;
            if (r != null)
            {
                return r;
            }
            try
            {
                r = StateReader.FindRegionFileManager() as RegionFileManager;
                if (r != null)
                {
                    Rfm = r;
                }
            }
            catch
            {
            }
            return r;
        }

        public static void Prefix(RegionFileManager __instance)
        {
            if (!Counters.On || __instance == null)
            {
                return;
            }
            Rfm = __instance;
            try
            {
                Dictionary<long, uint> csd = __instance.chunksInSaveDir;
                if (csd == null)
                {
                    return;
                }

                if (LockProbe && Monitor.IsEntered(__instance.saveLock))
                {
                    long t0 = Stopwatch.GetTimestamp();
                    Monitor.Enter(csd);
                    long t1 = Stopwatch.GetTimestamp();
                    try
                    {
                        Counters.AddExtra(Ex.CullKeys, csd.Count);
                        Counters.AddExtra(Ex.CullResetReq, __instance.resetRequestedChunks.Count);
                        Counters.AddExtra(Ex.CullProtLevels, __instance.chunkProtectionLevels.Count);
                        if (__instance.groupTimestampsDirty)
                        {
                            Counters.AddExtra(Ex.CullGroupDirty, 1L);
                        }
                        if (__instance.protectionLevelsDirty)
                        {
                            Counters.AddExtra(Ex.CullProtDirty, 1L);
                        }
                    }
                    finally
                    {
                        Monitor.Exit(csd);
                    }
                    Counters.AddTicks(Pr.CullLockWait, t1 - t0);
                }
            }
            catch
            {
            }
        }

        public static int Groups()
        {
            try
            {
                RegionFileManager r = Resolve();
                return (r != null && r.chunkGroups != null) ? r.chunkGroups.Count : -1;
            }
            catch
            {
                return -1;
            }
        }

        public static int GroupedChunks()
        {
            try
            {
                RegionFileManager r = Resolve();
                return (r != null && r.chunkGroups != null) ? r.chunkGroups.GroupedLongsCount : -1;
            }
            catch
            {
                return -1;
            }
        }

        public static int ResetRequests()
        {
            try
            {
                RegionFileManager r = Resolve();
                return (r != null && r.resetRequestedChunks != null) ? r.resetRequestedChunks.Count : -1;
            }
            catch
            {
                return -1;
            }
        }

        public static int ProtLevels()
        {
            try
            {
                RegionFileManager r = Resolve();
                return (r != null && r.chunkProtectionLevels != null) ? r.chunkProtectionLevels.Count : -1;
            }
            catch
            {
                return -1;
            }
        }

        private static double MsOf(long ticks)
        {
            return ticks * Counters.TicksToMs;
        }

        private static double Best(Func<int> f, int iters, out int result)
        {
            result = 0;
            double best = double.MaxValue;
            for (int i = 0; i < iters; i++)
            {
                long t0 = Stopwatch.GetTimestamp();
                result = f();
                double ms = MsOf(Stopwatch.GetTimestamp() - t0);
                if (ms < best)
                {
                    best = ms;
                }
            }
            return best;
        }

        private static double Locked(Func<int> f, int iters, out int result)
        {
            RegionFileManager r = Resolve();
            lock (r.saveLock)
            {
                lock (r.chunksInSaveDir)
                {
                    return Best(f, iters, out result);
                }
            }
        }

        public static string Bench(int iters)
        {
            RegionFileManager r = Resolve();
            if (r == null)
            {
                return "RegionFileManager not reachable, is the world loaded?";
            }
            if (iters < 1)
            {
                iters = 1;
            }
            if (iters > 10)
            {
                iters = 10;
            }

            Dictionary<long, uint> csd = r.chunksInSaveDir;
            List<long> resetList = r.resetRequestedChunks;
            Dictionary<long, ChunkProtectionLevel> prot = r.chunkProtectionLevels;
            LongSetGroups groups = r.chunkGroups;
            Dictionary<LongSetGroups.Group, uint> gts = r.groupTimestamps;
            long maxAge = r.maxChunkAge;
            uint now = GameUtils.WorldTimeToTotalMinutes(GameManager.Instance.World.worldTime);

            HashSet<long> resetSet = new HashSet<long>();
            for (int i = 0; i < resetList.Count; i++)
            {
                resetSet.Add(resetList[i]);
            }

            List<long> sink = new List<long>(10000);
            int keys = csd.Count;

            int rawN;
            double raw = Locked(delegate
            {
                long acc = 0L;
                foreach (KeyValuePair<long, uint> kv in csd)
                {
                    acc += kv.Key ^ kv.Value;
                }
                return (int)(acc & 0xFF);
            }, iters, out rawN);

            int v0N;
            double v0 = Locked(delegate
            {
                sink.Clear();
                foreach (long key in csd.Keys)
                {
                    if (!resetList.Contains(key))
                    {
                        uint ts = r.GetChunkTimestamp(key);
                        if (now - ts <= maxAge)
                        {
                            continue;
                        }
                    }
                    ChunkProtectionLevel pl;
                    if (prot.TryGetValue(key, out pl))
                    {
                        continue;
                    }
                    sink.Add(key);
                    if (sink.Count < 10000)
                    {
                        continue;
                    }
                    break;
                }
                return sink.Count;
            }, iters, out v0N);

            int v1N;
            double v1 = Locked(delegate
            {
                sink.Clear();
                foreach (long key in csd.Keys)
                {
                    if (!resetSet.Contains(key))
                    {
                        uint ts = r.GetChunkTimestamp(key);
                        if (now - ts <= maxAge)
                        {
                            continue;
                        }
                    }
                    ChunkProtectionLevel pl;
                    if (prot.TryGetValue(key, out pl))
                    {
                        continue;
                    }
                    sink.Add(key);
                    if (sink.Count < 10000)
                    {
                        continue;
                    }
                    break;
                }
                return sink.Count;
            }, iters, out v1N);

            int v2N;
            bool anyGroups = groups != null && groups.GroupedLongsCount > 0;
            double v2 = Locked(delegate
            {
                sink.Clear();
                foreach (KeyValuePair<long, uint> kv in csd)
                {
                    long key = kv.Key;
                    if (resetSet.Count == 0 || !resetSet.Contains(key))
                    {
                        uint ts = kv.Value;
                        if (anyGroups)
                        {
                            LongSetGroups.Group g;
                            uint gv;
                            if (groups.TryGetGroup(key, out g) && gts.TryGetValue(g, out gv))
                            {
                                ts = gv;
                            }
                        }
                        if (now - ts <= maxAge)
                        {
                            continue;
                        }
                    }
                    ChunkProtectionLevel pl;
                    if (prot.TryGetValue(key, out pl))
                    {
                        continue;
                    }
                    sink.Add(key);
                    if (sink.Count < 10000)
                    {
                        continue;
                    }
                    break;
                }
                return sink.Count;
            }, iters, out v2N);

            uint minTs = 0u;
            int v3N;
            double v3 = Locked(delegate
            {
                sink.Clear();
                uint mn = uint.MaxValue;
                foreach (KeyValuePair<long, uint> kv in csd)
                {
                    long key = kv.Key;
                    uint ts = kv.Value;
                    if (anyGroups)
                    {
                        LongSetGroups.Group g;
                        uint gv;
                        if (groups.TryGetGroup(key, out g) && gts.TryGetValue(g, out gv))
                        {
                            ts = gv;
                        }
                    }
                    if (resetSet.Count == 0 || !resetSet.Contains(key))
                    {
                        if (now - ts <= maxAge)
                        {
                            if (ts < mn)
                            {
                                mn = ts;
                            }
                            continue;
                        }
                    }
                    ChunkProtectionLevel pl;
                    if (prot.TryGetValue(key, out pl))
                    {
                        if (ts < mn)
                        {
                            mn = ts;
                        }
                        continue;
                    }
                    sink.Add(key);
                    if (sink.Count < 10000)
                    {
                        continue;
                    }
                    break;
                }
                minTs = mn;
                return sink.Count;
            }, iters, out v3N);

            int ugtN;
            double ugt = Locked(delegate
            {
                int c = 0;
                foreach (LongSetGroups.Group g in groups.Groups)
                {
                    uint m = 0u;
                    bool has = false;
                    foreach (long k in g.Keys)
                    {
                        uint v;
                        if (csd.TryGetValue(k, out v) && (!has || m < v))
                        {
                            m = v;
                            has = true;
                        }
                    }
                    if (has)
                    {
                        c++;
                    }
                }
                return c;
            }, iters, out ugtN);

            int gtsN;
            double gtsBench = Locked(delegate
            {
                int c = 0;
                uint v;
                foreach (LongSetGroups.Group g in groups.Groups)
                {
                    for (int i = 0; i < 64; i++)
                    {
                        if (gts.TryGetValue(g, out v))
                        {
                            c++;
                        }
                    }
                }
                return c;
            }, iters, out gtsN);

            StringBuilder o = new StringBuilder(1400);
            o.Append("cullbench iters=").Append(iters).Append(" keys=").Append(keys);
            o.Append(" groups=").Append(groups != null ? groups.Count : -1);
            o.Append(" groupedChunks=").Append(groups != null ? groups.GroupedLongsCount : -1);
            o.Append(" resetReq=").Append(resetList.Count);
            o.Append(" protLevels=").Append(prot.Count);
            o.Append(" maxChunkAgeMin=").Append(maxAge);
            o.Append(" nowMin=").Append(now).Append('\n');
            o.Append(string.Format(Inv, "  raw iterate            {0,8:F2} ms   ({1:F0} ns/key)\n", raw, raw * 1e6 / Math.Max(1, keys)));
            o.Append(string.Format(Inv, "  V0 vanilla replica     {0,8:F2} ms   ({1:F0} ns/key)  expired={2}\n", v0, v0 * 1e6 / Math.Max(1, keys), v0N));
            o.Append(string.Format(Inv, "  V1 +HashSet resetReq   {0,8:F2} ms   ({1:F0} ns/key)  expired={2}\n", v1, v1 * 1e6 / Math.Max(1, keys), v1N));
            o.Append(string.Format(Inv, "  V2 +KVP no relookup    {0,8:F2} ms   ({1:F0} ns/key)  expired={2}\n", v2, v2 * 1e6 / Math.Max(1, keys), v2N));
            o.Append(string.Format(Inv, "  V3 +min-deadline       {0,8:F2} ms   ({1:F0} ns/key)  expired={2} minTs={3}\n", v3, v3 * 1e6 / Math.Max(1, keys), v3N, minTs));
            o.Append(string.Format(Inv, "  UpdateGroupTimestamps  {0,8:F2} ms   ({1} groups)\n", ugt, ugtN));
            long refLookups = (long)(groups != null ? groups.Count : 0) * 64L;
            o.Append(string.Format(Inv, "  Dict<Group,uint> hash  {0,8:F2} ms   ({1:F1} ns/lookup over {2} lookups)\n", gtsBench, gtsBench * 1e6 / Math.Max(1L, refLookups), refLookups));
            if (minTs != 0u && minTs != uint.MaxValue && maxAge >= 0L)
            {
                long dueIn = (long)minTs + maxAge - now;
                o.Append(string.Format(Inv, "  earliest expiry in     {0} game minutes\n", dueIn));
            }
            o.Append("  measured CullExpired mean is in the probe TSV; V0 is what the game actually runs");
            return o.ToString();
        }

        public static string Snapshot()
        {
            RegionFileManager r = Resolve();
            if (r == null)
            {
                return "RegionFileManager not reachable, is the world loaded?";
            }
            StringBuilder o = new StringBuilder(400);
            try
            {
                o.Append("chunksInSaveDir=").Append(r.chunksInSaveDir.Count);
                o.Append(" resetRequested=").Append(r.resetRequestedChunks.Count);
                o.Append(" chunkProtectionLevels=").Append(r.chunkProtectionLevels.Count);
                o.Append(" groups=").Append(r.chunkGroups.Count);
                o.Append(" groupedChunks=").Append(r.chunkGroups.GroupedLongsCount);
                o.Append(" groupTimestamps=").Append(r.groupTimestamps.Count);
                o.Append(" protectionLevelsDirty=").Append(r.protectionLevelsDirty);
                o.Append(" groupTimestampsDirty=").Append(r.groupTimestampsDirty);
                o.Append(" maxChunkAgeMin=").Append(r.maxChunkAge);
                o.Append(" maxBytes=").Append(r.MaxBytes);
                o.Append(" lockProbe=").Append(LockProbe);
            }
            catch (Exception ex)
            {
                o.Append(" [error: ").Append(ex.Message).Append(']');
            }
            return o.ToString();
        }
    }
}
