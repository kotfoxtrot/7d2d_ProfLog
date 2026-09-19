using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace ProfLog
{
    public struct Target
    {
        public Pr Probe;
        public string TypeName;
        public string MethodName;
        public Type[] Params;
        public bool Outermost;

        public Target(Pr probe, string typeName, string methodName)
        {
            Probe = probe;
            TypeName = typeName;
            MethodName = methodName;
            Params = null;
            Outermost = false;
        }

        public Target(Pr probe, string typeName, string methodName, Type[] paramTypes)
        {
            Probe = probe;
            TypeName = typeName;
            MethodName = methodName;
            Params = paramTypes;
            Outermost = false;
        }

        public Target(Pr probe, string typeName, string methodName, bool outermost)
        {
            Probe = probe;
            TypeName = typeName;
            MethodName = methodName;
            Params = null;
            Outermost = outermost;
        }
    }

    public static class PatchTable
    {
        public static readonly List<string> Applied = new List<string>();
        public static readonly List<string> Failed = new List<string>();

        private static readonly Target[] Table = new Target[]
        {
            new Target(Pr.GmUpdate, "GameManager", "gmUpdate"),
            new Target(Pr.UpdateTick, "GameManager", "UpdateTick"),
            new Target(Pr.LateUpdate, "GameManager", "LateUpdate"),
            new Target(Pr.WorldTick, "World", "OnUpdateTick"),
            new Target(Pr.TickEntities, "World", "TickEntities"),
            new Target(Pr.TickEntity, "World", "TickEntity"),
            new Target(Pr.EntityActivity, "World", "EntityActivityUpdate"),
            new Target(Pr.LetBlocksFall, "World", "LetBlocksFall"),
            new Target(Pr.SleeperTick, "World", "TickSleeperVolumes"),
            new Target(Pr.OnUpdateLive, "EntityAlive", "OnUpdateLive"),
            new Target(Pr.HumanUpdateLive, "EntityHuman", "OnUpdateLive"),
            new Target(Pr.PlayerUpdateLive, "EntityPlayer", "OnUpdateLive"),
            new Target(Pr.UpdateTasks, "EntityAlive", "updateTasks"),
            new Target(Pr.CanBeSeen, "EntityAlive", "CanEntityBeSeen"),
            new Target(Pr.MoveHelper, "EntityMoveHelper", "UpdateMoveHelper"),
            new Target(Pr.BuffsTick, "EntityBuffs", "Tick"),
            new Target(Pr.VoxelRaycast, "Voxel", "raycastNew"),
            new Target(Pr.PathCalc, "GamePath.ASPPathFinder", "Calculate"),
            new Target(Pr.CopyChunks, "ChunkManager", "CopyChunksToUnity"),
            new Target(Pr.SendChunks, "ChunkManager", "SendChunksToClients"),
            new Target(Pr.DetermineChunks, "ChunkManager", "DetermineChunksToLoad"),
            new Target(Pr.GroundAlign, "ChunkManager", "GroundAlignFrameUpdate"),
            new Target(Pr.RegenChunk, "ChunkManager", "RegenerateNextChunk"),
            new Target(Pr.NetChunkSetup, "NetPackageChunk", "Setup"),
            new Target(Pr.ChunkTeTick, "Chunk", "UpdateTick"),
            new Target(Pr.BlockTicker, "WorldBlockTicker", "Tick"),
            new Target(Pr.AiDirector, "AIDirector", "Tick"),
            new Target(Pr.PowerUpdate, "PowerManager", "Update"),
            new Target(Pr.MapRender, "MapRendering.MapRenderer", "RenderDirtyChunks"),
            new Target(Pr.StabilityStep, "StabilityCalculator+UpdatePhysics", "MoveNext"),
            new Target(Pr.NetDistrib, "NetEntityDistribution", "OnUpdateEntities"),
            new Target(Pr.ProcessPkgs, "ConnectionManager", "ProcessPackages"),
            new Target(Pr.MainThreadTasks, "ThreadManager", "UpdateMainThreadTasks"),
            new Target(Pr.WaterSimUpdate, "WaterSimulationNative", "Update"),
            new Target(Pr.SaveSnapMain, "RegionFileManager", "SaveChunkSnapshot"),
            new Target(Pr.CullExpired, "RegionFileManager", "CullExpiredChunks"),
            new Target(Pr.UpdateProtection, "RegionFileManager", "UpdateChunkProtectionLevels"),
            new Target(Pr.DoSaveChunks, "RegionFileManager", "DoSaveChunks"),
            new Target(Pr.RemoveChunks, "RegionFileManager", "RemoveChunks"),
            new Target(Pr.LightChunk, "ChunkCluster", "LightChunk"),
            new Target(Pr.GenChunk, "ChunkProviderGenerateWorld", "GenerateSingleChunk"),
            new Target(Pr.TakeSnapshot, "ChunkSnapshotUtil", "TakeSnapshot"),
            new Target(Pr.AddGrouped, "RegionFileManager", "AddGroupedChunks"),
            new Target(Pr.RebuildGroups, "RegionFileManager", "RebuildChunkGroupsFromPOIs"),
            new Target(Pr.OptimizeLayouts, "RegionFileAccessMultipleChunks", "OptimizeLayouts"),
            new Target(Pr.IsChunkInSave, "RegionFileManager", "isChunkInSaveDir"),
            new Target(Pr.GetChunkSyncRfm, "RegionFileManager", "GetChunkSync", new Type[] { typeof(long) }),
            new Target(Pr.MultiBlockMain, "MultiBlockManager", "MainThreadUpdate", true)
        };

        public static void Warmup()
        {
            int done = 0;
            try
            {
                long t = Counters.Now();
                MethodInfo[] all = typeof(Probes).GetMethods(BindingFlags.Public | BindingFlags.Static);
                object[] one = new object[1];
                for (int i = 0; i < all.Length; i++)
                {
                    MethodInfo m = all[i];
                    if (!m.Name.StartsWith("Post"))
                    {
                        continue;
                    }
                    ParameterInfo[] ps = m.GetParameters();
                    try
                    {
                        if (ps.Length == 1)
                        {
                            one[0] = t;
                            m.Invoke(null, one);
                            done++;
                        }
                        else if (ps.Length == 2)
                        {
                            m.Invoke(null, new object[] { t, null });
                            done++;
                        }
                    }
                    catch
                    {
                    }
                }
                try
                {
                    CullProbe.Prefix(null);
                    CullProbe.Snapshot();
                    done++;
                }
                catch
                {
                }
                try
                {
                    long fs;
                    FrameProbe.Pre(out fs);
                    FrameProbe.Post(fs);
                    FrameProbe.Pre(out fs);
                    FrameProbe.Post(fs);
                    FrameProbe.Reset();
                    done++;
                }
                catch
                {
                }
                Counters.Reset();
            }
            catch (Exception ex)
            {
                Log.Warning("[ProfLog] warmup failed: " + ex.Message);
            }
            Log.Out("[ProfLog] warmed up {0} probe methods", done);
        }

        private static void ApplyFrameProbe(Harmony harmony)
        {
            try
            {
                Type t = AccessTools.TypeByName("GameManager");
                MethodInfo target = (t != null) ? AccessTools.DeclaredMethod(t, "Update") : null;
                MethodInfo pre = AccessTools.Method(typeof(FrameProbe), "Pre");
                MethodInfo post = AccessTools.Method(typeof(FrameProbe), "Post");
                if (target == null || pre == null || post == null)
                {
                    Failed.Add("GameManager.Update [frame probe target missing]");
                    return;
                }
                HarmonyMethod hpre = new HarmonyMethod(pre);
                hpre.priority = Priority.First;
                HarmonyMethod hpost = new HarmonyMethod(post);
                hpost.priority = Priority.Last;
                harmony.Patch(target, hpre, hpost);
                Applied.Add("UpdateOuter+FrameWall -> GameManager.Update [outermost]");
            }
            catch (Exception ex)
            {
                Failed.Add("GameManager.Update [frame probe " + ex.GetType().Name + ": " + ex.Message + "]");
            }
        }

        private static void ApplyCullProbe(Harmony harmony)
        {
            try
            {
                Type t = AccessTools.TypeByName("RegionFileManager");
                MethodInfo target = (t != null) ? AccessTools.Method(t, "CullExpiredChunks") : null;
                MethodInfo pre = AccessTools.Method(typeof(CullProbe), "Prefix");
                if (target == null || pre == null)
                {
                    Failed.Add("RegionFileManager.CullExpiredChunks [cull probe target missing]");
                    return;
                }
                HarmonyMethod hm = new HarmonyMethod(pre);
                hm.priority = Priority.Last;
                harmony.Patch(target, hm);
                Applied.Add("CullLockWait -> RegionFileManager.CullExpiredChunks [prefix, priority Last]");
            }
            catch (Exception ex)
            {
                Failed.Add("RegionFileManager.CullExpiredChunks [cull probe " + ex.GetType().Name + ": " + ex.Message + "]");
            }
        }

        public static void ApplyAll(Harmony harmony)
        {
            MethodInfo pre = AccessTools.Method(typeof(Probes), "Pre");
            if (pre == null)
            {
                Log.Error("[ProfLog] Pre method not found, aborting patch phase");
                return;
            }

            for (int i = 0; i < Table.Length; i++)
            {
                Target t = Table[i];
                string label = t.TypeName + "." + t.MethodName;
                try
                {
                    Type type = AccessTools.TypeByName(t.TypeName);
                    if (type == null)
                    {
                        Failed.Add(label + " [type not found]");
                        continue;
                    }

                    MethodInfo target = (t.Params != null)
                        ? AccessTools.DeclaredMethod(type, t.MethodName, t.Params)
                        : AccessTools.Method(type, t.MethodName);
                    if (target == null)
                    {
                        Failed.Add(label + " [method not found]");
                        continue;
                    }

                    if (target.IsAbstract || target.ContainsGenericParameters)
                    {
                        Failed.Add(label + " [not patchable]");
                        continue;
                    }

                    MethodInfo post = AccessTools.Method(typeof(Probes), "Post" + t.Probe.ToString());
                    if (post == null)
                    {
                        Failed.Add(label + " [postfix missing]");
                        continue;
                    }

                    HarmonyMethod hpre = new HarmonyMethod(pre);
                    HarmonyMethod hpost = new HarmonyMethod(post);
                    if (t.Outermost)
                    {
                        hpre.priority = Priority.First;
                        hpost.priority = Priority.Last;
                    }
                    harmony.Patch(target, hpre, hpost);
                    Applied.Add(t.Probe.ToString() + " -> " + label + (t.Outermost ? " [outermost]" : ""));
                }
                catch (Exception ex)
                {
                    Failed.Add(label + " [" + ex.GetType().Name + ": " + ex.Message + "]");
                }
            }

            ApplyFrameProbe(harmony);
            ApplyCullProbe(harmony);
            Warmup();

            Log.Out("[ProfLog] Patched {0} of {1} call sites", Applied.Count, Table.Length + 2);
            for (int i = 0; i < Failed.Count; i++)
            {
                Log.Warning("[ProfLog] NOT patched: " + Failed[i]);
            }
        }
    }
}
