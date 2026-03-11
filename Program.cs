// Based on AlwaysOnTopper by Alexey 'Cluster' Avdyukhin (2019)
// Modified by Ramazan Alkan (2026)

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Diagnostics;
using System.Security.Principal;
using System.Linq;
using System.Media;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace AlwaysOnTopper
{
    static class Program
    {
        internal const int MenuId = 31337;
        internal const int MenuIdTrans = 31338;
        const string MenuItemName = "Always on top";
        const string MenuItemNameTrans = "Transparency / Click-through";
        const int OffsetFromBottom = 1;

        #region Win32 API
        [DllImport("user32.dll")] static extern IntPtr GetSystemMenu(IntPtr hWnd, bool bRevert);
        [DllImport("user32.dll", CharSet = CharSet.Auto)] static extern bool GetMenuItemInfo(IntPtr hMenu, uint uItem, bool fByPosition, [In, Out] MENUITEMINFO lpmii);
        [DllImport("user32.dll", CharSet = CharSet.Auto)] static extern bool InsertMenuItem(IntPtr hMenu, uint uItem, bool fByPosition, [In] MENUITEMINFO lpmii);
        [DllImport("user32.dll", CharSet = CharSet.Auto)] static extern bool SetMenuItemInfo(IntPtr hMenu, uint uItem, bool fByPosition, [In] MENUITEMINFO lpmii);
        [DllImport("user32.dll")] static extern bool RemoveMenu(IntPtr hMenu, uint uItem, bool fByPosition);
        [DllImport("user32.dll")] static extern int GetMenuItemCount(IntPtr hMenu);
        [DllImport("user32.dll")] static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr mod, WinEventDelegate lpfn, uint procId, uint threadId, uint flags);
        [DllImport("user32.dll")] static extern bool UnhookWinEvent(IntPtr hWinEventHook);
        delegate void WinEventDelegate(IntPtr hhook, uint ev, IntPtr hwnd, int idObj, int idChild, uint thread, uint time);

        [DllImport("user32.dll")] static extern bool GetWindowInfo(IntPtr hwnd, ref WINDOWINFO pwi);
        [DllImport("user32.dll")] internal static extern bool SetWindowPos(IntPtr hWnd, int after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
        [DllImport("user32.dll")] static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);
        [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
        [DllImport("gdi32.dll")] internal static extern IntPtr CreateRoundRectRgn(int nL, int nT, int nR, int nB, int nW, int nH);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint Msg, UIntPtr wParam, IntPtr lParam, uint fuFlags, uint uTimeout, out UIntPtr lpdwResult);

        [DllImport("user32.dll")]
        static extern bool InvalidateRect(IntPtr hWnd, IntPtr lpRect, bool bErase);

        [StructLayout(LayoutKind.Sequential)]
        struct WINDOWINFO {
            public uint cbSize; public RECT rcWindow; public RECT rcClient; public uint dwStyle; public uint dwExStyle;
            public uint dwStatus; public uint cxBorders; public uint cyBorders; public ushort atom; public ushort creator;
            public WINDOWINFO(bool? f) : this() { cbSize = (uint)Marshal.SizeOf(typeof(WINDOWINFO)); }
        }
        [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        class MENUITEMINFO {
            public int cbSize = Marshal.SizeOf(typeof(MENUITEMINFO));
            public uint fMask; public uint fType; public uint fState; public uint wID;
            public IntPtr hSubMenu, hbmpChecked, hbmpUnchecked, dwItemData;
            public string dwTypeData = null; public uint cch; public IntPtr hbmpItem;
            public MENUITEMINFO(uint mask) { fMask = mask; }
        }

        const uint SMTO_ABORTIFHUNG = 0x0002;
        const uint WM_THEMECHANGED = 0x031A;
        #endregion

        class HookInfo
        {
            public WinEventDelegate Callback;
            public IntPtr TargetHwnd;
            public IntPtr SysMenu;
            public int IndexTop = -1;
            public int IndexTrans = -1;
            public bool MenuInserted = false;
        }

        static Dictionary<IntPtr, HookInfo> hooks = new Dictionary<IntPtr, HookInfo>();
        static readonly object hooksLock = new object();

        static WinEventDelegate globalMenuDelegateRef;
        static IntPtr globalMenuHook = IntPtr.Zero;

        static System.Threading.Timer restartTimer;
        static readonly TimeSpan RestartInterval = TimeSpan.FromMinutes(120);
        static readonly object restartLock = new object();
        static bool restarting = false;

        internal static AppSettings Settings;
        static TransparencyForm transForm;

        [STAThread]
        static void Main()
        {
            bool created;
            using (Mutex m = new Mutex(true, "AlwaysOnTopper_Mutex", out created))
            {
                if (!created) return;

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                Settings = AppSettings.Load();

                OptionsForm options = new OptionsForm();
                options.Hide();

                globalMenuDelegateRef = (h, e, hwnd, idObj, idChild, th, tm) =>
                    UpdateAlwaysOnTopToMenu(GetForegroundWindow());

                globalMenuHook = SetWinEventHook(0x8005, 0x8005, IntPtr.Zero, globalMenuDelegateRef, 0, 0, 0);

                restartTimer = new System.Threading.Timer(RestartTimerCallback, null, RestartInterval, RestartInterval);

                try
                {
                    Application.Run();
                }
                finally
                {
                    try
                    {
                        if (globalMenuHook != IntPtr.Zero)
                        {
                            UnhookWinEvent(globalMenuHook);
                            globalMenuHook = IntPtr.Zero;
                            globalMenuDelegateRef = null;
                        }
                    }
                    catch { }

                    lock (hooksLock)
                    {
                        foreach (var kvp in hooks.ToList())
                        {
                            try { UnhookWinEvent(kvp.Key); } catch { }
                        }
                        hooks.Clear();
                    }

                    if (restartTimer != null)
                    {
                        try
                        {
                            restartTimer.Change(Timeout.Infinite, Timeout.Infinite);
                            restartTimer.Dispose();
                        }
                        catch { }
                        restartTimer = null;
                    }

                    try { transForm?.Dispose(); transForm = null; } catch { }
                    try { options?.Dispose(); options = null; } catch { }
                }
            }
        }

        static void RestartTimerCallback(object state)
        {
            lock (restartLock)
            {
                if (restarting) return;
                restarting = true;
            }

            try
            {
                try
                {
                    if (globalMenuHook != IntPtr.Zero) { UnhookWinEvent(globalMenuHook); globalMenuHook = IntPtr.Zero; globalMenuDelegateRef = null; }
                }
                catch { }

                lock (hooksLock)
                {
                    foreach (var h in hooks.Keys.ToList())
                    {
                        try { UnhookWinEvent(h); } catch { }
                    }
                    hooks.Clear();
                }

                var psi = new ProcessStartInfo
                {
                    FileName = Application.ExecutablePath,
                    UseShellExecute = true,
                    WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory,
                };

                bool isElevated = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
                if (isElevated)
                    psi.Verb = "runas";

                try { Process.Start(psi); }
                catch { }
            }
            finally
            {
                try { Environment.Exit(0); } catch { }
            }
        }

        internal static void UpdateAlwaysOnTopToMenu(IntPtr hwnd, bool remove = false)
        {
            if (hwnd == IntPtr.Zero) return;

            IntPtr sysMenu = GetSystemMenu(hwnd, false);
            if (sysMenu == IntPtr.Zero) return;

            HookInfo existingInfo = null;
            lock (hooksLock)
            {
                existingInfo = hooks.Values.FirstOrDefault(h => h.TargetHwnd == hwnd);
            }

            if (remove)
            {
                if (existingInfo != null && existingInfo.MenuInserted && existingInfo.SysMenu == sysMenu)
                {
                    try
                    {
                        if (existingInfo.IndexTop >= 0) RemoveMenu(sysMenu, (uint)existingInfo.IndexTop, true);
                    }
                    catch { }
                    try
                    {
                        if (existingInfo.IndexTrans >= 0) RemoveMenu(sysMenu, (uint)existingInfo.IndexTrans, true);
                    }
                    catch { }
                }
                else
                {
                    SafeRemoveByScanning(sysMenu);
                }

                lock (hooksLock)
                {
                    var keys = hooks.Where(kvp => kvp.Value.TargetHwnd == hwnd).Select(kvp => kvp.Key).ToList();
                    foreach (var k in keys)
                    {
                        try { UnhookWinEvent(k); } catch { }
                        hooks.Remove(k);
                    }
                }

                return;
            }

            if (existingInfo != null && existingInfo.MenuInserted && existingInfo.SysMenu == sysMenu)
            {
                var info = new WINDOWINFO(true);
                GetWindowInfo(hwnd, ref info);
                bool isTop = (info.dwExStyle & 0x00000008) != 0;

                try
                {
                    var itemTopUpdate = new MENUITEMINFO(0x0001 | 0x0002 | 0x0040 | 0x0100)
                    {
                        wID = MenuId,
                        dwTypeData = MenuItemName,
                        fState = isTop ? 0x00000008u : 0u
                    };
                    SetMenuItemInfo(sysMenu, (uint)existingInfo.IndexTop, true, itemTopUpdate);
                }
                catch
                {
                    SafeUpdateByScanning(sysMenu, hwnd);
                }

                return;
            }

            int count = GetMenuItemCount(sysMenu);
            if (count < 0) return;

            uint pos = (uint)Math.Max(0, count - OffsetFromBottom);

            var itemTop = new MENUITEMINFO(0x0001 | 0x0002 | 0x0040 | 0x0100)
            {
                wID = MenuId,
                dwTypeData = MenuItemName
            };

            var itemTrans = new MENUITEMINFO(0x0001 | 0x0002 | 0x0040 | 0x0100)
            {
                wID = MenuIdTrans,
                dwTypeData = MenuItemNameTrans
            };

            bool insertedTop = false, insertedTrans = false;
            try { insertedTop = InsertMenuItem(sysMenu, pos, true, itemTop); } catch { insertedTop = false; }
            try { insertedTrans = InsertMenuItem(sysMenu, pos + 1, true, itemTrans); } catch { insertedTrans = false; }

            int foundTop = -1, foundTrans = -1;
            int newCount = GetMenuItemCount(sysMenu);
            if (newCount >= 0)
            {
                for (uint i = 0; i < (uint)newCount; i++)
                {
                    var scan = new MENUITEMINFO(0x0002 | 0x0040)
                    {
                        dwTypeData = new string(' ', 256),
                        cch = 256
                    };
                    if (GetMenuItemInfo(sysMenu, i, true, scan))
                    {
                        if (scan.wID == MenuId ||
                            (scan.dwTypeData != null && scan.dwTypeData.Trim().Equals(MenuItemName, StringComparison.OrdinalIgnoreCase)))
                            foundTop = (int)i;

                        if (scan.wID == MenuIdTrans ||
                            (scan.dwTypeData != null && scan.dwTypeData.Trim().Equals(MenuItemNameTrans, StringComparison.OrdinalIgnoreCase)))
                            foundTrans = (int)i;
                    }
                }
            }

            HookInfo hook = null;
            lock (hooksLock)
            {
                WinEventDelegate perHookDel = (h, e, hw, o, c, t, tm) => WinEventInvoked(h, e, hw, o, c, t, tm);
                var h = SetWinEventHook(0x8013, 0x8013, IntPtr.Zero, perHookDel, 0, 0, 0);

                if (h != IntPtr.Zero)
                {
                    hook = new HookInfo
                    {
                        Callback = perHookDel,
                        TargetHwnd = hwnd,
                        SysMenu = sysMenu,
                        IndexTop = foundTop,
                        IndexTrans = foundTrans,
                        MenuInserted = (foundTop >= 0 || foundTrans >= 0)
                    };
                    hooks[h] = hook;
                }
                else
                {
                    hook = new HookInfo
                    {
                        Callback = null,
                        TargetHwnd = hwnd,
                        SysMenu = sysMenu,
                        IndexTop = foundTop,
                        IndexTrans = foundTrans,
                        MenuInserted = (foundTop >= 0 || foundTrans >= 0)
                    };
                    IntPtr pseudoKey = new IntPtr(~(int)hwnd);
                    hooks[pseudoKey] = hook;
                }
            }
        }

        static void SafeUpdateByScanning(IntPtr sysMenu, IntPtr hwnd)
        {
            int count = GetMenuItemCount(sysMenu);
            if (count < 0) return;

            int idxTop = -1, idxTrans = -1;
            for (uint i = 0; i < (uint)count; i++)
            {
                var scan = new MENUITEMINFO(0x0002 | 0x0040)
                {
                    dwTypeData = new string(' ', 256),
                    cch = 256
                };
                if (GetMenuItemInfo(sysMenu, i, true, scan))
                {
                    if (scan.wID == MenuId || (scan.dwTypeData != null && scan.dwTypeData.Trim().Equals(MenuItemName, StringComparison.OrdinalIgnoreCase)))
                        idxTop = (int)i;
                    if (scan.wID == MenuIdTrans || (scan.dwTypeData != null && scan.dwTypeData.Trim().Equals(MenuItemNameTrans, StringComparison.OrdinalIgnoreCase)))
                        idxTrans = (int)i;
                }
            }

            var info = new WINDOWINFO(true);
            GetWindowInfo(hwnd, ref info);
            bool isTop = (info.dwExStyle & 0x00000008) != 0;

            if (idxTop >= 0)
            {
                var itemTop = new MENUITEMINFO(0x0001 | 0x0002 | 0x0040 | 0x0100)
                {
                    wID = MenuId,
                    dwTypeData = MenuItemName,
                    fState = isTop ? 0x00000008u : 0u
                };
                try { SetMenuItemInfo(sysMenu, (uint)idxTop, true, itemTop); } catch { }
            }
        }

        static void SafeRemoveByScanning(IntPtr sysMenu)
        {
            int count = GetMenuItemCount(sysMenu);
            if (count < 0) return;

            List<int> toRemove = new List<int>();
            for (uint i = 0; i < (uint)count; i++)
            {
                var scan = new MENUITEMINFO(0x0002 | 0x0040)
                {
                    dwTypeData = new string(' ', 256),
                    cch = 256
                };
                if (GetMenuItemInfo(sysMenu, i, true, scan))
                {
                    if (scan.wID == MenuId || (scan.dwTypeData != null && scan.dwTypeData.Trim().Equals(MenuItemName, StringComparison.OrdinalIgnoreCase)))
                        toRemove.Add((int)i);
                    if (scan.wID == MenuIdTrans || (scan.dwTypeData != null && scan.dwTypeData.Trim().Equals(MenuItemNameTrans, StringComparison.OrdinalIgnoreCase)))
                        toRemove.Add((int)i);
                }
            }

            foreach (var idx in toRemove.OrderByDescending(i => i))
            {
                try { RemoveMenu(sysMenu, (uint)idx, true); } catch { }
            }
        }

        static void WinEventInvoked(IntPtr h, uint ev, IntPtr hwnd, int idO, int idC, uint th, uint ti)
        {
            if (idC != MenuId && idC != MenuIdTrans) return;

            HookInfo info = null;
            lock (hooksLock)
            {
                if (!hooks.TryGetValue(h, out info))
                {
                    info = hooks.Values.FirstOrDefault(x => x.TargetHwnd == hwnd);
                }
            }

            if (info == null) return;

            if (GetForegroundWindow() != info.TargetHwnd)
                return;

            if (idC == MenuId)
                ToggleTopmostState(info.TargetHwnd, true);
            else
                ShowTransparencyForm(info.TargetHwnd);
        }

        internal static bool ToggleTopmostState(IntPtr hwnd, bool playSound)
        {
            var info = new WINDOWINFO(true); GetWindowInfo(hwnd, ref info);
            bool willBeTop = (info.dwExStyle & 0x00000008) == 0;
            SetWindowPos(hwnd, willBeTop ? -1 : -2, 0, 0, 0, 0, 0x0001 | 0x0002);
            UpdateAlwaysOnTopToMenu(hwnd);
            if (playSound) OptionsForm.PlayToggleSound(willBeTop);
            return willBeTop;
        }

        internal static void ShowTransparencyForm(IntPtr target)
        {
            if (transForm == null || transForm.IsDisposed)
                transForm = new TransparencyForm(target);
            else
                transForm.SetTarget(target);

            var pos = Cursor.Position;
            Screen scr = Screen.FromPoint(pos);

            int x = Math.Max(scr.WorkingArea.Left,
                    Math.Min(pos.X - 10, scr.WorkingArea.Right - transForm.Width));

            int y = Math.Max(scr.WorkingArea.Top,
                    Math.Min(pos.Y - 10, scr.WorkingArea.Bottom - transForm.Height));

            transForm.Location = new Point(x, y);

            if (!transForm.Visible)
                transForm.Show();

            transForm.Activate();
        }

        internal static void SetWindowTrans(IntPtr hwnd, byte alpha, bool clickThrough)
        {
            int ex = GetWindowLong(hwnd, -20);
            int newEx = ex | 0x00080000;
            if (clickThrough) newEx |= 0x00000020; else newEx &= ~0x00000020;
            SetWindowLong(hwnd, -20, newEx);
            SetLayeredWindowAttributes(hwnd, 0, alpha, 2);
        }

        internal static void ApplyClassicPerWindow(IntPtr hwnd)
        {
            try
            {
                int policy = 1;
                DwmSetWindowAttribute(hwnd, DWMWA_NCRENDERING_POLICY, ref policy, Marshal.SizeOf(typeof(int)));
            }
            catch { }

            try
            {
                SetWindowTheme(hwnd, "", "");
            }
            catch { }

            try
            {
                UIntPtr res;
                SendMessageTimeout(new IntPtr(0xffff), WM_THEMECHANGED, UIntPtr.Zero, IntPtr.Zero, SMTO_ABORTIFHUNG, 100, out res);
            }
            catch { }
        }

        #region small extra PInvoke for theme/DWM
        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
        static extern int SetWindowTheme(IntPtr hWnd, string pszSubAppName, string pszSubIdList);

        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);

        const int DWMWA_NCRENDERING_POLICY = 2;
        #endregion
    }

    public class AppSettings
    {
        public string SoundOn = "", SoundOff = "";
        public bool HotkeyTop = true, HotkeyTrans = true;

        public string HotkeyTopKey = "Y";
        public string HotkeyTransKey = "OemQuestion";

        static string Path => System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.ini");
        public static AppSettings Load()
        {
            var s = new AppSettings();
            if (File.Exists(Path)) try
                {
                    foreach (var l in File.ReadAllLines(Path))
                    {
                        var p = l.Split('='); if (p.Length < 2) continue;
                        if (p[0] == "SoundOn") s.SoundOn = p[1];
                        else if (p[0] == "SoundOff") s.SoundOff = p[1];
                        else if (p[0] == "HotkeyTop") s.HotkeyTop = bool.Parse(p[1]);
                        else if (p[0] == "HotkeyTrans") s.HotkeyTrans = bool.Parse(p[1]);
                        else if (p[0] == "HotkeyTopKey") s.HotkeyTopKey = p[1];
                        else if (p[0] == "HotkeyTransKey") s.HotkeyTransKey = p[1];
                    }
                }
                catch { }
            return s;
        }
        public void Save() => File.WriteAllLines(Path, new[]
        {
            $"SoundOn={SoundOn}",
            $"SoundOff={SoundOff}",
            $"HotkeyTop={HotkeyTop}",
            $"HotkeyTrans={HotkeyTrans}",
            $"HotkeyTopKey={HotkeyTopKey}",
            $"HotkeyTransKey={HotkeyTransKey}"
        });
    }

    public class OptionsForm : Form
    {
        NotifyIcon tray;
        CheckBox chkTop, chkTrans;
        TextBox txtOn, txtOff;

        TextBox txtHotTopKey, txtHotTransKey;
        Label lblHotTopKey, lblHotTransKey;

        [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr h, int id, uint mod, uint vk);
        [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr h, int id);

        public OptionsForm()
        {
            this.Text = "AlwaysOnTopper Settings"; this.Size = new Size(420, 260);
            this.FormBorderStyle = FormBorderStyle.FixedDialog; this.MaximizeBox = false;
            this.StartPosition = FormStartPosition.CenterScreen;
            this.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);

            chkTop = new CheckBox { Text = "Enable Win + (Topmost)", Location = new Point(20, 20), AutoSize = true, Checked = Program.Settings.HotkeyTop };
            chkTrans = new CheckBox { Text = "Enable Win + (Transparency)", Location = new Point(20, 45), AutoSize = true, Checked = Program.Settings.HotkeyTrans };

            lblHotTopKey = new Label { Text = "Top key:", Location = new Point(220, 22), AutoSize = true };
            txtHotTopKey = new TextBox { Location = new Point(275, 20), Width = 100, Text = Program.Settings.HotkeyTopKey };

            lblHotTransKey = new Label { Text = "Trans key:", Location = new Point(220, 47), AutoSize = true };
            txtHotTransKey = new TextBox { Location = new Point(275, 45), Width = 100, Text = Program.Settings.HotkeyTransKey };

            txtOn = new TextBox { Location = new Point(120, 80), Width = 150, Text = Program.Settings.SoundOn };
            var btnOn = new Button { Text = "...", Location = new Point(280, 78), Width = 30 };
            txtOff = new TextBox { Location = new Point(120, 110), Width = 150, Text = Program.Settings.SoundOff };
            var btnOff = new Button { Text = "...", Location = new Point(280, 108), Width = 30 };
            var btnSave = new Button { Text = "Save & Hide", Location = new Point(160, 180), Width = 100 };

            btnOn.Click += (s, e) => Browse(txtOn); btnOff.Click += (s, e) => Browse(txtOff);
            btnSave.Click += (s, e) => { Save(); this.Hide(); };

            this.Controls.AddRange(new Control[] {
                chkTop, chkTrans,
                lblHotTopKey, txtHotTopKey, lblHotTransKey, txtHotTransKey,
                new Label { Text = "Pinned Sound:", Location = new Point(20,82), AutoSize = true }, txtOn, btnOn,
                new Label { Text = "Unpinned Sound:", Location = new Point(20,112), AutoSize = true }, txtOff, btnOff,
                btnSave
            });

            tray = new NotifyIcon { Icon = SystemIcons.Application, Visible = true, Text = "AlwaysOnTopper" };
            var menu = new ContextMenuStrip();
            menu.Items.Add("Options", null, (s, e) => this.Show());
            menu.Items.Add("Exit", null, (s, e) => Application.Exit());
            tray.ContextMenuStrip = menu;
            tray.Icon = this.Icon;

            ApplyHotkeys();
        }

        void Browse(TextBox t) { using (var ofd = new OpenFileDialog { Filter = "Wav files|*.wav" }) if (ofd.ShowDialog() == DialogResult.OK) t.Text = ofd.FileName; }
        void Save()
        {
            Program.Settings.SoundOn = txtOn.Text; Program.Settings.SoundOff = txtOff.Text;
            Program.Settings.HotkeyTop = chkTop.Checked; Program.Settings.HotkeyTrans = chkTrans.Checked;
            Program.Settings.Save();
            ApplyHotkeys();
        }

        void ApplyHotkeys()
        {
            try { UnregisterHotKey(Handle, 1); } catch { }
            try { UnregisterHotKey(Handle, 2); } catch { }

            uint ResolveVk(string s)
            {
                if (string.IsNullOrWhiteSpace(s)) return 0;
                s = s.Trim();

                if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                {
                    if (uint.TryParse(s.Substring(2), System.Globalization.NumberStyles.HexNumber, null, out uint hx))
                        return hx;
                }

                if (s.Length == 1)
                {
                    char c = char.ToUpperInvariant(s[0]);
                    return (uint)c;
                }

                try
                {
                    if (Enum.TryParse(typeof(Keys), s, true, out object kobj))
                    {
                        return (uint)(int)kobj;
                    }
                }
                catch { }

                return 0;
            }

            const uint MOD_WIN = 0x0008;
            if (chkTop.Checked)
            {
                uint vk = ResolveVk(txtHotTopKey.Text ?? Program.Settings.HotkeyTopKey);
                if (vk != 0) RegisterHotKey(Handle, 1, MOD_WIN, vk);
            }
            if (chkTrans.Checked)
            {
                uint vk = ResolveVk(txtHotTransKey.Text ?? Program.Settings.HotkeyTransKey);
                if (vk != 0) RegisterHotKey(Handle, 2, MOD_WIN, vk);
            }
        }

        public static void PlayToggleSound(bool on)
        {
            string p = on ? Program.Settings.SoundOn : Program.Settings.SoundOff;

            if (!string.IsNullOrEmpty(p) && File.Exists(p))
            {
                try
                {
                    using (var sp = new SoundPlayer(p))
                        sp.Play();
                }
                catch { }
            }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x0312)
            {
                IntPtr fw = Program.GetForegroundWindow();
                if (m.WParam.ToInt32() == 1) Program.ToggleTopmostState(fw, true);
                else if (m.WParam.ToInt32() == 2) Program.ShowTransparencyForm(fw);
            }
            base.WndProc(ref m);
        }

        protected override void SetVisibleCore(bool v) => base.SetVisibleCore(IsHandleCreated && v);

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
            }
            else
            {
                base.OnFormClosing(e);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try { UnregisterHotKey(Handle, 1); } catch { }
                try { UnregisterHotKey(Handle, 2); } catch { }

                if (tray != null)
                {
                    try { tray.Visible = false; tray.ContextMenuStrip = null; tray.Dispose(); }
                    catch { }
                    tray = null;
                }
            }
            base.Dispose(disposing);
        }
    }

    public class TransparencyForm : Form
    {
        TrackBar bar; CheckBox chk; Label val; IntPtr target;
        Button btnNoTheme; Button btnNoDwm; Button btnRestore;

        public TransparencyForm(IntPtr t) { target = t; Init(); }
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr hObject);

        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
        static extern int SetWindowTheme(IntPtr hWnd, string pszSubAppName, string pszSubIdList);

        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);

        const int DWMWA_NCRENDERING_POLICY = 2;
        const int DWMNCRP_USEWINDOWSTYLE = 0;
        const int DWMNCRP_DISABLED = 1;

        public void SetTarget(IntPtr t) => target = t;

        void Init()
        {
            this.Size = new Size(260, 190);
            this.FormBorderStyle = FormBorderStyle.None;
            this.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            this.BackColor = Color.WhiteSmoke; this.TopMost = true; this.ShowInTaskbar = false;

            bar = new TrackBar { Minimum = 0, Maximum = 255, Value = 255, Location = new Point(10, 40), Width = 240, TickStyle = TickStyle.None };
            val = new Label { Text = "100%", Location = new Point(205, 15), AutoSize = true };
            chk = new CheckBox { Text = "Click-through (Mouse ignore)", Location = new Point(20, 95), AutoSize = true };

            btnNoTheme = new Button { Text = "No Theme", Location = new Point(10, 120), Width = 75 };
            btnNoDwm = new Button { Text = "No DWM", Location = new Point(92, 120), Width = 75 };
            btnRestore = new Button { Text = "Restore", Location = new Point(174, 120), Width = 75 };

            bar.ValueChanged += (s, e) => { Program.SetWindowTrans(target, (byte)bar.Value, chk.Checked); val.Text = $"{(int)((bar.Value / 255.0) * 100)}%"; };
            chk.CheckedChanged += (s, e) => Program.SetWindowTrans(target, (byte)bar.Value, chk.Checked);

            btnNoTheme.Click += (s, e) => { Program.ApplyClassicPerWindow(target); };
            btnNoDwm.Click += (s, e) => {
                try { int policy = DWMNCRP_DISABLED; DwmSetWindowAttribute(target, DWMWA_NCRENDERING_POLICY, ref policy, sizeof(int)); }
                catch { }
            };
            btnRestore.Click += (s, e) => {
                try { SetWindowTheme(target, null, null); } catch { }
                try { int policy = DWMNCRP_USEWINDOWSTYLE; DwmSetWindowAttribute(target, DWMWA_NCRENDERING_POLICY, ref policy, sizeof(int)); } catch { }
                try { UIntPtr res; SendMessageTimeout(new IntPtr(0xffff), 0x031A, UIntPtr.Zero, IntPtr.Zero, 0x0002, 100, out res); } catch { }
            };

            this.Deactivate += (s, e) => this.Hide();

            this.Controls.AddRange(new Control[] {
                new Label { Text = "Transparency:", Location = new Point(15,15), AutoSize = true, Font = new Font(this.Font, FontStyle.Bold) },
                bar, val, chk, btnNoTheme, btnNoDwm, btnRestore
            });

            this.Load += (s, e) =>
            {
                IntPtr rgn = Program.CreateRoundRectRgn(0, 0, Width, Height, 15, 15);
                this.Region = Region.FromHrgn(rgn);
                DeleteObject(rgn);
            };

            this.Paint += (s, e) =>
            {
                using (Pen p = new Pen(Color.LightGray, 2))
                    e.Graphics.DrawRectangle(p, 0, 0, Width - 1, Height - 1);
            };
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                // nothing extra to dispose;
            }
            base.Dispose(disposing);
        }

        [DllImport("user32.dll")]
        static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint Msg, UIntPtr wParam, IntPtr lParam, uint fuFlags, uint uTimeout, out UIntPtr lpdwResult);
    }
}