# Kugutsu MSI

## Build

Build the application payload in Release configuration, then build the x86 MSI from that standard layout:

~~~powershell
dotnet build ..\Kugutsu.slnx -c Release
dotnet build .\Kugutsu.wixproj -p:PayloadRoot=..\artifacts\Release -p:ProductVersion=1.0.0
~~~

The build generates bin/installation.json and checks the app, proxy, ConsoleProbe, AloeHost, fixed Aloe dependencies, OCR model files, x86 native libraries, and model license against their versions and SHA-256 values. The MSI project harvests bin, config, and dat into the matching installation folders and creates logs. A missing or modified fixed OCR asset stops the MSI build.

During installation, Kugutsu detects Windows Terminal, Windows PowerShell, conhost, Firefox, Chrome, and Edge from existing executable paths and writes only found targets to `config/detected-launch-targets.json`. Existing entries in `appsettings.json` take precedence. App Execution Aliases are not treated as installed executables; an undetected target remains unavailable until configured manually.

The setup action downloads drivers only for configured and present browsers: ChromeDriver from the official Chrome for Testing build metadata matching Chrome's major/minor/build, Edge WebDriver from Microsoft's matching three-part build, and geckodriver 0.36.0 for Firefox 115 or later. Downloads require HTTPS through the allowlisted official hosts, pass Authenticode publisher validation, and are extracted as a single expected executable. `bin/drivers/drivers-manifest.json` records browser/driver versions, source URL, architecture, size, SHA-256, and signer thumbprint. `BrowserDriverPaths` is generated in the detected settings file while explicit `appsettings.json` entries take precedence. If a detected browser has no supported official driver, installation fails with a nonzero setup result. No detected browser means no driver download.

## .NET runtime prerequisite check

`scripts/Test-DotNetRuntimes.ps1` reads the 32-bit .NET installation registry view and verifies that the corresponding x86 runtime directories exist for `Microsoft.NETCore.App`, `Microsoft.WindowsDesktop.App`, and `Microsoft.AspNetCore.App` 10.0. It returns structured JSON; exit code 0 means all are present, 1 means one or more are missing, and 2 means detection failed. A separately installed x64 runtime cannot satisfy the x86 check. The MSI also has a launch condition that rejects missing runtimes when launched directly.

## Bundled setup executable

Build the MSI first, then create the Burn setup executable:

~~~powershell
dotnet build .\installer\KugutsuSetup.wixproj -p:PayloadRoot=..\artifacts\Release -p:RuntimePayloadRoot=..\artifacts\runtime -p:ProductVersion=1.0.0
~~~

The build reads Microsoft's .NET 10 LTS release metadata and downloads the latest stable x86 Desktop Runtime and ASP.NET Core Runtime installers. It requires HTTPS metadata and download URLs, verifies each file's SHA-512 against Microsoft's release metadata and its valid Microsoft Authenticode signature, and writes `artifacts/runtime/dotnet-runtimes.json`. Both installers are embedded in `artifacts/installers/Kugutsu.Setup.exe`.

At install time Burn detects x86 .NET 10 Desktop and ASP.NET Core runtimes, installs only missing packages, and runs the MSI after both installers succeed. A failed runtime package is vital and stops the chain before the MSI. Runtime packages are permanent shared prerequisites and are not removed when Kugutsu is uninstalled. The MSI installs into `E:\Kugutsu` by default when invoked through the Burn bundle.

## Install destination

The package defaults to Program Files (x86)\Kugutsu. Override the public INSTALLFOLDER directory property to install at the selected location, including the planned E:\Kugutsu directory:

~~~powershell
msiexec.exe /i ..\artifacts\installers\Kugutsu.msi INSTALLFOLDER=E:\Kugutsu
~~~

## Update and uninstall

`MajorUpgrade` replaces an older Kugutsu version. The MSI retains `config/appsettings.json` as a permanent, never-overwritten per-machine settings component, so edits survive repair, upgrades, and uninstall. Windows Credential Manager credentials are also retained. `config/detected-launch-targets.json`, downloaded `bin/drivers`, and temporary `dat/driver-setup` content are installer-generated; the uninstall action removes them during normal removal and upgrades, then setup regenerates them for the new installation. MSI-managed application files and the current user's Run entry are removed by Windows Installer. Generated logs may remain for user inspection. The x86 .NET runtimes are shared prerequisites and are never uninstalled by Kugutsu.

The uninstall cleanup runs from the installed executable before MSI removes its files, uses only the fixed `bin/drivers`, `dat/driver-setup`, and detected-target paths under the installation root, and refuses to traverse reparse-point parents. If the app is in use, Windows Installer may request that it close or schedule locked files for removal after restart.

These commands build the MSI and setup executable only. They do not install or publish them.

## Toolchain

The MSI project uses WiX Toolset SDK 7.0.0. See the [WiX 7 release](https://github.com/wixtoolset/wix/releases/tag/v7.0.0) and its [Open Source Maintenance Fee EULA](https://github.com/wixtoolset/wix/blob/main/OSMFEULA.txt). The build machine must satisfy the applicable tool EULA.
## MCP資格情報

インストール時に、対話的にMSIを起動したWindowsユーザーのCredential Managerへ32バイト乱数由来のBearer tokenを作ります。既存資格情報が有効なら値を変更しません。修復・更新・再インストールでも同じ保存先を使い、アンインストールでは削除しません。SYSTEMアカウントによる無人配布ではSYSTEM用に保存されるため、その方式で一般ユーザー用資格情報を作ることはできません。秘密値はコマンドライン、MSIログ、画面、ファイルへ出力しません。
## ログオン自動起動

MSIはHKCUのRunキーへKugutsu.exeを登録します。登録はMSIを実行したユーザーに対して行われ、Windowsログオン時にトレイアプリを起動します。別のWindowsユーザーのRunキーは変更しません。アンインストールではMSI管理のRun値を除去します。
