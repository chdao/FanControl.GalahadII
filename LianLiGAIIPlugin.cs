using FanControl.Plugins;
using HidSharp;
using System;
using System.Linq;

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
        private const int READ_TIMEOUT_MS = 250; 
        private const int UPDATE_INTERVAL_MS = 0; // Remove internal throttle, attempt read on every FanControl update call


        private HidDevice _device;
        private HidStream _stream;
        private readonly byte[] _buffer = new byte[REPORT_LENGTH];
        private DateTime _lastUpdateTime = DateTime.MinValue; 
        private readonly IPluginLogger _logger;

        public string Id => "LianLiCoolantTemp";
        public string Name => "GA II Coolant Temp";
        public float? Value { get; private set; } = null; 

        public CoolantSensor(IPluginLogger logger)
        {
            _logger = logger;
            InitializeDevice();
        }

        private void InitializeDevice()
        {
            try
            {
                CloseDevice(); 
                _logger?.Log("Attempting to initialize HID device (passive mode)...");
                _device = DeviceList.Local.GetHidDeviceOrNull(vendorID: VENDOR_ID, productID: PRODUCT_ID);
                if (_device != null)
                {
                     _logger?.Log($"Device found: {_device.DevicePath}");
                    if (_device.TryOpen(out _stream))
                    {
                         _stream.ReadTimeout = READ_TIMEOUT_MS;
                         _logger?.Log("Device stream opened successfully for passive reading.");
                    }
                    else
                    {
                        _stream = null;
                        _logger?.Log("Failed to open device stream.");
                    }
                }
                else
                {
                    _logger?.Log($"Device VID={VENDOR_ID:X4} PID={PRODUCT_ID:X4} not found.");
                }
            }
            catch (Exception ex)
            {
                 _logger?.Log($"HID Initialization Exception: {ex.Message}");
                CloseDevice(); 
            }
        }

        public void Update()
        {
            if ((DateTime.UtcNow - _lastUpdateTime).TotalMilliseconds < UPDATE_INTERVAL_MS)
            {
                return; 
            }
            _lastUpdateTime = DateTime.UtcNow;

            if (_stream == null || !_stream.CanRead) // No CanWrite check for passive mode
            {
                 _logger?.Log("Stream is null or cannot be read, attempting reinitialization.");
                InitializeDevice(); 
                if (_stream == null) 
                {
                     _logger?.Log("Reinitialization failed, cannot update sensor.");
                    return;
                }
            }

            try
            {
                // Passive reading: No command is sent.
                // _stream.Write(GET_TEMP_COMMAND);

                int bytesRead = _stream.Read(_buffer);

                if (bytesRead > 0)
                {
                    int bytesToLog = Math.Min(bytesRead, 20);
                    string bufferHex = BitConverter.ToString(_buffer, 0, bytesToLog).Replace("-", " ");
                    float? tempAtIndex11 = (bytesRead > TEMPERATURE_BYTE_INDEX) ? (float?)_buffer[TEMPERATURE_BYTE_INDEX] : null;
                    
                    // Log the raw buffer details less frequently or only if an issue is suspected
                    // For now, we can keep it for debugging if values are still unstable.
                    _logger?.Log($"Read {bytesRead} bytes. Temp@Idx11={tempAtIndex11?.ToString() ?? "N/A"}. Buffer[:{bytesToLog}]: {bufferHex}");
                    
                    if (tempAtIndex11.HasValue)
                    {
                         // Update FanControl's value only if it actually changed from the last valid reading
                         if (Value == null || tempAtIndex11.Value != Value)
                         {
                             // Log only when the value changes or it's the first valid reading
                             _logger?.Log($"New temperature at index {TEMPERATURE_BYTE_INDEX}: {tempAtIndex11.Value} (Previous: {Value?.ToString() ?? "N/A"})");
                             Value = tempAtIndex11.Value;
                         }
                    }
                }
                else
                {
                     // Don't log "No bytes read" if just a timeout, could be normal between L-Connect polls
                     // _logger?.Log("No bytes read from HID stream."); 
                }
            }
            catch (TimeoutException)
            { 
                 // For passive reading, a timeout is expected if L-Connect isn't sending data frequently.
                 // _logger?.Log("HID read timed out (passive mode). Retaining previous value.");
            }
            catch (Exception ex)
            { 
                 _logger?.Log($"HID Update Exception (passive mode): {ex.Message}. Retaining previous value.");
                CloseDevice(); 
            }
        }

        private void CloseDevice()
        {
             bool wasOpen = _stream != null;
             try { _stream?.Dispose(); } catch { /* Ignore */ }
             _stream = null;
             if (wasOpen) { _logger?.Log("Device stream closed."); }
        }

        public void Close()
        {
           CloseDevice();
        }
    }
}
