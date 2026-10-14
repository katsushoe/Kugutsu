# Kugutsu

Windows上のウィンドウをAIから操作するC#製MCPサーバです。

## 現在の実装範囲

実装済み範囲はPhase 1～5とP06-01～P06-09（ウィンドウ参照レジストリ、一覧、現在情報、座標変換、通常サイズへの復元、前面化・フォーカス確認、最小化、最大化、通常終了）、P07-01～P07-08（単一クリック・ダブルクリック・縦横ホイール・キー送信・Unicode入力・固定時間待機・ウィンドウ／全モニター画像取得）、P08-01～P08-06（UIドライバ契約・登録と能力照会・要素参照・UIA基盤・条件探索・構造取得）、P09-01～P09-14（UIA状態・値取得、Invoke、値・キー・選択・チェック・数値・展開・スクロール・複数選択・テキスト範囲・グリッド操作）、Phase 10（アプリ・ターミナル・PowerShell）、P11-01～P11-10（ブラウザ照会、WebDriver共通契約、Chrome/Edge/Firefox起動、遷移、戻る・進む、終了）、Phase 12（DOM探索・失効・入力・クリック・キー・値取得）、Phase 13（シグナル管理・全7種類の監視・JSON signalKey）、Phase 14（ローカルOCR・形状/ラベル/意味推定・両画像ドライバ・画像参照再検証・フォーカス表示確認・画像操作・Craywork識別プロファイル）、P15-01～P15-08（固定依存、隔離コンパイル・実行、共通操作、制限、取消、Aloeバッチ受付）です。実装状況と残件は[進捗](PROGRESS.md)および[実装計画](TODO.md)、外部契約は[仕様書](SPECIFICATION.md)を参照してください。
本体は設定・資格情報を読み、HTTP待受を維持します。プロキシは標準入出力とHTTPのMCPメッセージを中継します。
公開済みMCPツールはget_version、execute_operation、submit_batch、get_request、cancel_request、lock_mcp、is_mcp_locked、renew_mcp_lock、unlock_mcp、list_windows、get_window、register_signal、wait_for_signal、unregister_signalの14ツールです。操作登録表にはwindow.list、window.get、window.restore、window.activate、window.minimize、window.maximize、window.close、mouse.click、mouse.double_click、mouse.scroll、keyboard.press_key、keyboard.type_text、wait、screen.capture、screen.capture_desktop、ui.list_drivers、control.find、ui.snapshot、control.get_state、control.get_value、control.invoke、control.set_value、control.send_keys、control.select、control.set_toggle、control.set_range_value、control.expand_collapse、control.scroll、control.select_items、control.set_text_selection、grid.get_cell、grid.set_cell_value、app.launch、window.open、app.exit、terminal.launch、terminal.send_keys、terminal.exit、powershell.launch、powershell.send_keys、powershell.exit、browser.get_default、browser.list_installed、browser.launch、browser.navigate、browser.back、browser.forward、browser.exit、dom.find、dom.fill、dom.click、dom.send_keys、dom.get_value、register_signal、wait_for_signal、unregister_signalを接続済みです。

### 操作ディスパッチ基盤

`OperationDispatcher`は起動時の登録一覧を固定し、`{"name":"操作名","args":{}}`を検証します。
未知の操作、必須項目不足、未知・重複プロパティ、型・範囲・列挙値の不一致は`INVALID_ARGUMENT`として拒否します。
引数スキーマはオブジェクト、文字列、整数、有限数、真偽値、配列の入れ子に対応します。
登録した追加検証で引数間の条件も確認でき、検証時にハンドラーは実行しません。
検証済み引数は元のJSON文書から複製し、同じ登録表のハンドラーへ渡します。入力値はエラー文や診断へ出しません。
操作登録表にはwindow.list、window.get、window.restore、window.activate、window.minimize、window.maximize、window.close、mouse.click、mouse.double_click、mouse.scroll、keyboard.press_key、keyboard.type_text、wait、screen.capture、screen.capture_desktop、ui.list_drivers、control.find、ui.snapshot、control.get_state、control.get_value、control.invoke、control.set_value、control.send_keys、control.select、control.set_toggle、control.set_range_value、control.expand_collapse、control.scroll、control.select_items、control.set_text_selection、grid.get_cell、grid.set_cell_value、app.launch、window.open、app.exit、terminal.launch、terminal.send_keys、terminal.exit、powershell.launch、powershell.send_keys、powershell.exit、browser.get_default、browser.list_installed、browser.launch、browser.navigate、browser.back、browser.forward、browser.exit、dom.find、dom.fill、dom.click、dom.send_keys、dom.get_value、register_signal、wait_for_signal、unregister_signalを実装済みです。今後の操作も同じ登録表へ接続します。
実行時の対象・権限確認は各ハンドラーで行います。

`OperationRequestQueue`は容量制限付きの単一ワーカーFIFOです。単体要求・JSONバッチの事前検証、所有者照合、重複ID判定、結果保持、取消、要求・操作の期限、排他ロックの取得・更新・解放・失効を扱います。window.list、window.get、window.restore、window.activate、window.minimize、window.maximize、window.close、mouse.click、mouse.double_click、mouse.scroll、keyboard.press_key、keyboard.type_text、wait、screen.capture、screen.capture_desktop、ui.list_drivers、control.find、ui.snapshot、control.get_state、control.get_value、control.invoke、control.set_value、control.send_keys、control.select、control.set_toggle、control.set_range_value、control.expand_collapse、control.scroll、control.select_items、control.set_text_selection、grid.get_cell、grid.set_cell_value、app.launch、window.open、app.exit、terminal.launch、terminal.send_keys、terminal.exit、powershell.launch、powershell.send_keys、powershell.exit、browser.get_default、browser.list_installed、browser.launchを登録済みです。後続操作は個別の実装項目で登録します。

| プロジェクト | 出力 | 用途 |
| --- | --- | --- |
| `src/Kugutsu.App` | `Kugutsu.exe` | Windows Forms本体。ASP.NET Coreのフレームワークを参照 |
| `src/Kugutsu.McpProxy` | `Kugutsu.McpProxy.exe` | stdioプロキシ用の実行プロジェクト |
| `src/Kugutsu.Contracts` | `Kugutsu.Contracts.dll` | 本体とプロキシから参照する共通応答・状態・エラー型 |
| `src/Kugutsu.AloeHost` | `Kugutsu.AloeHost.exe` | 隔離Aloeコンパイル・VM実行と私有RPCの内部補助 |
| `src/Kugutsu.ConsoleProbe` | `Kugutsu.ConsoleProbe.exe` | PowerShellのコンソール所属を確認する非表示の内部補助。入力・終了要求はしない |

両実行プロジェクトは共通契約に依存し、相互には依存しません。
共通応答型は`src/Kugutsu.Contracts`に実装しています。構造化ログは両実行プロジェクトにリンクした
`src/Shared/StructuredLog.cs`を使用します。

- P10-01 起動許可レジストリ：設定のコピーを固定し、実行ファイルの同一性・SHA-256・再解析ポイントと最終パスを利用時に照合する基盤をDIへ登録しました。起動APIはP10-02です。

- P10-02 app.launch：登録対象を固定引数で起動し、同一SID・セッション・実行パスとPID・開始時刻を確認してapplicationIdを発行します。

- P10-03 window.open：app.launchを再利用して同じPID・開始時刻の一意な表示ウィンドウを待ち、normal／maximizedを確認します。

- P10-04 app.exit：管理アプリと主ウィンドウの世代を照合して通常終了を要求し、保持プロセスハンドルの終了を確認します。

- P10-05 シェル参照レジストリ：専用sessionIdを管理アプリ・シェルプロセス・ウィンドウ・不透明入力先へ固定し、種類と世代を前後照合する基盤をDIへ登録しました。

- P10-06 terminal.launch：新規識別タブを持つWindows Terminalを起動し、登録済みPowerShellの一意な子孫PIDとUIA入力ペインを照合して専用sessionIdを発行します。

- P10-07 terminal.send_keys：セッションと入力フォーカスを再照合し、文字列の各送出区間・各キー直前にタブとペインを確認して送信します。Enterは追加しません。

- P10-08 terminal.exit：保持した識別タブ配下の閉じるボタンを一意に特定して通常終了を要求し、シェルPIDの終了確認後に専用sessionIdを破棄します。

- P10-09 powershell.launch：許可登録した専用conhostとPowerShellを起動し、別プロセスでコンソール所属を確認してPowerShell種別のsessionIdを発行します。

- P10-10 powershell.send_keys：管理PowerShellセッションへ文字列と明示キーを順番に送信し、種別・プロセス世代・コンソール所属・フォーカスを再照合。

- P10-11 powershell.exit：専用Console Hostへ通常終了を要求し、管理PowerShellプロセスの終了確認後にセッションを削除。

### シグナル基盤

Phase 13を完了しました。所有者・取得ロックに結び付けた登録・待機・解除・寿命管理と、window.state/window.opened/control.state/control.text/dom.state/dom.text/browser.urlを接続しました。JSONバッチ内のsignalKey参照は受付前に検証します。P14-01のローカルOCR基盤も接続済みで、画像ドライバの公開は後続です。実機未検証です。

### ブラウザ操作

P11-01～P11-10を実装済みです。起動は専用プロファイルと許可済みWebDriverを使います。対応版・通信制約・失敗時に残るプロセスとプロファイルはdocs/adr/0004-managed-webdriver-sessions.mdを参照してください。履歴移動には必要なBiDi能力がある版が必要です。実機未検証です。

- P13-01 シグナル登録表：所有セッション・ロック取得世代・期限・状態・一回限りの成立結果を保持する登録基盤を追加。

- P13-02 register_signal：MCPと共通操作へ監視登録を接続。所有者・ロック世代を引き継ぎ、監視開始・初期評価を確認。

- P13-03 wait_for_signal：単体MCPと共通操作へFIFO待機を接続し、成立・通常timeout・要求中断を区別。

- P13-04 unregister_signal：所有者の監視解除をMCPと共通操作へ接続し、未成立待機へ解除理由を通知。

- P13-05 シグナル寿命管理：HTTP要求がなくても200ms周期で期限・ロック世代を整理し、成立済み監視を停止。開始中の解除も資源生成完了を待って解放。

- P13-06 window.state監視：固定したウィンドウ世代の存在・アクティブ・最小化・最大化を設定間隔で観測し、消失待ちは成立を先に評価。

- P13-07 window.opened監視：WinEventの作成・表示通知を専用メッセージループで受信し、登録前のウィンドウを除いて条件に一致した参照を返す。

- P13-08 control.state監視：UIAの有効・表示・フォーカス・トグル・選択状態をMTAで観測し、固定した要素参照の失効を検出。

- P13-09 control.text監視：UIA Value/Textパターンの一致・包含を監視し、パスワード・不明な機密属性・長すぎる値を拒否。本文を観測結果へ保存しない。

- P13-10 dom.state監視：登録時の文書・フレームを固定し、その中でCSSを都度検索。背景監視と通常操作のWebDriver選択状態を操作全体で排他。

- P13-11 dom.text監視：固定した文書内で一意なCSS要素の表示テキストを比較し、入力値を読まず、本文を観測結果へ保存しない。

- P13-12 browser.url監視：登録済みタブの現在URLを設定間隔で比較し、遷移を行わず、観測結果には一致結果と文字数だけを保存。

- P13-13 JSON signalKey参照：同一要求内の名前を登録結果のsignalIdへ結び付け、重複・未定義・前方参照・二重指定を受付前に拒否。単体シグナル操作にも所有者とロック世代を引き継ぐ。

- P14-01 ローカルOCRアダプター：Tesseract 5.2.0 x86と固定SHA-256の日本語・英語モデルを接続し、メモリ内PNGから単語・矩形・OCR信頼度を返す基盤を実装。

- P14-02 入力欄の形状検出：メモリ内PNGから矩形・入力欄候補を抽出し、形状根拠とrule_score、上限打切りを返す基盤を実装。

- P14-03 ボタンの形状検出：ボタン候補の矩形規則を接続し、形状根拠とrule_scoreを保持。入力欄と重なる分類候補を暗黙に一意化しない。

- P14-04 ラベル関連付け：OCRの行・単語候補を入力欄の左・上、ボタン内部へ対応づけ、複数対応と信頼度の種類を保持。

- P14-05 プロファイル読込み：対象アプリ・画面・ラベル・意味規則を厳密なJSONとして読込み、不正定義を拒否して版と内容SHA-256を保持。

- P14-06 画面識別：プロセス・タイトル・周辺OCRのリテラル条件を照合し、一致した画面と根拠を複数保持。

- P14-07 意味判定：形状・ラベル・一致画面の明示規則から用途を別属性へ付け、複数解釈と未確定属性を保持。

- P14-08 imgui_visualドライバ：Test Engineを使わない画像・OCR・明示規則のドライバを登録し、期限と128MiB上限のフレーム表から共通候補を生成。

- P14-09 generic_visualドライバ：一般的な独自描画UI向けドライバを登録し、画像基盤を共有してドライバ別のプロファイル・参照を維持。

- P14-10 ui.snapshot（画像）：ui.snapshotへ画像ドライバと任意profileIdを接続し、候補上限・includeImage・不明状態を適用。

- P14-11 control.find（画像）：画像ドライバの名前・種類・profileId・semanticRole検索を公開し、automationIdを受付前に拒否して候補一覧を返す。

- P14-12 画像参照の再検証：期限・世代・DPI・矩形・プロファイルSHA-256・局所BGRA画素と5点ヒット先を照合し、変更時に再対応しない。

- P14-13 画像フォーカス確認：プロファイルのborder_colorを対象の四辺で照合し、50ms隔てた2回の画像でフォーカス表示を確認。未定義・不足・参照失効時はUI_TARGET_UNVERIFIED等で拒否し、確認処理自体はクリックやキーを送らない。

- P14-14 control.invoke（画像）：画像ボタンの明示coordinate_clickを共通control.invokeへ接続。プロファイル付きボタンだけ許可し、移動前とクリック直前に局所画素と参照を照合、変更なら送信を拒否。

- P14-15 control.set_value（画像）：画像入力欄はcoordinate_keyboardとallowTextInput/focusIndicatorを必須にし、単一クリック後の各キーとUnicode送信前にフォーカス表示・参照・利用者押下を照合する。Ctrl+AとDeleteで置換し改行等制御文字を拒否。

- P14-16 control.send_keys（画像）：画像入力欄へ明示coordinate_keyboardでキー列を送る経路を接続。許可付きプロファイルと各キー直前のフォーカス表示・参照照合を必須にし、入力後は画像参照を失効。

- P14-17 control.get_value（画像）：明示local_ocrとsensitive:falseの入力欄だけ、枠を除く内部画像からOCR値と単語・信頼度を返す。読取り前後の画素一致を確認し、空欄・打切り・複数行は確認不能として拒否。

- P14-18 Craywork用プロファイル：CrayworkソースのOpen Project/Path/Cancelを根拠に右側ラベルの識別規則をKugutsu内へ配置。フォーカス表示未確認のため入力許可はfalse、ビルドは既存設定を上書きしない。

- P15-01 Aloe依存版固定：Aloeの3アセンブリ1.0.0.0とSHA-256を固定し、MITライセンスと同梱。読取りロック・リパース拒否・サイズ・名前・版・ハッシュ照合の依存リースをDIへ登録。Aloeバッチはまだ無効。

- P15-02 コンパイルアダプター：固定AloeCompilerを内部AloeHost補助プロセスで使用し、Moduleは子のメモリへ保持。親はソース上限、私有フレーム、期限、取消時の終了確認、字句・構文エラーの固定コードを扱う。

- P15-03 実行アダプター：コンパイル済みModuleを同じ内部補助プロセスの固定AloeVmで一度だけ実行。一般用途入出力・Timer能力・Aloeログを無効化し、取消・要求期限・失敗時の終了確認を必須にする。

- P15-04 共通操作APIの公開：Aloeのexecute_operation(name,argsJSON)を親の操作ディスパッチへ接続。子へ所有者やロックトークンを渡さず、既存引数スキーマ・所有文脈・操作期限を適用する。

- P15-05 実行制限：命令予算・ホスト呼出し数・要求期限・私有フレーム上限とWindows Jobのメモリ上限を接続。超過位置を返し、観測を無効化するGCSetDebug/並行tickを拒否する。

- P15-06 Aloe取消：Aloeのコンパイル・実行・停止確認を1件のFIFO要求へ結び付け、要求期限を全体に適用。操作ごとの期限と所有者ロックの前後照合を維持し、停止未確認のまま次要求へ進めない。

- P15-07 Aloeバッチ受付：submit_batchのformat:aloeとsourceを有効化。JSONとの同時指定、ソースの範囲・Unicode、固定依存、要求ID・所有者・ロックを検証し、隔離実行全体を1件のFIFOへ登録。

- P15-08 Aloeシグナル連携：AloeへシグナルIDを返し、待機結果をsignaled／timeout文字列として条件分岐と解除に使えるホスト関数を接続

- P16-01 配布マニフェスト：固定構成のアプリ・プロキシ・ConsoleProbe・AloeHost・Aloe依存物を版とSHA-256で照合してinstallation.jsonを生成


## MSIインストーラー

WiX 7.0.0を使うx86 MSIとBurnセットアップ定義を[installer](installer/README.md)に追加しました。`Kugutsu.Setup.exe`は不足しているx86 .NET 10ランタイムを先に導入してからMSIを実行します。標準のbin/config/dat/logs配置へ導入でき、既定のインストール先はE:\Kugutsuです。インストール・公開は行っていません。

- P16-02 WiX MSI：WiX 7のx86 MSIでbin/config/dat/logsを標準配置へ収録しINSTALLFOLDER変更を可能にした

- P16-03 MCP資格情報初期生成：MSIから対話的インストールユーザーのCredential Managerへ暗号学的乱数Bearer tokenを欠落時だけ作成し有効値を保持

- P16-04 ユーザー単位ログオン起動：MSIのHKCU RunへKugutsu.exeを登録し導入ユーザーの対話ログオン時にトレイ常駐を起動

- P16-05 launch_target_detection：MSIセットアップが実在するTerminal／PowerShell／conhost／Firefox／Chrome／Edgeだけを検出設定へ記録し、手動設定を優先

- P16-06 webdriver_distribution：公式配布元からブラウザー版・OSアーキテクチャに合うWebDriverをセットアップ時に取得し署名を検証、版とSHA-256を記録

- P16-07 OCRモデル配布：MSI作成時に固定版OCRモデル・x86ネイティブDLL・ライセンスを実行時固定値と照合しinstallation.jsonへ記録

- P16-07 ocr_model_distribution：固定版Tesseractモデル2件・x86ネイティブライブラリ2件・モデルライセンスをSHA-256照合しinstallation.jsonへ記録、欠落時はMSIビルドを失敗

- P16-08 dotnet_runtime_detection：32ビットレジストリビューと実フォルダーを照合しx86 Microsoft.NETCore.App／WindowsDesktop.App／AspNetCore.Appの10.0.x不足を構造化判定

- P16-09 Burnランタイム導入：Kugutsu.Setup.exeへ公式x86 .NET 10 Desktop／ASP.NET CoreランタイムをSHA-512とAuthenticode検証後に同梱し、不足分だけMSIより先に導入

- P16-10 更新・アンインストール：MSI更新時はappsettings.jsonと資格情報を保持し、製品ファイル／ログオン起動登録を削除。セットアップ生成のドライバー・検出ファイルを清掃し、共有ランタイムとログは残す

- P16-09 burn_runtime_bundle：公式メタデータのSHA-512とMicrosoft署名を検証したx86 .NET 10 Desktop／ASP.NET Core RuntimeをセットアップEXEへ同梱し、不足分をMSI前に導入。MSI失敗時もセットアップを停止

- P16-10 installation_lifecycle：MSI preserves user settings and credentials during upgrade or uninstall, removes application components and startup registration, cleans setup-generated drivers and detection data, and leaves shared runtimes intact

## ビルド

.NET 10 SDKを使用します。対象は`net10.0-windows`、`win-x86`、フレームワーク依存型です。

```powershell
dotnet build src/Kugutsu.App/Kugutsu.App.csproj
dotnet build src/Kugutsu.McpProxy/Kugutsu.McpProxy.csproj
```

全体のビルドは`dotnet build Kugutsu.slnx`です。コンパイラ警告はエラーとして扱います。
SDKは開発時に必要です。配布先のランタイム導入はKugutsu.Setup.exeが行います。

## 標準配置の解決

両実行ファイルは`artifacts/Debug/bin`（Releaseでは`artifacts/Release/bin`）へ出力します。
本体とプロキシは`AppContext.BaseDirectory`のbinディレクトリの親をインストール先とし、
そこから`bin`、`config`、`dat`、`logs`、`config/appsettings.json`を解決します。
カレントディレクトリや固定ドライブには依存しません。配置を変更するときもbin構成を維持してください。

共有処理は`src/Shared/InstallationPaths.cs`を両実行プロジェクトへリンクしています。
共通契約ライブラリに実行環境の処理を持ち込まず、同じパス解決規則を使います。
明示的なインストール先からの解決も可能ですが、相対パスは受け付けません。
配置解決はパスの算出だけを行います。logsの作成とログファイルへの接続は構造化ログ処理が担当します。

## 設定の読込み

設定の正本はインストール先の`config/appsettings.json`です。[配布用設定](config/appsettings.json)の
`Kugutsu`セクションを`KugutsuOptions`へ読み込みます。本体とプロキシは同じ読込み処理を使います。
環境変数・カレントディレクトリの設定は読み込まず、実行中の自動再読込みも行いません。
変更は再起動時に反映します。プロキシでは`--url`で接続先だけを上書きできます。

数値・ログレベル・論理的な対象IDなどの省略値はモデルの既定値を使用します。
接続URL・プロファイル配置先など環境に依存する値はJSONで指定します。
AllowedLaunchTargetsの各要素は`Id`、`ExecutablePath`、`Arguments`（文字列配列）です。
`ConsoleHostLaunchTargetId`は専用PowerShellコンソールの登録済みconhost.exeを指定します（既定consolehost、null可）。Console Hostの固定引数は空とします。PowerShellの登録と同梱ConsoleProbeが必要です。設計と配布前提は[ADR 0003](docs/adr/0003-dedicated-powershell-console.md)を参照してください。
補助のログ接頭辞は`kugutsu-console-probe`で、本体・プロキシと共通の保持規則を適用します。補助出力は所属確認メタデータだけです。
BrowserLaunchTargetsとBrowserDriverPathsはブラウザ種類をキーとするJSONオブジェクトです。

開発ビルドは`artifacts/<Configuration>/config/appsettings.json`がない場合だけ配布用設定をコピーします。
既存ファイルを更新する場合は手動で編集してください。実行時には設定を自動生成しません。
設定ファイルの欠落・アクセス拒否・JSON読込み／型変換の失敗では非0で終了し、設定本文をログへ出しません。
設定の型・値域・必須値・相互条件・未知／重複キーを起動時に検証します。
数値はJSON整数として指定し、数字の文字列やnullは使用できません。
ServerUrlはloopbackのHTTP絶対URL、UiProfileDirectoryはインストール先配下の相対パスです。
要求タイムアウトは操作タイムアウト以上、ログ総容量は1ファイル上限以上としてください。
起動対象が空の場合、ターミナル／PowerShellは機能未設定として扱います。
明示したブラウザ起動先は登録済みIDが必要です。その他の制約は[仕様書の設定とログ](SPECIFICATION.md#7-設定とログ)を参照してください。

## 構造化ログ

ログはインストール先の`logs`へ、UTF-8 JSON Lines形式で保存します。
本体は`kugutsu-<起動時刻>-<ランダムID>.jsonl`、プロキシは`kugutsu-proxy-<起動時刻>-<ランダムID>.jsonl`です。
実行インスタンスごとに新規ファイルを作成し、毎行フラッシュします。LogLevelで出力を制御します。
ログファイルを作成・書込みできない場合は終了コード1で停止します。
ファイルを使えない場合の診断先は本体がTrace、プロキシが標準エラーです。

`ILogger.WriteEvent`は要求ID（Guid）、イベント・操作カテゴリ・成否・診断コード（列挙値）、
操作番号・所要時間、ソースファイル名・行番号だけを受け取ります。
入力本文・画像・トークン・任意メッセージ・例外本文を保存しません。
現在は起動・終了・起動失敗の処理に接続済みです。要求・操作ログは後続のハンドラーから呼び出します。
LogFileSizeMiBを超える直前とUTC日付が変わった後の最初の書込みで、新しいファイルへ切り替えます。
LogRetentionDaysを超えたログとLogTotalSizeMiBを超過させる古いログを削除します。
対象はlogs直下の本体・プロキシの管理対象ファイルだけです。使用中のファイルと再解析ポイントは削除しません。
起動時・設定反映時・書込み時・1分ごとの定期処理で整理します。プロセス間の排他取得は最大5秒待機します。
使用中ファイルなどにより上限を守れない場合は、診断を出して終了コード1で停止します。

## HTTPホスト

本体はServerUrlのloopbackアドレスとポートで待ち受けます。localhostはIPv4／IPv6のloopbackです。
公式MCP SDK 2.2.0でStreamable HTTPルートを登録し、URLのパスはASCII英数字と`/-._~`に限ります。
Host・Originを検証し、不一致は403です。環境変数による待受先の上書きはありません。
ポート競合はHttpPortInUse、その他の起動失敗はHttpHostStartFailedを記録し、終了コード1となります。
全要求でAuthorizationヘッダーのBearerトークンを検証し、一致した要求だけMCPへ渡します。
未指定・重複・形式不正・値の不一致は401（WWW-Authenticate: Bearer）です。
クエリや本文のトークンは認証に使用しません。トークンと認証ヘッダーはログに出力しません。
初期資格情報がない環境では、待受を開始する前に停止します。

## 版照会

認証後は`get_version`を引数なしで呼び出せます。共通応答のdataに`name`、`version`、`protocolVersion`を返します。
製品版はビルド時の情報（現在の製品版1.0.1.0）、MCP版はSDKが扱う接続のプロトコル版です。
操作バッファやロックは不要で、structuredContentとJSONテキストの両方を返します。

## MCP資格情報

本体とプロキシは実行ユーザーのWindows Credential Managerから、
汎用資格情報`Kugutsu/McpBearerToken`を起動時に読みます。
値はBOM・終端NULなしのUTF-16LEとし、空文字・空白・改行・不正なBearer文字を拒否します。
未登録はCredentialMissing、形式不正はCredentialInvalid、取得不能はCredentialUnavailableをログに記録し、
ダイアログなしで終了コード1となります。トークン値をログへ出しません。
更新の反映には本体とプロキシの再起動が必要です。

資格情報の初期生成・保存はP16-03で実装します。
現在の実行ファイルは資格情報を自動生成しないため、未登録環境では起動を停止します。

## stdioプロキシ

本体を起動した状態で、AIクライアントのMCP起動コマンドに`bin/Kugutsu.McpProxy.exe`を指定します。
接続先は`Kugutsu:ServerUrl`です。`--url <URL>`または`--url=<URL>`を1回だけ指定すると引数が優先されます。
例：`Kugutsu.McpProxy.exe --url http://127.0.0.1:47641/mcp`
loopbackのHTTP URLだけを許可し、資格情報・クエリ・フラグメント付きURLは拒否します。
不明・重複・値不足の引数はConfigurationInvalidと終了コード1になり、標準出力へ診断を出しません。
設定ファイルは引き続き必須です。上書きはメモリ内だけで行い、設定ファイルを変更しません。
SDK 2.2.0が改行区切りJSON-RPCを読み、HTTPのJSON／SSE応答を標準出力へ返します。
Bearerを付与し、HTTPリダイレクト・外部HTTPプロキシ・Cookieは使いません。
標準出力はMCP専用です。診断はログファイル、ファイルが使えない場合は標準エラーへ出します。
標準入力EOFまたはCtrl+Cで終了します。認証失敗・設定不正・再接続期限超過は固定診断と終了コード1です。
HTTPで返ったJSON-RPCエラーをSDKが例外へ変換した場合は、同じ要求IDとエラーコード、固定メッセージを返します。
一時的な通信障害では、操作本文を含まないOPTIONS接続確認を指数バックオフ（基準1秒から倍増、ジッター込み最大60秒）で再試行します。回復周期全体の期限はRequestTimeoutSeconds、各確認の期限は最大30秒です。同時障害は1周期にまとめ、新しいメッセージは回復を待ってから1回だけ送ります。
送信を開始した要求が失敗した場合は、同じJSON-RPC IDにエラー-32001、data:{outcome:"unknown",replayed:false}を返します。回復待機の期限超過で未送信なら-32002、outcome:"not_sent"です。失敗した要求・通知は自動再送しません。結果不明の操作は、状態照会機能の実装後に操作要求IDで確認します。
OPTIONSの2xx／405は接続先への到達確認です。その後、再開資格情報を使って既存所有者を確認し、成功してから新しい送信を再開します。MCP操作の成功を意味しません。401／403は再試行せず停止します。失効した所有者やMCP HTTPセッションを新規発行・自動再初期化で置き換えません。テスト・実機での再接続確認は未実施です。

## 論理クライアントセッション

プロキシは最初のMCPメッセージを送る前に、MCP URLの末尾へ`/sessions`を付けた専用HTTPエンドポイントへ共有Bearer付きの空POSTを1回送ります。本体は独立した所有者ID、通常要求用資格情報、再開資格情報を201の非キャッシュJSONで返します。この応答をstdioクライアントへ転送しません。
通常のMCP要求には`X-Kugutsu-Session-Id`と`X-Kugutsu-Session-Credential`を付けます。本体は両方を照合するため、共有Bearerや自己申告IDだけでは既存所有者として操作できません。MCPの`Mcp-Session-Id`とは別の識別子です。直接HTTP接続するクライアントにも発行とヘッダー付与が必要です。
プロキシは資格情報をメモリに保持し、本体はSHA-256ハッシュを保持します。ディスク・ログ・MCP結果へ保存しません。本体終了で全セッションが失効します。現在は発行上限`MaxClientSessions`（既定1000、1～10000）まで本体の寿命中に保持し、超過は503です。発行失敗は`McpSessionFailed`または認証エラーで停止し、結果不明の発行POSTを自動再送しません。
再接続時は`/sessions/resume`へ共有Bearer、`X-Kugutsu-Session-Id`、`X-Kugutsu-Session-Resume`を付けた空POSTを送ります。本体は既存所有者の再開ハッシュを照合し、200の`{sessionId,resumed:true}`を返します。プロキシはIDの一致まで確認します。通常資格情報・再開資格情報は変更せず、セッション数も増やしません。この確認だけは冪等なので、通信障害時に回復期限内で繰り返せます。本体再起動、未知ID、不正な資格情報、別IDの応答では送信を再開せず停止します。再開応答や資格情報をstdioへ転送しません。

## 共通応答

`ToolResponse`はschemaVersion、ok、requestId、state、data、errorを保持します。
`Information`は即時情報、`Accepted`はqueuedの受付、`Rejected`は受付前の拒否、
`Request`は操作別結果を含む要求状態のスナップショットを作成します。
受付・処理中もokはtrueです。失敗・取消・タイムアウトはfalseとし、操作結果はdataに保持できます。

`OperationResult`は操作番号・名称・状態・UTC時刻・データ・エラーを保持します。
`ToolError`は仕様書の固定コード、コード由来の診断文、retryable（既定false）だけを返します。
任意の例外本文をエラーメッセージへ渡すAPIは設けていません。

`new ToolCallResult(response)`でMCPのstructuredContent、同じJSONのtext content、isErrorを作ります。
`ResponseJson.Serialize(result)`でJSON化できます。nullの項目も出力し、状態・コードは文字列です。
MCPハンドラーへの接続は後続項目で実装します。

## 文書

- [進捗](PROGRESS.md) — 完了率と優先順の残作業。
- [利用手順](docs/USAGE.md) — インストール後のMCP接続、操作、結果確認。
- [仕様書](SPECIFICATION.md)
- [実装計画](TODO.md) — 1回の実行で1項目だけ実装します。
