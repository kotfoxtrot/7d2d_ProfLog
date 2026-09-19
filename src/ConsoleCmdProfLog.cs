using System;
using System.Collections.Generic;
using UnityEngine.Scripting;

namespace ProfLog
{
    [Preserve]
    public class ConsoleCmdProfLog : ConsoleCmdAbstract
    {
        public override bool IsExecuteOnClient => false;

        public override int DefaultPermissionLevel => 0;

        public override string[] getCommands()
        {
            return new string[1] { "proflog" };
        }

        public override string getDescription()
        {
            return "ProfLog sampling control";
        }

        public override string getHelp()
        {
            return "proflog                 show status\n"
                + "proflog on | off        enable or disable counter recording\n"
                + "proflog interval <sec>  set sampling interval, minimum 1\n"
                + "proflog summary <n>     write a log summary every n samples, 0 disables\n"
                + "proflog flush           flush output files to disk\n"
                + "proflog where           print output file paths\n"
                + "proflog patches         list patched and failed call sites\n"
                + "proflog env             print environment and settings line\n"
                + "proflog missing         list reflection members that failed to resolve\n"
                + "proflog reset           zero all counters\n"
                + "proflog cull            RegionFileManager state snapshot\n"
                + "proflog cullbench [n]   benchmark the CullExpiredChunks loop on live data, n iterations (default 3)\n"
                + "proflog lockprobe on|off  enable or disable the lock-wait measurement prefix\n"
                + "proflog pools           MemoryPools free-list snapshot with per size class breakdown\n"
                + "proflog pooldetail on|off  write the per size class pools TSV, on by default\n"
                + "proflog native          last native object census, counts and native bytes per Unity type\n"
                + "proflog native now      force a census right now, blocks the calling frame\n"
                + "proflog native on|off   enable or disable the periodic census\n"
                + "proflog native every <sec>  seconds between censuses, default 300\n"
                + "proflog native cap <n>  skip byte sizing for a type with more than n objects, default 300000\n"
                + "proflog native names on|off  collect the top objects by native size, on by default\n"
                + "proflog bundles         list the AssetBundles currently held by AssetBundleManager";
        }

        private static void Out(string s)
        {
            SingletonMonoBehaviour<SdtdConsole>.Instance.Output(s);
        }

        public override void Execute(List<string> _params, CommandSenderInfo _senderInfo)
        {
            try
            {
                if (_params.Count == 0)
                {
                    Out(Sampler.Status());
                    return;
                }

                string a = _params[0].ToLowerInvariant();

                if (a == "on")
                {
                    Counters.On = true;
                    Sampler.Start();
                    Out("recording on");
                    return;
                }

                if (a == "off")
                {
                    Counters.On = false;
                    Out("recording off, sampler still writing rows");
                    return;
                }

                if (a == "interval")
                {
                    int v;
                    if (_params.Count < 2 || !int.TryParse(_params[1], out v) || v < 1)
                    {
                        Out("usage: proflog interval <seconds >= 1>");
                        return;
                    }
                    Sampler.IntervalSec = v;
                    Out("interval = " + v + "s");
                    return;
                }

                if (a == "summary")
                {
                    int v;
                    if (_params.Count < 2 || !int.TryParse(_params[1], out v) || v < 0)
                    {
                        Out("usage: proflog summary <n >= 0>");
                        return;
                    }
                    Sampler.SummaryEvery = v;
                    Out("summary every " + v + " samples");
                    return;
                }

                if (a == "flush")
                {
                    Sampler.FlushNow();
                    Out("flushed, rows=" + Sampler.Rows);
                    return;
                }

                if (a == "where")
                {
                    Out("probes  " + Sampler.ProbesPath);
                    Out("threads " + Sampler.ThreadsPath);
                    Out("summary " + Sampler.SummaryPath);
                    Out("pools   " + Sampler.PoolsPath);
                    return;
                }

                if (a == "patches")
                {
                    Out("applied " + PatchTable.Applied.Count + ":");
                    for (int i = 0; i < PatchTable.Applied.Count; i++)
                    {
                        Out("  " + PatchTable.Applied[i]);
                    }
                    Out("failed " + PatchTable.Failed.Count + ":");
                    for (int i = 0; i < PatchTable.Failed.Count; i++)
                    {
                        Out("  " + PatchTable.Failed[i]);
                    }
                    return;
                }

                if (a == "missing")
                {
                    Out("unresolved members: " + StateReader.MissingMembers());
                    return;
                }

                if (a == "env")
                {
                    Out(StateReader.Environment());
                    return;
                }

                if (a == "cull")
                {
                    Out(CullProbe.Snapshot());
                    return;
                }

                if (a == "pools")
                {
                    string[] pl = PoolProbe.Snapshot().Split('\n');
                    for (int i = 0; i < pl.Length; i++)
                    {
                        if (pl[i].Length > 0)
                        {
                            Out(pl[i]);
                        }
                    }
                    return;
                }

                if (a == "native")
                {
                    if (_params.Count < 2)
                    {
                        Out(NativeProbe.Describe());
                        Out(NativeProbe.SummaryLine());
                        Out(NativeProbe.TopLine());
                        Out(NativeProbe.BundleLine());
                        Out("missing types: " + NativeProbe.MissingTypes());
                        return;
                    }

                    string b = _params[1].ToLowerInvariant();
                    if (b == "now")
                    {
                        NativeProbe.ClearFault();
                        Out(NativeProbe.RunNow());
                        Out(NativeProbe.TopLine());
                        Out(NativeProbe.BundleLine());
                        return;
                    }
                    if (b == "on" || b == "off")
                    {
                        NativeProbe.Enabled = b == "on";
                        if (NativeProbe.Enabled)
                        {
                            NativeProbe.ClearFault();
                        }
                        Out("native census " + b);
                        return;
                    }
                    if (b == "every" && _params.Count >= 3)
                    {
                        int sec;
                        if (int.TryParse(_params[2], out sec) && sec >= 10)
                        {
                            NativeProbe.IntervalSec = sec;
                            Out("native census every " + sec + "s");
                        }
                        else
                        {
                            Out("usage: proflog native every <sec>, minimum 10");
                        }
                        return;
                    }
                    if (b == "cap" && _params.Count >= 3)
                    {
                        int cap;
                        if (int.TryParse(_params[2], out cap) && cap >= 0)
                        {
                            NativeProbe.SizeCap = cap;
                            Out("native size cap " + cap);
                        }
                        else
                        {
                            Out("usage: proflog native cap <n>");
                        }
                        return;
                    }
                    if (b == "names" && _params.Count >= 3)
                    {
                        NativeProbe.TrackNames = _params[2].ToLowerInvariant() == "on";
                        Out("native name tracking " + (NativeProbe.TrackNames ? "on" : "off"));
                        return;
                    }
                    Out("usage: proflog native [now|on|off|every <sec>|cap <n>|names on|off]");
                    return;
                }

                if (a == "bundles")
                {
                    Out(NativeProbe.BundleLine());
                    return;
                }

                if (a == "pooldetail")
                {
                    if (_params.Count < 2)
                    {
                        Out("usage: proflog pooldetail on|off, currently " + (Sampler.PoolDetail ? "on" : "off"));
                        return;
                    }
                    Sampler.PoolDetail = _params[1].ToLowerInvariant() == "on";
                    Out("pool detail rows " + (Sampler.PoolDetail ? "on" : "off"));
                    return;
                }

                if (a == "cullbench")
                {
                    int n = 3;
                    if (_params.Count >= 2)
                    {
                        int.TryParse(_params[1], out n);
                    }
                    string report = CullProbe.Bench(n);
                    string[] lines = report.Split('\n');
                    for (int i = 0; i < lines.Length; i++)
                    {
                        Out(lines[i]);
                        Sampler.Say("cullbench| " + lines[i]);
                    }
                    return;
                }

                if (a == "lockprobe")
                {
                    if (_params.Count < 2)
                    {
                        Out("usage: proflog lockprobe on|off, currently " + (CullProbe.LockProbe ? "on" : "off"));
                        return;
                    }
                    CullProbe.LockProbe = _params[1].ToLowerInvariant() == "on";
                    Out("lockprobe " + (CullProbe.LockProbe ? "on" : "off"));
                    return;
                }

                if (a == "reset")
                {
                    Counters.Reset();
                    Out("counters zeroed");
                    return;
                }

                if (a == "status")
                {
                    Out(Sampler.Status());
                    return;
                }

                Out("unknown subcommand '" + _params[0] + "', see 'help proflog'");
            }
            catch (Exception ex)
            {
                Out("proflog error: " + ex.Message);
            }
        }
    }
}
