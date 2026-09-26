using System.Globalization;
using System.Text;

namespace BmwDiag;

// Line charts in the terminal for the live view: last N seconds, one chart per measure,
// up to two series per chart (target vs actual). Drawn with braille characters (2x4 dots per cell).
static class Charts
{
    // Categorical slots 1 and 2 of the validated reference palette (blue, orange) as xterm-256 colors
    // (Terminal.app has no reliable 24-bit color): 33 = #0087ff, 208 = #ff8700.
    static readonly string[] SeriesColor = { "\x1b[38;5;33m", "\x1b[38;5;208m" };
    const string Dim = "\x1b[2m", Reset = "\x1b[0m", Bold = "\x1b[1m";
    public const double WindowSeconds = 60;

    public sealed class History
    {
        readonly List<(DateTime t, double?[] v)> samples = new();
        public void Add(double?[] v)
        {
            var now = DateTime.Now;
            samples.Add((now, (double?[])v.Clone()));
            while (samples.Count > 0 && (now - samples[0].t).TotalSeconds > WindowSeconds * 2) samples.RemoveAt(0);
        }
        public IReadOnlyList<(DateTime t, double?[] v)> Samples => samples;
    }

    // Charts of a profile: the explicit "charts", or the usual pairs if the profile has none
    public static List<Profile.Chart> For(Profile p)
    {
        if (p.charts is { Count: > 0 }) return p.charts;
        var args = p.values.Select(v => v.arg).Where(a => a != null).ToHashSet();
        var list = new List<Profile.Chart>();
        if (args.Contains("SPLAD") && args.Contains("IPLAD"))
            list.Add(new() { title = "Boost pressure", title_ru = "Наддув", series = new() { "SPLAD", "IPLAD" }, minSpan = 0.3 });
        if (args.Contains("SLMMG") && args.Contains("ILMMG"))
            list.Add(new() { title = "Air mass", title_ru = "Воздух", series = new() { "SLMMG", "ILMMG" }, clipMax = 1500, minSpan = 200 });
        if (args.Contains("ITAVP1"))
            list.Add(new() { title = "Temperature before DPF", title_ru = "Температура перед сажевым фильтром", series = new() { "ITAVP1" }, minSpan = 50 });
        else if (args.Contains("INMOT"))
            list.Add(new() { title = "Engine speed", title_ru = "Обороты", series = new() { "INMOT" }, minSpan = 1000 });
        return list;
    }

    public static int MinRows(int charts) => 2 + charts * (3 + 4) + 2;

    // Renders all charts into lines (with ANSI colors), each at most `width` visible characters
    public static List<string> Render(Profile p, List<Profile.Value> all, History h, int width, int rows)
    {
        var charts = For(p);
        var lines = new List<string>();
        if (charts.Count == 0) { lines.Add(L.T("no charts for this profile", "для этого профиля нет графиков")); return lines; }
        int plotH = Math.Max(4, (rows - 4 - charts.Count * 3) / charts.Count);
        const int axisW = 8;
        int plotW = Math.Max(10, width - axisW - 2);
        var now = h.Samples.Count > 0 ? h.Samples[^1].t : DateTime.Now;

        foreach (var c in charts)
        {
            var idx = c.series.Select(a => all.FindIndex(v => v.arg == a || v.result == a)).Where(i => i >= 0).Take(2).ToList();
            lines.Add("");
            if (idx.Count == 0) { lines.Add(c.Title + ": " + L.T("values not in the profile", "таких показаний нет в профиле")); continue; }
            var fmt = all[idx[0]].format;
            string unit = all[idx[0]].Unit;

            // title + legend: colored mark, text stays in the normal text color
            var title = new StringBuilder($"{Bold}{c.Title}{Reset}" + (unit != "" ? $"{Dim}, {unit}{Reset}" : ""));
            for (int k = 0; k < idx.Count; k++)
            {
                double? last = h.Samples.Count > 0 ? h.Samples[^1].v[idx[k]] : null;
                string name = idx.Count > 1 ? ShortName(all[idx[k]].Label) : L.T("now", "сейчас");
                // above clipMax the line sits on the top edge; the arrow says the value is off the scale
                string off = last != null && c.clipMax != null && last > c.clipMax ? " ↑" : "";
                title.Append($"   {SeriesColor[k]}━━{Reset} {name} {Val(last, fmt)}{off}");
            }
            lines.Add(title.ToString());

            // value range over the window (values above clipMax are drawn at the top edge, not used for scaling)
            var inWindow = h.Samples.Where(s => (now - s.t).TotalSeconds <= WindowSeconds).ToList();
            var vals = inWindow.SelectMany(s => idx.Select(i => s.v[i])).Where(x => x.HasValue && (c.clipMax == null || x <= c.clipMax)).Select(x => x.Value).ToList();
            double lo = vals.Count > 0 ? vals.Min() : 0, hi = vals.Count > 0 ? vals.Max() : 1;
            double span = Math.Max(hi - lo, c.minSpan ?? 0);
            if (span < 1e-9) span = Math.Max(Math.Abs(hi) * 0.1, 1);
            double mid = (hi + lo) / 2;
            lo = mid - span / 2; hi = mid + span / 2;
            double padding = span * 0.08; lo -= padding; hi += padding;

            int dotsW = plotW * 2, dotsH = plotH * 4;
            var masks = idx.Select(_ => new bool[dotsW, dotsH]).ToList();
            for (int k = 0; k < idx.Count; k++)
            {
                int prevX = -1, prevY = -1;
                foreach (var s in inWindow)
                {
                    double? v = s.v[idx[k]];
                    if (v == null) { prevX = -1; continue; }
                    double age = (now - s.t).TotalSeconds;
                    int x = (int)Math.Round((1 - age / WindowSeconds) * (dotsW - 1));
                    double vv = Math.Min(v.Value, hi);
                    int y = (int)Math.Round((vv - lo) / (hi - lo) * (dotsH - 1));
                    y = Math.Clamp(y, 0, dotsH - 1);
                    if (x < 0 || x >= dotsW) continue;
                    if (prevX >= 0 && x - prevX <= 3)   // connect to the previous point
                        foreach (var (px, py) in Segment(prevX, prevY, x, y)) masks[k][px, py] = true;
                    else masks[k][x, y] = true;
                    prevX = x; prevY = y;
                }
            }

            for (int row = plotH - 1; row >= 0; row--)
            {
                string label = row == plotH - 1 ? Val(hi, fmt) : row == 0 ? Val(lo, fmt) : "";
                var sb = new StringBuilder($"{Dim}{label,axisW - 1} ┤{Reset}");
                for (int col = 0; col < plotW; col++)
                {
                    int bits = 0, owner = -1;
                    for (int k = 0; k < masks.Count; k++)
                    {
                        int b = Braille(masks[k], col, row);
                        if (b != 0) { bits |= b; owner = k; }   // the later series (actual) wins the color
                    }
                    sb.Append(bits == 0 ? " " : $"{SeriesColor[owner]}{(char)(0x2800 + bits)}{Reset}");
                }
                lines.Add(sb.ToString());
            }
            string left = L.T($"-{WindowSeconds:0} s", $"−{WindowSeconds:0} с"), right = L.T("now", "сейчас");
            lines.Add($"{Dim}{new string(' ', axisW)}{left}{new string(' ', Math.Max(1, plotW - left.Length - right.Length))}{right}{Reset}");
        }
        return lines;
    }

    // Sparkline of one value over the last `seconds`: ▁▂▃▄▅▆▇█, muted gray, the newest cell in the accent color.
    // Codes/bit fields (no unit) get none. A small floor on the range keeps sensor noise flat.
    public static string Spark(History h, int index, Profile.Value v, int width, double seconds = 30)
    {
        if (v.hex || string.IsNullOrEmpty(v.unit) || h.Samples.Count < 2) return "";
        var now = h.Samples[^1].t;
        var bins = new double?[width];
        foreach (var s in h.Samples)
        {
            double age = (now - s.t).TotalSeconds;
            if (age > seconds || s.v[index] == null) continue;
            int b = Math.Clamp((int)((1 - age / seconds) * width), 0, width - 1);
            bins[b] = s.v[index];   // the newest sample of each time slot
        }
        var have = bins.Where(x => x != null).Select(x => x.Value).ToList();
        if (have.Count < 2) return "";
        double lo = have.Min(), hi = have.Max();
        double floor = Math.Max(Math.Abs(have.Average()) * 0.05, 1e-6);
        if (hi - lo < floor) { double mid = (hi + lo) / 2; lo = mid - floor / 2; hi = mid + floor / 2; }
        const string levels = "▁▂▃▄▅▆▇█";
        var sb = new StringBuilder(Dim);
        int last = Array.FindLastIndex(bins, x => x != null);
        for (int i = 0; i < width; i++)
        {
            if (bins[i] == null) { sb.Append(' '); continue; }
            int k = Math.Clamp((int)Math.Round((bins[i].Value - lo) / (hi - lo) * 7), 0, 7);
            if (i == last) sb.Append(Reset).Append(SeriesColor[0]).Append(levels[k]).Append(Reset);
            else sb.Append(levels[k]);
        }
        return sb.Append(Reset).ToString();
    }

    // "Boost target (abs)" -> "target": the part that differs between the two series of a chart
    static string ShortName(string label)
    {
        foreach (var w in new[] { "target", "actual", "задание", "факт" })
            if (label.Contains(w, StringComparison.OrdinalIgnoreCase)) return w;
        return label;
    }

    static string Val(double? v, string fmt) =>
        v == null ? "—" : v.Value.ToString(fmt, CultureInfo.InvariantCulture).Replace('.', L.Ru ? ',' : '.');

    // braille dot bits for the 2x4 block of a cell; y grows upwards in the mask
    static int Braille(bool[,] m, int col, int row)
    {
        int[,] bit = { { 0x40, 0x80 }, { 0x04, 0x20 }, { 0x02, 0x10 }, { 0x01, 0x08 } };   // [dy from top][dx]
        int b = 0;
        for (int dy = 0; dy < 4; dy++)
            for (int dx = 0; dx < 2; dx++)
            {
                int x = col * 2 + dx, y = row * 4 + (3 - dy);
                if (x < m.GetLength(0) && y < m.GetLength(1) && m[x, y]) b |= bit[3 - dy, dx];
            }
        return b;
    }

    static IEnumerable<(int, int)> Segment(int x0, int y0, int x1, int y1)
    {
        int steps = Math.Max(Math.Abs(x1 - x0), Math.Abs(y1 - y0));
        for (int i = 0; i <= steps; i++)
            yield return (x0 + (x1 - x0) * i / Math.Max(1, steps), y0 + (y1 - y0) * i / Math.Max(1, steps));
    }
}
