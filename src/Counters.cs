using System.Diagnostics;
using System.Threading;

namespace ProfLog
{
    public enum Pr
    {
        FrameWall,
        UpdateOuter,
        GmUpdate,
        UpdateTick,
        WorldTick,
        TickEntities,
        TickEntity,
        EntityActivity,
        OnUpdateLive,
        HumanUpdateLive,
        PlayerUpdateLive,
        UpdateTasks,
        MoveHelper,
        BuffsTick,
        VoxelRaycast,
        CanBeSeen,
        PathCalc,
        CopyChunks,
        SendChunks,
        NetChunkSetup,
        DetermineChunks,
        ChunkTeTick,
        BlockTicker,
        LetBlocksFall,
        SleeperTick,
        AiDirector,
        MultiBlockMain,
        PowerUpdate,
        MapRender,
        StabilityStep,
        NetDistrib,
        ProcessPkgs,
        SaveSnapMain,
        MainThreadTasks,
        WaterSimUpdate,
        GroundAlign,
        LateUpdate,
        CullExpired,
        UpdateProtection,
        DoSaveChunks,
        LightChunk,
        RegenChunk,
        GenChunk,
        TakeSnapshot,
        RemoveChunks,
        AddGrouped,
        RebuildGroups,
        OptimizeLayouts,
        IsChunkInSave,
        GetChunkSyncRfm,
        CullLockWait,
        MAX
    }

    public enum Ex
    {
        ChunksRemoved,
        TeTicked,
        CullKeys,
        CullResetReq,
        CullProtLevels,
        CullProtDirty,
        MAX
    }

    public static class Counters
    {
        public static volatile bool On = true;

        public static readonly long[] Ticks = new long[(int)Pr.MAX];
        public static readonly long[] Calls = new long[(int)Pr.MAX];
        public static readonly long[] Extra = new long[(int)Ex.MAX];

        public static readonly double TicksToMs = 1000.0 / Stopwatch.Frequency;

        public static readonly long FrameWallCapTicks = Stopwatch.Frequency * 2L;

        public static readonly string[] Names = BuildNames();
        public static readonly string[] ExtraNames = BuildExtraNames();

        public static readonly int MainProbeCount = (int)Pr.CullExpired;

        private static string[] BuildNames()
        {
            string[] r = new string[(int)Pr.MAX];
            for (int i = 0; i < r.Length; i++)
            {
                r[i] = ((Pr)i).ToString();
            }
            return r;
        }

        private static string[] BuildExtraNames()
        {
            string[] r = new string[(int)Ex.MAX];
            for (int i = 0; i < r.Length; i++)
            {
                r[i] = ((Ex)i).ToString();
            }
            return r;
        }

        public static long Now()
        {
            return Stopwatch.GetTimestamp();
        }

        public static void Add(Pr p, long start)
        {
            long d = Stopwatch.GetTimestamp() - start;
            if (d < 0L)
            {
                d = 0L;
            }
            int i = (int)p;
            Interlocked.Add(ref Ticks[i], d);
            Interlocked.Increment(ref Calls[i]);
        }

        public static void AddTicks(Pr p, long ticks)
        {
            if (ticks < 0L)
            {
                ticks = 0L;
            }
            int i = (int)p;
            Interlocked.Add(ref Ticks[i], ticks);
            Interlocked.Increment(ref Calls[i]);
        }

        public static void AddExtra(Ex e, long v)
        {
            Interlocked.Add(ref Extra[(int)e], v);
        }

        public static void Snapshot(long[] ticksOut, long[] callsOut, long[] extraOut)
        {
            for (int i = 0; i < (int)Pr.MAX; i++)
            {
                ticksOut[i] = Interlocked.Exchange(ref Ticks[i], 0L);
                callsOut[i] = Interlocked.Exchange(ref Calls[i], 0L);
            }
            for (int i = 0; i < (int)Ex.MAX; i++)
            {
                extraOut[i] = Interlocked.Exchange(ref Extra[i], 0L);
            }
        }

        public static void Reset()
        {
            for (int i = 0; i < (int)Pr.MAX; i++)
            {
                Interlocked.Exchange(ref Ticks[i], 0L);
                Interlocked.Exchange(ref Calls[i], 0L);
            }
            for (int i = 0; i < (int)Ex.MAX; i++)
            {
                Interlocked.Exchange(ref Extra[i], 0L);
            }
        }
    }
}
