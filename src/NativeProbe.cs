using System;
using System.Collections;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text;
using UnityEngine;
using UnityEngine.Profiling;

namespace ProfLog
{
    public static class NativeProbe
    {
        private sealed class Kind
        {
            public string Short;
            public string Full;
            public Type T;
            public long Count;
            public long Bytes;
            public long PrevCount;
            public long PrevBytes;
            public double ScanMs;
            public bool Sized;
        }

        public const int IdxMesh = 0;
        public const int IdxTex2d = 1;
        public const int IdxMaterial = 5;
        public const int IdxShader = 6;
        public const int IdxGameObject = 7;
        public const int IdxTransform = 8;
        public const int IdxAnimClip = 11;

        private static readonly string[] FullNames =
        {
            "UnityEngine.Mesh",
            "UnityEngine.Texture2D",
            "UnityEngine.Texture2DArray",
            "UnityEngine.Cubemap",
            "UnityEngine.RenderTexture",
            "UnityEngine.Material",
            "UnityEngine.Shader",
            "UnityEngine.GameObject",
            "UnityEngine.Transform",
            "UnityEngine.Sprite",
            "UnityEngine.AudioClip",
            "UnityEngine.AnimationClip",
            "UnityEngine.ScriptableObject",
            "UnityEngine.TextAsset",
            "UnityEngine.Font"
        };

        private static readonly string[] ShortNames =
        {
            "mesh", "tex2d", "tex2darr", "cubemap", "rendertex",
            "material", "shader", "gameobject", "transform", "sprite",
            "audioclip", "animclip", "scriptableobj", "textasset", "font"
        };

        private const int TopN = 12;

        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        private static readonly object stateLock = new object();

        public static volatile bool Enabled = true;
        public static volatile int IntervalSec = 300;
        public static volatile int CheckEveryFrames = 40;
        public static volatile int SizeCap = 300000;
        public static volatile bool TrackNames = true;
        public static volatile int WarnMs = 500;

        public static volatile bool Faulted;
        public static string FaultReason = "";

        private static Kind[] kinds;
        private static bool initDone;
        private static int frameTick;
        private static long lastRunTicks;

        public static long Censuses;
        private static double lastCensusMs;
        private static int lastBundles;
        private static string lastBundleList = "";

        private static readonly long[] topBytes = new long[TopN];
        private static readonly string[] topLabel = new string[TopN];
        private static int topUsed;

        private static FieldInfo abmInstance;
        private static FieldInfo abmDict;
        private static bool bundleInitDone;

        public static int TypeCount
        {
            get { return (kinds != null) ? kinds.Length : 0; }
        }

        public static string TypeName(int i)
        {
            return (kinds != null && i >= 0 && i < kinds.Length) ? kinds[i].Short : "?";
        }

        public static bool Available
        {
            get { return kinds != null && kinds.Length > 0; }
        }

        public static double SecondsSinceCensus
        {
            get
            {
                if (lastRunTicks == 0L)
                {
                    return -1.0;
                }
                return (Stopwatch.GetTimestamp() - lastRunTicks) / (double)Stopwatch.Frequency;
            }
        }

        public static void Init()
        {
            if (initDone)
            {
                return;
            }
            initDone = true;
            try
            {
                Kind[] k = new Kind[FullNames.Length];
                for (int i = 0; i < FullNames.Length; i++)
                {
                    Kind e = new Kind();
                    e.Short = ShortNames[i];
                    e.Full = FullNames[i];
                    e.T = ResolveType(FullNames[i]);
                    k[i] = e;
                }
                kinds = k;
            }
            catch (Exception ex)
            {
                Fault(ex);
            }
        }

        private static Type ResolveType(string full)
        {
            try
            {
                Type t = Type.GetType(full + ", UnityEngine.CoreModule", false);
                if (t != null)
                {
                    return t;
                }
            }
            catch
            {
            }
            try
            {
                Assembly[] asms = AppDomain.CurrentDomain.GetAssemblies();
                for (int i = 0; i < asms.Length; i++)
                {
                    try
                    {
                        Type t = asms[i].GetType(full, false);
                        if (t != null)
                        {
                            return t;
                        }
                    }
                    catch
                    {
                    }
                }
            }
            catch
            {
            }
            return null;
        }

        public static void Tick()
        {
            if (Faulted || !Enabled || !Sampler.Running)
            {
                return;
            }
            int period = CheckEveryFrames;
            if (period < 1)
            {
                period = 1;
            }
            if (++frameTick < period)
            {
                return;
            }
            frameTick = 0;

            try
            {
                long now = Stopwatch.GetTimestamp();
                if (lastRunTicks != 0L)
                {
                    double since = (now - lastRunTicks) / (double)Stopwatch.Frequency;
                    if (since < IntervalSec)
                    {
                        return;
                    }
                }
                Run(now);
            }
            catch (Exception ex)
            {
                Fault(ex);
            }
        }

        public static string RunNow()
        {
            try
            {
                Init();
                if (!Available)
                {
                    return "no types resolved";
                }
                Run(Stopwatch.GetTimestamp());
                return SummaryLine();
            }
            catch (Exception ex)
            {
                return "failed: " + ex.Message;
            }
        }

        private static void Run(long now)
        {
            Init();
            if (kinds == null)
            {
                return;
            }

            lastRunTicks = now;
            long t0 = Stopwatch.GetTimestamp();

            lock (stateLock)
            {
                topUsed = 0;
                for (int i = 0; i < TopN; i++)
                {
                    topBytes[i] = 0L;
                    topLabel[i] = null;
                }

                for (int i = 0; i < kinds.Length; i++)
                {
                    Scan(kinds[i]);
                }

                ReadBundles();
                Censuses++;
                lastCensusMs = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
            }

            if (Censuses == 1L || lastCensusMs > WarnMs)
            {
                Log.Out(string.Format(Inv, "[ProfLog] native census #{0} took {1:F0}ms, bundles={2}, {3}",
                    Censuses, lastCensusMs, lastBundles, SlowestTypes()));
            }
        }

        private static string SlowestTypes()
        {
            if (kinds == null)
            {
                return "";
            }
            bool[] used = new bool[kinds.Length];
            StringBuilder b = new StringBuilder(128);
            for (int n = 0; n < 3; n++)
            {
                int best = -1;
                double bestMs = 0.0;
                for (int i = 0; i < kinds.Length; i++)
                {
                    if (used[i] || kinds[i].T == null || kinds[i].ScanMs <= bestMs)
                    {
                        continue;
                    }
                    best = i;
                    bestMs = kinds[i].ScanMs;
                }
                if (best < 0)
                {
                    break;
                }
                used[best] = true;
                if (b.Length > 0)
                {
                    b.Append(' ');
                }
                b.Append(kinds[best].Short).Append('=').Append(bestMs.ToString("F0", Inv)).Append("ms");
            }
            return b.ToString();
        }

        private static void Scan(Kind k)
        {
            k.PrevCount = k.Count;
            k.PrevBytes = k.Bytes;
            k.Count = 0L;
            k.Bytes = 0L;
            k.Sized = false;
            k.ScanMs = 0.0;
            if (k.T == null)
            {
                return;
            }

            long t0 = Stopwatch.GetTimestamp();
            try
            {
                UnityEngine.Object[] all = Resources.FindObjectsOfTypeAll(k.T);
                if (all == null)
                {
                    return;
                }
                k.Count = all.Length;
                int cap = SizeCap;
                if (cap > 0 && all.Length <= cap)
                {
                    k.Sized = true;
                    long sum = 0L;
                    for (int i = 0; i < all.Length; i++)
                    {
                        UnityEngine.Object o = all[i];
                        if (o == null)
                        {
                            continue;
                        }
                        long b = 0L;
                        try
                        {
                            b = Profiler.GetRuntimeMemorySizeLong(o);
                        }
                        catch
                        {
                        }
                        if (b <= 0L)
                        {
                            continue;
                        }
                        sum += b;
                        if (TrackNames && b > topBytes[TopN - 1])
                        {
                            OfferTop(k.Short, o, b);
                        }
                    }
                    k.Bytes = sum;
                }
            }
            catch
            {
            }
            k.ScanMs = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
        }

        private static void OfferTop(string typeShort, UnityEngine.Object o, long bytes)
        {
            int pos = TopN - 1;
            while (pos > 0 && topBytes[pos - 1] < bytes)
            {
                topBytes[pos] = topBytes[pos - 1];
                topLabel[pos] = topLabel[pos - 1];
                pos--;
            }
            string nm;
            try
            {
                nm = o.name;
            }
            catch
            {
                nm = "?";
            }
            if (string.IsNullOrEmpty(nm))
            {
                nm = "(unnamed)";
            }
            topBytes[pos] = bytes;
            topLabel[pos] = typeShort + ":" + nm;
            if (topUsed < TopN)
            {
                topUsed++;
            }
        }

        private static void ReadBundles()
        {
            lastBundles = 0;
            lastBundleList = "";
            if (!bundleInitDone)
            {
                bundleInitDone = true;
                try
                {
                    Type t = ResolveGameType("AssetBundleManager");
                    if (t != null)
                    {
                        abmInstance = t.GetField("instance", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                        abmDict = t.GetField("dictAssetBundleRefs", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    }
                }
                catch
                {
                }
            }
            if (abmInstance == null || abmDict == null)
            {
                return;
            }
            try
            {
                object inst = abmInstance.GetValue(null);
                if (inst == null)
                {
                    return;
                }
                IDictionary d = abmDict.GetValue(inst) as IDictionary;
                if (d == null)
                {
                    return;
                }
                lastBundles = d.Count;
                StringBuilder b = new StringBuilder(256);
                int n = 0;
                foreach (object key in d.Keys)
                {
                    if (n >= 40)
                    {
                        b.Append(" ...");
                        break;
                    }
                    if (n > 0)
                    {
                        b.Append(' ');
                    }
                    b.Append(Basename(key as string));
                    n++;
                }
                lastBundleList = b.ToString();
            }
            catch
            {
            }
        }

        private static string Basename(string s)
        {
            if (string.IsNullOrEmpty(s))
            {
                return "?";
            }
            int i = s.LastIndexOf('/');
            int j = s.LastIndexOf('\\');
            if (j > i)
            {
                i = j;
            }
            return (i >= 0 && i + 1 < s.Length) ? s.Substring(i + 1) : s;
        }

        private static Type ResolveGameType(string name)
        {
            try
            {
                Assembly[] asms = AppDomain.CurrentDomain.GetAssemblies();
                for (int i = 0; i < asms.Length; i++)
                {
                    try
                    {
                        Type t = asms[i].GetType(name, false);
                        if (t != null)
                        {
                            return t;
                        }
                    }
                    catch
                    {
                    }
                }
            }
            catch
            {
            }
            return null;
        }

        public static bool CopyRow(int i, out long count, out long bytes, out long dCount, out long dBytes, out bool sized, out double scanMs)
        {
            count = 0L;
            bytes = 0L;
            dCount = 0L;
            dBytes = 0L;
            sized = false;
            scanMs = 0.0;
            if (kinds == null || i < 0 || i >= kinds.Length)
            {
                return false;
            }
            lock (stateLock)
            {
                Kind k = kinds[i];
                if (k.T == null)
                {
                    return false;
                }
                count = k.Count;
                bytes = k.Bytes;
                dCount = k.Count - k.PrevCount;
                dBytes = k.Bytes - k.PrevBytes;
                sized = k.Sized;
                scanMs = k.ScanMs;
            }
            return true;
        }

        public static void Snapshot(out long totalCount, out long totalBytes, out int bundles, out double censusMs, out double ageSec)
        {
            totalCount = 0L;
            totalBytes = 0L;
            bundles = 0;
            censusMs = 0.0;
            ageSec = SecondsSinceCensus;
            if (kinds == null)
            {
                return;
            }
            lock (stateLock)
            {
                for (int i = 0; i < kinds.Length; i++)
                {
                    totalCount += kinds[i].Count;
                    totalBytes += kinds[i].Bytes;
                }
                bundles = lastBundles;
                censusMs = lastCensusMs;
            }
        }

        public static long CountOf(int i)
        {
            if (kinds == null || i < 0 || i >= kinds.Length)
            {
                return 0L;
            }
            return kinds[i].Count;
        }

        public static long BytesOf(int i)
        {
            if (kinds == null || i < 0 || i >= kinds.Length)
            {
                return 0L;
            }
            return kinds[i].Bytes;
        }

        public static int Bundles
        {
            get { return lastBundles; }
        }

        public static double LastCensusMs
        {
            get { return lastCensusMs; }
        }

        public static double Mb(long bytes)
        {
            return bytes / 1048576.0;
        }

        public static string SummaryLine()
        {
            if (kinds == null || Censuses == 0L)
            {
                return "native: no census yet";
            }
            StringBuilder b = new StringBuilder(320);
            b.Append("native: bundles=").Append(lastBundles);
            b.Append(" census=").Append(lastCensusMs.ToString("F0", Inv)).Append("ms age=");
            double age = SecondsSinceCensus;
            b.Append(age < 0.0 ? "n/a" : age.ToString("F0", Inv) + "s");
            lock (stateLock)
            {
                for (int i = 0; i < kinds.Length; i++)
                {
                    Kind k = kinds[i];
                    if (k.T == null || k.Count == 0L)
                    {
                        continue;
                    }
                    b.Append(" | ").Append(k.Short).Append('=').Append(k.Count);
                    if (k.Sized && k.Bytes > 0L)
                    {
                        b.Append('/').Append(Mb(k.Bytes).ToString("F1", Inv)).Append("MB");
                    }
                    long d = k.Count - k.PrevCount;
                    if (d != 0L && k.PrevCount != 0L)
                    {
                        b.Append(d > 0L ? " +" : " ").Append(d);
                    }
                }
            }
            return b.ToString();
        }

        public static string TopLine()
        {
            if (topUsed == 0)
            {
                return "native top: none";
            }
            StringBuilder b = new StringBuilder(256);
            b.Append("native top:");
            lock (stateLock)
            {
                for (int i = 0; i < topUsed && i < TopN; i++)
                {
                    if (topLabel[i] == null)
                    {
                        break;
                    }
                    b.Append(' ').Append(topLabel[i]).Append('=');
                    b.Append(Mb(topBytes[i]).ToString("F1", Inv)).Append("MB");
                }
            }
            return b.ToString();
        }

        public static string BundleLine()
        {
            return "native bundles: " + lastBundles + (lastBundleList.Length > 0 ? " [" + lastBundleList + "]" : "");
        }

        public static string Describe()
        {
            return "enabled=" + (Enabled ? "on" : "off")
                + " every=" + IntervalSec + "s check=" + CheckEveryFrames + "f"
                + " sizeCap=" + SizeCap + " names=" + (TrackNames ? "on" : "off")
                + " types=" + ResolvedCount() + "/" + TypeCount
                + (Faulted ? " FAULTED(" + FaultReason + ")" : "");
        }

        public static int ResolvedCount()
        {
            if (kinds == null)
            {
                return 0;
            }
            int n = 0;
            for (int i = 0; i < kinds.Length; i++)
            {
                if (kinds[i].T != null)
                {
                    n++;
                }
            }
            return n;
        }

        public static string MissingTypes()
        {
            if (kinds == null)
            {
                return "not initialised";
            }
            StringBuilder b = new StringBuilder(128);
            for (int i = 0; i < kinds.Length; i++)
            {
                if (kinds[i].T == null)
                {
                    if (b.Length > 0)
                    {
                        b.Append(' ');
                    }
                    b.Append(kinds[i].Full);
                }
            }
            return (b.Length == 0) ? "none" : b.ToString();
        }

        private static void Fault(Exception ex)
        {
            Faulted = true;
            FaultReason = ex.Message;
            Log.Warning("[ProfLog] native probe disabled after error: " + ex);
        }

        public static void ClearFault()
        {
            Faulted = false;
            FaultReason = "";
        }
    }
}
