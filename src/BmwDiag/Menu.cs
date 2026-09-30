using static BmwDiag.L;

namespace BmwDiag;

// Interactive menu — what you get when you double-click "BMW Diag.command" or run bmwdiag without arguments.
static class Menu
{
    // basic: work without the SGBD files (standard OBD, bare fault codes); also automatic when there are none
    public static int Run(string ecuFlag, string portFlag, bool trace, bool forceBasic = false)
    {
        // first start (no settings yet): check the BMW files, detect the car, pick or create its profile
        if (Settings.ReadConfig() == null && ecuFlag == null) FirstRun(portFlag, trace);

        while (true)
        {
            var s = Settings.Load(ecuFlag, portFlag, trace);
            Profile p = null;
            string profileError = null;
            try { if (s.ProfilePath != null) p = Profile.Load(s.ProfilePath); }
            catch (Exception ex) { profileError = ex.Message; }

            Clear();
            Console.WriteLine("\x1b[1mbmw-mac-diag\x1b[0m — " + T("BMW diagnostics on macOS", "диагностика BMW на маке") + "\n");
            Console.WriteLine(T("  cable:        ", "  кабель:       ") +
                (s.Port ?? "\x1b[33m" + T("not found — plug the cable into the Mac", "не найден — вставьте кабель в мак") + "\x1b[0m"));
            int n = CountSgbd(s.EcuPath);
            bool basic = forceBasic || n == 0;
            Console.WriteLine(T("  SGBD folder:  ", "  папка SGBD:   ") + s.EcuPath + "  " + (n > 0
                ? T($"({n} files)", $"({n} файлов)")
                : "\x1b[33m" + T("(no .PRG/.GRP files — choose 6)", "(нет файлов .PRG/.GRP — выберите 6)") + "\x1b[0m"));
            if (basic)
                Console.WriteLine(T("  mode:         ", "  режим:        ") + "\x1b[33m" +
                    T("basic, without BMW files — standard OBD values, fault codes without descriptions",
                      "базовый, без файлов BMW — стандартные показания OBD, коды ошибок без расшифровки") + "\x1b[0m");
            else
                Console.WriteLine(T("  car profile:  ", "  профиль:      ") +
                    (p?.Name ?? profileError ?? "\x1b[33m" + T("not chosen — choose 6", "не выбран — выберите 6") + "\x1b[0m"));
            Console.WriteLine();
            if (basic)
            {
                Console.WriteLine(T("  1  Live engine data, standard OBD (screen + CSV log)", "  1  Живые показания двигателя, стандартный OBD (экран + запись CSV)"));
                Console.WriteLine(T("  2  Engine faults (codes)", "  2  Ошибки двигателя (коды)"));
                Console.WriteLine(T("  3  Faults of all control units (codes)", "  3  Ошибки всех блоков (коды)"));
                Console.WriteLine(T("  4  Identify the control units (BMW part numbers)", "  4  Определить блоки (номера деталей BMW)"));
            }
            else
            {
                Console.WriteLine(T("  1  Live engine data (screen + CSV log)", "  1  Живые показания двигателя (экран + запись CSV)"));
                Console.WriteLine(T("  2  Engine faults", "  2  Ошибки двигателя"));
                Console.WriteLine(T("  3  Scan all control units", "  3  Ошибки всех блоков"));
                Console.WriteLine(T("  4  Identify engine control unit", "  4  Определить блок двигателя"));
                Console.WriteLine(T("  5  Clear faults of a control unit (choose from a list)", "  5  Стереть ошибки блока (выбор из списка)"));
            }
            Console.WriteLine(T("  6  Settings (SGBD folder, car profile, language)", "  6  Настройки (папка SGBD, профиль машины, язык)"));
            Console.WriteLine(T("  7  Report of a recording (opens in the browser)", "  7  Отчёт по записи (откроется в браузере)"));
            Console.WriteLine(T("  0  Quit", "  0  Выход"));
            Console.Write(T("\nchoose: ", "\nвыбор: "));
            string choice = Console.ReadLine()?.Trim();
            if (choice == null || choice == "0" || choice.Equals("q", StringComparison.OrdinalIgnoreCase)) return 0;
            if (choice == "6") { Setup(); continue; }
            if (choice is not ("1" or "2" or "3" or "4" or "5" or "7")) continue;

            Clear();
            try
            {
                if (choice == "7") { ChooseRecording(s); goto done; }
                if (s.Port == null) throw new UsageException(T("the cable is not found — plug it into the Mac (bmwdiag ports)",
                                                               "кабель не найден — вставьте его в мак (bmwdiag ports)"));
                if (basic)
                {
                    switch (choice)
                    {
                        case "1": Basic.RunLive(s, Path.Combine(s.LogDir, $"live-obd-{DateTime.Now:yyyyMMdd-HHmmss}.csv"), false); break;
                        case "2": Basic.Faults(s, null); break;
                        case "3": Basic.Scan(s); break;
                        case "4": Basic.IdentAll(s); break;
                        case "5": throw new UsageException(T("clearing faults needs the BMW files (SGBD) — choose 6 and set their folder",
                                                             "для стирания ошибок нужны файлы BMW (SGBD) — выберите 6 и укажите папку с ними"));
                    }
                    goto done;
                }
                if (choice is "1" or "2" or "4" && p == null) throw new UsageException(T("choose your car profile first (6)",
                                                                                          "сначала выберите профиль машины (6)"));
                switch (choice)
                {
                    case "1":
                        string csv = Path.Combine(s.LogDir, $"live-{DateTime.Now:yyyyMMdd-HHmmss}.csv");
                        Live.Run(s, p, csv);
                        AfterLive(csv, p);
                        break;
                    case "2": Commands.Faults(s, p.sgbd, false); break;
                    case "3": ScanAndBrowse(s); break;
                    case "4": Commands.Ident(s, p.sgbd); break;
                    case "5":
                        string unit = ChooseUnit(s, p);
                        if (unit != null) Commands.Clear(s, unit, false);
                        break;
                }
            }
            catch (UsageException ex) { Console.WriteLine(ex.Message); }
            catch (Exception ex) { Console.WriteLine(T("error: ", "ошибка: ") + Session.Explain(ex)); }
            done:
            Console.Write(T("\npress Enter to return to the menu ", "\nнажмите Enter, чтобы вернуться в меню "));
            Console.ReadLine();
        }
    }

    // Ask for the SGBD folder, the car profile and the language; save to ~/.config/bmw-mac-diag/config.json
    public static int Setup()
    {
        var c = Settings.ReadConfig() ?? new Settings.FileConfig { port = "auto" };
        var s = Settings.Load(null, null, false);
        Clear();
        Console.WriteLine("\x1b[1m" + T("Settings", "Настройки") + "\x1b[0m\n");
        Console.WriteLine("\x1b[1m" + T("1/4  Folder with your SGBD files (.PRG/.GRP)", "1/4  Папка с файлами SGBD (.PRG/.GRP)") + "\x1b[0m");
        Console.WriteLine(T($"now: {c.ecuPath ?? s.EcuPath}", $"сейчас: {c.ecuPath ?? s.EcuPath}"));
        Console.WriteLine(T("To change it, type the path or drag the folder from Finder into this window.",
                            "Чтобы поменять — введите путь или перетащите папку из Finder в это окно."));
        Console.Write(T("folder (Enter = keep it as it is): ", "папка (Enter — оставить как есть): "));
        string dir = CleanPath(Console.ReadLine());
        if (dir != "")
        {
            dir = Path.GetFullPath(Settings.Expand(dir));
            int n = CountSgbd(dir);
            Console.WriteLine(Directory.Exists(dir)
                ? T($"  {n} SGBD files found", $"  найдено файлов SGBD: {n}")
                : "  \x1b[33m" + T("folder does not exist", "папка не существует") + "\x1b[0m");
            c.ecuPath = dir;
        }

        Settings.SaveConfig(c);
        s = Settings.Load(null, null, false);
        CheckFiles(s);
        bool detected = false;
        if (CountSgbd(s.EcuPath) > 0)
        {
            Console.WriteLine("\n\x1b[1m" + T("2/4  Detect the car", "2/4  Определение машины") + "\x1b[0m");
            Console.WriteLine(T("Asks the engine unit which it is and picks or creates its profile (cable in, ignition on).",
                                "Программа спросит блок двигателя, какой он, и сама выберет или создаст профиль (кабель вставлен, зажигание включено)."));
            Console.Write(T("detect now? (Enter = yes, n = no, skip): ", "определить сейчас? (Enter — да, н — нет, пропустить): "));
            string yn = Console.ReadLine()?.Trim().ToLowerInvariant();
            if (yn is "" or "y" or "yes" or "д" or "да") detected = Detect(s, c);
        }

        var profiles = AutoProfile.AllProfiles(s).ToArray();
        if (detected)
            Console.WriteLine("\n\x1b[1m" + T("3/4  Car profile", "3/4  Профиль машины") + "\x1b[0m\n" +
                              T("chosen by the detection above — skipped", "выбран при определении машины — шаг пропущен"));
        if (!detected && profiles.Length > 0)
        {
            Console.WriteLine("\n\x1b[1m" + T("3/4  Car profile (which engine / values to show)", "3/4  Профиль машины (какой двигатель и какие показания)") + "\x1b[0m");
            string curProfile = null;
            try { if (c.profile != null) curProfile = Profile.Load(c.profile).Name; } catch { }
            Console.WriteLine(T($"now: {curProfile ?? "not chosen"}", $"сейчас: {curProfile ?? "не выбран"}"));
            for (int i = 0; i < profiles.Length; i++)
            {
                string name;
                try { name = Profile.Load(profiles[i]).Name; } catch { name = T("(cannot read)", "(не читается)"); }
                Console.WriteLine($"  {i + 1}  {name}   [{Path.GetFileName(profiles[i])}]");
            }
            Console.Write(c.profile != null
                ? T("number (Enter = keep the current one): ", "номер (Enter — оставить текущий): ")
                : T("number (Enter = the first one): ", "номер (Enter — первый): "));
            string ans = Console.ReadLine()?.Trim();
            if (int.TryParse(ans, out int k) && k >= 1 && k <= profiles.Length) c.profile = profiles[k - 1];
            else if (string.IsNullOrEmpty(ans) && c.profile == null) c.profile = profiles[0];
        }

        string sys = SystemIsRussian() ? "Русский" : "English";
        string cur = c.language switch { "ru" => "Русский", "en" => "English", _ => T($"auto ({sys})", $"авто ({sys})") };
        Console.WriteLine("\n\x1b[1m" + T("4/4  Language / Язык", "4/4  Язык / Language") + "\x1b[0m");
        Console.WriteLine(T($"now: {cur}", $"сейчас: {cur}"));
        Console.WriteLine(T($"  1  auto — like the system ({sys})", $"  1  авто — как в системе ({sys})"));
        Console.WriteLine("  2  English");
        Console.WriteLine("  3  Русский");
        Console.Write(T("number (Enter = keep it as it is): ", "номер (Enter — оставить как есть): "));
        switch (Console.ReadLine()?.Trim())
        {
            case "1": c.language = "auto"; break;
            case "2": c.language = "en"; break;
            case "3": c.language = "ru"; break;
        }

        Settings.SaveConfig(c);
        Init(null);   // apply the new language right away
        Console.WriteLine(T($"\nsaved to {Settings.ConfigFile}", $"\nсохранено в {Settings.ConfigFile}"));
        return 0;
    }

    // First start: BMW files -> car detection -> profile; everything saved
    static void FirstRun(string portFlag, bool trace)
    {
        var c = new Settings.FileConfig { port = "auto", language = "auto" };
        var s = Settings.Load(null, portFlag, trace);
        Clear();
        Console.WriteLine("\x1b[1m" + T("First start", "Первый запуск") + "\x1b[0m\n");
        c.ecuPath = AskForSgbd(s.EcuPath);
        Settings.SaveConfig(c);
        s = Settings.Load(null, portFlag, trace);
        CheckFiles(s);
        if (CountSgbd(s.EcuPath) > 0) Detect(s, c);
        Settings.SaveConfig(c);
        Console.Write(T("\npress Enter to open the menu ", "\nнажмите Enter, чтобы открыть меню "));
        Console.ReadLine();
    }

    // Repeat until the folder has SGBD files, or the user skips
    static string AskForSgbd(string dir)
    {
        while (CountSgbd(dir) == 0)
        {
            Console.WriteLine("\x1b[33m" + T("No BMW SGBD files (.PRG/.GRP) found in:", "Файлы BMW (SGBD — .PRG/.GRP) не найдены в папке:") + "\x1b[0m");
            Console.WriteLine($"  {dir}\n");
            Console.WriteLine(T("They are not part of this program (BMW property), you have to get them yourself.\n" +
                                "Copy them into the folder above, or type the path / drag the folder with them from Finder into this window.",
                                "Они не входят в программу (это собственность BMW), их нужно найти самостоятельно.\n" +
                                "Скопируйте их в папку выше или введите путь / перетащите папку с ними из Finder в это окно."));
            Console.WriteLine(T("Without them the program works in basic mode: standard OBD engine values and fault codes without descriptions.",
                                "Без них программа работает в базовом режиме: стандартные показания двигателя OBD и коды ошибок без расшифровки."));
            Console.Write(T("folder (Enter = check again, s = skip, basic mode): ", "папка (Enter — проверить снова, s — пропустить, базовый режим): "));
            string line = Console.ReadLine();
            if (line == null) return dir;
            string a = CleanPath(line);
            if (a.Equals("s", StringComparison.OrdinalIgnoreCase)) return dir;
            if (a != "") dir = Path.GetFullPath(Settings.Expand(a));
            Console.WriteLine();
        }
        Console.WriteLine(T($"SGBD folder: {dir} ({CountSgbd(dir)} files)", $"папка SGBD: {dir} (файлов: {CountSgbd(dir)})"));
        return dir;
    }

    // Which important files are there
    static void CheckFiles(Settings s)
    {
        if (CountSgbd(s.EcuPath) == 0) return;
        bool Has(string f) => Directory.EnumerateFiles(s.EcuPath).Any(x => Path.GetFileName(x).Equals(f, StringComparison.OrdinalIgnoreCase));
        string ok = "\x1b[32m✓\x1b[0m", no = "\x1b[33m✗\x1b[0m";
        Console.WriteLine();
        Console.WriteLine(Has("T_GRTB.PRG")
            ? $"  {ok} T_GRTB.PRG — " + T("assignment table, needed to recognise the control units", "таблица соответствий, нужна чтобы узнавать блоки")
            : $"  {no} T_GRTB.PRG — " + T("missing: the car cannot be recognised automatically", "нет: машину не получится определить автоматически"));
        Console.WriteLine(Has("D_MOTOR.GRP")
            ? $"  {ok} D_MOTOR.GRP — " + T("engine group file", "групповой файл двигателя")
            : $"  {no} D_MOTOR.GRP — " + T("missing: the engine cannot be recognised automatically", "нет: двигатель не получится определить автоматически"));
        try
        {
            var units = System.Text.Json.JsonSerializer.Deserialize<List<Commands.Unit>>(File.ReadAllText(s.ScanList));
            var missing = units.Where(u => !Has(u.sgbd)).Select(u => u.sgbd).ToList();
            Console.WriteLine(missing.Count == 0
                ? $"  {ok} " + T($"all {units.Count} unit files of the scan list", $"все {units.Count} файлов блоков из списка опроса")
                : $"  {no} " + T($"scan list: {units.Count - missing.Count} of {units.Count} unit files, missing: {string.Join(", ", missing)}",
                                 $"список опроса: есть {units.Count - missing.Count} из {units.Count}, нет: {string.Join(", ", missing)}"));
        }
        catch { }
    }

    // Detect the engine and pick or create a profile; true if c.profile was set
    static bool Detect(Settings s, Settings.FileConfig c)
    {
        Console.WriteLine();
        if (s.Port == null)
        {
            Console.WriteLine(T("cable not found — plug it into the Mac and the car; detect later: menu 6 or bmwdiag detect",
                                "кабель не найден — вставьте его в мак и в машину; определить позже: пункт 6 меню или bmwdiag detect"));
            return false;
        }
        Console.WriteLine(T("detecting the car (the ignition must be on)...", "определяю машину (зажигание должно быть включено)..."));
        var (outcome, path, msg) = AutoProfile.Detect(s);
        Console.WriteLine("  " + msg.Replace("\n", "\n  "));
        if (path != null) { c.profile = path; return true; }
        if (outcome == AutoProfile.Outcome.NoLink)
            Console.WriteLine(T("  turn the ignition on and detect later: menu 6 or bmwdiag detect",
                                "  включите зажигание и определите позже: пункт 6 меню или bmwdiag detect"));
        return false;
    }

    // bmwdiag detect
    public static int DetectAndSave(Settings s)
    {
        var c = Settings.ReadConfig() ?? new Settings.FileConfig { port = "auto", ecuPath = s.EcuPath };
        CheckFiles(s);
        bool ok = Detect(s, c);
        if (ok) { Settings.SaveConfig(c); Console.WriteLine(T($"saved to {Settings.ConfigFile}", $"сохранено в {Settings.ConfigFile}")); }
        return ok ? 0 : 2;
    }

    // Recent live recordings -> choose -> build the HTML report and open it
    static void ChooseRecording(Settings s)
    {
        var files = Directory.Exists(s.LogDir)
            ? new DirectoryInfo(s.LogDir).GetFiles("live-*.csv").OrderByDescending(f => f.LastWriteTime).Take(10).ToList()
            : new List<FileInfo>();
        if (files.Count == 0) { Console.WriteLine(T("no recordings yet — start live data first (1)", "записей пока нет — сначала запустите живые показания (1)")); return; }
        Console.WriteLine(T("Report — which recording?\n", "Отчёт — по какой записи?\n"));
        for (int i = 0; i < files.Count; i++)
        {
            int rows = Math.Max(0, File.ReadLines(files[i].FullName).Count() - 1);
            Console.WriteLine($"  {i + 1,2}  {files[i].LastWriteTime:dd.MM HH:mm}   " + T($"{rows} samples", $"замеров: {rows}") + $"   {files[i].Name}");
        }
        Console.Write(T("\nnumber (Enter = the latest): ", "\nномер (Enter — последняя): "));
        string a = Console.ReadLine()?.Trim();
        int k = string.IsNullOrEmpty(a) ? 1 : int.TryParse(a, out int x) ? x : 0;
        if (k < 1 || k > files.Count) return;
        OpenReport(s, files[k - 1].FullName);
    }

    // After the live view: build the report of this recording right away and offer to open it
    public static void AfterLive(string csv, Profile p)
    {
        if (csv == null || !File.Exists(csv)) return;
        string html;
        try { html = Report.Build(csv, p); }
        catch (UsageException ex) { Console.WriteLine(ex.Message); return; }   // e.g. too few samples
        catch (Exception ex) { Console.WriteLine(T("report failed: ", "отчёт не получился: ") + ex.Message); return; }
        Console.WriteLine(T($"report: {html}", $"отчёт: {html}"));
        if (Console.IsInputRedirected) return;
        Console.Write(T("open the report? (Enter = open, n = no): ", "открыть отчёт? (Enter — открыть, н — нет): "));
        string a = Console.ReadLine()?.Trim().ToLowerInvariant();
        if (a is "" or "y" or "yes" or "д" or "да")
            try { System.Diagnostics.Process.Start("open", $"\"{html}\""); } catch { }
    }

    // bmwdiag report [csv]
    public static int OpenReport(Settings s, string csv, bool open = true)
    {
        csv ??= Directory.Exists(s.LogDir)
            ? new DirectoryInfo(s.LogDir).GetFiles("live-*.csv").OrderByDescending(f => f.LastWriteTime).FirstOrDefault()?.FullName
            : null;
        if (csv == null || !File.Exists(csv)) throw new UsageException(T("no recording found — start live data first", "запись не найдена — сначала запустите живые показания"));
        Profile p = null;
        if (Path.GetFileName(csv).StartsWith("live-obd-")) p = Basic.ObdProfile();   // recorded in basic mode
        else try { if (s.ProfilePath != null) p = Profile.Load(s.ProfilePath); } catch { }
        string html = Report.Build(csv, p);
        Console.WriteLine(T($"report: {html}", $"отчёт: {html}"));
        if (open) try { System.Diagnostics.Process.Start("open", $"\"{html}\""); } catch { }
        return 0;
    }

    // results of the last scan in this session (for fault counts in the clear list)
    static List<Commands.ScanResult> lastScan;
    static DateTime lastScanTime;

    // Scan all units, then: number -> faults of that unit -> optionally clear them
    static void ScanAndBrowse(Settings s)
    {
        var results = Commands.ScanUnits(s, s.ScanList);
        if (results == null) return;
        lastScan = results; lastScanTime = DateTime.Now;
        while (true)
        {
            Console.Write(T("\nunit number to see its faults (Enter = back): ", "\nномер блока, чтобы посмотреть его ошибки (Enter — назад): "));
            if (!int.TryParse(Console.ReadLine()?.Trim(), out int k) || k < 1 || k > results.Count) return;
            var r = results[k - 1];
            string sgbd = r.Variant != null ? r.Variant + (r.Sgbd.EndsWith(".GRP", StringComparison.OrdinalIgnoreCase) ? ".PRG" : Path.GetExtension(r.Sgbd)) : r.Sgbd;
            Console.WriteLine();
            try
            {
                Commands.Faults(s, sgbd, false);
                if (r.Faults > 0)
                {
                    Console.Write(T("\nclear the faults of this unit? (c = clear, Enter = back to the list): ",
                                    "\nстереть ошибки этого блока? (с — стереть, Enter — к списку): "));
                    string a = Console.ReadLine()?.Trim().ToLowerInvariant();
                    if (a is "c" or "с")   // latin c or cyrillic с
                    {
                        Console.WriteLine();
                        Commands.Clear(s, sgbd, false);
                    }
                }
            }
            catch (Exception ex) { Console.WriteLine(T("error: ", "ошибка: ") + Session.Explain(ex)); }
        }
    }

    // List: engine from the car profile first, then the units of the scan list
    static string ChooseUnit(Settings s, Profile p)
    {
        var units = new List<(string name, string sgbd)>();
        if (p != null) units.Add((T($"Engine ({p.Name})", $"Двигатель ({p.Name})"), p.sgbd));
        try
        {
            foreach (var u in System.Text.Json.JsonSerializer.Deserialize<List<Commands.Unit>>(File.ReadAllText(s.ScanList)))
                if (!(p != null && u.sgbd.Equals("D_MOTOR.GRP", StringComparison.OrdinalIgnoreCase))) units.Add((u.Title, u.sgbd));
        }
        catch (Exception ex) { Console.WriteLine(T($"cannot read the unit list {s.ScanList}: {ex.Message}", $"не читается список блоков {s.ScanList}: {ex.Message}")); }
        Console.WriteLine(T("Clear faults — which control unit?", "Стереть ошибки — какого блока?"));
        Console.WriteLine(lastScan != null
            ? T($"(fault counts from the scan at {lastScanTime:HH:mm})\n", $"(количество ошибок — по опросу в {lastScanTime:HH:mm})\n")
            : T("(fault counts: run 3 'Scan all control units' first)\n", "(чтобы видеть количество ошибок, сначала выполните 3 «Ошибки всех блоков»)\n"));
        for (int i = 0; i < units.Count; i++)
        {
            var sr = lastScan?.FirstOrDefault(r => r.Sgbd.Equals(units[i].sgbd, StringComparison.OrdinalIgnoreCase)
                                                 || (r.Variant != null && units[i].sgbd.StartsWith(r.Variant, StringComparison.OrdinalIgnoreCase)));
            string cnt = sr?.Faults switch
            {
                null when sr != null => T("no answer", "не отвечает"),
                null => "",
                0 => T("no faults", "ошибок нет"),
                int n => "\x1b[33m" + T($"{n} fault(s)", $"ошибок: {n}") + "\x1b[0m",
            };
            Console.WriteLine($"  {i + 1,2}  {units[i].name,-40} {units[i].sgbd,-14} {cnt}");
        }
        Console.Write(T("\nnumber (Enter = back): ", "\nномер (Enter — назад): "));
        return int.TryParse(Console.ReadLine()?.Trim(), out int k) && k >= 1 && k <= units.Count ? units[k - 1].sgbd : null;
    }

    static void Clear() { try { Console.Clear(); } catch (IOException) { } }

    public static int CountSgbd(string dir) =>
        Directory.Exists(dir)
            ? Directory.EnumerateFiles(dir).Count(f => f.EndsWith(".prg", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".grp", StringComparison.OrdinalIgnoreCase))
            : 0;

    // A folder dragged from Finder arrives as '/path/with\ spaces ' or quoted
    static string CleanPath(string p)
    {
        p = (p ?? "").Trim();
        if (p.Length >= 2 && (p[0] == '\'' || p[0] == '"') && p[^1] == p[0]) p = p[1..^1];
        return p.Replace("\\ ", " ");
    }
}
