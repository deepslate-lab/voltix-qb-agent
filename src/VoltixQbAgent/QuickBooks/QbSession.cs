using System.Runtime.InteropServices;
using System.Xml.Linq;
using Microsoft.CSharp.RuntimeBinder;

namespace VoltixQbAgent.QuickBooks;

/// <summary>
/// Thin session wrapper over QBFC, late-bound so no Interop assembly or COM
/// reference is needed at build time (QBFC17 down to QBFC13 probed at run
/// time). Mechanics proven by the QBPV reference app:
///
///  - OpenConnection2(ctLocalQBD) + BeginSession(path, omDontCare) — with an
///    empty path it attaches to whatever company file QuickBooks currently
///    has open and never forces single-user mode; with a .qbw path it can run
///    unattended IF the app has been granted automatic login in QB's
///    Integrated Applications preferences.
///  - qbXML 13.0 message sets, one request per message set, roeStop.
///  - Dispose always EndSession + CloseConnection, each swallow-all, then
///    releases the COM object.
///
/// Sessions are meant to be SHORT-LIVED here (open → a batch of requests →
/// dispose) — this agent is a daemon, unlike the one-shot manual tool the
/// pattern comes from.
/// </summary>
public sealed class QbSession : IDisposable
{
    public const string AppName = "Voltix QB Agent";
    public const int QbXmlMajorVersion = 13;
    public const int QbXmlMinorVersion = 0;

    private const int CtLocalQBD = 1;   // ENConnectionType.ctLocalQBD
    private const int OmDontCare = 2;   // ENOpenMode.omDontCare
    private const int RoeStop = 1;      // ENRqOnError.roeStop

    private dynamic? _manager;
    private bool _connectionOpen;
    private bool _sessionOpen;

    public string? CompanyFilePath { get; private set; }

    private QbSession() { }

    /// <param name="companyFile">Empty/null = attach to the currently open
    /// company file; a full path = open that file (unattended mode).</param>
    public static QbSession Open(string? companyFile = null)
    {
        var session = new QbSession();
        var step = "create QBFC session manager";
        try
        {
            session._manager = CreateSessionManager();
            step = "OpenConnection2";
            session._manager!.OpenConnection2("", AppName, CtLocalQBD);
            session._connectionOpen = true;
            step = "BeginSession";
            session._manager.BeginSession(companyFile ?? "", OmDontCare);
            session._sessionOpen = true;

            try
            {
                session.CompanyFilePath = (string?)session._manager.GetCurrentCompanyFileName();
            }
            catch
            {
                session.CompanyFilePath = null; // advisory only
            }
            return session;
        }
        catch (Exception ex)
        {
            session.Dispose();
            Log.Warn($"QB open failed at step \"{step}\" " +
                     $"(hr=0x{unchecked((uint)ex.HResult):X8}, {ex.GetType().Name}: {ex.Message}, " +
                     $"process={(Environment.Is64BitProcess ? "x64" : "x86")})");
            throw Translate(ex);
        }
    }

    /// <summary>Run a single request Rq inside its own message set and return
    /// the raw qbXML response.</summary>
    public string RunRequest(Action<dynamic> appendRequest)
    {
        if (_manager is null || !_sessionOpen)
            throw new QbAgentException("QuickBooks session is not open.");
        try
        {
            dynamic msgSet = _manager.CreateMsgSetRequest("US", QbXmlMajorVersion, QbXmlMinorVersion);
            msgSet.Attributes.OnError = RoeStop;
            appendRequest(msgSet);
            dynamic response = _manager.DoRequests(msgSet);
            return (string)response.ToXMLString();
        }
        catch (Exception ex)
        {
            throw Translate(ex);
        }
    }

    /// <summary>CompanyQuery — identity gate before anything else runs.</summary>
    public QbCompanyInfo QueryCompany()
    {
        var xml = RunRequest(msgSet => msgSet.AppendCompanyQueryRq());
        var doc = XDocument.Parse(xml);
        var ret = doc.Descendants("CompanyRet").FirstOrDefault()
                  ?? throw new QbAgentException("QuickBooks returned no company information.");
        return new QbCompanyInfo(
            CompanyName: ret.Element("CompanyName")?.Value ?? "",
            LegalCompanyName: ret.Element("LegalCompanyName")?.Value ?? "",
            CompanyFilePath: CompanyFilePath);
    }

    private static dynamic CreateSessionManager()
    {
        var bitness = Environment.Is64BitProcess ? "x64" : "x86";
        Exception? lastCreateError = null;
        for (var v = 17; v >= 13; v--)
        {
            var type = Type.GetTypeFromProgID($"QBFC{v}.QBSessionManager");
            if (type == null) continue;
            try
            {
                var instance = Activator.CreateInstance(type)
                    ?? throw new QbAgentException($"QBFC{v}.QBSessionManager returned null.");
                Log.Info($"Using QBFC{v} ({bitness} process)");
                return instance;
            }
            catch (Exception ex) when (ex is not QbAgentException)
            {
                // ProgID registered but the server won't instantiate in THIS
                // process — almost always a bitness mismatch (64-bit QB 2022+
                // registers 64-bit QBFC; older QB registers 32-bit).
                lastCreateError = ex;
                Log.Warn($"QBFC{v} found but failed to instantiate in this {bitness} process " +
                         $"(hr=0x{unchecked((uint)ex.HResult):X8}: {ex.Message})");
            }
        }
        var other = Environment.Is64BitProcess ? "x86" : "x64";
        throw new QbAgentException(
            lastCreateError != null
                ? $"QBFC is registered but cannot load in a {bitness} process — your QuickBooks is likely " +
                  $"{(Environment.Is64BitProcess ? "32-bit (2021 or older)" : "64-bit (2022 or newer)")}. " +
                  $"Run the {other} build of this agent instead."
                : "The QuickBooks SDK (QBFC 13-17) is not registered on this machine. Is this the VM where " +
                  "QuickBooks is installed? If so, install the QBFC redistributable from the QB SDK.",
            lastCreateError);
    }

    /// <summary>HRESULT → human message, table lifted from the proven app.</summary>
    private static Exception Translate(Exception ex)
    {
        if (ex is QbAgentException) return ex;
        if (ex is RuntimeBinderException)
            return new QbAgentException("Unexpected QBFC API shape — is a supported QBFC version (13-17) installed?", ex);

        var hr = unchecked((uint)(ex.HResult));
        var message = hr switch
        {
            0x80040154 => "QBFC is not registered on this machine (install the QuickBooks SDK redistributable).",
            0x80040408 or 0x80040417 => "QuickBooks is not running. Start QuickBooks and open the company file.",
            0x80040401 => "Could not reach QuickBooks. Is it running on this machine?",
            0x80040405 => "No company file is open in QuickBooks.",
            0x8004040A => "QuickBooks is busy. Close any open dialog or wizard in QuickBooks and try again.",
            0x80040410 or 0x80040416 or 0x8004041A =>
                "QuickBooks has not granted this application access. In QuickBooks: Edit → Preferences → " +
                "Integrated Applications, allow \"" + AppName + "\" (for unattended mode also tick automatic login).",
            0x80040414 => "QuickBooks is in a mode that prevents access (e.g. single-user activity in progress).",
            _ => null,
        };
        return message != null
            ? new QbAgentException(message, ex, busy: hr == 0x8004040A)
            : new QbAgentException($"QuickBooks error: {ex.Message}", ex);
    }

    public void Dispose()
    {
        if (_manager is null) return;
        try { if (_sessionOpen) _manager.EndSession(); } catch { /* best effort */ }
        try { if (_connectionOpen) _manager.CloseConnection(); } catch { /* best effort */ }
        try { Marshal.FinalReleaseComObject(_manager); } catch { /* best effort */ }
        _manager = null;
        _sessionOpen = false;
        _connectionOpen = false;
    }
}

public sealed record QbCompanyInfo(string CompanyName, string LegalCompanyName, string? CompanyFilePath);

/// <summary>Message is safe to show verbatim in the UI/logs.</summary>
public sealed class QbAgentException : Exception
{
    /// <summary>True when QB reported busy — callers should back off and retry.</summary>
    public bool IsBusy { get; }

    public QbAgentException(string message, Exception? inner = null, bool busy = false)
        : base(message, inner)
    {
        IsBusy = busy;
    }
}
