using System;
using System.Globalization;
using System.Reflection;
using System.Text;

namespace ProfLog
{
    public struct PoolState
    {
        public long ArraysBytes;
        public long ArraysHwBytes;
        public long CblCacheBytes;
        public long V2Bytes;
        public long V3Bytes;
        public long V4Bytes;
        public long ColBytes;
        public long IntBytes;
        public long U16Bytes;
        public long F32Bytes;
        public long ByteBytes;
        public int PoolChunks;
        public int PoolCbl;
        public int PoolCbc;
        public int PoolCgl;
        public int PoolVml;
        public int PoolMs;
        public int PoolObjects;
        public int InstChunk;
        public int InstCbl;
        public int InstCbc;
        public int InstCgl;
        public int InstVml;
        public int InstMs;
    }

    public static class PoolProbe
    {
        private sealed class ArrayPool
        {
            public string Name;
            public object Instance;
            public int ElemSize;
            public FieldInfo PoolSize;
            public MethodInfo ElementsCount;
        }

        private static readonly string[] FieldNames =
        {
            "poolVector2", "poolVector3", "poolVector4", "poolColor",
            "poolInt", "poolUInt16", "poolFloat", "poolByte"
        };

        private static readonly string[] ShortNames =
        {
            "v2", "v3", "v4", "col", "int", "u16", "f32", "byte"
        };

        private static readonly int[] ElemSizes = { 8, 12, 16, 16, 4, 2, 4, 1 };

        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        private static readonly object stateLock = new object();

        private static ArrayPool[] arrayPools;
        private static int[] sizeClasses;
        private static int[][] classCounts;
        private static long[] typeBytes;
        private static long[] typeHwBytes;
        private static bool initFailed;

        public static int PoolCount
        {
            get { return (arrayPools != null) ? arrayPools.Length : 0; }
        }

        public static int ClassCount
        {
            get { return (sizeClasses != null) ? sizeClasses.Length : 0; }
        }

        public static string PoolName(int i)
        {
            return (arrayPools != null && i >= 0 && i < arrayPools.Length) ? arrayPools[i].Name : "?";
        }

        public static int ElemSize(int i)
        {
            return (arrayPools != null && i >= 0 && i < arrayPools.Length) ? arrayPools[i].ElemSize : 0;
        }

        public static int SizeClass(int k)
        {
            return (sizeClasses != null && k >= 0 && k < sizeClasses.Length) ? sizeClasses[k] : 0;
        }

        public static void Init()
        {
            EnsureInit();
        }

        private static void EnsureInit()
        {
            if (arrayPools != null || initFailed)
            {
                return;
            }
            lock (stateLock)
            {
                if (arrayPools != null || initFailed)
                {
                    return;
                }
                try
                {
                    Type mp = typeof(MemoryPools);
                    ArrayPool[] list = new ArrayPool[FieldNames.Length];
                    for (int i = 0; i < FieldNames.Length; i++)
                    {
                        FieldInfo f = mp.GetField(FieldNames[i], BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                        object inst = (f != null) ? f.GetValue(null) : null;
                        ArrayPool p = new ArrayPool();
                        p.Name = ShortNames[i];
                        p.Instance = inst;
                        p.ElemSize = ElemSizes[i];
                        if (inst != null)
                        {
                            Type t = inst.GetType();
                            p.PoolSize = t.GetField("poolSize", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                            p.ElementsCount = t.GetMethod("GetElementsCount", BindingFlags.Public | BindingFlags.Instance);
                        }
                        list[i] = p;
                    }
                    sizeClasses = MemoryPooledArraySizes.poolElements;
                    classCounts = new int[list.Length][];
                    for (int i = 0; i < list.Length; i++)
                    {
                        classCounts[i] = new int[sizeClasses.Length];
                    }
                    typeBytes = new long[list.Length];
                    typeHwBytes = new long[list.Length];
                    arrayPools = list;
                }
                catch
                {
                    initFailed = true;
                }
            }
        }

        private static void Capture(int idx)
        {
            ArrayPool p = arrayPools[idx];
            int[] dst = classCounts[idx];
            Array.Clear(dst, 0, dst.Length);
            typeBytes[idx] = 0L;
            typeHwBytes[idx] = 0L;
            if (p == null || p.Instance == null)
            {
                return;
            }
            try
            {
                if (p.ElementsCount != null)
                {
                    object v = p.ElementsCount.Invoke(p.Instance, null);
                    if (v is long)
                    {
                        typeHwBytes[idx] = (long)v * p.ElemSize;
                    }
                }
            }
            catch
            {
            }
            try
            {
                if (p.PoolSize == null)
                {
                    typeBytes[idx] = typeHwBytes[idx];
                    return;
                }
                int[] sizes = p.PoolSize.GetValue(p.Instance) as int[];
                if (sizes == null)
                {
                    typeBytes[idx] = typeHwBytes[idx];
                    return;
                }
                long elems = 0L;
                int n = (sizes.Length < dst.Length) ? sizes.Length : dst.Length;
                for (int k = 0; k < n; k++)
                {
                    int c = sizes[k];
                    if (c < 0)
                    {
                        c = 0;
                    }
                    dst[k] = c;
                    elems += (long)c * sizeClasses[k];
                }
                typeBytes[idx] = elems * p.ElemSize;
            }
            catch
            {
                typeBytes[idx] = typeHwBytes[idx];
            }
        }

        public static PoolState Read()
        {
            PoolState s = default(PoolState);
            EnsureInit();
            if (arrayPools == null)
            {
                return s;
            }

            lock (stateLock)
            {
                for (int i = 0; i < arrayPools.Length; i++)
                {
                    Capture(i);
                    s.ArraysBytes += typeBytes[i];
                    s.ArraysHwBytes += typeHwBytes[i];
                }

                s.V2Bytes = typeBytes[0];
                s.V3Bytes = typeBytes[1];
                s.V4Bytes = typeBytes[2];
                s.ColBytes = typeBytes[3];
                s.IntBytes = typeBytes[4];
                s.U16Bytes = typeBytes[5];
                s.F32Bytes = typeBytes[6];
                s.ByteBytes = typeBytes[7];
            }

            try
            {
                s.CblCacheBytes = (long)MemoryPools.poolCBLUpper24BitArrCache.Count * 3072L
                    + (long)MemoryPools.poolCBLLower8BitArrCache.Count * 1024L;
            }
            catch
            {
            }

            try
            {
                s.PoolChunks = MemoryPools.PoolChunks.GetPoolSize();
                s.PoolCbl = MemoryPools.poolCBL.GetPoolSize();
                s.PoolCbc = MemoryPools.poolCBC.GetPoolSize();
                s.PoolCgl = MemoryPools.poolCGOL.GetPoolSize();
                s.PoolVml = MemoryPools.poolVML.GetPoolSize();
                s.PoolMs = MemoryPools.poolMS.GetPoolSize();
                s.PoolObjects = s.PoolChunks + s.PoolCbl + s.PoolCbc + s.PoolCgl + s.PoolVml + s.PoolMs;
            }
            catch
            {
            }

            try
            {
                s.InstChunk = Chunk.InstanceCount;
                s.InstCbl = ChunkBlockLayer.InstanceCount;
                s.InstCbc = CBCLayer.InstanceCount;
                s.InstCgl = ChunkGameObjectLayer.InstanceCount;
                s.InstVml = VoxelMeshLayer.InstanceCount;
                s.InstMs = PooledMemoryStream.InstanceCount;
            }
            catch
            {
            }

            return s;
        }

        public static bool CopyCounts(int idx, int[] dest, out long bytes, out long hwBytes, out int arrays)
        {
            bytes = 0L;
            hwBytes = 0L;
            arrays = 0;
            if (arrayPools == null || classCounts == null || idx < 0 || idx >= classCounts.Length || dest == null)
            {
                return false;
            }
            lock (stateLock)
            {
                int[] src = classCounts[idx];
                int n = (dest.Length < src.Length) ? dest.Length : src.Length;
                for (int k = 0; k < n; k++)
                {
                    dest[k] = src[k];
                    arrays += src[k];
                }
                bytes = typeBytes[idx];
                hwBytes = typeHwBytes[idx];
            }
            return true;
        }

        private static double Mb(long bytes)
        {
            return bytes / 1048576.0;
        }

        public static string SummaryLine(PoolState s)
        {
            return string.Format(Inv,
                "pools: arrays={0:F1}MB hw={1:F1}MB cblcache={2:F1}MB | v2={3:F0} v3={4:F0} v4={5:F0} col={6:F0} int={7:F0} u16={8:F0} f32={9:F0} byte={10:F0} | objs={11} chunks={12} cbl={13} cbc={14} cgl={15} vml={16} ms={17} | inst chunk={18} cbl={19} cbc={20} vml={21}",
                Mb(s.ArraysBytes), Mb(s.ArraysHwBytes), Mb(s.CblCacheBytes),
                Mb(s.V2Bytes), Mb(s.V3Bytes), Mb(s.V4Bytes), Mb(s.ColBytes),
                Mb(s.IntBytes), Mb(s.U16Bytes), Mb(s.F32Bytes), Mb(s.ByteBytes),
                s.PoolObjects, s.PoolChunks, s.PoolCbl, s.PoolCbc, s.PoolCgl, s.PoolVml, s.PoolMs,
                s.InstChunk, s.InstCbl, s.InstCbc, s.InstVml);
        }

        public static string TopClassesLine(int top)
        {
            if (arrayPools == null || classCounts == null || sizeClasses == null)
            {
                return "pools top: unavailable";
            }
            if (top > takenI.Length)
            {
                top = takenI.Length;
            }
            if (top < 1)
            {
                top = 1;
            }
            StringBuilder sb = new StringBuilder(256);
            sb.Append("pools top:");
            lock (stateLock)
            {
                for (int r = 0; r < top; r++)
                {
                    long best = 0L;
                    int bi = -1;
                    int bk = -1;
                    for (int i = 0; i < classCounts.Length; i++)
                    {
                        for (int k = 0; k < classCounts[i].Length; k++)
                        {
                            long b = (long)classCounts[i][k] * sizeClasses[k] * arrayPools[i].ElemSize;
                            if (b > best && !Taken(i, k, r))
                            {
                                best = b;
                                bi = i;
                                bk = k;
                            }
                        }
                    }
                    if (bi < 0)
                    {
                        break;
                    }
                    takenI[r] = bi;
                    takenK[r] = bk;
                    sb.Append(' ').Append(arrayPools[bi].Name).Append('[').Append(sizeClasses[bk]).Append(']');
                    sb.Append('x').Append(classCounts[bi][bk]).Append('=').Append(Mb(best).ToString("F1", Inv)).Append("MB");
                }
            }
            return sb.ToString();
        }

        private static readonly int[] takenI = new int[16];
        private static readonly int[] takenK = new int[16];

        private static bool Taken(int i, int k, int upTo)
        {
            for (int r = 0; r < upTo; r++)
            {
                if (takenI[r] == i && takenK[r] == k)
                {
                    return true;
                }
            }
            return false;
        }

        public static string Snapshot()
        {
            PoolState s = Read();
            StringBuilder sb = new StringBuilder(4096);
            sb.AppendLine(SummaryLine(s));
            sb.AppendLine(TopClassesLine(6));

            if (arrayPools == null || sizeClasses == null || classCounts == null)
            {
                sb.AppendLine("size classes unavailable");
                return sb.ToString();
            }

            lock (stateLock)
            {
                for (int i = 0; i < arrayPools.Length; i++)
                {
                    sb.Append(arrayPools[i].Name).Append(':');
                    bool any = false;
                    for (int k = 0; k < classCounts[i].Length; k++)
                    {
                        int c = classCounts[i][k];
                        if (c <= 0)
                        {
                            continue;
                        }
                        any = true;
                        sb.Append(' ').Append(sizeClasses[k]).Append('x').Append(c).Append('=');
                        sb.Append(Mb((long)c * sizeClasses[k] * arrayPools[i].ElemSize).ToString("F1", Inv)).Append("MB");
                    }
                    if (!any)
                    {
                        sb.Append(" empty");
                    }
                    sb.AppendLine();
                }
            }
            return sb.ToString();
        }
    }
}
