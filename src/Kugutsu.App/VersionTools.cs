using System.ComponentModel;
using System.Reflection;
using Kugutsu.Contracts;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Kugutsu.App;

/// <summary>Product identity shared by MCP discovery and the version tool.</summary>
internal static class ProductInformation
{
    public const string Name = "Kugutsu";
    public static string Version { get; } = typeof(Program).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? throw new InvalidOperationException("Product version metadata is missing.");
}

/// <summary>Immediate information tools protected by the HTTP authentication middleware.</summary>
[McpServerToolType]
internal sealed class VersionTools
{
    /// <summary>Returns the build identity and the protocol version selected by the SDK.</summary>
    [McpServerTool(Name = "get_version", ReadOnly = true, UseStructuredContent = true,
        OutputSchemaType = typeof(ToolResponse))]
    [Description("Kugutsuの製品名、製品版、現在のMCPプロトコル版を取得します。引数は不要です。")]
    public static CallToolResult GetVersion(McpServer server)
    {
        var response = new ToolCallResult(ToolResponse.Information(new
        {
            name = ProductInformation.Name,
            version = ProductInformation.Version,
            protocolVersion = server.NegotiatedProtocolVersion
        }));
        var json = response.StructuredContent.GetRawText();
        return new CallToolResult
        {
            StructuredContent = response.StructuredContent,
            Content = [new TextContentBlock { Text = json }],
            IsError = response.IsError
        };
    }
}
