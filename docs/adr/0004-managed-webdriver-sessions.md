# ADR 0004: 管理WebDriverセッション

## Status

採用。2026-10-13。共通契約とChrome・Edge・Firefox起動を実装。

## Context

複数のブラウザと既存ユーザープロファイルが存在します。MCP操作の対象を種類と管理インスタンス・タブで区別し、失効した対象へ勝手に再接続しないことが必要です。

## Decision

BrowserSessionRegistryでb_から始まるインスタンスIDとbt_から始まるタブIDを発行します。ApplicationReferenceRegistryの管理プロセス世代と、アダプターの固定WebDriver接続・能力情報へ結び付けます。登録時には版の適合を確認済みの能力情報が必要です。ブラウザ名だけから適合を推定しません。

登録時のタブを上限256件で固定します。各操作前に管理プロセス、接続、ウィンドウハンドル一覧を照合します。閉じたタブのIDは削除し、新しく出現したタブへ再対応させません。種類不一致はBROWSER_TYPE_MISMATCH、ブラウザ終了はBROWSER_NOT_FOUND、ドライバー終了・通信断はBROWSER_CONNECTION_LOSTです。最大1000インスタンスとします。

Chromeは115以上とし、ChromeDriverのMAJOR.MINOR.BUILD一致を起動前にファイル版で確認します。通常のchrome.exe/chromedriver.exeだけを設定で指定し、ブラウザの固定引数は空とします。専用プロファイルをdat/browser-profiles内へ作成し、goog:chromeOptionsへ明示します。既存プロファイルやdebuggerAddressを受け付けません。返却能力のブラウザ版・ドライバー版・userDataDir・goog:processIDを確認し、PID・開始時刻・親子関係・実行パスを管理参照へ登録します。

EdgeはChromium版79以上とし、msedge.exe/msedgedriver.exeと先頭3部の版一致を要求します。ms:edgeOptionsとbrowserName:MicrosoftEdgeを使用し、返却能力msedge内のmsedgedriverVersionとuserDataDirを照合します。公開されている能力には汎用のブラウザPID保証がないため、ドライバーの子孫から実行パス・プロセス世代が一致し主ウィンドウを持つ一意な対象をOS情報で選びます。複数候補・転送・主ウィンドウなしは採用しません。これはChromeの返却PID方式への自動フォールバックではなく、Edgeアダプターの限定された対応方式です。

Firefoxは通常のfirefox.exeとgeckodriver.exeを指定し、Firefox 115以上・GeckoDriver 0.36.0の組合せを初期対応範囲とします。対応表を曖昧に拡張せず、別版の対応は明示的な変更で追加します。ドライバーの固定--version照会は起動リース下の別プロセスへ分離し、3秒・標準出力/標準エラー各8KiB・終了コード0を確認します。照会後の強制終了が許されるのはこの短命な版確認プロセスだけです。Firefoxの専用プロファイルは-profileで渡し、moz:profile・moz:processID・moz:geckodriverVersionとブラウザ版を照合します。ファイル版の末尾ゼロだけは返却版との比較で補完します。ESRの通常数値版は対応対象ですが、英字付き開発版は採用しません。

長寿命ドライバーの標準出力/標準エラーは読み捨て、アプリケーションログやMCP応答へ転送しません。FirefoxのドライバーとMarionetteのログはfatalへ限定します。ブラウザの起動転送や親プロセスの消失で世代を証明できない場合は登録しません。

ドライバーの待受はIPv4の127.0.0.1へ限定し、各要求前後にGetExtendedTcpTableで待受の所有PIDを確認します。空きポートの取得とドライバー起動の間は原子的ではないため、所有権を確認できない場合は失敗とします。応答は4MiB・JSON深さ32を上限とし、重複プロパティを拒否します。通信は30秒、ドライバー待受の確認は10秒を上限とし、操作全体の取消・タイムアウトも適用します。ドライバーのログレベルはOFF、WebDriver本文はアプリログへ出しません。

履歴移動はWebDriver BiDiのbrowsingContext.traverseHistoryと対象コンテキストのhistoryUpdated・fragmentNavigated・domContentLoaded通知を使います。履歴範囲外のno such history entryだけをmoved:falseへ変換します。成功応答だけでは移動完了を意味しないため、送信時刻以降の対象通知と文書readyState:interactive/completeを待ちます。URLだけから履歴位置を推定しません。ページやユーザーが同じタブへ同時に履歴変更する操作はOS・ブラウザ上で原子的に排他できません。

Chrome/Edge 126以上、Firefox 141以上の起動でwebSocketUrlを要求し、能力応答が提供するwsのループバック接続先と当該セッションパスだけを使用します。要求版は通知の初期実装を目安とする内部対応境界であり、版だけで実装能力を保証しません。各接続・送受信の前後に待受が管理ブラウザまたは管理ドライバーのPIDであることを照合し、localhostは127.0.0.1へ固定します。HTTPのプロキシ・リダイレクトは使用しません。最大30秒、各JSONメッセージ4MiB・深さ32、各待機1024メッセージを上限とします。通知が得られない場合はタイムアウトで、既存WebDriverのbackコマンドへ自動再送しません。BiDiや必要な通知を利用できない版はBROWSER_CAPABILITY_UNSUPPORTEDです。旧版は起動・通常遷移の対応範囲を保ちます。

## 代替案と不採用理由

- 既存の任意ブラウザへの接続: プロファイルと対象所有権を確定できません。
- ブラウザ名だけの指定: 同じ種類の複数インスタンスを区別できません。
- 失効IDの自動再解決: 別タブ・別文書への誤操作になります。

## 影響範囲

ブラウザ別起動、遷移、履歴、終了、後続DOM要素参照へ適用します。共通契約だけではブラウザを起動しません。

## セキュリティ条件

後続アダプターは許可済み実行ファイル、専用プロファイル、所有するドライバープロセスとループバック接続を照合します。MCP入力から任意のWebDriver接続先や実行ファイルを受け付けません。HTTP通信のリダイレクト・プロキシ・再試行は無効とし、応答上限・タイムアウト・構造化エラーを設けます。WebDriverの本文・URL・入力内容をログへ保存しません。

## 運用条件

browser.exitは管理セッションにDelete Sessionを1回送信し、保持したブラウザのプロセスハンドルが終了するまで待ちます。応答前の切断はブラウザの終了事実を確認できる場合だけ成功判定を続けます。その後、専用WebDriverサービスが残っている場合は管理世代を照合して当該サービスだけをKill(false)で終了します。ブラウザや他のユーザープロセスを強制終了しません。サービス終了後に参照を削除します。取消・期限後に未完了の後処理を継続せず、プロファイルは常に保持します。成功応答のprofileRetained:trueで保管状態を明示します。子プロセスが残る可能性があるため、起動時に作成したプロファイルを自動で再利用・削除しません。

ブラウザとドライバーの版の適合を起動前およびセッションの能力応答で確認します。Chrome、Edge、Firefoxの確認方法は別アダプターで明示します。全体停止時のDisposeは通信資源を解放し、一般ユーザーのブラウザを終了しません。

失敗時は既存ブラウザへの再接続、別PIDの採用、強制終了を行いません。ブラウザ起動後の失敗・取消では専用ブラウザやドライバーが残る場合があります。実行状態を確認できない状態でプロファイルを削除しないため、プロファイルは保持されます。終了と配布時の保守で管理対象の停止・保管方針を明示します。ChromeDriverのバイナリ版が読めない場合も適合不明として拒否します。

## 実装・検証・文書への対応

BrowserSessionRegistryとIManagedBrowserConnectionを実装し、共通URL制約と識別子検証を提供します。起動APIは後続P11-04～P11-06で接続します。全体ビルドを確認し、実機・接続断の動作は未検証として仕様・README・進捗へ記録します。

参照: [W3C WebDriver](https://www.w3.org/TR/webdriver2/)。

Chrome参照: [版の選定](https://developer.chrome.com/docs/chromedriver/downloads/version-selection)、[能力情報](https://developer.chrome.com/docs/chromedriver/capabilities)、[セッション能力の実装](https://chromium.googlesource.com/chromium/src.git/+/main/chrome/test/chromedriver/session_commands.cc)、[待受PID表](https://learn.microsoft.com/en-us/windows/win32/api/tcpmib/ns-tcpmib-mib_tcprow_owner_pid)。

Edge参照: [版の適合](https://learn.microsoft.com/en-us/microsoft-edge/webdriver/)、[返却能力](https://learn.microsoft.com/en-us/microsoft-edge/webdriver/capabilities-edge-options)。

Firefox参照: [対応版](https://firefox-source-docs.mozilla.org/testing/geckodriver/Support.html)、[起動フラグ](https://firefox-source-docs.mozilla.org/testing/geckodriver/Flags.html)、[専用プロファイル](https://firefox-source-docs.mozilla.org/testing/geckodriver/Profiles.html)。

履歴参照: [W3C WebDriver BiDi](https://www.w3.org/TR/webdriver-bidi/)、[履歴移動と完了通知](https://developer.mozilla.org/en-US/docs/Web/WebDriver/Reference/BiDi/Modules/browsingContext/traverseHistory)。
