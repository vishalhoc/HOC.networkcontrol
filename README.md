# WinNetControl

**WinNetControl** is a modern, WinUI 3-based network monitoring and control application for Windows. It provides deep visibility into your active network connections, allowing you to seamlessly view process traffic, map connections, and instantly block or allow traffic using Windows Firewall.

## Features

*   **Real-time Process Monitoring:** View all active network connections, including hidden, UDP, and TCP protocols. 
*   **Local Network Scanner:** Scan your local subnet (Wi-Fi/LAN) to discover all connected devices. Identify device vendors (Apple, Samsung, Amazon, etc.), view MAC addresses, and ping latency.
*   **1-Click Blocking:** Instantly block or unblock any process or local device from accessing the network. Temporary blocks (15 mins, 1 hour, etc.) are also supported.
*   **Per-App Network Map:** Visual request/response tree that shows exactly how a request travels from an app -> Windows Firewall -> AdGuard -> Network Adapter -> Router -> DNS Server -> Remote Host.
*   **Single Instance Manager:** Keeps your desktop clean by ensuring only one instance of the app runs at a time.
*   **Beautiful UI:** Modern WinUI 3 design with dark/light mode support, auto-suggest search, and sorting capabilities.

## Installation

### Method 1: Pre-built Installer (Recommended)
Download the latest `WinNetControl_Setup.exe` from the [Releases page](https://github.com/vishalhoc/HOC.networkcontrol/releases). 
During installation, you can choose between:
*   **Full Installation:** Installs to Program Files, creates Start Menu/Desktop shortcuts, and provides an uninstaller.
*   **Portable Installation:** Extracts the necessary files to a folder of your choice without touching the registry.

### Method 2: Build from Source
Ensure you have the [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) and the [Windows App SDK](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/downloads) installed.
```bash
git clone https://github.com/vishalhoc/HOC.networkcontrol.git
cd HOC.networkcontrol
dotnet build -c Release
```

## Contributing
Pull requests are welcome! If you're adding major features, please open an issue first to discuss what you would like to change.

## License
MIT License
