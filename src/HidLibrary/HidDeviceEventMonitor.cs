using System;
using System.Threading;
using System.Threading.Tasks;

namespace HidLibrary
{
    internal class HidDeviceEventMonitor
    {
        public event InsertedEventHandler Inserted;
        public event RemovedEventHandler Removed;

        public delegate void InsertedEventHandler();
        public delegate void RemovedEventHandler();

        private readonly HidDevice _device;
        private bool _wasConnected;
        private CancellationTokenSource _cts;

        // allow callers/tests to tune this globally
        public static int PollIntervalMs { get; set; } = 2000; // was 500

        public HidDeviceEventMonitor(HidDevice device)
        {
            _device = device ?? throw new ArgumentNullException(nameof(device));
        }

        public void Init()
        {
            if (_cts != null) return;               // already running
            _cts = new CancellationTokenSource();

            // move off the ThreadPool to avoid worker churn
            Task.Factory.StartNew(MonitorLoop, _cts.Token,
                TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        public void Stop()
        {
            try { _cts?.Cancel(); }
            catch { /* ignore */ }
            finally { _cts = null; }
        }

        private void MonitorLoop()
        {
            var token = _cts.Token;

            // capture initial state cheaply
            _wasConnected = HidDevices.IsConnected(_device.DevicePath);

            while (!token.IsCancellationRequested && _device.MonitorDeviceEvents)
            {
                var isConnected = HidDevices.IsConnected(_device.DevicePath);

                if (isConnected != _wasConnected)
                {
                    if (isConnected) Inserted?.Invoke();
                    else Removed?.Invoke();
                    _wasConnected = isConnected;
                }

                // cancellable sleep
                token.WaitHandle.WaitOne(PollIntervalMs);
            }
        }
    }
}
