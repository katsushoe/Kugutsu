# Windows Terminalの管理シェルと入力先

- Status: 採用（2026-10-13、実機未検証）

## Context

Windows Terminalは複数タブ・ペインを持ち、起動プロセスとシェルPIDも異なります。ウィンドウやタブのタイトルだけで既存シェルへ接続すると、入力先と終了対象を確定できません。

## Decision

`TerminalLaunchTargetId`は許可登録されたWindows Terminalの通常実行ファイルを指定し、固定引数を空にします。専用アダプターが新規ウィンドウ、新規タブ、ランダム識別タイトル、アプリによるタイトル上書き抑止を指定します。シェルは`PowerShellLaunchTargetId`の登録済み実行パスと固定引数を使います。

シェル実行パス・固定引数にセミコロンがある場合は、Terminalの複数コマンド区切りとの混同を避けるため起動前に拒否します。任意のシェルプロファイルや既存タブをMCP引数から選択させません。

直接起動したTerminalのプロセス世代と一意な表示ウィンドウを照合します。プロセススナップショットの親子関係、各世代の開始時刻、登録済みシェルの絶対イメージパス・SID・セッションを確認し、一意な子孫だけを管理参照へ登録します。

UIAでは識別タイトルを持つ一意な選択タブと、同じ開始タイトルを持つ`TermControl`の入力ペインを確認します。RuntimeId、同じウィンドウ配下、タブ選択、入力先の有効状態・フォーカス能力を保持し、後続操作で再照合します。非表示を含め、UIAツリーに複数の入力ペインがあれば拒否します。

タブ終了は、保持したTabItem配下で一意に特定できるButton／AutomationId=`CloseButton`のInvoke経路だけを使います。この識別条件の実機適合は未検証で、条件不一致・未対応時に別経路へ切り替えません。Invoke直前にも同じタブ配下であることを照合し、他タブを含むウィンドウの閉鎖や未確認のexit入力は行いません。

## 代替案と不採用理由

- 最新ウィンドウ・先頭タブへの接続: 他のシェルへの誤入力を排除できません。
- タイトルだけのPID探索: 他プロセスや世代の違いを検出できません。
- Terminalの既定プロファイルに依存: 起動するシェルの実行パスとPIDを確定できません。

## 影響範囲

Terminal起動、セッション参照、後続のキー送信・タブ終了に適用します。汎用`app.launch`は固定引数のみの契約を維持し、Terminal専用の引数構築は内部アダプターだけで行います。プロセス照合・ウィンドウ待機は既存部品を共用します。

## セキュリティ条件

起動対象の許可と初回固定はADR 0001に従います。直接起動プロセスが短命な転送役になる場合や、シェルが別のブローカー配下になり関係を証明できない場合は接続しません。既存プロセスの暗黙採用や別タブへの再対応はありません。

## 運用条件

Windows Terminalの通常.exeとPowerShellの許可登録、uiaドライバが必要です。App Execution AliasはADR 0001の基盤では未対応です。WSL・任意既定プロファイル・複数ペインはこのアダプターの対象外です。起動後の失敗・取消でもアプリを強制終了したり、自動再起動したりしません。

## 実装・検証・文書への対応

`TerminalLaunchOperation`、`ManagedProcessTree`、`TerminalShellBinding`を実装し、`ShellSessionRegistry`へ登録します。ビルドと仕様・ソースの読取りのみを確認し、OS版・Terminal版・UIA・PID転送・タブ／ペイン競合の実機検証はユーザーの指示後に行います。仕様書8.4節とREADMEに入力・出力・エラー・前提を記録します。

参照: [Terminalコマンドライン](https://learn.microsoft.com/en-us/windows/terminal/command-line-arguments)、[TermControlAutomationPeer](https://github.com/microsoft/terminal/blob/main/src/cascadia/TerminalControl/TermControlAutomationPeer.cpp)。
