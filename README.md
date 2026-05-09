# Simple Radius

A clean, user-friendly RADIUS server for Windows built with .NET 8 and WPF.
Designed as a modern alternative to WinRadius and FreeRADIUS for SMB/enterprise use.

---

## Phase 1 — What's working now

| Feature                        | Status |
|-------------------------------|--------|
| RFC 2865 PAP authentication    | ✅ |
| RFC 2866 Accounting responses  | ✅ |
| Local user store (JSON)        | ✅ |
| NAS client registry (IP/CIDR) | ✅ |
| VLAN assignment (Tunnel attrs) | ✅ |
| Session timeout attribute      | ✅ |
| Live log streaming in GUI      | ✅ |
| Dashboard auth statistics      | ✅ |
| User enable/disable toggle     | ✅ |
| Configurable ports & bind addr | ✅ |

## Coming in Phase 2

- CHAP, MSCHAPv2 authentication
- PEAP / MSCHAPv2 (EAP tunnelled)
- EAP-TLS (certificate-based)
- bcrypt password hashing (replaces plain-text)
- Windows Service installer (auto-start, no GUI needed)
- LDAP / Active Directory user backend
- Policy engine with group-based rules
- Accounting log to SQLite + CSV export

---

## Requirements

- Windows 10/11 or Windows Server 2019+
- [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0)
  (install the **"Desktop Runtime"** — includes WPF)

---

## Build from source

```powershell
# Clone / unzip the project, then:
cd SimpleRadius
dotnet build SimpleRadius.sln
dotnet run --project SimpleRadius.GUI
```

> **Note:** The GUI project targets `net8.0-windows` and must be built on Windows.
> The Core library (`SimpleRadius.Core`) is cross-platform and can be built on Linux/macOS.

---

## Quick-start testing with `radtest`

Install `freeradius-utils` (Linux) or NTRadPing (Windows) and run:

```bash
# Test with the seeded default user (shared secret: testing123)
radtest testuser testpass 127.0.0.1 0 testing123

# Expected output:
# Received Access-Accept Id 0 from 127.0.0.1:1812
```

The server seeds two default NAS entries on first run:
- `localhost-test` → `127.0.0.1` with secret `testing123`
- `all-private`    → `0.0.0.0` (wildcard) with secret `testing123` *(remove in production!)*

And three default users:
| Username   | Password    | Group   | VLAN |
|------------|-------------|---------|------|
| testuser   | testpass    | default | 1    |
| admin      | adminpass   | admins  | 10   |
| disabled   | any         | default | 1    | ← account disabled

---

## Project structure

```
SimpleRadius/
├── SimpleRadius.sln
├── SimpleRadius.Core/           # Cross-platform RADIUS engine
│   ├── IRadiusLogger.cs         # Lightweight logging interface
│   ├── Protocol/
│   │   └── RadiusPacket.cs      # RFC 2865 packet parsing & encoding
│   ├── Models/
│   │   └── Models.cs            # UserEntry, NasClient
│   ├── Storage/
│   │   ├── UserStore.cs         # Thread-safe JSON user database
│   │   └── NasStore.cs          # Thread-safe JSON NAS registry
│   └── Server/
│       └── RadiusServer.cs      # UDP server, auth & accounting logic
│
└── SimpleRadius.GUI/            # Windows WPF application
    ├── App.xaml / App.xaml.cs
    └── MainWindow.xaml / .cs    # Full UI: dashboard, users, NAS, logs, settings
```

---

## Data files

All data is stored as human-readable JSON in the `data/` directory
(configurable in Settings):

- `data/users.json` — user accounts
- `data/nas.json`   — NAS client entries

These files are created automatically with defaults on first run.

---

## Architecture notes

- The RADIUS engine runs entirely on async UDP sockets (`UdpClient` + `Task.Run`)
- Each incoming packet is dispatched to the thread pool — no head-of-line blocking
- The GUI communicates with the server via C# events (thread-safe via `Dispatcher.InvokeAsync`)
- The server can run standalone (no GUI) — useful for the Phase 2 Windows Service
- PAP password decryption follows RFC 2865 §5.2 exactly (MD5 XOR chain)
- Response authenticators are computed per RFC 2865 §3

---

## License

MIT — free to use, modify, and distribute.
