// Midiboard Bridge - window, tray icon, settings.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using Windows.Devices.Enumeration;

namespace MidiboardBridge
{
    class Settings
    {
        static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MidiboardBridge");
        static readonly string FilePath = Path.Combine(Dir, "settings.ini");
        public string DeviceId = "", DeviceName = "", Port = "Midiboard", PhoneIp = "", ListenPort = "9100", PhonePort = "9000";
        public bool AutoConnect = true, Relay = false;

        public static Settings Load()
        {
            var s = new Settings();
            try
            {
                foreach (var line in File.ReadAllLines(FilePath))
                {
                    int i = line.IndexOf('='); if (i < 0) continue;
                    string k = line.Substring(0, i), v = line.Substring(i + 1);
                    switch (k)
                    {
                        case "DeviceId": s.DeviceId = v; break;
                        case "DeviceName": s.DeviceName = v; break;
                        case "Port": s.Port = v; break;
                        case "PhoneIp": s.PhoneIp = v; break;
                        case "ListenPort": s.ListenPort = v; break;
                        case "PhonePort": s.PhonePort = v; break;
                        case "AutoConnect": s.AutoConnect = v == "1"; break;
                        case "Relay": s.Relay = v == "1"; break;
                    }
                }
            }
            catch { }
            return s;
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Dir);
                File.WriteAllLines(FilePath, new[] {
                    "DeviceId=" + DeviceId, "DeviceName=" + DeviceName, "Port=" + Port, "PhoneIp=" + PhoneIp,
                    "ListenPort=" + ListenPort, "PhonePort=" + PhonePort, "AutoConnect=" + (AutoConnect ? "1" : "0"), "Relay=" + (Relay ? "1" : "0") });
            }
            catch { }
        }
    }

    class DeviceItem
    {
        public string Id, Name; public bool Midi;
        public override string ToString() { return Midi ? Name + "   (MIDI)" : Name; }
    }

    class MainForm : Form
    {
        readonly Settings cfg = Settings.Load();
        readonly BleMidiLink link = new BleMidiLink();
        readonly OscRelay relay = new OscRelay();

        ComboBox cbDevice, cbPort;
        Button btnRefresh, btnCreatePort, btnConnect;
        Label lblStatus, lblDot, lblCount, lblLoop;
        CheckBox chkAuto, chkRelay;
        TextBox txtIp, txtListen, txtPhonePort, txtLog;
        NotifyIcon tray;
        Timer uiTimer;
        bool quitting;

        public MainForm()
        {
            Text = "Midiboard Bridge";
            AutoScaleMode = AutoScaleMode.None;   // fonts are in points (already DPI-correct); positions scaled once below
            Font = new Font("Segoe UI", 9.5f);
            ClientSize = new Size(470, 530);
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { Icon = MakeIcon(Color.FromArgb(0, 200, 255)); }

            int y = 14, x = 14, w = 442;
            Controls.Add(new Label { Text = L.T("1. Phone (paired Bluetooth device)", "1. Telefon (eşleşmiş Bluetooth cihazı)"), Location = new Point(x, y), AutoSize = true, Font = new Font(Font, FontStyle.Bold) });
            y += 24;
            cbDevice = new ComboBox { Location = new Point(x, y), Width = w - 100, DropDownStyle = ComboBoxStyle.DropDownList };
            btnRefresh = new Button { Text = L.T("Refresh", "Yenile"), Location = new Point(x + w - 92, y - 1), Width = 92, Height = 27 };
            btnRefresh.Click += async (s, e) => await LoadDevices();
            Controls.Add(cbDevice); Controls.Add(btnRefresh);
            y += 30;
            Controls.Add(new Label
            {
                Text = L.T("Not listed? Pair the phone first: Windows Settings > Bluetooth > Add device, while Midiboard shows its Bluetooth screen.",
                           "Listede yok mu? Önce eşleştir: Windows Ayarlar > Bluetooth > Cihaz ekle (Midiboard Bluetooth ekranı açıkken)."),
                Location = new Point(x, y), Size = new Size(w, 34), ForeColor = Color.DimGray
            });

            y += 44;
            Controls.Add(new Label { Text = L.T("2. Output port (loopMIDI) - choose it as MIDI input in your DAW", "2. Çıkış portu (loopMIDI) - DAW'da MIDI girişi olarak bunu seç"), Location = new Point(x, y), AutoSize = true, Font = new Font(Font, FontStyle.Bold) });
            y += 24;
            cbPort = new ComboBox { Location = new Point(x, y), Width = w - 130 };
            btnCreatePort = new Button { Text = L.T("Create port", "Port oluştur"), Location = new Point(x + w - 122, y - 1), Width = 122, Height = 27 };
            btnCreatePort.Click += (s, e) => CreatePort();
            Controls.Add(cbPort); Controls.Add(btnCreatePort);
            y += 30;
            lblLoop = new Label { Location = new Point(x, y), Size = new Size(w, 34), ForeColor = Color.DimGray };
            Controls.Add(lblLoop);

            y += 42;
            btnConnect = new Button { Location = new Point(x, y), Size = new Size(150, 38), Font = new Font(Font.FontFamily, 10.5f, FontStyle.Bold) };
            btnConnect.Click += (s, e) => { if (link.State == LinkState.Stopped) Connect(); else Disconnect(); };
            lblDot = new Label { Text = "●", Location = new Point(x + 164, y + 6), AutoSize = true, Font = new Font(Font.FontFamily, 14f) };
            lblStatus = new Label { Location = new Point(x + 188, y + 4), Size = new Size(w - 188, 20), Font = new Font(Font, FontStyle.Bold) };
            lblCount = new Label { Location = new Point(x + 188, y + 22), Size = new Size(w - 188, 18), ForeColor = Color.DimGray };
            Controls.Add(btnConnect); Controls.Add(lblDot); Controls.Add(lblStatus); Controls.Add(lblCount);
            y += 46;
            chkAuto = new CheckBox { Text = L.T("Connect automatically when the app starts", "Uygulama açılınca otomatik bağlan"), Location = new Point(x, y), AutoSize = true, Checked = cfg.AutoConnect };
            chkAuto.CheckedChanged += (s, e) => { cfg.AutoConnect = chkAuto.Checked; cfg.Save(); };
            Controls.Add(chkAuto);

            y += 32;
            var grp = new GroupBox { Text = L.T("Live label relay (optional, OSC over Wi-Fi)", "Canlı etiket aktarıcı (isteğe bağlı, Wi-Fi üzerinden OSC)"), Location = new Point(x, y), Size = new Size(w, 92) };
            chkRelay = new CheckBox { Text = L.T("On", "Açık"), Location = new Point(10, 24), AutoSize = true, Checked = cfg.Relay };
            grp.Controls.Add(chkRelay);
            grp.Controls.Add(new Label { Text = L.T("Phone IP", "Telefon IP"), Location = new Point(70, 26), AutoSize = true });
            txtIp = new TextBox { Location = new Point(140, 22), Width = 120, Text = cfg.PhoneIp };
            grp.Controls.Add(txtIp);
            grp.Controls.Add(new Label { Text = L.T("port", "port"), Location = new Point(268, 26), AutoSize = true });
            txtPhonePort = new TextBox { Location = new Point(302, 22), Width = 52, Text = cfg.PhonePort };
            grp.Controls.Add(txtPhonePort);
            grp.Controls.Add(new Label { Text = L.T("DAW sends OSC to 127.0.0.1 port", "DAW OSC'yi şuraya yollar: 127.0.0.1 port"), Location = new Point(10, 58), AutoSize = true });
            txtListen = new TextBox { Location = new Point(302, 54), Width = 52, Text = cfg.ListenPort };
            grp.Controls.Add(txtListen);
            chkRelay.CheckedChanged += (s, e) => ApplyRelay();
            Controls.Add(grp);

            y += 102;
            txtLog = new TextBox { Location = new Point(x, y), Size = new Size(w, ClientSize.Height - y - 12), Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BackColor = Color.White, Font = new Font("Consolas", 8.5f) };
            Controls.Add(txtLog);

            link.Log += m => Ui(() => AddLog(m));
            link.StateChanged += st => Ui(UpdateStatus);
            relay.Log += m => Ui(() => AddLog(m));

            tray = new NotifyIcon { Icon = Icon, Text = "Midiboard Bridge", Visible = true };
            var menu = new ContextMenuStrip();
            menu.Items.Add(L.T("Show", "Göster"), null, (s, e) => ShowWindow());
            menu.Items.Add(L.T("Exit", "Çık"), null, (s, e) => { quitting = true; Close(); });
            tray.ContextMenuStrip = menu;
            tray.DoubleClick += (s, e) => ShowWindow();
            Resize += (s, e) => { if (WindowState == FormWindowState.Minimized) Hide(); };
            FormClosing += (s, e) =>
            {
                if (!quitting && e.CloseReason == CloseReason.UserClosing)
                {
                    e.Cancel = true; Hide();
                    tray.ShowBalloonTip(2000, "Midiboard Bridge", L.T("Still running in the tray.", "Sistem tepsisinde çalışmaya devam ediyor."), ToolTipIcon.Info);
                    return;
                }
                link.Stop(); relay.Stop(); tray.Visible = false;
            };

            // Layout above is in 96-dpi pixels; scale positions/sizes to the screen (fonts scale by themselves).
            float k;
            using (var g = CreateGraphics()) k = g.DpiX / 96f;   // DeviceDpi reports 96 under system DPI awareness
            if (k > 1.01f) Scale(new SizeF(k, k));

            uiTimer = new Timer { Interval = 1000 };
            uiTimer.Tick += (s, e) => { UpdateLoopInfo(); UpdateStatus(); };
            uiTimer.Start();

            Load += async (s, e) =>
            {
                CenterToScreen();   // after DPI scaling, so the bottom is not under the taskbar
                RefreshPorts();
                UpdateLoopInfo();
                UpdateStatus();
                await LoadDevices();
                if (cfg.Relay) ApplyRelay();
                if (cfg.AutoConnect && !string.IsNullOrEmpty(cfg.DeviceId)) Connect();
            };
        }

        void Ui(Action a) { if (IsHandleCreated) BeginInvoke(a); }

        void AddLog(string m)
        {
            txtLog.AppendText(string.Format("[{0:HH:mm:ss}] {1}\r\n", DateTime.Now, m));
        }

        void ShowWindow() { Show(); WindowState = FormWindowState.Normal; Activate(); }

        async Task LoadDevices()
        {
            btnRefresh.Enabled = false;
            try
            {
                var list = await BleMidiLink.PairedBleDevices();
                cbDevice.Items.Clear();
                foreach (var d in list) cbDevice.Items.Add(new DeviceItem { Id = d.Id, Name = d.Name, Midi = d.Midi });
                var pick = cbDevice.Items.Cast<DeviceItem>().FirstOrDefault(d => d.Id == cfg.DeviceId)
                        ?? cbDevice.Items.Cast<DeviceItem>().FirstOrDefault(d => d.Name == cfg.DeviceName);
                if (pick != null) cbDevice.SelectedItem = pick;
                else if (cbDevice.Items.Count > 0) cbDevice.SelectedIndex = 0;
                if (cbDevice.Items.Count == 0) AddLog(L.T("No paired Bluetooth LE devices found.", "Eşleşmiş Bluetooth LE cihazı bulunamadı."));
            }
            catch (Exception e) { AddLog(e.Message); }
            btnRefresh.Enabled = true;
        }

        void RefreshPorts()
        {
            var keep = string.IsNullOrEmpty(cbPort.Text) ? cfg.Port : cbPort.Text;
            cbPort.Items.Clear();
            foreach (var n in WinMM.OutputNames()) if (n != "Microsoft GS Wavetable Synth") cbPort.Items.Add(n);
            cbPort.Text = keep;
        }

        void UpdateLoopInfo()
        {
            string port = cbPort.Text.Trim();
            bool exists = WinMM.OutId(port) >= 0;
            if (!LoopMidi.Installed) { lblLoop.Text = L.T("loopMIDI is not installed. Install it (free, tobias-erichsen.de), then press Create port.", "loopMIDI kurulu değil. Kur (ücretsiz, tobias-erichsen.de), sonra Port oluştur'a bas."); lblLoop.ForeColor = Color.Firebrick; }
            else if (!exists) { lblLoop.Text = L.T("This port does not exist yet - press Create port.", "Bu port henüz yok - Port oluştur'a bas."); lblLoop.ForeColor = Color.DarkOrange; }
            else { lblLoop.Text = L.T("Port ready. In your DAW pick it as a MIDI input.", "Port hazır. DAW'da MIDI girişi olarak seç."); lblLoop.ForeColor = Color.SeaGreen; }
            btnCreatePort.Enabled = LoopMidi.Installed && !exists && port.Length > 0;
        }

        void CreatePort()
        {
            string port = cbPort.Text.Trim();
            if (port.Length == 0) return;
            AddLog(string.Format(L.T("Creating loopMIDI port \"{0}\" (loopMIDI restarts)...", "loopMIDI portu oluşturuluyor: \"{0}\" (loopMIDI yeniden başlar)..."), port));
            LoopMidi.CreatePort(port);
            var t = new Timer { Interval = 2500 };
            t.Tick += (s, e) => { t.Stop(); RefreshPorts(); UpdateLoopInfo(); AddLog(WinMM.OutId(port) >= 0 ? L.T("Port created.", "Port oluşturuldu.") : L.T("Port not visible yet - check loopMIDI.", "Port henüz görünmüyor - loopMIDI'yi kontrol et.")); };
            t.Start();
        }

        void Connect()
        {
            var d = cbDevice.SelectedItem as DeviceItem;
            if (d == null) { AddLog(L.T("Choose the phone first.", "Önce telefonu seç.")); return; }
            string port = cbPort.Text.Trim();
            cfg.DeviceId = d.Id; cfg.DeviceName = d.Name; cfg.Port = port; cfg.Save();
            if (!LoopMidi.Running) LoopMidi.Start();
            AddLog(string.Format(L.T("Connecting to {0} -> {1} ...", "{0} -> {1} bağlanıyor..."), d.Name, port));
            link.Start(d.Id, d.Name, port);
            UpdateStatus();
        }

        void Disconnect() { link.Stop(); AddLog(L.T("Stopped.", "Durduruldu.")); UpdateStatus(); }

        void ApplyRelay()
        {
            relay.Stop();
            cfg.Relay = chkRelay.Checked; cfg.PhoneIp = txtIp.Text.Trim(); cfg.ListenPort = txtListen.Text.Trim(); cfg.PhonePort = txtPhonePort.Text.Trim(); cfg.Save();
            if (!chkRelay.Checked) return;
            try { relay.Start(int.Parse(cfg.ListenPort), cfg.PhoneIp, int.Parse(cfg.PhonePort), 1000); }
            catch (Exception e) { AddLog(L.T("Relay error: ", "Aktarıcı hatası: ") + e.Message); chkRelay.Checked = false; }
        }

        void UpdateStatus()
        {
            var st = link.State;
            btnConnect.Text = st == LinkState.Stopped ? L.T("Connect", "Bağlan") : L.T("Disconnect", "Kes");
            cbDevice.Enabled = cbPort.Enabled = st == LinkState.Stopped;
            if (st == LinkState.Connected) { lblDot.ForeColor = Color.LimeGreen; lblStatus.Text = L.T("Connected", "Bağlı") + " - " + link.DeviceName; }
            else if (st == LinkState.Searching) { lblDot.ForeColor = Color.Orange; lblStatus.Text = L.T("Connecting...", "Bağlanıyor..."); }
            else { lblDot.ForeColor = Color.Silver; lblStatus.Text = L.T("Not connected", "Bağlı değil"); }
            if (st == LinkState.Searching)
                lblCount.Text = L.T("Keep Midiboard's Bluetooth screen open", "Midiboard'un Bluetooth ekranı açık kalsın");
            else
                lblCount.Text = string.Format(L.T("{0} MIDI messages from the phone{1}", "Telefondan {0} MIDI mesajı{1}"), link.RxMessages,
                    chkRelay.Checked ? string.Format(L.T(", {0} labels relayed", ", {0} etiket aktarıldı"), relay.Forwarded) : "");
            tray.Text = "Midiboard Bridge - " + (st == LinkState.Connected ? L.T("connected", "bağlı") : st == LinkState.Searching ? L.T("connecting", "bağlanıyor") : L.T("off", "kapalı"));
        }

        static Icon MakeIcon(Color c)
        {
            var bmp = new Bitmap(32, 32);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                using (var b = new SolidBrush(Color.FromArgb(30, 30, 40))) g.FillEllipse(b, 1, 1, 30, 30);
                using (var p = new SolidBrush(c))
                {
                    g.FillRectangle(p, 8, 9, 5, 14); g.FillRectangle(p, 14, 13, 5, 10); g.FillRectangle(p, 20, 7, 5, 16);
                }
            }
            return Icon.FromHandle(bmp.GetHicon());
        }
    }

    static class Program
    {
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool SetProcessDPIAware();

        [STAThread]
        static void Main()
        {
            try { SetProcessDPIAware(); } catch { }
            foreach (var a in Environment.GetCommandLineArgs())
            {
                if (a == "--en") L.Tr = false;
                if (a == "--tr") L.Tr = true;
            }
            bool created;
            using (var m = new System.Threading.Mutex(true, "MidiboardBridge.SingleInstance", out created))
            {
                if (!created) { MessageBox.Show(L.T("Midiboard Bridge is already running (see the tray).", "Midiboard Bridge zaten çalışıyor (sistem tepsisine bak)."), "Midiboard Bridge"); return; }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new MainForm());
            }
        }
    }
}
