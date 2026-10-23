namespace SbStudio.Core;

/// <summary>lazer StatelessRNG; object start time seeds rotation, banana colour and scale.</summary>
public static class CatchVisualRandom
{
    public static ulong Value(double time, int series = 0)
    {
        unchecked
        {
            ulong x = (((ulong)(uint)series << 32) | (uint)(int)time) ^ 0x12345678;
            x ^= x >> 33;
            x *= 0xff51afd7ed558ccd;
            x ^= x >> 33;
            x *= 0xc4ceb9fe1a85ec53;
            return x ^ (x >> 33);
        }
    }
    public static float Single(double time, int series = 0) => (Value(time, series) & 0xffffff) / 16777216f;
}
