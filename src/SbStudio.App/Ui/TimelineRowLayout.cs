namespace SbStudio.App.Ui;

/// <summary>Row positions in units of the normal row height, including taller read-only tracks.</summary>
internal sealed class TimelineRowLayout
{
    private readonly int[] tops;
    public int Count => tops.Length - 1;
    public int Total => tops[^1];
    public TimelineRowLayout(IEnumerable<int> heights)
    {
        var units = heights.ToArray();
        tops = new int[units.Length + 1];
        for (int i = 0; i < units.Length; i++) tops[i + 1] = tops[i] + Math.Max(1, units[i]);
    }
    public int Top(int row) => tops[Math.Clamp(row, 0, Count)];
    public int Height(int row) => tops[row + 1] - tops[row];
    /// <summary>Reveal the first child after expanding, keeping its parent visible and moving only as far as needed.</summary>
    public double ScrollAfterExpansion(int parent, double scroll, double visible)
    {
        if (parent < 0 || parent + 1 >= Count || visible <= 0 || Top(parent) < scroll) return scroll;
        double needed = Math.Max(scroll, Top(parent + 2) - visible);
        return Math.Clamp(Math.Min(needed, Top(parent)), 0, Math.Max(0, Total - visible));
    }
    public int RowAt(double position)
    {
        if (position < 0) return -1;
        int low = 0, high = Count;
        while (low < high)
        {
            int mid = (low + high) / 2;
            if (tops[mid + 1] <= position) low = mid + 1; else high = mid;
        }
        return low;
    }
}
