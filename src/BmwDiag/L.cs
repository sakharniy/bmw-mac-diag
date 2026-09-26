using System.Diagnostics;

namespace BmwDiag;

// Interface language: English or Russian.
// Choice: --lang flag > BMWDIAG_LANG > "language" in config.json > macOS system language (AppleLanguages).
static class L
{
    public static bool Ru { get; private set; }

    public static void Init(string flag)
    {
        string pick = (flag ?? Environment.GetEnvironmentVariable("BMWDIAG_LANG") ?? Settings.ReadConfig()?.language)?.ToLowerInvariant();
        Ru = pick switch { "ru" => true, "en" => false, _ => SystemIsRussian() };
    }

    public static string T(string en, string ru) => Ru ? ru : en;

    // First entry of the macOS preferred languages list, e.g. "ru-NO"; LANG as a fallback (Linux/ssh)
    public static bool SystemIsRussian()
    {
        try
        {
            var psi = new ProcessStartInfo("defaults", "read -g AppleLanguages") { RedirectStandardOutput = true, RedirectStandardError = true };
            using var p = Process.Start(psi);
            string outp = p.StandardOutput.ReadToEnd();
            p.WaitForExit(2000);
            int q = outp.IndexOf('"');
            if (q >= 0) return outp[(q + 1)..].StartsWith("ru", StringComparison.OrdinalIgnoreCase);
        }
        catch { }
        return (Environment.GetEnvironmentVariable("LANG") ?? "").StartsWith("ru", StringComparison.OrdinalIgnoreCase);
    }
}
