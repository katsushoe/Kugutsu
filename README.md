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

Setup detects installed launch targets and browser drivers; explicit settings take precedence over detected values. MCP credentials are read from Windows Credential Manager, not from client configuration. See the [configuration guide](CONFIG.md), [sample settings](config/appsettings.json), and [configuration specification (Japanese)](SPECIFICATION.md#7-設定とログ).

## Usage

Start `Kugutsu.exe`, then register `Kugutsu.McpProxy.exe` as an stdio MCP server in your AI client using the configuration in [Getting Started](#getting-started). Example requests:

- “List the open windows.”
- “Capture the window titled ‘Notepad’ and summarize what is visible.”

See the [MCP setup guide](MCP_SETUP.md), [MCP command reference](COMMANDS.md), and [usage guide (Japanese)](docs/USAGE.md) for details.

## Documentation

- [Document index](DOCUMENTS.md)
- [MCP setup](MCP_SETUP.md) and [configuration](CONFIG.md)
- [MCP command reference](COMMANDS.md) and [package inventory](PACKAGES.md)
- [Detailed operation specification (Japanese)](SPECIFICATION.md)
- [Usage guide (Japanese)](docs/USAGE.md), [Aloe batches (Japanese)](docs/ALOE_BATCH.md), and [UI profiles (Japanese)](docs/UI_PROFILES.md)
- [Design decisions (Japanese)](docs/adr/)
- [Installer and packaging](installer/README.md)
- [Third-party notices (Japanese)](THIRD_PARTY_NOTICES.md)

## Security

The HTTP server binds to loopback and validates Bearer and logical session credentials. Use the packaged proxy to manage authentication and sessions. Keep credentials out of source files, client settings, command history, and logs. App launch targets and drivers must satisfy configured validation rules.

Screenshots and operation results can contain information displayed by other applications. Review the target before sending input or sharing results. Input already sent to an application is not rolled back by cancellation, lock expiry, or emergency stop. See the [usage guide (Japanese)](docs/USAGE.md) for lock and error handling.

## License

This repository does not currently include a project-wide license file. Third-party components retain their respective licenses; see [third-party notices (Japanese)](THIRD_PARTY_NOTICES.md) and the bundled license files under `dat/licenses` and `dependencies/Aloe`.
