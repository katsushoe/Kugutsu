# 専用PowerShellコンソールと所属確認プロセス

- Status: 採用（2026-10-13、実機未検証）

## Context

Windowsの既定ターミナルへ委ねると、PowerShellのコンソールHWNDが表示中の入力先と一致するとは限りません。AttachConsoleは呼出しプロセスのコンソール所属を変え、対象コンソールの終了イベントも受けるため、MCP本体へ適用する設計は適切ではありません。

## Decision

新しい設定`ConsoleHostLaunchTargetId`（既定consolehost、null可）に通常のconhost.exeと空の固定引数を許可登録します。専用アダプターが登録済みPowerShellの実行パス・固定引数を`--`以降へ構造化配列で渡します。Windows 11ではForceNoHandoffを指定し、Windowsの既定ターミナル設定を変更せず専用Console Hostを使います。Windows 10ではこのフラグを追加しません。

登録済みシェルはファイル名とPowerShellのファイル説明を確認し、pwsh.exeはPEファイル版のmajor 7を対象とします。この種別情報は内部の対応判定であり、実行したPSVersionTableの取得ではありません。

起動したConsole Hostと一意なPowerShell子孫の世代を照合し、PowerShell自身をapplicationIdへ結び付けます。表示ウィンドウはConsole Hostの同世代参照で保持します。

コンソール所属の確認は同梱の非表示WinExe `Kugutsu.ConsoleProbe.exe`へ分離します。MCP本体はAttachConsoleを呼びません。補助プロセスはPID・開始FILETIME・期待HWNDを受け、対象の開始時刻を確認してAttachConsoleし、GetConsoleWindowと最大1024件のGetConsoleProcessListを照合してFreeConsoleします。入力や終了要求は行いません。

補助プロセスの標準出力は5項目のメタデータJSONだけとし、本体で最大16KiB・深さ4・重複項目・PID・開始時刻・HWND・補助PIDを検証します。シェル以外のクライアントは当該シェルの子孫であることを確認します。所属確認の上限は3秒で、取消・異常終了・コンソール終了競合は入力先不明として拒否します。

## 代替案と不採用理由

- 既定ターミナルへの起動: 専用入力先を確定できません。
- MCP本体でのAttachConsole: 本体のプロセス全体の状態・終了へ影響します。
- 本体が出力を読むConPTY実装: 描画ホストとVT処理まで本機能へ追加する必要があります。

## 影響範囲

PowerShell起動、後続の入力・終了、共通設定検証、補助プロジェクト、ログ管理、配布対象へ適用します。App Execution Aliasや既存の任意コンソールへの接続は行いません。

## セキュリティ条件

許可対象とファイル同一性はADR 0001に従います。同梱補助ファイルと設定は信頼されたインストールを前提とします。補助出力だけで参照を再対応せず、本体のプロセス・ウィンドウ世代も前後確認します。未知のクライアント、PID再利用、別ウィンドウは拒否します。照合と入力はOS上で原子的ではありません。

## 運用条件

ConsoleHostLaunchTargetIdとPowerShellLaunchTargetIdの許可登録、同梱補助exe・dll・deps.json・runtimeconfig.jsonと共通設定が必要です。補助処理のログはkugutsu-console-probe接頭辞で既存の容量・期間管理へ参加します。要求値・コンソール本文は保存しません。

本体が中止・失敗時に終了できるのは、この確認のために起動した補助プロセスだけです。ユーザーのPowerShell・Console Hostを強制終了しません。

## 実装・検証・文書への対応

PowerShellLaunchOperation、ConsoleShellBinding、ConsoleAssociationProbe、Kugutsu.ConsoleProbeを実装します。補助をソリューションと本体ビルドへ接続し、全体ビルド・設定JSONとプロジェクトXMLのパーサー確認を行います。コンソール所属・終了競合・表示先・入力は実機未検証です。仕様書8.4節・設定表、READMEと実装計画へ反映します。

参照: [ConsoleArguments](https://github.com/microsoft/terminal/blob/main/src/host/ConsoleArguments.cpp)、[AttachConsole](https://learn.microsoft.com/en-us/windows/console/attachconsole)、[GetConsoleProcessList](https://learn.microsoft.com/en-us/windows/console/getconsoleprocesslist)。
