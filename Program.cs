using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace PanguSpacing;

internal static class Program
{
    private static Mutex? _mutex;

    [STAThread]
    private static void Main()
    {
        _mutex = new Mutex(true, "PanguSpacing.SingleInstance", out bool isNewInstance);
        if (!isNewInstance)
        {
            MessageBox.Show("盤古之白已經在執行中了。", "盤古之白",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        // 這行由 SDK 依 csproj 裡的 Application* 屬性自動產生
        ApplicationConfiguration.Initialize();
        Application.Run(new TrayContext());
    }
}

// ---------------------------------------------------------------------------
// 系統匣常駐 + 熱鍵處理
// ---------------------------------------------------------------------------
internal sealed class TrayContext : ApplicationContext
{
    private const uint MOD_ALT = 0x0001;
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_NOREPEAT = 0x4000;
    private const uint VK_P = 0x50;

    private readonly NotifyIcon _icon;
    private readonly HotkeyWindow _hotkey;
    private readonly AppSettings _settings = AppSettings.Load();
    private bool _busy;

    public TrayContext()
    {
        _hotkey = new HotkeyWindow();
        _hotkey.HotkeyPressed += () => _ = ConvertSelectionAsync();

        var menu = new ContextMenuStrip();
        menu.Items.Add("轉換選取的文字  (Ctrl+Alt+P)", null, (_, _) => _ = ConvertSelectionAsync());
        menu.Items.Add("只轉換剪貼簿內容", null, (_, _) => ConvertClipboardOnly());
        menu.Items.Add(new ToolStripSeparator());

        var tidy = new ToolStripMenuItem("清理連續空白與全形標點旁的空白")
        {
            CheckOnClick = true,
            Checked = _settings.Pangu.TidySpaces
        };
        tidy.CheckedChanged += (_, _) =>
        {
            _settings.Pangu.TidySpaces = tidy.Checked;
            _settings.Save();
        };
        menu.Items.Add(tidy);

        var joinCjk = new ToolStripMenuItem("移除中文字之間的空白（韓文除外）")
        {
            CheckOnClick = true,
            Checked = _settings.Pangu.RemoveSpaceBetweenCjk
        };
        joinCjk.CheckedChanged += (_, _) =>
        {
            _settings.Pangu.RemoveSpaceBetweenCjk = joinCjk.Checked;
            _settings.Save();
        };
        menu.Items.Add(joinCjk);

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("開啟設定檔…", null, (_, _) => OpenSettingsFile());
        menu.Items.Add("結束", null, (_, _) => ExitThread());

        _icon = new NotifyIcon
        {
            Icon = LoadTrayIcon(),
            Text = "盤古之白 (Ctrl+Alt+P)",
            Visible = true,
            ContextMenuStrip = menu
        };

        if (!_hotkey.Register(MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, VK_P))
            Notify("熱鍵 Ctrl+Alt+P 註冊失敗，可能已被其他程式佔用。");
    }

    /// <summary>用系統預設的 .json 編輯器打開設定檔，讓使用者改 PasteDelayMs 之類的值。</summary>
    private void OpenSettingsFile()
    {
        try
        {
            if (!File.Exists(AppSettings.FilePath)) _settings.Save();
            Process.Start(new ProcessStartInfo(AppSettings.FilePath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Notify("無法開啟設定檔：" + ex.Message);
        }
    }

    /// <summary>
    /// 從內嵌資源載入圖示。傳入 SmallIconSize 讓 Icon 依目前 DPI
    /// 自動挑選 ico 裡最適合的那一張，不會拿大圖硬縮而糊掉。
    /// </summary>
    private static Icon LoadTrayIcon()
    {
        try
        {
            var asm = System.Reflection.Assembly.GetExecutingAssembly();
            using System.IO.Stream? s = asm.GetManifestResourceStream("pangu.ico");
            if (s is not null) return new Icon(s, SystemInformation.SmallIconSize);
        }
        catch { /* 載入失敗就退回系統預設圖示 */ }
        return SystemIcons.Application;
    }

    /// <summary>
    /// 主流程：模擬 Ctrl+C 取得選取文字 → 加空白 → 模擬 Ctrl+V 貼回去。
    /// </summary>
    private async Task ConvertSelectionAsync()
    {
        if (_busy) return;
        _busy = true;
        try
        {
            // 使用者按熱鍵時，Ctrl 與 Alt 實體按鍵還按著。
            // 如果不先送 keyup，接下來送出的會變成 Ctrl+Alt+C。
            Native.ReleaseModifiers();
            await Task.Delay(40);

            string? backup = Clip.TryGetText();
            uint seqBefore = Native.GetClipboardSequenceNumber();

            Native.SendCtrlKey(Native.VK_C);

            if (!await WaitClipboardChangedAsync(seqBefore, 800))
            {
                Notify("沒有偵測到選取的文字。");
                return;
            }

            string? text = Clip.TryGetText();
            if (string.IsNullOrEmpty(text))
            {
                Notify("選取的內容不是文字。");
                return;
            }

            string result = Pangu.Spacing(text, _settings.Pangu);
            if (result == text)
            {
                Notify("這段文字已經有盤古之白了。");
                if (backup is not null) Clip.TrySetText(backup);   // 還原被我們蓋掉的剪貼簿
                return;
            }

            Clip.TrySetText(result);
            await Task.Delay(60);
            Native.SendCtrlKey(Native.VK_V);

            // 等目標程式真的把剪貼簿讀完，再還原原本的內容
            await Task.Delay(_settings.PasteDelayMs);
            if (backup is not null) Clip.TrySetText(backup);
        }
        catch (Exception ex)
        {
            Notify("轉換失敗：" + ex.Message);
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>不動目標視窗，只把剪貼簿裡的文字就地轉換。</summary>
    private void ConvertClipboardOnly()
    {
        string? text = Clip.TryGetText();
        if (string.IsNullOrEmpty(text))
        {
            Notify("剪貼簿裡沒有文字。");
            return;
        }

        string result = Pangu.Spacing(text, _settings.Pangu);
        if (result == text)
        {
            Notify("剪貼簿內容已經有盤古之白了。");
            return;
        }

        Clip.TrySetText(result);
        Notify("剪貼簿已轉換完成，可以直接貼上。");
    }

    private static async Task<bool> WaitClipboardChangedAsync(uint before, int timeoutMs)
    {
        int waited = 0;
        while (waited < timeoutMs)
        {
            await Task.Delay(25);
            waited += 25;
            if (Native.GetClipboardSequenceNumber() != before) return true;
        }
        return false;
    }

    private void Notify(string message) =>
        _icon.ShowBalloonTip(2000, "盤古之白", message, ToolTipIcon.Info);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _icon.Visible = false;
            _icon.Dispose();
            _hotkey.Dispose();
        }
        base.Dispose(disposing);
    }
}

// ---------------------------------------------------------------------------
// 盤古之白轉換規則
// ---------------------------------------------------------------------------
// ---------------------------------------------------------------------------
// 設定檔：%AppData%\PanguSpacing\settings.json
// ---------------------------------------------------------------------------
internal sealed class AppSettings
{
    public static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "PanguSpacing", "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,   // 讓使用者可以在檔案裡寫註解
        AllowTrailingCommas = true
    };

    /// <summary>轉換規則的開關。</summary>
    public PanguOptions Pangu { get; set; } = new();

    /// <summary>
    /// 送出 Ctrl+V 之後、還原剪貼簿之前要等多久（毫秒）。
    /// 貼上後剪貼簿內容不對，代表目標程式讀取較慢，把這個值調大。
    /// </summary>
    public int PasteDelayMs { get; set; } = 250;

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), JsonOptions);
                if (loaded is not null)
                {
                    loaded.Pangu ??= new PanguOptions();
                    loaded.PasteDelayMs = Math.Clamp(loaded.PasteDelayMs, 50, 5000);
                    return loaded;
                }
            }
        }
        catch
        {
            // 檔案壞掉或格式錯誤就當作沒有，用預設值繼續跑，不要讓程式起不來
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch
        {
            // 存不進去（唯讀、權限）不算致命錯誤，設定會在這次執行期間維持有效
        }
    }
}

internal sealed class PanguOptions
{
    /// <summary>連續空白縮成一個、移除全形標點前後的空白。風險低，預設開。</summary>
    public bool TidySpaces { get; set; } = true;

    /// <summary>移除中文字之間的空白。會破壞「台北 東京 大阪」這種清單，預設關。</summary>
    public bool RemoveSpaceBetweenCjk { get; set; } = false;
}

internal static class Pangu
{
    private const string Cjk =
        @"\p{IsCJKUnifiedIdeographs}\p{IsCJKUnifiedIdeographsExtensionA}" +
        @"\p{IsCJKCompatibilityIdeographs}\p{IsHiragana}\p{IsKatakana}" +
        @"\p{IsBopomofo}\p{IsHangulSyllables}";

    // 移除字間空白時不含諺文：韓文靠空白分詞，拿掉會毀掉整句
    private const string CjkNoHangul =
        @"\p{IsCJKUnifiedIdeographs}\p{IsCJKUnifiedIdeographsExtensionA}" +
        @"\p{IsCJKCompatibilityIdeographs}\p{IsHiragana}\p{IsKatakana}\p{IsBopomofo}";

    private const string FullwidthPunct = "，。、；：？！「」『』（）《》〈〉【】…—～·";

    // 中文在前、半形英數或符號在後
    private static readonly Regex CjkThenAnsi =
        new($@"([{Cjk}])([a-zA-Z0-9@#$%^&*\-+\\=|/\[({{<])", RegexOptions.Compiled);

    // 半形英數或符號在前、中文在後
    private static readonly Regex AnsiThenCjk =
        new($@"([a-zA-Z0-9~!@#$%^&*\-+\\=|/.,:;?)\]}}>""'])([{Cjk}])", RegexOptions.Compiled);

    // 兩個非空白字元之間夾了 2 個以上的空白或 Tab → 縮成 1 個
    // 用 lookbehind 排除行首，避免把程式碼縮排或 Markdown 巢狀清單壓平
    private static readonly Regex MultiSpace =
        new(@"(?<=\S)[ \t]{2,}(?=\S)", RegexOptions.Compiled);

    // 全形標點前後的空白
    private static readonly Regex SpaceAroundFullwidth =
        new($@"[ \t]+(?=[{FullwidthPunct}])|(?<=[{FullwidthPunct}])[ \t]+", RegexOptions.Compiled);

    // 中文字之間的空白（不含諺文）
    private static readonly Regex SpaceBetweenCjk =
        new($@"(?<=[{CjkNoHangul}])[ \t]+(?=[{CjkNoHangul}])", RegexOptions.Compiled);

    // 需要整段跳過的東西：網址、Windows 路徑、UNC 路徑
    private static readonly Regex Protected = new(
        @"(https?://\S+|ftp://\S+|[a-zA-Z]:\\[^\s""<>|]*|\\\\[^\s""<>|]+)",
        RegexOptions.Compiled);

    public static string Spacing(string text) => Spacing(text, new PanguOptions());

    public static string Spacing(string text, PanguOptions options)
    {
        if (string.IsNullOrEmpty(text)) return text;

        // 先把網址、路徑抽出來換成佔位符，避免 /中文 被插入空白而毀掉
        var kept = new System.Collections.Generic.List<string>();
        string masked = Protected.Replace(text, m =>
        {
            kept.Add(m.Value);
            return $"\uE000{kept.Count - 1}\uE001";   // 私用區字元，正常文字不會出現
        });

        // 先清理、再補空白。順序反過來的話補上的空白可能又被清掉。
        if (options.RemoveSpaceBetweenCjk)
            masked = SpaceBetweenCjk.Replace(masked, "");

        if (options.TidySpaces)
        {
            masked = SpaceAroundFullwidth.Replace(masked, "");
            masked = MultiSpace.Replace(masked, " ");
        }

        masked = CjkThenAnsi.Replace(masked, "$1 $2");
        masked = AnsiThenCjk.Replace(masked, "$1 $2");

        return Regex.Replace(masked, "\uE000(\\d+)\uE001", m => kept[int.Parse(m.Groups[1].Value)]);
    }
}

// ---------------------------------------------------------------------------
// 剪貼簿存取（剪貼簿隨時可能被別的程式鎖住，一定要重試）
// ---------------------------------------------------------------------------
internal static class Clip
{
    public static string? TryGetText()
    {
        for (int i = 0; i < 10; i++)
        {
            try { return Clipboard.ContainsText() ? Clipboard.GetText(TextDataFormat.UnicodeText) : null; }
            catch (ExternalException) { Thread.Sleep(30); }
        }
        return null;
    }

    public static void TrySetText(string text)
    {
        for (int i = 0; i < 10; i++)
        {
            try
            {
                if (string.IsNullOrEmpty(text)) Clipboard.Clear();
                else Clipboard.SetText(text, TextDataFormat.UnicodeText);
                return;
            }
            catch (ExternalException) { Thread.Sleep(30); }
        }
    }
}

// ---------------------------------------------------------------------------
// 隱藏視窗，負責接收 WM_HOTKEY
// ---------------------------------------------------------------------------
internal sealed class HotkeyWindow : NativeWindow, IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const int HotkeyId = 0xB001;

    public event Action? HotkeyPressed;

    public HotkeyWindow() => CreateHandle(new CreateParams());

    public bool Register(uint modifiers, uint virtualKey) =>
        Native.RegisterHotKey(Handle, HotkeyId, modifiers, virtualKey);

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_HOTKEY && m.WParam.ToInt32() == HotkeyId)
            HotkeyPressed?.Invoke();
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        Native.UnregisterHotKey(Handle, HotkeyId);
        DestroyHandle();
    }
}

// ---------------------------------------------------------------------------
// Win32 P/Invoke
// ---------------------------------------------------------------------------
internal static class Native
{
    public const ushort VK_C = 0x43;
    public const ushort VK_V = 0x56;
    private const ushort VK_CONTROL = 0x11;
    private const ushort VK_MENU = 0x12;    // Alt
    private const ushort VK_SHIFT = 0x10;
    private const ushort VK_LWIN = 0x5B;
    private const ushort VK_RWIN = 0x5C;

    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll")]
    public static extern uint GetClipboardSequenceNumber();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint uCode, uint uMapType);

    /// <summary>把使用者還按著的修飾鍵送出 keyup，否則模擬出來的會是 Ctrl+Alt+C。</summary>
    public static void ReleaseModifiers()
    {
        foreach (ushort vk in new ushort[] { VK_CONTROL, VK_MENU, VK_SHIFT, VK_LWIN, VK_RWIN })
            if ((GetAsyncKeyState(vk) & 0x8000) != 0)
                Send(new[] { Key(vk, true) });
    }

    public static void SendCtrlKey(ushort vk) => Send(new[]
    {
        Key(VK_CONTROL, false),
        Key(vk, false),
        Key(vk, true),
        Key(VK_CONTROL, true)
    });

    private static void Send(INPUT[] inputs) =>
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());

    private static INPUT Key(ushort vk, bool up) => new()
    {
        type = INPUT_KEYBOARD,
        u = new INPUTUNION
        {
            ki = new KEYBDINPUT
            {
                wVk = vk,
                wScan = (ushort)MapVirtualKey(vk, 0),
                dwFlags = up ? KEYEVENTF_KEYUP : 0,
                time = 0,
                dwExtraInfo = IntPtr.Zero
            }
        }
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public INPUTUNION u;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct INPUTUNION
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public HARDWAREINPUT hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx, dy;
        public uint mouseData, dwFlags, time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HARDWAREINPUT
    {
        public uint uMsg;
        public ushort wParamL, wParamH;
    }
}