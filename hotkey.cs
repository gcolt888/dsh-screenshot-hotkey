// Global hotkey bridge for @local/dsh-shot-hotkey2.
//
// DSH's own shortcut service only receives keys while its window is focused, so
// this helper owns the system-wide chord. It deliberately does NOT raise the DSH
// window: raising it would cover the very screen region the user wants to snip.
// Instead it runs shot.exe directly (the selector draws over whatever is on
// screen), then drops the finished PNG into a queue directory that the plugin's
// host half serves to the page, which pastes it into the composer.
//
// The chord itself comes from the Host, which reads DSH's keybindings.json — so
// rebinding "截图标注" in DSH's shortcut panel moves this global hotkey too.
//
// It exits on its own when the owning Host process disappears.

using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

internal static class DshShotHotkey
{
    private const int WM_HOTKEY = 0x0312;
    private const uint MOD_ALT = 0x0001;
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_SHIFT = 0x0004;
    private const uint MOD_WIN = 0x0008;
    private const uint MOD_NOREPEAT = 0x4000;

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public UIntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    [DllImport("user32.dll")]
    private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    private static string logPath;
    private static string shotExe;
    private static string queueDir;

    private static void Log(string message)
    {
        try
        {
            File.AppendAllText(logPath, "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "] " + message + Environment.NewLine);
        }
        catch
        {
            // logging must never take the listener down
        }
    }

    /** Map the page's KeyboardEvent.code to a virtual-key code; 0 means unsupported. */
    private static ushort VirtualKey(string code)
    {
        if (string.IsNullOrEmpty(code)) return 0;
        if (code.Length == 4 && code.StartsWith("Key", StringComparison.Ordinal))
        {
            char letter = code[3];
            if (letter >= 'A' && letter <= 'Z') return (ushort)(0x41 + (letter - 'A'));
            return 0;
        }
        if (code.Length == 6 && code.StartsWith("Digit", StringComparison.Ordinal))
        {
            char digit = code[5];
            if (digit >= '0' && digit <= '9') return (ushort)(0x30 + (digit - '0'));
            return 0;
        }
        if (code.Length >= 2 && code[0] == 'F')
        {
            int number;
            if (int.TryParse(code.Substring(1), out number) && number >= 1 && number <= 24) return (ushort)(0x70 + number - 1);
        }
        switch (code)
        {
            case "Slash": return 0xBF;
            case "Semicolon": return 0xBA;
            case "Comma": return 0xBC;
            case "Period": return 0xBE;
            case "Backquote": return 0xC0;
            case "Minus": return 0xBD;
            case "Equal": return 0xBB;
            case "BracketLeft": return 0xDB;
            case "BracketRight": return 0xDD;
            case "Backslash": return 0xDC;
            case "Quote": return 0xDE;
            case "Space": return 0x20;
            case "Tab": return 0x09;
            case "Enter": return 0x0D;
            case "PrintScreen": return 0x2C;
            case "Insert": return 0x2D;
            case "Home": return 0x24;
            case "End": return 0x23;
            case "PageUp": return 0x21;
            case "PageDown": return 0x22;
            default: return 0;
        }
    }

    /** Translate the page's modifier names into RegisterHotKey flags. */
    private static uint ModifierFlags(string modifiers)
    {
        uint flags = 0;
        if (string.IsNullOrEmpty(modifiers)) return flags;
        foreach (string raw in modifiers.Split(','))
        {
            string name = raw.Trim().ToLowerInvariant();
            if (name == "primary" || name == "control" || name == "ctrl") flags |= MOD_CONTROL;
            else if (name == "alt" || name == "option") flags |= MOD_ALT;
            else if (name == "shift") flags |= MOD_SHIFT;
            else if (name == "meta" || name == "command" || name == "win") flags |= MOD_WIN;
        }
        return flags;
    }

    private static void Capture()
    {
        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
        string png = Path.Combine(queueDir, "shot-" + stamp + ".png");
        Log("hotkey pressed, opening the selector");
        try
        {
            var info = new ProcessStartInfo();
            info.FileName = shotExe;
            info.Arguments = "\"" + png + "\"";
            info.UseShellExecute = false;
            info.CreateNoWindow = true;
            info.WorkingDirectory = Path.GetDirectoryName(shotExe);
            using (var process = Process.Start(info))
            {
                process.WaitForExit(600000);
                if (process.ExitCode == 0 && File.Exists(png))
                {
                    string meta = Path.Combine(queueDir, "ready-" + stamp + ".json");
                    File.WriteAllText(meta, "{\"png\":" + Quote(png) + "}");
                    Log("captured " + Path.GetFileName(png) + ", queued for the page");
                }
                else
                {
                    Log("capture ended without an image (exit " + process.ExitCode + ")");
                    try { File.Delete(png); } catch { }
                }
            }
        }
        catch (Exception error)
        {
            Log("capture failed: " + error.Message);
        }
    }

    private static string Quote(string value)
    {
        return "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }

    private static void WatchParent(int parentPid)
    {
        var thread = new Thread(delegate()
        {
            while (true)
            {
                Thread.Sleep(2000);
                try { Process.GetProcessById(parentPid); }
                catch { Log("parent process " + parentPid + " is gone, exiting"); Environment.Exit(0); }
            }
        });
        thread.IsBackground = true;
        thread.Start();
    }

    private static int Main(string[] args)
    {
        // parentPid shotExe queueDir code modifiers
        if (args.Length < 5) return 1;
        int parentPid;
        int.TryParse(args[0], out parentPid);
        shotExe = args[1];
        queueDir = args[2];
        string code = args[3];
        string modifiers = args[4];
        logPath = Path.Combine(Path.GetTempPath(), "dsh-shot-hotkey.log");
        Directory.CreateDirectory(queueDir);

        ushort vk = VirtualKey(code);
        uint flags = ModifierFlags(modifiers);
        if (vk == 0 || flags == 0)
        {
            Log("unsupported shortcut " + modifiers + "+" + code + ", no global hotkey registered");
            return 3;
        }
        if (parentPid > 0) WatchParent(parentPid);

        if (!RegisterHotKey(IntPtr.Zero, 1, flags | MOD_NOREPEAT, vk))
        {
            Log("RegisterHotKey(" + modifiers + "+" + code + ") failed, win32=" + Marshal.GetLastWin32Error());
            return 2;
        }
        Log("global hotkey " + modifiers + "+" + code + " registered (parent=" + parentPid + ", queue=" + queueDir + ")");

        MSG msg;
        while (GetMessage(out msg, IntPtr.Zero, 0, 0) > 0)
        {
            if (msg.message == WM_HOTKEY) Capture();
        }
        UnregisterHotKey(IntPtr.Zero, 1);
        Log("message loop ended");
        return 0;
    }
}
