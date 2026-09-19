namespace ProfLog
{
    public static class FrameProbe
    {
        private static long lastEntry;

        public static void Pre(out long __state)
        {
            if (!Counters.On)
            {
                lastEntry = 0L;
                __state = 0L;
                return;
            }

            long now = Counters.Now();
            long prev = lastEntry;
            lastEntry = now;
            if (prev != 0L)
            {
                long d = now - prev;
                if (d > 0L && d < Counters.FrameWallCapTicks)
                {
                    Counters.AddTicks(Pr.FrameWall, d);
                }
            }
            __state = now;
        }

        public static void Post(long __state)
        {
            if (__state != 0L)
            {
                Counters.Add(Pr.UpdateOuter, __state);
            }
            NativeProbe.Tick();
        }

        public static void Reset()
        {
            lastEntry = 0L;
        }
    }
}
