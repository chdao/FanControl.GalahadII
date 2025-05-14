using FanControl.Plugins;
using HidSharp;
using System;
using System.Linq;
using System.Threading; // Added for Thread and CancellationTokenSource

namespace FanControl.LianLiGAII
{
    // Reverted to separate IPlugin implementation
    public class LianLiGAIIPlugin : IPlugin
    {
        public string Name => "Lian Li GA II LCD Plugin"; // Plugin name

        private CoolantSensor _coolantSensor;
        private readonly IPluginLogger _logger; // Store logger

        // Constructor accepts IPluginLogger (optional injection)
        public LianLiGAIIPlugin(IPluginLogger logger)
        {
            _logger = logger;
            _logger?.Log($"{Name} plugin constructor called.");
        }

        public void Initialize()
        {
            _logger?.Log($"{Name} Initialize called.");
        }

        public void Load(IPluginSensorsContainer container) // Container is provided here
        {
            _logger?.Log($"{Name} Load called.");
            _coolantSensor = new CoolantSensor(_logger); // Pass logger to sensor
            container.TempSensors.Add(_coolantSensor);
        }

        public void Close()
        {
            _logger?.Log($"{Name} Close called.");
            _coolantSensor?.Close(); // Ensure sensor resources are released
        }
    }

    // Re-added separate CoolantSensor class
    public class CoolantSensor : IPluginSensor
    {
        private const int VENDOR_ID = 0x0416;
        private const int PRODUCT_ID = 0x7395;
        private const int REPORT_LENGTH = 64;
        private const int TEMPERATURE_BYTE_INDEX = 11;
        private const int READ_TIMEOUT_MS = 500; // Increased timeout slightly for dedicated thread
        private const int THREAD_JOIN_TIMEOUT_MS = 1000;
        private const int READ_RETRY_DELAY_MS = 2000; // Delay before retrying after certain errors

        private HidDevice _device;
        private HidStream _stream;
        private readonly byte[] _buffer = new byte[REPORT_LENGTH];
        private readonly IPluginLogger _logger;

        private Thread _readThread;
        private CancellationTokenSource _cancellationTokenSource;

        public string Id => "LianLiCoolantTemp";
        public string Name => "GA II Coolant Temp";
        public float? Value { get; private set; } = null;

        public CoolantSensor(IPluginLogger logger)
        {
            _logger = logger;
            InitializeAndStartListener();
        }

        private void InitializeAndStartListener()
        {
            _logger?.Log("CoolantSensor: Initializing and starting listener thread...");
            if (FindDevice())
            {
                StartReadThread();
            }
            else
            {
                _logger?.Log("CoolantSensor: Device not found during initial setup. Will retry on Update.");
            }
        }

        private bool FindDevice()
        {
            try
            {
                _device = DeviceList.Local.GetHidDeviceOrNull(vendorID: VENDOR_ID, productID: PRODUCT_ID);
                if (_device != null)
                {
                    _logger?.Log($"CoolantSensor: Device found: {_device.DevicePath}");
                    return true;
                }
                else
                {
                    _logger?.Log($"CoolantSensor: Device VID={VENDOR_ID:X4} PID={PRODUCT_ID:X4} not found.");
                    return false;
                }
            }
            catch (Exception ex)
            {
                _logger?.Log($"CoolantSensor: Exception while finding device: {ex.Message}");
                return false;
            }
        }

        private bool OpenStream()
        {
            if (_device == null)
            {
                _logger?.Log("CoolantSensor: Cannot open stream, device is null.");
                return false;
            }

            try
            {
                // Ensure any existing stream is closed before opening a new one
                CloseStream();

                if (_device.TryOpen(out _stream))
                {
                    _stream.ReadTimeout = READ_TIMEOUT_MS;
                    _logger?.Log("CoolantSensor: Device stream opened successfully.");
                    return true;
                }
                else
                {
                    _stream = null;
                    _logger?.Log("CoolantSensor: Failed to open device stream.");
                    return false;
                }
            }
            catch (Exception ex)
            {
                _logger?.Log($"CoolantSensor: Exception while opening stream: {ex.Message}");
                _stream = null;
                return false;
            }
        }

        private void StartReadThread()
        {
            if (_readThread != null && _readThread.IsAlive)
            {
                _logger?.Log("CoolantSensor: Read thread already running.");
                return;
            }

            if (!OpenStream()) // Attempt to open stream before starting thread
            {
                _logger?.Log("CoolantSensor: Failed to open stream, cannot start read thread.");
                return;
            }

            _cancellationTokenSource = new CancellationTokenSource();
            _readThread = new Thread(() => DeviceReadLoop(_cancellationTokenSource.Token))
            {
                IsBackground = true,
                Name = "LianLiCoolantSensorThread"
            };
            _readThread.Start();
            _logger?.Log("CoolantSensor: Read thread started.");
        }

        private void DeviceReadLoop(CancellationToken token)
        {
            _logger?.Log("CoolantSensor: DeviceReadLoop started.");
            while (!token.IsCancellationRequested)
            {
                try
                {
                    if (_stream == null || !_stream.CanRead)
                    {
                        _logger?.Log("CoolantSensor: Stream is not valid in read loop. Attempting to reopen...");
                        if (!FindDevice() || !OpenStream()) // Re-find device and open stream
                        {
                            _logger?.Log($"CoolantSensor: Failed to re-open stream. Retrying in {READ_RETRY_DELAY_MS}ms.");
                            Value = null; // Indicate sensor is not available
                            token.WaitHandle.WaitOne(READ_RETRY_DELAY_MS); // Wait before retrying
                            continue;
                        }
                    }

                    int bytesRead = _stream.Read(_buffer);

                    if (bytesRead > 0)
                    {
                        int bytesToLog = Math.Min(bytesRead, 20);
                        string bufferHex = BitConverter.ToString(_buffer, 0, bytesToLog).Replace("-", " ");
                        float? tempAtIndex11 = (bytesRead > TEMPERATURE_BYTE_INDEX) ? (float?)_buffer[TEMPERATURE_BYTE_INDEX] : null;

                        // _logger?.Log($"CoolantSensor: Read {bytesRead} bytes. Temp@Idx11={tempAtIndex11?.ToString() ?? "N/A"}. Buffer[:{bytesToLog}]: {bufferHex}");
                        
                        if (tempAtIndex11.HasValue)
                        {
                            if (Value == null || tempAtIndex11.Value != Value)
                            {
                                _logger?.Log($"CoolantSensor: New temperature: {tempAtIndex11.Value} (Previous: {Value?.ToString() ?? "N/A"})");
                                Value = tempAtIndex11.Value;
                            }
                        }
                    }
                    // No specific action for 0 bytes read if it's a valid timeout, loop will continue
                }
                catch (TimeoutException)
                {
                    // This is expected in passive mode if L-Connect doesn't send data frequently.
                    // _logger?.Log("CoolantSensor: HID read timed out (passive mode).");
                    // Value remains unchanged, will try again.
                }
                catch (Exception ex)
                {
                    _logger?.Log($"CoolantSensor: Exception in DeviceReadLoop: {ex.Message}.");
                    Value = null; // Indicate sensor error
                    CloseStream(); // Close potentially broken stream
                                   // Attempt to re-establish connection after a delay
                    _logger?.Log($"CoolantSensor: Retrying connection in {READ_RETRY_DELAY_MS}ms due to error.");
                    token.WaitHandle.WaitOne(READ_RETRY_DELAY_MS);
                }
            }
            _logger?.Log("CoolantSensor: DeviceReadLoop exited.");
        }

        public void Update()
        {
            // The primary role of Update is now to ensure the listener thread is active.
            // FanControl will read the Value property which is updated by the background thread.
            if (_readThread == null || !_readThread.IsAlive)
            {
                _logger?.Log("CoolantSensor: Read thread is not alive. Attempting to restart.");
                // Clean up existing resources before trying to restart
                StopReadThread(); 
                InitializeAndStartListener();
            }
        }
        
        private void CloseStream()
        {
            if (_stream != null)
            {
                try { _stream.Dispose(); } catch (Exception ex) { _logger?.Log($"CoolantSensor: Exception disposing stream: {ex.Message}"); }
                _stream = null;
                _logger?.Log("CoolantSensor: Device stream closed.");
            }
        }

        private void StopReadThread()
        {
            if (_cancellationTokenSource != null)
            {
                _logger?.Log("CoolantSensor: Requesting read thread to stop.");
                _cancellationTokenSource.Cancel();
            }

            if (_readThread != null && _readThread.IsAlive)
            {
                _logger?.Log("CoolantSensor: Waiting for read thread to exit...");
                if (_readThread.Join(THREAD_JOIN_TIMEOUT_MS))
                {
                    _logger?.Log("CoolantSensor: Read thread exited gracefully.");
                }
                else
                {
                    _logger?.Log("CoolantSensor: Read thread did not exit in time.");
                    // Consider _readThread.Abort(); if absolutely necessary, but it's generally discouraged.
                }
            }
            _readThread = null;
            if (_cancellationTokenSource != null)
            {
                _cancellationTokenSource.Dispose();
                _cancellationTokenSource = null;
            }
        }

        public void Close()
        {
            _logger?.Log("CoolantSensor: Close called.");
            StopReadThread();
            CloseStream(); // Ensure stream is closed
            // _device is managed by HidSharp's DeviceList, no explicit disposal needed unless holding specific handles.
             _device = null; 
        }
    }
}
