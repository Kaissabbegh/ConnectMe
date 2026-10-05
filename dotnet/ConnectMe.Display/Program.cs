// ConnectMe Display: turns an iMac running Windows or Linux into a wireless screen for a laptop.
// Usage: ConnectMeDisplay [--name "Desk 12 iMac"] [--hwdec auto-safe|no] [--vo gpu|wlshm|x11|...]
//   --hwdec: mpv hardware decoding (default: auto-safe on Windows, no on Linux)
//   --vo:    mpv video output, for GPUs/drivers that garble mpv's default renderer
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using ConnectMe;

var logPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ConnectMe", "display.log");
Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
var logLock = new object();
void Log(string line)
{
    var text = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {line}";
    lock (logLock)
    {
        Console.WriteLine(text);
        try { File.AppendAllText(logPath, text + Environment.NewLine); } catch { }
    }
}

var nameArg = Array.IndexOf(args, "--name");
var name = nameArg >= 0 && nameArg + 1 < args.Length ? args[nameArg + 1] : Environment.MachineName;

var mpvPath = MpvPlayer.FindMpv();
if (mpvPath == null)
{
    Fatal(OperatingSystem.IsWindows()
        ? "mpv was not found. Put mpv.exe in the same folder as ConnectMeDisplay.exe (see README)."
        : "mpv was not found. Install it with:  sudo apt install mpv");
    return 1;
}

var hwdecArg = Array.IndexOf(args, "--hwdec");
var hwdec = hwdecArg >= 0 && hwdecArg + 1 < args.Length ? args[hwdecArg + 1] : null;
var voArg = Array.IndexOf(args, "--vo");
var vo = voArg >= 0 && voArg + 1 < args.Length ? args[voArg + 1] : null;
var mpvLog = Path.Combine(Path.GetDirectoryName(logPath)!, "mpv.log");
MpvPlayer.KillOrphans(Log);
using var player = new MpvPlayer(mpvPath, Log, mpvLog, hwdec, vo);
try { player.Start(); }
catch (Exception ex) { Fatal("Could not start mpv: " + ex.Message); return 1; }

var server = new ReceiverServer(name, player, Log);
var size = player.DisplaySize();
if (size is { } s && s.Width > 0) { server.Width = s.Width; server.Height = s.Height; }
Log($"ConnectMe Display \"{name}\" on {RuntimeInformation.OSDescription}, screen {server.Width}x{server.Height}, mpv: {mpvPath}");

void ShowIdle()
{
    if (server.ActiveSession) return; // never draw over the video
    player.EnsureFullscreen();
    var ips = LocalIPv4();
    var ipText = ips.Count == 0 ? "No network connection" : "IP address: " + string.Join("   ", ips);
    player.ShowText(
        $"ConnectMe  —  {server.Name}\n\n" +
        $"Pairing code:   {string.Join(" ", server.Code.ToCharArray())}\n\n" +
        $"{ipText}\n\n" +
        $"Open ConnectMe on your laptop, pick this iMac (or type the IP), and enter the code.\n\n" +
        server.Status);
}

using var mdns = new MdnsResponder(() => (server.Name, server.Width, server.Height), Log);
server.IdleChanged += ShowIdle;
var exited = new ManualResetEventSlim();
player.Exited += () => { Log("mpv window closed; exiting."); exited.Set(); };

// pkill / Ctrl+C / logout: shut down cleanly so the full-screen mpv window closes with us.
var signals = new[] { PosixSignal.SIGTERM, PosixSignal.SIGINT, PosixSignal.SIGHUP, PosixSignal.SIGQUIT }
    .Select(sig => PosixSignalRegistration.Create(sig, ctx =>
    {
        ctx.Cancel = true;
        Log($"Received {ctx.Signal}; exiting.");
        exited.Set();
    }))
    .ToList();
AppDomain.CurrentDomain.ProcessExit += (_, _) => player.Dispose();

try { server.Start(); }
catch (SocketException ex)
{
    Fatal($"Port {Proto.Port} is busy ({ex.Message}). Is ConnectMe Display already running?");
    return 1;
}
mdns.Start();

// While idle, refresh the screen now and then: the code changes after sessions and IPs can change.
using var timer = new Timer(_ => ShowIdle(), null, 0, 5000);

exited.Wait();
server.Stop();
return 0;

static List<string> LocalIPv4() =>
    NetworkInterface.GetAllNetworkInterfaces()
        .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
        .SelectMany(n => n.GetIPProperties().UnicastAddresses)
        .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork && !a.Address.ToString().StartsWith("169.254."))
        .Select(a => a.Address.ToString())
        .Distinct()
        .ToList();

void Fatal(string message)
{
    Log(message);
    if (OperatingSystem.IsWindows())
        MessageBoxW(IntPtr.Zero, message, "ConnectMe Display", 0x10);
    else
        try { System.Diagnostics.Process.Start("notify-send", new[] { "ConnectMe Display", message }); } catch { }
}

[DllImport("user32.dll", CharSet = CharSet.Unicode)]
static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);
