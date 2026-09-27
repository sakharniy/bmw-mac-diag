using System.Text;
using EdiabasLib;

namespace BmwDiag;

// One EdiabasLib connection: K+DCAN cable on a serial port (interface STD:OBD) + SGBD folder.
sealed class Session : IDisposable
{
    public EdiabasNet Ediabas { get; }
    public EdInterfaceObd Obd { get; }
    public string Sgbd { get; private set; }
    // set when the session is abandoned (cable gone, job hanging): EdiabasLib stops the running job
    public volatile bool Abort;

    static Session()
    {
        // SGBD texts are Windows-1252 (German); .NET on macOS needs the code page provider
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public Session(Settings s, string sgbd)
    {
        Ediabas = new EdiabasNet();
        Obd = new EdInterfaceObd();
        Ediabas.EdInterfaceClass = Obd;
        Ediabas.AbortJobFunc = () => Abort;
        Ediabas.SetConfigProperty("EcuPath", s.EcuPath);
        Ediabas.SetConfigProperty("RetryComm", "1");
        Obd.ComPort = s.Port;
        if (s.Trace)
        {
            Directory.CreateDirectory(s.TraceDir);
            Ediabas.SetConfigProperty("TracePath", s.TraceDir);
            Ediabas.SetConfigProperty("IfhTrace", "3");
            Ediabas.SetConfigProperty("ApiTrace", "1");
        }
        try { Use(sgbd); }
        catch { Dispose(); throw; }
    }

    // Switch to another SGBD (.PRG, or group .GRP which resolves the variant by asking the ECU)
    public void Use(string sgbd)
    {
        string dir = Ediabas.GetConfigProperty("EcuPath");
        if (dir != null && Directory.Exists(dir) &&
            !Directory.EnumerateFiles(dir).Any(f => Path.GetFileName(f).Equals(sgbd, StringComparison.OrdinalIgnoreCase)))
            throw new UsageException(L.T($"SGBD file {sgbd} not found in {dir} — copy it there or use --ecu (see README, \"SGBD files\")",
                                         $"файл SGBD {sgbd} не найден в {dir} — скопируйте его туда или укажите --ecu (см. README, «Файлы SGBD»)"));
        Ediabas.ResolveSgbdFile(sgbd);
        Sgbd = Path.GetFileNameWithoutExtension(Ediabas.SgbdFileName ?? sgbd).ToUpperInvariant();
    }

    // Run a job. args: "a;b;c" string arguments, or "hex:01 02" for binary ones.
    public List<Dictionary<string, EdiabasNet.ResultData>> Run(string job, string args = null)
    {
        // ArgString and ArgBinary share one buffer in EdiabasLib: clear first, then set
        Ediabas.ArgBinary = null;
        Ediabas.ArgBinaryStd = null;
        Ediabas.ResultsRequests = string.Empty;
        if (args != null && args.StartsWith("hex:", StringComparison.OrdinalIgnoreCase))
            Ediabas.ArgBinary = Hex.Parse(args[4..]);
        else
            Ediabas.ArgString = args ?? string.Empty;
        Ediabas.ExecuteJob(job);
        return Ediabas.ResultSets;
    }

    // EdiabasLib bug: disposing an interface whose connect failed half-way throws ThreadStateException
    // (Thread.Join on a comm thread that never started) — and if that happens later in the GC finalizer
    // it kills the whole process ("Abort trap"). So: dispose here, swallow that, and switch the finalizers off.
    public void Dispose()
    {
        try { Ediabas.Dispose(); } catch { }
        try { Obd.Dispose(); } catch { }
        GC.SuppressFinalize(Obd);
        GC.SuppressFinalize(Ediabas);
    }

    // Human-readable EDIABAS error with a hint for the common cable problems
    public static string Explain(Exception ex)
    {
        var sb = new StringBuilder(ex.Message);
        for (var e = ex.InnerException; e != null; e = e.InnerException) sb.Append(" <- ").Append(e.Message);
        string msg = sb.ToString();
        string hint =
            msg.Contains("IFH_0018") ? L.T("cannot open the serial port — is the cable plugged into the Mac? (bmwdiag ports)",
                                           "не открывается порт — кабель вставлен в мак? (bmwdiag ports)") :
            msg.Contains("IFH_0003") ? L.T("no data from the cable — ignition on? K+DCAN switch in the right position (D-CAN for cars after 03/2008)?",
                                           "кабель не отвечает — зажигание включено? переключатель на кабеле в нужном положении (D-CAN для машин после 03/2008)?") :
            msg.Contains("SYS_0010") ? L.T("no answer from the car — ignition on? cable in the OBD socket? try once more",
                                           "машина не отвечает — зажигание включено? кабель в OBD-разъёме? попробуйте ещё раз") :
            msg.Contains("IFH_0009") ? L.T("this control unit does not answer — wrong SGBD, unit not fitted, or it only talks with engine running",
                                           "блок не отвечает — не тот SGBD, блока нет в машине, или он отвечает только на заведённом двигателе") :
            msg.Contains("SYS_0002") || msg.Contains("not found") ? L.T("SGBD file not found — put .PRG/.GRP files into the ecu folder (see README)",
                                           "файл SGBD не найден — положите файлы .PRG/.GRP в папку ecu (см. README)") :
            msg.Contains("No variant found") ? L.T("the group file does not know this unit — needs a newer T_GRTB.PRG, or use the variant .PRG directly",
                                           "групповой файл не знает этот блок — нужен свежий T_GRTB.PRG или вызывайте .PRG варианта напрямую") :
            null;
        return hint == null ? msg : $"{msg}\n  {L.T("hint", "подсказка")}: {hint}";
    }
}

// Errors that are the user's input, printed without a stack of EDIABAS details
sealed class UsageException(string message) : Exception(message);

static class Hex
{
    public static byte[] Parse(string s) =>
        s.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(h => Convert.ToByte(h, 16)).ToArray();

    public static string Format(IEnumerable<byte> b) => string.Join(" ", b.Select(x => x.ToString("X2")));
}
