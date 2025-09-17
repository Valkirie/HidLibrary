// src/HidLibrary/HidDeviceEventMonitor.cs

using System;

namespace HidLibrary
{
    internal class HidDeviceEventMonitor
    {
        public event InsertedEventHandler Inserted;
        public event RemovedEventHandler Removed;

        public delegate void InsertedEventHandler();
        public delegate void RemovedEventHandler();

        private readonly HidDevice _device;
        private bool _connectedCached;
        private string _devicePathNorm; // uppercase for fast compare
        private bool _running;

        public HidDeviceEventMonitor(HidDevice device) => _device = device ?? throw new ArgumentNullException(nameof(device));

        public void Init()
        {
            if (_running) return;
            _running = true;

            _devicePathNorm = Normalize(_device.DevicePath);
            _connectedCached = HidDevices.IsConnected(_device.DevicePath);

            // Start global watcher once; subscribe per device
            HidDeviceWatcherWin32.Instance.Start();
            HidDeviceWatcherWin32.Instance.DeviceArrived += OnArrived;
            HidDeviceWatcherWin32.Instance.DeviceRemoved += OnRemoved;

            // If already connected at init, ensure cached state reflects it
            // (no event fired here to keep behavior identical to previous code)
        }

        public void Stop()
        {
            if (!_running) return;
            _running = false;

            HidDeviceWatcherWin32.Instance.DeviceArrived -= OnArrived;
            HidDeviceWatcherWin32.Instance.DeviceRemoved -= OnRemoved;
        }

        private void OnArrived(string path)
        {
            if (!_running) return;
            if (!IsThisDevice(path)) return;

            if (!_connectedCached)
            {
                _connectedCached = true;
                Inserted?.Invoke();
            }
        }

        private void OnRemoved(string path)
        {
            if (!_running) return;
            if (!IsThisDevice(path)) return;

            if (_connectedCached)
            {
                _connectedCached = false;
                Removed?.Invoke();
            }
        }

        private bool IsThisDevice(string path)
        {
            // Some drivers vary case / slash; compare uppercase
            return Normalize(path) == _devicePathNorm;
        }

        private static string Normalize(string s) =>
            string.IsNullOrEmpty(s) ? string.Empty : s.Replace('\\', '#').ToUpperInvariant(); // minor normalization
    }
}
