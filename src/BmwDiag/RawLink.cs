using System.Text;
using EdiabasLib;

namespace BmwDiag;

// Talking to the car without BMW's SGBD files (basic mode). The link is set up by hand with the parameters
// the SGBDs set for BMW-FAST over D-CAN (seen in EDIABAS traces: concept 0x10F, 115200 baud, timeouts),
// and only read requests go out: identification (1A), fault memory (18), standard OBD (01, 03),
// BMW's read of OBD values (2C 10).
sealed class RawLink : IDisposable
{
    static readonly byte[] ReadServices = { 0x01, 0x03, 0x1A, 0x18, 0x2C };
    const int TimeoutMs = 6000;

    readonly EdiabasNet ediabas = new();
    readonly EdInterfaceObd obd = new();
    bool fresh = true;     // no request sent yet on this link
    bool broken;           // a request hung: the comm thread may still be blocked

    static RawLink() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public RawLink(Settings s)
    {
        if (s.Port == null) throw new UsageException(L.T("the cable is not found — plug it into the Mac (bmwdiag ports)",
                                                         "кабель не найден — вставьте его в мак (bmwdiag ports)"));
        try
        {
            ediabas.EdInterfaceClass = obd;
            obd.ComPort = s.Port;
            if (!obd.InterfaceConnect()) throw new Exception($"EDIABAS_IFH_0018: cannot open {s.Port}");
            obd.CommParameter = new uint[] { 0x10F, 115200, 800, 20, 10, 2, 5000 };
            obd.CommAnswerLen = new short[] { 0, 0 };   // answer length from the telegram header
        }
        catch { Dispose(); throw; }
    }

    // One request to the unit at `addr`; returns the answer data without header and checksum.
    // The first request after connecting often gets no answer (IFH-0009 in the car test), so that one is repeated.
    public byte[] Request(byte addr, params byte[] data)
    {
        if (data.Length is 0 or > 63 || !ReadServices.Contains(data[0]))
            throw new InvalidOperationException("only read requests: " + Hex.Format(data));
        if (broken) throw new LinkLostException();
        byte[] tel = new byte[] { (byte)(0x80 | data.Length), addr, 0xF1 }.Concat(data).ToArray();
        bool retry = fresh;
        fresh = false;
        while (true)
        {
            try { return Payload(Transmit(tel), addr); }
            catch (Exception ex) when (retry && ex.Message.Contains("IFH_0009")) { retry = false; }
        }
    }

    byte[] Transmit(byte[] tel)
    {
        var t = Task.Run(() => obd.TransmitData(tel, out byte[] recv) ? recv : null);
        try { if (!t.Wait(TimeoutMs)) { broken = true; throw new LinkLostException(); } }
        catch (AggregateException ae) { throw ae.GetBaseException(); }
        return t.Result ?? throw new Exception("EDIABAS_IFH_0009: no answer");
    }

    // Telegram: 80|len, tester F1, source, data..., checksum; long answers: 80, F1, source, len, data...
    static byte[] Payload(byte[] r, byte addr)
    {
        int len = r.Length >= 4 ? r[0] & 0x3F : -1, start = 3;
        if (len == 0) { len = r[3]; start = 4; }
        if (len < 1 || r.Length < start + len || r[2] != addr) throw new Exception("unexpected answer: " + Hex.Format(r));
        byte[] d = r[start..(start + len)];
        if (d[0] == 0x7F) throw new Exception(L.T("the unit refused the request", "блок отклонил запрос") + $" ({Hex.Format(d)})");
        return d;
    }

    // same EdiabasLib dispose trap as in Session; a hanging comm thread must not block the caller
    public void Dispose()
    {
        void Close()
        {
            try { ediabas.Dispose(); } catch { }
            try { obd.Dispose(); } catch { }
            GC.SuppressFinalize(obd);
            GC.SuppressFinalize(ediabas);
        }
        if (broken) Task.Run(Close); else Close();
    }
}

sealed class LinkLostException() : Exception(L.T(
    "no answer for 6 s — cable out of the Mac, or the ignition is off?",
    "нет ответа 6 с — кабель выпал из мака или зажигание выключено?"));
