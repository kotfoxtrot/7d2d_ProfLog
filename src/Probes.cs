using System.Collections.Generic;

namespace ProfLog
{
    public static class Probes
    {
        public static void Pre(out long __state)
        {
            __state = Counters.On ? Counters.Now() : 0L;
        }

        public static void PostGmUpdate(long __state)
        {
            if (__state != 0L)
            {
                Counters.Add(Pr.GmUpdate, __state);
            }
        }

        public static void PostUpdateTick(long __state)
        {
            if (__state != 0L)
            {
                Counters.Add(Pr.UpdateTick, __state);
            }
        }

        public static void PostWorldTick(long __state)
        {
            if (__state != 0L)
            {
                Counters.Add(Pr.WorldTick, __state);
            }
        }

        public static void PostTickEntities(long __state)
        {
            if (__state != 0L)
            {
                Counters.Add(Pr.TickEntities, __state);
            }
        }

        public static void PostTickEntity(long __state)
        {
            if (__state != 0L)
            {
                Counters.Add(Pr.TickEntity, __state);
            }
        }

        public static void PostEntityActivity(long __state)
        {
            if (__state != 0L)
            {
                Counters.Add(Pr.EntityActivity, __state);
            }
        }

        public static void PostOnUpdateLive(long __state)
        {
            if (__state != 0L)
            {
                Counters.Add(Pr.OnUpdateLive, __state);
            }
        }

        public static void PostHumanUpdateLive(long __state)
        {
            if (__state != 0L)
            {
                Counters.Add(Pr.HumanUpdateLive, __state);
            }
        }

        public static void PostPlayerUpdateLive(long __state)
        {
            if (__state != 0L)
            {
                Counters.Add(Pr.PlayerUpdateLive, __state);
            }
        }

        public static void PostUpdateTasks(long __state)
        {
            if (__state != 0L)
            {
                Counters.Add(Pr.UpdateTasks, __state);
            }
        }

        public static void PostMoveHelper(long __state)
        {
            if (__state != 0L)
            {
                Counters.Add(Pr.MoveHelper, __state);
            }
        }

        public static void PostBuffsTick(long __state)
        {
            if (__state != 0L)
            {
                Counters.Add(Pr.BuffsTick, __state);
            }
        }

        public static void PostVoxelRaycast(long __state)
        {
            if (__state != 0L)
            {
                Counters.Add(Pr.VoxelRaycast, __state);
            }
        }

        public static void PostCanBeSeen(long __state)
        {
            if (__state != 0L)
            {
                Counters.Add(Pr.CanBeSeen, __state);
            }
        }

        public static void PostPathCalc(long __state)
        {
            if (__state != 0L)
            {
                Counters.Add(Pr.PathCalc, __state);
            }
        }

        public static void PostCopyChunks(long __state)
        {
            if (__state != 0L)
            {
                Counters.Add(Pr.CopyChunks, __state);
            }
        }

        public static void PostSendChunks(long __state)
        {
            if (__state != 0L)
            {
                Counters.Add(Pr.SendChunks, __state);
            }
        }

        public static void PostNetChunkSetup(long __state)
        {
            if (__state != 0L)
            {
                Counters.Add(Pr.NetChunkSetup, __state);
            }
        }

        public static void PostDetermineChunks(long __state)
        {
            if (__state != 0L)
            {
                Counters.Add(Pr.DetermineChunks, __state);
            }
        }

        public static void PostChunkTeTick(long __state)
        {
            if (__state != 0L)
            {
                Counters.Add(Pr.ChunkTeTick, __state);
            }
        }

        public static void PostBlockTicker(long __state)
        {
            if (__state != 0L)
            {
                Counters.Add(Pr.BlockTicker, __state);
            }
        }

        public static void PostLetBlocksFall(long __state)
        {
            if (__state != 0L)
            {
                Counters.Add(Pr.LetBlocksFall, __state);
            }
        }

        public static void PostSleeperTick(long __state)
        {
            if (__state != 0L)
            {
                Counters.Add(Pr.SleeperTick, __state);
            }
        }

        public static void PostAiDirector(long __state)
        {
            if (__state != 0L)
            {
                Counters.Add(Pr.AiDirector, __state);
            }
        }

        public static void PostPowerUpdate(long __state)
        {
            if (__state != 0L)
            {
                Counters.Add(Pr.PowerUpdate, __state);
            }
        }

        public static void PostMapRender(long __state)
        {
            if (__state != 0L)
            {
                Counters.Add(Pr.MapRender, __state);
            }
        }

        public static void PostStabilityStep(long __state)
        {
            if (__state != 0L)
            {
                Counters.Add(Pr.StabilityStep, __state);
            }
        }

        public static void PostNetDistrib(long __state)
        {
            if (__state != 0L)
            {
                Counters.Add(Pr.NetDistrib, __state);
            }
        }

        public static void PostProcessPkgs(long __state)
        {
            if (__state != 0L)
            {
                Counters.Add(Pr.ProcessPkgs, __state);
            }
        }

        public static void PostSaveSnapMain(long __state)
        {
            if (__state != 0L)
            {
                Counters.Add(Pr.SaveSnapMain, __state);
            }
        }

        public static void PostMainThreadTasks(long __state)
        {
            if (__state != 0L)
            {
                Counters.Add(Pr.MainThreadTasks, __state);
            }
        }

        public static void PostWaterSimUpdate(long __state)
        {
            if (__state != 0L)
            {
                Counters.Add(Pr.WaterSimUpdate, __state);
            }
        }

        public static void PostGroundAlign(long __state)
        {
            if (__state != 0L)
            {
                Counters.Add(Pr.GroundAlign, __state);
            }
        }

        public static void PostLateUpdate(long __state)
        {
            if (__state != 0L)
            {
                Counters.Add(Pr.LateUpdate, __state);
            }
        }

        public static void PostCullExpired(long __state)
        {
            if (__state != 0L)
            {
                Counters.Add(Pr.CullExpired, __state);
            }
        }

        public static void PostUpdateProtection(long __state)
        {
            if (__state != 0L)
            {
                Counters.Add(Pr.UpdateProtection, __state);
            }
        }

        public static void PostDoSaveChunks(long __state)
        {
            if (__state != 0L)
            {
                Counters.Add(Pr.DoSaveChunks, __state);
            }
        }

        public static void PostLightChunk(long __state)
        {
            if (__state != 0L)
            {
                Counters.Add(Pr.LightChunk, __state);
            }
        }

        public static void PostRegenChunk(long __state)
        {
            if (__state != 0L)
            {
                Counters.Add(Pr.RegenChunk, __state);
            }
        }

        public static void PostGenChunk(long __state)
        {
            if (__state != 0L)
            {
                Counters.Add(Pr.GenChunk, __state);
            }
        }

        public static void PostTakeSnapshot(long __state)
        {
            if (__state != 0L)
            {
                Counters.Add(Pr.TakeSnapshot, __state);
            }
        }

        public static void PostRemoveChunks(long __state, ICollection<long> _chunks)
        {
            if (__state != 0L)
            {
                Counters.Add(Pr.RemoveChunks, __state);
                if (_chunks != null)
                {
                    Counters.AddExtra(Ex.ChunksRemoved, _chunks.Count);
                }
            }
        }
    
        public static void PostAddGrouped(long __state)
        {
            if (__state != 0L)
            {
                Counters.Add(Pr.AddGrouped, __state);
            }
        }

        public static void PostRebuildGroups(long __state)
        {
            if (__state != 0L)
            {
                Counters.Add(Pr.RebuildGroups, __state);
            }
        }

        public static void PostOptimizeLayouts(long __state)
        {
            if (__state != 0L)
            {
                Counters.Add(Pr.OptimizeLayouts, __state);
            }
        }

        public static void PostIsChunkInSave(long __state)
        {
            if (__state != 0L)
            {
                Counters.Add(Pr.IsChunkInSave, __state);
            }
        }

        public static void PostGetChunkSyncRfm(long __state)
        {
            if (__state != 0L)
            {
                Counters.Add(Pr.GetChunkSyncRfm, __state);
            }
        }

        public static void PostMultiBlockMain(long __state)
        {
            if (__state != 0L)
            {
                Counters.Add(Pr.MultiBlockMain, __state);
            }
        }
    }
}
