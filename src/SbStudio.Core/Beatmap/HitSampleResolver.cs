namespace SbStudio.Core;

public static class HitSampleResolver
{
    public static (int Normal, int Addition, float Volume, int Index) Resolve(OsuBeatmap map, double time, HitSample sample)
    {
        var point = map.SampleAt(time + 5);
        int normal = sample.NormalSet == 0 ? point.SampleSet : sample.NormalSet;
        return (normal, sample.AdditionSet == 0 ? normal : sample.AdditionSet,
            (sample.Volume == 0 ? point.Volume : sample.Volume) / 100f, sample.CustomIndex == 0 ? point.CustomIndex : sample.CustomIndex);
    }
}
