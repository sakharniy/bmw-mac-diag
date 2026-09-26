using System.Globalization;
using System.Text.Json;
using EdiabasLib;
using static BmwDiag.L;

namespace BmwDiag;

static class Commands
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static int ListPorts()
    {
        var ports = Ports.List();
        if (ports.Length == 0)
        {
            Console.WriteLine(T("no USB serial ports found — plug in the K+DCAN cable", "USB-портов не найдено — вставьте кабель K+DCAN"));
            return 2;
        }
        foreach (var p in ports) Console.WriteLine(p);
        return 0;
    }

    public static int Ident(Settings s, string sgbd)
    {
        using var ses = new Session(s, sgbd);
        var r = ses.Run("IDENT");
        Console.WriteLine(T($"variant: {ses.Sgbd}   port: {s.Port}", $"вариант: {ses.Sgbd}   порт: {s.Port}"));
        foreach (var kv in Data(r).OrderBy(k => k.Key, StringComparer.Ordinal))
            if (kv.Key.StartsWith("ID_")) Console.WriteLine($"  {kv.Key,-18} {Format(kv.Value)}");
        return Status(r);
    }

    public static int Faults(Settings s, string sgbd, bool all)
    {
        using var ses = new Session(s, sgbd);
        var r = ses.Run("FS_LESEN");
        PrintFaults(ses.Sgbd, r, all);
        return Status(r);
    }

    static List<Dictionary<string, EdiabasNet.ResultData>> FaultSets(List<Dictionary<string, EdiabasNet.ResultData>> r) =>
        r.Skip(1).Where(d => d.ContainsKey("F_ORT_NR")).ToList();

    static void PrintFaults(string unit, List<Dictionary<string, EdiabasNet.ResultData>> r, bool all)
    {
        var faults = FaultSets(r);
        Console.WriteLine(T($"{unit}: {faults.Count} fault(s)", $"{unit}: ошибок: {faults.Count}"));
        foreach (var f in faults)
        {
            long code = f.TryGetValue("F_ORT_NR", out var c) && c.OpData is long l ? l : -1;
            string text = Text(f, "F_ORT_TEXT");
            // some SGBDs already start the text with the code ("452A Info - ...")
            if (text.StartsWith($"{code:X4}")) text = text[4..].TrimStart(' ', '-');
            Console.WriteLine($"  {code:X4}  {text}");
            Console.WriteLine($"        {Text(f, "F_VORHANDEN_TEXT")}");
            string sym = Text(f, "F_SYMPTOM_TEXT");
            if (sym != "") Console.WriteLine($"        {sym}");
            if (all)
                foreach (var kv in f.OrderBy(k => k.Key, StringComparer.Ordinal))
                    Console.WriteLine($"        {kv.Key}: {Format(kv.Value)}");
        }
    }

    // Before clearing: read the faults, show them, save everything (incl. FS_LESEN_DETAIL per fault) to logs/,
    // ask, clear, read again.
    public static int Clear(Settings s, string sgbd, bool yes)
    {
        using var ses = new Session(s, sgbd);
        var before = ses.Run("FS_LESEN");
        if (Status(before) != 0) { Console.WriteLine(T("cannot read the fault memory — not clearing", "память ошибок не читается — стирать не буду")); return 3; }
        PrintFaults(ses.Sgbd, before, false);
        var faults = FaultSets(before);
        if (faults.Count == 0) { Console.WriteLine(T("no stored faults — nothing to clear", "ошибок нет — стирать нечего")); return 0; }

        Directory.CreateDirectory(s.LogDir);
        string file = Path.Combine(s.LogDir, $"faults-before-clear-{ses.Sgbd}-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
        using (var w = new StreamWriter(file))
        {
            w.WriteLine($"{ses.Sgbd} fault memory before clearing, {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            foreach (var f in faults)
            {
                w.WriteLine();
                foreach (var kv in f.OrderBy(k => k.Key, StringComparer.Ordinal)) w.WriteLine($"{kv.Key}: {Format(kv.Value)}");
                // details (mileage when stored, how often, ...) if the unit supports it
                if (f.TryGetValue("F_ORT_NR", out var c) && c.OpData is long code)
                {
                    try
                    {
                        foreach (var d in ses.Run("FS_LESEN_DETAIL", $"0x{code:X4}").Skip(1))
                            foreach (var kv in d.OrderBy(k => k.Key, StringComparer.Ordinal))
                                if (kv.Key.StartsWith("F_") && !f.ContainsKey(kv.Key)) w.WriteLine($"  detail {kv.Key}: {Format(kv.Value)}");
                    }
                    catch { /* no FS_LESEN_DETAIL in this SGBD */ }
                }
            }
        }
        Console.WriteLine(T($"\nsaved to {file}", $"\nошибки сохранены в {file}"));

        if (!yes)
        {
            Console.Write(T($"\nClear ALL {faults.Count} stored fault(s) of {ses.Sgbd}? This cannot be undone. Type 'yes': ",
                            $"\nСтереть ВСЕ ошибки блока {ses.Sgbd} ({faults.Count} шт.)? Отменить будет нельзя. Введите 'да' (или 'yes'): "));
            string a = Console.ReadLine()?.Trim().ToLowerInvariant();
            if (a != "yes" && a != "да") { Console.WriteLine(T("cancelled, nothing cleared", "отменено, ничего не стёрто")); return 1; }
        }
        var r = ses.Run("FS_LOESCHEN");
        if (Status(r) != 0) { Console.WriteLine(T("clear FAILED", "стереть НЕ УДАЛОСЬ")); return 3; }
        var after = ses.Run("FS_LESEN");
        int left = FaultSets(after).Count;
        Console.WriteLine(T($"fault memory cleared. read again: {left} fault(s)", $"ошибки стёрты. прочитано заново: {left}") +
                          (left > 0 ? T(" — these are active right now and came back immediately", " — они активны прямо сейчас и сразу вернулись") : ""));
        if (left > 0) PrintFaults(ses.Sgbd, after, false);
        return 0;
    }

    public static int Jobs(Settings s, string sgbd)
    {
        using var ses = new Session(s, sgbd);
        var r = ses.Run("_JOBS");
        foreach (var d in r.Skip(1))
            if (d.TryGetValue("JOBNAME", out var j)) Console.WriteLine(j.OpData);
        return 0;
    }

    public static int Job(Settings s, string sgbd, string job, string args)
    {
        using var ses = new Session(s, sgbd);
        Console.WriteLine(T("variant: ", "вариант: ") + $"{ses.Sgbd}   job: {job}" + (args != null ? T($"   args: {args}", $"   аргументы: {args}") : ""));
        var r = ses.Run(job, args);
        Dump(r);
        return Status(r);
    }

    // Scan list: JSON array of { "name": "...", "sgbd": "..." }
    public record Unit(string name, string sgbd, string name_ru = null)
    {
        // not "Name": System.Text.Json would bind it to the "name" constructor parameter too
        public string Title => L.Ru && name_ru != null ? name_ru : name;
    }

    // Result of one unit in a scan: Faults == null when the unit did not answer / could not be read
    public record ScanResult(string Title, string Sgbd, string Variant, int? Faults, string Status);

    public static int Scan(Settings s, string listFile) => ScanUnits(s, listFile) == null ? 2 : 0;

    // Numbered table with fault counts; returns null if the car does not answer at all
    public static List<ScanResult> ScanUnits(Settings s, string listFile)
    {
        var units = JsonSerializer.Deserialize<List<Unit>>(File.ReadAllText(listFile));
        Directory.CreateDirectory(s.LogDir);
        string log = Path.Combine(s.LogDir, $"scan-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
        using var w = new StreamWriter(log);
        var results = new List<ScanResult>();
        bool anyAnswer = false;
        for (int i = 0; i < units.Count; i++)
        {
            var u = units[i];
            Console.Write($"{i + 1,3}  {u.Title,-28} {u.sgbd,-14} ");
            w.WriteLine($"===== {u.Title} [{u.sgbd}] =====");
            string st, variant = null;
            int? count = null;
            try
            {
                using var ses = new Session(s, u.sgbd);
                variant = ses.Sgbd;
                var r = ses.Run("FS_LESEN");
                anyAnswer = true;
                foreach (var d in r.Skip(1))
                    foreach (var kv in d.OrderBy(k => k.Key, StringComparer.Ordinal)) w.WriteLine($"{kv.Key}: {Format(kv.Value)}");
                int n = r.Skip(1).Count(d => d.ContainsKey("F_ORT_NR"));
                if (Status(r, quiet: true) != 0) st = T("READ ERROR (see log)", "ОШИБКА ЧТЕНИЯ (см. лог)");
                else
                {
                    count = n;
                    st = n == 0 ? T("OK, no faults", "OK, ошибок нет") : "\x1b[33m" + T($"{n} fault(s)", $"ошибок: {n}") + "\x1b[0m";
                    if (n > 0 && !variant.Equals(Path.GetFileNameWithoutExtension(u.sgbd), StringComparison.OrdinalIgnoreCase)) st += $"  [{variant}]";
                }
            }
            catch (Exception ex)
            {
                string m = Session.Explain(ex);
                w.WriteLine(m);
                // no answer from the very first unit -> the cable/car is not talking at all, stop early
                if (!anyAnswer && (m.Contains("IFH_0003") || m.Contains("SYS_0010") || m.Contains("IFH_0018")))
                {
                    Console.WriteLine(T("NO LINK", "НЕТ СВЯЗИ"));
                    Console.WriteLine(m);
                    return null;
                }
                st = m.Contains("IFH_0009") ? T("no answer", "не отвечает") : m.Contains("No variant") ? T("variant not found", "вариант не найден") : T("error (see log)", "ошибка (см. лог)");
            }
            Console.WriteLine(st);
            results.Add(new ScanResult(u.Title, u.sgbd, variant, count, st));
        }
        w.WriteLine("===== SUMMARY =====");
        foreach (var r in results) w.WriteLine($"{r.Title,-28} {System.Text.RegularExpressions.Regex.Replace(r.Status, @"\x1b\[[0-9;]*m", "")}");
        Console.WriteLine(T($"full log: {log}", $"полный лог: {log}"));
        return results;
    }

    // Repeat a job, write every numeric *_WERT result to CSV
    public static int Watch(Settings s, string sgbd, string job, string args, int seconds, string logPath)
    {
        using var ses = new Session(s, sgbd);
        StreamWriter log = null;
        if (logPath != null) { Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(logPath))); log = new StreamWriter(logPath) { AutoFlush = true }; }
        var sw = System.Diagnostics.Stopwatch.StartNew();
        List<string> header = null;
        int n = 0;
        while (sw.Elapsed.TotalSeconds < seconds)
        {
            var row = new Dictionary<string, string>();
            try
            {
                foreach (var kv in Data(ses.Run(job, args)))
                    if (kv.Key.EndsWith("_WERT") && kv.Value.OpData is double or long)
                        row[kv.Key] = Convert.ToDouble(kv.Value.OpData).ToString("0.###", Inv);
            }
            catch (Exception ex) { Console.Error.WriteLine(Session.Explain(ex)); Thread.Sleep(1000); continue; }
            if (header == null)
            {
                header = row.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
                string h = "time," + string.Join(",", header);
                Console.WriteLine(h); log?.WriteLine(h);
            }
            string line = DateTime.Now.ToString("HH:mm:ss.fff") + "," + string.Join(",", header.Select(k => row.GetValueOrDefault(k, "")));
            Console.WriteLine(line); log?.WriteLine(line);
            n++;
        }
        log?.Dispose();
        Console.Error.WriteLine(T($"{n} samples", $"замеров: {n}") + (logPath != null ? $", CSV: {logPath}" : ""));
        return 0;
    }

    // Raw KWP2000 telegrams to the unit of the SGBD. Only reading services are allowed.
    static readonly byte[] ReadServices = { 0x1A, 0x21, 0x22, 0x2C, 0x18, 0x17, 0x3E };

    public static int Raw(Settings s, string sgbd, string requests)
    {
        // check everything before connecting: only reading services may be sent
        foreach (var req in requests.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            byte[] d = Hex.Parse(req);
            if (d.Length == 0 || d.Length > 0x3F || !ReadServices.Contains(d[0]))
                throw new UsageException(T($"refused: {req.Trim()} — only read services {Hex.Format(ReadServices)} are allowed", $"отказ: {req.Trim()} — разрешены только сервисы чтения {Hex.Format(ReadServices)}"));
        }
        using var ses = new Session(s, sgbd);
        // run IDENT so the SGBD sets the communication parameters; take the unit address from its telegram
        var id = ses.Run("IDENT");
        byte ecu = Data(id).TryGetValue("_TEL_AUFTRAG", out var t) && t.OpData is byte[] tb && tb.Length > 1 ? tb[1] : (byte)0x12;
        int rc = 0;
        foreach (var req in requests.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            byte[] data = Hex.Parse(req);
            byte[] send = new byte[] { (byte)(0x80 | data.Length), ecu, 0xF1 }.Concat(data).ToArray();
            bool ok = ses.Ediabas.EdInterfaceClass.TransmitData(send, out byte[] recv);
            Console.WriteLine($"> {Hex.Format(send)}");
            Console.WriteLine($"< {(ok && recv != null ? Hex.Format(recv) : T("no answer", "нет ответа"))}");
        }
        return rc;
    }

    // ---- result helpers ----

    // All result fields of the job (datasets after the first one), last value wins
    public static Dictionary<string, EdiabasNet.ResultData> Data(List<Dictionary<string, EdiabasNet.ResultData>> r)
    {
        var d = new Dictionary<string, EdiabasNet.ResultData>();
        foreach (var set in r?.Skip(1) ?? Enumerable.Empty<Dictionary<string, EdiabasNet.ResultData>>())
            foreach (var kv in set) d[kv.Key] = kv.Value;
        return d;
    }

    static string Text(Dictionary<string, EdiabasNet.ResultData> d, string key) =>
        d.TryGetValue(key, out var v) ? v.OpData?.ToString() ?? "" : "";

    // Prints JOB_STATUS if it is not OKAY; returns 0 on OKAY
    static int Status(List<Dictionary<string, EdiabasNet.ResultData>> r, bool quiet = false)
    {
        string st = Data(r).TryGetValue("JOB_STATUS", out var v) ? v.OpData as string : null;
        if (st == "OKAY") return 0;
        if (!quiet) Console.WriteLine($"JOB_STATUS: {st ?? "(none)"}");
        return 3;
    }

    public static void Dump(List<Dictionary<string, EdiabasNet.ResultData>> sets)
    {
        for (int n = 0; n < sets.Count; n++)
        {
            Console.WriteLine("DATASET: " + n);
            foreach (var kv in sets[n].OrderBy(k => k.Key, StringComparer.Ordinal))
                Console.WriteLine($"{kv.Key}: {Format(kv.Value)}");
        }
    }

    public static string Format(EdiabasNet.ResultData r)
    {
        switch (r.OpData)
        {
            case null: return "";
            case string str: return str;
            case byte[] b: return Hex.Format(b);
            case double d: return d.ToString("0.######", Inv);
            case long l:
                int bytes = r.ResType switch
                {
                    EdiabasNet.ResultType.TypeB or EdiabasNet.ResultType.TypeC => 1,
                    EdiabasNet.ResultType.TypeW or EdiabasNet.ResultType.TypeI => 2,
                    EdiabasNet.ResultType.TypeD or EdiabasNet.ResultType.TypeL => 4,
                    _ => 8,
                };
                ulong mask = bytes == 8 ? ulong.MaxValue : (1UL << (bytes * 8)) - 1;
                return $"{l} (0x{((ulong)l & mask).ToString("X" + bytes * 2)})";
            default: return r.OpData.ToString();
        }
    }
}
