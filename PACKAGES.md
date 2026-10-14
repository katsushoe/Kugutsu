# Kugutsu packages

This inventory lists direct NuGet package references. Project-to-project references are not NuGet packages. The Japanese overview is in [DOCUMENTS.ja.md](DOCUMENTS.ja.md); this file uses package and project identifiers as they appear in the build files.

## Direct package references

| Package | Version | Project | Purpose |
| --- | --- | --- | --- |
| `ModelContextProtocol.Core` | `2.2.0` | `src/Kugutsu.McpProxy` | MCP client transport and protocol types for the stdio proxy |
| `ModelContextProtocol.AspNetCore` | `2.2.0` | `src/Kugutsu.App` | MCP server endpoint over ASP.NET Core |
| `Tesseract` | `[5.2.0]` | `src/Kugutsu.App` | Local OCR integration |

## Sources and transitive dependencies

Package references are declared in the project `.csproj` files and restored through the configured NuGet sources. The repository does not check in a NuGet lock file; the resolved transitive graph depends on the SDK and restore inputs. Inspect it for a specific build with:

```powershell
dotnet list Kugutsu.slnx package --include-transitive
```

## Update guidance

Update the version in the owning project file, restore the solution, review direct and transitive package changes, and run the applicable build and tests. Keep package purpose and version in this inventory synchronized with the project files. Do not add a package solely to duplicate functionality already provided by the .NET platform or an existing dependency.
