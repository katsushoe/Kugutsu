# Kugutsu MCPセットアップ

[English](MCP_SETUP.md) | 日本語

英語版は[MCP_SETUP.md](MCP_SETUP.md)です。詳しい操作例と問題の切り分けは[利用手順](docs/USAGE.md)を参照してください。

## 本体を起動する

Kugutsuをインストールし、`Kugutsu.exe` を起動します。本体はWindowsのタスクトレイに常駐し、ローカルMCPエンドポイントを提供します。本体とプロキシは同じWindowsユーザーで実行してください。

## stdioプロキシを登録する

MCPクライアントに同梱プロキシを登録します。必要に応じてクライアントの設定形式に合わせてください。

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

プロキシはインストール先の設定とWindows Credential Managerから接続先・資格情報を読み込みます。Bearer資格情報をクライアント設定へ書かないでください。保存後、クライアントを再起動するかMCPサーバ一覧を再読込みします。

## 接続を確認する

`get_version` を呼び出してMCP接続を確認し、続けて `list_windows` で利用可能なウィンドウを調べます。接続できない場合は、本体が起動していること、プロキシのパスが正しいこと、両プロセスが同じWindowsアカウントで実行されていることを確認してください。接続先設定は[設定ガイド](CONFIG.ja.md)、詳細な切り分けは[利用手順](docs/USAGE.md)を参照してください。
