﻿using System;
using SmartSystemMenu.Native;
using static SmartSystemMenu.Native.User32;

namespace SmartSystemMenu.Hooks
{
    class CBTHook : Hook
    {
        private int _msgIdCbtCreateWnd;
        private int _msgIdCbtDestroyWnd;
        private int _msgIdCbtMinMax;
        private int _msgIdCbtMoveSize;

        public event EventHandler<WindowEventArgs> WindowCreated;
        public event EventHandler<WindowEventArgs> WindowDestroyed;
        public event EventHandler<SysCommandEventArgs> MinMax;
        public event EventHandler<WindowEventArgs> MoveSize;

        public CBTHook(IntPtr handle, int dragByMouseMenuItem) : base(handle, dragByMouseMenuItem)
        {
        }

        protected override void OnStart()
        {
            _msgIdCbtCreateWnd = RegisterWindowMessage("SMART_SYSTEM_MENU_HOOK_HCBT_CREATEWND");
            _msgIdCbtDestroyWnd = RegisterWindowMessage("SMART_SYSTEM_MENU_HOOK_HCBT_DESTROYWND");
            _msgIdCbtMinMax = RegisterWindowMessage("SMART_SYSTEM_MENU_HOOK_HCBT_MINMAX");
            _msgIdCbtMoveSize = RegisterWindowMessage("SMART_SYSTEM_MENU_HOOK_HCBT_MOVESIZE");

            if (Environment.OSVersion.Version.Major >= 6)
            {
                ChangeWindowMessageFilter(_msgIdCbtCreateWnd, Constants.MSGFLT_ADD);
                ChangeWindowMessageFilter(_msgIdCbtDestroyWnd, Constants.MSGFLT_ADD);
                ChangeWindowMessageFilter(_msgIdCbtMinMax, Constants.MSGFLT_ADD);
                ChangeWindowMessageFilter(_msgIdCbtMoveSize, Constants.MSGFLT_ADD);
            }
            NativeHookMethods.InitializeCbtHook(0, _handle, _dragByMouseMenuItem);
        }

        protected override void OnStop()
        {
            NativeHookMethods.UninitializeCbtHook();
        }

        public override void ProcessWindowMessage(ref System.Windows.Forms.Message m)
        {
            if (m.Msg == _msgIdCbtCreateWnd)
            {
                RaiseEvent(WindowCreated, new WindowEventArgs(m.WParam));
            }
            else if (m.Msg == _msgIdCbtDestroyWnd)
            {
                RaiseEvent(WindowDestroyed, new WindowEventArgs(m.WParam));
            }
            else if (m.Msg == _msgIdCbtMinMax)
            {
                RaiseEvent(MinMax, new SysCommandEventArgs(m.WParam, m.LParam));
            }
            else if (m.Msg == _msgIdCbtMoveSize)
            {
                RaiseEvent(MoveSize, new WindowEventArgs(m.WParam));
            }
        }
    }
}