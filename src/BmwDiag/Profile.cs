using System.Text.Json;
using System.Text.Json.Serialization;

namespace BmwDiag;

// Live-view profile (profiles/*.json): which SGBD, which values, how to show them.
//
// Values with "arg" are read together through one "block" job (e.g. STATUS_MESSWERTBLOCK_LESEN of BMW
// diesel DDE units: first call "JA;ARG1;ARG2..." defines the block in the unit, next calls "NEIN;..." only read it).
// Values with "job" are read with their own job every cycle. "extras" are read from other control units
// every few seconds (slow values like fuel level).
sealed class Profile
{
    public string name { get; set; }
    public string name_ru { get; set; }
    [JsonIgnore] public string Name => L.Ru && name_ru != null ? name_ru : name;
    public string sgbd { get; set; }
    public BlockJob block { get; set; }
    public List<Value> values { get; set; } = new();
    public List<Extra> extras { get; set; } = new();
    public List<Chart> charts { get; set; }      // live graphs; if missing, the usual pairs are used (Charts.For)

    public sealed class Chart
    {
        public string title { get; set; }
        public string title_ru { get; set; }
        public List<string> series { get; set; } = new();   // "arg" (or "result") of up to two values, same unit
        public double? clipMax { get; set; }                // ignore bigger values for scaling (e.g. 2000 = "no target")
        public double? minSpan { get; set; }                // smallest value range of the axis, so noise does not fill the chart
        [JsonIgnore] public string Title => L.Ru && title_ru != null ? title_ru : title;
    }

    public sealed class BlockJob
    {
        public string job { get; set; } = "STATUS_MESSWERTBLOCK_LESEN";
        public string firstPrefix { get; set; } = "JA;";
        public string nextPrefix { get; set; } = "NEIN;";
    }

    public sealed class Value
    {
        public string arg { get; set; }        // argument name in the block job (table MESSWERTETAB, column ARG)
        public string job { get; set; }        // or: separate job to run
        public string jobArgs { get; set; }
        public string result { get; set; }     // result name, e.g. STAT_MOTORDREHZAHL_WERT
        public string label { get; set; }
        public string unit { get; set; } = "";
        public string group { get; set; }      // heading printed before this value
        // Russian texts (used when the interface language is Russian)
        public string label_ru { get; set; }
        public string unit_ru { get; set; }
        public string group_ru { get; set; }
        [JsonIgnore] public string Label => L.Ru && label_ru != null ? label_ru : label;
        [JsonIgnore] public string Unit => L.Ru && unit_ru != null ? unit_ru : unit;
        [JsonIgnore] public string Group => L.Ru && group_ru != null ? group_ru : group;
        public double scale { get; set; } = 1;
        public string format { get; set; } = "0";
        public bool hex { get; set; }          // result is a hex string (bit field) -> show 0x........
    }

    public sealed class Extra
    {
        public string sgbd { get; set; }
        public string job { get; set; }
        public string jobArgs { get; set; }
        public int everySeconds { get; set; } = 5;
        public List<Value> values { get; set; } = new();
    }

    public static Profile Load(string file)
    {
        if (!File.Exists(file)) throw new UsageException(L.T($"profile not found: {file}", $"профиль не найден: {file}"));
        var p = JsonSerializer.Deserialize<Profile>(File.ReadAllText(file),
            new JsonSerializerOptions { ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        if (string.IsNullOrEmpty(p?.sgbd) || p.values.Count == 0) throw new UsageException(L.T($"profile {file}: needs \"sgbd\" and \"values\"", $"профиль {file}: нужны \"sgbd\" и \"values\""));
        if (p.values.Any(v => v.arg != null) && p.block == null) p.block = new BlockJob();
        return p;
    }
}
