using System;
using System.Collections;
using System.Collections.Generic;
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
            public long Instances;
            public long PrevCount;
            public long PrevBytes;
            public double ScanMs;
            public double HistMs;
            public bool Sized;
            public bool SizeEst;
            public bool Hist;
            public bool HistEst;
            public Dictionary<string, long> Cur;
            public Dictionary<string, long> Prev;
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
        private const string InstSuffix = " (Instance)";

        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        private static readonly object stateLock = new object();

        public static volatile bool Enabled = true;
        public static volatile int IntervalSec = 300;
        public static volatile int CheckEveryFrames = 40;
        public static volatile int SizeCap = 50000;
        public static volatile int HistCap = 50000;
        public static volatile int NameTopN = 25;
        public static volatile bool TrackNames = true;
        public static volatile string HistType = "material";

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

        private static string[] histName = new string[0];
        private static long[] histCount = new long[0];
        private static long[] histDelta = new long[0];
        private static int histUsed;
        private static string histTypeResolved = "";
        private static bool histEstimated;

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

        public static int Bundles
        {
            get { return lastBundles; }
        }

        public static double LastCensusMs
        {
            get { return lastCensusMs; }
        }

        public static int HistUsed
        {
            get { return histUsed; }
        }

        public static string HistTypeResolved
        {
            get { return histTypeResolved; }
        }

        public static bool HistEstimated
        {
            get { return histEstimated; }
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
                    e.Instances = -1L;
                    k[i] = e;
                }
                kinds = k;
                ApplyHistSelection();
            }
            catch (Exception ex)
            {
                Fault(ex);
            }
        }

        public static void ApplyHistSelection()
        {
            if (kinds == null)
            {
                return;
            }
            string want = HistType;
            if (want == null)
            {
                want = "";
            }
            want = want.Trim().ToLowerInvariant();
            for (int i = 0; i < kinds.Length; i++)
            {
                bool on = want.Length > 0 && want != "off" && kinds[i].Short == want;
                kinds[i].Hist = on;
                if (!on)
                {
                    kinds[i].Cur = null;
                    kinds[i].Prev = null;
                    kinds[i].Instances = -1L;
                }
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

                BuildHist();
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

        public static volatile int WarnMs = 500;

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
                    double ms = kinds[i].ScanMs + kinds[i].HistMs;
                    if (used[i] || kinds[i].T == null || ms <= bestMs)
                    {
                        continue;
                    }
                    best = i;
                    bestMs = ms;
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
            k.SizeEst = false;
            k.HistEst = false;
            k.ScanMs = 0.0;
            k.HistMs = 0.0;
            if (!k.Hist)
            {
                k.Instances = -1L;
            }
            if (k.T == null)
            {
                return;
            }

            long t0 = Stopwatch.GetTimestamp();
            UnityEngine.Object[] all = null;
            try
            {
                all = Resources.FindObjectsOfTypeAll(k.T);
            }
            catch
            {
            }
            if (all == null)
            {
                k.ScanMs = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
                return;
            }
            k.Count = all.Length;

            int cap = SizeCap;
            if (cap > 0 && all.Length > 0)
            {
                int stride = 1;
                if (all.Length > cap)
                {
                    stride = (all.Length + cap - 1) / cap;
                }
                k.Sized = true;
                k.SizeEst = stride > 1;
                long sum = 0L;
                long seen = 0L;
                for (int i = 0; i < all.Length; i += stride)
                {
                    UnityEngine.Object o = all[i];
                    seen++;
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
                    if (TrackNames && stride == 1 && b > topBytes[TopN - 1])
                    {
                        OfferTop(k.Short, o, b);
                    }
                }
                k.Bytes = (stride > 1 && seen > 0L) ? (long)(sum * ((double)all.Length / seen)) : sum;
            }
            k.ScanMs = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;

            if (!k.Hist)
            {
                return;
            }

            long t1 = Stopwatch.GetTimestamp();
            Dictionary<string, long> swap = k.Prev;
            k.Prev = k.Cur;
            if (swap == null)
            {
                swap = new Dictionary<string, long>(2048);
            }
            else
            {
                swap.Clear();
            }
            k.Cur = swap;

            int hcap = HistCap;
            int hstride = 1;
            if (hcap > 0 && all.Length > hcap)
            {
                hstride = (all.Length + hcap - 1) / hcap;
            }
            k.HistEst = hstride > 1;
            long inst = 0L;
            long hseen = 0L;
            for (int i = 0; i < all.Length; i += hstride)
            {
                UnityEngine.Object o = all[i];
                hseen++;
                if (o == null)
                {
                    continue;
                }
                string raw;
                try
                {
                    raw = o.name;
                }
                catch
                {
                    raw = null;
                }
                bool isInst;
                string key = BaseName(raw, out isInst);
                if (isInst)
                {
                    inst++;
                }
                long cur;
                k.Cur.TryGetValue(key, out cur);
                k.Cur[key] = cur + 1L;
            }
            double scale = (hstride > 1 && hseen > 0L) ? ((double)all.Length / hseen) : 1.0;
            if (scale != 1.0)
            {
                List<string> keys = new List<string>(k.Cur.Keys);
                for (int i = 0; i < keys.Count; i++)
                {
                    k.Cur[keys[i]] = (long)(k.Cur[keys[i]] * scale);
                }
            }
            k.Instances = (long)(inst * scale);
            k.HistMs = (Stopwatch.GetTimestamp() - t1) * 1000.0 / Stopwatch.Frequency;
        }

        private static string BaseName(string nm, out bool inst)
        {
            inst = false;
            if (string.IsNullOrEmpty(nm))
            {
                return "(unnamed)";
            }
            while (nm.Length > InstSuffix.Length && nm.EndsWith(InstSuffix, StringComparison.Ordinal))
            {
                inst = true;
                nm = nm.Substring(0, nm.Length - InstSuffix.Length);
            }
            return nm;
        }

        private static void BuildHist()
        {
            histUsed = 0;
            histTypeResolved = "";
            histEstimated = false;
            if (kinds == null)
            {
                return;
            }
            Kind k = null;
            for (int i = 0; i < kinds.Length; i++)
            {
                if (kinds[i].Hist && kinds[i].Cur != null)
                {
                    k = kinds[i];
                    break;
                }
            }
            if (k == null)
            {
                return;
            }
            histTypeResolved = k.Short;
            histEstimated = k.HistEst;

            int want = NameTopN;
            if (want < 1)
            {
                want = 1;
            }
            if (histName.Length < want)
            {
                histName = new string[want];
                histCount = new long[want];
                histDelta = new long[want];
            }
            for (int i = 0; i < want; i++)
            {
                histName[i] = null;
                histCount[i] = 0L;
                histDelta[i] = 0L;
            }

            foreach (KeyValuePair<string, long> e in k.Cur)
            {
                if (e.Value <= histCount[want - 1])
                {
                    continue;
                }
                int pos = want - 1;
                while (pos > 0 && histCount[pos - 1] < e.Value)
                {
                    histCount[pos] = histCount[pos - 1];
                    histName[pos] = histName[pos - 1];
                    pos--;
                }
                histCount[pos] = e.Value;
                histName[pos] = e.Key;
                if (histUsed < want)
                {
                    histUsed++;
                }
            }

            for (int i = 0; i < histUsed; i++)
            {
                long prev = 0L;
                if (k.Prev != null && histName[i] != null)
                {
                    k.Prev.TryGetValue(histName[i], out prev);
                }
                histDelta[i] = histCount[i] - prev;
            }
        }

        public static bool HistRow(int rank, out string name, out long count, out long dCount)
        {
            name = null;
            count = 0L;
            dCount = 0L;
            lock (stateLock)
            {
                if (rank < 0 || rank >= histUsed || histName[rank] == null)
                {
                    return false;
                }
                name = histName[rank];
                count = histCount[rank];
                dCount = histDelta[rank];
            }
            return true;
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
                scanMs = k.ScanMs + k.HistMs;
            }
            return true;
        }

        public static int SizedFlag(int i)
        {
            if (kinds == null || i < 0 || i >= kinds.Length)
            {
                return 0;
            }
            Kind k = kinds[i];
            if (!k.Sized)
            {
                return 0;
            }
            return k.SizeEst ? 2 : 1;
        }

        public static long InstancesOf(int i)
        {
            if (kinds == null || i < 0 || i >= kinds.Length)
            {
                return -1L;
            }
            return kinds[i].Instances;
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
            StringBuilder b = new StringBuilder(384);
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
                        if (k.SizeEst)
                        {
                            b.Append('~');
                        }
                    }
                    if (k.Instances >= 0L)
                    {
                        b.Append(" inst=").Append(k.Instances);
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

        public static string HistLine(int top)
        {
            lock (stateLock)
            {
                if (histUsed == 0)
                {
                    return "native names: no histogram, type=" + HistType;
                }
                if (top > histUsed)
                {
                    top = histUsed;
                }
                StringBuilder b = new StringBuilder(384);
                b.Append("native names[").Append(histTypeResolved).Append(histEstimated ? ",sampled" : "").Append("]:");
                for (int i = 0; i < top; i++)
                {
                    if (histName[i] == null)
                    {
                        break;
                    }
                    b.Append(' ').Append(histName[i]).Append('=').Append(histCount[i]);
                    if (histDelta[i] != 0L)
                    {
                        b.Append(histDelta[i] > 0L ? "+" : "").Append(histDelta[i]);
                    }
                }
                return b.ToString();
            }
        }

        public static string BundleLine()
        {
            return "native bundles: " + lastBundles + (lastBundleList.Length > 0 ? " [" + lastBundleList + "]" : "");
        }

        public static string Describe()
        {
            return "enabled=" + (Enabled ? "on" : "off")
                + " every=" + IntervalSec + "s check=" + CheckEveryFrames + "f"
                + " sizeCap=" + SizeCap + " histCap=" + HistCap + " histType=" + HistType + " topN=" + NameTopN
                + " names=" + (TrackNames ? "on" : "off")
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
