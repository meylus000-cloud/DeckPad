using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace DeckPad
{
    /// <summary>
    /// Keeps a TCP connection to the tablet. In ADB mode it sets up
    /// "adb forward" (USB cable) and connects to 127.0.0.1; otherwise it
    /// connects to the given host (e.g. USB tethering address).
    /// </summary>
    public class TabletLink
    {
        public event Action<bool, string> StatusChanged;
        public event Action<int> Pressed;

        readonly DeckConfig cfg;
        readonly object wl = new object();
        readonly JavaScriptSerializer js = new JavaScriptSerializer();
        volatile bool stop;
        TcpClient client;
        NetworkStream stream;
        string lastConfigJson;
        bool connected;
        string lastMsg;

        public TabletLink(DeckConfig c) { cfg = c; }

        public void Start()
        {
            new Thread(Loop) { IsBackground = true }.Start();
        }

        public void Stop()
        {
            stop = true;
            lock (wl) { try { if (client != null) client.Close(); } catch (Exception) { } }
        }

        public void SetConfigJson(string json)
        {
            lastConfigJson = json;
            SendLine(json);
        }

        void Notify(bool ok, string msg)
        {
            if (ok == connected && msg == lastMsg) return;
            connected = ok;
            lastMsg = msg;
            var h = StatusChanged;
            if (h != null) h(ok, msg);
        }

        void Loop()
        {
            while (!stop)
            {
                TcpClient c = null;
                try
                {
                    string host = cfg.Host;
                    if (cfg.UseAdb)
                    {
                        string err = AdbForward();
                        if (err != null) { Notify(false, err); goto wait; }
                        host = "127.0.0.1";
                    }

                    c = new TcpClient();
                    c.NoDelay = true;
                    var ar = c.BeginConnect(host, cfg.Port, null, null);
                    if (!ar.AsyncWaitHandle.WaitOne(1500)) throw new IOException("timeout");
                    c.EndConnect(ar);

                    var ns = c.GetStream();
                    ns.ReadTimeout = 2500;
                    var rd = new StreamReader(ns, new UTF8Encoding(false));
                    string hello = rd.ReadLine();   // tablet app greets first
                    if (hello == null || hello.IndexOf("hello", StringComparison.Ordinal) < 0)
                        throw new IOException("no hello");
                    ns.ReadTimeout = 7000;

                    lock (wl) { client = c; stream = ns; }
                    Notify(true, "Tablet connected");
                    if (lastConfigJson != null) SendLine(lastConfigJson);

                    var mine = c;
                    new Thread(() =>
                    {
                        while (!stop && client == mine)
                        {
                            SendLine("{\"type\":\"ping\"}");
                            Thread.Sleep(2000);
                        }
                    }) { IsBackground = true }.Start();

                    string line;
                    while (!stop && (line = rd.ReadLine()) != null) Handle(line);
                }
                catch (Exception)
                {
                    if (!connected)
                        Notify(false, cfg.UseAdb
                            ? "Waiting for tablet (USB cable, USB debugging, DeckPad app open)"
                            : "Waiting for tablet at " + cfg.Host);
                }
                finally
                {
                    lock (wl) { if (client == c) { client = null; stream = null; } }
                    try { if (c != null) c.Close(); } catch (Exception) { }
                }

                if (connected)
                    Notify(false, "Connection lost, reconnecting...");
                wait:
                Thread.Sleep(1500);
            }
        }

        string AdbForward()
        {
            string adb = cfg.AdbPath;
            if (string.IsNullOrWhiteSpace(adb))
            {
                string local = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "adb.exe");
                adb = File.Exists(local) ? local : "adb";
            }
            try
            {
                var psi = new ProcessStartInfo(adb, "forward tcp:" + cfg.Port + " tcp:" + cfg.Port)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using (var p = Process.Start(psi))
                {
                    if (!p.WaitForExit(4000)) { try { p.Kill(); } catch (Exception) { } }
                    if (p.HasExited && p.ExitCode != 0)
                        return "No tablet found via adb. Enable USB debugging and plug in the cable.";
                }
                return null;
            }
            catch (Exception)
            {
                return "adb.exe not found. Put it next to DeckPad.exe or set its path.";
            }
        }

        void Handle(string line)
        {
            try
            {
                var d = js.Deserialize<Dictionary<string, object>>(line);
                object t;
                if (d.TryGetValue("type", out t) && (t as string) == "press")
                {
                    var h = Pressed;
                    if (h != null) h(Convert.ToInt32(d["i"]));
                }
            }
            catch (Exception) { }
        }

        void SendLine(string s)
        {
            lock (wl)
            {
                if (stream == null) return;
                try
                {
                    byte[] b = new UTF8Encoding(false).GetBytes(s + "\n");
                    stream.Write(b, 0, b.Length);
                }
                catch (Exception)
                {
                    try { client.Close(); } catch (Exception) { }
                }
            }
        }
    }
}
