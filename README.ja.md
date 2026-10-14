# Kugutsu

[English](README.md) | [日本語](README.ja.md)

Kugutsuは、ローカルWindows PCのウィンドウ、コントロール、ブラウザをAIから操作するC#製MCPサーバです。タスクトレイに常駐し、ローカルのstdioプロキシを介してStreamable HTTPサーバへ操作依頼を渡します。

## はじめに

1. [Kugutsu 1.0.1.0](https://github.com/katsushoe/Kugutsu/releases/tag/v1.0.1.0)から `Kugutsu.Setup.zip` と `SHA256SUMS.txt` を取得します。ZIPのSHA-256を照合し、展開して `Kugutsu.Setup.exe` を実行します。
2. `E:\Kugutsu\bin\Kugutsu.exe` を起動するか、サインアウト後に再ログオンして自動起動します。本体はタスクトレイに常駐し、メニューから操作バッファのウィンドウを開けます。
3. MCPクライアントに次のstdioサーバを登録します。設定形式はクライアントに合わせてください。

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

`get_version` で接続を確認し、`list_windows` で操作可能なウィンドウを調べます。本体とプロキシは同じWindowsユーザーで実行してください。要求例と問題の切り分けは[利用手順](docs/USAGE.md)を参照してください。

## インストール

### インストーラー

対応環境はWindows 10/11、x86版.NET 10ランタイムです。推奨のSetup EXEは、不足しているx86版.NET 10 Desktop RuntimeとASP.NET Core Runtimeを導入してからMSIを実行します。利用PCに.NET SDKは不要です。Setupの既定インストール先は `E:\Kugutsu` です。

[リリース](https://github.com/katsushoe/Kugutsu/releases/tag/v1.0.1.0)では `Kugutsu.msi`、`Kugutsu.Setup.zip`、`SHA256SUMS.txt` を配布しています。1.0.1.0は未署名のため、Windowsで発行元不明やSmartScreenの警告が表示される場合があります。取得ファイルのハッシュを `SHA256SUMS.txt` と照合してください。

```powershell
Get-FileHash .\Kugutsu.Setup.zip -Algorithm SHA256
```

MSIを直接使う場合は必要なx86ランタイムを先に導入し、インストール先を指定します。

```powershell
msiexec.exe /i .\Kugutsu.msi INSTALLFOLDER=E:\Kugutsu
```

インストール時に実行ユーザーのWindows Credential ManagerへMCP資格情報を作成し、そのユーザーのログオン自動起動を登録します。更新時は設定と資格情報を保持します。前提条件、更新、アンインストールの詳細は[インストーラーガイド（英語・一部日本語）](installer/README.md)を参照してください。

### バイナリアーカイブ

`Kugutsu.Setup.zip` はSetup EXEを格納したアーカイブです。展開後にインストーラーを実行してください。ポータブル版のアプリケーションではありません。

### ソースからのビルド

.NET 10 SDKを使用します。リポジトリルートで実行してください。

```powershell
dotnet restore Kugutsu.slnx
dotnet build Kugutsu.slnx -c Release --no-restore
```

出力は `artifacts/Release/bin`、`config`、`dat`、`logs` の構成です。本体の起動には資格情報とランタイムが必要です。WiX Toolset SDK 7.0.0で配布物を作成します。

```powershell
dotnet build installer/Kugutsu.wixproj -p:PayloadRoot=../artifacts/Release -p:ProductVersion=1.0.1
dotnet build installer/KugutsuSetup.wixproj -p:PayloadRoot=../artifacts/Release -p:RuntimePayloadRoot=../artifacts/runtime -p:ProductVersion=1.0.1
```

配布物の検証とランタイム取得は[インストーラーガイド（英語・一部日本語）](installer/README.md)を参照してください。

## 設定

実行時設定はインストール先の `config/appsettings.json` に保存します。既定のMCP接続先は `http://127.0.0.1:47641/mcp` です。設定変更後は本体とプロキシを再起動してください。設定は自動再読込みされません。

| ディレクトリ | 用途 |
| --- | --- |
| `bin` | 実行ファイルと補助ライブラリ |
| `config` | 設定ファイルとUIプロファイル |
| `dat` | OCRモデルなどのデータファイル |
| `logs` | 構造化された実行ログ |

セットアップがインストール済み起動対象とブラウザドライバーを検出し、明示設定を検出値より優先します。MCP資格情報はクライアント設定ではなくWindows Credential Managerから読みます。[設定ガイド](CONFIG.ja.md)、[設定例](config/appsettings.json)、[設定仕様](SPECIFICATION.md#7-設定とログ)を参照してください。

## 利用方法

`Kugutsu.exe`を起動し、[はじめに](#はじめに)の設定例に従って、MCPクライアントに `Kugutsu.McpProxy.exe` をstdioサーバとして登録します。AIへの依頼例：

- 「開いているウィンドウの一覧を見せて」
- 「タイトルが『メモ帳』のウィンドウを撮影して、表示内容を説明して」

接続・初期設定は[MCPセットアップガイド](MCP_SETUP.ja.md)、ほかの依頼例は[利用手順](docs/USAGE.md)、操作の詳細は[MCPコマンドリファレンス](COMMANDS.ja.md)と[製品仕様](SPECIFICATION.md)を参照してください。

## 関連文書

- [文書一覧](DOCUMENTS.ja.md)
- [MCPセットアップ](MCP_SETUP.ja.md)と[設定ガイド](CONFIG.ja.md)
- [MCPコマンドリファレンス](COMMANDS.ja.md)と[パッケージ一覧](PACKAGES.md)
- [製品仕様](SPECIFICATION.md)
- [利用手順](docs/USAGE.md)、[Aloeバッチ](docs/ALOE_BATCH.md)、[UIプロファイル](docs/UI_PROFILES.md)
- [設計判断](docs/adr/)
- [インストーラーと配布物の作成（英語・一部日本語）](installer/README.md)
- [第三者コンポーネント](THIRD_PARTY_NOTICES.md)

## セキュリティ

HTTPサーバはloopbackで待ち受け、Bearerと論理セッションの資格情報を検証します。同梱プロキシに認証とセッション管理を任せてください。資格情報をソース、クライアント設定、コマンド履歴、ログへ記録しないでください。アプリ起動対象とドライバーには設定された検証規則を適用します。

画像と操作結果には他のアプリに表示された情報が含まれる場合があります。入力や結果の共有前に対象を確認してください。取消、ロック期限切れ、緊急停止は、既にアプリへ送った入力を巻き戻しません。ロックとエラー処理は[利用手順](docs/USAGE.md)を参照してください。

## ライセンス

このリポジトリには現在、製品全体のライセンスファイルがありません。第三者コンポーネントは各ライセンスに従います。[第三者コンポーネント](THIRD_PARTY_NOTICES.md)と `dat/licenses`、`dependencies/Aloe` の同梱ライセンスを参照してください。
