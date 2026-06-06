# Controller-over-LAN

A C# application that allows you to use an Xbox/XInput gamepad (such as the **ASUS ROG Ally**) connected to one Windows PC to control games and applications on another Windows PC over a local area network (LAN), with full support for vibration/force feedback.

## Overview

**Controller-over-LAN** is a client-server solution that enables gamepad forwarding across networked Windows computers. This is particularly useful for:

- Playing games on a remote PC using a controller from your primary device
- Testing games on different machines without physically moving your controller
- Using the ASUS ROG Ally as a controller for another Windows PC
- Remote gaming scenarios where the controller and the game run on different computers

The application uses UDP for low-latency communication, HMAC-SHA256 for security, and ViGEm (Virtual Gamepad Emulation) to create virtual Xbox 360 controllers on the server side.

## Project Structure

```
├── Client/              # Reads physical controller input and sends to server
│   ├── Program.cs       # Main client application with GUI server discovery
│   └── Client.csproj
├── Server/              # Receives input and creates virtual controller
│   ├── Program.cs       # Main server application with ViGEm integration
│   └── Server.csproj
├── inspect/             # Utility for inspecting ViGEm library (development)
│   ├── Program.cs
│   └── inspect.csproj
├── publish/             # Pre-built binaries (optional)
└── inspect_vigem.ps1    # PowerShell script for ViGEm inspection
```

## System Requirements

### Server PC (target machine)
- **OS**: Windows 7 SP1 or later (Windows 10/11 recommended)
- **.NET Runtime**: .NET 7 Runtime or .NET 7 SDK
- **ViGEm Bus Driver**: Required for virtual controller emulation
  - [Download ViGEmBusSetup from Nefarius/ViGEmBus](https://github.com/nefarius/ViGEmBus/releases)
  - Must be installed and driver should be running (automatic after installation)
- **Network**: Connected to the same LAN as the client PC

### Client PC (controller source)
- **OS**: Windows 7 SP1 or later (Windows 10/11 recommended)
- **.NET Runtime**: .NET 7 Runtime or .NET 7 SDK
- **Controller**: Xbox 360/Xbox One controller, or any XInput-compatible gamepad (including ASUS ROG Ally)
- **Network**: Connected to the same LAN as the server PC

### Both PCs
- Firewall must allow UDP traffic on ports 7776-7777 (or configure accordingly)
- For best performance: Connected via Ethernet or 5GHz WiFi with low latency

## Installation & Setup

### 1. Prerequisites - Install .NET 7 Runtime/SDK

#### Option A: Install .NET 7 Runtime (smaller, for running only)
- Download from: https://dotnet.microsoft.com/download/dotnet/7.0
- Choose "Runtime" installer for your architecture

#### Option B: Install .NET 7 SDK (for development/building)
- Download from: https://dotnet.microsoft.com/download/dotnet/7.0
- Choose "SDK" installer for your architecture

Verify installation:
```powershell
dotnet --version
```

### 2. Install ViGEm Bus Driver (Server PC only)

The server requires the ViGEm Bus Driver to create virtual controllers:

1. Download latest release from: https://github.com/nefarius/ViGEmBus/releases
2. Run `ViGEmBusSetup.exe`
3. Follow the installation wizard
4. Reboot if prompted
5. Verify installation by checking Device Manager → Universal Serial Bus controllers (you should see "Virtual Gamepad Emulation Bus")

### 3. Clone/Download the Project

```powershell
git clone https://github.com/yourusername/kontroler.git
cd kontroler
```

Or download the repository as a ZIP file and extract it.

## Building

### Option 1: Build from Command Line

```powershell
# Build Client
dotnet build ./Client

# Build Server
dotnet build ./Server
```

### Option 2: Build Using Visual Studio 2022

1. Open the solution in Visual Studio 2022
2. Right-click on the solution → "Build Solution"
3. Wait for build to complete

### Output Location

Built executables will be in:
- `Client/bin/Debug/net7.0-windows/`
- `Server/bin/Debug/net7.0/`

## Usage

### Quick Start

**On Server PC (the PC running games):**
```powershell
cd kontroler
dotnet run --project ./Server -- 7777 mysecretpassword
```

**On Client PC (the PC with the controller):**
```powershell
cd kontroler
dotnet run --project ./Client
```

A window will appear with options to scan for servers or manually enter connection details.

### Advanced Usage

#### Server with Custom Port and Secret
```powershell
dotnet run --project ./Server -- <port> <secret>
```

Parameters:
- `<port>`: UDP port to listen on (default: 7777)
- `<secret>`: Password for authentication (default: "secret")

Example:
```powershell
dotnet run --project ./Server -- 8888 verysecurepassword123
```

#### Client with Manual Connection

```powershell
dotnet run --project ./Client -- <server_ip> <server_port> <local_port> <secret>
```

Parameters:
- `<server_ip>`: IP address of the server PC
- `<server_port>`: Server's UDP port (must match server setting)
- `<local_port>`: Local UDP port for client to bind to (usually 7778)
- `<secret>`: Password (must match server setting)

Example:
```powershell
dotnet run --project ./Client -- 192.168.1.100 7777 7778 verysecurepassword123
```

### Server Discovery

The Client includes an automatic server discovery feature:

1. Click "Scan Network" to search for available servers on your LAN
2. Servers will appear in the list with their IP addresses
3. Enter the shared secret (password)
4. Select a server from the list
5. Click "Connect"

Alternatively, use "Manual Entry" to specify server details directly.

## Protocol Details

### Communication

- **Protocol**: UDP (User Datagram Protocol)
- **Ports**: 
  - Discovery: UDP 7776
  - Control: UDP 7777 (default, configurable)
- **Packet Format**: 
  - Gamepad state packets contain button presses, trigger values, and analog stick positions
  - Vibration packets contain motor speed values for feedback
  - All packets are authenticated with HMAC-SHA256

### Security

- **Authentication**: HMAC-SHA256 with pre-shared secret
- **Encryption**: Each packet includes an HMAC for integrity verification
- **Assumptions**: All devices on trusted LAN; for internet use, consider adding additional security

### Xbox 360 Controller Format

The virtual controller emulates an Xbox 360 controller with:
- 16 digital buttons (A, B, X, Y, LB, RB, Start, Back, Left/Right thumbsticks pressed, D-Pad)
- 2 analog triggers (Left, Right)
- 2 analog thumbsticks (Left, Right) with 2D axis input
- Vibration feedback (left/right motors)

## Troubleshooting

### "ViGEmClient initialization failed" on Server
- **Cause**: ViGEm Bus Driver not installed or not running
- **Solution**: 
  1. Download and install ViGEmBusSetup from https://github.com/nefarius/ViGEmBus/releases
  2. Reboot your system
  3. Verify in Device Manager that the virtual bus appears

### "Failed to connect to server" on Client
- **Cause**: Server not running, wrong IP, wrong port, or firewall blocking
- **Solution**:
  1. Verify server is running and listening on the correct port
  2. Check network connection: `ping <server_ip>`
  3. Verify firewall allows UDP on the port
  4. Confirm both PCs are on the same network

### "Authentication failed" or "Invalid secret"
- **Cause**: Client and server secret passwords don't match
- **Solution**: Ensure both Client and Server use the same secret parameter

### Controller not working on Server PC
- **Cause**: Virtual controller not properly initialized, or server port conflict
- **Solution**:
  1. Check that ViGEm is properly installed (see above)
  2. Try a different port: `dotnet run --project ./Server -- 7779 secret`
  3. Check Windows Event Viewer for related errors
  4. Restart the ViGEm Bus service

### High latency or input lag
- **Cause**: Network congestion, WiFi interference, or packet loss
- **Solution**:
  1. Use Ethernet cables instead of WiFi
  2. Reduce network load on both PCs
  3. Check for WiFi interference (try different channel)
  4. Move closer to the WiFi router

### Vibration not working
- **Cause**: Game doesn't support vibration, or ViGEm not properly initialized
- **Solution**:
  1. Verify the game supports Xbox 360 controller vibration
  2. Test with a known game like *Forza* or *Gears of War*
  3. Ensure ViGEm is properly installed on Server PC

## Building for Release

To create optimized, self-contained executables:

```powershell
# Build Client
dotnet publish ./Client -c Release -o ./publish/client

# Build Server  
dotnet publish ./Server -c Release -o ./publish/server
```

Executables will be in:
- `./publish/client/`
- `./publish/server/`

## Performance Considerations

- **Latency**: Typically 10-50ms on local LAN depending on network conditions
- **Bandwidth**: ~200-500 bytes per second per connection
- **CPU**: Minimal (< 5% on modern systems)
- **Best Performance**: Ethernet connection with low ping time

## Limitations

- Requires both PCs on the same local network
- Only supports Xbox 360 controller input format (XInput)
- Cannot forward audio (must use other solutions for game audio)
- Cannot forward video (requires separate streaming solution like Steam Remote Play)
- Vibration depends on game support

## Future Improvements

Potential enhancements:
- Internet/remote connection support with VPN
- Multiple client support
- DualShock 4 / Xbox One S native support
- Direct controller passthrough for non-XInput devices
- Web-based configuration interface
- System tray support for client

## Development Notes

### Project File Structure

- **Client**: Uses Windows Forms for GUI
  - Includes built-in server discovery
  - Reads XInput controller state via P/Invoke
  - Sends encrypted UDP packets

- **Server**: Console application
  - Uses Nefarius.ViGEm.Client NuGet package
  - Creates virtual Xbox 360 controller
  - Listens for incoming controller data
  - Sends vibration feedback

- **Inspect**: Utility for analyzing ViGEm assemblies (development only)

### Dependencies

- **Nefarius.ViGEm.Client** (Server only): Virtual gamepad emulation library
- **.NET 7 Runtime**: Cross-platform .NET runtime
- **Windows.Forms** (Client): GUI framework

## Contributing

Contributions are welcome! Please feel free to submit issues or pull requests.

## Support

For issues, questions, or suggestions:
1. Check the Troubleshooting section above
2. Review existing GitHub issues
3. Open a new issue with detailed information about your problem

## References

- [XInput Documentation](https://learn.microsoft.com/en-us/windows/win32/xinput/xinput-game-controller-api-portal)
- [ViGEm Project](https://github.com/nefarius/ViGEm)
- [ViGEm Bus Driver](https://github.com/nefarius/ViGEmBus)
- [ASUS ROG Ally](https://rog.asus.com/us/handheld-gaming-device/)
