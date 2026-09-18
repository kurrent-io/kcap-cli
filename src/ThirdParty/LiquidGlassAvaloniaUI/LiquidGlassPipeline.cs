using System;
using System.Threading;

namespace LiquidGlassAvaloniaUI
{
    // Local addition, not upstream: see VENDORED.md.
    public static class LiquidGlassPipeline
    {
        private static int s_reported;

        public static event Action<string>? Unavailable;

        public static void Report(string reason)
        {
            if (Interlocked.Exchange(ref s_reported, 1) == 0)
                Unavailable?.Invoke(reason);
        }

        public static void ResetForTesting() => Interlocked.Exchange(ref s_reported, 0);
    }
}
