using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using static BmwDiag.L;

namespace BmwDiag;

// HTML report from a live-view CSV: one self-contained page (no internet needed) with synchronized charts,
// hover values, drag-to-zoom, a summary and a per-second table. Written next to the CSV.
static class Report
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    // charts of the report, by profile "arg"; only those whose columns are in the CSV are shown
    record Spec(string Title, string TitleRu, string[] Args, double? Clip = null, double MinSpan = 0, string Note = null, string NoteRu = null);

    static readonly Spec[] Specs =
    {
        new("Boost pressure", "Наддув", new[] { "SPLAD", "IPLAD" }, MinSpan: 0.3),
        new("Air mass", "Воздух", new[] { "SLMMG", "ILMMG" }, Clip: 1500, MinSpan: 200,
            Note: "target above 1500 (2000 = no EGR regulation) is not drawn", NoteRu: "задание выше 1500 (2000 = регулирование EGR выключено) не рисуется"),
        new("Actuators: EGR valve and turbo", "Управление: клапан EGR и актуатор турбины", new[] { "IAAGR", "IALDS" }, MinSpan: 20),
        new("Temperature before DPF", "Температура перед сажевым фильтром", new[] { "ITAVP1" }, MinSpan: 50),
        new("Soot mass in DPF", "Сажа в фильтре", new[] { "IMRUP" }, MinSpan: 5),
        new("Engine speed", "Обороты", new[] { "INMOT" }, MinSpan: 500),
        new("Vehicle speed", "Скорость", new[] { "IVKMH" }, MinSpan: 20),
        new("Rail pressure", "Давление в рампе", new[] { "SPRDR", "IPRDR" }, MinSpan: 200),
        new("Coolant", "Охлаждающая жидкость", new[] { "ITKUM" }, MinSpan: 20),
    };

    public static string Build(string csvPath, Profile profile)
    {
        var lines = File.ReadAllLines(csvPath);
        if (lines.Length < 3) throw new UsageException(T($"{csvPath}: too few samples for a report", $"{csvPath}: слишком мало замеров для отчёта"));
        var header = SplitCsv(lines[0]);
        int cols = header.Count - 1;

        // columns: "Label (unit)"
        var names = new string[cols];
        var units = new string[cols];
        for (int c = 0; c < cols; c++)
        {
            string h = header[c + 1];
            int k = h.LastIndexOf(" (", StringComparison.Ordinal);
            names[c] = k > 0 && h.EndsWith(")") ? h[..k] : h;
            units[c] = k > 0 && h.EndsWith(")") ? h[(k + 2)..^1] : "";
        }

        // rows
        var t = new List<double>();
        var data = Enumerable.Range(0, cols).Select(_ => new List<double?>()).ToArray();
        var dec = new int[cols];
        double? startSec = null, prev = null, dayShift = 0;
        foreach (var line in lines.Skip(1))
        {
            var f = SplitCsv(line);
            if (f.Count < 2 || !TimeSpan.TryParse(f[0], Inv, out var ts)) continue;
            double sec = ts.TotalSeconds + dayShift.Value;
            if (prev != null && sec < prev - 3600) { dayShift += 86400; sec += 86400; }   // past midnight
            prev = sec;
            startSec ??= sec;
            t.Add(sec - startSec.Value);
            for (int c = 0; c < cols; c++)
            {
                string v = c + 1 < f.Count ? f[c + 1] : "";
                double? d = null;
                if (v.StartsWith("0x") && long.TryParse(v[2..], NumberStyles.HexNumber, Inv, out long hx)) d = hx;
                else if (double.TryParse(v, NumberStyles.Float, Inv, out double x))
                {
                    d = x;
                    int dot = v.IndexOf('.');
                    if (dot >= 0) dec[c] = Math.Min(3, Math.Max(dec[c], v.Length - dot - 1));
                }
                data[c].Add(d);
            }
        }
        if (t.Count < 2) throw new UsageException(T($"{csvPath}: no data rows", $"{csvPath}: нет строк с данными"));

        // profile values -> columns (the CSV header is the label in the language used while recording)
        var byArg = new Dictionary<string, int>();
        if (profile != null)
            foreach (var v in profile.values.Where(v => v.arg != null))
            {
                int c = Array.FindIndex(names, n => n == v.label || n == v.label_ru);
                if (c >= 0) byArg[v.arg] = c;
            }

        var charts = new List<object>();
        foreach (var sp in Specs)
        {
            var idx = sp.Args.Where(byArg.ContainsKey).Select(a => byArg[a]).ToArray();
            if (idx.Length == 0 || idx.All(c => data[c].All(x => x == null))) continue;
            string title = Ru ? sp.TitleRu : sp.Title;
            string unit = units[idx[0]];
            charts.Add(new { title, unit, cols = idx, clip = sp.Clip, minSpan = sp.MinSpan, note = Ru ? sp.NoteRu : sp.Note });
        }
        if (charts.Count == 0)   // unknown profile: every column that changes gets a chart
            for (int c = 0; c < cols && charts.Count < 12; c++)
            {
                var vals = data[c].Where(x => x != null).ToList();
                if (vals.Count > 0 && vals.Max() > vals.Min()) charts.Add(new { title = names[c], unit = units[c], cols = new[] { c }, clip = (double?)null, minSpan = 0.0, note = (string)null });
            }

        // summary tiles
        var tiles = new List<object>();
        void Tile(string en, string ru, string value) => tiles.Add(new { label = T(en, ru), value });
        string Num(double v, int d) => v.ToString("F" + d, Inv).Replace('.', Ru ? ',' : '.');   // invariant globalization: no ru-RU culture
        double dur = t[^1] - t[0];
        Tile("Duration", "Длительность", $"{(int)dur / 60}:{(int)dur % 60:00}");
        int Col(string arg) => byArg.TryGetValue(arg, out int c) ? c : -1;
        (double min, double max, double first, double last)? Stat(string arg)
        {
            int c = Col(arg);
            if (c < 0) return null;
            var v = data[c].Where(x => x != null).Select(x => x.Value).ToList();
            return v.Count == 0 ? null : (v.Min(), v.Max(), v[0], v[^1]);
        }
        if (Stat("IVKMH") is { } sp2)
        {
            double km = 0; int c = Col("IVKMH");
            for (int i = 1; i < t.Count; i++) if (data[c][i] is double a && data[c][i - 1] is double b) km += (a + b) / 2 * (t[i] - t[i - 1]) / 3600;
            Tile("Distance", "Пробег", Num(km, 1) + T(" km", " км"));
            Tile("Max speed", "Макс. скорость", Num(sp2.max, 0) + T(" km/h", " км/ч"));
        }
        if (Stat("INMOT") is { } rpm) Tile("Max engine speed", "Макс. обороты", Num(rpm.max, 0));
        if (Stat("IPLAD") is { } bst) Tile("Max boost (abs)", "Макс. наддув (абс.)", Num(bst.max, 2) + T(" bar", " бар"));
        if (Stat("ITAVP1") is { } dpf) Tile("Max temp. before DPF", "Макс. темп. перед фильтром", Num(dpf.max, 0) + " °C");
        if (Stat("IMRUP") is { } soot) Tile("Soot: start → end", "Сажа: начало → конец", $"{Num(soot.first, 1)} → {Num(soot.last, 1)}" + T(" g", " г"));
        if (Stat("ITKUM") is { } cool) Tile("Coolant: start → end", "Жидкость: начало → конец", $"{Num(cool.first, 0)} → {Num(cool.last, 0)} °C");

        string file = Path.GetFileName(csvPath);
        string title2 = T("Drive report", "Отчёт о поездке") + (profile != null ? " — " + profile.Name : "");
        // date from the file name (live-YYYYMMDD-..., live-mac-YYYYMMDD-...), else the file time
        var m = System.Text.RegularExpressions.Regex.Match(file, @"(20\d{2})(\d{2})(\d{2})");
        string day = m.Success ? $"{m.Groups[1].Value}-{m.Groups[2].Value}-{m.Groups[3].Value}" : File.GetLastWriteTime(csvPath).ToString("yyyy-MM-dd");
        string span = $"{Clock(startSec.Value)}–{Clock(startSec.Value + dur)}";
        var payload = new
        {
            lang = Ru ? "ru-RU" : "en-US",
            title = title2,
            sub = $"{day}, {span} · {t.Count} " + T("samples", "замеров") + $" · {file}",
            footer = "bmw-mac-diag · " + T("data: ", "данные: ") + file,
            startSec = startSec.Value % 86400,
            t = t.Select(x => Math.Round(x, 3)),
            cols = Enumerable.Range(0, cols).Select(c => new { name = names[c], unit = units[c], dec = dec[c], @short = Short(names[c]) }),
            data = data.Select(d => d.Select(x => x == null ? (double?)null : Math.Round(x.Value, 4))),
            charts,
            tiles,
            text = new
            {
                hint = T("Hover a chart for values · drag to zoom · double-click to reset", "Наведите на график — значения · протяните мышкой — увеличить участок · двойной щелчок — сбросить"),
                reset = T("Show everything", "Показать всё"),
                add = T("Add a chart:", "Добавить график:"),
                table = T("Table (one row per second)", "Таблица значений (строка в секунду)"),
                time = T("Time", "Время"),
            },
        };
        // the default encoder escapes <, >, & — safe inside <script>
        string json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { Encoder = JavaScriptEncoder.Create(UnicodeRanges.All) });

        using var res = typeof(Report).Assembly.GetManifestResourceStream("BmwDiag.report-template.html")
                        ?? throw new InvalidOperationException("report template missing");
        string html = new StreamReader(res).ReadToEnd()
            .Replace("__LANG__", Ru ? "ru" : "en")
            .Replace("__TITLE__", System.Net.WebUtility.HtmlEncode(title2))
            .Replace("__DATA__", json);
        string outPath = Path.ChangeExtension(csvPath, ".html");
        File.WriteAllText(outPath, html);
        return outPath;
    }

    static string Clock(double sec)
    {
        int x = (int)Math.Round(sec) % 86400;
        return $"{x / 3600:00}:{x / 60 % 60:00}:{x % 60:00}";
    }

    // "Boost target (abs)" -> "target" for legends of two-series charts
    static string Short(string label)
    {
        foreach (var w in new[] { "target", "actual", "задание", "факт" })
            if (label.Contains(w, StringComparison.OrdinalIgnoreCase)) return w;
        return label;
    }

    static List<string> SplitCsv(string line)
    {
        var r = new List<string>();
        var sb = new System.Text.StringBuilder();
        bool q = false;
        for (int i = 0; i < line.Length; i++)
        {
            char ch = line[i];
            if (q) { if (ch == '"' && i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; } else if (ch == '"') q = false; else sb.Append(ch); }
            else if (ch == '"') q = true;
            else if (ch == ',') { r.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(ch);
        }
        r.Add(sb.ToString());
        return r;
    }
}
