using System.Text.Json;
using static BmwDiag.L;

namespace BmwDiag;

// Basic mode: diagnostics without BMW's SGBD files. What the car gives without them:
//  - live engine values by standard OBD (SAE J1979), the ones the engine says it supports;
//  - fault memory of every unit as bare BMW codes (hex) with their state — the texts are in the SGBDs;
//  - standard OBD fault codes (P-codes) of the engine;
//  - identification: BMW part number, date, diagnosis index and variant.
// Clearing is not offered: it is a write request and has not been tried without the SGBDs.
static class Basic
{
    const byte Engine = 0x12;   // DDE/DME address on the E-series bus

    // ---------- live values: standard OBD ----------

    public record Pid(byte Id, int Bytes, Func<byte[], double> Calc, string Label, string LabelRu,
                      string Unit, string UnitRu, string Format = "0", string Group = null, string GroupRu = null)
    {
        public string Arg => "OBD_" + Id.ToString("X2");
    }

    static double W(byte[] b) => b[0] * 256 + b[1];
    static double Pct(byte[] b) => b[0] * 100 / 255.0;
    static double Temp(byte[] b) => b[0] - 40;
    static IEnumerable<Pid> Group(string en, string ru, params Pid[] p) => p.Select(x => x with { Group = en, GroupRu = ru });

    // SAE J1979 formulas, in screen order; values the engine does not support are left out
    public static readonly Pid[] Catalog = new[]
    {
        Group("Engine", "Двигатель",
            new Pid(0x0C, 2, b => W(b) / 4, "Engine speed", "Обороты", "rpm", "об/мин"),
            new Pid(0x04, 1, Pct, "Engine load (calculated)", "Нагрузка (расчётная)", "%", "%"),
            new Pid(0x49, 1, Pct, "Accelerator pedal", "Педаль газа", "%", "%"),
            new Pid(0x05, 1, Temp, "Coolant temperature", "Температура охлаждающей жидкости", "°C", "°C"),
            new Pid(0x5C, 1, Temp, "Oil temperature", "Температура масла", "°C", "°C"),
            new Pid(0x1F, 2, W, "Time since engine start", "Время с запуска двигателя", "s", "с")),
        Group("Air and boost", "Воздух и наддув",
            new Pid(0x0B, 1, b => b[0] / 100.0, "Intake manifold pressure (abs.)", "Давление во впуске (абс.)", "bar", "бар", "0.00"),
            new Pid(0x33, 1, b => b[0] / 100.0, "Barometric pressure", "Атмосферное давление", "bar", "бар", "0.00"),
            new Pid(0x10, 2, b => W(b) / 100 * 3.6, "Air mass flow", "Расход воздуха", "kg/h", "кг/ч"),
            new Pid(0x0F, 1, Temp, "Intake air temperature", "Температура воздуха на впуске", "°C", "°C"),
            new Pid(0x46, 1, Temp, "Outside temperature", "Температура снаружи", "°C", "°C"),
            new Pid(0x11, 1, Pct, "Throttle valve", "Дроссельная заслонка", "%", "%"),
            new Pid(0x2C, 1, Pct, "EGR commanded", "EGR: задание", "%", "%"),
            new Pid(0x2D, 1, b => (b[0] - 128) * 100 / 128.0, "EGR error", "EGR: отклонение", "%", "%")),
        Group("Fuel", "Топливо",
            new Pid(0x23, 2, b => W(b) / 10, "Fuel rail pressure", "Давление в топливной рампе", "bar", "бар"),
            new Pid(0x5E, 2, b => W(b) / 20, "Fuel rate", "Расход топлива", "l/h", "л/ч", "0.0"),
            new Pid(0x2F, 1, Pct, "Fuel level", "Уровень топлива", "%", "%")),
        Group("Exhaust", "Выхлоп",
            new Pid(0x3C, 2, b => W(b) / 10 - 40, "Catalyst temperature", "Температура катализатора", "°C", "°C")),
        Group("Car", "Машина",
            new Pid(0x0D, 1, b => b[0], "Vehicle speed", "Скорость", "km/h", "км/ч"),
            new Pid(0x42, 2, b => W(b) / 1000, "Control unit voltage", "Напряжение на блоке двигателя", "V", "В", "0.0"),
            new Pid(0x21, 2, W, "Distance with the engine lamp on", "Пробег с горящей лампой двигателя", "km", "км"),
            new Pid(0x31, 2, W, "Distance since faults were cleared", "Пробег после стирания ошибок", "km", "км")),
    }.SelectMany(g => g).ToArray();

    // Live profile of these values (default: the whole catalog — for reports of earlier recordings)
    public static Profile ObdProfile(IReadOnlyList<Pid> pids = null)
    {
        pids ??= Catalog;
        var p = new Profile { name = "Engine — standard OBD (basic mode)", name_ru = "Двигатель — стандартный OBD (базовый режим)" };
        string group = null;
        foreach (var d in pids)
        {
            bool head = d.Group != group;
            p.values.Add(new Profile.Value
            {
                arg = d.Arg, label = d.Label, label_ru = d.LabelRu, unit = d.Unit, unit_ru = d.UnitRu, format = d.Format,
                group = head ? d.Group : null, group_ru = head ? d.GroupRu : null,
            });
            group = d.Group;
        }
        var have = pids.Select(d => d.Arg).ToHashSet();
        p.charts = new List<Profile.Chart>
        {
            new() { title = "Engine speed", title_ru = "Обороты", series = new() { "OBD_0C" }, minSpan = 1000 },
            new() { title = "Intake manifold pressure (abs.)", title_ru = "Давление во впуске (абс.)", series = new() { "OBD_0B" }, minSpan = 0.3 },
            new() { title = "Air mass flow", title_ru = "Расход воздуха", series = new() { "OBD_10" }, minSpan = 50 },
            new() { title = "Coolant", title_ru = "Охлаждающая жидкость", series = new() { "OBD_05" }, minSpan = 20 },
        }.Where(c => have.Contains(c.series[0])).Take(3).ToList();
        return p;
    }

    // Live view + report, like the full mode
    public static int RunLive(Settings s, string csv, bool graphs)
    {
        Console.WriteLine(T("basic mode: asking the engine which standard OBD values it gives...",
                            "базовый режим: спрашиваю двигатель, какие стандартные показания OBD он отдаёт..."));
        var link = new RawLink(s);
        Pid[] pids;
        bool fast;
        try { (pids, fast) = DetectPids(link); }
        catch (Exception ex) { link.Dispose(); throw new UsageException(NoLink(ex)); }
        if (pids.Length == 0)
        {
            link.Dispose();
            throw new UsageException(T("the engine gives none of the known OBD values", "двигатель не отдаёт ни одного из известных показаний OBD"));
        }
        var p = ObdProfile(pids);
        RawLink first = link;   // the first data source goes on with the link that is already open
        int rc;
        try { rc = Live.Run(s, p, csv, graphs, () => { var l = first; first = null; return new ObdSource(s, pids, fast, l); }); }
        finally { first?.Dispose(); }
        Menu.AfterLive(csv, p);
        return rc;
    }

    // Which catalog values the engine supports (mode 01 PIDs 00/20/40/60 are bit masks of the next 32),
    // and whether BMW's 2C 10 reads them all in one request (much faster than one OBD request per value)
    static (Pid[] pids, bool fast) DetectPids(RawLink link)
    {
        var supported = new HashSet<int>();
        for (int b = 0; b <= 0x60; b += 0x20)
        {
            if (b > 0 && !supported.Contains(b)) break;
            var r = link.Request(Engine, 0x01, (byte)b);
            if (r.Length < 6 || r[0] != 0x41 || r[1] != b) break;
            for (int k = 0; k < 32; k++)
                if ((r[2 + k / 8] & (0x80 >> (k % 8))) != 0) supported.Add(b + k + 1);
        }
        var pids = Catalog.Where(d => supported.Contains(d.Id)).ToArray();
        bool fast = false;
        if (pids.Length > 0)
            try { ReadFast(link, pids, new double?[pids.Length]); fast = true; }
            catch (LinkLostException) { throw; }
            catch { /* not supported this way: one request per value */ }
        return (pids, fast);
    }

    // 2C 10 00 <pid> 00 <pid>... -> 6C 10 + the raw OBD bytes of every PID, in order
    static void ReadFast(RawLink link, Pid[] pids, double?[] v)
    {
        var r = link.Request(Engine, new byte[] { 0x2C, 0x10 }.Concat(pids.SelectMany(d => new byte[] { 0, d.Id })).ToArray());
        if (r.Length != 2 + pids.Sum(d => d.Bytes) || r[0] != 0x6C || r[1] != 0x10) throw new Exception("2C 10: unexpected answer " + Hex.Format(r));
        for (int i = 0, at = 2; i < pids.Length; at += pids[i].Bytes, i++) v[i] = pids[i].Calc(r[at..(at + pids[i].Bytes)]);
    }

    // 01 <pid> -> 41 <pid> <bytes>, one request per value
    static void ReadOneByOne(RawLink link, Pid[] pids, double?[] v, Func<bool> abort)
    {
        Exception last = null;
        int ok = 0;
        for (int i = 0; i < pids.Length && !abort(); i++)
        {
            try
            {
                var r = link.Request(Engine, 0x01, pids[i].Id);
                v[i] = r.Length >= 2 + pids[i].Bytes && r[0] == 0x41 && r[1] == pids[i].Id ? pids[i].Calc(r[2..(2 + pids[i].Bytes)]) : null;
                ok++;
            }
            catch (LinkLostException) { throw; }
            catch (Exception ex) { v[i] = null; last = ex; }
        }
        if (ok == 0 && last != null) throw last;
    }

    sealed class ObdSource(Settings s, Pid[] pids, bool fast, RawLink first) : ILiveSource
    {
        RawLink link = first;
        public bool Abort { get; set; }

        public void ReadCycle(double?[] v)
        {
            link ??= new RawLink(s);
            if (fast) ReadFast(link, pids, v);
            else ReadOneByOne(link, pids, v, () => Abort);
        }

        public void Dispose() => link?.Dispose();
    }

    // ---------- control units ----------

    public record Dtc(int Code, byte Status)
    {
        // KWP2000 status of a fault: bits 5-6 = storage state (11 present now, 01 stored, 10 intermittent); bit 7 = warning lamp
        public int State => (Status >> 5) & 3;
        public string StateText => State switch
        {
            3 => T("active now", "активна сейчас"),
            1 => T("stored, not present now", "в памяти, сейчас нет"),
            2 => T("intermittent", "появляется временами"),
            _ => "—",
        };
    }

    // 58 N, then per fault: code (2 bytes) + status
    static List<Dtc> ParseFaults(byte[] r)
    {
        if (r.Length < 2 || r[0] != 0x58) throw new Exception("fault memory: unexpected answer " + Hex.Format(r));
        var list = new List<Dtc>();
        for (int i = 0, at = 2; i < r[1] && at + 2 < r.Length; i++, at += 3) list.Add(new Dtc(r[at] << 8 | r[at + 1], r[at + 2]));
        return list;
    }

    // OBD mode 03: "43 N" + 2 bytes per code on CAN; K-line style has no count and pads with 00 00
    static List<string> ParsePCodes(byte[] r)
    {
        if (r.Length < 1 || r[0] != 0x43) throw new Exception("OBD 03: unexpected answer " + Hex.Format(r));
        int start = r.Length >= 2 && r[1] * 2 == r.Length - 2 ? 2 : 1;
        var list = new List<string>();
        for (int i = start; i + 1 < r.Length; i += 2)
            if (r[i] != 0 || r[i + 1] != 0) list.Add($"{"PCBU"[r[i] >> 6]}{(r[i] >> 4) & 3}{r[i] & 0xF:X}{r[i + 1]:X2}");
        return list;
    }

    public record Ident(string PartNo, string Date, string Diag, string Variant);

    // 5A 80, BMW part number (6 bytes BCD), 2 bytes, diagnosis index (2), variant index (2, letters), 20, date (BCD yy mm dd), ...
    // (layout checked against the SGBD IDENT results of the test car: 8519671, 0x10, "WE", 23.04.2010)
    static Ident ParseIdent(byte[] r)
    {
        if (r.Length < 18 || r[0] != 0x5A || r[1] != 0x80) return null;
        string part = Hex.Format(r[2..8]).Replace(" ", "").TrimStart('0');
        static bool Bcd(byte b) => b >> 4 < 10 && (b & 0xF) < 10;
        string date = r[14] == 0x20 && r[15..18].All(Bcd) ? $"20{r[15]:X2}-{r[16]:X2}-{r[17]:X2}" : "";
        static bool Letter(byte b) => b is >= 0x20 and < 0x7F;
        string variant = Letter(r[12]) && Letter(r[13]) ? $"{(char)r[12]}{(char)r[13]}" : $"{r[12]:X2}{r[13]:X2}";
        return new Ident(part.All(char.IsDigit) ? part : Hex.Format(r[2..8]), date, $"{r[10]:X2}{r[11]:X2}", variant);
    }

    record Unit(string Title, byte Addr);

    // The scan list with the bus address of every unit ("addr")
    static List<Unit> Units(Settings s)
    {
        var list = JsonSerializer.Deserialize<List<Commands.Unit>>(File.ReadAllText(s.ScanList))
            .Where(u => u.addr != null).Select(u => new Unit(u.Title, Convert.ToByte(u.addr.Replace("0x", ""), 16))).ToList();
        if (list.Count == 0) throw new UsageException(T($"{s.ScanList}: the units have no \"addr\" (bus address) — needed without BMW files",
                                                        $"{s.ScanList}: у блоков нет \"addr\" (адреса на шине) — без файлов BMW он нужен"));
        return list;
    }

    // "D_MOTOR.GRP", "KOMB87.PRG", "KOMB87", "0x60" or "60" -> unit; null -> engine
    static Unit FindUnit(Settings s, string arg)
    {
        var raw = JsonSerializer.Deserialize<List<Commands.Unit>>(File.ReadAllText(s.ScanList)).Where(u => u.addr != null).ToList();
        var units = Units(s);
        if (arg == null) return units.FirstOrDefault(u => u.Addr == Engine) ?? new Unit(T("Engine", "Двигатель"), Engine);
        int k = raw.FindIndex(u => u.sgbd.Equals(arg, StringComparison.OrdinalIgnoreCase)
                                || Path.GetFileNameWithoutExtension(u.sgbd).Equals(Path.GetFileNameWithoutExtension(arg), StringComparison.OrdinalIgnoreCase));
        if (k >= 0) return units[k];
        string h = arg.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? arg[2..] : arg;
        if (h.Length is 1 or 2 && byte.TryParse(h, System.Globalization.NumberStyles.HexNumber, null, out byte a))
            return units.FirstOrDefault(u => u.Addr == a) ?? new Unit($"0x{a:X2}", a);
        throw new UsageException(T($"unknown unit {arg} — use a name from {Path.GetFileName(s.ScanList)} or a bus address like 0x60",
                                   $"неизвестный блок {arg} — укажите имя из {Path.GetFileName(s.ScanList)} или адрес на шине, напр. 0x60"));
    }

    // identification (optional) + fault memory of one unit; every request and answer goes to the log
    static (Ident id, List<Dtc> faults) ReadUnit(RawLink link, byte addr, StreamWriter log)
    {
        byte[] Ask(params byte[] req)
        {
            log?.WriteLine("> " + Hex.Format(req));
            try { var r = link.Request(addr, req); log?.WriteLine("< " + Hex.Format(r)); return r; }
            catch (Exception ex) { log?.WriteLine("< " + ex.Message); throw; }
        }
        Ident id = null;
        try { id = ParseIdent(Ask(0x1A, 0x80)); }
        catch (Exception ex) when (ex is not LinkLostException && !ex.Message.Contains("IFH_0009")) { /* the fault memory matters more */ }
        return (id, ParseFaults(Ask(0x18, 0x02, 0xFF, 0xFF)));
    }

    static List<string> PCodes(RawLink link, StreamWriter log)
    {
        log?.WriteLine("> 03 (OBD)");
        try { var r = link.Request(Engine, 0x03); log?.WriteLine("< " + Hex.Format(r)); return ParsePCodes(r); }
        catch (Exception ex) { log?.WriteLine("< " + ex.Message); return null; }
    }

    static string PCodesLine(List<string> pc) =>
        T("Engine, standard OBD codes (P-codes): ", "Двигатель, стандартные коды OBD (P-коды): ") +
        (pc == null ? T("could not be read", "не прочитались") : pc.Count == 0 ? T("none", "нет") : "\x1b[33m" + string.Join(", ", pc) + "\x1b[0m");

    static string FaultsLine(List<Dtc> f)
    {
        int active = f.Count(x => x.State == 3);
        return f.Count == 0 ? T("OK, no faults", "OK, ошибок нет")
            : "\x1b[33m" + T($"{f.Count} fault(s)", $"ошибок: {f.Count}") + (active > 0 ? T($", {active} active now", $", активных сейчас: {active}") : "") + "\x1b[0m";
    }

    static readonly string Tip = T(
        "Basic mode (no BMW files): the codes are BMW's own, in hex, without descriptions — look a code up by its number\n" +
        "and the unit. The descriptions and clearing need the SGBD files.",
        "Базовый режим (без файлов BMW): коды — собственные коды BMW (шестнадцатеричные), без расшифровки — ищите по номеру\n" +
        "и названию блока. Расшифровка и стирание — только с файлами SGBD.");

    // the first request of a command is the link check: say plainly what is wrong
    static string NoLink(Exception ex) =>
        ex is UsageException or LinkLostException ? ex.Message :
        ex.Message.Contains("IFH_0018") ? Session.Explain(ex) :
        T("the engine unit does not answer — ignition on? K+DCAN switch in the D-CAN position (cars after 03/2008)?",
          "блок двигателя не отвечает — зажигание включено? переключатель на кабеле в положении D-CAN (машины после 03/2008)?") + $"\n  ({ex.Message})";

    // bmwdiag scan --basic / menu 3: all units, fault codes without texts, engine P-codes
    public static int Scan(Settings s)
    {
        var units = Units(s);
        Directory.CreateDirectory(s.LogDir);
        string logFile = Path.Combine(s.LogDir, $"scan-basic-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
        using var log = new StreamWriter(logFile);
        using var link = new RawLink(s);
        Console.WriteLine(T("All control units — basic mode, fault codes without descriptions\n", "Все блоки — базовый режим, коды ошибок без расшифровки\n"));
        var found = new List<(string title, List<Dtc> faults)>();
        List<string> pcodes = null;
        bool anyAnswer = false;
        for (int i = 0; i < units.Count; i++)
        {
            var u = units[i];
            Console.Write($"{i + 1,3}  {u.Title,-28} ");
            log.WriteLine($"===== {u.Title} [0x{u.Addr:X2}] =====");
            string st, part = "";
            try
            {
                var (id, faults) = ReadUnit(link, u.Addr, log);
                anyAnswer = true;
                part = id?.PartNo ?? "";
                st = FaultsLine(faults);
                if (faults.Count > 0) found.Add((u.Title, faults));
                if (u.Addr == Engine) pcodes = PCodes(link, log);
            }
            catch (Exception ex)
            {
                // no answer from the first unit (the engine) -> the car is not talking at all, stop early
                if (!anyAnswer) { Console.WriteLine(T("NO LINK", "НЕТ СВЯЗИ")); Console.WriteLine(NoLink(ex)); return 2; }
                if (ex is LinkLostException) { Console.WriteLine(ex.Message); break; }
                st = ex.Message.Contains("IFH_0009") ? T("no answer", "не отвечает") : T("error (see log)", "ошибка (см. лог)");
            }
            Console.WriteLine($"{part,-8} {st}");
        }
        if (found.Count > 0)
        {
            Console.WriteLine("\n" + T("Fault codes:", "Коды ошибок:"));
            foreach (var (title, faults) in found)
            {
                Console.WriteLine($"  {title}");
                foreach (var f in faults) Console.WriteLine($"    {f.Code:X4}   {f.StateText}");
            }
        }
        if (anyAnswer && units.Any(u => u.Addr == Engine)) Console.WriteLine("\n" + PCodesLine(pcodes));
        Console.WriteLine("\n" + Tip);
        Console.WriteLine(T($"full log: {logFile}", $"полный лог: {logFile}"));
        return 0;
    }

    // bmwdiag faults --basic [unit] / menu 2: one unit (default: engine)
    public static int Faults(Settings s, string unit)
    {
        var u = FindUnit(s, unit);
        using var link = new RawLink(s);
        Console.WriteLine($"{u.Title} — " + T("fault codes, basic mode (without descriptions)", "коды ошибок, базовый режим (без расшифровки)") + "\n");
        List<Dtc> faults;
        try { faults = ReadUnit(link, u.Addr, null).faults; }
        catch (Exception ex) { throw new UsageException(u.Addr == Engine ? NoLink(ex) : Session.Explain(ex)); }
        Console.WriteLine(FaultsLine(faults));
        foreach (var f in faults) Console.WriteLine($"  {f.Code:X4}   {f.StateText}");
        if (u.Addr == Engine) Console.WriteLine("\n" + PCodesLine(PCodes(link, null)));
        Console.WriteLine("\n" + Tip);
        return 0;
    }

    // bmwdiag ident --basic / menu 4: part numbers of all units
    public static int IdentAll(Settings s)
    {
        var units = Units(s);
        using var link = new RawLink(s);
        Console.WriteLine(T("Control units — basic mode\n", "Блоки управления — базовый режим\n"));
        Console.WriteLine($"     {T("unit", "блок"),-28} {T("BMW part no.", "номер BMW"),-12} {T("date", "дата"),-11} {T("diag. index / variant", "индекс диагн. / вариант")}");
        bool anyAnswer = false;
        for (int i = 0; i < units.Count; i++)
        {
            var u = units[i];
            Console.Write($"{i + 1,3}  {u.Title,-28} ");
            try
            {
                var id = ParseIdent(link.Request(u.Addr, 0x1A, 0x80));
                anyAnswer = true;
                Console.WriteLine(id == null ? T("answer not recognised", "ответ не распознан") : $"{id.PartNo,-12} {id.Date,-11} {id.Diag} / {id.Variant}");
            }
            catch (Exception ex)
            {
                if (!anyAnswer) { Console.WriteLine(T("NO LINK", "НЕТ СВЯЗИ")); Console.WriteLine(NoLink(ex)); return 2; }
                if (ex is LinkLostException) { Console.WriteLine(ex.Message); break; }
                Console.WriteLine(ex.Message.Contains("IFH_0009") ? T("no answer", "не отвечает") : T("error: ", "ошибка: ") + ex.Message);
            }
        }
        return 0;
    }
}
