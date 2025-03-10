﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using System.Diagnostics;
using System.IO;
using System.Drawing.Imaging;
using System.Threading;
using System.Runtime.InteropServices;
using SmartSystemMenu.Extensions;
using SmartSystemMenu.Utils;
using SmartSystemMenu.Hooks;
using SmartSystemMenu.HotKeys;
using SmartSystemMenu.Settings;
using SmartSystemMenu.Native.Structs;
using static SmartSystemMenu.Native.User32;
using static SmartSystemMenu.Native.Constants;

namespace SmartSystemMenu.Forms
{
    partial class MainForm : Form
    {
        private const string SHELL_WINDOW_NAME = "Program Manager";
        private List<Window> _windows;
        private CallWndProcHook _callWndProcHook;
        private GetMsgHook _getMsgHook;
        private ShellHook _shellHook;
        private CBTHook _cbtHook;
        private Hooks.MouseHook _mouseHook;
        private AboutForm _aboutForm;
        private SettingsForm _settingsForm;
        private SmartSystemMenuSettings _settings;
        private WindowSettings _windowSettings;
        private IntPtr _parentHandle;
        private IntPtr _childHandle;

#if WIN32
        private SystemTrayMenu _systemTrayMenu;
        private HotKeyHook _hotKeyHook;
        private HotKeys.MouseHook _hotKeyMouseHook;
        private Process _64BitProcess;
#endif

        public MainForm(SmartSystemMenuSettings settings, WindowSettings windowSettings, IntPtr parentHandle)
        {
            InitializeComponent();

            _settings = settings;
            _windowSettings = windowSettings;
            _parentHandle = parentHandle;
            _childHandle = IntPtr.Zero;
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);

#if WIN32
            if (Environment.Is64BitOperatingSystem)
            {
                var resourceName = "SmartSystemMenu.SmartSystemMenu64.exe";
                var fileName = "SmartSystemMenu64.exe";
                var directoryName = Path.GetDirectoryName(AssemblyUtils.AssemblyLocation);
                var filePath = Path.Combine(directoryName, fileName);
                try
                {
                    if (!File.Exists(filePath))
                    {
                        AssemblyUtils.ExtractFileFromAssembly(resourceName, filePath);
                    }
                    _64BitProcess = Process.Start(filePath, $"--parentHandle {Handle.ToInt64()}");
                }
                catch
                {
                    string message = string.Format("Failed to load {0} process!", fileName);
                    MessageBox.Show(message, AssemblyUtils.AssemblyTitle, MessageBoxButtons.OK, MessageBoxIcon.Error);
                    Close();
                    return;
                }
            }

            if (_settings.ShowSystemTrayIcon)
            {
                _systemTrayMenu = new SystemTrayMenu(_settings);
                _systemTrayMenu.MenuItemAutoStartClick += MenuItemAutoStartClick;
                _systemTrayMenu.MenuItemSettingsClick += MenuItemSettingsClick;
                _systemTrayMenu.MenuItemAboutClick += MenuItemAboutClick;
                _systemTrayMenu.MenuItemExitClick += MenuItemExitClick;
                _systemTrayMenu.MenuItemRestoreClick += MenuItemRestoreClick;
                _systemTrayMenu.Create();
                _systemTrayMenu.CheckMenuItemAutoStart(AutoStarter.IsAutoStartByRegisterEnabled(AssemblyUtils.AssemblyProductName, AssemblyUtils.AssemblyLocation));
            }

            var moduleName = Process.GetCurrentProcess().MainModule.ModuleName;

            _hotKeyHook = new HotKeyHook();
            _hotKeyHook.Hooked += HotKeyHooked;
            if (_settings.MenuItems.Items.Flatten(x => x.Items).Any(x => x.Type == MenuItemType.Item && x.Key3 != VirtualKey.None && x.Show) || _settings.MenuItems.WindowSizeItems.Any(x => x.Key3 != VirtualKey.None))
            {
                _hotKeyHook.Start(moduleName, _settings.MenuItems);
            }

            _hotKeyMouseHook = new HotKeys.MouseHook();
            _hotKeyMouseHook.Hooked += HotKeyMouseHooked;
            if (_settings.Closer.MouseButton != MouseButton.None)
            {
                _hotKeyMouseHook.Start(moduleName, _settings.Closer.Key1, _settings.Closer.Key2, _settings.Closer.MouseButton);
            }

#endif
            if (_parentHandle != IntPtr.Zero)
            {
                var ptrCopyData = SystemUtils.BuildWmCopyDataPointer(SEND_CHILD_HANDLE, Handle.ToInt64().ToString());
                if (ptrCopyData != IntPtr.Zero)
                {
                    SendMessage(_parentHandle, WM_COPYDATA, IntPtr.Zero, ptrCopyData);
                }
            }

            _windows = EnumWindows.EnumAllWindows(_settings, _windowSettings, new string[] { SHELL_WINDOW_NAME }).ToList();

            foreach (var window in _windows)
            {
                var processPath = window.Process?.GetMainModuleFileName() ?? string.Empty;
                var fileName = Path.GetFileName(processPath);
                if (!string.IsNullOrEmpty(fileName) && _settings.ProcessExclusions.Contains(fileName.ToLower()))
                {
                    continue;
                }

                var isAdded = window.Menu.Create();
                if (isAdded)
                {
                    window.Menu.CheckMenuItem(window.ProcessPriority.GetMenuItemId(), true);
                    if (window.AlwaysOnTop)
                    {
                        window.Menu.CheckMenuItem(MenuItemId.SC_TOPMOST, true);
                    }

                    if (window.IsExToolWindow)
                    {
                        window.Menu.CheckMenuItem(MenuItemId.SC_HIDE_FOR_ALT_TAB, true);
                    }

                    if (window.IsDisabledMinimizeButton)
                    {
                        window.Menu.CheckMenuItem(MenuItemId.SC_DISABLE_MINIMIZE_BUTTON, true);
                    }

                    if (window.IsDisabledMaximizeButton)
                    {
                        window.Menu.CheckMenuItem(MenuItemId.SC_DISABLE_MAXIMIZE_BUTTON, true);
                    }

                    if (window.IsDisabledCloseButton)
                    {
                        window.Menu.CheckMenuItem(MenuItemId.SC_DISABLE_CLOSE_BUTTON, true);
                    }

                    var windowClassName = window.GetClassName();
                    var states = _windowSettings.Find(windowClassName, processPath);
                    if (states.Any())
                    {
                        window.ApplyState(states[0], _settings.SaveSelectedItems, _settings.MenuItems.WindowSizeItems);
                        window.Menu.CheckMenuItem(MenuItemId.SC_SAVE_SELECTED_ITEMS, true);
                    }
                }
            }

            _callWndProcHook = new CallWndProcHook(Handle, MenuItemId.SC_DRAG_BY_MOUSE);
            _callWndProcHook.CallWndProc += WindowProc;
            _callWndProcHook.Start();

            _getMsgHook = new GetMsgHook(Handle, MenuItemId.SC_DRAG_BY_MOUSE);
            _getMsgHook.GetMsg += WindowGetMsg;
            _getMsgHook.Start();

            _shellHook = new ShellHook(Handle, MenuItemId.SC_DRAG_BY_MOUSE);
            _shellHook.WindowCreated += WindowCreated;
            _shellHook.WindowDestroyed += WindowDestroyed;
            _shellHook.Start();

            _cbtHook = new CBTHook(Handle, MenuItemId.SC_DRAG_BY_MOUSE);
            _cbtHook.WindowCreated += WindowCreated;
            _cbtHook.WindowDestroyed += WindowDestroyed;
            _cbtHook.MoveSize += WindowMoveSize;
            _cbtHook.MinMax += WindowMinMax;
            _cbtHook.Start();


            _mouseHook = new Hooks.MouseHook(Handle, MenuItemId.SC_DRAG_BY_MOUSE);
            var dragByMouseItemName = MenuItemId.GetName(MenuItemId.SC_DRAG_BY_MOUSE);
            if (_settings.MenuItems.Items.Flatten(x => x.Items).Any(x => x.Type == MenuItemType.Item && x.Name == dragByMouseItemName && x.Show))
            {
                _mouseHook.Start();
            }

            Hide();
        }

        private void HotKeyMouseHooked(object sender, EventArgs<Point> e)
        {
            if (_settings.Closer.Type == WindowCloserType.CloseForegroundWindow)
            {
                var handle = GetForegroundWindow();
                PostMessage(handle, WM_CLOSE, 0, 0);
            }
            else if (_settings.Closer.Type == WindowCloserType.CloseWindowUnderCursor)
            {
                var handle = WindowFromPoint(e.Entity);
                handle = WindowUtils.GetParentWindow(handle);
                PostMessage(handle, WM_CLOSE, 0, 0);
            }
            else if (_settings.Closer.Type == WindowCloserType.KillProcessWithForegroundWindow)
            {
                var handle = GetForegroundWindow();
                var processId = WindowUtils.GetProcessId(handle);
                SystemUtils.TerminateProcess(processId, 0);
            }
            else if (_settings.Closer.Type == WindowCloserType.KillProcessWithWindowUnderCursor)
            {
                var handle = WindowFromPoint(e.Entity);
                handle = WindowUtils.GetParentWindow(handle);
                var processId = WindowUtils.GetProcessId(handle);
                SystemUtils.TerminateProcess(processId, 0);
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            _callWndProcHook?.Stop();
            SendNotifyMessage((IntPtr)HWND_BROADCAST, WM_NULL, 0, 0);
            _getMsgHook?.Stop();
            SendNotifyMessage((IntPtr)HWND_BROADCAST, WM_NULL, 0, 0);
            _shellHook?.Stop();
            SendNotifyMessage((IntPtr)HWND_BROADCAST, WM_NULL, 0, 0);
            _cbtHook?.Stop();
            SendNotifyMessage((IntPtr)HWND_BROADCAST, WM_NULL, 0, 0);

            if (_windows != null)
            {
                foreach (Window window in _windows)
                {
                    window.Dispose();
                }
            }

            WindowUtils.ForceAllMessageLoopsToWakeUp();

#if WIN32
            _systemTrayMenu?.Dispose();
            _hotKeyHook?.Dispose();

            if (Environment.Is64BitOperatingSystem && _64BitProcess != null && !_64BitProcess.HasExited)
            {
                foreach (var handle in _64BitProcess.GetWindowHandles())
                {
                    PostMessage(handle, WM_CLOSE, 0, 0);
                }

                if (!_64BitProcess.WaitForExit(5000))
                {
                    _64BitProcess.Kill();
                }

                try
                {
                    File.Delete(_64BitProcess.StartInfo.FileName);
                }
                catch
                {
                }
            }
#endif
            base.OnClosed(e);
            SendNotifyMessage((IntPtr)HWND_BROADCAST, WM_NULL, 0, 0);
        }

        protected override void WndProc(ref Message m)
        {
            _shellHook?.ProcessWindowMessage(ref m);
            _cbtHook?.ProcessWindowMessage(ref m);
            _callWndProcHook?.ProcessWindowMessage(ref m);
            _getMsgHook?.ProcessWindowMessage(ref m);

            if (m.Msg == WM_COPYDATA)
            {
                var copyData = (CopyDataStruct)Marshal.PtrToStructure(m.LParam, typeof(CopyDataStruct));
                var identifier = copyData.dwData.ToInt64();
                if (identifier == SEND_CHILD_HANDLE)
                {
                    var handleString = Marshal.PtrToStringAnsi(copyData.lpData);
                    _childHandle = long.TryParse(handleString, out var handleValue) ? new IntPtr(handleValue) : IntPtr.Zero;
                }

                if (identifier == MenuItemId.SC_TRANS_DEFAULT || identifier == MenuItemId.SC_CLICK_THROUGH)
                {
                    MenuItemRestoreClick(this, new EventArgs<long>(identifier));
                }
            }

            base.WndProc(ref m);
        }

        private void MenuItemAutoStartClick(object sender, EventArgs e)
        {
            var keyName = AssemblyUtils.AssemblyProductName;
            var assemblyLocation = AssemblyUtils.AssemblyLocation;
            var autoStartEnabled = AutoStarter.IsAutoStartByRegisterEnabled(keyName, assemblyLocation);
            if (autoStartEnabled)
            {
                AutoStarter.UnsetAutoStartByRegister(keyName);
                if (Environment.OSVersion.Version.Major >= 6)
                {
                    AutoStarter.UnsetAutoStartByScheduler(keyName);
                }
            }
            else
            {
                AutoStarter.SetAutoStartByRegister(keyName, assemblyLocation);
                if (Environment.OSVersion.Version.Major >= 6)
                {
                    AutoStarter.SetAutoStartByScheduler(keyName, assemblyLocation);
                }
            }
            ((ToolStripMenuItem)sender).Checked = !autoStartEnabled;
        }

        private void MenuItemAboutClick(object sender, EventArgs e)
        {
            if (_aboutForm == null || _aboutForm.IsDisposed || !_aboutForm.IsHandleCreated)
            {
                _aboutForm = new AboutForm(_settings.Language);
            }
            _aboutForm.Show();
            _aboutForm.Activate();
        }

        private void MenuItemSettingsClick(object sender, EventArgs e)
        {
            if (_settingsForm == null || _settingsForm.IsDisposed || !_settingsForm.IsHandleCreated)
            {
                _settingsForm = new SettingsForm(_settings);
                _settingsForm.OkClick += (object s, EventArgs<SmartSystemMenuSettings> ea) => { _settings = ea.Entity; };
            }

            _settingsForm.Show();
            _settingsForm.Activate();
        }

        private void MenuItemExitClick(object sender, EventArgs e) => Close();

        private void MenuItemRestoreClick(object sender, EventArgs<long> e)
        {
            switch (e.Entity)
            {
                case MenuItemId.SC_TRANS_DEFAULT:
                    {
                        foreach (var window in _windows)
                        {
                            window.Menu.UncheckTransparencyMenu();
                            window.Menu.CheckMenuItem(MenuItemId.SC_TRANS_DEFAULT, true);
                            window.RestoreTransparency();
                        }
                    }
                    break;

                case MenuItemId.SC_CLICK_THROUGH:
                    {
                        foreach (var window in _windows)
                        {
                            if (window.IsClickThrough)
                            {
                                var isChecked = window.Menu.IsMenuItemChecked(MenuItemId.SC_CLICK_THROUGH);
                                if (isChecked)
                                {
                                    window.Menu.CheckMenuItem(MenuItemId.SC_CLICK_THROUGH, !isChecked);
                                    window.ClickThrough(!isChecked);
                                }
                            }
                        }
                    }
                    break;
            }

            if ((e.Entity == MenuItemId.SC_TRANS_DEFAULT || e.Entity == MenuItemId.SC_CLICK_THROUGH) && _childHandle != IntPtr.Zero)
            {
                var ptrCopyData = SystemUtils.BuildWmCopyDataPointer(e.Entity);
                if (ptrCopyData != IntPtr.Zero)
                {
                    SendMessage(_childHandle, WM_COPYDATA, IntPtr.Zero, ptrCopyData);
                }
            }
        }

        private void WindowCreated(object sender, WindowEventArgs e)
        {
            if (e.Handle != IntPtr.Zero && !_windows.Any(w => w.Handle == e.Handle))
            {
                GetWindowThreadProcessId(e.Handle, out int processId);
                var window = new Window(e.Handle, _settings.MenuItems, _settings.Language);
                var filterTitles = new string[] { SHELL_WINDOW_NAME };
                bool isWriteProcess;
#if WIN32
                isWriteProcess = !Environment.Is64BitOperatingSystem || SystemUtils.IsWow64Process(processId);
#else
                isWriteProcess = Environment.Is64BitOperatingSystem && !SystemUtils.IsWow64Process(processId);
#endif

                if (isWriteProcess && !filterTitles.Any(s => window.GetWindowText() == s))
                {
                    var processPath = window.Process?.GetMainModuleFileName() ?? string.Empty;
                    var fileName = Path.GetFileName(processPath);
                    if (!string.IsNullOrEmpty(fileName) && _settings.ProcessExclusions.Contains(fileName.ToLower()))
                    {
                        return;
                    }

                    var isAdded = window.Menu.Create();
                    if (isAdded)
                    {
                        var menuItemId = window.ProcessPriority.GetMenuItemId();
                        window.Menu.CheckMenuItem(menuItemId, true);
                        if (window.AlwaysOnTop)
                        {
                            window.Menu.CheckMenuItem(MenuItemId.SC_TOPMOST, true);
                        }

                        if (window.IsExToolWindow)
                        {
                            window.Menu.CheckMenuItem(MenuItemId.SC_HIDE_FOR_ALT_TAB, true);
                        }

                        if (window.IsDisabledMinimizeButton)
                        {
                            window.Menu.CheckMenuItem(MenuItemId.SC_DISABLE_MINIMIZE_BUTTON, true);
                        }

                        if (window.IsDisabledMaximizeButton)
                        {
                            window.Menu.CheckMenuItem(MenuItemId.SC_DISABLE_MAXIMIZE_BUTTON, true);
                        }

                        if (window.IsDisabledCloseButton)
                        {
                            window.Menu.CheckMenuItem(MenuItemId.SC_DISABLE_CLOSE_BUTTON, true);
                        }

                        _windows.Add(window);

                        var windowClassName = window.GetClassName();
                        var isConsoleClassName = string.Compare(windowClassName, Window.ConsoleClassName, StringComparison.CurrentCulture) == 0;
                        var states = isConsoleClassName ? _windowSettings.Find(windowClassName) : _windowSettings.Find(windowClassName, processPath);
                        if (states.Any())
                        {
                            window.ApplyState(states[0], _settings.SaveSelectedItems, _settings.MenuItems.WindowSizeItems);
                            window.Menu.CheckMenuItem(MenuItemId.SC_SAVE_SELECTED_ITEMS, true);
                        }
                    }
                }
            }
        }

        private void WindowDestroyed(object sender, WindowEventArgs e)
        {
            int windowIndex = _windows.FindIndex(w => w.Handle == e.Handle);
            if (windowIndex != -1 && !_windows[windowIndex].ExistSystemTrayIcon)
            {
                _windows[windowIndex].Dispose();
                _windows.RemoveAt(windowIndex);
            }
        }

        private void WindowMinMax(object sender, SysCommandEventArgs e)
        {
            var window = _windows.FirstOrDefault(w => w.Handle == e.WParam);
            if (window != null)
            {
                if (e.LParam.ToInt64() == SW_MAXIMIZE)
                {
                    window.Menu.UncheckSizeMenu();
                }
                if (e.LParam.ToInt64() == SW_MINIMIZE && window.Menu.IsMenuItemChecked(MenuItemId.SC_MINIMIZE_ALWAYS_TO_SYSTEMTRAY))
                {
                    window.MoveToSystemTray();
                }
            }
        }

        private void WindowMoveSize(object sender, WindowEventArgs e)
        {
            var window = _windows.FirstOrDefault(w => w.Handle == e.Handle);
            window?.SaveDefaultSizePosition();
        }

        private void WindowKeyboardEvent(object sender, BasicHookEventArgs e)
        {
            var wParam = e.WParam.ToInt64();
            if (wParam == VK_DOWN)
            {
                var controlState = GetAsyncKeyState(VK_CONTROL) & 0x8000;
                var shiftState = GetAsyncKeyState(VK_SHIFT) & 0x8000;
                var controlKey = Convert.ToBoolean(controlState);
                var shiftKey = Convert.ToBoolean(shiftState);
                if (controlKey && shiftKey)
                {
                    IntPtr handle = GetForegroundWindow();
                    Window window = _windows.FirstOrDefault(w => w.Handle == handle);
                    window?.MinimizeToSystemTray();
                }
            }
        }

        private void WindowProc(object sender, WndProcEventArgs e)
        {
            WindowGetMsg(sender, new WndProcEventArgs(e.Handle, e.Message, e.WParam, e.LParam));
        }

        private void WindowGetMsg(object sender, WndProcEventArgs e)
        {
            var message = e.Message.ToInt64();
            if (message == WM_SYSCOMMAND)
            {
                //string dbgMessage = string.Format("WM_SYSCOMMAND, Form, Handle = {0}, WParam = {1}", e.Handle, e.WParam);
                //System.Diagnostics.Trace.WriteLine(dbgMessage);
                var window = _windows.FirstOrDefault(w => w.Handle == e.Handle);
                if (window != null)
                {
                    var lowOrder = e.WParam.ToInt64() & 0x0000FFFF;
                    switch (lowOrder)
                    {
                        case MenuItemId.SC_MAXIMIZE:
                            {
                                window.Menu.UncheckSizeMenu();
                            }
                            break;

                        case MenuItemId.SC_MINIMIZE_TO_SYSTEMTRAY:
                            {
                                window.MinimizeToSystemTray();
                            }
                            break;

                        case MenuItemId.SC_MINIMIZE_ALWAYS_TO_SYSTEMTRAY:
                            {
                                bool r = window.Menu.IsMenuItemChecked(MenuItemId.SC_MINIMIZE_ALWAYS_TO_SYSTEMTRAY);
                                window.Menu.CheckMenuItem(MenuItemId.SC_MINIMIZE_ALWAYS_TO_SYSTEMTRAY, !r);
                                window.SetStateMinimizeToTrayAlways(!r);
                            }
                            break;

                        case MenuItemId.SC_SUSPEND_TO_SYSTEMTRAY:
                            {
                                window.MinimizeToSystemTray();
                                Thread.Sleep(100);
                                window.Suspend();
                            }
                            break;

                        case MenuItemId.SC_INFORMATION:
                            {
                                var infoForm = new InfoForm(window.GetWindowInfo(), _settings.Language);
                                infoForm.Show(window.Win32Window);
                            }
                            break;

                        case MenuItemId.SC_SAVE_SCREEN_SHOT:
                            {
                                var bitmap = WindowUtils.PrintWindow(window.Handle);
                                var dialog = new SaveFileDialog
                                {
                                    OverwritePrompt = true,
                                    ValidateNames = true,
                                    Title = _settings.Language.GetValue("save_screenshot_title"),
                                    FileName = _settings.Language.GetValue("save_screenshot_filename"),
                                    DefaultExt = _settings.Language.GetValue("save_screenshot_default_ext"),
                                    RestoreDirectory = false,
                                    Filter = _settings.Language.GetValue("save_screenshot_filter")
                                };
                                if (dialog.ShowDialog(window.Win32Window) == DialogResult.OK)
                                {
                                    var fileExtension = Path.GetExtension(dialog.FileName).ToLower();
                                    var imageFormat = fileExtension == ".bmp" ? ImageFormat.Bmp :
                                        fileExtension == ".gif" ? ImageFormat.Gif :
                                        fileExtension == ".jpeg" ? ImageFormat.Jpeg :
                                        fileExtension == ".png" ? ImageFormat.Png :
                                        fileExtension == ".tiff" ? ImageFormat.Tiff : ImageFormat.Wmf;
                                    bitmap.Save(dialog.FileName, imageFormat);
                                }
                            }
                            break;

                        case MenuItemId.SC_COPY_SCREEN_SHOT:
                            {
                                var bitmap = WindowUtils.PrintWindow(window.Handle);
                                Clipboard.SetImage(bitmap);
                            }
                            break;

                        case MenuItemId.SC_COPY_WINDOW_TEXT:
                            {
                                var text = window.ExtractText();
                                if (!string.IsNullOrEmpty(text))
                                {
                                    Clipboard.SetText(text);
                                }
                            }
                            break;

                        case MenuItemId.SC_COPY_WINDOW_TITLE:
                            {
                                var text = window.GetWindowText();
                                if (!string.IsNullOrEmpty(text))
                                {
                                    Clipboard.SetText(text);
                                }
                            }
                            break;

                        case MenuItemId.SC_COPY_FULL_PROCESS_PATH:
                            {
                                var path = window.Process?.GetMainModuleFileName();
                                if (!string.IsNullOrEmpty(path))
                                {
                                    Clipboard.SetText(path);
                                }
                            }
                            break;

                        case MenuItemId.SC_CLEAR_CLIPBOARD:
                            {
                                Clipboard.Clear();
                            }
                            break;

                        case MenuItemId.SC_DRAG_BY_MOUSE:
                            {
                                var isChecked = window.Menu.IsMenuItemChecked(MenuItemId.SC_DRAG_BY_MOUSE);
                                window.Menu.CheckMenuItem(MenuItemId.SC_DRAG_BY_MOUSE, !isChecked);
                            }
                            break;

                        case MenuItemId.SC_OPEN_FILE_IN_EXPLORER:
                            {
                                try
                                {
                                    SystemUtils.RunAs("explorer.exe", "/select, " + window.Process.GetMainModuleFileName(), true, UserType.Normal);
                                }
                                catch
                                {
                                }
                            }
                            break;

                        case MenuItemId.SC_MINIMIZE_OTHER_WINDOWS:
                        case MenuItemId.SC_CLOSE_OTHER_WINDOWS:
                            {
                                EnumWindows((IntPtr handle, int lParam) =>
                                {
                                    if (handle != IntPtr.Zero && handle != Handle && handle != window.Handle && WindowUtils.IsAltTabWindow(handle))
                                    {
                                        if (lowOrder == MenuItemId.SC_CLOSE_OTHER_WINDOWS)
                                        {
                                            PostMessage(handle, WM_CLOSE, 0, 0);
                                        }
                                        else
                                        {
                                            PostMessage(handle, WM_SYSCOMMAND, MenuItemId.SC_MINIMIZE, 0);
                                        }
                                    }
                                    return true;
                                }, 0);
                            }
                            break;

                        case MenuItemId.SC_TOPMOST:
                            {
                                var isChecked = window.Menu.IsMenuItemChecked(MenuItemId.SC_TOPMOST);
                                window.Menu.CheckMenuItem(MenuItemId.SC_TOPMOST, !isChecked);
                                window.MakeTopMost(!isChecked);
                            }
                            break;

                        case MenuItemId.SC_HIDE_FOR_ALT_TAB:
                            {
                                var isChecked = window.Menu.IsMenuItemChecked(MenuItemId.SC_HIDE_FOR_ALT_TAB);
                                window.Menu.CheckMenuItem(MenuItemId.SC_HIDE_FOR_ALT_TAB, !isChecked);
                                window.HideForAltTab(!isChecked);
                            }
                            break;

                        case MenuItemId.SC_CLICK_THROUGH:
                            {
                                var isChecked = window.Menu.IsMenuItemChecked(MenuItemId.SC_CLICK_THROUGH);
                                window.Menu.CheckMenuItem(MenuItemId.SC_CLICK_THROUGH, !isChecked);
                                window.ClickThrough(!isChecked);
                            }
                            break;

                        case MenuItemId.SC_SEND_TO_BOTTOM:
                            {
                                window.SendToBottom();
                            }
                            break;

                        case MenuItemId.SC_AERO_GLASS:
                            {
                                var isChecked = window.Menu.IsMenuItemChecked(MenuItemId.SC_AERO_GLASS);
                                window.AeroGlass(!isChecked);
                                window.Menu.CheckMenuItem(MenuItemId.SC_AERO_GLASS, !isChecked);
                            }
                            break;

                        case MenuItemId.SC_ROLLUP:
                            {
                                var isChecked = window.Menu.IsMenuItemChecked(MenuItemId.SC_ROLLUP);
                                window.Menu.CheckMenuItem(MenuItemId.SC_ROLLUP, !isChecked);
                                if (!isChecked)
                                {
                                    window.RollUp();
                                    window.Menu.UncheckSizeMenu();
                                }
                                else
                                {
                                    window.UnRollUp();
                                }
                            }
                            break;


                        case MenuItemId.SC_SIZE_DEFAULT:
                            {
                                window.Menu.UncheckSizeMenu();
                                window.Menu.CheckMenuItem(MenuItemId.SC_SIZE_DEFAULT, true);
                                window.ShowNormal();
                                window.RestoreSize();
                                window.Menu.UncheckMenuItems(MenuItemId.SC_ROLLUP);
                            }
                            break;

                        case MenuItemId.SC_SIZE_CUSTOM:
                            {
                                var sizeForm = new SizeForm(window, _settings);
                                var result = sizeForm.ShowDialog(window.Win32Window);
                                if (result == DialogResult.OK)
                                {
                                    window.ShowNormal();

                                    if (_settings.Sizer == WindowSizerType.WindowWithMargins)
                                    {
                                        window.SetSize(sizeForm.WindowWidth, sizeForm.WindowHeight, sizeForm.WindowLeft, sizeForm.WindowTop);
                                    }
                                    else if (_settings.Sizer == WindowSizerType.WindowWithoutMargins)
                                    {
                                        var margins = window.GetSystemMargins();
                                        window.SetSize(sizeForm.WindowWidth + margins.Left + margins.Right, sizeForm.WindowHeight + margins.Top + margins.Bottom, sizeForm.WindowLeft, sizeForm.WindowTop);
                                    }
                                    else
                                    {
                                        window.SetSize(sizeForm.WindowWidth + (window.Size.Width - window.ClientSize.Width), sizeForm.WindowHeight + (window.Size.Height - window.ClientSize.Height), sizeForm.WindowLeft, sizeForm.WindowTop);
                                    }

                                    window.Menu.UncheckSizeMenu();
                                    window.Menu.CheckMenuItem(MenuItemId.SC_SIZE_CUSTOM, true);
                                    window.Menu.UncheckMenuItems(MenuItemId.SC_ROLLUP);
                                }
                            }
                            break;

                        case MenuItemId.SC_TRANS_DEFAULT:
                            {
                                window.Menu.UncheckTransparencyMenu();
                                window.Menu.CheckMenuItem(MenuItemId.SC_TRANS_DEFAULT, true);
                                window.RestoreTransparency();
                            }
                            break;

                        case MenuItemId.SC_TRANS_CUSTOM:
                            {
                                var opacityForm = new TransparencyForm(window, _settings);
                                var result = opacityForm.ShowDialog(window.Win32Window);
                                if (result == DialogResult.OK)
                                {
                                    window.SetTransparency(opacityForm.WindowTransparency);
                                    window.Menu.UncheckTransparencyMenu();
                                    window.Menu.CheckMenuItem(MenuItemId.SC_TRANS_CUSTOM, true);
                                }
                            }
                            break;

                        case MenuItemId.SC_ALIGN_DEFAULT:
                            {
                                window.Menu.UncheckAlignmentMenu();
                                window.Menu.CheckMenuItem(MenuItemId.SC_ALIGN_DEFAULT, true);
                                window.RestorePosition();
                            }
                            break;

                        case MenuItemId.SC_ALIGN_CUSTOM:
                            {
                                var positionForm = new PositionForm(window, _settings.Language);
                                var result = positionForm.ShowDialog(window.Win32Window);

                                if (result == DialogResult.OK)
                                {
                                    window.ShowNormal();
                                    window.SetPosition(positionForm.WindowLeft, positionForm.WindowTop);
                                    window.Menu.UncheckAlignmentMenu();
                                    window.Menu.CheckMenuItem(MenuItemId.SC_ALIGN_CUSTOM, true);
                                }
                            }
                            break;

                        case MenuItemId.SC_DISABLE_MINIMIZE_BUTTON:
                            {
                                var isChecked = window.Menu.IsMenuItemChecked(MenuItemId.SC_DISABLE_MINIMIZE_BUTTON);
                                window.Menu.CheckMenuItem(MenuItemId.SC_DISABLE_MINIMIZE_BUTTON, !isChecked);
                                window.DisableMinimizeButton(!isChecked);
                            }
                            break;

                        case MenuItemId.SC_DISABLE_MAXIMIZE_BUTTON:
                            {
                                var isChecked = window.Menu.IsMenuItemChecked(MenuItemId.SC_DISABLE_MAXIMIZE_BUTTON);
                                window.Menu.CheckMenuItem(MenuItemId.SC_DISABLE_MAXIMIZE_BUTTON, !isChecked);
                                window.DisableMaximizeButton(!isChecked);
                            }
                            break;

                        case MenuItemId.SC_DISABLE_CLOSE_BUTTON:
                            {
                                var isChecked = window.Menu.IsMenuItemChecked(MenuItemId.SC_DISABLE_CLOSE_BUTTON);
                                window.Menu.CheckMenuItem(MenuItemId.SC_DISABLE_CLOSE_BUTTON, !isChecked);
                                window.DisableCloseButton(!isChecked);
                            }
                            break;

                        case MenuItemId.SC_TRANS_100:
                        case MenuItemId.SC_TRANS_90:
                        case MenuItemId.SC_TRANS_80:
                        case MenuItemId.SC_TRANS_70:
                        case MenuItemId.SC_TRANS_60:
                        case MenuItemId.SC_TRANS_50:
                        case MenuItemId.SC_TRANS_40:
                        case MenuItemId.SC_TRANS_30:
                        case MenuItemId.SC_TRANS_20:
                        case MenuItemId.SC_TRANS_10:
                        case MenuItemId.SC_TRANS_00:
                            SetTransparencyMenuItem(window, (int)lowOrder);
                            break;

                        case MenuItemId.SC_PRIORITY_REAL_TIME:
                        case MenuItemId.SC_PRIORITY_HIGH:
                        case MenuItemId.SC_PRIORITY_ABOVE_NORMAL:
                        case MenuItemId.SC_PRIORITY_NORMAL:
                        case MenuItemId.SC_PRIORITY_BELOW_NORMAL:
                        case MenuItemId.SC_PRIORITY_IDLE: 
                            SetPriorityMenuItem(window, (int)lowOrder);
                            break;

                        case MenuItemId.SC_ALIGN_TOP_LEFT:
                        case MenuItemId.SC_ALIGN_TOP_CENTER:
                        case MenuItemId.SC_ALIGN_TOP_RIGHT:
                        case MenuItemId.SC_ALIGN_MIDDLE_LEFT:
                        case MenuItemId.SC_ALIGN_MIDDLE_CENTER:
                        case MenuItemId.SC_ALIGN_MIDDLE_RIGHT:
                        case MenuItemId.SC_ALIGN_BOTTOM_LEFT:
                        case MenuItemId.SC_ALIGN_BOTTOM_CENTER:
                        case MenuItemId.SC_ALIGN_BOTTOM_RIGHT:
                        case MenuItemId.SC_ALIGN_CENTER_HORIZONTALLY:
                        case MenuItemId.SC_ALIGN_CENTER_VERTICALLY:
                            SetAlignmentMenuItem(window, (int)lowOrder);
                            break;
                    }

                    var moveToSubMenuItem = (int)lowOrder - MenuItemId.SC_MOVE_TO;
                    if (window.Menu.MoveToMenuItems.ContainsKey(moveToSubMenuItem))
                    {
                        var monitorHandle = window.Menu.MoveToMenuItems[moveToSubMenuItem];
                        window.MoveToMonitor(monitorHandle);
                    }

                    var windowSizeItem = _settings.MenuItems.WindowSizeItems.FirstOrDefault(x => x.Id == lowOrder);
                    if (windowSizeItem != null)
                    {
                        SetSizeMenuItem(window, (int)lowOrder, windowSizeItem);
                    }

                    for (int i = 0; i < _settings.MenuItems.StartProgramItems.Count; i++)
                    {
                        if (lowOrder - MenuItemId.SC_START_PROGRAM == i)
                        {
                            try
                            {
                                var item = _settings.MenuItems.StartProgramItems[i];
                                var arguments = item.Arguments;
                                var argumentParameters = arguments.GetParams(item.BeginParameter, item.EndParameter);
                                var allParametersInputed = true;
                                var processPath = window.Process?.GetMainModuleFileName() ?? string.Empty;
                                foreach (var parameter in argumentParameters)
                                {
                                    var parameterName = parameter.TrimStart(item.BeginParameter).TrimEnd(item.EndParameter);
                                    if (string.Compare(parameterName, StartProgramMenuItem.PARAMETER_PROCESS_ID, true) == 0)
                                    {
                                        arguments = arguments.Replace(parameter, window.Process?.Id.ToString() ?? string.Empty);
                                        continue;
                                    }

                                    if (string.Compare(parameterName, StartProgramMenuItem.PARAMETER_PROCESS_NAME, true) == 0)
                                    {
                                        arguments = arguments.Replace(parameter, Path.GetFileName(processPath));
                                        continue;
                                    }

                                    if (string.Compare(parameterName, StartProgramMenuItem.PARAMETER_WINDOW_TITLE, true) == 0)
                                    {
                                        arguments = arguments.Replace(parameter, window.GetWindowText());
                                        continue;
                                    }

                                    var parameterForm = new ParameterForm(parameterName, _settings.Language);
                                    var result = parameterForm.ShowDialog(window.Win32Window);

                                    if (result == DialogResult.OK)
                                    {
                                        arguments = arguments.Replace(parameter, parameterForm.ParameterValue);
                                    }
                                    else
                                    {
                                        allParametersInputed = false;
                                        break;
                                    }
                                }
                                
                                if (allParametersInputed)
                                {
                                    SystemUtils.RunAs(item.FileName, arguments, item.ShowWindow, item.RunAs, item.UseWindowWorkingDirectory ? Path.GetDirectoryName(processPath) : null);
                                }
                            }
                            catch (Exception ex)
                            {
                                MessageBox.Show(ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                            }
                            break;
                        }
                    }

                    var isSaveItemChecked = window.Menu.IsMenuItemChecked(MenuItemId.SC_SAVE_SELECTED_ITEMS);
                    if (lowOrder == MenuItemId.SC_SAVE_SELECTED_ITEMS)
                    {
                        isSaveItemChecked = !isSaveItemChecked;
                        window.Menu.CheckMenuItem(MenuItemId.SC_SAVE_SELECTED_ITEMS, isSaveItemChecked);
                        window.RefreshState();
                        var windowClassName = window.GetClassName();
                        var processPath = window.Process?.GetMainModuleFileName() ?? string.Empty;
                        if (!string.IsNullOrEmpty(windowClassName) && !string.IsNullOrEmpty(processPath))
                        {
                            var states = _windowSettings.Find(windowClassName, processPath);
                            if (states.Any())
                            {
                                _windowSettings.Remove(windowClassName, processPath);
                            }

                            if (isSaveItemChecked)
                            {
                                _windowSettings.Items.Add((WindowState)window.State.Clone());
                            }

#if WIN32
                            var fileName = Path.Combine(AssemblyUtils.AssemblyDirectory, "Window.xml");
#else
                            var fileName = Path.Combine(AssemblyUtils.AssemblyDirectory, "Window64.xml");
#endif
                            WindowSettings.Save(fileName, _windowSettings, _settings);
                        }
                    }
                    else if (isSaveItemChecked &&
                             lowOrder != MenuItemId.SC_MOVE &&
                             lowOrder != MenuItemId.SC_MINIMIZE &&
                             lowOrder != MenuItemId.SC_MAXIMIZE && 
                             lowOrder != MenuItemId.SC_RESTORE && 
                             lowOrder != MenuItemId.SC_RESIZE && 
                             lowOrder != MenuItemId.SC_CLOSE)
                    {
                        window.RefreshState();
                        var windowClassName = window.GetClassName();
                        var processPath = window.Process?.GetMainModuleFileName() ?? string.Empty;
                        if (!string.IsNullOrEmpty(windowClassName) && !string.IsNullOrEmpty(processPath))
                        {
                            var states = _windowSettings.Find(windowClassName, processPath);
                            if (states.Any())
                            {
                                _windowSettings.Remove(windowClassName, processPath);
                            }

                            _windowSettings.Items.Add((WindowState)window.State.Clone());
#if WIN32
                            var fileName = Path.Combine(AssemblyUtils.AssemblyDirectory, "Window.xml");
#else
                            var fileName = Path.Combine(AssemblyUtils.AssemblyDirectory, "Window64.xml");
#endif
                            WindowSettings.Save(fileName, _windowSettings, _settings);
                        }
                    }
                }
            }
        }

        private void HotKeyHooked(object sender, HotKeyEventArgs e)
        {
            var handle = GetForegroundWindow();
            var systemMenuHandle = GetSystemMenu(handle, false);

            if (handle != null && handle != IntPtr.Zero && systemMenuHandle != null && systemMenuHandle != IntPtr.Zero)
            {
                var processName = "";

                try
                {
                    GetWindowThreadProcessId(handle, out var processId);
                    var process = SystemUtils.GetProcessByIdSafely(processId);
                    processName = Path.GetFileName(process.GetMainModuleFileName());
                }
                catch
                {
                }

                if (!_settings.ProcessExclusions.Contains(processName.ToLower()))
                {
                    PostMessage(handle, WM_SYSCOMMAND, (uint)e.MenuItemId, 0);
                    e.Succeeded = true;
                }
            }
        }

        private void SetPriorityMenuItem(Window window, int itemId)
        {
            window.Menu.UncheckPriorityMenu();
            window.Menu.CheckMenuItem(itemId, true);
            window.SetPriority(EnumUtils.GetPriority(itemId));
        }

        private void SetAlignmentMenuItem(Window window, int itemId)
        {
            window.Menu.UncheckAlignmentMenu();
            window.Menu.CheckMenuItem(itemId, true);
            window.ShowNormal();
            window.SetAlignment(EnumUtils.GetWindowAlignment(itemId));
        }

        private void SetSizeMenuItem(Window window, int itemId, WindowSizeMenuItem item)
        {
            window.Menu.UncheckSizeMenu();
            window.Menu.CheckMenuItem(itemId, true);
            window.ShowNormal();
            if (_settings.Sizer == WindowSizerType.WindowWithMargins)
            {
                window.SetSize(item.Width, item.Height, item.Left, item.Top);
            }
            else if (_settings.Sizer == WindowSizerType.WindowWithoutMargins)
            {
                var margins = window.GetSystemMargins();
                window.SetSize(item.Width + margins.Left + margins.Right, item.Height + margins.Top + margins.Bottom, item.Left, item.Top);
            }
            else
            {
                window.SetSize(item.Width + (window.Size.Width - window.ClientSize.Width), item.Height + (window.Size.Height - window.ClientSize.Height), item.Left, item.Top);
            }
            window.Menu.UncheckMenuItems(MenuItemId.SC_ROLLUP);
        }

        private void SetTransparencyMenuItem(Window window, int itemId)
        {
            window.Menu.UncheckTransparencyMenu();
            window.Menu.CheckMenuItem(itemId, true);
            window.SetTransparency(EnumUtils.GetTransparency(itemId));
        }
    }
}