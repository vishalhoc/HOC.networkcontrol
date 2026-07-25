# HOC Network Control — Changelog

---

## v3.1.0 — 2026-07-20 🚀

### ⚙️ Settings Page — Complete Overhaul
- **Widget Customization** section fully implemented (was missing):
  - Opacity slider (20–100%) with live percentage label
  - Font size slider (10–22 pt) with live label
  - Widget width & height NumberBoxes
  - Layout selector: Vertical / Horizontal / Compact
  - Refresh interval combo (500 ms – 5 s)
  - Disable transparency toggle — all saved to config
- **Appearance**: Acrylic and Animations toggles now backed by `AppConfig` (were empty stubs)
- **Behavior**: Start Minimized toggle now saves to config (was empty stub)
- **Notifications** section — all options now saved to config:
  - Notify when app is blocked → `NotifyOnBlock`
  - Notify on high bandwidth usage → `NotifyOnHighUsage`
  - Notify on QoS policy changes → `NotifyOnQos`
  - Configurable bandwidth threshold → `BandwidthThresholdMBps`
- **Data & Storage**:
  - Added **Import Config** with file picker
  - Added **Reset to Defaults** with confirmation dialog
  - Config file path displayed inline
- **Update Checker** — real implementation replacing stub:
  - Queries `api.github.com/repos/vishalhoc/HOC.networkcontrol/releases/latest`
  - Compares GitHub tag version against running assembly version
  - Shows green ✓ badge when up to date
  - Shows red badge + expandable release-notes banner + **Download →** link when update available
  - Handles network errors and timeouts gracefully
- **About section**: Added GitHub hyperlink, .NET 10 platform info, direct link to All Releases page
- Save button moved to header bar for quicker access; still available inline

---

### 🔧 Per-Socket QoS — Fixed & Enhanced
- **Root cause fixed**: QoS was applying to the whole application instead of a single socket
  - Windows `New-NetQosPolicy` cannot combine `AppPathNameMatchCondition` with IP/Port conditions simultaneously
  - New **"Set QoS — this socket only (IP:Port)"** sub-menu creates a destination-only policy targeting the exact remote `IP:Port`, bypassing the app-path constraint
- **Auto-Expire Timer**: Set a countdown (5 min – 2 hours) for any socket QoS policy; auto-removed via background task
- Context menu now shows two clearly labelled QoS sub-menus for clarity

### 🛠️ QoS Manager — Advanced Management
- **Edit button** per row: change DSCP value of an existing policy in-place (re-applies with same name/conditions)
- **Expires column**: live countdown (HH:mm:ss) showing when a policy will auto-delete
- **Make persistent toggle**: copies policy to the `localhost` store so it survives reboots (`Copy-NetQosPolicy`)
- **Auto-expire NumberBox**: set a timer when creating any new policy

### 🔩 QosPolicyService — Architecture Fix
- Old guard blocked policies when `processPath` was empty, even with valid destination IP/Port
- Now correctly handles three modes:
  - App-only: `processPath` set, no destination → ✅
  - Destination-only (socket-level): empty path + IP/Port → ✅ *(new)*
  - Both combined → ❌ with clear error (Windows limitation)

### 🛡️ ElevatedRunner — Reliability Fix
- Refactored to use temporary `.ps1` files with `-File` execution
- Eliminates all PowerShell string-escaping issues that caused silent failures with complex rule names

### 🗄️ BlockedConnectionStore
- New cross-module state store for tracking blocked connections
- Enables real-time sync between ConnectionManager, Firewall, and Socket Manager pages

### 🔧 Internal / Model Changes
- `AppConfig`: Added `EnableAcrylic`, `EnableAnimations`, `StartMinimized`, `NotifyOnBlock`, `NotifyOnHighUsage`, `NotifyOnQos`, `BandwidthThresholdMBps`
- `MainViewModel.CurrentConfig`: Changed `private set` → `internal set` to allow Settings page import/reset

---

## v2.1.0 — Previous Release

- Initial public release with:
  - Real-time connection monitoring (ETW)
  - Per-app firewall rules (inbound + outbound)
  - QoS policy manager
  - Socket inspector with process correlation
  - DNS manager, LAN scanner, port scanner
  - HTTP inspector (proxy-based)
  - Packet capture (WinDivert)
  - Dashboard with live bandwidth widgets
  - Hosts file manager
  - VPN manager, routing table, IP config
  - Wireless diagnostics
  - Speed tools (ping, traceroute, speedtest)
  - Automation rules engine
  - Security page (open ports audit)
  - Network reset tools
  - History log with export
  - Dark/Light/System theme
  - Per-app data limits & notes
  - Elevated privilege support (no UAC loop)
