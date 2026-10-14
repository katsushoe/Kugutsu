# Kugutsu 設定ガイド

[English](CONFIG.md) | 日本語

実行時設定はインストール先の `config/appsettings.json` に保存します。[Git管理されている設定例](config/appsettings.json)に既定値を示します。英語版は[CONFIG.md](CONFIG.md)です。

## 読込みと検証

- 起動時にJSON内の `Kugutsu` オブジェクトを読み込みます。変更後は本体とstdioプロキシを再起動してください。実行中の自動再読込みはありません。
- UTF-8 JSONを使用します。未知のキー、大文字小文字だけが異なる重複キー、型違い、不正値がある場合は起動しません。
- `ServerUrl` はloopbackのHTTP絶対URLに限ります。`UiProfileDirectory` はインストール先の配下へ解決される必要があります。
- `AllowedLaunchTargets` は明示的な許可リストです。空配列は有効ですが、任意の実行ファイル起動を許可しません。
- MCP資格情報はWindows Credential Managerに保存します。このファイルやMCPクライアント設定へコピーしないでください。

## 設定の分類

| 分類 | 例 | 用途 |
| --- | --- | --- |
| MCP接続と要求上限 | `ServerUrl`、`QueueCapacity`、`MaxRequestBytes`、`OperationTimeoutSeconds` | ローカル接続と要求処理の上限 |
| ロックとシグナル | `McpLockLeaseSeconds`、`MaxSignals`、`SignalRetentionSeconds` | 複数要求間の排他と条件監視 |
| UIとOCR | `EnabledUiDrivers`、`UiProfileDirectory`、`OcrLanguage`、`MaxUiCandidates` | UI探索とローカル画像処理 |
| アプリとブラウザー | `AllowedLaunchTargets`、`BrowserLaunchTargets`、`BrowserDriverPaths` | 許可する起動対象とドライバー |
| ログ | `LogLevel`、`LogRetentionDays`、`LogFileSizeMiB`、`LogTotalSizeMiB` | 構造化ログと保存期間 |

全キーの型、既定値、範囲、検証条件は[製品仕様の7節](SPECIFICATION.md#7-設定とログ)を参照してください。設定変更時は仕様とサンプルを同時に更新し、この表へ詳細を重複記載しません。
