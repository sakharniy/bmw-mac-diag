using System.Globalization;
using EdiabasLib;
using static BmwDiag.L;

namespace BmwDiag;

// Live values in the terminal (redrawn in place) + CSV log, driven by a Profile.
static class Live
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static int Run(Settings s, Profile p, string logPath, bool startWithGraphs = false)
    {
        bool stop = false;
        ConsoleCancelEventHandler onCtrlC = (_, e) => { e.Cancel = true; stop = true; };
        Console.CancelKeyPress += onCtrlC;
        var all = p.values.Concat(p.extras.SelectMany(x => x.values)).ToList();
        // own screen (like top/less): nothing scrolls, the previous window content comes back on exit;
        // the Terminal window is resized to fit the live view and restored afterwards
        int oldRows = Rows(), oldCols = Width();
        int needRows = Math.Max(LineCount(all), Charts.MinRows(Charts.For(p).Count)) + 1;
        Console.Write("\x1b[?1049h\x1b[?25l" + $"\x1b[8;{needRows};{Math.Max(oldCols, 90)}t" + "\x1b[2J");
        int samples = 0;
        try { samples = Loop(s, p, all, logPath, () => stop, startWithGraphs); }
        finally
        {
            Console.CancelKeyPress -= onCtrlC;
            Console.Write($"\x1b[0m\x1b[?25h\x1b[?1049l\x1b[8;{oldRows};{oldCols}t");
        }
        Console.WriteLine(T($"stopped, {samples} samples", $"остановлено, замеров: {samples}") + (logPath != null ? $", CSV: {logPath}" : ""));
        return 0;
    }

    static int Loop(Settings s, Profile p, List<Profile.Value> all, string logPath, Func<bool> stopRequested, bool startWithGraphs)
    {

        var values = new double?[all.Count];
        StreamWriter log = null;
        if (logPath != null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(logPath)));
            log = new StreamWriter(logPath) { AutoFlush = true };
            log.WriteLine("time," + string.Join(",", all.Select(v => Csv($"{v.Label} ({v.Unit})"))));
        }

        string blockArgs = string.Join(";", p.values.Where(v => v.arg != null).Select(v => v.arg));
        var extraTimers = p.extras.Select(_ => (System.Diagnostics.Stopwatch)null).ToArray();
        Session ses = null;
        bool defined = false;
        int samples = 0;
        var rate = System.Diagnostics.Stopwatch.StartNew();
        string status = T("connecting...", "подключение...");
        var history = new Charts.History();
        bool graphs = startWithGraphs;

        // Every reading cycle runs in a worker task and the screen waits for it at most CycleTimeoutMs:
        // if the USB cable drops out (it did on the test car — "hardware connection lost"), EdiabasLib can block
        // inside a job; then the session is abandoned and a new one is opened when the cable is back.
        const int CycleTimeoutMs = 6000;
        bool quit = false;
        try
        {
        while (!stopRequested() && !quit)
        {
            // keys: G — numbers/graphs (П on the Russian layout), Q — quit
            while (!Console.IsInputRedirected && Console.KeyAvailable)
            {
                var k = Console.ReadKey(true).KeyChar;
                if ("gGпП".Contains(k)) { graphs = !graphs; Console.Write("\x1b[2J"); }
                if ("qQйЙ".Contains(k)) quit = true;
            }
            if (quit) break;

            if (s.Port != null && !File.Exists(s.Port))
            {
                Abandon(ref ses);
                defined = false;
                status = T("the cable is disconnected from the Mac (USB) — plug it back in, the recording continues by itself",
                           "кабель отключился от мака (USB) — вставьте его обратно, запись продолжится сама");
                Draw(p, all, values, history, graphs, status, logPath, stale: true);
                Thread.Sleep(500);
                continue;
            }

            var next = (double?[])values.Clone();          // the worker fills a copy; taken over only on success
            var cur = ses;
            bool def = defined;
            var work = Task.Run(() =>
            {
                cur ??= new Session(s, p.sgbd);
                // slow values from other units, every N seconds
                for (int x = 0; x < p.extras.Count; x++)
                {
                    if (cur.Abort) return;
                    if (extraTimers[x] != null && extraTimers[x].Elapsed.TotalSeconds < p.extras[x].everySeconds) continue;
                    var ex = p.extras[x];
                    try
                    {
                        cur.Use(ex.sgbd);
                        Read(Commands.Data(cur.Run(ex.job, ex.jobArgs)), ex.values, all, next);
                    }
                    catch { /* keep the last value; the main unit matters more */ }
                    finally
                    {
                        if (!cur.Abort) cur.Use(p.sgbd);
                        def = false;   // the block must be defined again after switching units
                        extraTimers[x] = System.Diagnostics.Stopwatch.StartNew();
                    }
                }
                if (p.block != null && blockArgs.Length > 0)
                {
                    var r = Commands.Data(cur.Run(p.block.job, (def ? p.block.nextPrefix : p.block.firstPrefix) + blockArgs));
                    string js = r.TryGetValue("JOB_STATUS", out var st) ? st.OpData as string : null;
                    if (js != "OKAY") throw new Exception($"{p.block.job}: JOB_STATUS {js}");
                    def = true;
                    Read(r, p.values.Where(v => v.arg != null), all, next);
                }
                foreach (var g in p.values.Where(v => v.job != null).GroupBy(v => (v.job, v.jobArgs)))
                    Read(Commands.Data(cur.Run(g.Key.job, g.Key.jobArgs)), g, all, next);
            });

            // wait, but keep the screen and the keys alive (polling: Task.Wait would throw on a failed cycle)
            var waited = System.Diagnostics.Stopwatch.StartNew();
            while (!work.IsCompleted)
            {
                Thread.Sleep(50);
                if (!Console.IsInputRedirected && Console.KeyAvailable && "qQйЙ".Contains(Console.ReadKey(true).KeyChar)) { quit = true; break; }
                if (stopRequested() || waited.ElapsedMilliseconds > CycleTimeoutMs) break;
            }
            if (!work.IsCompleted)
            {
                // hanging job: give up this session, the next cycle opens a new one
                if (cur != null) cur.Abort = true;
                ses = cur;
                Abandon(ref ses);
                defined = false;
                if (quit || stopRequested()) break;
                status = T("no answer — waiting for the car / cable...", "нет ответа — жду машину / кабель...");
                Draw(p, all, values, history, graphs, status, logPath, stale: true);
                continue;
            }
            ses = cur;
            if (work.IsFaulted)
            {
                var ex = work.Exception.GetBaseException();
                defined = false;
                if (ex.Message.Contains("IFH_0018") || ex.Message.Contains("SYS_0010") || ex.Message.Contains("IFH_0003")) Abandon(ref ses);
                status = T("no data: ", "нет данных: ") + Session.Explain(ex).Replace("\n", " ");
                Draw(p, all, values, history, graphs, status, logPath, stale: true);
                Thread.Sleep(1000);
                continue;
            }
            defined = def;
            values = next;
            samples++;
            history.Add(values);
            status = $"{samples / Math.Max(rate.Elapsed.TotalSeconds, 0.001):0.0} " + T("samples/s", "замеров/с");
            log?.WriteLine(DateTime.Now.ToString("HH:mm:ss.fff") + "," +
                string.Join(",", all.Select((v, i) => CsvValue(values[i], v))));
            Draw(p, all, values, history, graphs, status, logPath, stale: false);
        }
        }
        finally
        {
            Abandon(ref ses, waitMs: 2000);   // close the connection so the port is free for the next start
            log?.Dispose();
        }
        return samples;
    }

    // Close a session without letting a stuck EdiabasLib call block the caller
    static void Abandon(ref Session ses, int waitMs = 0)
    {
        if (ses == null) return;
        var old = ses;
        ses = null;
        old.Abort = true;
        var t = Task.Run(() => { try { old.Dispose(); } catch { } });
        if (waitMs > 0) t.Wait(waitMs);
    }

    static void Read(Dictionary<string, EdiabasNet.ResultData> r, IEnumerable<Profile.Value> which,
                     List<Profile.Value> all, double?[] values)
    {
        foreach (var v in which)
        {
            int i = all.IndexOf(v);
            if (!r.TryGetValue(v.result.ToUpperInvariant(), out var d) && !r.TryGetValue(v.result, out d)) { values[i] = null; continue; }
            values[i] = d.OpData switch
            {
                double x => x * v.scale,
                long x => x * v.scale,
                string h when v.hex && long.TryParse(h.Trim(), NumberStyles.HexNumber, Inv, out long x) => x,
                _ => null,
            };
        }
    }

    // on screen: decimal comma in Russian; in the CSV: always invariant (dot), see Csv()
    static string Show(double? v, Profile.Value val) =>
        v == null ? "—" : val.hex ? "0x" + ((long)v.Value).ToString("X8") : v.Value.ToString(val.format, Inv).Replace('.', Ru ? ',' : '.');

    static string CsvValue(double? v, Profile.Value val) =>
        v == null ? "" : val.hex ? "0x" + ((long)v.Value).ToString("X8") : v.Value.ToString(val.format, Inv);

    static string Csv(string s) => s.Contains(',') ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;

    static int Width() { try { return Math.Max(40, Console.WindowWidth); } catch { return 80; } }
    static int Rows() { try { return Math.Max(10, Console.WindowHeight); } catch { return 24; } }

    // header, status, (blank + heading) per group, values, blank, footer
    static int LineCount(List<Profile.Value> all) => 2 + all.Count(v => v.group != null) * 2 + all.Count + 2;

    // cut to the window width so nothing wraps (a wrapped line would scroll the screen)
    static string Fit(string s, int w) => s.Length < w ? s : s[..Math.Max(0, w - 2)] + "…";

    static void Draw(Profile p, List<Profile.Value> all, double?[] v, Charts.History h, bool graphs,
                     string status, string logPath, bool stale)
    {
        int w = Width();
        bool compact = Rows() < LineCount(all);
        var sb = new System.Text.StringBuilder("\x1b[H");
        void Line(string text, string style = null) =>
            sb.Append(style).Append(Fit(text, w)).Append(style != null ? "\x1b[0m" : "").Append("\x1b[K\n");
        Line($"{p.Name}   {DateTime.Now:HH:mm:ss}", "\x1b[1m");
        Line(status, stale ? "\x1b[33m" : "\x1b[32m");
        if (graphs)
        {
            // chart lines carry their own colors and already fit the width
            foreach (var l in Charts.Render(p, all, h, w, Rows() - 4)) sb.Append(l).Append("\x1b[K\n");
        }
        else
            for (int i = 0; i < all.Count; i++)
            {
                // small screen (window could not grow to the full height): drop the blank lines between groups
                if (all[i].group != null) { if (!compact) Line(""); Line(all[i].Group, "\x1b[1m"); }
            {
                // number + a 30 s sparkline if the window is wide enough
                string text = $"  {all[i].Label,-34} {Show(v[i], all[i]),10} {all[i].Unit,-9}";
                const int sparkW = 20;
                if (w >= text.Length + sparkW + 2)
                    sb.Append(text).Append(' ').Append(Charts.Spark(h, i, all[i], sparkW)).Append("\x1b[K\n");
                else Line(text.TrimEnd());
            }
            }
        Line("");
        sb.Append(Fit((logPath != null ? $"CSV: logs/{Path.GetFileName(logPath)}   " : "") +
                      (graphs ? T("G — numbers", "G — цифры") : T("G — graphs", "G — графики")) + T("   Q or Ctrl+C — quit", "   Q или Ctrl+C — выход"), w))
          .Append("\x1b[K\x1b[J");
        Console.Write(sb.ToString());
    }
}
