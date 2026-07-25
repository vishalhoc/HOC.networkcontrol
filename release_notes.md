# WinNetControl v3.2.1

We're excited to release **WinNetControl v3.2.1**, which includes massive improvements to the **Local Network Scanner**. 

## What's New 🚀

- **Advanced Device Fingerprinting** 🔍
  The LAN Scanner can now reliably identify and categorize virtually any device on your network:
  - **OUI Mac Address Matching**: Maps the MAC address of over 170+ common device manufacturers (Apple, Samsung, Amazon Echo, Raspberry Pi, TP-Link, Sony, Nintendo, and more) to properly identify iPhones, Smart TVs, IoT hardware, and consoles.
  - **Multi-Probe Hostname Resolution**: No more missing hostnames! The scanner now runs parallel queries to quickly resolve device names via NetBIOS NBNS, mDNS PTR, Android `.local`, HTTP Banner Grabs, and standard Reverse DNS.
  - **Android & IoT Device Support**: Explicitly detects Android phones and tablets on the network as well as smart home gear (Philips Hue, LIFX, Nest, Ring, Sonos).

- **1-Click Dependency Installer** ⚙️
  - Npcap missing? No problem. The Settings page now has a dedicated dependency section where you can download and silently install Npcap with a single click, complete with a real-time progress bar.

- **ARP Spoofing Capabilities** 🥷
  - We've added the ability to **Cut** and **Restore** internet access for specific devices on your network using precise ARP Spoofing (via Npcap/SharpPcap). 

## Fixes & Improvements 🛠️
- Device icons correctly adapt to the fingerprinted OS (📱 for phones, 📺 for TVs, 🍎 for Apple devices, etc).
- Network Scanner UI list columns have been realigned and optimized for speed and clarity.
- Updated to rely seamlessly on the high-performance `LibPcapLiveDevice` for network control tasks.

_Note: This is a standalone, single-file executable for 64-bit Windows systems. Simply download `WinNetControl.exe` and run!_
