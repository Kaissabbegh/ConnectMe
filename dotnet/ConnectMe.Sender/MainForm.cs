using System.Diagnostics;

namespace ConnectMe;

public sealed class MainForm : Form
{
    readonly Settings _settings = Settings.Load();
    readonly ListBox _receivers = new() { Height = 90, Dock = DockStyle.Fill, IntegralHeight = false };
    readonly Button _refresh = new() { Text = "Search again", AutoSize = true };
    readonly TextBox _manualIp = new() { PlaceholderText = "or type the iMac's IP address", Dock = DockStyle.Fill };
    readonly TextBox _code = new() { PlaceholderText = "4-digit code shown on the iMac", Dock = DockStyle.Fill, MaxLength = 4 };
    readonly ComboBox _display = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    readonly ComboBox _quality = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    readonly Button _connect = new() { Text = "Connect", AutoSize = true, Padding = new Padding(16, 4, 16, 4) };
    readonly Label _status = new() { Text = "Not connected", AutoSize = true, ForeColor = Color.DimGray };
    readonly TextBox _log = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill };
    readonly LinkLabel _vddLink = new() { Text = "Want a second (extended) screen? Install the Virtual Display Driver", AutoSize = true };

    StreamSession? _session;
    bool _searching;

    static readonly (string Label, int Fps, int Mbps)[] Qualities =
    {
        ("Standard (30 fps, 15 Mbps)", 30, 15),
        ("Busy Wi-Fi (30 fps, 8 Mbps)", 30, 8),
        ("Smooth (60 fps, 20 Mbps, fast laptops)", 60, 20),
        ("Best (60 fps, 35 Mbps, wired, fast laptops)", 60, 35),
    };

    public MainForm()
    {
        Text = "ConnectMe — use an iMac as your screen";
        ClientSize = new Size(560, 600);
        MinimumSize = new Size(480, 520);
        Font = new Font("Segoe UI", 9.5f);
        StartPosition = FormStartPosition.CenterScreen;

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(14), ColumnCount = 1 };
        root.RowStyles.Clear();
        void Add(Control c, bool fill = false)
        {
            root.RowStyles.Add(fill ? new RowStyle(SizeType.Percent, 100) : new RowStyle(SizeType.AutoSize));
            root.Controls.Add(c);
        }

        Add(Header("1. Choose the iMac"));
        var recvRow = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 1, AutoSize = true };
        recvRow.Controls.Add(_receivers);
        recvRow.RowStyles.Add(new RowStyle(SizeType.Absolute, 92));
        var ipRow = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 2, AutoSize = true };
        ipRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        ipRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        ipRow.Controls.Add(_manualIp, 0, 0);
        ipRow.Controls.Add(_refresh, 1, 0);
        recvRow.Controls.Add(ipRow);
        Add(recvRow);

        Add(Header("2. Pairing code"));
        Add(_code);
        Add(Header("3. Screen to show on the iMac"));
        Add(_display);
        Add(_vddLink);
        Add(Header("Quality"));
        Add(_quality);

        var actions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Top, Padding = new Padding(0, 10, 0, 6) };
        actions.Controls.Add(_connect);
        _status.Padding = new Padding(10, 9, 0, 0);
        actions.Controls.Add(_status);
        Add(actions);
        Add(_log, fill: true);
        Controls.Add(root);

        foreach (var q in Qualities) _quality.Items.Add(q.Label);
        var qi = Array.FindIndex(Qualities, q => q.Fps == _settings.Fps && q.Mbps == _settings.BitrateMbps);
        _quality.SelectedIndex = qi >= 0 ? qi : 0;
        _manualIp.Text = _settings.LastHost;

        _refresh.Click += async (_, _) => { LoadDisplays(); await SearchAsync(); };
        _receivers.SelectedIndexChanged += (_, _) =>
        {
            if (_receivers.SelectedItem is FoundReceiver r) _manualIp.Text = r.Address.ToString();
        };
        _connect.Click += (_, _) => ToggleConnect();
        _code.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; ToggleConnect(); } };
        _vddLink.LinkClicked += (_, _) => Process.Start(new ProcessStartInfo(
            "https://github.com/VirtualDrivers/Virtual-Display-Driver/releases") { UseShellExecute = true });

        Shown += async (_, _) =>
        {
            LoadDisplays();
            if (Encoder.FindFfmpeg() == null)
                Log("FFmpeg not found. Put ffmpeg.exe next to ConnectMe.exe (see README).");
            await SearchAsync();
        };
        FormClosing += (_, _) => _session?.Stop("App closed.");
    }

    static Label Header(string text) => new()
    {
        Text = text, AutoSize = true, Font = new Font("Segoe UI Semibold", 10f), Padding = new Padding(0, 10, 0, 3),
    };

    void LoadDisplays()
    {
        var prev = (_display.SelectedItem as DisplayInfo)?.DeviceName;
        _display.Items.Clear();
        foreach (var d in Displays.List()) _display.Items.Add(d);
        if (_display.Items.Count == 0) { Log("No displays found."); return; }
        // Prefer the previously used screen, then any extended (non-main) one, then the main screen.
        var items = _display.Items.Cast<DisplayInfo>().ToList();
        var pick = items.FindIndex(d => d.DeviceName == prev);
        if (pick < 0) pick = items.FindIndex(d => !d.IsPrimary);
        _display.SelectedIndex = Math.Max(0, pick);
    }

    async Task SearchAsync()
    {
        if (_searching) return;
        _searching = true;
        _refresh.Enabled = false;
        _refresh.Text = "Searching...";
        try
        {
            var found = await Discovery.BrowseAsync(TimeSpan.FromSeconds(2));
            _receivers.Items.Clear();
            foreach (var r in found) _receivers.Items.Add(r);
            if (found.Count == 0)
                _receivers.Items.Add("No iMacs found. Is ConnectMe Display open on it? You can also type its IP below.");
            var last = found.FindIndex(r => r.Name == _settings.LastDeviceName);
            if (last >= 0) _receivers.SelectedIndex = last;
        }
        finally
        {
            _refresh.Text = "Search again";
            _refresh.Enabled = true;
            _searching = false;
        }
    }

    void ToggleConnect()
    {
        if (_session != null) { _session.Stop("Disconnected."); return; }

        var host = _manualIp.Text.Trim();
        if (host.Length == 0) { Log("Choose an iMac or type its IP address."); return; }
        if (_code.Text.Trim().Length != 4) { Log("Enter the 4-digit code shown on the iMac."); return; }
        if (_display.SelectedItem is not DisplayInfo display) { Log("Choose a screen to send."); return; }
        var ffmpeg = Encoder.FindFfmpeg();
        if (ffmpeg == null) { Log("FFmpeg not found. Put ffmpeg.exe next to ConnectMe.exe (see README)."); return; }

        var q = Qualities[Math.Max(0, _quality.SelectedIndex)];
        var device = _receivers.SelectedItem as FoundReceiver;
        _settings.LastHost = host;
        _settings.LastDeviceName = device?.Name ?? "";
        _settings.Fps = q.Fps;
        _settings.BitrateMbps = q.Mbps;
        _settings.Save();

        var session = new StreamSession(new StreamSettings(host, device?.Port ?? Proto.Port, _code.Text, display, q.Fps, q.Mbps), Log);
        session.Stats += s => BeginInvoke(() => _status.Text = "Streaming: " + s);
        session.Stopped += _ => BeginInvoke(() =>
        {
            _session = null;
            SetBusy(false);
            _status.Text = "Not connected";
        });
        _session = session;
        SetBusy(true);
        _status.Text = "Connecting...";
        Task.Run(() =>
        {
            try { session.Start(ffmpeg); }
            catch (Exception ex) { session.Stop("Could not connect: " + (ex.InnerException ?? ex).Message); }
        });
    }

    void SetBusy(bool connected)
    {
        _connect.Text = connected ? "Disconnect" : "Connect";
        foreach (var c in new Control[] { _receivers, _manualIp, _code, _display, _quality, _refresh })
            c.Enabled = !connected;
    }

    void Log(string line)
    {
        if (InvokeRequired) { BeginInvoke(() => Log(line)); return; }
        _log.AppendText($"[{DateTime.Now:HH:mm:ss}] {line}{Environment.NewLine}");
    }
}
