using System;
using HarmonyLib;

namespace ProfLog
{
    public class Init : IModApi
    {
        public static bool Ready;

        public void InitMod(Mod modInstance)
        {
            try
            {
                Log.Out("[ProfLog] init");
                Harmony harmony = new Harmony("com.proflog");
                PatchTable.ApplyAll(harmony);
                ModEvents.GameStartDone.RegisterHandler(OnGameStartDone);
                ModEvents.GameShutdown.RegisterHandler(OnGameShutdown);
                Ready = true;
                Log.Out("[ProfLog] ready, console: proflog");
            }
            catch (Exception ex)
            {
                Log.Error("[ProfLog] init failed: " + ex);
            }
        }

        private void OnGameStartDone(ref ModEvents.SGameStartDoneData data)
        {
            try
            {
                Log.Out("[ProfLog] env: " + StateReader.Environment());
                Sampler.Start();
            }
            catch (Exception ex)
            {
                Log.Error("[ProfLog] start failed: " + ex);
            }
        }

        private void OnGameShutdown(ref ModEvents.SGameShutdownData data)
        {
            try
            {
                Sampler.Stop();
                Sampler.FlushNow();
                Log.Out("[ProfLog] stopped, rows=" + Sampler.Rows);
            }
            catch
            {
            }
        }
    }
}
