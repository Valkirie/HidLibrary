// src/HidLibrary/HidDeviceWatcherWin32.cs
// Windows-only (user32). Provides process-wide HID arrival/removal events without polling.

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace HidLibrary
{
    internal sealed class HidDeviceWatcherWin32 : IDisposable
    {
        // GUID_DEVINTERFACE_HID {4D1E55B2-F16F-11CF-88CB-001111000030}
        private static readonly Guid GUID_DEVINTERFACE_HID = new Guid("4D1E55B2-F16F-11CF-88CB-001111000030");

        public static HidDeviceWatcherWin32 Instance => _instance ??= new HidDeviceWatcherWin32();
        private static HidDeviceWatcherWin32 _instance;

        public event Action<string> DeviceArrived;
        public event Action<string> DeviceRemoved;

        private Thread _thread;
        private IntPtr _hwnd = IntPtr.Zero;
        private IntPtr _devNotify = IntPtr.Zero;
        private volatile bool _running;
        private WndProc _wndProcKeepAlive;

        private HidDeviceWatcherWin32() { }

        public void Start()
        {
            if (_running) return;
            _running = true;
            _thread = new Thread(MessagePump)
            {
                IsBackground = true,
                Name = "HidLibrary.DeviceWatcher"
            };
            _thread.Start();
        }

        public void Stop()
        {
            _running = false;
            if (_hwnd != IntPtr.Zero)
                PostMessage(_hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        }

        public void Dispose() => Stop();

        // ---- Message pump & window ----
        private void MessagePump()
        {
            var hInstance = GetModuleHandle(null);

            // Register a tiny window class
            var wcx = new WNDCLASSEX
            {
                cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
                style = 0,
                lpfnWndProc = _wndProcKeepAlive = WndProcImpl,
                hInstance = hInstance,
                lpszClassName = "HidLibrary.DeviceWatcherWindow"
            };
            ushort atom = RegisterClassEx(ref wcx);
            if (atom == 0) return; // failed to register

            // Create a message-only window (HWND_MESSAGE = -3)
            _hwnd = CreateWindowEx(
                0, wcx.lpszClassName, "HidWatcher",
                0, 0, 0, 0, 0,
                new IntPtr(-3), IntPtr.Zero, hInstance, IntPtr.Zero);

            if (_hwnd == IntPtr.Zero) return;

            RegisterForHidNotifications(_hwnd);

            // Standard message loop
            MSG msg;
            while (_running && GetMessage(out msg, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }

            if (_devNotify != IntPtr.Zero) { UnregisterDeviceNotification(_devNotify); _devNotify = IntPtr.Zero; }
            if (_hwnd != IntPtr.Zero) { DestroyWindow(_hwnd); _hwnd = IntPtr.Zero; }
        }

        private void RegisterForHidNotifications(IntPtr hwnd)
        {
            // DEV_BROADCAST_DEVICEINTERFACE -> request notifications for GUID_DEVINTERFACE_HID
            var filter = new DEV_BROADCAST_DEVICEINTERFACE
            {
                dbcc_size = Marshal.SizeOf<DEV_BROADCAST_DEVICEINTERFACE>(),
                dbcc_devicetype = DBT_DEVTYP_DEVICEINTERFACE,
                dbcc_reserved = 0,
                dbcc_classguid = GUID_DEVINTERFACE_HID
            };
            IntPtr buffer = Marshal.AllocHGlobal(filter.dbcc_size);
            Marshal.StructureToPtr(filter, buffer, false);
            _devNotify = RegisterDeviceNotification(hwnd, buffer, DEVICE_NOTIFY_WINDOW_HANDLE);
            Marshal.FreeHGlobal(buffer);
        }

        // ---- Window proc ----
        private IntPtr WndProcImpl(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            if (msg == WM_DEVICECHANGE)
            {
                int code = wParam.ToInt32();
                if (code == DBT_DEVICEARRIVAL || code == DBT_DEVICEREMOVECOMPLETE)
                {
                    string path = TryGetDevicePath(lParam);
                    if (!string.IsNullOrEmpty(path))
                    {
                        if (code == DBT_DEVICEARRIVAL) DeviceArrived?.Invoke(path);
                        else DeviceRemoved?.Invoke(path);
                    }
                }
            }
            else if (msg == WM_CLOSE)
            {
                PostQuitMessage(0);
            }

            return DefWindowProc(hWnd, msg, wParam, lParam);
        }

        private static string TryGetDevicePath(IntPtr lParam)
        {
            if (lParam == IntPtr.Zero) return null;

            var hdr = Marshal.PtrToStructure<DEV_BROADCAST_HDR>(lParam);
            if (hdr.dbch_devicetype != DBT_DEVTYP_DEVICEINTERFACE) return null;

            // Path is a null-terminated WCHAR* appended to the struct
            int nameOffset = Marshal.OffsetOf<DEV_BROADCAST_DEVICEINTERFACE_W>("dbcc_name").ToInt32();
            IntPtr pName = IntPtr.Add(lParam, nameOffset);
            return Marshal.PtrToStringUni(pName);
        }

        // ---- Win32 interop ----

        private const uint WM_DEVICECHANGE = 0x0219;
        private const int DBT_DEVICEARRIVAL = 0x8000;
        private const int DBT_DEVICEREMOVECOMPLETE = 0x8004;
        private const int DBT_DEVTYP_DEVICEINTERFACE = 5;
        private const int DEVICE_NOTIFY_WINDOW_HANDLE = 0x00000000;
        private const uint WM_CLOSE = 0x0010;

        [StructLayout(LayoutKind.Sequential)]
        private struct DEV_BROADCAST_HDR
        {
            public int dbch_size;
            public int dbch_devicetype;
            public int dbch_reserved;
        }

        // Unicode flavor with name placeholder
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DEV_BROADCAST_DEVICEINTERFACE_W
        {
            public int dbcc_size;
            public int dbcc_devicetype;
            public int dbcc_reserved;
            public Guid dbcc_classguid;
            public char dbcc_name; // first char of variable-length string
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DEV_BROADCAST_DEVICEINTERFACE
        {
            public int dbcc_size;
            public int dbcc_devicetype;
            public int dbcc_reserved;
            public Guid dbcc_classguid;
            public IntPtr dbcc_name; // unused on registration
        }

        private delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WNDCLASSEX
        {
            public uint cbSize;
            public uint style;
            public WndProc lpfnWndProc;
            public int cbClsExtra;
            public int cbWndExtra;
            public IntPtr hInstance;
            public IntPtr hIcon;
            public IntPtr hCursor;
            public IntPtr hbrBackground;
            public string lpszMenuName;
            public string lpszClassName;
            public IntPtr hIconSm;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MSG
        {
            public IntPtr hwnd;
            public uint message;
            public IntPtr wParam;
            public IntPtr lParam;
            public uint time;
            public int pt_x;
            public int pt_y;
        }

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern ushort RegisterClassEx(ref WNDCLASSEX lpwcx);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateWindowEx(
            int exStyle, string lpClassName, string lpWindowName,
            int dwStyle, int x, int y, int nWidth, int nHeight,
            IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr DefWindowProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern sbyte GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

        [DllImport("user32.dll")]
        private static extern bool TranslateMessage(ref MSG lpMsg);

        [DllImport("user32.dll")]
        private static extern IntPtr DispatchMessage(ref MSG lpMsg);

        [DllImport("user32.dll")]
        private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern void PostQuitMessage(int nExitCode);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr RegisterDeviceNotification(IntPtr hRecipient, IntPtr notificationFilter, int flags);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnregisterDeviceNotification(IntPtr handle);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);
    }
}
