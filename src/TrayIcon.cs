using System;
using System.Drawing;
using System.Runtime.InteropServices;

namespace ScrollVolume
{
    /// <summary>
    /// Shell_NotifyIcon を直接扱うトレイアイコン。
    /// 更新に失敗したら（エクスプローラー側でアイコンが消えていたら）自動で登録し直す。
    /// </summary>
    sealed class TrayIcon : IDisposable
    {
        const uint ID = 1;
        readonly IntPtr hwnd;
        readonly uint callbackMsg;
        Icon icon;
        string tip = "";
        bool added;

        public TrayIcon(IntPtr ownerHwnd, uint callbackMessage)
        {
            hwnd = ownerHwnd;
            callbackMsg = callbackMessage;
        }

        NOTIFYICONDATA Data()
        {
            NOTIFYICONDATA d = new NOTIFYICONDATA();
            d.cbSize = Marshal.SizeOf(typeof(NOTIFYICONDATA));
            d.hWnd = hwnd;
            d.uID = ID;
            d.uFlags = Native.NIF_MESSAGE | Native.NIF_ICON | Native.NIF_TIP | Native.NIF_SHOWTIP;
            d.uCallbackMessage = callbackMsg;
            d.hIcon = icon != null ? icon.Handle : IntPtr.Zero;
            d.szTip = tip.Length > 127 ? tip.Substring(0, 127) : tip;
            d.szInfo = "";
            d.szInfoTitle = "";
            return d;
        }

        /// <summary>アイコンとツールチップを更新。渡した Icon の所有権はこのクラスに移る。</summary>
        public void Update(Icon newIcon, string newTip)
        {
            Icon old = icon;
            icon = newIcon;
            tip = newTip ?? "";
            if (!added || !Modify()) Add();
            if (old != null && old != newIcon) old.Dispose();
        }

        bool Modify()
        {
            NOTIFYICONDATA d = Data();
            return Native.Shell_NotifyIcon(Native.NIM_MODIFY, ref d);
        }

        public void Add()
        {
            NOTIFYICONDATA d = Data();
            Native.Shell_NotifyIcon(Native.NIM_DELETE, ref d);
            d = Data();
            added = Native.Shell_NotifyIcon(Native.NIM_ADD, ref d);
            if (added)
            {
                d.uVersion = Native.NOTIFYICON_VERSION_4;
                Native.Shell_NotifyIcon(Native.NIM_SETVERSION, ref d);
            }
            else Log.Write("Shell_NotifyIcon(NIM_ADD) failed");
        }

        /// <summary>定期確認用：消えていたら登録し直す。</summary>
        public void Ensure()
        {
            if (!added || !Modify()) Add();
        }

        public void OnTaskbarCreated()
        {
            added = false;
            Add();
        }

        public Rectangle GetRect()
        {
            NOTIFYICONIDENTIFIER id = new NOTIFYICONIDENTIFIER();
            id.cbSize = Marshal.SizeOf(typeof(NOTIFYICONIDENTIFIER));
            id.hWnd = hwnd;
            id.uID = ID;
            RECT r;
            if (Native.Shell_NotifyIconGetRect(ref id, out r) != 0) return Rectangle.Empty;
            return Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
        }

        public void Dispose()
        {
            NOTIFYICONDATA d = Data();
            Native.Shell_NotifyIcon(Native.NIM_DELETE, ref d);
            added = false;
            if (icon != null) { icon.Dispose(); icon = null; }
        }
    }
}
