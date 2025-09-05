# FanControl LianLi Galahad II AIO Plugin

A comprehensive FanControl plugin for Lian-Li Galahad II AIO coolers with automatic PWM synchronization and intelligent temperature monitoring.

## Features

- **Automatic PWM Sync**: Automatically enables PWM synchronization on plugin startup
- **Coolant Temperature Monitoring**: Reads and displays coolant temperature in FanControl
- **Intelligent Query System**: Automatically queries device when temperature data becomes stale (every 2 seconds)
- **Custom Command Support**: Built-in support for sending custom HID commands to the device
- **Robust Error Handling**: Comprehensive logging and error recovery mechanisms
- **Device Auto-Detection**: Automatically finds and connects to Galahad II pumps

## Requirements

- FanControl (latest version)
- Lian-Li Galahad II AIO cooler
- Windows 10 or later
- .NET Framework 4.8

## Installation

1. Download the latest release from the releases page
2. Extract the contents to your FanControl plugins directory (typically `%APPDATA%\FanControl\Plugins`)
3. Restart FanControl

## Usage

1. Open FanControl
2. The "Lian Li GA II LCD Plugin" should be automatically detected and loaded
3. The plugin will automatically:
   - Enable PWM sync with your motherboard
   - Start monitoring coolant temperature
   - Query the device every 2 seconds if temperature data becomes stale
4. Use the "GA II Coolant Temp" sensor in your fan curves

## Troubleshooting

If the plugin is not detected:
1. Ensure your LianLi GA II AIO is properly connected via USB
2. Check that you have the latest version of FanControl
3. Verify that the plugin files are in the correct directory
4. Try restarting FanControl

## Technical Details

- **Communication**: Uses HidSharp library for USB HID communication
- **Commands**: Based on reverse-engineered L-Connect protocol via Wireshark analysis
- **PWM Enable**: `018a00000002013a...` (64-byte command)
- **PWM Disable**: `018a00000002003a...` (64-byte command) 
- **Device Query**: `018100000000...` (64-byte command)
- **Temperature Location**: Byte 11 of device response

## Changelog

### Version 2.1.1
- ✨ **Auto-Padding Commands**: SendCustomCommand now automatically pads short commands with zeros
- 🎯 **Simplified Command Syntax**: Commands can now be ultra-short (e.g., "0181" for query, "018a00000002013a" for PWM enable)
- 📝 **Cleaner Code**: Removed 100+ characters from command strings, much more readable
- 🔧 **Protocol Analysis**: Identified command structure - "018" header + operation type ("1" for query, "a" for set)

### Version 2.1.0
- 🧹 **Major Code Cleanup**: Removed 75+ lines of unused configuration code
- ⚡ **Performance Improvements**: Eliminated unnecessary JSON file operations and config system
- 🎯 **Simplified Architecture**: Streamlined plugin to focus only on PWM sync and temperature monitoring
- 🚀 **Faster Startup**: No more config file loading/saving operations
- 🔧 **Reduced Complexity**: Removed unused SetPumpSpeed methods and configuration properties

### Version 2.0.1
- ✅ **Continuous PWM Sync**: Plugin now sends PWM enable command every 2 seconds continuously
- ✅ **Enhanced Reliability**: Ensures persistent PWM synchronization with motherboard
- ✅ **Fault Tolerance**: Automatically recovers from PWM sync disconnections

### Version 2.0.0
- ✅ **Automatic PWM Sync**: Plugin now automatically enables PWM sync on startup
- ✅ **Intelligent Query System**: Automatically queries device every 2 seconds when temperature data is stale
- ✅ **Custom Command Support**: Added `SendCustomCommand()` method for future extensibility
- ✅ **Improved Error Handling**: Enhanced logging and error recovery throughout the plugin
- ✅ **Rate Limiting**: Prevents device hammering with proper query timing controls
- ✅ **Repository Cleanup**: Removed test files and focused on production-ready code

### Version 1.0.0
- Initial release with basic coolant temperature monitoring

## Building from Source

1. Clone this repository
2. Ensure you have .NET Framework 4.8 SDK installed
3. Run `dotnet build LianLiGAIIPlugin.csproj -c Release`
4. The compiled plugin will be in `bin/Release/`

## License

This project is licensed under the MIT License - see the LICENSE file for details. 
