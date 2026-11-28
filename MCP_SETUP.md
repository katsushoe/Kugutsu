# Kugutsu MCP setup

English | [日本語](MCP_SETUP.ja.md)

The Japanese setup guide is [MCP_SETUP.ja.md](MCP_SETUP.ja.md). Detailed operation examples and troubleshooting are in the [usage guide (Japanese)](docs/USAGE.md).

## Start the server

Install Kugutsu and start `Kugutsu.exe`. The application runs in the Windows system tray and hosts the local MCP endpoint. Run the application and proxy as the same Windows user.

## Register the stdio proxy

Register the packaged proxy in your MCP client. Adapt the wrapper format to the client if needed:

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

The proxy reads the endpoint and credentials from the installed configuration and Windows Credential Manager. Do not configure the Bearer credential in the client. Restart the client or reload its MCP servers after saving the configuration.

## Verify the connection

Call `get_version` to verify the MCP connection, then `list_windows` to inspect available windows. If the server does not connect, confirm that Kugutsu is running, the proxy path exists, and both processes use the same Windows account. See [CONFIG.md](CONFIG.md) for endpoint settings and the [usage guide](docs/USAGE.md) for further troubleshooting.
