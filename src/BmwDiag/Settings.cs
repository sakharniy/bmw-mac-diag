using System.Text.Json;

namespace BmwDiag;

// Where the SGBD files are and which serial port to use.
// Priority: command-line flag > environment variable > ~/.config/bmw-mac-diag/config.json > default.
sealed class Settings
{
    public string EcuPath;
    public string Port;
    public bool Trace;
    public string TraceDir;
    public string LogDir;
    public string RepoDir;
    public string ProfilePath;   // car profile for live / engine faults (menu)
    public string ScanList;

    public static string ConfigFile =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "bmw-mac-diag", "config.json");

    public sealed class FileConfig
    {
        public string ecuPath { get; set; }
        public string port { get; set; }
        public string profile { get; set; }
        public string scanList { get; set; }
        public string language { get; set; }   // "auto" (system), "en", "ru"
    }

    public static FileConfig ReadConfig()
    {
        if (!File.Exists(ConfigFile)) return null;
        try { return JsonSerializer.Deserialize<FileConfig>(File.ReadAllText(ConfigFile)); }
        catch (Exception ex) { Console.Error.WriteLine($"warning / внимание: cannot read {ConfigFile}: {ex.Message}"); return null; }
    }

    public static void SaveConfig(FileConfig c)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ConfigFile));
        File.WriteAllText(ConfigFile, JsonSerializer.Serialize(c, new JsonSerializerOptions { WriteIndented = true }));
    }

    public static Settings Load(string ecuFlag, string portFlag, bool trace)
    {
        // the binary lives in <repo>/bin/
        string repo = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, ".."));
        FileConfig fc = ReadConfig();
        var s = new Settings
        {
            RepoDir = repo,
            Trace = trace,
            TraceDir = Path.Combine(repo, "trace"),
            LogDir = Path.Combine(repo, "logs"),
            EcuPath = Expand(ecuFlag ?? Environment.GetEnvironmentVariable("BMWDIAG_ECU") ?? fc?.ecuPath ?? Path.Combine(repo, "ecu")),
            ProfilePath = fc?.profile != null ? Expand(fc.profile) : null,   // set by detection (bmwdiag detect) or setup
            ScanList = fc?.scanList != null ? Expand(fc.scanList) : Path.Combine(repo, "scan", "e9x.json"),
        };
        string port = portFlag ?? Environment.GetEnvironmentVariable("BMWDIAG_PORT") ?? fc?.port;
        s.Port = string.IsNullOrEmpty(port) || port == "auto" ? Ports.AutoDetect() : port;
        return s;
    }

    public static string Expand(string p) =>
        p.StartsWith("~/") ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), p[2..]) : p;
}

static class Ports
{
    // FTDI-based K+DCAN cables show up as /dev/cu.usbserial-<chip serial> with the built-in macOS driver
    public static string[] List() =>
        Directory.GetFiles("/dev", "cu.usbserial*").Concat(Directory.GetFiles("/dev", "cu.usbmodem*")).OrderBy(p => p).ToArray();

    public static string AutoDetect()
    {
        var ports = List();
        if (ports.Length == 0) return null;
        if (ports.Length > 1)
            Console.Error.WriteLine(L.T($"note: several serial ports found, using {ports[0]} (choose with --port)", $"найдено несколько портов, используется {ports[0]} (другой — через --port)"));
        return ports[0];
    }
}
