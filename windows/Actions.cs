using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace DeckPad
{
    /// <summary>Executes the action bound to a button (keys, text, programs, URLs, media keys).</summary>
    public static class Actions
    {
        public static readonly string[] MediaNames =
            { "PlayPause", "Next", "Previous", "Stop", "Mute", "VolumeUp", "VolumeDown" };

        public static void Run(DeckButton b)
        {
            try
            {
                switch (b.Type)
                {
                    case "hotkey": Hotkey(b.Value); break;
                    case "text": TypeText(b.Value); break;
                    case "run":
                        if (!string.IsNullOrWhiteSpace(b.Value))
                            Process.Start(new ProcessStartInfo(b.Value, b.Args ?? "") { UseShellExecute = true });
                        break;
                    case "url":
                        if (!string.IsNullOrWhiteSpace(b.Value))
                            Process.Start(new ProcessStartInfo(b.Value) { UseShellExecute = true });
                        break;
                    case "media": Tap(new List<ushort>(), MediaKey(b.Value)); break;
                }
            }
            catch (Exception) { }
        }

        static ushort MediaKey(string n)
        {
            switch (n)
            {
                case "PlayPause": return (ushort)Keys.MediaPlayPause;
                case "Next": return (ushort)Keys.MediaNextTrack;
                case "Previous": return (ushort)Keys.MediaPreviousTrack;
                case "Stop": return (ushort)Keys.MediaStop;
                case "Mute": return (ushort)Keys.VolumeMute;
                case "VolumeUp": return (ushort)Keys.VolumeUp;
                case "VolumeDown": return (ushort)Keys.VolumeDown;
            }
            return 0;
        }

        // ------------------------------------------------------------ hotkeys

        static void Hotkey(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return;
            var mods = new List<ushort>();
            ushort key = 0;
            foreach (var raw in s.Split('+'))
            {
                string t = raw.Trim();
                if (t.Length == 0) continue;
                switch (t.ToLowerInvariant())
                {
                    case "ctrl": case "control": mods.Add((ushort)Keys.ControlKey); continue;
                    case "alt": mods.Add((ushort)Keys.Menu); continue;
                    case "shift": mods.Add((ushort)Keys.ShiftKey); continue;
                    case "win": case "windows": mods.Add((ushort)Keys.LWin); continue;
                }
                key = ParseKey(t);
            }
            Tap(mods, key);
        }

        static ushort ParseKey(string t)
        {
            string l = t.ToLowerInvariant();
            switch (l)
            {
                case "esc": return (ushort)Keys.Escape;
                case "enter": case "return": return (ushort)Keys.Return;
                case "backspace": return (ushort)Keys.Back;
                case "del": return (ushort)Keys.Delete;
                case "ins": return (ushort)Keys.Insert;
                case "pgup": return (ushort)Keys.PageUp;
                case "pgdn": case "pagedown": return (ushort)Keys.Next;
                case "space": return (ushort)Keys.Space;
                case "printscreen": case "prtsc": return (ushort)Keys.PrintScreen;
                case "up": return (ushort)Keys.Up;
                case "down": return (ushort)Keys.Down;
                case "left": return (ushort)Keys.Left;
                case "right": return (ushort)Keys.Right;
                case "-": return 0xBD;
                case "=": return 0xBB;
                case ",": return 0xBC;
                case ".": return 0xBE;
                case "/": return 0xBF;
                case ";": return 0xBA;
                case "[": return 0xDB;
                case "]": return 0xDD;
            }
            if (t.Length == 1)
            {
                char c = char.ToUpperInvariant(t[0]);
                if ((c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9')) return (ushort)c;
            }
            Keys k;
            if (Enum.TryParse(t, true, out k)) return (ushort)k;
            return 0;
        }

        static bool IsExtended(ushort vk)
        {
            switch ((Keys)vk)
            {
                case Keys.Up: case Keys.Down: case Keys.Left: case Keys.Right:
                case Keys.Home: case Keys.End: case Keys.PageUp: case Keys.Next:
                case Keys.Insert: case Keys.Delete: case Keys.LWin: case Keys.RWin:
                case Keys.PrintScreen:
                    return true;
            }
            return false;
        }

        static void Tap(List<ushort> mods, ushort key)
        {
            // Press modifiers and the key, hold briefly (some apps poll and miss
            // instant taps), then release. Real scan codes are included so apps
            // that read them (hotkey hooks) see a normal keystroke.
            var down = new List<INPUT>();
            foreach (var m in mods) down.Add(Key(m, 0));
            if (key != 0) down.Add(Key(key, 0));
            Send(down);
            System.Threading.Thread.Sleep(70);

            var up = new List<INPUT>();
            if (key != 0) up.Add(Key(key, KEYEVENTF_KEYUP));
            for (int i = mods.Count - 1; i >= 0; i--) up.Add(Key(mods[i], KEYEVENTF_KEYUP));
            Send(up);
        }

        static INPUT Key(ushort vk, uint flags)
        {
            if (IsExtended(vk)) flags |= KEYEVENTF_EXTENDEDKEY;
            ushort scan = (ushort)MapVirtualKey(vk, 0);
            return Kb(vk, scan, flags);
        }

        [DllImport("user32.dll")]
        static extern uint MapVirtualKey(uint uCode, uint uMapType);

        static void TypeText(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            var list = new List<INPUT>();
            foreach (char c in text)
            {
                if (c == '\r') continue;
                if (c == '\n')
                {
                    list.Add(Kb((ushort)Keys.Return, 0, 0));
                    list.Add(Kb((ushort)Keys.Return, 0, KEYEVENTF_KEYUP));
                    continue;
                }
                list.Add(Kb(0, c, KEYEVENTF_UNICODE));
                list.Add(Kb(0, c, KEYEVENTF_UNICODE | KEYEVENTF_KEYUP));
            }
            Send(list);
        }

        // ------------------------------------------------------------ SendInput

        const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
        const uint KEYEVENTF_KEYUP = 0x0002;
        const uint KEYEVENTF_UNICODE = 0x0004;

        static INPUT Kb(ushort vk, ushort scan, uint flags)
        {
            var i = new INPUT();
            i.type = 1; // INPUT_KEYBOARD
            i.U.ki.wVk = vk;
            i.U.ki.wScan = scan;
            i.U.ki.dwFlags = flags;
            return i;
        }

        static void Send(List<INPUT> list)
        {
            if (list.Count == 0) return;
            SendInput((uint)list.Count, list.ToArray(), Marshal.SizeOf(typeof(INPUT)));
        }

        [DllImport("user32.dll", SetLastError = true)]
        static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        [StructLayout(LayoutKind.Sequential)]
        struct INPUT
        {
            public uint type;
            public InputUnion U;
        }

        [StructLayout(LayoutKind.Explicit)]
        struct InputUnion
        {
            [FieldOffset(0)] public MOUSEINPUT mi;
            [FieldOffset(0)] public KEYBDINPUT ki;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct MOUSEINPUT
        {
            public int dx;
            public int dy;
            public uint mouseData;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct KEYBDINPUT
        {
            public ushort wVk;
            public ushort wScan;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }
    }
}
