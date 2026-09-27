using System.Text;
using EdiabasLib;

namespace BmwDiag;

// Experiment for a "no BMW files" mode: talk to the car without any SGBD.
// The link is set up by hand with the BMW-FAST / D-CAN parameters the SGBDs normally set
// (seen in EDIABAS traces: 0x10F, 115200 baud, timeouts), then only READ requests are sent:
// identification, standard OBD (modes 01 and 03), BMW's measurement read (2C 10), fault memory (18 02 FF FF).
// Everything goes to the screen and to logs/probe-nofiles-*.txt. Not in --help: a tool for development.
static class Probe
{
    static readonly byte[] Allowed = { 0x01, 0x03, 0x1A, 0x18, 0x2C };   // read-only services

    // E9x bus addresses (public facts; same as in BMW's assignment table)
    static readonly (byte addr, string name)[] Units =
    {
        (0x12, "engine DDE"), (0x18, "gearbox EGS"), (0x29, "DSC/ABS"), (0x40, "CAS"), (0x60, "instrument cluster"),
        (0x01, "airbags ACSM"), (0x00, "junction box JBBF"), (0x72, "footwell FRM"), (0x78, "climate IHKA"),
        (0x17, "fuel pump EKP"), (0x64, "PDC"), (0x63, "iDrive CCC"),
    };

    public static int Run(Settings s)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Directory.CreateDirectory(s.LogDir);
        string file = Path.Combine(s.LogDir, $"probe-nofiles-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
        using var log = new StreamWriter(file) { AutoFlush = true };
        void Out(string line) { Console.WriteLine(line); log.WriteLine(line); }

        var ediabas = new EdiabasNet();
        var obd = new EdInterfaceObd();
        try
        {
            ediabas.EdInterfaceClass = obd;
            obd.ComPort = s.Port;
            Out($"port {s.Port}");
            if (!obd.InterfaceConnect()) { Out("InterfaceConnect failed"); return 3; }
            // BMW-FAST over D-CAN, as set by the engine SGBD (from the trace): concept 0x10F, 115200 baud,
            // then the timing values; answer length: taken from the telegram header
            obd.CommParameter = new uint[] { 0x10F, 115200, 800, 20, 10, 2, 5000 };
            obd.CommAnswerLen = new short[] { 0, 0 };

            byte[] Send(byte addr, string hex, string what)
            {
                byte[] data = Hex.Parse(hex);
                if (!Allowed.Contains(data[0])) { Out($"refused {hex}"); return null; }
                byte[] tel = new byte[] { (byte)(0x80 | data.Length), addr, 0xF1 }.Concat(data).ToArray();
                byte[] recv = null;
                string res;
                try
                {
                    var t = Task.Run(() => obd.TransmitData(tel, out recv) ? recv : null);
                    res = t.Wait(5000) ? (t.Result != null ? Hex.Format(t.Result) : "no answer") : "timeout";
                    recv = t.IsCompleted ? t.Result : null;
                }
                catch (Exception ex) { res = "error: " + ex.GetBaseException().Message; }
                Out($"{what,-34} > {Hex.Format(tel)}");
                Out($"{"",-34} < {res}");
                return recv;
            }

            Out("\n== engine, link check ==");
            Send(0x12, "1A 80", "ident (1A 80)");

            Out("\n== live values ==");
            Send(0x12, "2C 10 00 0C 00 05 00 42", "BMW 2C 10: rpm, coolant, voltage");
            foreach (byte a in new byte[] { 0x33, 0xDF, 0x12 })
            {
                Send(a, "01 00", $"OBD mode 01 PID 00 @0x{a:X2}");
                Send(a, "01 0C", $"OBD mode 01 PID 0C (rpm) @0x{a:X2}");
            }

            Out("\n== faults ==");
            foreach (byte a in new byte[] { 0x33, 0xDF, 0x12 })
                Send(a, "03", $"OBD mode 03 (P-codes) @0x{a:X2}");
            foreach (var (addr, name) in Units)
            {
                Send(addr, "1A 80", $"{name}: ident");
                Send(addr, "18 02 FF FF", $"{name}: fault memory");
            }
            Out($"\nsaved to {file}");
            return 0;
        }
        catch (Exception ex)
        {
            Out("error: " + Session.Explain(ex));
            return 3;
        }
        finally
        {
            try { ediabas.Dispose(); } catch { }
            try { obd.Dispose(); } catch { }
            GC.SuppressFinalize(obd);
            GC.SuppressFinalize(ediabas);
        }
    }
}
