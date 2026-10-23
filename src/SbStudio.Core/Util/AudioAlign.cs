namespace SbStudio.Core;

/// <summary>
/// Finds where one recording of a piece of music lies in time against another of the same piece: a
/// lossless file against the MP3 that was made from it, say, which begins a little later because of the
/// way MP3 is encoded and decoded, or much later because its beginning was cut off.
/// </summary>
public static class AudioAlign
{
    /// <summary>How much the sound is thinned out for the first, rough answer: every 16 samples become one.</summary>
    private const int Coarse = 16;

    /// <summary>
    /// How far <paramref name="other"/> is ahead of <paramref name="reference"/>: the number of samples to
    /// add to a place in the reference to get to the same moment of the music in the other, so that
    /// other[i + Lag] sounds like reference[i]. Both are one channel at the same sample rate.
    /// </summary>
    /// <returns>
    /// The lag, and how alike the two are once shifted by it: 1 for the same sound, around 0 for sounds
    /// that have nothing to do with each other. A low value means the answer is not to be trusted.
    /// </returns>
    public static (long Lag, double Match) Find(ReadOnlySpan<float> reference, ReadOnlySpan<float> other, int sampleRate)
    {
        if (reference.Length < Coarse * 8 || other.Length < Coarse * 8) return (0, 0);

        // Roughly, over every shift there is: the whole of both thinned out, all shifts at once by way of
        // their spectra.
        double[] a = Thin(reference), b = Thin(other);
        int size = 1;
        while (size < a.Length + b.Length) size <<= 1;
        double[] aRe = new double[size], aIm = new double[size], bRe = new double[size], bIm = new double[size];
        a.CopyTo(aRe, 0);
        b.CopyTo(bRe, 0);
        Transform(aRe, aIm, false);
        Transform(bRe, bIm, false);
        for (int i = 0; i < size; i++)
        {
            // The spectrum of the one turned round, times that of the other: what comes back is how well
            // they agree at every shift.
            double re = aRe[i] * bRe[i] + aIm[i] * bIm[i], im = aRe[i] * bIm[i] - aIm[i] * bRe[i];
            bRe[i] = re;
            bIm[i] = im;
        }
        Transform(bRe, bIm, true);

        int best = 0;
        for (int i = 1; i < size; i++)
            if (bRe[i] > bRe[best]) best = i;
        // Shifts beyond the end of the other are those the other way: the result goes round in a circle.
        long rough = (best < b.Length ? best : best - size) * (long)Coarse;

        // Exactly, sample by sample around the rough answer, over half a minute from the middle of what
        // the two have in common.
        long bestLag = rough;
        double bestMatch = double.NegativeInfinity;
        for (long lag = rough - Coarse * 2; lag <= rough + Coarse * 2; lag++)
        {
            double match = Likeness(reference, other, lag, sampleRate * 30);
            if (match > bestMatch)
            {
                bestMatch = match;
                bestLag = lag;
            }
        }
        return double.IsNegativeInfinity(bestMatch) ? (0, 0) : (bestLag, Math.Max(0, bestMatch));
    }

    /// <summary>One value for every <see cref="Coarse"/> samples: their mean, with the mean of the whole taken off.</summary>
    private static double[] Thin(ReadOnlySpan<float> samples)
    {
        var thin = new double[samples.Length / Coarse];
        double total = 0;
        for (int i = 0; i < thin.Length; i++)
        {
            double sum = 0;
            for (int k = 0; k < Coarse; k++) sum += samples[i * Coarse + k];
            thin[i] = sum / Coarse;
            total += thin[i];
        }
        double mean = thin.Length > 0 ? total / thin.Length : 0;
        for (int i = 0; i < thin.Length; i++) thin[i] -= mean;
        return thin;
    }

    /// <summary>
    /// How alike the two are with the other shifted by a lag: their correlation over the middle of the
    /// stretch they share, from -1 to 1. Negative infinity when they share too little to tell.
    /// </summary>
    private static double Likeness(ReadOnlySpan<float> reference, ReadOnlySpan<float> other, long lag, int window)
    {
        long from = Math.Max(0, -lag), to = Math.Min(reference.Length, other.Length - lag);
        if (to - from < 1024) return double.NegativeInfinity;
        if (to - from > window)
        {
            from += (to - from - window) / 2;
            to = from + window;
        }
        double both = 0, first = 0, second = 0;
        for (long i = from; i < to; i++)
        {
            double x = reference[(int)i], y = other[(int)(i + lag)];
            both += x * y;
            first += x * x;
            second += y * y;
        }
        return first <= 0 || second <= 0 ? 0 : both / Math.Sqrt(first * second);
    }

    /// <summary>The Fourier transform of a signal whose length is a power of two, in place; or the way back, not scaled.</summary>
    private static void Transform(double[] re, double[] im, bool inverse)
    {
        int n = re.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i >= j) continue;
            (re[i], re[j]) = (re[j], re[i]);
            (im[i], im[j]) = (im[j], im[i]);
        }

        for (int length = 2; length <= n; length <<= 1)
        {
            double angle = 2 * Math.PI / length * (inverse ? 1 : -1);
            double stepRe = Math.Cos(angle), stepIm = Math.Sin(angle);
            int half = length / 2;
            for (int i = 0; i < n; i += length)
            {
                double turnRe = 1, turnIm = 0;
                for (int k = 0; k < half; k++)
                {
                    int low = i + k, high = low + half;
                    double re2 = re[high] * turnRe - im[high] * turnIm, im2 = re[high] * turnIm + im[high] * turnRe;
                    re[high] = re[low] - re2;
                    im[high] = im[low] - im2;
                    re[low] += re2;
                    im[low] += im2;
                    double next = turnRe * stepRe - turnIm * stepIm;
                    turnIm = turnRe * stepIm + turnIm * stepRe;
                    turnRe = next;
                }
            }
        }
    }
}
