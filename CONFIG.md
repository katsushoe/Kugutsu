# Kugutsu configuration

English | [日本語](CONFIG.ja.md)

Runtime configuration is stored in `config/appsettings.json` under the installation directory. The tracked [sample](config/appsettings.json) shows the default values. The Japanese guide is [CONFIG.ja.md](CONFIG.ja.md).

## Loading and validation

- The application reads the `Kugutsu` JSON object at startup. Restart the application and stdio proxy after editing it; settings are not reloaded automatically.
- Use UTF-8 JSON. Unknown keys, duplicate keys that differ only by case, invalid types, and invalid values prevent startup.
- `ServerUrl` must be a loopback HTTP absolute URL. `UiProfileDirectory` must resolve inside the installation directory.
- `AllowedLaunchTargets` is an explicit allowlist. An empty list is valid and does not permit arbitrary executable paths.
- MCP credentials are stored in Windows Credential Manager. Do not copy them into this file or an MCP client configuration.

## Setting groups

| Group | Examples | Purpose |
| --- | --- | --- |
| MCP endpoint and request limits | `ServerUrl`, `QueueCapacity`, `MaxRequestBytes`, `OperationTimeoutSeconds` | Local connection and request processing limits |
| Locks and signals | `McpLockLeaseSeconds`, `MaxSignals`, `SignalRetentionSeconds` | Cross-request exclusivity and condition monitoring |
| UI and OCR | `EnabledUiDrivers`, `UiProfileDirectory`, `OcrLanguage`, `MaxUiCandidates` | UI discovery and local visual processing |
| Applications and browsers | `AllowedLaunchTargets`, `BrowserLaunchTargets`, `BrowserDriverPaths` | Explicitly permitted launch targets and drivers |
| Logs | `LogLevel`, `LogRetentionDays`, `LogFileSizeMiB`, `LogTotalSizeMiB` | Structured log output and retention |

For every key's type, default, range, and validation rules, see [SPECIFICATION.md, section 7](SPECIFICATION.md#7-設定とログ). Do not duplicate that table here; update the specification and sample together when a setting changes.
