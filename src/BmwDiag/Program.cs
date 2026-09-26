// bmwdiag — BMW diagnostics on macOS with a cheap K+DCAN (FTDI) cable.
// Copyright (C) 2026 bmw-mac-diag contributors. GPL-3.0-or-later, see LICENSE.
// Uses EdiabasLib by Ulrich Holeschak (GPL-3.0): https://github.com/uholeschak/ediabaslib
using BmwDiag;
using static BmwDiag.L;

const string UsageEn = """
bmwdiag — BMW diagnostics on macOS (EdiabasLib + K+DCAN cable)

usage: bmwdiag                     interactive menu (same as double-clicking "BMW Diag.command")
       bmwdiag <command> [arguments] [options]

  setup                          choose the SGBD folder, car profile and language (saved for next time)
  detect                         detect the engine (cable + ignition) and pick or create its live profile
  ports                          list serial ports (find your cable)
  ident  <SGBD>                  identify a control unit            e.g. ident D_MOTOR.GRP
  faults <SGBD> [--all]          read fault memory (FS_LESEN)        e.g. faults KOMB87.PRG
  clear  <SGBD> [--yes]          CLEAR fault memory (FS_LOESCHEN): saves the faults first, asks for confirmation
  scan   [list.json]             read faults of all units in a list (default: from setup, scan/e9x.json)
  live   [profile.json]          live values on screen + CSV log (default: profile from setup)
  report [file.csv]              HTML report of a live recording, opens in the browser (default: the latest)
  jobs   <SGBD>                  list the jobs a unit supports
  job    <SGBD> <JOB> [args]     run any job, print all results     e.g. job D_MOTOR.GRP _ARGUMENTS FS_LESEN_DETAIL
  watch  <SGBD> <JOB> [args]     repeat a job, write numeric results to CSV (--seconds N)
  raw    <SGBD> "<hex>[;<hex>]"  send raw KWP2000 requests (read services only)

options:
  --port <dev>     serial port (default: auto, /dev/cu.usbserial-*)
  --ecu <dir>      folder with SGBD files (.PRG/.GRP)   (default: <repo>/ecu)
  --log <file>     CSV file for live/watch (default: logs/<command>-<time>.csv), --no-log to disable
  --seconds <n>    duration for watch (default 60)
  --trace          write EDIABAS communication trace to <repo>/trace/
  --yes            do not ask for confirmation (clear)
  --all            faults: print every result field
  --graphs         live: start with the graphs (key G switches)
  --no-open        report: only write the HTML, do not open the browser
  --lang en|ru     interface language (default: macOS system language)

settings can also come from BMWDIAG_ECU / BMWDIAG_PORT / BMWDIAG_LANG or ~/.config/bmw-mac-diag/config.json
""";

const string UsageRu = """
bmwdiag — диагностика BMW на маке (EdiabasLib + кабель K+DCAN)

запуск: bmwdiag                    меню (то же, что двойной щелчок по «BMW Diag.command»)
        bmwdiag <команда> [аргументы] [опции]

  setup                          выбрать папку SGBD, профиль машины и язык (запоминается)
  detect                         определить двигатель (кабель + зажигание) и выбрать или создать профиль
  ports                          список последовательных портов (найти кабель)
  ident  <SGBD>                  определить блок управления          напр. ident D_MOTOR.GRP
  faults <SGBD> [--all]          прочитать ошибки (FS_LESEN)          напр. faults KOMB87.PRG
  clear  <SGBD> [--yes]          СТЕРЕТЬ ошибки (FS_LOESCHEN): сначала сохраняет их, спрашивает подтверждение
  scan   [list.json]             ошибки всех блоков из списка (по умолчанию: из настроек, scan/e9x.json)
  live   [profile.json]          живые показания на экране + запись CSV (по умолчанию: профиль из настроек)
  report [файл.csv]              отчёт по записи в браузере (по умолчанию: последняя запись)
  jobs   <SGBD>                  список job'ов блока
  job    <SGBD> <JOB> [аргументы]  любой job, все результаты         напр. job D_MOTOR.GRP _ARGUMENTS FS_LESEN_DETAIL
  watch  <SGBD> <JOB> [аргументы]  повторять job, числовые результаты в CSV (--seconds N)
  raw    <SGBD> "<hex>[;<hex>]"  сырые запросы KWP2000 (только чтение)

опции:
  --port <dev>     последовательный порт (по умолчанию: авто, /dev/cu.usbserial-*)
  --ecu <папка>    папка с файлами SGBD (.PRG/.GRP)   (по умолчанию: <проект>/ecu)
  --log <файл>     CSV для live/watch (по умолчанию: logs/<команда>-<время>.csv), --no-log — без записи
  --seconds <n>    длительность watch (по умолчанию 60)
  --trace          трассировка обмена EDIABAS в <проект>/trace/
  --yes            не спрашивать подтверждение (clear)
  --all            faults: вывести все поля результата
  --graphs         live: сразу открыть графики (клавиша G переключает)
  --no-open        report: только создать HTML, не открывать браузер
  --lang en|ru     язык интерфейса (по умолчанию: язык системы)

настройки также можно задать через BMWDIAG_ECU / BMWDIAG_PORT / BMWDIAG_LANG или ~/.config/bmw-mac-diag/config.json
""";

// language first, so that even --help and argument errors come in the right language
string langFlag = null;
for (int i = 0; i + 1 < args.Length; i++) if (args[i] == "--lang") langFlag = args[i + 1];
Init(langFlag);
string Usage = Ru ? UsageRu : UsageEn;

var pos = new List<string>();
string portFlag = null, ecuFlag = null, logFlag = null;
bool trace = false, yes = false, all = false, noLog = false, graphs = false, noOpen = false;
int seconds = 60;
try
{
    for (int i = 0; i < args.Length; i++)
    {
        string Next() => i + 1 < args.Length ? args[++i] : throw new UsageException(T($"{args[i]} needs a value", $"после {args[i]} нужно значение"));
        switch (args[i])
        {
            case "--port": portFlag = Next(); break;
            case "--ecu": ecuFlag = Next(); break;
            case "--log": logFlag = Next(); break;
            case "--no-log": noLog = true; break;
            case "--seconds": seconds = int.Parse(Next()); break;
            case "--lang": Next(); break;
            case "--trace": trace = true; break;
            case "--yes": yes = true; break;
            case "--all": all = true; break;
            case "--graphs": graphs = true; break;
            case "--no-open": noOpen = true; break;
            case "-h": case "--help": Console.Write(Usage); return 0;
            default:
                if (args[i].StartsWith("--")) throw new UsageException(T($"unknown option {args[i]}", $"неизвестная опция {args[i]}"));
                pos.Add(args[i]); break;
        }
    }
}
catch (UsageException ex) { Console.Error.WriteLine(ex.Message); return 1; }

if (pos.Count == 0)
{
    // started without arguments (e.g. double-click on "BMW Diag.command") -> interactive menu
    if (!Console.IsInputRedirected) return Menu.Run(ecuFlag, portFlag, trace);
    Console.Write(Usage);
    return 1;
}

string cmd = pos[0];
string Arg(int n, string name) => pos.Count > n ? pos[n] : throw new UsageException(T($"missing {name} — see bmwdiag --help", $"не хватает {name} — см. bmwdiag --help"));

try
{
    if (cmd == "ports") return Commands.ListPorts();
    if (cmd == "setup") return Menu.Setup();
    if (cmd == "menu") return Menu.Run(ecuFlag, portFlag, trace);
    if (cmd == "report") return Menu.OpenReport(Settings.Load(ecuFlag, portFlag, trace), pos.Count > 1 ? pos[1] : null, !noOpen);

    var s = Settings.Load(ecuFlag, portFlag, trace);
    if (s.Port == null)
    {
        Console.Error.WriteLine(T("no serial port found — plug in the cable (it shows up as /dev/cu.usbserial-*), or use --port",
                                  "порт не найден — вставьте кабель в мак (он появляется как /dev/cu.usbserial-*) или укажите --port"));
        return 2;
    }
    if (!Directory.Exists(s.EcuPath))
    {
        Console.Error.WriteLine(T($"SGBD folder not found: {s.EcuPath} — put .PRG/.GRP files there or use --ecu (see README)",
                                  $"папка SGBD не найдена: {s.EcuPath} — положите туда файлы .PRG/.GRP или укажите --ecu (см. README)"));
        return 2;
    }
    string Log(string name) => noLog ? null : logFlag ?? Path.Combine(s.LogDir, $"{name}-{DateTime.Now:yyyyMMdd-HHmmss}.csv");

    // live view, then the report of the recording
    int RunLive()
    {
        var prof = Profile.Load(pos.Count > 1 ? pos[1] : s.ProfilePath ?? throw new UsageException(
                      T("which profile? bmwdiag live profiles/<car>.json, or choose one in bmwdiag setup",
                        "какой профиль? bmwdiag live profiles/<машина>.json или выберите его в bmwdiag setup")));
        string csv = Log("live");
        int rc = Live.Run(s, prof, csv, graphs);
        Menu.AfterLive(csv, prof);
        return rc;
    }

    return cmd switch
    {
        "detect" => Menu.DetectAndSave(s),
        "ident" => Commands.Ident(s, Arg(1, "SGBD")),
        "faults" => Commands.Faults(s, Arg(1, "SGBD"), all),
        "clear" => Commands.Clear(s, Arg(1, "SGBD"), yes),
        "scan" => Commands.Scan(s, pos.Count > 1 ? pos[1] : s.ScanList),
        "live" => RunLive(),
        "jobs" => Commands.Jobs(s, Arg(1, "SGBD")),
        "job" => Commands.Job(s, Arg(1, "SGBD"), Arg(2, "JOB"), pos.Count > 3 ? pos[3] : null),
        "watch" => Commands.Watch(s, Arg(1, "SGBD"), Arg(2, "JOB"), pos.Count > 3 ? pos[3] : null, seconds, Log("watch")),
        "raw" => Commands.Raw(s, Arg(1, "SGBD"), Arg(2, T("hex requests", "hex-запросов"))),
        _ => throw new UsageException(T($"unknown command '{cmd}' — see bmwdiag --help", $"неизвестная команда '{cmd}' — см. bmwdiag --help")),
    };
}
catch (UsageException ex)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}
catch (Exception ex)
{
    Console.Error.WriteLine(T("error: ", "ошибка: ") + Session.Explain(ex));
    return 3;
}
