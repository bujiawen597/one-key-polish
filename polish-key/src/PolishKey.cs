// 一键润色 · 托盘工具
// 全局热键 Ctrl+Alt+P：复制当前选中文字 → 本机桥接（127.0.0.1:8791/polish）→ 粘贴替换。
// 依赖 DSH 桌面端正在运行（桥接插件 dsh-polish-bridge 已装）。

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using System.Web.Script.Serialization;

namespace PolishKey
{
    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            bool autostart = false;
            foreach (string a in args) if (a == "--autostart") autostart = true;

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            using (new TrayApp(autostart))
            {
                Application.Run();
            }
        }
    }

    sealed class Config
    {
        public int port = 8791;
        public string style = "chat";
        public string hotkey = "Ctrl+Alt+P";
        public bool selectAllFallback = true;

        static string FilePath
        {
            get
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PolishKey");
                Directory.CreateDirectory(dir);
                return Path.Combine(dir, "config.json");
            }
        }

        public static Config Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    var loaded = new JavaScriptSerializer().Deserialize<Config>(File.ReadAllText(FilePath, Encoding.UTF8));
                    if (loaded != null) return loaded;
                }
            }
            catch { }
            var fresh = new Config();
            fresh.Save();
            return fresh;
        }

        public void Save()
        {
            try { File.WriteAllText(FilePath, new JavaScriptSerializer().Serialize(this), Encoding.UTF8); } catch { }
        }
    }

    static class Native
    {
        public const int WM_HOTKEY = 0x0312;

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        [StructLayout(LayoutKind.Sequential)]
        public struct INPUT
        {
            public uint type;
            public InputUnion u;
        }

        [StructLayout(LayoutKind.Explicit)]
        public struct InputUnion
        {
            [FieldOffset(0)] public KEYBDINPUT ki;
            [FieldOffset(0)] public MOUSEINPUT mi;
            [FieldOffset(0)] public HARDWAREINPUT hi;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct KEYBDINPUT
        {
            public ushort wVk;
            public ushort wScan;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MOUSEINPUT
        {
            public int dx;
            public int dy;
            public uint mouseData;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct HARDWAREINPUT
        {
            public uint uMsg;
            public ushort wParamL;
            public ushort wParamH;
        }

        public const uint INPUT_KEYBOARD = 1;
        public const uint KEYEVENTF_KEYUP = 0x0002;

        [DllImport("user32.dll", SetLastError = true)]
        public static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        public static void Key(ushort vk, bool up)
        {
            var input = new INPUT
            {
                type = INPUT_KEYBOARD,
                u = new InputUnion
                {
                    ki = new KEYBDINPUT { wVk = vk, dwFlags = up ? KEYEVENTF_KEYUP : 0 }
                }
            };
            SendInput(1, new[] { input }, Marshal.SizeOf(typeof(INPUT)));
        }

        public const ushort VK_CONTROL = 0x11;
        public const ushort VK_A = 0x41;
        public const ushort VK_C = 0x43;
        public const ushort VK_V = 0x56;

        public static void CtrlChord(ushort vk)
        {
            Thread.Sleep(15);
            Key(VK_CONTROL, false);
            Key(vk, false);
            Key(vk, true);
            Key(VK_CONTROL, true);
        }
    }

    sealed class TrayApp : IDisposable
    {
        readonly Config _cfg;
        readonly NotifyIcon _tray;
        readonly HotkeyWindow _hotkey;
        readonly Toast _toast;
        readonly string _baseUrl;
        volatile bool _busy;

        public TrayApp(bool autostart)
        {
            _cfg = Config.Load();
            _baseUrl = "http://127.0.0.1:" + _cfg.port;

            _toast = new Toast();
            _hotkey = new HotkeyWindow();
            _hotkey.HotkeyPressed += delegate { Trigger(); };
            _hotkey.CreateHandleNow();

            _tray = new NotifyIcon
            {
                Icon = MakeIcon(),
                Visible = true,
                Text = "一键润色 · Ctrl+Alt+P"
            };
            _tray.DoubleClick += delegate { Trigger(); };

            var menu = new ContextMenuStrip();
            menu.Items.Add("润色选中文字  (Ctrl+Alt+P)", null, delegate { Trigger(); });
            menu.Items.Add(new ToolStripSeparator());

            var modes = new ToolStripMenuItem("模式");
            AddMode(modes, "chat", "聊天消息");
            AddMode(modes, "mail", "邮件");
            AddMode(modes, "brief", "要点");
            menu.Items.Add(modes);

            var auto = new ToolStripMenuItem("开机自动启动") { Checked = Startup.Enabled() };
            auto.Click += (s, e) =>
            {
                if (Startup.Enabled()) Startup.Disable(); else Startup.Enable();
                auto.Checked = Startup.Enabled();
            };
            menu.Items.Add(auto);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("桥接状态", null, delegate { ShowHealth(); });
            menu.Items.Add("退出", null, delegate { Exit(); });
            _tray.ContextMenuStrip = menu;

            _tray.ShowBalloonTip(2000, "一键润色已启动", "选中文字后按 Ctrl+Alt+P。", ToolTipIcon.None);

            ThreadPool.QueueUserWorkItem(delegate { SetTrayState(Health()); });
        }

        void AddMode(ToolStripMenuItem parent, string key, string label)
        {
            var item = new ToolStripMenuItem(label) { Checked = _cfg.style == key, Tag = key };
            item.Click += delegate { SetStyle(key, parent); };
            parent.DropDownItems.Add(item);
        }

        void SetStyle(string style, ToolStripMenuItem parent)
        {
            _cfg.style = style;
            _cfg.Save();
            foreach (ToolStripItem i in parent.DropDownItems)
                ((ToolStripMenuItem)i).Checked = (string)i.Tag == style;
        }

        void SetTrayState(bool bridgeOk)
        {
            try
            {
                _tray.Text = bridgeOk
                    ? "一键润色 · Ctrl+Alt+P"
                    : "一键润色 · 桥接未连接（先在 DSH 里启用 dsh-polish-bridge）";
            }
            catch { }
        }

        bool Health()
        {
            try
            {
                using (var wc = new WebClient { Encoding = Encoding.UTF8 })
                {
                    string json = wc.DownloadString(_baseUrl + "/health");
                    return json.IndexOf("\"ok\":true", StringComparison.Ordinal) >= 0;
                }
            }
            catch { return false; }
        }

        void ShowHealth()
        {
            try
            {
                using (var wc = new WebClient { Encoding = Encoding.UTF8 })
                {
                    string json = wc.DownloadString(_baseUrl + "/health");
                    MessageBox.Show(json, "桥接状态", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("连不上 " + _baseUrl + "\r\n\r\n" + ex.Message + "\r\n\r\n" +
                    "请确认 DSH 桌面端正在运行，且 dsh-polish-bridge 已启用。",
                    "桥接未连接", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        void Trigger()
        {
            if (_busy) return;
            _busy = true;
            var t = new Thread(Run) { IsBackground = true };
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
        }

        void Run()
        {
            string original = null;
            try
            {
                original = ReadClipboard();
                string text = GrabSelection(original);
                if (string.IsNullOrWhiteSpace(text))
                {
                    _toast.Show("没有取到文字：先选中一段话，再按 Ctrl+Alt+P", 2600);
                    return;
                }

                _toast.Show("润色中…", 0);
                string answer;
                string error;
                if (!Polish(text, out answer, out error))
                {
                    RestoreClipboard(original);
                    _toast.Show("失败：" + error, 4000);
                    return;
                }

                SetClipboard(answer);
                Thread.Sleep(60);
                Native.CtrlChord(Native.VK_V);
                _toast.Show("已替换 · Ctrl+Z 撤销", 1600);
            }
            catch (Exception ex)
            {
                try { RestoreClipboard(original); } catch { }
                _toast.Show("出错：" + ex.Message, 4000);
            }
            finally
            {
                _busy = false;
            }
        }

        string GrabSelection(string original)
        {
            Native.CtrlChord(Native.VK_C);
            Thread.Sleep(180);
            string text = ReadClipboard();

            bool looksSame = text == original || string.IsNullOrWhiteSpace(text);
            if (looksSame && _cfg.selectAllFallback)
            {
                Native.CtrlChord(Native.VK_A);
                Thread.Sleep(80);
                Native.CtrlChord(Native.VK_C);
                Thread.Sleep(180);
                text = ReadClipboard();
            }
            return text == null ? "" : text.Trim();
        }

        bool Polish(string text, out string answer, out string error)
        {
            answer = null;
            error = null;
            try
            {
                var payload = new Dictionary<string, object>
                {
                    { "text", text },
                    { "style", _cfg.style }
                };
                string body = new JavaScriptSerializer().Serialize(payload);

                var request = (HttpWebRequest)WebRequest.Create(_baseUrl + "/polish");
                request.Method = "POST";
                request.ContentType = "application/json; charset=utf-8";
                request.Timeout = 60000;
                request.ReadWriteTimeout = 60000;
                byte[] bytes = Encoding.UTF8.GetBytes(body);
                request.ContentLength = bytes.Length;
                using (var stream = request.GetRequestStream()) stream.Write(bytes, 0, bytes.Length);

                using (var response = (HttpWebResponse)request.GetResponse())
                using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
                {
                    var json = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(reader.ReadToEnd());
                    if (json.ContainsKey("ok") && Convert.ToBoolean(json["ok"]))
                    {
                        answer = Convert.ToString(json["text"]);
                        return !string.IsNullOrWhiteSpace(answer);
                    }
                    error = json.ContainsKey("error") ? Convert.ToString(json["error"]) : "unknown";
                    return false;
                }
            }
            catch (WebException ex)
            {
                try
                {
                    using (var resp = ex.Response)
                    using (var reader = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                    {
                        var json = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(reader.ReadToEnd());
                        error = json.ContainsKey("error") ? Convert.ToString(json["error"]) : ex.Message;
                    }
                }
                catch { error = ex.Message; }
                return false;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        static string ReadClipboard()
        {
            try { return Clipboard.ContainsText() ? Clipboard.GetText() : ""; }
            catch { return ""; }
        }

        static void SetClipboard(string text)
        {
            for (int i = 0; i < 5; i++)
            {
                try { Clipboard.SetText(text); return; }
                catch { Thread.Sleep(40); }
            }
        }

        static void RestoreClipboard(string text)
        {
            if (!string.IsNullOrEmpty(text)) SetClipboard(text);
        }

        static Icon MakeIcon()
        {
            using (var bmp = new Bitmap(32, 32))
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                using (var back = new SolidBrush(Color.FromArgb(28, 28, 30)))
                using (var path = Rounded(new Rectangle(1, 1, 30, 30), 9))
                    g.FillPath(back, path);
                using (var font = new Font("Microsoft YaHei UI", 15f, FontStyle.Bold, GraphicsUnit.Pixel))
                using (var brush = new SolidBrush(Color.White))
                {
                    var size = g.MeasureString("润", font);
                    g.DrawString("润", font, brush, (32 - size.Width) / 2f, (32 - size.Height) / 2f + 1);
                }
                IntPtr handle = bmp.GetHicon();
                return Icon.FromHandle(handle);
            }
        }

        static GraphicsPath Rounded(Rectangle rect, int radius)
        {
            var path = new GraphicsPath();
            int d = radius * 2;
            path.AddArc(rect.X, rect.Y, d, d, 180, 90);
            path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
            path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
            path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        void Exit()
        {
            _tray.Visible = false;
            _hotkey.Dispose();
            Application.Exit();
        }

        public void Dispose()
        {
            try { _tray.Dispose(); } catch { }
        }
    }

    sealed class HotkeyWindow : NativeWindow, IDisposable
    {
        const int HotkeyId = 0xB171;
        public event Action HotkeyPressed;

        public void CreateHandleNow()
        {
            CreateHandle(new CreateParams
            {
                Caption = "PolishKeyHotkey",
                X = -1000,
                Y = -1000,
                Width = 1,
                Height = 1
            });
            if (!Native.RegisterHotKey(Handle, HotkeyId, 0x0002 | 0x0001, 0x50)) // MOD_CONTROL | MOD_ALT | 'P'
            {
                MessageBox.Show("无法注册热键 Ctrl+Alt+P（可能被其他程序占用）。", "一键润色",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_HOTKEY && m.WParam.ToInt32() == HotkeyId)
            {
                var handler = HotkeyPressed;
                if (handler != null) handler();
            }
            base.WndProc(ref m);
        }

        public void Dispose()
        {
            try { Native.UnregisterHotKey(Handle, HotkeyId); } catch { }
            DestroyHandle();
        }
    }

    /// <summary>顶部居中的无焦点提示条。</summary>
    sealed class Toast : Form
    {
        readonly Label _label;
        readonly System.Windows.Forms.Timer _timer;

        public Toast()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            BackColor = Color.FromArgb(20, 20, 22);
            Size = new Size(420, 44);
            Opacity = 0.96;

            _label = new Label
            {
                Dock = DockStyle.Fill,
                ForeColor = Color.FromArgb(240, 240, 240),
                TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font("Microsoft YaHei UI", 10f, FontStyle.Regular, GraphicsUnit.Point),
                AutoEllipsis = true
            };
            Controls.Add(_label);

            _timer = new System.Windows.Forms.Timer { Interval = 1600 };
            _timer.Tick += delegate { _timer.Stop(); Hide(); };
        }

        protected override bool ShowWithoutActivation
        {
            get { return true; }
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x08000000; // WS_EX_NOACTIVATE
                cp.ExStyle |= 0x00000020; // WS_EX_TRANSPARENT
                return cp;
            }
        }

        public void Show(string text, int autoHideMs)
        {
            if (InvokeRequired) { BeginInvoke(new Action<string, int>(Show), text, autoHideMs); return; }
            _label.Text = text;
            var screen = Screen.PrimaryScreen.WorkingArea;
            Location = new Point(screen.X + (screen.Width - Width) / 2, screen.Y + 6);
            _timer.Stop();
            base.Show();
            if (autoHideMs > 0) { _timer.Interval = autoHideMs; _timer.Start(); }
        }
    }

    static class Startup
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string ValueName = "PolishKey";

        public static bool Enabled()
        {
            try
            {
                using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey))
                    return key != null && key.GetValue(ValueName) != null;
            }
            catch { return false; }
        }

        public static void Enable()
        {
            try
            {
                using (var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKey))
                    key.SetValue(ValueName, "\"" + Application.ExecutablePath + "\" --autostart");
            }
            catch { }
        }

        public static void Disable()
        {
            try
            {
                using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey, true))
                    if (key != null) key.DeleteValue(ValueName, false);
            }
            catch { }
        }
    }
}
