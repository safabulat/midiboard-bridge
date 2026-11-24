// Midiboard Bridge - core: BLE MIDI (direct GATT) -> loopMIDI port, and an optional OSC label relay.
// C# 5 / .NET Framework 4.8; no Windows SDK needed (see build.bat).
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Windows.Foundation;
using Windows.Security.Cryptography;

namespace MidiboardBridge
{
    static class WinRT
    {
        public static Task<T> Await<T>(IAsyncOperation<T> op)
        {
            var tcs = new TaskCompletionSource<T>();
            op.Completed = (o, s) =>
            {
                if (s == AsyncStatus.Completed) tcs.TrySetResult(o.GetResults());
                else if (s == AsyncStatus.Error) tcs.TrySetException(o.ErrorCode);
                else tcs.TrySetCanceled();
            };
            return tcs.Task;
        }

        public static async Task<T> WithTimeout<T>(Task<T> t, int ms)
        {
            var done = await Task.WhenAny(t, Task.Delay(ms));
            if (done != t) throw new TimeoutException(string.Format(L.T("no answer in {0} s", "{0} sn içinde cevap yok"), ms / 1000));
            return t.Result;
        }
    }

    // ---------------- WinMM (classic MIDI ports that DAWs see) ----------------
    static class WinMM
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct OCAPS { public ushort mid, pid; public uint ver; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string name; public ushort t, v, n, cm; public uint sup; }
        [StructLayout(LayoutKind.Sequential)]
        public struct HDR { public IntPtr data; public uint len, rec; public IntPtr user; public uint flags; public IntPtr next, res; public uint off; [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)] public IntPtr[] r2; }
        [DllImport("winmm.dll")] static extern uint midiOutGetNumDevs();
        [DllImport("winmm.dll", CharSet = CharSet.Unicode)] static extern uint midiOutGetDevCaps(UIntPtr i, out OCAPS c, uint sz);
        [DllImport("winmm.dll")] public static extern uint midiOutOpen(out IntPtr h, uint id, IntPtr cb, IntPtr inst, uint fl);
        [DllImport("winmm.dll")] public static extern uint midiOutClose(IntPtr h);
        [DllImport("winmm.dll")] public static extern uint midiOutShortMsg(IntPtr h, uint m);
        [DllImport("winmm.dll")] public static extern uint midiOutPrepareHeader(IntPtr h, IntPtr hdr, uint sz);
        [DllImport("winmm.dll")] public static extern uint midiOutLongMsg(IntPtr h, IntPtr hdr, uint sz);
        [DllImport("winmm.dll")] public static extern uint midiOutUnprepareHeader(IntPtr h, IntPtr hdr, uint sz);

        public static List<string> OutputNames()
        {
            var l = new List<string>();
            for (uint i = 0; i < midiOutGetNumDevs(); i++)
            {
                OCAPS c; midiOutGetDevCaps((UIntPtr)i, out c, (uint)Marshal.SizeOf(typeof(OCAPS)));
                l.Add(c.name);
            }
            return l;
        }
        public static int OutId(string n) { var l = OutputNames(); return l.IndexOf(n); }
    }

    // ---------------- loopMIDI integration ----------------
    static class LoopMidi
    {
        const string RegPorts = @"Software\Tobias Erichsen\loopMIDI\Ports";
        public static string Exe
        {
            get
            {
                foreach (var p in new[] { Environment.GetEnvironmentVariable("ProgramFiles(x86)"), Environment.GetEnvironmentVariable("ProgramFiles") })
                {
                    if (string.IsNullOrEmpty(p)) continue;
                    var f = System.IO.Path.Combine(p, @"Tobias Erichsen\loopMIDI\loopMIDI.exe");
                    if (System.IO.File.Exists(f)) return f;
                }
                return null;
            }
        }
        public static bool Installed { get { return Exe != null; } }
        public static bool Running { get { return System.Diagnostics.Process.GetProcessesByName("loopMIDI").Length > 0; } }

        public static void Start()
        {
            if (!Running && Installed) System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Exe) { WindowStyle = System.Diagnostics.ProcessWindowStyle.Minimized });
        }

        // loopMIDI keeps its ports in HKCU\...\loopMIDI\Ports (value name = port name). Add ours and restart it.
        public static void CreatePort(string name)
        {
            using (var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RegPorts))
                k.SetValue(name, 1, Microsoft.Win32.RegistryValueKind.DWord);
            foreach (var p in System.Diagnostics.Process.GetProcessesByName("loopMIDI")) { try { p.Kill(); p.WaitForExit(3000); } catch { } }
            Start();
        }
    }

    public enum LinkState { Stopped, Searching, Connected }

    // ---------------- BLE MIDI link ----------------
    class BleMidiLink
    {
        static readonly Guid MIDI_SVC = new Guid("03B80E5A-EDE8-4B33-A751-6CE34EC4C700");
        static readonly Guid MIDI_CHR = new Guid("7772E5DB-3868-4112-A1A9-F2669D106BF3");

        public event Action<string> Log;
        public event Action<LinkState> StateChanged;
        public long RxMessages;

        string deviceId, deviceName, portName;
        IntPtr hOut = IntPtr.Zero;
        BluetoothLEDevice ble;
        GattSession session;
        GattDeviceService svc;
        GattCharacteristic chr;
        volatile bool up, running;
        LinkState state = LinkState.Stopped;
        readonly AutoResetEvent kick = new AutoResetEvent(false);
        readonly object outLock = new object();
        readonly List<byte> sysex = new List<byte>();
        bool inSysex;
        int emptyTries;

        public LinkState State { get { return state; } }
        public string DeviceName { get { return deviceName; } }

        void Say(string fmt, params object[] a) { var h = Log; if (h != null) h(string.Format(fmt, a)); }
        void SetState(LinkState s) { if (state == s) return; state = s; var h = StateChanged; if (h != null) h(s); }

        public class Found { public string Id, Name; public bool Midi; }

        // Paired BLE devices; the ones whose (cached) services include BLE MIDI come first, marked Midi=true.
        // Cached lookup does not connect to the device.
        public static async Task<List<Found>> PairedBleDevices()
        {
            var all = await WinRT.Await(DeviceInformation.FindAllAsync(BluetoothLEDevice.GetDeviceSelectorFromPairingState(true)));
            // Windows lists BLE MIDI devices as "<name> (Bluetooth MIDI IN)" - use that to know which are MIDI.
            var midiNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var ins = await WinRT.Await(DeviceInformation.FindAllAsync(Windows.Devices.Midi.MidiInPort.GetDeviceSelector()));
                foreach (var m in ins)
                {
                    var n = m.Name ?? "";
                    int k = n.LastIndexOf(" (Bluetooth MIDI", StringComparison.OrdinalIgnoreCase);
                    if (k > 0) midiNames.Add(n.Substring(0, k));
                }
            }
            catch { }
            // ...but that list only holds devices Windows is currently connected to, so also ask the (cached) GATT table.
            var tasks = all.Where(x => !string.IsNullOrEmpty(x.Name)).Select(async d =>
            {
                var f = new Found { Id = d.Id, Name = d.Name, Midi = midiNames.Contains(d.Name) };
                if (!f.Midi)
                {
                    try
                    {
                        using (var dev = await WinRT.WithTimeout(WinRT.Await(BluetoothLEDevice.FromIdAsync(d.Id)), 1500))
                        {
                            if (dev != null)
                            {
                                var sr = await WinRT.WithTimeout(WinRT.Await(dev.GetGattServicesForUuidAsync(MIDI_SVC, BluetoothCacheMode.Cached)), 1500);
                                f.Midi = sr.Status == GattCommunicationStatus.Success && sr.Services.Count > 0;
                                foreach (var s in sr.Services) s.Dispose();
                            }
                        }
                    }
                    catch { }
                }
                return f;
            }).ToList();
            var list = (await Task.WhenAll(tasks)).ToList();
            return list.OrderBy(x => x.Midi ? 0 : 1).ThenBy(x => x.Name).ToList();
        }

        public void Start(string devId, string devName, string outPort)
        {
            Stop();
            deviceId = devId; deviceName = devName; portName = outPort;
            running = true;
            var t = new Thread(() => Loop().Wait()) { IsBackground = true, Name = "BLE MIDI link" };
            t.Start();
        }

        public void Stop()
        {
            running = false;
            kick.Set();
            Teardown(null);
            if (ble != null) { try { ble.Dispose(); } catch { } ble = null; }
            lock (outLock) { if (hOut != IntPtr.Zero) { WinMM.midiOutClose(hOut); hOut = IntPtr.Zero; } }
            SetState(LinkState.Stopped);
        }

        bool OpenOut()
        {
            lock (outLock)
            {
                if (hOut != IntPtr.Zero) return true;
                int id = WinMM.OutId(portName);
                if (id < 0) return false;
                IntPtr h;
                if (WinMM.midiOutOpen(out h, (uint)id, IntPtr.Zero, IntPtr.Zero, 0) != 0) return false;
                hOut = h;
            }
            Say(L.T("Output port open: \"{0}\"", "Çıkış portu açık: \"{0}\""), portName);
            return true;
        }

        async Task Loop()
        {
            SetState(LinkState.Searching);
            int waitedPort = 0;
            while (running && !OpenOut())
            {
                if (waitedPort == 0) { Say(L.T("Port \"{0}\" not found - is loopMIDI running with this port?", "\"{0}\" portu yok - loopMIDI bu portla açık mı?"), portName); LoopMidi.Start(); }
                waitedPort++;
                kick.WaitOne(1500);
            }
            while (running)
            {
                if (!up)
                {
                    SetState(LinkState.Searching);
                    bool ok = false;
                    try { ok = await TryConnect(); }
                    catch (Exception e) { Say(L.T("Attempt failed: {0}", "Deneme başarısız: {0}"), e.Message); }
                    if (!ok) { Teardown(null); kick.WaitOne(2000); continue; }
                    SetState(LinkState.Connected);
                }
                kick.WaitOne(1000);
            }
        }

        void Teardown(string why)
        {
            if (up && why != null) Say(L.T("Link closed: {0}", "Bağlantı kapandı: {0}"), why);
            up = false;
            var c = chr; chr = null;
            if (c != null) { try { c.ValueChanged -= OnNotify; } catch { } }
            if (svc != null) { try { svc.Dispose(); } catch { } svc = null; }
            if (session != null) { try { session.Dispose(); } catch { } session = null; }
        }

        async Task<bool> TryConnect()
        {
            if (ble == null)
            {
                ble = await WinRT.Await(BluetoothLEDevice.FromIdAsync(deviceId));
                if (ble == null) { Say(L.T("Bluetooth device not available", "Bluetooth cihazı açılamadı")); return false; }
                ble.ConnectionStatusChanged += (s, o) =>
                {
                    if (s.ConnectionStatus == BluetoothConnectionStatus.Disconnected && up) { Teardown(L.T("Bluetooth disconnected", "Bluetooth koptu")); kick.Set(); }
                };
            }
            try
            {
                session = await WinRT.Await(GattSession.FromDeviceIdAsync(ble.BluetoothDeviceId));
                session.MaintainConnection = true;
            }
            catch { }

            // Windows often answers "Success" with no services for the first tries; retrying fixes it.
            var sr = await WinRT.WithTimeout(WinRT.Await(ble.GetGattServicesForUuidAsync(MIDI_SVC, BluetoothCacheMode.Uncached)), 12000);
            if (sr.Status != GattCommunicationStatus.Success || sr.Services.Count == 0)
            {
                emptyTries++;
                if (emptyTries == 8 || emptyTries % 30 == 0)
                    Say(L.T("No Bluetooth MIDI service on \"{0}\" yet. Is Midiboard's Bluetooth screen open on the phone, and is this the right device?",
                            "\"{0}\" üzerinde henüz Bluetooth MIDI servisi yok. Telefonda Midiboard'un Bluetooth ekranı açık mı, doğru cihaz mı?"), deviceName);
                return false;
            }
            emptyTries = 0;
            svc = sr.Services[0];
            var cr = await WinRT.WithTimeout(WinRT.Await(svc.GetCharacteristicsForUuidAsync(MIDI_CHR, BluetoothCacheMode.Uncached)), 8000);
            if (cr.Status != GattCommunicationStatus.Success || cr.Characteristics.Count == 0) return false;
            var c = cr.Characteristics[0];
            c.ValueChanged += OnNotify;
            var st = await WinRT.WithTimeout(WinRT.Await(c.WriteClientCharacteristicConfigurationDescriptorAsync(GattClientCharacteristicConfigurationDescriptorValue.Notify)), 8000);
            if (st != GattCommunicationStatus.Success) { c.ValueChanged -= OnNotify; Say(L.T("Subscription refused ({0})", "Abonelik reddedildi ({0})"), st); return false; }
            chr = c; up = true;
            Say(L.T("Connected to {0} - MIDI is flowing", "{0} bağlandı - MIDI akıyor"), deviceName);
            return true;
        }

        static int DataLen(byte st)
        {
            if (st < 0xF0) { int h = st & 0xF0; return (h == 0xC0 || h == 0xD0) ? 1 : 2; }
            if (st == 0xF1 || st == 0xF3) return 1;
            if (st == 0xF2) return 2;
            return 0;
        }

        void Emit(byte[] m)
        {
            lock (outLock)
            {
                if (hOut == IntPtr.Zero) return;
                if (m.Length <= 3 && m[0] != 0xF0)
                {
                    uint d = m[0]; if (m.Length > 1) d |= (uint)m[1] << 8; if (m.Length > 2) d |= (uint)m[2] << 16;
                    WinMM.midiOutShortMsg(hOut, d);
                }
                else
                {
                    var hdr = new WinMM.HDR();
                    IntPtr buf = Marshal.AllocHGlobal(m.Length);
                    Marshal.Copy(m, 0, buf, m.Length);
                    hdr.data = buf; hdr.len = (uint)m.Length; hdr.rec = (uint)m.Length; hdr.r2 = new IntPtr[8];
                    int sz = Marshal.SizeOf(typeof(WinMM.HDR));
                    IntPtr ph = Marshal.AllocHGlobal(sz);
                    Marshal.StructureToPtr(hdr, ph, false);
                    WinMM.midiOutPrepareHeader(hOut, ph, (uint)sz);
                    WinMM.midiOutLongMsg(hOut, ph, (uint)sz);
                    for (int k = 0; k < 50 && WinMM.midiOutUnprepareHeader(hOut, ph, (uint)sz) == 65; k++) Thread.Sleep(1);
                    Marshal.FreeHGlobal(ph); Marshal.FreeHGlobal(buf);
                }
            }
            Interlocked.Increment(ref RxMessages);
        }

        // BLE-MIDI packet: [header 10xxxxxx] ([timestamp 1xxxxxxx] [status] [data..] | [data..] running status)...
        void OnNotify(GattCharacteristic sender, GattValueChangedEventArgs args)
        {
            byte[] p; CryptographicBuffer.CopyToByteArray(args.CharacteristicValue, out p);
            if (p == null || p.Length < 2 || (p[0] & 0xC0) != 0x80) return;
            int i = 1; byte running = 0;
            while (i < p.Length)
            {
                byte b = p[i];
                if (inSysex)
                {
                    if ((b & 0x80) == 0) { sysex.Add(b); i++; continue; }
                    if (i + 1 < p.Length && p[i + 1] == 0xF7) { sysex.Add(0xF7); Emit(sysex.ToArray()); sysex.Clear(); inSysex = false; i += 2; continue; }
                    i++; continue;
                }
                if ((b & 0x80) != 0)
                {
                    i++;
                    if (i >= p.Length) break;
                    b = p[i];
                    if ((b & 0x80) != 0)
                    {
                        if (b == 0xF0) { inSysex = true; sysex.Clear(); sysex.Add(0xF0); i++; continue; }
                        if (b >= 0xF8) { Emit(new[] { b }); i++; continue; }
                        running = b; i++;
                    }
                }
                if (running == 0) { i++; continue; }
                int n = DataLen(running);
                if (i + n > p.Length) break;
                var m = new byte[1 + n]; m[0] = running;
                Array.Copy(p, i, m, 1, n);
                Emit(m);
                i += n;
            }
        }
    }

    // ---------------- OSC label relay ----------------
    // The DAW / live host sends OSC to 127.0.0.1:<listen>; forwarded at once to the phone, and the last message of
    // every address is re-sent every second, so a lost UDP packet costs at most ~1 s of a stale label.
    class OscRelay
    {
        public event Action<string> Log;
        public long Forwarded;
        readonly Dictionary<string, byte[]> last = new Dictionary<string, byte[]>();
        readonly object gate = new object();
        UdpClient rx, tx;
        volatile bool running;

        public void Start(int listenPort, string phoneIp, int phonePort, int resendMs)
        {
            Stop();
            var target = new IPEndPoint(IPAddress.Parse(phoneIp), phonePort);
            rx = new UdpClient(new IPEndPoint(IPAddress.Loopback, listenPort));
            tx = new UdpClient(0);
            running = true;
            var h = Log; if (h != null) h(string.Format(L.T("Label relay: 127.0.0.1:{0} -> {1}:{2}", "Etiket aktarıcı: 127.0.0.1:{0} -> {1}:{2}"), listenPort, phoneIp, phonePort));
            new Thread(() =>
            {
                while (running)
                {
                    Thread.Sleep(resendMs);
                    List<byte[]> snap; lock (gate) snap = new List<byte[]>(last.Values);
                    foreach (var m in snap) { try { tx.Send(m, m.Length, target); } catch { } }
                }
            }) { IsBackground = true }.Start();
            new Thread(() =>
            {
                var from = new IPEndPoint(IPAddress.Any, 0);
                while (running)
                {
                    byte[] p;
                    try { p = rx.Receive(ref from); } catch { if (!running) return; continue; }
                    try { tx.Send(p, p.Length, target); Interlocked.Increment(ref Forwarded); } catch { }
                    Remember(p, 0, p.Length);
                }
            }) { IsBackground = true }.Start();
        }

        public void Stop()
        {
            running = false;
            if (rx != null) { try { rx.Close(); } catch { } rx = null; }
            if (tx != null) { try { tx.Close(); } catch { } tx = null; }
            lock (gate) last.Clear();
        }

        void Remember(byte[] p, int off, int len)
        {
            if (len < 8) return;
            int end = off; while (end < off + len && p[end] != 0) end++;
            string addr = Encoding.ASCII.GetString(p, off, end - off);
            if (addr == "#bundle")
            {
                int i = off + 16;
                while (i + 4 <= off + len)
                {
                    int sz = (p[i] << 24) | (p[i + 1] << 16) | (p[i + 2] << 8) | p[i + 3];
                    i += 4;
                    if (sz <= 0 || i + sz > off + len) break;
                    Remember(p, i, sz);
                    i += sz;
                }
                return;
            }
            if (addr.Length == 0 || addr[0] != '/') return;
            var copy = new byte[len]; Buffer.BlockCopy(p, off, copy, 0, len);
            lock (gate) last[addr] = copy;
        }
    }

    // ---------------- tiny localisation (English / Turkish) ----------------
    static class L
    {
        public static bool Tr = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "tr";
        public static string T(string en, string tr) { return Tr ? tr : en; }
    }
}
