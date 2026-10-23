# Controller-over-LAN

Use an Xbox/XInput gamepad (such as the **ASUS ROG Ally**) connected to one Windows PC to play games on another Windows PC over the local network, including rumble.

## Overview

- **Client** runs on the device that has the controller (e.g. the ROG Ally). It reads the controller through XInput and streams it to the server. While relaying it can lock and darken the screen, so the handheld neither lights up nor reacts to the controller itself.
- **Server** runs on the PC with the games. It creates a virtual Xbox 360 controller through [ViGEmBus](https://github.com/nefarius/ViGEmBus) for each connected client, sends the game's rumble back, and shows a live view of every connected controller.

Typical uses:

- Using the ROG Ally (or any laptop/handheld) as a wireless controller for a desktop PC
- Playing on a PC in another room while streaming the picture with Steam Remote Play, Moonlight, Parsec, etc.

## Project Structure

```
├── ControllerRelay.sln
├── Client/                     # Windows Forms app for the device with the controller
│   ├── Relay.cs                #   XInput polling, sending, rumble playback
│   ├── LockScreenForm.cs       #   dark lock screen used while relaying
│   ├── ControllerRelayForm.cs  #   status window
│   ├── ServerDiscoveryForm.cs  #   server list / connect window
│   └── XInput.cs, Brightness.cs
├── Server/                     # Windows Forms app for the gaming PC
│   ├── RelayServer.cs          #   networking and ViGEm virtual controllers
│   └── ServerForm.cs           #   window with live controller view
└── Shared/                     # compiled into both: wire protocol, controller drawing, settings
```

## Requirements

### Server PC (runs the games)
- Windows 10 or 11
- **ViGEm Bus Driver** – [download from nefarius/ViGEmBus](https://github.com/nefarius/ViGEmBus/releases)
- Inbound UDP allowed on ports **7776** (discovery) and **7777** (input) – see [Firewall](#firewall)

### Client (has the controller)
- Windows 10 or 11
- An Xbox controller or any XInput gamepad (the ROG Ally's built-in controller is XInput)
- No inbound firewall rules needed

The release builds below are single `.exe` files with .NET included, so nothing else needs to be installed. To build from source you need the .NET 7 SDK.

For the lowest latency use Ethernet on the server and 5 GHz/6 GHz Wi-Fi on the handheld.

### Firewall

On the **server** PC, Windows asks to allow the app the first time it starts – allow it on private networks. Or add a rule from an elevated PowerShell:

```powershell
netsh advfirewall firewall add rule name="Controller Relay" dir=in action=allow protocol=UDP localport=7776,7777
```

The automatic rule Windows creates is tied to the exe's path, so it has to be allowed again if you move `Server.exe`.

## Building

```powershell
dotnet build ControllerRelay.sln
```

Single-file release builds (about 150 MB each, .NET included):

```powershell
dotnet publish ./Server -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o ./publish/server
dotnet publish ./Client -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o ./publish/client
```

The first start of a single-file exe takes a few seconds longer while it unpacks.

## Usage

### Server (gaming PC)

Start `Server.exe`. It starts listening immediately. The window shows:

- **Port** and **Secret** – the password clients must use. A random one is generated on first start; use **Show** to read it. Stop the server to change either.
- **Connected clients** – each client's computer name and IP.
- **Live view** of the selected client's controller: buttons light up, sticks and triggers move, and the bars at the bottom show the rumble the game is currently sending.
- **Test strong motor / Test weak motor** – rumble the client's controller for a second.
- **Log** – connects, disconnects, wrong-secret packets, timeouts.

Minimizing hides the window to the notification area; double-click the tray icon to bring it back. Closing the window stops the server.

`Server.exe [port] [secret]` overrides (and saves) the port and secret from the command line.

Several clients can connect at once; each gets its own virtual controller (player 1, 2, ...).

### Client (e.g. ROG Ally)

Start `Client.exe`. It scans the network as soon as it opens. Double-click your server (or type its IP into **Server**), enter the secret, and press **Connect**. The address and secret are remembered.

While connected, the window shows a live view of the controller, plus:

- **Controller** – which XInput slot is sent. The first connected controller is used and picked up again if it reconnects.
- **Connection** – the round-trip time to the server, or *No response* when the server is unreachable or the secret is wrong.
- **Rumble** – the motor levels the game is requesting, or the XInput error if the controller rejects them. **Test rumble** vibrates locally, without the server, to tell a controller problem from a network one.
- **Screen off** – locks the screen now (see below). **Disconnect** returns to the server list, **Exit** closes the app; either way the server unplugs the virtual controller immediately.

`Client.exe <server_ip> <server_port> [local_port] [secret]` connects straight away without the server list.

### Lock screen

After 10 seconds without touch (or when you press **Screen off**) the client covers the screen with a black lock screen and sets the panel brightness to 0. Controller input doesn't count as activity, so playing doesn't keep it unlocked. While locked:

- Touching the screen shows a short hint at your normal brightness; it goes dark again 2.5 s after you let go.
- **Touch and hold for 5 seconds, then lift your finger** to unlock. Brightness is restored.
- All keyboard input (except volume and media keys) and all mouse input that does not come from the touch screen is blocked system-wide. Controller-to-mouse emulation (e.g. Steam's desktop layout) can't click anything.
- The lock screen keeps focus, so apps that read the controller only while in front get nothing.
- If the server stops responding for 15 seconds the lock opens by itself, so you can reconnect.

The display is deliberately never switched off. On Modern Standby devices like the ROG Ally, a switched-off display puts the whole device to sleep and drops the connection. For the same reason the client keeps the display and system awake for as long as it is connected. Turn off **Turn the screen off after 10 s without touch** in the client window to disable the automatic lock.

If the client is killed while the screen is dark, the next start restores the brightness.

### ROG Ally tips

- Keep Armoury Crate in **Gamepad** mode so the built-in controls appear as an XInput controller.
- The **Armoury Crate** and **Command Center** buttons are handled by ASUS software, not XInput. They are neither relayed nor blocked by the lock screen; remap them in Armoury Crate if they get in the way.
- Games running on the Ally itself can still read the controller in the background. Close them while relaying.

## Reliability

UDP packets can be lost or arrive out of order, so the protocol is designed so that nothing gets stuck:

- The client sends its state on every change **and** at least every 50 ms. A lost "button released" packet is corrected within 50 ms.
- Every packet carries a sequence number; the server ignores packets older than the last one applied.
- If the server hears nothing from a client for 0.5 s (Wi-Fi drop, client crash), it releases all buttons and centres the sticks. After 10 s it unplugs the virtual controller. A client that comes back from the same IP gets its controller back.
- The server repeats the current rumble every 200 ms; the client stops rumbling if it hears nothing for 1 s.
- If the controller is disconnected from the client, the client sends a neutral state.

## Protocol Details

All traffic is UDP. Integers are little-endian. Every packet except discovery ends with the first 16 bytes of HMAC-SHA256(secret, payload).

| Packet | Direction | Layout |
|---|---|---|
| Discovery request | client → broadcast :7776 | `[0xAA]` |
| Discovery response | server → client | `[0xBB][port i32][version u8][hostname utf-8]` |
| Input | client → server | `[1][session u32][seq u32][time u32][buttons u16][lt u8][rt u8][lx i16][ly i16][rx i16][ry i16]` |
| Feedback | server → client | `[2][session u32][large motor u8][small motor u8][echoed time u32][hold ms u16]` |
| Bye | client → server | `[3][session u32][seq u32]` |

Buttons use the XInput bit layout, including the Guide button (0x0400), which the client reads through `XInputGetStateEx`. Discovery broadcasts go out on every IPv4 adapter, so servers are found even when VPN or Hyper-V adapters are present.

The protocol version is 2. Version 1 builds are not compatible, so update both sides. The client marks outdated servers in the list, and the server logs packets from outdated clients.

### Security

- The HMAC authenticates every packet: without the secret nobody on the network can drive the virtual controller or forge rumble.
- Packets are **not encrypted** – controller input is visible to anyone on the LAN.
- Intended for trusted local networks. For use over the internet, tunnel it through a VPN (WireGuard, Tailscale, ZeroTier) and connect by IP.

### Settings

Both apps keep their settings in `%APPDATA%\ControllerRelay\` (`server.json`, `client.json`). The secret is stored as plain text there.

## Troubleshooting

### Server: "ViGEmBus driver not found"
Install the ViGEm Bus Driver (see above), reboot, and start the server again.

### Client: "No servers found"
- Make sure the server is running and UDP 7776 is allowed through its firewall
- Some routers/guest Wi-Fi networks block broadcasts – type the server's IP into **Server** instead

### Client: "Connection: No response"
- The secret differs from the server's – the server log shows *rejected a packet with an invalid signature*
- UDP 7777 (or your custom port) is blocked by the server's firewall
- Wrong IP/port

### Client: "Controller: Not detected"
- Connect the controller, or on the ROG Ally switch Armoury Crate to Gamepad mode
- Only XInput controllers are supported; for DualShock/DualSense use Steam Input or DS4Windows to expose them as XInput

### High latency or stutter
- Check the round-trip time in the client window; on a healthy LAN it is a few ms
- Prefer Ethernet on the server and 5 GHz/6 GHz Wi-Fi on the client; move closer to the access point

### Vibration not working
- Press **Test strong motor** in the server window: if the client's **Rumble** row changes but nothing vibrates, press **Test rumble** in the client. If that doesn't vibrate either, check the controller's vibration setting (ROG Ally: Armoury Crate → Vibration intensity)
- The game must use XInput rumble

### Lock screen stays bright
Brightness is controlled through WMI, which works for built-in laptop/handheld panels only. On external monitors the lock screen is just black.

## Performance

- **Latency**: network round trip (typically 1–5 ms on Ethernet, 2–15 ms on Wi-Fi) plus up to 2 ms polling
- **Bandwidth**: about 1 KB/s when idle, up to about 20 KB/s while sticks are moving
- **CPU**: negligible

## Limitations

- Only the Xbox 360 (XInput) controller format – no gyro, touchpad or extra back buttons
- Video and audio are not forwarded; use Steam Remote Play, Moonlight/Sunshine, Parsec or similar
- .NET 7 is out of support. The code builds with the .NET 7 SDK; retargeting to .NET 8+ is a one-line change in each `.csproj`.

## Dependencies

- **Nefarius.ViGEm.Client** (server) – virtual gamepad emulation
- **System.Management** (client) – WMI brightness control
- **Windows Forms** – UI of both apps
- **XInput** (`xinput1_4.dll`, client) – controller input and rumble

## References

- [XInput documentation](https://learn.microsoft.com/en-us/windows/win32/xinput/xinput-game-controller-api-portal)
- [ViGEmBus driver](https://github.com/nefarius/ViGEmBus)
- [ASUS ROG Ally](https://rog.asus.com/us/handheld-gaming-device/)
