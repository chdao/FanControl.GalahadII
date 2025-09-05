using FanControl.Plugins;
using HidSharp;
using System;
using System.Linq;
using System.Threading; // Added for Thread and CancellationTokenSource

namespace FanControl.LianLiGAII
{
    // Extended plugin implementation with pump control
    public class LianLiGAIIPlugin : IPlugin
    {
        public string Name => "Lian Li GA II LCD Plugin"; // Plugin name

        private CoolantSensor _coolantSensor;
        private PumpController _pumpController;
        private readonly IPluginLogger _logger; // Store logger

        // Constructor accepts IPluginLogger (optional injection)
        public LianLiGAIIPlugin(IPluginLogger logger)
        {
            _logger = logger;
        }

        public void Initialize()
        {
            try 
            {
                _pumpController = new PumpController(_logger);
                _pumpController.Initialize();
                
                // Automatically enable PWM sync on startup
                _pumpController.SendCustomCommand("018a00000002013a");
            }
            catch (Exception ex)
            {
                _logger?.Log($"LianLi Plugin Initialize error: {ex.Message}");
            }
        }

        public void Load(IPluginSensorsContainer container) // Container is provided here
        {
            try
            {
                _coolantSensor = new CoolantSensor(_logger, _pumpController); // Pass logger and pump controller to sensor
                container.TempSensors.Add(_coolantSensor);
            }
            catch (Exception ex)
            {
                _logger?.Log($"LianLi Plugin Load error: {ex.Message}");
            }
        }

        public void Close()
        {
            try
            {
                _coolantSensor?.Close(); // Ensure sensor resources are released
                _pumpController?.Close(); // Ensure pump controller resources are released
            }
            catch { }
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
        private const int QUERY_INTERVAL_MS = 2000; // 2 seconds - send query if no temperature update

        private HidDevice _device;
        private HidStream _stream;
        private readonly byte[] _buffer = new byte[REPORT_LENGTH];
        private readonly IPluginLogger _logger;
        private readonly PumpController _pumpController;
        private DateTime _lastTemperatureUpdate = DateTime.MinValue;
        private DateTime _lastQuerySent = DateTime.MinValue;
        private DateTime _lastPwmSent = DateTime.MinValue;

        private Thread _readThread;
        private CancellationTokenSource _cancellationTokenSource;

        public string Id => "LianLiCoolantTemp";
        public string Name => "GA II Coolant Temp";
        public float? Value { get; private set; } = null;

        public CoolantSensor(IPluginLogger logger, PumpController pumpController)
        {
            _logger = logger;
            _pumpController = pumpController;
            try 
            { 
                InitializeAndStartListener(); 
            }
            catch (Exception ex)
            {
                _logger?.Log($"CoolantSensor initialization error: {ex.Message}");
            }
        }

        private void InitializeAndStartListener()
        {
            if (FindDevice())
            {
                StartReadThread();
            }
        }

        private bool FindDevice()
        {
            try
            {
                _device = DeviceList.Local.GetHidDeviceOrNull(vendorID: VENDOR_ID, productID: PRODUCT_ID);
                return _device != null;
            }
            catch { return false; }
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
                    return true;
                }
                else
                {
                    _stream = null;
                    return false;
                }
            }
            catch
            {
                _stream = null;
                return false;
            }
        }

        private void StartReadThread()
        {
            if (_readThread != null && _readThread.IsAlive) { return; }

            if (!OpenStream()) // Attempt to open stream before starting thread
            {
                _logger?.Log("CoolantSensor: Failed to open stream, cannot start read thread.");
                return;
            }

            try
            {
                _cancellationTokenSource = new CancellationTokenSource();
                _readThread = new Thread(() =>
                {
                    try { DeviceReadLoop(_cancellationTokenSource.Token); }
                    catch { }
                })
                {
                    IsBackground = true,
                    Name = "LianLiCoolantSensorThread"
                };
                _readThread.Start();
            }
            catch
            {
                StopReadThread();
                CloseStream();
            }
        }

        private void DeviceReadLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    if (_stream == null || !_stream.CanRead)
                    {
                        if (!FindDevice() || !OpenStream()) // Re-find device and open stream
                        {
                            Value = null; // Indicate sensor is not available
                            token.WaitHandle.WaitOne(READ_RETRY_DELAY_MS); // Wait before retrying
                            continue;
                        }
                    }

                    var now = DateTime.Now;
                    var timeSinceLastUpdate = now - _lastTemperatureUpdate;
                    var timeSinceLastQuery = now - _lastQuerySent;
                    var timeSinceLastPwm = now - _lastPwmSent;
                    
                    // Send PWM enable command every 2 seconds
                    if (timeSinceLastPwm.TotalMilliseconds > QUERY_INTERVAL_MS)
                    {
                        _pumpController?.SendCustomCommand("018a0000000201");
                        _lastPwmSent = now;
                    }
                    
                    // Check if we need to send a query command (but only once every 2 seconds)
                    bool shouldQuery = (Value == null || timeSinceLastUpdate.TotalMilliseconds > QUERY_INTERVAL_MS) 
                                    && (timeSinceLastQuery.TotalMilliseconds > QUERY_INTERVAL_MS);
                    
                    if (shouldQuery)
                    {
                        // Temperature hasn't changed in 2 seconds or is unknown, and we haven't sent a query recently
                        _pumpController?.SendCustomCommand("0181");
                        _lastQuerySent = now;
                    }

                    int bytesRead = _stream.Read(_buffer);

                    if (bytesRead > 0)
                    {
                        float? tempAtIndex11 = (bytesRead > TEMPERATURE_BYTE_INDEX) ? (float?)_buffer[TEMPERATURE_BYTE_INDEX] : null;

                        if (tempAtIndex11.HasValue)
                        {
                            // Update temperature and tracking variables
                            if (Value != tempAtIndex11.Value)
                            {
                                Value = tempAtIndex11.Value;
                                _lastTemperatureUpdate = DateTime.Now;
                            }
                        }
                    }
                    // No specific action for 0 bytes read if it's a valid timeout, loop will continue
                }
                catch (TimeoutException)
                {
                    // Expected in passive mode if L-Connect doesn't send data frequently.
                }
                catch (Exception)
                {
                    Value = null; // Indicate sensor error
                    CloseStream(); // Close potentially broken stream
                                   // Attempt to re-establish connection after a delay
                    token.WaitHandle.WaitOne(READ_RETRY_DELAY_MS);
                }
            }
        }

        public void Update()
        {
            // The primary role of Update is now to ensure the listener thread is active.
            // FanControl will read the Value property which is updated by the background thread.
            if (_readThread == null || !_readThread.IsAlive)
            {
                // Clean up existing resources before trying to restart
                StopReadThread(); 
                InitializeAndStartListener();
            }
        }
        
        private void CloseStream()
        {
            if (_stream != null)
            {
                try { _stream.Dispose(); } catch { }
                _stream = null;
            }
        }

        private void StopReadThread()
        {
            if (_cancellationTokenSource != null)
            {
                _cancellationTokenSource.Cancel();
            }

            if (_readThread != null && _readThread.IsAlive)
            {
                _readThread.Join(THREAD_JOIN_TIMEOUT_MS);
                // Consider _readThread.Abort(); if absolutely necessary, but it's generally discouraged.
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
            try
            {
                StopReadThread();
                CloseStream(); // Ensure stream is closed
                // _device is managed by HidSharp's DeviceList, no explicit disposal needed unless holding specific handles.
                _device = null; 
            }
            catch { }
        }
    }

    // Pump controller for PWM sync functionality
    public class PumpController
    {
        private const int VENDOR_ID = 0x0416;
        private const int PRODUCT_ID = 0x7395;
        private const int REPORT_LENGTH = 64;
        private const int WRITE_TIMEOUT_MS = 1000;

        private HidDevice _device;
        private readonly IPluginLogger _logger;


        public PumpController(IPluginLogger logger)
        {
            _logger = logger;
        }

        public void Initialize()
        {
            try
            {
                FindDevice();
            }
            catch (Exception ex)
            {
                _logger?.Log($"PumpController Initialize error: {ex.Message}");
            }
        }


        private bool FindDevice()
        {
            try
            {
                _device = DeviceList.Local.GetHidDeviceOrNull(vendorID: VENDOR_ID, productID: PRODUCT_ID);
                if (_device != null)
                {
                    return true;
                }
                else
                {
                    return false;
                }
            }
            catch (Exception ex)
            {
                _logger?.Log($"PumpController FindDevice error: {ex.Message}");
                return false;
            }
        }


        /// <summary>
        /// Sends a custom command to the HID device
        /// Commands shorter than 64 bytes are automatically padded with zeros
        /// </summary>
        /// <param name="hexCommand">Command as hex string (e.g., "018a00000002013a" - will be padded to 64 bytes)</param>
        /// <returns>True if command was sent successfully, false otherwise</returns>
        public bool SendCustomCommand(string hexCommand)
        {
            try
            {
                if (_device == null)
                {
                    return false;
                }

                // Convert hex string to byte array
                var commandBytes = HexStringToBytes(hexCommand);
                if (commandBytes == null || commandBytes.Length == 0)
                {
                    return false;
                }

                // Ensure command is exactly 64 bytes (pad with zeros if shorter, truncate if longer)
                var paddedCommand = new byte[REPORT_LENGTH];
                Array.Copy(commandBytes, 0, paddedCommand, 0, Math.Min(commandBytes.Length, REPORT_LENGTH));

                // Send command using HidSharp
                using (var stream = _device.Open())
                {
                    if (stream.CanWrite)
                    {
                        stream.Write(paddedCommand, 0, paddedCommand.Length);
                        return true;
                    }
                    else
                    {
                        return false;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.Log($"SendCustomCommand error: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Converts a hex string to byte array
        /// </summary>
        /// <param name="hexString">Hex string (e.g., "018a00000002013a")</param>
        /// <returns>Byte array or null if invalid format</returns>
        private byte[] HexStringToBytes(string hexString)
        {
            try
            {
                if (string.IsNullOrEmpty(hexString))
                    return null;

                // Remove any spaces or dashes
                hexString = hexString.Replace(" ", "").Replace("-", "");

                // Ensure even number of characters
                if (hexString.Length % 2 != 0)
                    return null;

                var bytes = new byte[hexString.Length / 2];
                for (int i = 0; i < bytes.Length; i++)
                {
                    bytes[i] = Convert.ToByte(hexString.Substring(i * 2, 2), 16);
                }

                return bytes;
            }
            catch
            {
                return null;
            }
        }

        public void Close()
        {
            try
            {
                // Device is managed by HidSharp, no explicit disposal needed
                _device = null;
            }
            catch { }
        }
    }
}
