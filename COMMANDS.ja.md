# Kugutsu MCPコマンド

[English](COMMANDS.md) | 日本語

英語版は[COMMANDS.md](COMMANDS.md)です。Kugutsuは要求受付・状態照会・取消、ウィンドウとコントロール、ブラウザーとDOM、Aloeバッチ、ロック、シグナルのMCPツールを提供します。

## 要求の流れ

単体操作は `execute_operation`、JSON操作列またはAloeプログラムは `submit_batch` で受け付けます。受付後の要求はFIFOキューへ入ります。`queued` は受付済みを示し、完了を意味しません。`get_request` で最終状態と結果を確認してください。`cancel_request` は協調取消を要求しますが、アプリへ送信済みの副作用を巻き戻しません。

## ツールの分類

| 分類 | 例 | 詳細 |
| --- | --- | --- |
| 接続と探索 | `get_version`、`list_windows`、`ui.list_drivers` | [SPECIFICATION.md](SPECIFICATION.md) |
| 要求処理 | `execute_operation`、`submit_batch`、`get_request`、`cancel_request` | [docs/USAGE.md](docs/USAGE.md) |
| ウィンドウ・コントロール・画面 | window、control、grid、`screen.capture` 系操作 | [SPECIFICATION.md](SPECIFICATION.md) |
| ブラウザーとDOM | `browser.*`、`dom.*` 操作 | [SPECIFICATION.md](SPECIFICATION.md) |
| 複数要求の調整 | `lock_mcp`、`is_mcp_locked`、`renew_mcp_lock`、`unlock_mcp`、signal系ツール | [SPECIFICATION.md](SPECIFICATION.md) |

操作名、入力項目、検証、返却形式、状態遷移、エラーコードの完全な契約は[製品仕様の5節・8節](SPECIFICATION.md#5-mcp-通信とコマンド)を正本とします。契約を重複記載せず、仕様変更時は正本を更新してください。バッチ構文は[Aloeバッチ](docs/ALOE_BATCH.md)を参照してください。
