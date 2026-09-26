using System.Text.Json;
using EdiabasLib;
using static BmwDiag.L;

namespace BmwDiag;

// Detect the engine control unit and pick or create a live profile for it.
// Detection: IDENT through the engine group file (D_MOTOR.GRP) -> variant, e.g. D72N47B0 (needs a recent T_GRTB.PRG).
// Creation: take the unit's own measurement table (MESSWERTETAB), keep the values we know how to show,
// check them with one real STATUS_MESSWERTBLOCK_LESEN read, save as a profile.
static class AutoProfile
{
    public static string UserProfileDir =>
        Path.Combine(Path.GetDirectoryName(Settings.ConfigFile), "profiles");

    // ARG -> how to show it. Order = order on the screen.
    record Known(string Arg, string Label, string LabelRu, string Group, string GroupRu,
                 string Unit = null, string UnitRu = null, double Scale = 1, string Format = "0", bool Hex = false);

    static readonly Known[] Catalog =
    {
        new("INMOT", "Engine speed", "Обороты", "Engine", "Двигатель", "rpm", "об/мин"),
        new("IVKMH", "Vehicle speed", "Скорость", null, null, "km/h", "км/ч"),
        new("IFPWG", "Accelerator pedal", "Педаль газа", null, null, "%"),
        new("ITKUM", "Coolant", "Охлаждающая жидкость", "Temperatures", "Температуры", "°C"),
        new("ITMOT", "Engine temperature", "Температура двигателя", null, null, "°C"),
        new("ITOEL", "Oil", "Масло", null, null, "°C"),
        new("ITLAL", "Charge air (after IC)", "Воздух после интеркулера", null, null, "°C"),
        new("ITANS", "Intake air", "Воздух на впуске", null, null, "°C"),
        new("ITUMG", "Ambient", "Наружный воздух", null, null, "°C"),
        new("SPLAD", "Boost target (abs)", "Наддув: задание (абс.)", "Turbo", "Турбина", "bar", "бар", 0.001, "0.00"),
        new("IPLAD", "Boost actual (abs)", "Наддув: факт (абс.)", null, null, "bar", "бар", 0.001, "0.00"),
        new("IALDS", "Turbo actuator duty", "Управление актуатором турбины", null, null, "%"),
        new("IPUMG", "Ambient pressure", "Атмосферное давление", null, null, "bar", "бар", 0.001, "0.00"),
        new("SLMMG", "Air mass target", "Воздух: задание", "Air & EGR", "Воздух и EGR", "mg/stroke", "мг/такт"),
        new("ILMMG", "Air mass actual", "Воздух: факт", null, null, "mg/stroke", "мг/такт"),
        new("ILMKG", "Air flow (MAF)", "Расход воздуха", null, null, "kg/h", "кг/ч", 1, "0.0"),
        new("IAAGR", "EGR valve duty", "Управление клапаном EGR", null, null, "%"),
        new("SPRDR", "Rail pressure target", "Рампа: задание", "Fuel", "Топливо", "bar", "бар"),
        new("IPRDR", "Rail pressure actual", "Рампа: факт", null, null, "bar", "бар"),
        new("ITKRS", "Fuel temperature", "Температура топлива", null, null, "°C"),
        new("IMRUP", "Soot mass", "Сажа в фильтре", "Diesel particulate filter", "Сажевый фильтр", "g", "г", 1, "0.0"),
        new("IPDIP", "Differential pressure", "Перепад давления на фильтре", null, null, "mbar", "мбар"),
        new("ITAVP1", "Temperature before DPF", "Температура перед фильтром", null, null, "°C"),
        new("IDSLRE", "Distance since last regen", "Пробег с последнего прожига", null, null, "km", "км", 0.001),
        new("PFltRgn_numRgn", "Regen request status (code)", "Статус запроса прожига (код)", null, null, ""),
        new("ISRBF", "Regen lock (code)", "Блокировка прожига (код)", null, null, ""),
        new("IUBAT", "Battery voltage", "Напряжение бортсети", "Electrical", "Бортсеть", "V", "В", 1, "0.00"),
    };

    public enum Outcome { Found, Created, NoLink, NotDetected, NoMeasurementJob }

    // Returns the profile path (existing or newly created) or null; message says what happened.
    public static (Outcome outcome, string path, string message) Detect(Settings s, string engineGroup = "D_MOTOR.GRP")
    {
        string variant;
        try
        {
            using var ses = new Session(s, engineGroup);
            ses.Run("IDENT");
            variant = ses.Sgbd;
        }
        catch (UsageException ex) { return (Outcome.NotDetected, null, ex.Message); }
        catch (Exception ex)
        {
            string m = Session.Explain(ex);
            bool noLink = m.Contains("IFH_0003") || m.Contains("SYS_0010") || m.Contains("IFH_0018") || m.Contains("IFH_0009");
            return (noLink ? Outcome.NoLink : Outcome.NotDetected, null, m);
        }

        // an existing profile for this variant?
        foreach (var f in AllProfiles(s))
        {
            try
            {
                if (Profile.Load(f).sgbd.Equals(variant + ".PRG", StringComparison.OrdinalIgnoreCase))
                    return (Outcome.Found, f, T($"engine: {variant} — profile found: {Path.GetFileName(f)}",
                                                $"двигатель: {variant} — найден профиль: {Path.GetFileName(f)}"));
            }
            catch { }
        }
        return Create(s, variant);
    }

    public static IEnumerable<string> AllProfiles(Settings s)
    {
        foreach (var dir in new[] { Path.Combine(s.RepoDir, "profiles"), UserProfileDir })
            if (Directory.Exists(dir))
                foreach (var f in Directory.GetFiles(dir, "*.json").OrderBy(x => x)) yield return f;
    }

    static (Outcome, string, string) Create(Settings s, string variant)
    {
        string sgbd = variant + ".PRG";
        using var ses = new Session(s, sgbd);

        var jobs = ses.Run("_JOBS").Skip(1).Select(d => d.TryGetValue("JOBNAME", out var j) ? j.OpData as string : null).ToHashSet();
        if (!jobs.Contains("STATUS_MESSWERTBLOCK_LESEN"))
            return (Outcome.NoMeasurementJob, null, T($"engine: {variant} — this unit has no STATUS_MESSWERTBLOCK_LESEN, a live profile cannot be created automatically",
                                                     $"двигатель: {variant} — у блока нет STATUS_MESSWERTBLOCK_LESEN, профиль автоматически не создать"));

        // MESSWERTETAB: header row has the column names (ARG, RESULTNAME, ...), the next rows are the values
        var rows = ses.Run("_TABLE", "MesswerteTab").Skip(1)
            .Select(d => d.OrderBy(k => ColumnIndex(k.Key)).Where(k => k.Key.StartsWith("COLUMN")).Select(k => k.Value.OpData?.ToString() ?? "").ToList())
            .Where(r => r.Count > 0).ToList();
        int hdr = rows.FindIndex(r => r.Contains("ARG") && r.Contains("RESULTNAME"));
        if (hdr < 0)
            return (Outcome.NoMeasurementJob, null, T($"engine: {variant} — no measurement table in the SGBD", $"двигатель: {variant} — в SGBD нет таблицы показаний"));
        int cArg = rows[hdr].IndexOf("ARG"), cRes = rows[hdr].IndexOf("RESULTNAME");
        var table = rows.Skip(hdr + 1).Where(r => r.Count > Math.Max(cArg, cRes))
                        .GroupBy(r => r[cArg]).ToDictionary(g => g.Key, g => g.First()[cRes]);

        var picked = Catalog.Where(k => table.ContainsKey(k.Arg)).ToList();
        if (picked.Count == 0)
            return (Outcome.NoMeasurementJob, null, T($"engine: {variant} — none of the known values are in its table", $"двигатель: {variant} — в его таблице нет знакомых показаний"));

        // one real read: keep only the values the unit actually returns
        var got = Commands.Data(ses.Run("STATUS_MESSWERTBLOCK_LESEN", "JA;" + string.Join(";", picked.Select(k => k.Arg))));
        if (got.TryGetValue("JOB_STATUS", out var st) && st.OpData as string != "OKAY")
            return (Outcome.NoMeasurementJob, null, T($"engine: {variant} — the unit does not return live values with this SGBD (JOB_STATUS {st.OpData}). Wrong variant file?",
                                                     $"двигатель: {variant} — блок не отдаёт живые показания с этим SGBD (JOB_STATUS {st.OpData}). Не тот вариант файла?"));
        picked = picked.Where(k => got.ContainsKey(table[k.Arg].ToUpperInvariant())).ToList();

        // groups: the first value of each group carries the heading
        var values = new List<Profile.Value>();
        string lastGroup = null, lastGroupRu = null;
        foreach (var k in Catalog)
        {
            if (k.Group != null) { lastGroup = k.Group; lastGroupRu = k.GroupRu; }
            if (!picked.Contains(k)) continue;
            values.Add(new Profile.Value
            {
                arg = k.Arg, result = table[k.Arg], label = k.Label, label_ru = k.LabelRu,
                unit = k.Unit ?? "", unit_ru = k.UnitRu, scale = k.Scale, format = k.Format, hex = k.Hex,
                group = lastGroup, group_ru = lastGroupRu,
            });
            lastGroup = lastGroupRu = null;   // heading only once
        }

        var p = new Profile
        {
            name = $"{variant} (auto)",
            name_ru = $"{variant} (авто)",
            sgbd = sgbd,
            block = new Profile.BlockJob(),
            values = values,
        };
        AddFuel(s, p);

        Directory.CreateDirectory(UserProfileDir);
        string path = Path.Combine(UserProfileDir, $"{variant.ToLowerInvariant()}-auto.json");
        File.WriteAllText(path, JsonSerializer.Serialize(p, new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }));
        return (Outcome.Created, path, T($"engine: {variant} — new profile created with {values.Count} values: {path}",
                                         $"двигатель: {variant} — создан профиль, показаний: {values.Count}: {path}"));
    }

    // Fuel level from the instrument cluster, if the cluster file from the scan list answers STATUS_TANKINHALT
    static void AddFuel(Settings s, Profile p)
    {
        List<Commands.Unit> units;
        try { units = JsonSerializer.Deserialize<List<Commands.Unit>>(File.ReadAllText(s.ScanList)); }
        catch { return; }
        foreach (var u in units.Where(u => u.sgbd.StartsWith("KOMB", StringComparison.OrdinalIgnoreCase) || u.sgbd.StartsWith("D_KOMBI", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                using var ses = new Session(s, u.sgbd);
                var d = Commands.Data(ses.Run("STATUS_TANKINHALT"));
                if (!d.ContainsKey("STAT_GEDAEMPFT_ANZ_WERT")) continue;
                p.extras.Add(new Profile.Extra
                {
                    sgbd = u.sgbd, job = "STATUS_TANKINHALT", everySeconds = 5,
                    values = new()
                    {
                        new() { result = "STAT_GEDAEMPFT_ANZ_WERT", label = "Fuel in tank (as on gauge)", label_ru = "В баке (как на приборке)",
                                unit = "l", unit_ru = "л", format = "0.0", group = "Fuel tank (instrument cluster)", group_ru = "Топливный бак (приборка)" },
                    },
                });
                return;
            }
            catch { }
        }
    }

    static int ColumnIndex(string key) => key.StartsWith("COLUMN") && int.TryParse(key[6..], out int n) ? n : int.MaxValue;
}
