namespace Overtake
{
    internal static class NativeTiming
    {
        // One native vehicle update: each car steps on one of sixteen 60 Hz ticks.
        internal const float StepSeconds = 4f / 15f;
    }
}
