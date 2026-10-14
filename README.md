# Kugutsu

[English](README.md) | [日本語](README.ja.md)

Kugutsu is a C# MCP server that lets AI clients operate windows, controls, and browsers on a local Windows PC. It runs in the system tray and accepts requests through a local stdio proxy connected to its Streamable HTTP server.

## Getting Started

1. Download `Kugutsu.Setup.zip` and `SHA256SUMS.txt` from [Kugutsu 1.0.1.0](https://github.com/katsushoe/Kugutsu/releases/tag/v1.0.1.0). Verify the ZIP's SHA-256, extract it, and run `Kugutsu.Setup.exe`.
2. Start `E:\Kugutsu\bin\Kugutsu.exe`, or sign out and sign back in to use the installed logon startup entry. The app runs in the system tray; its menu opens the operation buffer window.
3. Register the following stdio server in your MCP client, adapting the configuration format to that client:

```json
{
  "mcpServers": {
    "kugutsu": {
      "command": "E:\\Kugutsu\\bin\\Kugutsu.McpProxy.exe",
      "args": []
    }
  }
}
```

Run `get_version` to verify the connection, then `list_windows` to inspect available windows. Run the app and proxy as the same Windows user. See the [usage guide (Japanese)](docs/USAGE.md) for request examples and troubleshooting.

## Installation

### Installer

Supported environments are Windows 10/11 with an x86 .NET 10 runtime. The recommended Setup executable installs missing x86 .NET 10 Desktop and ASP.NET Core runtimes before installing the MSI. No .NET SDK is required on the target PC. The Setup default destination is `E:\Kugutsu`.

The [release](https://github.com/katsushoe/Kugutsu/releases/tag/v1.0.1.0) provides `Kugutsu.msi`, `Kugutsu.Setup.zip`, and `SHA256SUMS.txt`. Version 1.0.1.0 is unsigned; Windows may display an unknown publisher or SmartScreen warning. Compare the downloaded file's hash with `SHA256SUMS.txt`:

```powershell
Get-FileHash .\Kugutsu.Setup.zip -Algorithm SHA256
```

For direct MSI installation, install the required x86 runtimes first and specify the destination:

```powershell
msiexec.exe /i .\Kugutsu.msi INSTALLFOLDER=E:\Kugutsu
```

Installation creates MCP credentials in the installing user's Windows Credential Manager and registers logon startup for that user. Updates preserve settings and credentials. See the [installer guide](installer/README.md) for prerequisites, update behavior, and uninstall details.

### Binary Archive

`Kugutsu.Setup.zip` contains the Setup executable. Extract it and run the installer; it is not a portable application archive.

### Source Build

Use the .NET 10 SDK. From the repository root:

```powershell
dotnet restore Kugutsu.slnx
dotnet build Kugutsu.slnx -c Release --no-restore
```

Build output uses the `artifacts/Release/bin`, `config`, `dat`, and `logs` layout. The app still requires configured credentials and runtimes. Package the output with WiX Toolset SDK 7.0.0:

```powershell
dotnet build installer/Kugutsu.wixproj -p:PayloadRoot=../artifacts/Release -p:ProductVersion=1.0.1
dotnet build installer/KugutsuSetup.wixproj -p:PayloadRoot=../artifacts/Release -p:RuntimePayloadRoot=../artifacts/runtime -p:ProductVersion=1.0.1
```

See the [installer guide](installer/README.md) for payload validation and runtime downloads.

## Configuration

Runtime settings are stored in `config/appsettings.json` under the installation directory. The default MCP endpoint is `http://127.0.0.1:47641/mcp`. Restart the app and proxy after changing settings; configuration is not reloaded automatically.

| Directory | Purpose |
| --- | --- |
| `bin` | Executables and supporting libraries |
| `config` | Settings and UI profiles |
| `dat` | Data files, including OCR models |
| `logs` | Structured runtime logs |

Setup detects installed launch targets and browser drivers; explicit settings take precedence over detected values. MCP credentials are read from Windows Credential Manager, not from client configuration. See the [sample settings](config/appsettings.json) and [configuration specification (Japanese)](SPECIFICATION.md#7-設定とログ).

## Usage

- Use `execute_operation` for one operation and `submit_batch` for a JSON operation list or an Aloe program. Accepted requests enter a FIFO operation buffer; use `get_request` to check completion and results, or `cancel_request` to request cancellation.
- Window operations include listing, activation, opening, closing, minimizing, maximizing, and restoration. Input and inspection include mouse/keyboard operations and window or desktop snapshots.
- Control operations use UI Automation or visual drivers for custom drawn UIs. Visual operations require supported profiles and target verification; OCR does not guarantee an exact value or access to internal application state.
- Browser and DOM operations support Chrome, Edge, and Firefox through managed WebDriver sessions. Select the browser type explicitly. Installed/default browser discovery is also available.
- Use `lock_mcp`, `is_mcp_locked`, `renew_mcp_lock`, and `unlock_mcp` for exclusive work across multiple requests. Signals support registering, waiting for, and removing conditions.

See the [usage guide (Japanese)](docs/USAGE.md), [Aloe guide (Japanese)](docs/ALOE_BATCH.md), and [operation specification (Japanese)](SPECIFICATION.md) for arguments, results, and limitations.

## Documentation

- [Usage and MCP client setup (Japanese)](docs/USAGE.md)
- [Operation and configuration specification (Japanese)](SPECIFICATION.md)
- [Aloe batches (Japanese)](docs/ALOE_BATCH.md)
- [UI profiles (Japanese)](docs/UI_PROFILES.md)
- [Craywork profile example (Japanese)](docs/CRAYWORK_PROFILE.md)
- [Installer and packaging](installer/README.md)
- [Managed browser session design (Japanese)](docs/adr/0004-managed-webdriver-sessions.md)
- [ChromeDriver authenticity policy (Japanese)](docs/adr/0011-chrome-driver-authenticity.md)
- [Third-party notices (Japanese)](THIRD_PARTY_NOTICES.md)

## Security

The HTTP server binds to loopback and validates Bearer and logical session credentials. Use the packaged proxy to manage authentication and sessions. Keep credentials out of source files, client settings, command history, and logs. App launch targets and drivers must satisfy configured validation rules.

Screenshots and operation results can contain information displayed by other applications. Review the target before sending input or sharing results. Input already sent to an application is not rolled back by cancellation, lock expiry, or emergency stop. See the [usage guide (Japanese)](docs/USAGE.md) for lock and error handling.

## License

This repository does not currently include a project-wide license file. Third-party components retain their respective licenses; see [third-party notices (Japanese)](THIRD_PARTY_NOTICES.md) and the bundled license files under `dat/licenses` and `dependencies/Aloe`.
