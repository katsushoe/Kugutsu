# Kugutsu 仕様書

- 文書種別: 要求仕様
- 状態: 実装用の初期仕様（既定値は設定で変更可能）
- 対象: Kugutsu
- 最終更新: 2026-10-13（P09-06のUIA単一項目選択までの実装に合わせて更新）

## 1. 概要

Kugutsu は、ローカル PC 上のウィンドウを AI から操作するための C# 製 MCP サーバです。AI クライアントは stdio プロキシを介して Kugutsu のローカル Streamable HTTP エンドポイントへコマンドを渡します。

通信方向は `AIクライアント → Kugutsu.McpProxy.exe（stdio）→ Kugutsu.exe（Streamable HTTP）→ 操作バッファ → Windows操作` です。Itoguruma の接続手順書と設定仕様を参照して決定しました。

## 2. 配置・インストール

実機への標準インストール先は `E:\Kugutsu` です。標準のフォルダ構成は次のとおりです。

| フォルダ | 用途 |
| --- | --- |
| `bin` | 実行ファイル |
| `config` | 設定ファイル |
| `dat` | データファイル |
| `logs` | ログファイル |

- 本体は `bin\Kugutsu.exe`、プロキシは `bin\Kugutsu.McpProxy.exe` とします。
- 実行時の配置はAppContext.BaseDirectoryを基準に解決し、binの親をインストール先とします。カレントディレクトリやEドライブの固定値へ依存しません。bin以外に実行ファイルを置いた場合は配置エラーとし、別の場所を推測しません。パス解決だけではフォルダの作成や設定の読込みは行いません。
- フレームワーク依存型で配布します。WiX Toolset製の `Kugutsu.msi` と、前提ランタイムを導入するBurnブートストラッパー `Kugutsu.Setup.exe` を作成します。
- セットアップは x86版 .NET Desktop Runtime 10 と ASP.NET Core Runtime 10 を検出し、不足分をMicrosoft公式配布物から自動導入します。x64版のみの導入は要件充足とみなしません。SDKは不要です。
- ランタイムは公式リリースメタデータにあるSHA-512とMicrosoft Authenticode署名をビルド時に検証して同梱します。セットアップはx86 .NET 10 Desktop／ASP.NET Core Runtimeを検出し、不足分を先に導入します。いずれかの失敗時はMSIを実行せず、共有ランタイムはアンインストール時に削除しません。MSI単体起動時も必要ランタイムがなければローンチ条件で停止します。
- 更新はMajorUpgradeで旧バージョンを置換します。`config/appsettings.json`とCredential Managerの資格情報は修復・更新・アンインストール後も保持し、MSI管理の製品ファイルとログオンRun登録はアンインストールで除去します。セットアップ生成の検出ファイル・WebDriver・一時ダウンロードは旧製品アンインストール時に消し、更新後のセットアップで再生成します。ログは未管理ファイルとして残し、x86 .NET共有ランタイムは削除しません。
- 既定の `E:\Kugutsu` が利用できない場合はインストール先を選択できるようにします。通常実行はログオンユーザー権限、インストール時のみ必要に応じて昇格します。

## 3. 対象環境

- 対応OSは Windows 10 / Windows 11 とします。初期の動作対象は Windows 10 22H2 と Windows 11 Home / Pro / Enterprise です。
- .NET 10 を使用します。Target Frameworkは `net10.0-windows`、Runtime Identifierは `win-x86` とします。
- x86プロセスとして配布し、Windows 11ではx64 OSのWOW64上で動作します。Windows 11用の32bit OSを前提にしません。Arm64は初期検証対象外とします。
- UIはWindows Forms、HTTPサーバはASP.NET Coreを採用します。OS／ランタイムのベンダーサポート状況と、本製品の動作対象は区別します。

## 4. 起動と常駐

- 実行ファイルは PC 起動時に自動起動します。
- 起動後は TaskTray に常駐します。
- TaskTray から、操作バッファの内容を参照するウィンドウを起動できます。
- ユーザーログオン時にユーザー単位のRun登録で起動します。単一インスタンスとし、終了時は新規受付を停止します。
- トレイの「操作を緊急停止」は全所有者の新規操作受付を止め、待機要求をCANCELLEDで取り消し、実行中要求へ協調取消を通知します。コールバック通知はUIをブロックせず、停止確認中と確認済みを表示します。停止確認までワーカーを保持し、停止確認後にユーザーが「操作受付を再開」を選ぶと受付を再開します。停止中の新規execute_operation／submit_batchはCANCELLEDで拒否し、既存要求の照会・重複照会は継続します。ロックは自動解放せず、取得待ちへの新規付与も再開まで保留します。緊急停止・再開はローカルUIだけに提供し、外部アプリで生じた副作用の取消は保証しません。
- 単一インスタンスの範囲はWindowsユーザーSIDと対話セッションです。同じ組み合わせですでに起動済みなら、新しいプロセスはHTTP待受を開始せず固定診断を記録して終了します。別セッションの同一ユーザーは独立して起動できます。
- 通常終了はトレイの終了メニューとHTTPホストの停止通知から同じ処理へ接続します。受付を閉じた時点から新しい操作をCANCELLEDで拒否し、待機要求を取り消し、ロック取得待ちを終了します。実行中ハンドラーと進行中の取消通知が終了するまではトレイに終了中を表示し、既存要求の照会を可能にします。停止未確認では通常終了を完了扱いにせず、プロセスを強制終了しません。受付停止時にタイマーを止め、停止確認後にロック・キュー資源とUIを解放し、HTTPホストの停止・破棄、資格情報・単一インスタンス保持の解放へ進みます。キュー終了処理は再入可能とし、同じ終了Taskを返します。
- 終了中のロック更新はCANCELLED、通常解放はMCP_LOCK_BUSYです。HTTPホストの停止処理は停止確認後に30秒の期限を設け、失敗はHttpHostStopFailed／終了コード1とします。成功ログはHTTPホストの停止・破棄後に記録します。実行停止の確認待ちとHTTP停止期限は別に扱います。
- 緊急停止の再開条件は、実行中ハンドラーの終了と、緊急停止が発行した取消コールバック通知の完了です。停止を確認できない間は受付を閉じたまま維持します。取消を無視して完了した操作は実際の完了結果を保持します。
- 同じユーザーの対話セッション内のウィンドウを操作します。ロック画面、UACのセキュアデスクトップ、別ユーザーの画面は対象外とし、権限不足はエラーで返します。

## 5. MCP 通信とコマンド

- Kugutsu本体は `http://127.0.0.1:47641/mcp` で待ち受けます。URLは設定変更可能ですが、初期版ではloopbackのみを許可します。ポート競合時は明示的な起動エラーとし、別ポートへ勝手に切り替えません。
- HTTPホストは公式ModelContextProtocol.AspNetCore SDK（2.2.0に固定）とKestrelで構成します。設定のIPアドレスへ明示的にListenし、localhostはIPv4／IPv6のloopbackへListenLocalhostで待ち受けます。DNS名・ワイルドカード・動的ポートは使いません。標準のホスト構成読込みを使わず、環境変数や別のappsettingsで待受先を上書きさせません。プロキシはHTTPサーバを起動しません。
- URLのパスは静的なMCPルートとし、ASCII英数字、`/`、`-._~`だけを許可します。ルートテンプレート・パーセントエスケープ等は起動設定エラーとします。相手アドレスがloopback以外、Hostが設定URLのホスト・ポートと異なる、Originが存在し設定URLのscheme・host・portと異なる場合はHTTP 403です。Originは省略可能で、複数指定やnull originは拒否します。
- MaxRequestBytesをHTTP本文の上限にも適用し、起動・停止処理は30秒を上限とします。ポート競合はHttpPortInUse、その他の起動失敗はHttpHostStartFailedの固定ログ診断で終了コード1とします。起動成功は待受開始後に記録します。要求URL・ヘッダー・本文・例外本文をホストの汎用ログから保存しません。
- 本体は単一インスタンス確認後に待受を開始し、TaskTrayのWinFormsメッセージループをSTAスレッドで実行します。トレイから操作バッファ画面を開けます。MCPツールの利用方法とクライアント接続手順は[利用手順](docs/USAGE.md)を参照してください。
- MCPツール`execute_operation`はハイフン付き標準UUID文字列`requestId`と単一の`operation`（`name`、`args`）を受け取ります。未登録操作・引数不正・UUID不正は`INVALID_ARGUMENT`、待機容量超過は`QUEUE_FULL`で受付前に返します。受付成功は`queued`を返し、完了を意味しません。要求IDの重複照合と実行結果照会は後続項目です。
- MCPツール`get_request`はハイフン付き標準UUID文字列の要求IDを受け取り、認証済み論理所有者と一致する要求の状態・時刻・操作別結果を共通応答形式で返します。未存在と他所有者の要求はともに`REQUEST_NOT_FOUND`とし、存在を推測できる情報を返しません。
- HTTP認証は全メソッド・全要求で境界検証の後、MCPハンドラーより前に行います。Authorizationは1値だけを許可し、認証方式Bearerは大文字小文字を区別しません。方式とトークンの間は1個以上のASCII空白とし、トークンは大文字小文字を区別して起動時の資格情報と完全一致を求めます。タブ区切り、トークン内・末尾の空白、連結・重複ヘッダー、1280文字超、非ASCII文字を拒否します。HTTPパーサーが除去するヘッダー外側の空白は判定対象外です。クエリ・本文・Cookieから認証情報を取得しません。
- 認証未指定・形式不正・値不一致は本文なしの401とWWW-Authenticate: Bearer、Cache-Control: no-storeを返し、MCP処理へ進めません。Host／Origin等の境界不一致は従来どおり403です。認証用ASCII一時バッファは照合後に消去し、同じ長さのトークン照合にはFixedTimeEqualsを使います。共有Bearerは接続の認証だけに使用し、操作の所有者・論理セッションIDには使用しません。
- Itogurumaと同様にstdioプロキシがBearerトークンを付けてHTTPサーバへ接続します。設定ファイルは `config\appsettings.json` とし、URLは `Kugutsu:ServerUrl` で指定します。プロキシの `--url` 引数は設定より優先します。
- トークンはセットアップ時に生成し、Windows Credential Managerの `Kugutsu/McpBearerToken` へ保存します。更新・再インストールで維持し、明示的なローテーション時のみ変更します。サーバとプロキシは同じユーザーの資格情報を読みます。認証なしの運用は許可しません。
- 資格情報はCRED_TYPE_GENERICとし、CredentialBlobはBOM・終端NULを含まないUTF-16LEで保存します。Windows側の文字列形式に合わせてこの形式を正本とし、UTF-8等の自動判別は行いません。保存サイズはWindowsの上限2560バイト以内の正の偶数です。値はBearerの文字列構文（ASCII英数字と`-._~+/`、末尾だけの`=`）に限り、空文字、空白、改行、NUL、BOM、他のUnicode文字を拒否します。前後の空白除去などの補正は行いません。書込み・初期生成はP16-03で実装し、読込み時に生成・上書きしません。
- 本体・プロキシは設定検証後、起動成功を記録する前に資格情報を一度読み込みます。CredReadWのERROR_NOT_FOUNDはCredentialMissing、保存形式不正はCredentialInvalid、その他の読込み失敗や利用不能はCredentialUnavailableとして起動失敗を記録し、ダイアログなしで終了コード1とします。別ユーザーの資格情報や設定・環境変数・引数への代替は行いません。更新したトークンは本体とプロキシの再起動時に反映します。
- 取得した資格情報のネイティブバッファはCredFreeで解放し、読込み時の一時バッファと保持するトークンバイト列は使用終了時に消去します。トークンの診断文字列は(redacted)で、JSON化されるトークンプロパティは持ちません。HTTP送受信ヘッダーの.NET文字列は消去を保証できないため、ヘッダーは通信処理だけで扱い、永続化・ログ・例外本文への出力を禁止します。HTTP認証はP02-03、プロキシ通信はP02-05で接続済みです。
- このプロキシはMCPのstdio/HTTP変換器です。初期版はloopback直結であり、外部HTTPプロキシの認証・経路設定は不要です。
- P02-05ではModelContextProtocol.Core 2.2.0のStreamServerTransport（標準入力／標準出力）とHttpClientTransport（StreamableHttp固定）を使用します。initialize／server/discoverを含むJSON-RPCを中継し、プロキシ自身の初期化要求、要求IDの書換え、独自ツール一覧を追加しません。SDKによる解析・再シリアライズを行うため、空白・プロパティ順までのバイト一致は保証しません。HTTPのJSON／SSE応答と通知はstdioへ返します。
- HTTPクライアントはIHttpClientFactoryで生成し、資格情報をAuthorizationへ付与します。外部HTTPプロキシ、リダイレクト、Cookie、SDKの自動再接続は無効です。P02-07の再接続は下記の到達確認として実装します。接続期限は30秒、各メッセージの通信期限はRequestTimeoutSecondsです。標準入力の1行はLFを除いてMaxRequestBytes以内とし、CRLFの場合のCRも計数します。SDKが行文字列を構築する前にバイト数を制限します。同時HTTP送信は最大16件とし、応答待ちの間も他の要求・通知を送れるようにします。
- 標準入力EOF／Ctrl+Cでは中継を停止し、処理中の通信タスクを取消して解放します。HTTP上のMCPセッションをDELETEせず、受付済み操作の取消・再送は行いません。STDIOクライアントがEOF後の返信を待てることは保証しません。標準出力はSDKのMCP出力だけとし、ヘッダー・入力本文・例外本文をログへ渡しません。
- HTTP 401／403はMcpAuthenticationFailedで再試行せず終了コード1とします。一時的通信障害はMcpTransportFailed、通信期限超過はTimeoutとして記録して到達確認へ移ります。回復期限超過・回復不能時は終了コード1です。SDKがHTTP上のJSON-RPCエラーをMcpProtocolExceptionへ変換した場合、要求には同じID・エラーコードと固定文言のJSON-RPCエラーを返し、例外本文を開示せず中継を継続します。要求に対応しないプロトコルエラーはMcpProtocolFailedで停止します。起動成功ログはローカル準備の完了であり、最初のHTTP送信成功までリモート接続成功を記録しません。
- 接続先の--url上書きはP02-06、再接続はP02-07、論理クライアントセッション発行はP02-08、再開資格情報による所有者確認はP02-09で実装済みです。この範囲のテスト・実機検証は未実施です。
- P02-06のプロキシ起動引数は`--url <URL>`または`--url=<URL>`のどちらか1回だけです。大文字小文字を区別し、引数なしならKugutsu:ServerUrlを使用します。重複、未知引数、位置引数、値不足、空文字、空白文字・NULを含む値を拒否します。引数の値はメモリ内でServerUrlへ反映し、設定ファイルの保存・書換えは行いません。本体にはこの引数を適用しません。
- 引数解析と有効設定の検証は、資格情報読込み・stdio生成・HTTP通信より前に行います。設定ファイルは必須で、JSON構造・キー・型の検証を省略しません。設定ファイルのURL文字列が不正または未指定でも、引数で有効URLへ上書きした後の設定値を検証します。HTTPの絶対URL、localhostまたはloopback IP、正のポート、資格情報・クエリ・フラグメントなしという既存のURL規則を使用し、他の設定項目は従来どおり検証します。引数が不正なら設定URLへ戻さず、ConfigurationInvalidを記録し、診断本文へ入力値を含めず終了コード1で停止します。
- P02-05では独立したGET通知ストリームを開きません。通知の中継対象はHTTP応答ストリームで受信したものです。標準出力への各メッセージ送信にもRequestTimeoutSecondsを適用し、クライアントが読まなくなった場合に無期限で待ち続けません。
- 通信にはMCPの公式Streamable HTTPトランスポートを使用します。接続先のMCPサーバと対応プロトコル版をネゴシエーションします。
- P02-07の設計判断（採用済み）：操作の二重実行を防ぐため、失敗したMCPメッセージの再送方式は採用せず、接続先へ本文なしのOPTIONS到達確認を送ります。共有BearerはHTTPクライアントから付与し、MCPセッションID、再開資格情報、操作本文は確認要求へ含めません。新たな設定項目は追加せず、回復周期全体にRequestTimeoutSeconds、各確認に最大30秒を適用します。
- 対象障害はネットワーク・IO障害、通信期限超過、SDKの応答欠落等のMcpException（McpProtocolExceptionを除く）、HTTP 408／429／500／502／503／504です。同じ送信世代の同時障害は1周期にまとめます。待機基準は1、2、4…60秒、各待機へ0～20%のジッターを加え、最終値を60秒で上限化します。回復成功後の次の障害は基準1秒から始めます。期限超過、標準入力EOF、Ctrl+Cで遅延と確認通信を終了します。
- OPTIONSの2xx／405は到達確認成功です。P02-09では、その後の論理セッション再開確認が成功して初めてMcpReconnecting/Succeededを記録し、待機中の新しい送信を許可します。MCP初期化・操作完了を証明するものではありません。401／403は認証失敗で即停止し、他の非再試行HTTP応答は設定不正として停止します。MCP HTTPセッション失効（404）やSDK受信チャネル終了は自動再初期化せず停止します。到達確認と所有者確認は既存SDKトランスポートのプロトコル情報を変更しません。
- 新しいメッセージは回復完了まで待機し、メッセージごとの期限内に1回だけ送ります。送信を開始した要求の通信障害には同じJSON-RPC IDで-32001と固定文言、data:{outcome:"unknown",replayed:false}を返します。回復待機中の期限超過で未送信なら-32002、outcome:"not_sent"です。いずれも操作成功／失敗の断定ではなく中継上のエラーです。要求に対応する応答とローカルエラーは1回だけ返し、ローカルエラー後の遅延応答を破棄します。未処理の同じJSON-RPC IDの再使用はプロトコル不正として停止します。失敗した通知も暗黙再送せず固定イベントMcpMessageFailedを記録します。
- 影響範囲はプロキシ中継・終了処理と固定ログイベントです。本体の操作状態やキューは変更しません。受付済み要求の取消・再送は行わず、結果不明の操作は操作要求IDで状態照会します（照会機能はPhase 3で実装予定）。代替案のMCP pingによる確認は初期化状態に依存し、要求の自動再送は副作用を重複させるため採用しません。テスト指示後の確認項目は同時障害の集約、待機値と期限、回復後の新規送信、401／403・404停止、EOF／Ctrl+C、送信回数1回、遅延応答の二重返却防止です。
- AI から受け取るコマンドは、単体で実行する形式と、複数のコマンドをまとめるバッチ形式に対応します。
- 単体操作は1つの操作要求として受け付けます。バッチは複数の操作を1つの要求にまとめ、定義順に実行します。
- 操作コマンドとMCPツールの契約は「入力・出力仕様」に定めます。
- バッチに含める操作手順の表現形式は次の 2 種類です。
  1. JSON 形式: ウィンドウ操作の内容を JSON として渡します。
  2. Aloe プログラム形式: 操作手順を Aloe 言語のプログラムとして渡します。
- JSONとAloeは共通の操作APIを呼び出します。Aloeは既存のAloe-langのCompilerLib／RuntimeLib／CommonLibを参照し、配布時のアセンブリバージョンとSHA-256を固定します。他プロジェクトのソースは本作業で変更しません。
- 2026-10-14に読み直したAloeCompilerソースにはCompile(string)が実装されています。Kugutsuからのコンパイル・実行は内部補助プロセスへ接続しています。P15-02～P15-07でコンパイル・ホストAPI・命令数／時間／メモリ制限・取消・受付を接続しています。固定依存が利用不能な場合は `ALOE_UNAVAILABLE` を返し、JSONへ暗黙に置き換えません。
- コマンド入力には形式バージョン、要求ID、操作または操作列を含めます。バッチ件数・入力サイズ・実行時間には上限を設け、上限値は設定可能とします。
- 操作結果は要求ID、全体の成否、各操作の順序・成否・結果またはエラーを含む構造化形式で返します。エラーには安定したコードと短い説明を含めます。
- バッチは順次実行とし、途中の操作が失敗した場合は後続操作を実行せず停止します。すでに完了した操作はロールバックしません。失敗位置までの結果を返します。
- Aloeプログラムは任意のプロセス起動、ファイル・ネットワークアクセスなどの一般用途機能を許可せず、定義済みウィンドウ操作APIだけを利用可能にします。

## 6. 操作バッファ

操作バッファは、AI から受け取った操作依頼を格納し、1 件ずつ順番に実行する Kugutsu 内部のバッファです。TaskTray から起動するウィンドウで、バッファの内容を参照できます。

- バッファはFIFOの揮発性キューとし、バッチ全体を1件として扱います。プロセス再起動後への持ち越しは行いません。受理された要求は通信切断後も処理を継続します。
- 同時実行数は1とし、キュー容量を超える要求は実行せず、明示的な過負荷エラーを返します。
- 参照ウィンドウは保持期間内の要求から受付時刻の降順で最大1000件を表示し、1秒周期で更新します。表示項目は要求ID、UTC受付時刻、状態（待機中／実行中／成功／失敗／取消／タイムアウト）、操作名の要約です。所有者ID、操作引数、結果データ、エラー詳細、スクリーンショットは表示しません。
- `cancel_request`は認証済みの論理所有者だけが呼び出せます。状態が`queued`の要求をFIFOキューから原子的に取り除き、`cancelled`として完了させます。状態が`running`なら要求専用の取消トークンを通知し、現在状態を返します。完了済みなら状態を変えず現在状態を返します。未知IDと他所有者のIDは同じ`REQUEST_NOT_FOUND`です。
- 状態は `queued → running → succeeded / failed / cancelled / timed_out` とします。待機中は即時取消、実行中は操作の境界で協調的に取消します。すでに実行された入力の取り消しは保証しません。
- 実行中の`cancel_request`は要求専用の取消トークンを通知し、現在の`running`状態を返します。完了は`get_request`で確認します。操作ハンドラーが取消を協調的に処理して`OperationCanceledException`を返した場合に限り要求を`cancelled`とします。ハンドラーが取消を無視して完了した場合は実際の完了結果を返し、既に発生した副作用を取り消したとは報告しません。ワーカーは実行中ハンドラーの終了確認まで次の要求を開始しません。
- 同じ論理所有者・要求ID・同じ操作JSONの再送は既存状態を返し、二重実行しません。JSONオブジェクトのプロパティ順は比較時に正規化し、配列順と値は保持します。同じ所有者・要求IDで操作内容が異なる場合は `REQUEST_ID_CONFLICT` です。異なる論理所有者の同じ要求IDは別要求として扱います。この保証は同じプロセスの結果保持期間内に限定します。期限または上限により記録が削除された後の同じIDは新しい要求として受付可能です。
- 排他ロック取得待ちがある間は新しい操作要求の受付を止め、取得前に受理した要求をFIFO順に完了させてからロックを付与します。占有中は所有者の要求だけを受理します。詳細は8.1節に定めます。
- シグナル監視とロックの管理処理は操作バッファと独立して動作します。シグナル待機はバッファを占有しますが、監視・ロック更新・状態照会・取消は継続できます。

## 7. 設定とログ

- プログラムのカスタマイズは設定ファイルで行います。
- config/appsettings.jsonのKugutsuセクションを起動時に一度読み込み、数値等の省略値はモデルの既定値で補います。接続URLやプロファイル配置先の既定値は配布用JSONに置きます。環境変数による上書き・実行中の自動再読込みは行いません。設定ファイルの欠落、アクセス拒否、JSON読込み・型変換失敗時は起動を停止し、設定本文をログへ出しません。
- 設定はUTF-8 JSON（BOM、コメント、末尾カンマを許可）です。ルートにはKugutsuオブジェクトだけを置き、未知キー・大文字小文字を無視した重複キー・型違いを拒否します。数値はJSON整数、列挙値は名前の文字列、配列と辞書は所定のJSON構造で指定します。キー名・ログレベル名は大文字小文字を区別しません。
- ServerUrlとUiProfileDirectoryは必須です。ServerUrlはloopbackのHTTP絶対URLに限り、資格情報・クエリ・フラグメントを含めません。UiProfileDirectoryはインストール先配下へ字句的に解決できる相対ディレクトリです。絶対パス、ルート外への遡及、ワイルドカード等の不正パスを拒否します。リンク先の解決や存在・アクセス権の確認は利用時に行います。
- LogTotalSizeMiBはLogFileSizeMiB以上とします。要求期限と操作期限は独立して指定でき、先に到達した方を適用します。EnabledUiDriversは既知の名前を1件以上、重複せず指定します。ドライバ名とブラウザ種類は小文字の定義名を使います。未実装・未導入のドライバであること自体は設定エラーにしません。
- 起動対象IDはASCII英字で始まる1～64文字の英数字・アンダースコア・ハイフン・ピリオドとし、大文字小文字を区別して一意にします。実行パスは絶対.exeパス、固定引数は文字列配列（NUL禁止）です。BrowserLaunchTargetsの参照先は登録済みIDとします。TerminalLaunchTargetIdとPowerShellLaunchTargetIdはnullで未設定を表せます。未登録のIDも機能未設定として扱うため、初期の空のAllowedLaunchTargetsで起動できます。ブラウザ・WebDriverの存在と版の適合はセットアップ／利用時に確認します。
- 実行時にログファイルを出力します。
- ログ形式はUTF-8（BOMなし）・LF区切りのJSON Linesとします。標準の行形式に代えて、timestamp（UTC・ISO 8601）、level、component、event、outcome、request_id、operation_kind、operation_index、elapsed_ms、error_code、source_file、source_lineだけを保存します。値がない任意項目はnullです。要求IDはUUID、操作種別・イベント・成否・診断コードは内部の列挙値とし、ソース位置は呼出し元ファイル名と行番号です。
- 本体はkugutsu、プロキシはkugutsu-proxyを接頭辞とし、起動時刻とランダムIDを含む別々の.jsonlファイルをlogsへ作成します。複数プロキシの同時書込みを分離し、同一ファイルへの書込みはプロセス内で直列化・毎行フラッシュします。既存ログは上書きしません。
- パス解決後にログファイルを開き、設定読込み前の最小レベルはInformation、設定検証後はLogLevelを適用します。ファイルを作成できない場合の診断は本体がTrace出力、プロキシが標準エラー出力を使い、起動を終了コード1で停止します。ファイル書込み・終了時のフラッシュ失敗も、固定のLogWriteFailure診断後に終了コード1で停止します。標準出力へ診断を出しません。
- ログへの入力は専用の型付きイベントAPIに限定します。汎用ILoggerのメッセージ状態・フォーマッター・スコープ・カテゴリ文字列・例外本文・内部例外は保存しません。例外は固定の診断分類だけに変換します。後続の要求処理・操作ハンドラーは共通APIへ接続します。
- 1行のUTF-8バイト数とLFを含めてLogFileSizeMiBを超える直前に新しいファイルへ切り替えます。UTC日付が変わった後の最初の書込みでも切り替え、1行を複数ファイルへ分割しません。
- LogRetentionDaysは最終更新UTC時刻からの日数です。期限を過ぎた閉じたログを削除し、さらに本体・全プロキシの管理対象ログの合計がLogTotalSizeMiB以内になるよう古い閉じたログから削除します。起動時・設定反映時・書込み前と1分ごとの定期処理で整理します。設定検証前はモデルの既定上限を使います。
- 削除対象はlogs直下で、接頭辞・UTC時刻・32桁のランダムIDを含む本製品の.jsonlファイル名に一致する通常ファイルだけです。子ディレクトリ、管理対象外ファイル、再解析ポイントは削除しません。使用中ファイルは保護し、ファイル操作を共有リースでプロセス間排他します。リース取得は最大5秒です。
- 使用中ファイルなどにより容量上限を確保できない場合や、整理・排他取得に失敗した場合は、上限を超える書込みを行わず終了コード1で停止します。稼働中の失敗では固定のLogWriteFailure診断をファイル以外の診断先へ出します。共有リース用ファイルは容量集計に含めず、プロセス終了・異常終了時にハンドルの排他が解除されます。
- 設定ファイルは `config`、ログファイルは `logs` に配置します。
- ログには起動・終了、MCP接続状態、要求ID、操作種別、所要時間、成否、エラーコードを記録します。キー入力値、入力テキスト、画面画像、認証情報は既定で記録しません。ロック取得・更新・解放・失効とシグナルの登録・状態遷移・解除も記録しますが、トークン、条件の比較文字列、観測した本文は記録しません。
- ログレベル、保存期間、ファイルサイズ上限は設定可能とし、サイズ上限によるローテーションを行います。
- 既定値は次のとおりです。変更は本体の再起動時に反映し、不正値では操作受付を開始しません。

| 設定キー（`Kugutsu:`配下） | 既定値 | 制約 |
| --- | --- | --- |
| `QueueCapacity` | 100要求 | 待機要求のみ。1～1000 |
| `MaxBatchOperations` | 1000操作 | 1～10000。AloeはホストAPI呼出し数を計数 |
| `MaxRequestBytes` | 1 MiB | UTF-8要求本文。64 KiB～16 MiB |
| `MaxClientSessions` | 1000 | 本体の寿命中に保持する論理セッション上限。1～10000。上限超過時に既存所有者を削除しない |
| `OperationTimeoutSeconds` | 30 | 待機を含む1操作。1～300 |
| `RequestTimeoutSeconds` | 300 | 実行開始から要求全体に適用する上限。1～3600。操作期限より短く設定可能 |
| `QueueTimeoutSeconds` | 300 | 受付から実行開始まで。1～3600 |
| `McpLockLeaseSeconds` | 120 | 排他ロックの既定有効期間。10～3600。呼び出し時の指定も同範囲 |
| `McpLockAcquireTimeoutSeconds` | 30 | ロック取得待ちの既定上限。0～300。0は即時判定 |
| `MaxSignals` | 100 | サーバ全体の登録数上限。1～1000 |
| `SignalRetentionSeconds` | 300 | シグナル登録からの自動解除期限。1～3600。ロック終了時には期限前でも解除 |
| `SignalPollIntervalMilliseconds` | 200 | イベント通知を利用できない状態条件の監視間隔。50～5000 |
| `OcrLanguage` | jpn+eng | ローカルOCR言語。eng/jpn/jpn+eng |
| `OcrMaxWords` | 1000 | メモリ内に保持するOCR単語上限。1～10000 |
| `VisualEdgeContrast` | 24 | 画像候補の境界と判断する輝度差。1～255 |
| `EnabledUiDrivers` | uia, imgui_visual, generic_visual | UIAとローカル画像解析ドライバ。利用不可のドライバは能力照会で理由を返す |
| `UiProfileDirectory` | config/ui-profiles | インストール先相対の宣言的なアプリ別プロファイル配置先 |
| `VisualSnapshotLifetimeSeconds` | 10 | 画像由来の要素参照の最大有効期間。1～60。操作直前の再検証も必須 |
| `MaxUiCandidates` | 100 | 1回の探索結果上限。1～1000。超過はtruncatedと件数を返す |
| `ResultRetentionMinutes` | 60 | 1～1440。最大1000件、超過時は古い完了結果から削除 |
| `LogLevel` | Information | Trace / Debug / Information / Warning / Error |
| `LogRetentionDays` | 14 | 1～365 |
| `LogFileSizeMiB` | 10 | 1～100 |
| `LogTotalSizeMiB` | 200 | 10～1024。超過時は古いログから削除 |
| `AllowedLaunchTargets` | 空配列 | 起動を許可するアプリのID、絶対実行パス、固定引数を列挙。登録外の実行ファイル起動は禁止。セットアップ検出結果ファイルの対象も追加される |
| `TerminalLaunchTargetId` | terminal | ターミナル起動用の登録済み起動対象ID。セットアップでWindows Terminalを検出した場合だけ設定され、未検出なら機能未設定 |
| `PowerShellLaunchTargetId` | powershell | PowerShell起動用の登録済み起動対象ID。セットアップでWindows PowerShellを検出した場合だけ設定され、PowerShell 7を利用する場合は手動設定する |
| `ConsoleHostLaunchTargetId` | consolehost | 専用PowerShellコンソールの通常conhost.exe登録ID（固定引数は空）。セットアップで検出できない場合は機能未設定。入力先確認に同梱Kugutsu.ConsoleProbeを使用 |
| `BrowserLaunchTargets` | 検出結果 | firefox/chrome/edgeと登録済み起動対象IDの対応。検出できた種類のみ設定され、手動設定は検出結果より優先 |
| `BrowserDriverPaths` | セットアップで設定 | 各ブラウザ用WebDriver実行ファイルの絶対パス。ブラウザ版・OSアーキテクチャとの適合を確認する |

セットアップは `bin/Kugutsu.exe --detect-launch-targets` を実行し、実在確認できたWindows Terminal、Windows PowerShell、conhost.exe、Firefox、Chrome、Edgeだけを `config/detected-launch-targets.json` に記録します。App Pathsレジストリと既知の標準配置を使い、App Execution Aliasは検出済みと扱いません。続いてWebDriverを公式HTTPS配布元から取得し、コード署名、ブラウザー版、OSアーキテクチャを確認して `bin/drivers` へ置き、版・SHA-256をdrivers-manifest.jsonへ記録します。起動時に検出ファイルを読み込みますが、明示的な `appsettings.json` 設定を優先します。未検出の対象は登録せず、検出ブラウザーに適合する公式ドライバーが得られないセットアップは失敗します。

ランタイム前提条件の検査スクリプトはHKLMの32ビットレジストリビューに登録されたx86共有フレームワーク版と各インストールディレクトリを照合します。Microsoft.NETCore.App、Microsoft.WindowsDesktop.App、Microsoft.AspNetCore.Appの各10.0.xが揃う場合だけ充足とし、x64レジストリビューの存在では代替しません。検査結果はJSONと終了コード0（充足）、1（不足）、2（検出失敗）で返します。Burnは不足ランタイムの導入後にMSIを実行します。

- `dat` には `installation.json`（製品・組込ライブラリ版／ハッシュ）を保存します。要求本文・入力値・スクリーンショット・キュー・結果・ロック・シグナル登録はディスクへ保存しません。

## 8. 入力・出力仕様

- MCPツール一覧は次のとおりです。全ツールは認証済み要求のみを受理し、共通応答形式は本節の後半に定めます。
- P02-04では`get_version`だけを公開します。引数なしで即時応答し、操作バッファ・要求ID・MCPロックを必要としません。認証・Host／Origin検証はHTTPホストの全要求向け処理を適用します。
- `get_version`の共通応答はschemaVersion:1、ok:true、requestId:null、state:null、error:nullです。dataは`{name, version, protocolVersion}`とし、nameはKugutsu、versionは本体のAssemblyInformationalVersion、protocolVersionはSDKの現在の要求／セッションに対応したNegotiatedProtocolVersionを返します。版未確定ならprotocolVersionはnullとし、SDKのパッケージ版をMCPプロトコル版として返しません。製品版はDirectory.Build.propsのVersionで管理し、現在の製品版は1.0.1.0です。初期化／探索時のServerInfoも同じ製品名・製品版を使います。
- SDKのCallToolResultへ共通応答を変換し、structuredContentとcontent内のJSONテキストを一致させます。isErrorはfalseです。版情報を設定ファイルから上書きせず、ツール呼出しでディスク・資格情報の再読込みやウィンドウ操作を行いません。

| MCPツール | 用途 | 主な入力 | 返却内容・動作 |
| --- | --- | --- | --- |
| `get_version` | Kugutsuの版確認 | なし | 製品名、製品版、MCPプロトコル版 |
| `list_windows` | 操作可能なウィンドウを探す | `titleContains`、`processName`（省略可） | `windowId`、タイトル、PID、プロセス名、表示／最小化状態、矩形の一覧 |
| `get_window` | ウィンドウの現在情報を得る | `windowId` | 対象ウィンドウ情報。存在しない、または無効ならエラー |
| `execute_operation` | 単体操作を依頼する | `schemaVersion`、`requestId`、`operation`（操作名と引数）、`lockToken`（ロック中必須） | 検証後キューへ受付。要求IDと初期状態 `queued` を返す |
| `submit_batch` | 複数操作を順番に依頼する | `schemaVersion`、`requestId`、`format`、JSON形式の`operations`／Aloe形式の`source`（排他）、`lockToken`（ロック中必須） | JSONバッチ全体を事前検証後、1件としてキューへ受付。要求IDと初期状態 `queued` を返す |
| `get_request` | 実行状況・結果を確認する | `requestId` | 現在状態、完了した操作別の結果、または要求／操作のエラー |
| `cancel_request` | 待機中または実行中の要求を取消す | `requestId` | 取消後または現在の状態。実行中は協調的な取消を要求 |
| `lock_mcp` | MCPの操作受付を排他占有する | `acquireTimeoutMs`、`leaseSeconds`（省略可） | 取得時に`lockToken`、`expiresAt`。取得不能なら`MCP_LOCK_TIMEOUT` |
| `renew_mcp_lock` | 排他権の期限を延長する | `lockToken`、`leaseSeconds`（省略可） | 更新後の`expiresAt` |
| `unlock_mcp` | 排他権を解放する | `lockToken` | 解放結果。所有者の処理が残る場合は`MCP_LOCK_BUSY` |
| `is_mcp_locked` | 排他権の状態を照会する | なし | `locked`、`ownedByCaller`、`expiresAt`、`remainingMs` |
| `register_signal` | 条件を登録し監視を開始する | `lockToken`、`condition`、`retentionSeconds`（省略可） | 監視開始確認後に`signalId`、初期状態、有効期限を返す |
| `wait_for_signal` | 登録した条件の成立を待つ | `schemaVersion`、`requestId`、`lockToken`、`signalId`、`timeoutMs` | 非同期でキュー受付。結果は`get_request`で取得 |
| `unregister_signal` | 条件の登録・監視を解除する | `lockToken`、`signalId` | 解除結果。実行中の当該シグナル待機があれば解除理由で終了 |

- `execute_operation` と `submit_batch` は非同期受付です。処理結果は `get_request` で取得します。閲覧系ツールは即時応答します。
- 占有中の`execute_operation`／`submit_batch`はトップレベルに`lockToken`を必須とし、受付時と各操作開始時に所有権を検証します。`wait_for_signal`も同じ非同期受付・要求ID規則を使用します。その他のシグナル／ロック管理ツールはバッファを経由せず、登録確認・取得待ち等の完了時に応答します。
- 操作用入力は `schemaVersion: 1`、UUID形式の `requestId`、`operation` を含みます。JSONバッチは `format: "json"` と `operations`、Aloeバッチは `format: "aloe"` とUTF-8の `source` を渡します。両方を同時指定できません。
- 現在のJSONバッチ受付は`format: "json"`と1～`MaxBatchOperations`件の`operations`配列を受け付けます。配列内の全操作をディスパッチ表で検証してから1つの要求としてキューへ登録し、1件でも不正ならキューへ登録しません。Aloe形式はP15-07実装まで`ALOE_UNAVAILABLE`です。
- JSONバッチはFIFO上で1要求として逐次実行し、要求結果の`operations`配列に各操作のindex・name・state・startedAt・finishedAt・data・errorを実行順に記録します。操作が失敗・取消・タイムアウトした場合は要求を終端し、未開始の後続操作を`skipped`として記録します。要求タイムアウトはバッチ全体で共有し、操作タイムアウトは各操作の開始時に計測し直します。要求全体の終了理由はREQUEST_TIMEOUT、TIMEOUT、CANCELLEDまたはINTERNAL_ERRORで返します。
- バッチ全体のスキーマとコマンドを受付前に検証します。ウィンドウの存否や権限は実行直前にも検証します。
- 操作ディスパッチは`name`と`args`の2項目を必須とし、操作名を大文字小文字を区別して照合します。未登録の操作、未知・重複プロパティ、必須引数不足、型・範囲・列挙値不一致は`INVALID_ARGUMENT`です。引数なしの操作も`args: {}`を指定します。
- 引数スキーマはオブジェクト、文字列（長さはUTF-16コード単位）、整数（Int64の整数表記）、有限数、真偽値、配列の入れ子に対応し、型の自動変換や暗黙の既定値挿入は行いません。任意引数の既定動作と対象状態・権限の確認は操作ハンドラーで行います。
- 操作名・スキーマ・ハンドラーは起動時に登録し固定します。重複名や不正な定義は登録エラーです。引数間の条件は登録した副作用のない追加検証で確認します。検証済み引数を複製して保持し、同じ登録表のハンドラーへ渡します。P03-01時点では登録済み操作はなく、各後続機能で実装済みの操作を追加します。
- 受理した要求はプロセス内の単一ワーカーでFIFO順に実行します。待機要求数のみを`QueueCapacity`（1～1000）と比較し、実行中要求は待機数に含めません。満杯なら新しい要求を受付前に拒否します。
- 受付から`QueueTimeoutSeconds`（1～3600）以内にワーカーが開始できない要求は、単調時計で期限を判定し、実行ハンドラーを呼ばず状態`timed_out`／エラー`TIMEOUT`で完了させます。実行中要求が期限後までワーカーを占有している場合は、ワーカーが次にその待機要求を確認した時点で期限切れを確定します。期限後に取消が競合した場合も`timed_out`を優先します。
- 操作開始から`OperationTimeoutSeconds`（1～300）を超えた場合、操作へキャンセルを通知し、停止が確認できた後に要求を状態`timed_out`／エラー`TIMEOUT`で完了させます。期限前に明示取消が要求され、その操作がキャンセル例外で終了した場合は`cancelled`を優先します。ハンドラーがキャンセルを無視する場合は終了するまでワーカーを占有し続け、次の要求を開始しません。タイムアウトは既に発生した副作用を取り消す保証ではありません。
- 要求開始から`RequestTimeoutSeconds`（1～3600）を超えた場合もキャンセルを通知し、停止確認後に状態`timed_out`／エラー`REQUEST_TIMEOUT`で完了させます。操作期限との競合は先に到達した期限を優先します。要求期限は要求全体で共有し、操作ごとにリセットしません。明示取消が最初の期限より前に要求され、その操作がキャンセル例外で終了した場合は`cancelled`を優先します。キャンセルを無視するハンドラーが終了するまではワーカーを占有し続けます。既に発生した副作用を取り消す保証はありません。
- 完了要求は`ResultRetentionMinutes`経過時に期限切れとし、定期整理と要求の受付・照会・取消時に削除します。保持する完了結果は最大1000件とし、上限超過時は完了時刻の古いものから削除します。待機中・実行中の要求は件数整理で削除しません。削除後の要求IDは`REQUEST_NOT_FOUND`となり、同じ所有者が再利用できます。保持はメモリ内のみで、再起動後は復元しません。
- キュー項目は要求ID・検証済み論理所有者ID・受付時刻・状態を保持します。受付直後は`queued`、ワーカー取得後は`running`、完了時は終端状態になります。ホスト終了時は待機要求を`cancelled`にし、実行中処理へキャンセルを通知します。再起動後の復元はしません。
- ウィンドウ参照はサーバが発行するプロセス内限定の `windowId` とし、タイトルだけで操作対象を決めません。終了・再生成された対象は無効として扱います。
- P06-02のlist_windowsと共通操作window.listは同じハンドラーを使用します。現在のデスクトップのトップレベルウィンドウ（非表示を含む）を列挙し、レジストリのSID・セッション・世代照合を通った対象だけを返します。省略・空文字はフィルタなし、titleContainsは取得タイトルの部分一致（最大1024文字）、processNameは拡張子なしのプロセス名の完全一致（最大256文字）です。比較はOrdinalIgnoreCase、NULはINVALID_ARGUMENT、未知のJSON引数は拒否します。
- 一覧のdataは{windows,truncated,skippedCount}です。各行にwindowId、title、processId、processName、visible、minimized、maximized、bounds:{x,y,width,height}、titleTruncatedを返します。矩形は画面座標の物理ピクセルで、DPI認識をスレッド内で一時切替して取得・復元します。列挙は最大4096対象、結果は最大1000件です。上限で打ち切った場合はtruncated:true、取得不能・権限不足・取得中の対象失効は行を除外しskippedCountへ加算します。skippedCountはフィルタ一致を確認できなかった対象も含みます。タイトルは最大4096文字でtitleTruncatedを付け、フィルタは取得できたタイトル部分に適用します。一致なしは空配列の成功です。返却直前に参照を再照合し、取得内容やタイトル・フィルタ本文をログへ保存しません。
- list_windowsは認証済みの即時照会としてMCPロック中も利用でき、window.listは単体／JSONバッチのキュー・ロック・取消・期限規則に従います。呼出し取消はCANCELLED、全体の列挙失敗はINTERNAL_ERRORです。
- P06-03のget_windowと共通操作window.getは必須のwindowIdを受け取り、同じ取得ハンドラーで現在のWindowInfoを返します。ID形式はw_と32桁UUIDで、入力不正はINVALID_ARGUMENT、未知・失効・世代不一致はWINDOW_NOT_FOUND、権限不足はACCESS_DENIEDです。取得前と返却直前に参照を再照合し、入力IDを別対象へ再対応しません。返却項目とタイトル上限・物理ピクセル矩形は一覧の各行と同じです。タイトル・状態・矩形の読み取り処理を一覧と共有します。get_windowは認証済みの即時照会としてロック中も利用でき、window.getはキュー・ロック・取消・期限の規則を適用します。情報の取得内容や入力本文はログへ出しません。
- P06-01の設計判断（採用済み）：同一プロセス内のHWND再利用を識別するため、サーバごとにランダムな名前の専用ウィンドウプロパティと、再利用しない正の整数マーカーをSetPropWで付けます。HWND・PID・スレッドID・プロセス開始FILETIMEとマーカーを登録時および参照時に照合し、同じ生存対象には同じw_形式のUUIDを返します。ウィンドウ破棄時にプロパティが消えるため、再生成後に古いIDを引き継ぎません。PIDと開始時刻だけの案は同一プロセス内の再生成を区別できず、非同期破棄イベントだけの案は通知遅延があるため採用しません。
- ウィンドウ参照レジストリはWindowsユーザーSIDとセッションIDを照合します。別ユーザー・別セッション、または識別プロパティを書き込めない権限不足はACCESS_DENIEDとして拒否します。不正IDはINVALID_ARGUMENT、未知・失効・マーカー不一致はWINDOW_NOT_FOUNDです。プロパティの欠落を別ウィンドウへ暗黙に再対応しません。無効参照整理とホスト終了時に自分のマーカーだけを削除し、削除失敗は固定ログへ記録します。入力やアプリの表示内容はプロパティへ保存しません。
- レジストリは本体DIのSingletonとし、一覧・照会・操作の共通部品にします。実操作の直前にも参照を再照合します。ただし照合と外部ウィンドウ操作はOS上の単一トランザクションではなく、その間の対象変更を完全に原子的に防ぐ保証はしません。実機検証時にはHWND再利用、PID再利用、別ユーザー／セッション拒否、権限不足、プロパティ削除を確認します。

- P06-04は内部共通部品WindowCoordinatesをSingleton登録します。ClientToScreenとScreenToClientは、クライアント矩形の画面上の左上を原点にした物理ピクセル座標を相互変換し、変換結果と参照したClientGeometryを返します。この項目ではMCPツールを追加せず、後続の入力・画像取得機能で利用します。
- GetClientRectとMapWindowPoints（RECTを2点として渡す）でRTLを含む画面上のクライアント矩形を取得します。PhysicalDpiScopeは一覧・照会とも共有し、スレッドを一時的にPER_MONITOR_AWAREへ切り替えて終了時に元へ戻します。96 DPIへの追加換算は行いません。[MapWindowPointsの仕様](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-mapwindowpoints)に従い、変位0の成功と失敗を最終エラー値で区別します。
- 仮想画面の負の原点を保持し、範囲判定は右端・下端を含みません。クライアント矩形外、仮想画面外、MonitorFromPointで実モニターがない空白領域、整数オーバーフローはINVALID_ARGUMENTです。参照の失効・権限エラーはレジストリの規則を適用し、DPIコンテキスト切替失敗や無効な仮想画面サイズはINTERNAL_ERRORです。
- 変換前後にウィンドウ参照とクライアント矩形・仮想画面矩形を照合します。取得中または再照合時の矩形変更はWINDOW_GEOMETRY_CHANGEDとし、取消トークンも確認します。VerifyGeometryを後続操作の直前にも利用できますが、最終確認後の移動を原子的に防ぐ保証はありません。DPI差、RTL、負の原点、モニター間の空白、位置・サイズ変更は実機未検証です。

- P06-05のwindow.restoreは必須windowIdのみを受け、execute_operation／JSONバッチ経由の認証・所有者・キュー・ロック・取消・操作／要求期限に従います。未知の引数・不正IDはINVALID_ARGUMENT、失効はWINDOW_NOT_FOUND、権限不足はACCESS_DENIEDです。直接の即時MCPツールは追加しません。
- 表示中かつ最小化・最大化されていない対象は操作せず現在のWindowInfoを返します。それ以外は参照を再照合し、[ShowWindowAsync](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-showwindowasync)にSW_RESTOREを渡します。開始失敗時は参照を再照合し、有効ならWINDOW_STATE_CHANGE_FAILEDを返します。復元中は50ms間隔で現在情報と参照を読み返し、visible:true、minimized:false、maximized:falseの確認後にWindowInfoをdataとして返します。前面化やフォーカスの成功は保証しません。
- 復元が確認できない間は操作／要求期限まで待ち、既存のTIMEOUT／REQUEST_TIMEOUTを適用します。取消・緊急停止・終了時は確認待ちを停止します。ただしWindowsへ送信済みの非同期復元要求は取り消せず、期限超過・取消後にも対象アプリが処理する可能性があります。最終照合後の対象変更を原子的に防ぐ保証はありません。実機の復元・無応答・取消競合は未検証です。

- P06-06のwindow.activateは必須windowIdのみを受け、window.restoreと同じキュー・所有者・ロック・取消・期限規則を適用します。即時MCPツールは追加しません。最小化・非表示なら既存の復元処理を実行し、表示中の最大化ウィンドウはそのまま前面化します。
- 対象の前面化とフォーカスが既に確認できる場合は再要求せず、それ以外はSetForegroundWindowを1回呼びます。開始拒否は参照再照合後にFOCUS_FAILEDとし、開始後は50ms間隔、最大1秒でフォーカスを確認します。確認不能はFOCUS_FAILED、参照失効はWINDOW_NOT_FOUND、権限不足はACCESS_DENIED、先にキュー期限へ到達した場合はTIMEOUT／REQUEST_TIMEOUTです。
- 成功条件は、GetForegroundWindowが対象と一致し、GetGUIThreadInfoのactiveが対象、focusが対象自身または同じプロセスの子ウィンドウであり、対象・フォーカス先が有効で、メニュー・移動／サイズ変更モードでないことです。現在のWindowInfoを取得し、返却直前にも参照・フォーカスを確認します。後続入力直前用のVerifyInputTargetを共通部品として用意します。キー・マウスはこの操作では送信しません。
- Windowsの[前面化制限](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setforegroundwindow)に従い、疑似キー入力やスレッド入力の強制接続で回避しません。確認後のフォーカス変更を原子的に防ぐ保証はなく、送信済みの復元・前面化は取消時に元へ戻しません。モーダルウィンドウを対象自身と暗黙に置き換えず、前面化拒否・子ウィンドウのフォーカス・取消競合は実機未検証です。

- P06-07のwindow.minimizeは必須windowIdのみを受け、execute_operation／JSONバッチ経由で既存の認証・所有者・キュー・ロック・取消・操作／要求期限を適用します。即時MCPツールは追加しません。不正ID・未知引数はINVALID_ARGUMENT、参照失効はWINDOW_NOT_FOUND、権限不足はACCESS_DENIEDです。
- 既にminimized:trueなら追加要求せず現在のWindowInfoを返します。それ以外は参照を再照合し、[ShowWindowAsync](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-showwindowasync)へSW_MINIMIZEを1回送ります。開始失敗時は参照を再照合し、有効ならWINDOW_STATE_CHANGE_FAILEDです。開始後は50ms間隔で現在情報と参照を確認し、minimized:trueのWindowInfoをdataとして返します。visibleやmaximizedの値は取得値のまま返し、推測で補正しません。
- 復元と最小化は同じ状態変更・確認処理を利用します。状態が成立しない場合は既存期限まで待ってTIMEOUT／REQUEST_TIMEOUTとなり、取消時は協調停止します。送信済みの非同期最小化要求は取り消せず、取消・期限超過後に処理される可能性があります。SW_MINIMIZEによって次のウィンドウがアクティブになる場合がありますが、その対象の選択や確認は行いません。実機動作は未検証です。

- P06-08のwindow.maximizeは必須windowIdのみを受け、execute_operation／JSONバッチ経由で既存の認証・所有者・キュー・ロック・取消・操作／要求期限を適用します。即時MCPツールは追加しません。入力不正・未知引数はINVALID_ARGUMENT、参照失効はWINDOW_NOT_FOUND、権限不足はACCESS_DENIEDです。
- visible:true、minimized:false、maximized:trueなら再要求せず現在のWindowInfoを返します。それ以外は既存の状態変更処理で参照を再照合し、ShowWindowAsyncへSW_MAXIMIZEを1回送ります。開始失敗時は参照を再照合し、有効ならWINDOW_STATE_CHANGE_FAILEDです。開始後は50ms間隔で参照と現在情報を確認し、上記3条件を満たしたWindowInfoをdataとして返します。矩形などの値は現在情報の取得結果を使い、前面化・フォーカスの成功は保証しません。
- 最大化できない対象も強制的なスタイル変更は行わず、既存期限まで状態を確認してTIMEOUT／REQUEST_TIMEOUTを返します。取消は協調停止し、送信済みの非同期要求は取り消せません。実機の最大化・非対応対象・取消競合は未検証です。

- P06-09のwindow.closeは必須windowIdのみを受け、execute_operation／JSONバッチ経由で既存の認証・所有者・キュー・ロック・取消・操作／要求期限を適用します。即時MCPツールは追加しません。要求開始時の不正ID・未知引数はINVALID_ARGUMENT、未知・失効参照はWINDOW_NOT_FOUND、権限不足はACCESS_DENIEDです。
- 開始時に対象参照を照合し、[PostMessageW](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-postmessagew)でWM_CLOSEを1回送信します。送信失敗のエラー5はACCESS_DENIED、それ以外は参照を再照合して有効ならWINDOW_STATE_CHANGE_FAILEDです。送信後は50ms間隔で元のHWNDの消滅またはプロセス・スレッド・開始時刻の識別不一致を確認し、確認できた場合だけ参照を整理してdata:{windowId,closed:true}を返します。プロセス全体の終了を意味しません。
- 同じ識別情報のままマーカーだけが欠落した場合や、参照だけが登録表から失われた場合はWINDOW_NOT_FOUNDとし、終了成功へ置き換えません。同一プロセス・同一スレッドでHWNDが再利用され消滅を観測できなかった場合も、終了を断定しません。照合と送信をOS上で原子的に実行する保証はありません。
- [WM_CLOSE](https://learn.microsoft.com/en-us/windows/win32/winmsg/wm-close)は対象アプリが未保存確認等を表示して拒否・保留できます。ダイアログへの自動回答、WM_QUIT送信、プロセス強制終了、再送は行いません。閉じない場合は既存の操作／要求期限によるTIMEOUT／REQUEST_TIMEOUTを返し、WINDOW_CLOSE_TIMEOUTはこの実装では使用しません。取消は確認待ちを停止しますが送信済みWM_CLOSEを取り消せず、取消・期限超過後にも閉じる可能性があります。実機動作は未検証です。

- P07-01のmouse.clickはwindowId、非負整数x/y（各int上限まで）とbutton（left/right/middle、既定left）を受け、execute_operation／JSONバッチの認証・所有者・キュー・ロック・取消・期限に従います。未知引数・不正ID・座標範囲外はINVALID_ARGUMENTです。即時MCPツールは追加しません。
- 前面化の後にクライアント物理ピクセルを画面座標へ変換します。SendInputのMOVE／ABSOLUTE／VIRTUALDESKで仮想画面上のピクセル中央を正規化し、負の原点も扱います。整数正規化で指定ピクセルを表せない場合はINVALID_ARGUMENTです。カーソル移動の送出後は10ms間隔、最大1秒で指定位置を確認し、再度矩形・参照・前面・フォーカス・ヒット対象を確認してから押下と解放を1回のSendInputで送ります。
- クリック地点のWindowFromPointが対象自身または対象をルートとする子ウィンドウでない場合はFOCUS_FAILEDです。マウスキャプチャ中、マウスボタン・Shift/Ctrl/Alt/Winの押下中、またはキャプチャ状態を取得できない場合はINPUT_STATE_UNSAFEとします。利用者の入力状態を自動解除しません。範囲外・配置変更・参照失効は既存のINVALID_ARGUMENT／WINDOW_GEOMETRY_CHANGED／WINDOW_NOT_FOUND規則です。
- 移動・クリックの送出不足やカーソル位置未確認はINPUT_SEND_FAILEDです。押下だけが受理された場合は取消状態でも解放だけを1回試み、それも送出できなければINPUT_RELEASE_FAILEDとします。クリック自体は再送しません。成功dataは{windowId,x,y,button,inputSent:true,screenPoint:{x,y}}です。[SendInputの返却数](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-sendinput)による成功は入力ストリームへの送出を意味し、対象アプリの処理完了を保証しません。UIPI拒否の原因は返却値から断定せずINPUT_SEND_FAILEDにします。
- 移動送出後の失敗・取消でもカーソルは元へ戻しません。最終確認と送出間のウィンドウ・フォーカス・利用者入力の変化を原子的に防ぐ保証はなく、後続の入力処理でフォーカスが変わる可能性もあります。x86のINPUT配置、DPI・複数モニター、重なり、部分送出・解放失敗は実機未検証です。

- P07-02のmouse.double_clickはmouse.clickと同じwindowId、x/y、button（既定left）を受ける独立した操作です。座標変換・前面化・フォーカス・ヒット対象・キャプチャ・押下状態の確認、キュー・取消・期限規則を共通処理で適用します。単一クリック操作を2回キューへ登録する方式ではありません。
- カーソル位置確認後、同じボタンの押下・解放・押下・解放の4イベントを待機を挟まず1回のSendInputで送ります。送出4件で成功とし、dataは単一クリックの項目にclickCount:2を追加します。単一クリックの返却形式は変更しません。Windows設定や対象アプリの認識条件によっては2回のクリックとして処理されます。[ダブルクリックの認識](https://learn.microsoft.com/en-us/windows/win32/inputdev/about-mouse-input)や対象アプリの業務処理完了は成功応答では保証しません。システムのダブルクリック時間やウィンドウスタイルを変更しません。
- 4件未満の送出はINPUT_SEND_FAILEDです。奇数件の送出なら押下が残る可能性があるため解放だけを1回試み、失敗はINPUT_RELEASE_FAILEDとします。完了した1組を含む副作用は戻さず、失敗したダブルクリックも再送しません。一括送出開始後にクリック間へ取消や再照合を挟まず、対象アプリの1回目の処理でフォーカス等が変わる可能性は単一クリックと同様に保証範囲外です。実機動作は未検証です。

- P07-03のmouse.scrollはwindowId、非負整数x/y（各int上限まで）、delta（必須、-12000～12000の0以外の120の倍数）、axis（vertical/horizontal、既定vertical）を受けます。入力不正・未知引数はINVALID_ARGUMENTでキュー受付前に拒否します。execute_operation／JSONバッチ経由の認証・所有者・ロック・取消・操作／要求期限を適用し、即時MCPツールは追加しません。
- クリックと共通のカーソル移動・位置確認・参照・矩形・前面・フォーカス・ヒット対象・キャプチャ・押下状態の確認後、SendInputへ1件のWHEEL（縦）またはHWHEEL（横）を渡します。符号付きdeltaはmouseDataへ符号のビットを保持して格納します。[ホイール仕様](https://learn.microsoft.com/en-us/windows/win32/api/winuser/ns-winuser-mouseinput)に従い縦の正値は奥向き、負値は手前向き、横の正値は右、負値は左です。120は1ノッチ相当で、画面ピクセル・行数ではありません。
- 成功dataは{windowId,x,y,delta,axis,inputSent:true,screenPoint:{x,y}}です。1件の送出を確認できなければINPUT_SEND_FAILEDとし、再送しません。対象アプリ・Windows設定によりホイールの処理先や移動量が異なるため、特定コントロールのスクロールや表示内容の変化を保証しません。既存の範囲・フォーカス・入力状態エラーと、送出後の取消で副作用を元へ戻さない規則を適用します。縦横・正負・非対応コントロール・DPI・複数モニターでの実機動作は未検証です。

- P07-04のkeyboard.press_keyはwindowId、key、modifiers（配列、省略・空配列可、最大4件）を受けます。keyはA～Z、0～9、F1～F24、Enter、Tab、Escape、Backspace、Space、Insert、Delete、Home、End、PageUp、PageDown、Left、Up、Right、Downのいずれかです。modifiersはCtrl／Alt／Shift／Winで重複不可、名前は大文字小文字を区別します。数値VK、未登録名、未知引数、不正windowIdはINVALID_ARGUMENTで受付前に拒否します。
- execute_operation／JSONバッチの認証・所有者・ロック・取消・操作／要求期限を適用し、即時MCPツールは追加しません。前面化・フォーカスを確認し、VK 1～254のいずれかが押下中ならINPUT_STATE_UNSAFEとします。修飾キーは配列順に左側のCtrl/Alt/Shift/Winを押し、主キー押下後、主キーから修飾キーの逆順で解放します。全イベントを1回のSendInputで送り、拡張キーには[KEYEVENTF_EXTENDEDKEY](https://learn.microsoft.com/en-us/windows/win32/api/winuser/ns-winuser-keybdinput)を付けます。
- 全件の送出確認後にdata:{windowId,key,modifiers,inputSent:true}を返します。不足はINPUT_SEND_FAILEDとし、受理された先頭イベントを追跡して、今回押した未解放キーだけを逆順で解放する入力を1回送ります。解放の送出不足はINPUT_RELEASE_FAILEDです。主キーやショートカットを再送せず、利用者のキーを自動解除しません。ログへキー本文を保存しません。
- 成功は入力ストリームへの送出であり、ショートカットの業務処理完了や文字列への変換を保証しません。仮想キーは対象のキーボード配列・IME・OS処理の影響を受け、Win等の組合せはOSが処理する場合があります。送出開始後はイベント間へ取消を挟まず、既に発生した副作用を戻しません。確認と送出間のフォーカス変更を原子的に防ぐ保証はなく、x86のINPUT共用体配置、修飾・拡張キー、部分送出・解放失敗は実機未検証です。

- P07-05のkeyboard.type_textはwindowIdとtext（必須、1～65536 UTF-16コード単位）を受けます。空文字、NUL、対応しないサロゲート、不正ID・未知引数はINVALID_ARGUMENTで受付前に拒否します。HTTP本文サイズの上限も別途適用します。execute_operation／JSONバッチの認証・所有者・ロック・取消・操作／要求期限に従い、即時MCPツールは追加しません。
- 対象を前面化し、フォーカスと利用者の押下状態をキー送信と共通処理で確認します。KEYEVENTF_UNICODEの押下・解放をUTF-16コード単位ごとに作り、1回最大256コード単位（512イベント）ずつSendInputで送ります。サロゲートペアは送信単位の境界で分割しません。各送信前に取消・参照・フォーカスを再確認します。Ctrl+A、Enter、クリップボード操作や読戻しは追加せず、既存文字を置換する契約ではありません。
- 全件送出の成功dataは{windowId,utf16Length,inputSent:true}とし、本文を結果・ログへ含めません。不足はINPUT_SEND_FAILED、押下だけが受理された場合はそのUnicodeキーの解放を1回試み、解放失敗はINPUT_RELEASE_FAILEDです。文字列や残りの文字は再送せず、既に送った文字を削除しません。取消・期限超過も送出済みの副作用を戻しません。
- 成功は[KEYEVENTF_UNICODEによる入力送出](https://learn.microsoft.com/en-us/windows/win32/api/winuser/ns-winuser-keybdinput)を示し、独自描画UI・IME等での文字認識や表示値との一致を保証しません。明示した改行等の制御文字は対象アプリの処理に従います。送信単位の途中の取消やフォーカス変化を原子的に防ぐ保証はなく、途中で利用者がキーを押す可能性もあります。日本語・補助平面文字・上限文字数・部分送出・IMEは実機未検証です。

- P07-06のwaitはdurationMs（必須整数、0～30000）のみを受けます。未知引数、欠落、型・範囲の不一致はINVALID_ARGUMENTで受付前に拒否します。execute_operation／JSONバッチの認証・所有者・ロック・キュー・取消・要求／操作期限へ接続し、即時MCPツールは追加しません。
- Task.Delayで協調取消可能な待機を行い、待機前後にも取消を確認します。0は取消されていなければ即時成功です。成功dataは{durationMs,elapsedMs}で、elapsedMsは単調時計で測った実行開始後の経過ミリ秒（整数切捨て）です。キュー待ち時間は含まず、スケジューラにより指定時間より長くなる場合があります。
- 待機中はFIFOワーカーを占有し、バッチの次操作へ進みません。明示取消・ロック失効・緊急停止・終了には既存の取消規則、操作／要求期限にはTIMEOUT／REQUEST_TIMEOUTを適用します。指定時間を終えていない状態を成功としません。durationMs:30000でも既定の30秒操作期限が先に確定すればTIMEOUTとなり、待機用に期限を延長しません。実行・取消・期限競合は未検証です。

- P07-07のscreen.captureは必須windowIdのみを受け、execute_operation／JSONバッチの認証・所有者・ロック・取消・期限へ接続します。即時MCPツールは追加しません。不正ID・未知引数はINVALID_ARGUMENT、参照失効はWINDOW_NOT_FOUND、権限不足はACCESS_DENIEDです。
- 取得方式はvisible_clientです。表示中かつ最小化されていない対象のクライアント領域を物理ピクセルで[CopyFromScreen](https://learn.microsoft.com/en-us/dotnet/api/system.drawing.graphics.copyfromscreen)により読み取ります。タイトルバー・枠は含めず、前面化や復元は行いません。非表示・最小化・空領域・モニター領域外（モニター間の空白を含む）はCAPTURE_TARGET_UNAVAILABLEです。別ウィンドウの重なりは画像に映り、隠れた対象だけの描画や保護された画面の取得を保証しません。
- 取得前後・PNG変換後に対象参照とクライアント／仮想画面矩形を再照合し、変化はWINDOW_GEOMETRY_CHANGEDです。生画像は最大16777216ピクセル（32bpp、約64MiB）、PNGは最大8388608バイトです。上限超過はCAPTURE_TOO_LARGE、その他の画面コピー・画像変換失敗はCAPTURE_FAILEDです。PNGは書込み時にも上限を適用するメモリストリームで生成し、ディスク・ログへ保存しません。
- get_requestは成功したscreen.captureのPNGをimage/pngのMCP画像コンテンツとして返し、構造化dataには{windowId,width,height,bounds,source,byteLength,image:{mimeType,contentIndex}}を残します。boundsはクライアント矩形の画面座標です。contentIndexはcontent配列の0起点位置（先頭の応答テキストに続く画像位置）で、バッチの複数画像も操作ごとに対応します。PNGのbase64本文は構造化・テキスト応答へ重複掲載しません。画像は既存の結果保持期間・件数規則に従いメモリ保持します。
- 取消はコピー前・コピー後・PNG変換後に確認します。同期的な画面コピーやエンコードを実行中に強制中断する保証はなく、既存ワーカーは停止確認まで占有します。画面内容の変化や最終照合後の対象変更を原子的に防ぐ保証はありません。DPI、重なり、GPU／保護画面、PNG上限、画像表示は実機未検証です。

- P07-08のscreen.capture_desktopは引数なし（args:{}）で、未知引数はINVALID_ARGUMENTです。execute_operation／JSONバッチの認証・所有者・ロック・取消・期限へ接続し、即時MCPツールは追加しません。
- DPI認識を一時切替し、[EnumDisplayMonitors](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-enumdisplaymonitors)とGetMonitorInfoで現在のデスクトップのモニター矩形・主モニターを取得します。OSが列挙する論理モニターを対象とし、ミラー用の擬似モニターを含む場合があります。位置・サイズ・主フラグ順に並べ、その要求内のindexを付けます。indexは再接続を越える永続IDではありません。
- 各モニターの物理ピクセルを1枚の外接矩形画像へコピーし、モニター間の空白は黒とします。負の原点を保持し、画像内の原点は外接矩形の左上です。最大64モニター、外接矩形は最大33554432ピクセル（32bpp、約128MiB）、PNGは最大16777216バイトとし、上限超過はCAPTURE_TOO_LARGEです。モニターなし・無効領域はCAPTURE_TARGET_UNAVAILABLE、コピー・画像変換失敗はCAPTURE_FAILED、確認できた権限拒否はACCESS_DENIEDです。
- コピー後とPNG変換後にモニターの件数・矩形・主フラグを再列挙して比較し、不一致はDESKTOP_CONFIGURATION_CHANGEDとします。物理機器の同一性、同じ配置での機器交換、色形式や画面内容の変化を検出する保証はありません。モニターごとに順次コピーするため、全画面が同時刻の画像になる保証もありません。
- 成功dataは{width,height,bounds,source:"visible_desktop",byteLength,monitors:[{index,bounds,primary}],image:{mimeType,contentIndex}}です。get_requestでimage/pngのMCP画像コンテンツを返し、ウィンドウ画像と同じくPNG本文をテキスト・構造化応答へ重複掲載せず、ディスク・ログへ保存しません。各コピー前・構成再照合時に取消を確認し、同期コピー・PNG変換を途中で強制中断しない規則と既存の結果保持規則を適用します。複数モニター・負の原点・配置変更・16MiB上限・画像表示は実機未検証です。

| 操作API名 | 入力／動作 |
| --- | --- |
| `app.launch` | `launchTargetId`。`AllowedLaunchTargets`に登録した実行ファイルを固定引数で起動する。ウィンドウ生成は待たず、`applicationId`、PID、プロセス開始時刻を返す |
| `app.exit` | `applicationId`。登録済みアプリ実行インスタンスに通常の終了を要求し、対象プロセスの終了を確認する。終了待ち上限は操作タイムアウトに従う |
| `terminal.launch` | 引数なし。設定済みWindows Terminal起動対象を使い、Kugutsu管理用の新規ウィンドウとシェルセッションを起動する。`sessionId`、`applicationId`、`windowId`、シェルPIDを返す |
| `terminal.exit` | `sessionId`。そのセッションへ通常の終了を要求し、管理対象シェルの終了を確認する。他セッションやタブは終了しない |
| `terminal.send_keys` | `sessionId`、`text`（省略可）、`keySequence`（省略可）。指定セッションの入力先を確認して文字列・キー列を送る。入力送出結果を返し、コマンド完了を意味しない |
| `powershell.launch` | 引数なし。設定済みPowerShell起動対象を専用コンソールで起動する。`sessionId`、`applicationId`、`windowId`、シェルPID、PowerShell種別を返す |
| `powershell.exit` | `sessionId`。指定したPowerShellセッションへ通常の終了を要求し、シェルの終了を確認する |
| `powershell.send_keys` | `sessionId`、`text`（省略可）、`keySequence`（省略可）。PowerShellセッションへの入力。検証・送信規則は`terminal.send_keys`と同じ |
| `browser.get_default` | 引数なし。現在のWindowsユーザーのHTTP／HTTPS既定ハンドラーをそれぞれ取得し、ブラウザ種類、表示名、ProgID、実行パス、対応可否を返す |
| `browser.list_installed` | 引数なし。検出したインストール済みブラウザの一覧を返す。各項目は種類、表示名、版、実行パス、対応可否、HTTP／HTTPSの既定フラグ、ドライバー準備状態を含む |
| `browser.launch` | `browser`（必須、firefox/chrome/edge）、`url`（省略可、既定about:blank）。指定ブラウザを専用インスタンスとして起動し、種類と`browserId`、`tabId`、`applicationId`、`windowId`を返す |
| `browser.navigate` | `browser`、`browserId`、`tabId`、`url`。指定タブを絶対HTTP/HTTPS URLへ遷移させ、到達URLとページタイトルを返す |
| `browser.back` | `browser`、`browserId`、`tabId`。履歴を1つ戻る。`moved`と現在URL・タイトルを返し、戻れる履歴がない場合は`moved: false`とする |
| `browser.forward` | `browser`、`browserId`、`tabId`。履歴を1つ進む。`moved`と現在URL・タイトルを返し、進める履歴がない場合は`moved: false`とする |
| `browser.exit` | `browser`、`browserId`。Kugutsuが管理するブラウザインスタンスへ終了を要求し、終了を確認する。他のブラウザインスタンスは対象にしない |
| `dom.find` | `browser`、`browserId`、`tabId`、`selector`（CSS）、`framePath`（省略可、外側から順にiframeのCSSセレクター）。一致する要素の`domElementId`、タグ名、入力型、操作可能状態を返す |
| `dom.fill` | `browser`、`browserId`、`tabId`、`domElementId`、`text`。input/textarea/contenteditableの既存内容を置き換えて入力する |
| `dom.click` | `browser`、`browserId`、`tabId`、`domElementId`。ボタン、リンク等をクリックする。不可視・無効・遮蔽された対象にはエラーを返す |
| `dom.send_keys` | `browser`、`browserId`、`tabId`、`domElementId`、`keySequence`。対象DOM要素へEnterやショートカット等のキー列を送る |
| `dom.get_value` | `browser`、`browserId`、`tabId`、`domElementId`。入力欄の現在値を取得する。contenteditableではテキストを返し、パスワード値は既定で伏せる |
| `window.list` | `titleContains`、`processName`（省略可）。windowId、タイトル、PID、プロセス名、表示／ウィンドウ状態、矩形を返す |
| `window.get` | `windowId`。該当ウィンドウの現在情報を返す |
| `window.activate` | `windowId`。最小化を復元し前面化、成功を確認する |
| `window.open` | `launchTargetId`、`initialState`（normal/maximized、既定normal）。`app.launch`と同じ起動処理の後、対象プロセスのウィンドウ生成を待ち、`applicationId`と`windowId`を返す。起動済みアプリに追加ウィンドウを作る機能は含まない |
| `window.close` | `windowId`。対象ウィンドウへ通常の終了要求を送り、閉じたことを確認する。未保存確認等で閉じない場合はタイムアウトとし、プロセスを強制終了しない |
| `window.minimize` | `windowId`。対象ウィンドウを最小化し、状態を確認する |
| `window.maximize` | `windowId`。対象ウィンドウを最大化し、状態を確認する |
| `window.restore` | `windowId`。最小化または最大化状態を通常サイズへ戻し、状態を確認する |
| `control.find` | `windowId`、`driver`（既定uia）、`automationId`、`name`、`controlType`等。指定ドライバで探索し、`elementId`と候補情報を返す。画像ドライバの意味判定・プロファイルは8.3節に従う。複数一致時は候補一覧を返し、暗黙に1つを選ばない |
| `control.invoke` | `windowId`と`elementId`。ボタン等のInvokeパターンを実行する。「押す」に相当する意味操作で、画面座標のクリックを必要としない |
| `control.set_value` | `windowId`、`elementId`、`value`。uiaではValueパターンへ文字列を設定する。画像ドライバでは8.3節のmethod指定・入力手順に従う。最大65536文字 |
| `control.get_value` | `windowId`、`elementId`。ValueまたはTextパターンから値を取得する。機密情報属性がある要素は既定で値を伏せ、明示的に許可された場合だけ返す |
| `control.send_keys` | `windowId`、`elementId`、`keySequence`。対象コントロールへフォーカスを設定・確認後、指定キー列を順に送信する。テキスト設定では表現できないTab移動、ショートカット、特殊キーに使う |
| `control.select` | `windowId`、`elementId`、`item`（省略可）。`SelectionItem`要素（ラジオボタンやリスト項目）を選択する。`item`を指定した場合は選択コントロール内から`automationId`または`name`で項目を一意に特定して選ぶ（プルダウン等） |
| `control.set_toggle` | `windowId`、`elementId`、`isOn`。チェックボックス等のToggle要素を指定状態へ設定する。現在状態を確認し、すでに一致していれば再トグルしない |
| `control.set_range_value` | `windowId`、`elementId`、`value`。スライダー等のRangeValue要素を指定値へ設定する。有効な最小値・最大値・刻み幅を要素から取得して範囲と精度を検証する |
| `control.expand_collapse` | `windowId`、`elementId`、`action`（expand/collapse）。ツリー項目、メニュー、折りたたみ領域を指定状態へ開閉する |
| `control.scroll` | `windowId`、`elementId`、`direction`（up/down/left/right）、`step`（small/large）、`repeat`（1～20）。スクロール可能な領域を指定方向へ動かし、位置を返す |
| `control.get_state` | `windowId`、`elementId`。有効／無効、表示／画面外、フォーカス、展開、Toggle状態、選択状態、対応パターンなどを取得する。機密属性付きValueは伏せる |
| `control.select_items` | `windowId`、`elementId`、`items`（一意な子`elementId`の配列）、`mode`（replace/add/remove）。複数選択可能なリストの選択項目をまとめて設定する |
| `control.set_text_selection` | `windowId`、`elementId`、`start`、`length`。テキスト範囲を選択する。位置と長さはUTF-16コード単位で指定する |
| `grid.get_cell` | `windowId`、`elementId`、`row`、`column`（0起点）。グリッドセルを取得し、名前、型、Valueの有無、利用可能な操作パターンを返す |
| `grid.set_cell_value` | `windowId`、`elementId`、`row`、`column`（0起点）、`value`（文字列）。編集可能でValueパターンを公開するセルの値を設定し、結果を読み返す |
| `mouse.click` / `mouse.double_click` | `windowId`、`x`、`y`、`button`（left/right/middle、既定left）。クライアント領域左上基準の物理ピクセル座標 |
| `keyboard.type_text` | `windowId`、`text`（最大65536文字）。フォーカス確認後にUnicode入力を送る |
| `keyboard.press_key` | `windowId`、`key`、`modifiers`（Ctrl/Alt/Shift/Win、省略可）。許可されたキー名を検証する |
| `mouse.scroll` | `windowId`、`x`、`y`、`delta`（符号付きホイール量、120単位）、`axis`（vertical/horizontal、既定vertical） |
| `wait` | `durationMs`（0～30000）。取消可能な待機 |
| `screen.capture` | `windowId`で指定したウィンドウの表示内容を取得する。PNGをMCP画像コンテンツとして返す。最大8 MiB、ディスクに保存しない |
| `screen.capture_desktop` | 仮想デスクトップ全体（接続中の全モニター）を取得する。PNGをMCP画像コンテンツとして返し、モニターごとの仮想デスクトップ上の矩形も構造化データで返す。最大16 MiB、ディスクに保存しない |
| `ui.list_drivers` | 引数なし。登録ドライバ、利用可否、理由、探索・操作能力とプロファイル版を返す |
| `ui.snapshot` | `windowId`、`driver`。画面構造と操作候補を取得する。`profileId`（省略可）、`includeImage`（既定false）。`snapshotId`と候補一覧を返す |

- マウス・キー入力は対象の前面化とフォーカスを確認してから送ります。範囲外座標や対象を確認できない場合は入力を送らずエラーです。成功はWindowsへの入力送出成功を意味し、対象アプリの業務処理完了を保証しません。
- 既定のuiaドライバではWindows UI Automationを用い、対象ウィンドウの配下から要素を特定します。`elementId`は同一プロセス実行中だけ有効です。`control.send_keys`は要素をフォーカス可能と確認し、前面化後に送信します。キー列は1～100項目とし、各項目は許可されたキー名と修飾キー（Ctrl/Alt/Shift/Win、省略可）の組み合わせです。`control.select`と`control.select_items`は選択後の状態を確認します。`control.select_items`は複数選択を公開するリストだけに使えます。複数項目が一致する場合は選択せずエラーにします。`control.set_toggle`はチェック状態を読み、要求状態と異なる場合にだけ切り替えた後、結果状態を確認します。`control.set_range_value`と`grid.set_cell_value`は値域・編集可否を検証し、設定後に実値を読み返します。`control.expand_collapse`は要求状態と異なる場合だけ実行し、状態を読み返します。`control.scroll`はSmallまたはLargeのUI Automationスクロール単位を指定回数適用し、最後に位置を取得します。`control.set_text_selection`のstartとlengthはTextパターンの文字位置として扱い、取得可能な範囲を超えた場合は拒否します。グリッドのrowとcolumnは0起点とし、仮想化セルはUI Automationが提供する場合に限り実体化して処理します。UI Automationで必要なパターンを公開しない独自描画コントロールは、呼び出し元が座標・キー操作APIまたは8.3節の画像ドライバを明示指定します。自動で操作方法を切り替えません。対応パターンがない、要素が曖昧、または要素が失効した場合は専用エラーを返します。
- 応答はMCPの `structuredContent` に `{schemaVersion, ok, requestId, state, data, error}` を格納します。情報取得ツールではrequestIdとstateはnull、受付時はstateがqueued、状態照会では現在状態です。
- 完了結果のdataは `operations` 配列を含み、各要素は `{index, name, state, startedAt, finishedAt, data, error}` です。失敗後の後続操作は `skipped` とします。時刻はUTCのISO 8601です。
- 操作エラー時は `ok: false`、MCPの `isError: true`、`error: {code, message, retryable}` を返します。未完了はエラーではありません。`ok: true` は受付／照会成功であり、操作完了はstateで判断します。
- 共通契約はKugutsu.ContractsのToolResponse、OperationResult、ToolErrorで定義します。schemaVersionは1、定義された状態・コードは文字列とし、nullの項目も省略しません。情報取得はrequestId／stateがnull、受付前の拒否はstateがnullで、有効な要求IDがある場合だけrequestIdを返せます。queued／running／succeededはokがtrue、failed／cancelled／timed_outはokがfalseです。失敗状態の照会でも取得済み操作結果はdataに保持できます。
- messageは定義済みエラーコードから生成する固定診断文です。任意の入力・例外本文は受け取りません。retryableは既定falseとし、担当ハンドラーが再試行可能と判断した場合だけtrueを指定します。trueでも副作用のある操作を暗黙に再送しません。cancelledにはCANCELLED、キュー待ちまたは操作期限切れにはTIMEOUT、要求全体期限切れにはREQUEST_TIMEOUTを対応させます。認証のHTTPエラーは共通ツールエラーへ変換しません。
- 操作番号は0起点で連続させ、停止した操作の後続結果はskippedとします。skippedは時刻・data・errorがnull、runningは開始時刻のみ、完了操作は終了時刻を持ちます。時刻はUTCへ正規化します。要求・操作状態とエラーの矛盾、未定義状態・コード、終了時刻の逆転、空UUID、未定義JSON値は契約生成時に拒否します。応答データは元のJSON文書の破棄や配列変更の影響を受けないスナップショットです。
- ToolCallResultはstructuredContentのJSONを同一内容のtext contentにも格納し、isErrorをokの反転から生成します。JSON-RPCの外側の包絡、HTTP処理、ツールへの接続は後続実装で扱います。text content併記は[MCPの構造化結果に関する互換性規定](https://modelcontextprotocol.io/specification/2025-06-18/server/tools#structured-content)に従います。
- エラーコードは `INVALID_ARGUMENT`、`QUEUE_FULL`、`REQUEST_ID_CONFLICT`、`REQUEST_NOT_FOUND`、`WINDOW_NOT_FOUND`、`LAUNCH_TARGET_NOT_ALLOWED`、`WINDOW_LAUNCH_FAILED`、`WINDOW_CLOSE_TIMEOUT`、`WINDOW_STATE_CHANGE_FAILED`、`WINDOW_GEOMETRY_CHANGED`、`ACCESS_DENIED`、`FOCUS_FAILED`、`INPUT_SEND_FAILED`、`INPUT_RELEASE_FAILED`、`INPUT_STATE_UNSAFE`、`CAPTURE_FAILED`、`CAPTURE_TOO_LARGE`、`CAPTURE_TARGET_UNAVAILABLE`、`DESKTOP_CONFIGURATION_CHANGED`、`UIA_ELEMENT_NOT_FOUND`、`UIA_ELEMENT_AMBIGUOUS`、`UIA_ELEMENT_STALE`、`UIA_PATTERN_UNSUPPORTED`、`UIA_NOT_SELECTABLE`、`UIA_NOT_EXPANDABLE`、`UIA_NOT_SCROLLABLE`、`UIA_TEXT_RANGE_UNAVAILABLE`、`UIA_CELL_NOT_EDITABLE`、`TIMEOUT`、`REQUEST_TIMEOUT`、`CANCELLED`、`ALOE_UNAVAILABLE`、`ALOE_COMPILE_ERROR`、`ALOE_RUNTIME_ERROR`、`INTERNAL_ERROR` とします。認証エラーはHTTP 401/403で処理します。

```json
{
  "schemaVersion": 1,
  "requestId": "cfbe1fa8-6034-44c4-af7a-e67383793951",
  "format": "json",
  "operations": [
    {"name": "window.activate", "args": {"windowId": "w_123"}},
    {"name": "keyboard.type_text", "args": {"windowId": "w_123", "text": "Hello"}}
  ]
}
```

- アプリ操作も`execute_operation`／`submit_batch`を通じて順次実行します。`applicationId`は起動時に発行するプロセス内限定の識別子です。PIDと開始時刻を照合し、PIDの再利用による別プロセスの操作を防ぎます。
- `app.exit`は対象プロセスのウィンドウに通常の終了を要求します。`window.close`は指定した1つのウィンドウが閉じた時点で成功、`app.exit`は登録した対象プロセスが終了した時点で成功です。未保存確認には自動回答せず、プロセスが残れば`APP_EXIT_TIMEOUT`を返します。通常終了手段がない場合は`APP_EXIT_UNSUPPORTED`です。
- 起動済みの単一インスタンスへ起動処理が転送された場合は、対象インスタンスを一意に照合できたときだけ登録します。プロセス名一致だけで既存プロセスや子プロセスをまとめて終了しません。対象を特定できなければ`APP_INSTANCE_UNRESOLVED`を返します。
- アプリ操作固有のエラーは`APP_NOT_FOUND`、`APP_LAUNCH_FAILED`、`APP_INSTANCE_UNRESOLVED`、`APP_EXIT_UNSUPPORTED`、`APP_EXIT_TIMEOUT`とします。未登録の起動対象には`LAUNCH_TARGET_NOT_ALLOWED`を返します。

- ターミナル／PowerShell操作も操作バッファ経由で実行します。`sessionId`は本体プロセス内限定の識別子で、管理対象シェルのPID・開始時刻・ウィンドウ・入力先を関連づけます。`powershell.*`はPowerShell種別のセッションだけを受け付けます。既存の任意ターミナルへ暗黙に接続しません。
- 起動対象はセットアップが検出し、`AllowedLaunchTargets`へ固定パス・固定引数で登録します。ターミナルは新規ウィンドウを要求し、シェル／入力先を一意に確認できなければ起動成功とはしません。Windows Terminal未導入の場合はエラーを返し、自動インストールや別アプリへの切替は行いません。
- `send_keys`は`text`と`keySequence`の少なくとも一方を指定します。両方指定時は文字列、キー列の順に送ります。文字列は最大65536文字、キー列は最大100項目で、各項目はキー名と修飾キーです。Enterを暗黙に追加しません。入力にはシェルコマンドも指定でき、その内容は通常のシェル権限で実行されます。内容をログへ保存しません。
- キー送信時は対象ウィンドウとセッション入力先のフォーカスを検証します。セッション内で対話プログラムが実行中の場合、そのプログラムがキー入力を受け取ります。フォーカスやセッションを特定できない場合は送信せずエラーです。終了時はシェルプロンプト未確認のまま`exit`文字列を送らず、利用可能な通常終了手段を使います。終了しない場合はタイムアウトとし、強制終了は行いません。
- ターミナル操作固有のエラーは`TERMINAL_NOT_INSTALLED`、`POWERSHELL_NOT_FOUND`、`TERMINAL_LAUNCH_FAILED`、`SESSION_NOT_FOUND`、`SESSION_TYPE_MISMATCH`、`SESSION_TARGET_UNRESOLVED`、`SESSION_EXIT_UNSUPPORTED`、`SESSION_EXIT_TIMEOUT`とします。起動許可とフォーカスのエラーは既存コードを使用します。

- ブラウザ操作も操作バッファ経由で実行します。初期版からFirefox、Google Chrome、Microsoft Edgeに対応します。SeleniumのC#ライブラリを用い、起動・遷移・DOM検索・入力・クリックは共通のWebDriver操作に対応づけます。イベントと履歴観測は利用可能なWebDriver BiDi機能を使い、機能差はブラウザアダプターが吸収します。CDPだけに依存する設計は採用しません。
- Firefoxはgeckodriver、ChromeはChromeDriver、EdgeはMicrosoft Edge WebDriverを使用します。セットアップでブラウザ版とOSに適合するドライバーを公式配布元から導入し、bin/drivers配下へ版別に配置します。ブラウザ更新後は互換性を確認し、不適合ならエラーで必要なドライバー更新を示します。ブラウザ自体は自動インストールしません。Kugutsuはx86プロセスのまま、ドライバーとはloopbackで通信します。
- ブラウザ起動時には一時プロファイルとloopback限定のWebDriver接続を生成します。起動に必要な引数はアダプターが生成し、呼び出し元から任意の起動引数は受け付けません。接続先はMCPの返却値やログに公開しません。既存のブラウザや既存ユーザープロファイルへ接続しません。通常終了時に一時プロファイルを削除し、異常終了による残留は次回起動時に管理対象の一時領域だけを確認・清掃します。
- ブラウザ・DOM操作の`browser`は必須で、firefox/chrome/edgeのいずれかです。browserIdは同じ種類の複数インスタンスを識別します。登録された種類と引数が異なる場合は`BROWSER_TYPE_MISMATCH`を返し、操作しません。ブラウザ種類を指定しても、実行中の別インスタンスへ自動切替はしません。
- `browserId`と`tabId`は本体プロセス内限定の識別子です。管理対象インスタンスとタブの対応を操作前に検証し、タブが閉じられた場合はエラーとします。起動時には最初の管理対象タブを返し、別の既存タブへ勝手に切り替えません。
- `browser.get_default`と`browser.list_installed`は起動を伴わない照会操作で、ブラウザ種類引数は不要です。既定ブラウザはKugutsuの設定値ではなくWindowsの有効なHTTP／HTTPS関連付けから取得し、両者が異なる場合もそのまま返します。未設定や識別不能の場合はnullとreasonを返し、Edgeなどを推定で補いません。Windowsの既定設定は変更しません。
- 一覧はユーザー単位／マシン単位の登録、32bit／64bit登録、設定済み実行パスから検出し、存在・版を確認して実行パスで重複をまとめます。登録から識別できる非対応ブラウザも`browser: other`と`supported: false`で返します。未登録のポータブル版まで網羅する保証はせず、検出範囲を応答に明記します。導入済みでもドライバー未準備なら`driverReady: false`です。
- 照会失敗は`BROWSER_DISCOVERY_FAILED`、部分的な検出失敗は一覧とwarningsを返します。正常に検出して0件なら空配列です。既定の種類を取得後、呼び出し元がbrowser.launchへ具体的な種類を明示して渡します。
- `browser.launch`の省略時about:blank以外のURLと`browser.navigate`のURLはHTTP/HTTPSのみを許可し、無効URLやfile/javascript/data等のスキームを拒否します。URL文字列は最大8192文字とします。通常の文書遷移はDOMContentLoaded相当まで待ち、同一文書内の移動や履歴キャッシュからの復元はURL・履歴位置の変化とブラウザの完了通知で確認します。操作タイムアウトを適用し、継続的な通信やアプリ固有の処理完了までは保証しません。
- 履歴がない場合の`browser.back`／`browser.forward`は成功応答で`moved: false`を返します。遷移待ちの失敗・タイムアウト時は取得可能な現在URLとともにエラーを返し、遷移前の状態に戻ったとはみなしません。
- DOM操作もJSON／Aloeの共通操作APIとして提供します。framePath省略時はトップ文書が対象です。CSSセレクターは最大4096文字、framePathは最大16階層とし、各iframeを一意に特定します。dom.findは候補一覧を返し、操作は選択したdomElementIdに対して実行します。
- domElementIdはbrowserId、tabId、フレーム、文書世代に紐づけます。文書遷移やDOM再生成で失効した要素はエラーとし、似た要素へ勝手に再解決しません。Shadow DOM内部は初期の通常CSS検索では横断せず、必要な場合は明示的な対応を追加します。
- dom.fillは最大65536文字とし、編集可能性を確認して既存内容をクリアし、WebDriverの入力操作を使います。ページの入力イベントを発生させ、JavaScriptでvalueだけを直接書き換えません。dom.send_keysは既存のキー列形式と100項目上限を使用します。入力成功はページ固有の保存処理完了を保証しません。入力値・DOM本文・認証情報をログへ保存しません。
- ブラウザ操作固有のエラーは`BROWSER_NOT_INSTALLED`、`BROWSER_DRIVER_INCOMPATIBLE`、`BROWSER_CAPABILITY_UNSUPPORTED`、`BROWSER_LAUNCH_FAILED`、`BROWSER_NOT_FOUND`、`BROWSER_TAB_NOT_FOUND`、`BROWSER_CONNECTION_LOST`、`BROWSER_NAVIGATION_FAILED`、`BROWSER_EXIT_TIMEOUT`、`DOM_ELEMENT_NOT_FOUND`、`DOM_ELEMENT_AMBIGUOUS`、`DOM_ELEMENT_STALE`、`DOM_ELEMENT_NOT_INTERACTABLE`、`DOM_FRAME_NOT_FOUND`とします。無効URLは`INVALID_ARGUMENT`、遷移待ちタイムアウトは`TIMEOUT`を使用します。

### 8.1. MCPグローバル排他ロック

- 排他権はKugutsu本体プロセス全体に1つです。MCP標準の機能ではなく、Kugutsu独自の操作受付制御です。ロック未取得時は従来のFIFO受付を使用でき、シグナル操作を使用する場合はロック取得を必須とします。
- 所有者はサーバが発行する論理クライアントセッションIDで識別します。プロキシ接続の初期化時に発行し、HTTP接続や要求ごとに変えません。再接続時はサーバ発行の再開資格情報を検証して同じセッションを復元します。クライアントの自己申告名や共有Bearerトークンだけでは所有者を識別しません。再開資格情報はプロキシのメモリで保持し、別プロキシへの暗黙の引き継ぎは行いません。
- P02-08の設計判断（採用済み）：MCP SDKの版やHTTPセッションモードに依存しない所有者を持つため、専用のHTTP発行エンドポイントと通常要求の資格情報を採用します。SDKのMcp-Session-Idは輸送上のIDとして区別し、共有Bearerやクライアント名を所有者として使う案は別プロキシを区別できないため採用しません。影響範囲はHTTP受付、プロキシ初期化、設定の発行上限です。MCP公開ツールは追加しません。
- 発行先は設定のMCP URLの末尾の`/`を除いて`/sessions`を付けたパスです（既定`/mcp/sessions`）。loopback／Host／Originと共有Bearerの検証を通常通信と共用します。POST、Content-Length:0、Transfer-Encodingなし、論理セッションヘッダーなしを要求し、不正形式は本文なし400です。正常時は201、Content-Type:application/json、Cache-Control:no-store、本文`{sessionId,credential,resumeCredential}`です。容量超過は本文なし503で既存セッションを維持します。
- sessionIdは本体発行のGUID N形式、credentialとresumeCredentialはそれぞれ独立した暗号学的乱数32バイトをBase64url・パディングなしの43文字へ変換したものです。本体は通常資格情報と再開資格情報のSHA-256ハッシュだけをメモリへ保持します。通常MCP要求の`X-Kugutsu-Session-Id`と`X-Kugutsu-Session-Credential`を各1値で検証し、固定時間のハッシュ比較で確定した所有者だけをHttpContext.Itemsの内部キーへ格納します。欠落、重複、不正形式、未知ID、不一致は本文なし403、Cache-Control:no-storeです。共有Bearer単独・ID単独・再開資格情報の通常ヘッダーへの流用では認証しません。OPTIONS到達確認は共有Bearerだけで許可します。
- 後続の要求処理はLogicalSessions.GetOwnerで検証済み所有者のスナップショットを取得し、キューへコピーします。HttpContextを要求終了後の操作バッファへ保持しません。プロキシは最初のMCPメッセージ前に1回発行し、同時要求は同じ初期化タスクを待ちます。以後のSDK送信に同じ論理ヘッダーを付け、再開資格情報はOPTIONS到達確認・通常MCP本文／ヘッダー・stdioへ出しません。資格情報の発行自体が結果不明の場合、別所有者を暗黙作成しないよう自動再試行せずMcpSessionFailedで終了コード1にします。401／403はMcpAuthenticationFailedです。
- プロキシの発行待機・読込みはRequestTimeoutSeconds以内、発行応答は最大2048バイトです。不正JSON・必須値不足・型不正・不正資格情報はMcpSessionFailedで停止します。プロキシ終了で再開資格情報の所有バイト列を消去し、本体終了でハッシュを消去します。HTTP JSON／ヘッダーに使う.NET文字列の消去は保証できないため、保存・ログ・診断・ツール結果への出力は禁止します。発行応答の未受信でも本体に所有者が残る可能性があります。
- 現段階のセッションは本体寿命中に保持し、MaxClientSessionsで発行数を制限します。プロキシの正常終了・通信切断だけでは削除せず、上限時に古い所有者を暗黙に追い出しません。所有者、排他権、結果の寿命を同じ値で安易に扱わないため、この段階では時間切れ整理を加えません。検証指示後の確認項目は別プロキシ・同名クライアント間の分離、共有Bearer単独／別資格情報／重複ヘッダー拒否、同時発行集約、容量制限、発行応答障害時の再送なし、秘密情報非出力、本体再起動時の失効です。
- P02-09の設計判断（採用済み）：既存所有者と再開資格情報の組を確認する冪等な再開エンドポイントを採用します。再開で新所有者を発行する案は既存要求・排他権の所有者を変え、資格情報を自動更新する案は同時要求や再開応答喪失時に整合を失うため採用しません。影響範囲は論理セッション照合、専用HTTP受付、プロキシの回復完了条件です。操作受付・取消・再送・MCPツール一覧は変更しません。
- 再開先は発行パスの末尾へ`/resume`を付けたパス（既定`/mcp/sessions/resume`）です。loopback／Host／Originと共有Bearerの検証を共用します。空POST、Content-Length:0、Transfer-Encodingなし、通常資格情報ヘッダーなしを要求し、不正形式は本文なし400です。所有者IDヘッダーと`X-Kugutsu-Session-Resume`をそれぞれ1値で受け取り、GUID N形式・Base64url 43文字と既存所有者の再開ハッシュを照合します。欠落、重複、未知ID、不一致は本文なし403、Cache-Control:no-storeです。発行側への再開ヘッダー指定も400で拒否します。
- 成功時は検証済みの同じ所有者をHttpContextへ設定し、HTTP 200、application/json、Cache-Control:no-store、本文`{sessionId,resumed:true}`を返します。所有者・通常資格情報・再開資格情報・発行数を変更しないため、再開要求や成功応答が通信で失われても同じ確認を再試行できます。別プロキシのIDと再開資格情報の組み合わせ、共有Bearer単独、通常資格情報の再開ヘッダーへの流用では再開できません。
- プロキシはOPTIONS到達確認後に、メモリで保持した自分のIDと再開資格情報だけを専用POSTへ付けます。HTTP 200、JSONの正確な2項目、sessionIdの一致、resumed:trueを最大2048バイト内で検証し、成功まで新規MCP送信を待機させます。通常SDKヘッダーへ再開資格情報を追加せず、再開応答をstdioへ転送しません。到達確認と再開確認を合わせた各試行は最大30秒、回復周期全体はRequestTimeoutSecondsです。
- 再開確認のネットワーク・IO障害、HTTP 408／429／500／502／503／504では既存のバックオフ周期へ戻ります。期限超過・EOF／Ctrl+Cで取消します。401／403はMcpAuthenticationFailed、それ以外の非成功HTTPや不正JSON・応答ID不一致・resumed不正はMcpSessionFailedで停止し、別所有者の発行へ戻りません。本体再起動で保持表が失われた場合も停止します。再開成功は論理所有者の確認であり、失効したMCP HTTPセッションの再初期化や操作の結果確認を意味しません。
- テスト指示後の確認項目は再接続前後の同じ所有者、再開成功前の送信抑止、別ID・別資格情報・重複ヘッダー拒否、通信障害時の再開確認の反復と発行数不変、応答ID不一致・不正JSONでの停止、本体再起動時の拒否、期限・EOF／Ctrl+C、操作の暗黙再送なし、秘密情報非出力です。
- `lock_mcp`の`acquireTimeoutMs`は0～300000、`leaseSeconds`は10～3600とします。省略時は設定値を使用します。取得待ちは受付順に処理し、取得待ちがある間の新しい操作要求には`MCP_LOCK_PENDING`を返します。既に受理した操作要求を完了させ、実行中・待機中要求のない境界で所有者と期限を原子的に設定します。待機が期限内に完了しなければ`MCP_LOCK_TIMEOUT`です。
- ロックは再入可能な参照カウント方式にしません。同じ所有者による再取得は既存のトークンと期限を返し、自動更新はしません。別所有者は取得待ちまたは即時失敗となります。新規取得時は推測困難な`lockToken`を生成し、期限更新は現在時刻から指定有効期間を設定します。
  - `renew_mcp_lock`は必須の`lockToken`と省略可能な`leaseSeconds`を受け付けます。期間は10～3600秒、省略時は`McpLockLeaseSeconds`です。認証済み呼出し元が現在の所有者で、期限内かつトークンが一致する場合に限り、現在時刻から期間を再設定します。所有者違いは`MCP_LOCKED`、トークン不一致は`MCP_LOCK_TOKEN_INVALID`、ロックなし・期限切れは`MCP_LOCK_EXPIRED`です。更新後の期限を`expiresAt`として返し、再取得にも更新後の期限を使用します。
- 有効なロックがない状態でも、失効済みのlockTokenを付けた要求を通常の非占有要求へ読み替えません。期限切れと解放済みのトークンは拒否し、再取得した新しいトークンを必要とします。
- 占有中の`execute_operation`／`submit_batch`はトップレベルの`lockToken`を要求し、論理セッションとトークンを受付時およびバッチ内各操作の開始時に検証します。シグナル登録・待機・解除、ロック更新・解放も両方を検証します。他所有者の操作は`MCP_LOCKED`、不正トークンは`MCP_LOCK_TOKEN_INVALID`、失効した排他権は`MCP_LOCK_EXPIRED`です。トークンと再開資格情報をログや照会結果へ出力しません。
- `get_version`、`list_windows`、`get_window`、`is_mcp_locked`は占有中も呼び出せます。`get_request`／`cancel_request`は要求の所有セッションだけが使用できます。要求IDの重複判定も所有者を照合し、他セッションへ要求結果を開示しません。閲覧でも`execute_operation`経由の場合は操作受付制御を適用します。
  - `unlock_mcp`は所有者の待機中または実行中の受付済み要求が残っていれば`MCP_LOCK_BUSY`として拒否します。取消が必要な場合は`cancel_request`を呼び、終了を確認してから解放します。正常解放時はそのロックに属するシグナルをすべて解除します。同じ所有セッション・同じトークンによる解放の再送は`ResultRetentionMinutes`の保持期間中に限り同じ成功結果を返します。再送記録はトークンそのものを保存せず、保持期限後または最大1000件を超えた場合に古いものから整理します。
- 通信切断だけでは即時解放しません。有効期限まで再接続・更新を可能とします。期限切れ時は所有者の未実行要求を取り消し、実行中の待機を中断し、進行中の操作には協調的停止を要求します。停止確認まで新しい所有者へ付与しません。停止を確認できない場合は操作受付を閉じたまま維持し、TaskTrayに復旧が必要な状態を表示します。外部アプリで既に開始した処理の終了や副作用の取り消しは保証しません。
- `is_mcp_locked`は`{locked, ownedByCaller, expiresAt, remainingMs}`を返します。未ロック時はfalse／false／null／0、時刻はUTCのISO 8601、残り時間は0以上です。期限切れ後の停止確認中も`locked: true`、`ownedByCaller: false`、`remainingMs: 0`とします。所有者のトークンは返しません。照会は取得の予約ではなく、取得可否は`lock_mcp`で原子的に判定します。
- TaskTrayに占有状態・有効期限と、ユーザーによる緊急停止・強制解除を用意します。強制解除も実行停止の確認後に解放します。AIから所有者確認を省略して強制解除するAPIは提供しません。
- トレイの「停止してロックを強制解除」は、全所有者の新規受付を停止し、待機要求を取り消し、実行中のハンドラーと取消通知の終了を確認した後にロックを解放します。確認中はリース失効でもロックを保持し、状態表示を継続します。期限内のロックに対する通常のunlock_mcpも確認中はMCP_LOCK_BUSYです。解放後はユーザーが操作受付を再開するまで停止状態を保持し、再開時にFIFOの取得待ちを再開します。シグナル資源はPhase 13の200ms周期寿命整理で取消・解放します。
- ロック状態はトレイのツールチップ・右クリックメニューと操作バッファ画面で1秒周期に更新します。未占有、占有中、リース失効後の停止確認中を区別し、有効期限はUTCで表示します。失効後にロックが保持されている間は復旧が必要な状態を明示します。表示用スナップショットに所有者ID・lockToken・資格情報を含めません。

### 8.2. MCPグローバルシグナル

- シグナル登録表はKugutsu本体プロセス全体で共有する揮発性データです。各登録は`signalId`、所有セッション、取得したロックの識別子、条件、登録時刻、有効期限、状態、成立時刻、観測結果を保持します。他所有者からの参照・待機・解除を拒否し、再起動後へ持ち越しません。
- `register_signal`は監視開始を確認してから返します。状態条件では監視を設置して初期状態も評価し、登録時点で成立済みなら即座に`signaled`とします。イベント条件は監視開始後の発生だけを対象とし、登録以前のイベントを遡って成立させません。
- 状態は`pending → signaled / expired / invalidated / unregistered`です。一度成立した結果を有効期間内で保持する一回限りのシグナルとし、待機で消費・再武装しません。待機前に成立していても即時返却し、再利用する場合は新しい条件を登録します。明示解除、期限切れ、対象失効、ロック終了時には監視資源を解放します。
- `condition`は下表の種類、対象、比較条件を持つ構造化データです。任意のC#／JavaScript式は受け付けません。初版は1登録につき1条件とし、複数条件のAND／ORは含めません。対象ID、ブラウザ種類、UI Automationパターン、比較値は登録時にも検証します。

| 条件の種類 | 対象・比較条件 | 判定方式 |
| --- | --- | --- |
| `window.state` | `windowId`、`property`（exists/active/minimized/maximized）、`equals`（真偽値） | 現在状態。exists:falseは対象が閉じた場合にも成立 |
| `window.opened` | `processName`または`launchTargetId`（少なくとも一方）、`titleContains`（省略可） | 登録後に出現した一致ウィンドウ。成立結果に`windowId` |
| `control.state` | `windowId`、`elementId`、`property`（enabled/visible/focused/toggle/selected）、`equals` | UI Automationの状態。visibleはIsOffscreenに基づき、遮蔽判定とは区別 |
| `control.text` | `windowId`、`elementId`、`comparison`（equals/contains）、`text` | ValueまたはTextパターンの文字列。パスワード要素は登録拒否 |
| `dom.state` | `browser`、`browserId`、`tabId`、`selector`、`framePath`（省略可）、`property`（exists/visible/enabled）、`equals` | 対象文書で都度検索。existsは1件以上、その他は一意な要素を必要とする |
| `dom.text` | DOM検索条件、`comparison`（equals/contains）、`text` | 一意な要素の表示テキスト。パスワード値を取得しない |
| `browser.url` | `browser`、`browserId`、`tabId`、`comparison`（equals/contains）、`url` | 指定タブの現在URL |

- `window.opened`はイベント監視を必須とし、サポートできない場合は`SIGNAL_CONDITION_UNSUPPORTED`を返します。状態条件はイベント通知を優先し、利用できない場合は設定間隔でポーリングします。ポーリング間隔より短い状態変化の検出は保証しません。曖昧な対象は既存の曖昧性エラー、対象失効・監視障害は`SIGNAL_TARGET_INVALIDATED`／`SIGNAL_MONITOR_FAILED`です。閉じる／消失を成立条件にしている場合は成立を先に評価します。
- `retentionSeconds`は1～3600、省略時は設定値です。`MaxSignals`超過は`SIGNAL_LIMIT_EXCEEDED`とし、登録を作成しません。登録上限には成立済みで保持中の登録も含めます。解除済み等の識別子と終了理由は結果保持期間まで軽量な記録として保持します。
- `wait_for_signal`は`timeoutMs`（必須、0～300000）を指定します。0は開始時点の即時判定です。結果は`{status: "signaled" | "timeout", signalId, signaledAt, observed}`とし、timeout時のsignaledAt／observedはnullです。規定時間内に成立しないことは通常結果（操作状態succeeded）として返し、バッチは継続します。要求・操作の上限時間が先に到達した場合は既存の`TIMEOUT`で停止します。
- 待機取消は`CANCELLED`、解除は`SIGNAL_UNREGISTERED`、登録期限切れは`SIGNAL_EXPIRED`、未知のIDは`SIGNAL_NOT_FOUND`、他所有者は`SIGNAL_ACCESS_DENIED`です。取消は登録自体を解除しません。シグナルと期限の競合はサーバ側で順序を確定し、待機開始前または待機期限までに記録した成立を優先します。
- JSON／Aloe共通操作APIにも`register_signal`、`wait_for_signal`、`unregister_signal`を同名で提供します。ロックの取得・更新・解放はバッチ外のMCP管理ツールで行い、ロック待ちによる操作バッファの自己待ちを防ぎます。バッチ内のシグナル操作は要求の所有者とロックを引き継ぎます。
- JSONバッチでは`register_signal`の引数に任意の`signalKey`を指定でき、後段の待機・解除は`signalId`の代わりにその`signalKey`を指定できます。キーは同一要求内限定、英数字とアンダースコアの1～64文字、重複不可です。両方の同時指定、未定義キー、登録より前の参照は`INVALID_ARGUMENT`です。Aloeは登録の戻り値signalIdを変数で渡します。単体MCP呼び出しはsignalIdを使用します。
- 単体・バッチとも、条件を成立させる操作は待機より前に配置します。待機中は後続の操作要求を実行しません。タイムアウト後に継続させたくない場合の条件分岐はAloeで記述します。登録と解除を一括しない場合も、ロック終了・登録期限による自動解除を適用します。

```json
{
  "schemaVersion": 1,
  "requestId": "4f70a81d-0d9a-4cb8-930d-8f3a5b63cf42",
  "lockToken": "<lock_mcpで取得したトークン>",
  "format": "json",
  "operations": [
    {"name": "register_signal", "args": {"signalKey": "done", "condition": {"kind": "control.text", "windowId": "w_123", "elementId": "e_status", "comparison": "equals", "text": "完了"}}},
    {"name": "control.invoke", "args": {"windowId": "w_123", "elementId": "e_start"}},
    {"name": "wait_for_signal", "args": {"signalKey": "done", "timeoutMs": 20000}},
    {"name": "unregister_signal", "args": {"signalKey": "done"}}
  ]
}
```

- 上記はlock_mcp取得後に送信します。get_requestでバッチ終了を確認後、unlock_mcpで解放します。長い待機・実行中はバッファ外でrenew_mcp_lockを呼びます。

### 8.3. UIドライバ、意味判定、アプリ別プロファイル

- UIドライバは構造情報、OCR、明示的ルールで操作候補を返します。Kugutsu内部にLLMを組み込まず、外部AIサービスへ画像・文字列を送信しません。OCRはローカル実行とし、機械学習を使用するOCR実装も許容します。曖昧な候補の選択と次の操作の判断は呼び出し元AIが行います。
- P08-01ではContractsにUiBounds、UiElementState、UiEvidence、UiCandidate、UiDriverInfo、UiProfileVersion、UiSnapshot、UiSnapshotImageを定義します。候補のJSON項目は下記の共通候補形式に従い、取得できない属性をnullで保持します。状態はenabled／visible／offscreen／focused／selected／expanded／toggleStateを独立した取得値として扱います。
- 候補のconfidenceは有限の0～1またはnullとし、値がある場合はconfidenceKindを必須にします。provider／ocr／rule_score等の種別はそのまま保持し、スコアから確率や成功保証を推測しません。候補の根拠はkind／text／ruleId／bounds、能力は対応操作名の一覧とします。根拠・能力・プロファイル版・候補のコレクションは入力をコピーして読取り専用にし、スナップショットはdriver／snapshotIdの所属一致を検証します。
- UiDriverInfoはdriver、available、unavailableReason、discoveryCapabilities、supportedOperations、profilesを持ち、未準備の場合は理由コードを必須、準備済みなら理由はnullです。UiSnapshotはwindowId／driver／snapshotId／candidates／truncatedと任意のimageを持ち、画像は最大8MiBのPNGデータをコピーして保持します。画像取得・MCP返却への接続は後続のスナップショット実装で行います。
- 本体のIUiDriverはInfo、FindAsync、SnapshotAsync、ExecuteAsyncを定義します。探索条件・深さ／候補上限・画像要求を明示し、操作委譲の対象はWindows参照、driver、elementId、snapshotId、profileId／profileVersionを保持します。呼出し元が参照・所有ドライバを照合して委譲し、実装ドライバは実入力直前にも対象を再確認します。未準備はUI_DRIVER_UNAVAILABLE、未対応はUI_OPERATION_UNSUPPORTEDです。他ドライバへの暗黙切替や候補の自動選択は含めません。
- この項目は共通型とインターフェースの実装です。ドライバ登録・UIA・探索・操作の実体はP08-02以降で接続し、この段階でMCPツールや操作登録を追加しません。UIA／画像ドライバの実機連携は未検証です。
- P08-02では本体SingletonのUiDriverRegistryへIUiDriver実装とEnabledUiDrivers設定を登録します。登録表と有効化設定は起動時に固定し、同名・未知名の実装登録を拒否します。設定の配列はコピーして保持し、各照会では登録実装のInfoから現在の利用可否・能力を読み取ります。ドライバ識別名の変更は内部エラーです。
- ui.list_driversはargs:{}のみを受ける操作です。未知引数はINVALID_ARGUMENTで拒否し、execute_operation／JSONバッチの認証・所有者・キュー・ロック・取消・期限に従います。即時MCPツールは追加しません。data:{drivers:[...]}の行はuia、imgui_visual、generic_visualの順で返し、driver、enabled、registered、available、reason、unavailableReason、discoveryCapabilities、supportedOperations、profilesを含みます。
- reasonは設定無効ならdisabled、実装登録なしならnot_implemented、登録済みだが未準備ならnot_ready、利用可能ならreadyです。availableは設定有効かつ登録済みかつInfo.Available:trueの場合だけtrueです。利用不可の場合はunavailableReasonに理由コードを返し、設定無効・未実装の既定コードはUI_DRIVER_UNAVAILABLEです。無効・未実装の能力とプロファイル版は空配列です。実装未準備でも一覧操作自体は成功します。
- 後続操作用のResolveは要求された既知名だけを解決し、未知名はINVALID_ARGUMENT、無効・未実装・未準備はUI_DRIVER_UNAVAILABLEです。別ドライバを探索して代替しません。P08-02の時点ではドライバ実体は未登録なので、既定設定の3種類はenabled:true、registered:false、available:false、reason:not_implementedを返します。UIA実体はP08-04、画像実体は後続フェーズで登録します。実機連携は未検証です。
- P08-03ではUiElementRegistryを本体Singletonとして登録します。登録時にWindows参照と利用可能な明示ドライバを照合し、e_と32桁UUIDのelementIdを発行します。Windowsの世代情報、driver、snapshotId、profileId／profileVersionと、ドライバ所有の不透明な対象参照を保持します。nativeTargetは外部応答・ログへ出さず、その内容の検証は所有ドライバが行います。IDはプロセス内限定で、既存IDを別対象へ付け替えません。
- ResolveはelementId、windowId、driverを照合します。形式不正はINVALID_ARGUMENT、未知・削除済みIDはUI_ELEMENT_NOT_FOUND、別ウィンドウ・別ドライバの指定はUI_ELEMENT_STALEです。誤指定だけでは正しい参照を削除しません。対象の世代不一致・失効、プロファイル版不一致、期限・画像条件不一致は参照を削除してUI_ELEMENT_STALEとします。権限不足はACCESS_DENIED、ドライバ利用不可はUI_DRIVER_UNAVAILABLEとし、その参照も削除します。
- 画像ドライバの登録はsnapshotIdとUiVisualStampを必須とし、プロファイルIDと版は両方指定するか両方nullとします。プロファイル指定時は現在のドライバInfoの版と一致することを確認します。stampはクライアント・仮想画面矩形と[GetDpiForWindow](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getdpiforwindow)のDPI、取得開始の単調時計を保持します。DPIは対象のDPI認識方式に従う値で、常に物理モニターのDPIになるとは扱いません。取得不能はUI_TARGET_UNVERIFIEDです。
- 画像参照のVisualSnapshotLifetimeSecondsはstamp取得開始から計測し、登録で期限を延長しません。解決時に矩形・DPIを再取得して照合します。UIA参照には画像の期限・DPI照合を適用せず、UIA要素自体の生存・所属・パターン検証は後続ドライバで行います。画面内容だけの変化や同じ位置の別候補は共通レジストリだけでは識別せず、画像ドライバが入力直前に局所画像と文脈を再検証します。
- 最大10000件を保持し、登録時に期限切れ画像参照を整理します。満杯時は失効・権限喪失ウィンドウの参照も整理し、なお満杯ならUI_ELEMENT_LIMIT_EXCEEDEDです。期限整理済みのIDは未知IDとして返します。単一ID・スナップショット単位の明示失効と終了時の参照破棄を提供します。ドライバの対象資源はドライバ側が所有します。この項目ではMCPツール・操作登録を追加せず、探索への接続は後続項目で行います。実機の世代再利用・DPI変更・画像再検証は未検証です。
- P08-04ではWindows DesktopのWPFフレームワーク参照を通じてUIAutomationClientを使用し、IUiDriver実装のUiaDriverをSingleton登録します。ui.list_driversのuiaはregistered:trueとなり、設定有効時はavailable:true、reason:readyです。これはUIA基盤の登録状態であり、すべての対象アプリでアクセス成功を保証する値ではありません。
- 基盤のdiscoveryCapabilitiesはroot_access、element_validation、pattern_discovery、supportedOperationsは空配列です。内部のGetRootAsyncでHWNDからUIAルートを取得し、UiaElementHandleへRuntimeIdをコピーして保持します。WithElementAsyncは対象ウィンドウ世代・RuntimeId・RawViewの祖先（最大256段）を操作前後に照合します。対象と同じプロセスでルート配下と確認できない要素は拒否し、別プロセスのUIA子要素はACCESS_DENIEDとします。
- GetPatternsAsyncは実際に取得した対応パターン名を返します。WithPatternAsyncはTryGetCurrentPatternが成功した場合だけMTA上で処理へ渡し、非対応はUIA_PATTERN_UNSUPPORTEDです。UIAオブジェクトやパターンをMCP応答へ公開しません。要素失効はUIA_ELEMENT_STALE、確認できたアクセス拒否はACCESS_DENIED、その他のCOM障害・MTA不成立はUI_DRIVER_UNAVAILABLEへ変換し、例外本文は返しません。
- [UI Automationのスレッド規則](https://learn.microsoft.com/en-us/dotnet/framework/ui-automation/ui-automation-threading-issues)に従い、UIA呼出しはWinForms UIスレッドから分離したMTA ThreadPoolで実行します。取消は開始前・祖先照合中・処理後に確認します。応答しないUIAプロバイダーの同期呼出しを強制中断する保証はなく、停止確認まで既存のワーカーを占有します。OS上の照合と処理を原子的に実行する保証はありません。
- P08-04は実際のUIAアクセス・参照／パターン検証基盤までです。FindAsync、SnapshotAsync、ExecuteAsyncの公開機能は未対応としてUI_OPERATION_UNSUPPORTEDとし、能力一覧へ完成済み操作として掲載しません。条件探索はP08-05、構造スナップショットはP08-06、意味操作はPhase 9で接続します。この項目ではMCPツールや操作登録を追加しません。x86からx64へのアクセス、独自描画UI、他プロセスの子要素、無応答プロバイダーは実機未検証です。
- ドライバはコントロール種類・矩形・取得できる状態と操作能力を識別し、共通の意味判定層はラベル・周辺要素・画面文脈からsemanticRoleを付与します。「テキスト入力欄」と「ファイルパス入力欄」は別属性です。見た目だけから内部変数、編集可否、正確な現在値を断定しません。

- P08-05のcontrol.findはwindowId、driver（uiaのみ、既定uia）、automationId（最大512文字）、name（最大4096文字）、controlType（UIA定義名、例Edit）、maxCandidates（1～1000、既定1000）を受けます。文字列条件は大小文字を区別する完全一致・AND、未指定は条件なし、空文字は空文字との一致です。NUL、未知引数・種類・不正IDはINVALID_ARGUMENTです。profileId／semanticRoleはこの段階のUIA探索では未対応で受付しません。
- 対象ルートを含むControlViewをMTA上で有界探索し、最大10000ノード、深さ64を上限とします。上限・一部要素の取得中消失・対象外プロセスの枝ではtruncated:trueを返します。一致なしは空候補の成功、複数一致はすべて返して自動選択しません。返却dataはUiSnapshotです。
- 候補にはレジストリ発行のelementIdとsnapshotId、UIAの種類・ラベル・クライアント物理ピクセル矩形、取得できたenabled／offscreen／focused、automationIdの根拠を返します。未取得属性、意味役割、信頼度はnullで、現在未実装の意味操作はsupportedOperationsへ掲載しません。UIAパスワード値は読みません。取得前後に矩形・Windows世代と返却する要素のRuntimeId・所属を照合し、全体失敗・取消では今回のsnapshotIdの参照を破棄します。
- DIの循環を避けるため、UIAドライバにはコンポジションルートで要素レジストリ取得用のFuncを注入し、Info登録時には呼び出さず探索時に解決します。レジストリをドライバ内で構築せず、Singletonを共有します。control.findは既存のキュー・所有者・ロック・取消・期限を適用し、入力条件・観測本文をログへ出しません。実機未検証です。

- P08-06のui.snapshotはwindowId、driver（必須、uia）、includeImage（既定false）、maxDepth（0～64、既定32）、maxCandidates（1～1000、既定1000）を受け、既存キュー・所有者・ロック・取消・期限に従います。未知引数・不正ID・範囲外・未対応ドライバ名はINVALID_ARGUMENTです。
- 条件探索と共通のControlView走査でルートを深さ0として構造を取得します。各候補のparentElementIdで親を示し、ルートはnullです。上限等による打切りはtruncated:trueです。候補IDは重複を拒否し、親は同じスナップショット内に存在する別IDに限定します。control.findの候補ではparentElementIdはnullとし、構造を保証しません。
- includeImage:trueでは既存のscreen.capture方式でクライアントPNGを取得し、get_requestの画像コンテンツへ接続します。構造取得前後・画像取得後に矩形を照合し、画像取得後に各要素も再照合します。画像は最大8MiB、全体失敗・取消では今回の参照を破棄します。構造と画像が同時刻になる保証はありません。最小化・非表示等の画像取得不可は既存の画像エラーです。P08-06完了時点でFindAsyncとSnapshotAsyncが利用可能となり、意味操作はPhase 9で追加します。実機未検証です。

- P09-01のcontrol.get_stateはwindowId、elementIdのみを受け、参照に記録されたドライバへ委譲します。不正ID・未知引数はINVALID_ARGUMENTで受付前に拒否し、既存の参照失効・未準備・未対応エラー、キュー・所有者・ロック・取消・期限を適用します。
- UIAは要素を再照合し、enabled／offscreen／focused、SelectionItemのselected、ExpandCollapseのexpanded、ToggleのtoggleStateを取得します。未取得・未対応属性はnullです。expandedはExpanded:true、Collapsed:falseとし、PartiallyExpanded／LeafNodeはnullです。visibleはIsOffscreenの否定（取得時のみ）で、他ウィンドウによる遮蔽を意味しません。sensitiveはIsPasswordの取得値で、Value／Textは読みません。
- dataは{windowId,elementId,driver,state,sensitive,supportedPatterns,supportedOperations}です。対応パターン名は実際のUIA取得結果とし、完成済み操作のcontrol.get_stateだけを対応操作へ追加します。呼出し前後にRuntimeId・所属を照合し、状態を原子的に取得する保証はありません。実機未検証です。

- P09-02のcontrol.get_valueはwindowIdとelementIdのみを受け、所有ドライバへ委譲します。UIAはIsPasswordが明示的にfalseと取得できた場合だけ値を読み、trueまたは未取得ならvalue:null、redacted:trueを返します。機密属性を読取り後にも確認し、伏字の場合は値本文・取得元を返しません。
- Valueパターンを優先し、未対応ならTextのDocumentRangeを最大65537 UTF-16コード単位まで読みます。両方未対応はUIA_PATTERN_UNSUPPORTEDです。返却上限は65536で、サロゲートペア境界を保持して打ち切ります。Valueパターン自体は全値を返すため、取得後に上限を適用します。
- dataは{windowId,elementId,value,redacted,source,truncated,exact,isReadOnly}です。sourceはuia_value／uia_text、isReadOnlyはValueパターン取得時だけ値を返し、exactはクライアントによる打切りがないことを示します。伏字ではsource／exact／isReadOnlyはnullです。入力・観測値はログへ出さず、既存の参照・取消・期限を適用します。プロバイダーの報告内容をアプリ内部値と独立に検証する保証はなく、実機未検証です。

- P09-03のcontrol.invokeはwindowIdとelementIdのみを受け、記録されたドライバへ委譲します。UIAは所属・RuntimeIdとIsEnabled:trueを確認し、Invokeパターンを取得・再照合した後に1回だけInvokeを呼びます。未対応はUIA_PATTERN_UNSUPPORTED、無効・有効状態未確認はUIA_ELEMENT_NOT_ENABLEDです。
- 呼出しが戻った場合はdata:{windowId,elementId,invoked:true}です。Invokeによってダイアログやボタン自体が閉じることがあるため、実行後の要素消滅を呼出し失敗へ置き換えません。呼出し前の失効・権限確認と、処理後の取消確認は適用します。成功はUIAへの呼出し成功であり、対象アプリの後続業務処理完了を保証しません。クリックへの自動切替・再送・取消後の副作用取消は行いません。実機未検証です。

- P09-04のcontrol.set_valueはwindowId、elementId、value（0～65536 UTF-16コード単位、空文字でクリア）を受けます。NUL・不正サロゲート・未知引数・不正IDはINVALID_ARGUMENTです。UIAはIsEnabled:trueとValueパターン・IsReadOnly:falseを確認し、読取専用はUIA_VALUE_READ_ONLY、未対応はUIA_PATTERN_UNSUPPORTEDです。
- 所属を再照合した後にSetValueを1回呼びます。通常の値は読戻しとのOrdinal完全一致を確認し、不一致はUIA_VALUE_VERIFICATION_FAILED、その他の設定拒否はUIA_VALUE_SET_FAILEDです。設定前後に機密属性を確認し、機密または未確認なら値を読まずverification:unverifiedとします。機密値の設定自体は許可し、実値の検証成功とは扱いません。
- 成功dataは{windowId,elementId,valueSet:true,redacted,verification,observedValue}です。機密値のobservedValueはnull、通常は確認できた値を返します。キー入力への切替・再送・副作用の取消は行わず、既存の参照・キュー・取消・期限を適用します。実機未検証です。

- P09-05のcontrol.send_keysはwindowId、elementId、keySequence（1～100項目）を受けます。各項目はkeyboard.press_keyと同じ許可keyと重複のないmodifiers配列です。すべての項目を受付前に検証し、不正項目・未知引数はINVALID_ARGUMENTです。
- UIAは前面化後に有効・フォーカス可能を確認してSetFocusし、最大1秒でHasKeyboardFocus:trueを確認します。各キーは既存のPressAsyncで送る直前に要素参照・RuntimeId・HasKeyboardFocusを再照合します。確認不能はFOCUS_FAILED、無効はUIA_ELEMENT_NOT_ENABLEDです。キー列の途中でTab等により別要素へフォーカスが移った場合、次のキーを送らず停止し、元要素へ自動で戻しません。
- 成功dataは{windowId,elementId,sentKeys,inputSent:true}です。既に送ったキーは戻さず、部分送信・解放・取消の規則はkeyboard.press_keyと同じです。最後のキーの効果で要素が閉じても、入力送出をアプリ処理完了へ読み替えません。画像方式へ切り替えず、キー本文はログへ出しません。実機未検証です。

- P09-06のcontrol.selectはwindowId、elementIdと任意item:{automationId,name}を受けます。item指定時は一方以上の非空条件を必須とし、AND・Ordinal完全一致、最大512／4096文字、NUL不可です。未指定時は対象自身のSelectionItemを選択します。
- item指定時は対象のSelectionパターンを確認し、ControlView配下を最大10000ノード・深さ64で探索します。複数一致はUIA_ELEMENT_AMBIGUOUS、なしはUIA_ELEMENT_NOT_FOUND、上限到達で一意性を確認できない場合はUIA_SEARCH_LIMIT_EXCEEDEDです。部分探索の候補を暗黙に選びません。別プロセスの枝を除外し、選択する項目のSelectionContainerが元の対象と一致することを確認します。ポップアップや仮想化項目の自動展開・実体化は行いません。
- 有効状態とSelectionItemパターンを確認し、既に選択済みなら再操作せず、そうでなければSelectを1回呼びます。未対応・選択拒否はUIA_NOT_SELECTABLE、読戻しでIsSelectedを確認できなければUIA_SELECTION_VERIFICATION_FAILEDです。成功data:{windowId,elementId,selectionVerified:true,itemSelected}とし、itemSelectedはitem指定の有無です。対象項目の選択状態を確認する契約で、他の選択項目の状態は保証しません。既存のキュー・所有者・参照・取消・期限を適用し、実機未検証です。

| ドライバ | 探索方法 | 操作方法と制約 |
| --- | --- | --- |
| `uia` | Windows UI Automationの要素・パターン | 既存の意味操作。必要なパターンがない場合は専用エラー |
| `imgui_visual` | ウィンドウ画像、ローカルOCR、ImGui向けの形状・配置ルール | 座標クリックとキー入力。Test Engineや対象アプリ内の連携を前提とせず、内部ID・変数は取得しない |
| `generic_visual` | ウィンドウ画像、ローカルOCR、一般的な形状・配置ルール | 座標・キー操作。任意の独自描画UIでの認識成功は保証しない |

- DOM探索は既存のdom.*を使用します。将来のアプリ内ImGui Automation連携は別ドライバとして拡張可能ですが、初版の画像ドライバと同等の能力があるとは扱いません。
- `control.find`へ`driver`（既定uia）、`profileId`、`semanticRole`を追加します。uiaの既存検索条件を維持し、画像ドライバでは`name`をOCRラベル、`controlType`を認識種類として扱います。画像ドライバでautomationIdを指定した場合は`INVALID_ARGUMENT`です。プロファイルのルールIDはOSのautomationIdとは区別します。
- `ui.snapshot`と`control.find`の候補は`{elementId, snapshotId, driver, profileId, profileVersion, controlType, semanticRole, label, bounds, state, evidence, confidence, confidenceKind, supportedOperations}`を返します。取得できない属性はnullとします。boundsは既存の座標APIと同じクライアント領域基準の物理ピクセルです。evidenceはOCR文字列、ラベルとの位置関係、一致した画面・ルールID等を含みます。confidenceは0～1、confidenceKindはprovider／ocr／rule_score等とし、ルールスコアを正解確率として扱いません。
- 画像由来の要素IDはsnapshotId、ウィンドウ・プロセス開始時刻、DPI、画面寸法、プロファイル版に結びつけます。最大有効期間超過や対象・レイアウト変更時は失効します。入力直前に局所画像と画面文脈を再取得して対象を照合し、同じ対象だと確認できなければ`UI_ELEMENT_STALE`または`UI_TARGET_UNVERIFIED`として操作せず再探索を求めます。別候補へ自動で付け替えません。
- アプリ別プロファイルはJSONで`schemaVersion`、`profileId`、`profileVersion`、実行ファイル名等の対象条件、画面識別条件、コントロール識別ルールを定義します。ルールはラベル一致、種類、矩形の相対位置、近傍要素の組み合わせとします。例として「Open Project画面内でPathラベルに隣接する入力欄」をfile_path_input候補にします。任意スクリプト実行、固定メモリアドレスへの書込み、対象アプリのソース変更は含めません。
- driverは呼び出し元が明示選択します。uiaから画像操作への暗黙の切替は行いません。プロファイル候補が複数一致する場合も一覧で返し、暗黙に1つを選びません。一意な候補でも操作方法や入力成功の保証を自動で強めません。
- 共通のcontrol.*はelementIdに記録されたドライバへ委譲します。既存のUI Automationパターン規則はuiaドライバに適用します。画像ドライバの要素はUIAのcontrol.text／control.stateシグナルへ流用できません。画像によるシグナル監視は初版の対象外です。

| 共通操作 | 画像ドライバでの契約 |
| --- | --- |
| `control.invoke` | ボタン候補を再検証して中心をクリックする。入力送出結果を返す |
| `control.set_value` | `method: coordinate_keyboard`を明示必須とする。入力欄をクリックし、フォーカス根拠を確認した後、Ctrl+AとUnicode入力で置換する。フォーカス未確認時は送信しない。Enterは追加しない |
| `control.send_keys` | 同じmethod指定・対象とフォーカス検証後にキー列を送る |
| `control.get_value` | 取得可能なら表示テキストをOCRで返し、`source: ocr`、信頼度、`exact: false`を付ける。内部値とは扱わず、機密入力欄は取得しない |
| その他の選択・状態設定・グリッド操作 | ドライバが必要な能力を公開した場合のみ実行。初版は未対応として`UI_OPERATION_UNSUPPORTED`を返す |

- 画像入力の結果は`{method, inputSent, verification: verified / unverified, observedText}`を含めます。入力送出成功と値の完全一致を区別し、OCRだけでexactな読戻しを保証しません。検証失敗時に入力を自動再送しません。文字認識、Unicode入力、IME、フォーカス表示の検出可否は対象アプリごとに確認します。
- OCRエンジン・モデルの製品と版は実装時にx86/.NET 10対応と配布条件を確認して固定します。未準備時は`UI_DRIVER_UNAVAILABLE`を返し、他ドライバへ切り替えません。探索失敗は`UI_ELEMENT_NOT_FOUND`、複数候補を単一対象として扱う要求は`UI_ELEMENT_AMBIGUOUS`、不正プロファイルは`UI_PROFILE_INVALID`です。
- 画像・OCR本文・入力値・観測値はディスクとログへ保存しません。ログはドライバ、ルールID、プロファイル版、要求ID、成否だけを記録します。ui.*もJSON／Aloe共通APIと操作バッファ・ロック・タイムアウトの規則に従います。

- P09-07 control.set_toggle：windowId、elementId、isOn（真偽値）を必須とします。有効なToggleパターンに対して、既にOn／Offが一致すれば0回、異なる場合だけToggleを最大2回呼びます。Indeterminateも循環に含めます。各回の所属・有効状態・取消を再確認し、状態不変・循環の再訪・未定義状態はUIA_TOGGLE_VERIFICATION_FAILED、切替拒否はUIA_TOGGLE_FAILEDです。成功data:{windowId,elementId,isOn,toggleCount,stateVerified:true}。UIAネイティブ呼出しの終了前に次要求へ進まず、画像・座標操作へ自動切替しません。実機未検証。

- P09-08 control.set_range_value：windowId、elementId、有限数valueを必須とします。RangeValueパターン、有効状態、非読取専用、有限で整合したMinimum／Maximum、要求値の範囲内を確認します。既に同値ならSetValueを省略し、異なる場合は1回呼び、読戻しが要求値と厳密一致することを確認します。SmallChange／LargeChangeは操作単位情報として返し、最小量子化幅とは推定しません。NaN・非有限・負数はnullです。丸めによる不一致はUIA_VALUE_VERIFICATION_FAILED、編集不可はUIA_VALUE_READ_ONLY、設定拒否はUIA_VALUE_SET_FAILEDです。成功data:{windowId,elementId,value,changed,minimum,maximum,smallChange,largeChange,valueVerified:true}。参考：[Microsoft RangeValue](https://learn.microsoft.com/en-us/dotnet/api/system.windows.automation.rangevaluepattern?view=windowsdesktop-10.0)、[SmallChangeとNaN](https://learn.microsoft.com/en-us/windows/win32/api/uiautomationcore/nf-uiautomationcore-irangevalueprovider-get_smallchange)。実機未検証。

- P09-09 control.expand_collapse：windowId、elementId、action（expand／collapse）を必須とします。有効状態とExpandCollapseパターンを確認し、LeafNode・未定義状態・開閉拒否はUIA_NOT_EXPANDABLEです。要求状態と同じなら呼出しを省略し、PartiallyExpandedを含む異なる状態ならExpandまたはCollapseを1回呼びます。所属と取消を前後確認し、読戻しが要求状態に一致しなければUIA_EXPANSION_VERIFICATION_FAILEDです。成功data:{windowId,elementId,expanded,changed,stateVerified:true}。実機未検証。

- P09-10 control.scroll：windowId、elementId、direction（up／down／left／right）、step（small／large）、repeat（整数1～20）を必須とします。有効なScrollパターンと対象軸のスクロール能力を各回で確認し、NoAmountで他軸を動かしません。未対応・操作拒否はUIA_NOT_SCROLLABLEです。各回の所属・取消を確認し、要求回数を呼び出した後のhorizontalScrollable、verticalScrollable、horizontalPercent、verticalPercent、horizontalViewSize、verticalViewSizeとscrollCallsを返します。位置は0～100またはNoScroll=-1、ビューサイズは0～100、非有限・範囲外はnullです。端の位置不変も正常として扱い、移動距離やピクセル単位の移動は保証しません。途中失敗・取消による既実行分の巻戻し・再送は行いません。実機未検証。

- P09-11 control.select_items：windowId、elementId（Selectionコンテナ）、items（子elementIdの配列、0～100件、重複不可）、mode（replace／add／remove）を必須とします。同一ウィンドウ・uia参照、同じSelectionContainer、項目とコンテナの有効状態、CanSelectMultipleを確認します。別IDで同じUIA項目を指す場合もINVALID_ARGUMENTです。現在選択は最大1000件まで取得し、全項目を操作前に検証します。IsSelectionRequiredの場合の空集合は操作前にUIA_NOT_SELECTABLEです。replaceは要求集合、addは和集合、removeは差集合を最終状態とし、追加を先に実行してから不要項目を除去します。既に一致する項目は再操作しません。各回の所属・有効状態・取消・能力を確認し、最終集合を厳密に再照合します。不一致はUIA_SELECTION_VERIFICATION_FAILED、操作拒否はUIA_NOT_SELECTABLEです。途中までの変更の巻戻しや再送は保証しません。成功data:{windowId,elementId,mode,selectedCount,selectionCalls,selectionVerified:true}。実機未検証。

- P09-12 control.set_text_selection：windowId、elementId、start、length（0～65536の整数、合計65536以下）を必須とします。有効でTextパターンと選択能力を公開し、IsPasswordが明示falseの要素だけを扱います。文書を最大65537 UTF-16単位取得し、65536超はUIA_TEXT_RANGE_UNAVAILABLEです。文書外・サロゲート対の分断はINVALID_ARGUMENTです。UIA CharacterをUTF-16と同一視せず、二分探索した接頭辞の実長で両端点を確定します。指定境界が表現不能、本文変化、秘密属性変化、範囲操作拒否はUIA_TEXT_RANGE_UNAVAILABLEです。Selectを1回実行し、GetSelectionの単一範囲の両端点を照合します。不一致はUIA_TEXT_SELECTION_VERIFICATION_FAILEDです。成功data:{windowId,elementId,start,length,selectionVerified:true}。本文は結果・ログに含めません。参考：[Microsoft MoveEndpointByUnit](https://learn.microsoft.com/en-us/dotnet/api/system.windows.automation.text.textpatternrange.moveendpointbyunit?view=windowsdesktop-10.0)。実機未検証。

- P09-13 grid.get_cell：windowId、elementId（Grid要素）、row、column（0～int.MaxValueの整数）を必須とします。Gridパターンの行列数と範囲を確認し、GetItemで返された仮想セルがVirtualizedItemを公開する場合だけRealizeを呼びます。実体化により表示・スクロール状態が変わる場合があります。その後に同じウィンドウ・プロセス・GridItem.ContainingGrid・要求行列を含むRow／ColumnとSpanを照合します。未対応はUIA_PATTERN_UNSUPPORTED、範囲外はINVALID_ARGUMENT、所属・セル範囲不正はUIA_GRID_CELL_UNAVAILABLEです。新しいuia elementIdを登録し、成功data:{windowId,gridElementId,elementId,driver,row,column,name,nameTruncated,controlType,hasValue,supportedPatterns,supportedOperations}を返します。nameは4096文字上限、未取得属性はnull、セル値本文は読みません。失敗時の新規参照を破棄し、後続control操作へ再利用できます。実機未検証。

- P09-14 grid.set_cell_value：windowId、elementId（Grid要素）、row、column（0起点）、value（最大65536 UTF-16単位の文字列、NUL・不正サロゲート不可、空文字は消去）を必須とします。Gridの有効状態とgrid.get_cellと同じ行列・実体化・所属・結合範囲照合を行います。セルのValueパターンと編集可否を検証し、非対応・読取専用はUIA_CELL_NOT_EDITABLEです。SetValueを1回呼び、同一セルの所属を再照合します。control.set_valueと共通の処理で非機密値をOrdinal完全一致で読み返します。不一致はUIA_VALUE_VERIFICATION_FAILED、設定拒否はUIA_VALUE_SET_FAILEDです。機密または機密属性未取得の場合は値を読み返さずredacted:true、verification:unverified、observedValue:nullです。成功data:{windowId,elementId,row,column,valueSet:true,redacted,verification,observedValue}。汎用キー・座標による編集への自動切替は行いません。実機未検証。

### 8.4. 起動対象と管理アプリケーション

- P10-01 起動許可レジストリ：LaunchTargetRegistryをSingleton登録します。AllowedLaunchTargetsのIDをOrdinalで解決し、正規化した絶対パスとコピーした固定引数だけを保持します。初回利用でファイル同一性とSHA-256を固定し、以後の利用で照合します。未登録・差替え・再解析ポイント・最終パス不一致・UNC／デバイスパス・0バイト／512MiB超はLAUNCH_TARGET_NOT_ALLOWED、欠落はAPP_NOT_FOUND、権限不足はACCESS_DENIEDです。検証中から起動呼出し終了まで読取りリースでファイルの書込み・削除共有を拒否し、取消・失敗で解放します。対象の存在は利用時確認とし、未導入の登録対象で他のMCP機能の起動を妨げません。基盤の設計理由、初回利用前の差替え、親ディレクトリ競合、正当な更新後の再起動、App Execution Alias未対応の条件は[ADR 0001](docs/adr/0001-launch-target-identity.md)を正本とします。起動APIはこの項目では公開しません。実機未検証。

- P10-02 app.launch：launchTargetId（必須文字列1～64文字）だけを受け、LaunchTargetRegistryのリースと固定引数を使用します。ProcessStartInfoのUseShellExecute:false、ArgumentList、設定実行パスの親をWorkingDirectoryとして用い、任意引数・シェル・昇格指定を受けません。起動前に最大1000件の管理枠を予約し、終了済みの古い管理参照を整理します。上限はAPP_INSTANCE_LIMIT_EXCEEDEDです。起動後250msの取消可能な観測を行い、ウィンドウを待たずに直接起動プロセスのハンドル、PID、FILETIME開始時刻、イメージの絶対パス、Windows SIDとセッションを照合します。短命・単一起動転送・別イメージ等で特定できなければAPP_INSTANCE_UNRESOLVEDとし、別の既存プロセスへ推測で再対応しません。起動拒否はAPP_LAUNCH_FAILED、権限不足はACCESS_DENIEDです。成功data:{applicationId,launchTargetId,processId,processStartedAt（UTCのISO 8601）}。applicationIdはa_と32桁UUIDで本体寿命内に管理し、保持したプロセスハンドルと開始時刻でPID再利用を拒否します。取消・検証失敗後も既に起動したアプリを強制終了せず、その起動や引数を自動再送しません。本体終了時はハンドルを解放し、アプリを強制終了しません。起動対象の信頼境界は[ADR 0001](docs/adr/0001-launch-target-identity.md)に従います。実機未検証。

- P10-03 window.open：launchTargetIdと任意initialState（normal／maximized、既定normal）を受けます。共通app.launchで新しい管理アプリを起動し、その実行パスのプロセス名でウィンドウを列挙して同じPIDの表示ウィンドウを100ms周期で待ちます。列挙打切り・複数一致・アプリ終了はWINDOW_LAUNCH_FAILEDです。同じWindows SID／セッションに加えてウィンドウ参照のPIDとFILETIME開始時刻を管理アプリと照合し、既存プロセスや別世代へ再対応しません。一意な対象をRestore／Maximize共通処理で設定し、再照合後にdata:{applicationId,windowId,processId,window（現在のWindowInfo）}を返します。ウィンドウ待機と状態変更は同じ操作・要求期限と取消に従います。生成待ち失敗・取消後のアプリを強制終了せず、再起動しません。起動済みアプリへの追加ウィンドウ生成・子プロセスや別プロセスのウィンドウへの追従は含めません。実機未検証。

- P10-04 app.exit：applicationId（a_と32桁UUID）を必須とします。管理参照の同じPID・FILETIME開始時刻・Windows SID・セッション・イメージパスを確認し、キャッシュを更新した主ウィンドウをWindowReferenceRegistryへ登録します。そのウィンドウのPID・開始時刻を管理アプリと再照合して、window.close共通処理でWM_CLOSEを1回要求します。ウィンドウ消滅だけでは成功とせず、保持するプロセスハンドルの終了を50ms周期で確認してdata:{applicationId,exited:true}を返します。終了済みまたは未知の参照はAPP_NOT_FOUND、主ウィンドウなし・別世代・通常終了経路なしはAPP_EXIT_UNSUPPORTED、権限不足はACCESS_DENIEDです。終了待ちは共通操作／要求期限のTIMEOUT／REQUEST_TIMEOUTと協調取消に従います。保存確認・終了拒否があれば待機期限で止め、強制終了・exit文字列入力・子プロセス終了・自動再送は行いません。実機未検証。

- P10-05 シェル参照レジストリ：ShellSessionRegistryをSingleton登録し、sessionIdをsh_と32桁UUID（35文字）で発行します。通常MCPの32桁セッションIDを流用しません。Terminal／PowerShell種別、任意WindowsPowerShell／PowerShell7種別、管理アプリ参照、シェルプロセス参照、ウィンドウ参照、不透明なInputIdentityをコピーして固定します。登録と解決の前後でPID・開始時刻・SID・セッション・ウィンドウ世代を共通レジストリで照合し、IManagedShellBindingのアダプター固有OS情報による入力先証明を呼びます。証明前後でBindingの参照・種類・入力同一性の変更も拒否します。未知IDはSESSION_NOT_FOUND、要求種別違いはSESSION_TYPE_MISMATCH、失効・再対応・入力先不明はSESSION_TARGET_UNRESOLVEDです。取消と権限不足は既存の規則です。最大1000件で、登録成功後だけBindingの所有権を移し、容量到達時に終了済み管理参照を整理します。上限はSESSION_LIMIT_EXCEEDEDです。削除・本体終了は入力先ハンドル等を解放するだけでシェルを強制終了しません。起動・入力・終了APIは後続項目で接続し、この基盤だけでは任意の既存シェルへ登録・接続するMCP操作を公開しません。実機未検証。

- P10-06 terminal.launch：引数なしでTerminalLaunchTargetIdの登録済み通常.exe（WindowsTerminal／wt／wtd、固定引数は空）とPowerShellLaunchTargetIdの登録済みpowershell.exe／pwsh.exe・固定引数を利用します。MCPから任意プロファイル・引数は受けません。内部アダプターが-w new new-tab --title Kugutsu_UUID --suppressApplicationTitleと登録済みシェルの実行パス・固定引数を構造化配列で渡します。シェルの実行パス・引数にセミコロンがあればTERMINAL_LAUNCH_FAILEDです。未設定・登録なし・本体欠落はTERMINAL_NOT_INSTALLED、シェル未設定・欠落・未知種別はPOWERSHELL_NOT_FOUNDです。短命な転送や起動失敗はTERMINAL_LAUNCH_FAILED、許可・権限・資源上限は共通エラーです。直接起動プロセスの一意な同世代ウィンドウを待ち、最大16384件のプロセススナップショット・親子64段・開始時刻・SID・イメージパスで一意なシェル子孫を確認します。UIAは最大10000ノード・深さ64で識別タブの選択と開始タイトル一致のTermControl入力先を探索し、RuntimeIdを固定します。曖昧・複数表示ペイン・対応不明はSESSION_TARGET_UNRESOLVEDです。待機は共通期限・取消に従います。成功data:{sessionId,applicationId,windowId,processId,shellProcessId,shellProcessStartedAt（UTC ISO 8601）,powerShellKind（windows_powershell／powershell7）,inputVerified:true}。inputVerifiedは対応関係の確認で、送信前フォーカスは入力APIで確認します。既存ターミナルへの再対応・起動後失敗の強制終了・自動再起動はしません。設計理由・既定プロファイル不使用・通常.exe／uiaの前提・転送と親子関係の限界は[ADR 0002](docs/adr/0002-managed-terminal-binding.md)を正本とします。実機未検証。

- P10-07 terminal.send_keys：sessionId（sh_と32桁UUID）と任意text（1～65536 UTF-16単位、NUL・不正サロゲート不可）、任意keySequence（1～100件、共通許可キー名・重複なし修飾キー）を受け、一方以上を必須とします。全入力を受付前に検証し、Terminal種別の管理セッションだけを解決します。ウィンドウ前面化と入力ペインのSetFocusを1回行い、最大1秒のフォーカス確認後、textを先に、キー列を続けて送ります。Unicodeの各最大256単位区間と各キー送出直前に、同じセッション・アダプター・管理プロセス世代・識別タブ・入力ペイン・実フォーカスを再照合します。フォーカス不明はFOCUS_FAILED、対応不明はSESSION_TARGET_UNRESOLVEDです。途中で別ペインへ再対応・再フォーカスせず、共通SendInputの保持キー確認・部分送出時解放・取消・期限を適用します。成功data:{sessionId,windowId,inputSent:true,sentTextUnits,sentKeys,commandCompletion:not_observed}。入力本文・キー列は結果やログへ返さず、Enter／改行を暗黙追加せず、コマンドの実行・成功・終了・出力を保証しません。途中失敗・取消の既送出分を巻き戻したり、自動再送したりしません。実機未検証。

- P10-08 terminal.exit：sessionIdを必須とし、Terminal種別の同じ管理参照と入力先を確認します。識別タブ配下を最大1000ノード・深さ16で探索し、ButtonかつAutomationId:CloseButtonの一意な有効Invoke要素だけを使います。この内部識別条件の実機適合は未確認です。未取得・曖昧・未対応・探索上限はSESSION_EXIT_UNSUPPORTEDです。閉じる直前にもタブのRuntimeId・識別タイトル・RawView祖先関係を確認し、共通UIA Invokeを1回呼びます。Invokeによる対象消滅は許容します。管理シェルの保持プロセスハンドルが終了したことを50ms周期で確認し、sessionIdを破棄してdata:{sessionId,exited:true,shellProcessId}を返します。ウィンドウ全体の終了、他タブの閉鎖、exit文字列の未確認入力、強制終了、再送はしません。終了拒否・確認ダイアログ・待機は共通期限と取消に従います。照合のため当該タブが選択されている必要があります。非表示も含むUIAツリー上のTermControl入力ペイン数は1を必須とし、複数ペインはSESSION_TARGET_UNRESOLVEDです。未公開のUI構造の推測や別経路への切替はありません。実機未検証。

- P10-09 powershell.launch：引数なしでPowerShellLaunchTargetIdの登録済み実行ファイル・固定引数と、新設定ConsoleHostLaunchTargetId（既定consolehost、null可）の通常conhost.exe・空の固定引数を使います。ファイル説明のPowerShell表示と実行名を確認し、pwsh.exeはPE major 7だけをPowerShell7として受けます。種別は内部対応判定で、PSVersionTableの実行結果ではありません。Windows 11では-ForceNoHandoff、--以降にシェルの実行パス・固定引数を構造化配列で渡し、既定ターミナル設定を変更しません。PowerShell未登録・欠落・非対応種別はPOWERSHELL_NOT_FOUND、Console Host未登録・欠落はCONSOLE_HOST_NOT_FOUNDです。Console Hostの一意な同世代表示ウィンドウと、登録済みPowerShellの一意な子孫・SID・イメージ・開始時刻を確認します。同梱Kugutsu.ConsoleProbeを非表示WinExeとして起動し、MCP本体をコンソールへ接続せず、GetConsoleWindow／GetConsoleProcessListの5項目JSONを最大16KiB・深さ4・最大1024クライアント・3秒期限で確認します。別クライアントは当該シェルの子孫だけを受けます。失敗・取消・終了競合・未知所属はSESSION_TARGET_UNRESOLVEDです。中止時に終了できるのは当該補助プロセスだけで、ユーザーのシェルを強制終了しません。成功data:{sessionId,applicationId（PowerShell自身）,windowId（Console Host）,processId,shellProcessId,shellProcessStartedAt,powerShellKind,inputVerified:true}。PowerShell種別で登録し、GUIタブ・既存コンソールへ再対応しません。補助のログはkugutsu-console-probe接頭辞で共通保持規則に参加します。内部exeと依存ファイルの配布、プロセス全体の状態へ影響させない設計、既定ターミナルとGUI競合の条件は[ADR 0003](docs/adr/0003-dedicated-powershell-console.md)を正本とします。ソリューション全体ビルド警告0・エラー0、設定JSONとプロジェクトXMLのパース成功、実機未検証。

- P10-10 powershell.send_keys：powershell.send_keysはsessionIdとtextまたはkeySequenceを受けます。PowerShell以外のセッションはSESSION_TYPE_MISMATCHで拒否します。文字列は最大65536 UTF-16単位、キー列は最大100件で、文字列を先に送信します。各入力直前に管理参照・コンソール所属・ウィンドウのフォーカスを確認し、Enterは自動追加しません。結果はinputSent、sentTextUnits、sentKeys、commandCompletion:not_observedであり、コマンド完了や出力の確認を意味しません。入力内容は返却・ログ保存しません。実機未検証です。

- P10-11 powershell.exit：powershell.exitはPowerShell種別のsessionIdだけを受け、プロセス世代・専用Console Host・コンソール所属を照合してWM_CLOSEを1回送信します。管理PowerShellの終了を確認した後にセッションを削除し、exited:trueとshellProcessIdを返します。終了の拒否や確認待ちは共通タイムアウト・取消に従い、文字列exitの注入や強制終了は行いません。コンソール内の当該シェルの子孫も通常終了の影響を受けます。実機未検証です。

- P11-01 browser.get_default：browser.get_defaultは引数なしでWindowsのAssocQueryStringWをIS_PROTOCOL・NOFIXUPS・NOTRUNCATE指定で呼び、HTTPとHTTPSそれぞれのProgID・表示名・実行パスを返します。関連付けなしはbrowser:nullとassociation_not_found、実行先不明はexecutable_unresolved、chrome.exe/msedge.exe/firefox.exe以外はbrowser:otherとunsupported_handlerです。種類識別は実行ファイル名による照会上の分類であり、署名・起動許可・ドライバー適合の保証ではありません。照会エラーや上限32768文字を超える登録はBROWSER_DISCOVERY_FAILEDです。OS関連付けを変更せず、照会時点の値を返します。実機未検証です。

- P11-02 browser.list_installed：browser.list_installedはユーザー単位とマシン単位の32/64bit StartMenuInternet登録、chrome.exe/msedge.exe/firefox.exeのApp Paths、BrowserLaunchTargetsから検出します。ローカル絶対.exeパスだけを確認し、再解析ポイントを含む対象は解決せず、パスで重複をまとめます。最大1024候補、各登録元最大1024件、表示文字列最大4096文字です。版・既定フラグ・対応可否・検出範囲を返し、取得失敗は固定コードのwarningsへ集約します。既定照会に失敗したフラグはnullです。ドライバーは存在確認だけでは適合を保証できないためdriverReady:falseと理由を返します。未登録ポータブル版は設定済みパスだけが対象です。ブラウザやドライバーは起動しません。実機未検証です。

- P11-03 WebDriver共通契約：WebDriver共通契約はADR 0004に従います。ブラウザIDはb_、タブIDはbt_で始まるプロセス内識別子です。登録時に版の適合確認済み能力情報と最大256件のタブを固定し、各解決前に管理プロセス・接続・現在のタブ一覧を検証します。失効タブは削除し、新規タブへ再対応しません。最大1000インスタンスを超える登録はBROWSER_INSTANCE_LIMIT_EXCEEDEDです。共通URLは8192文字以下のHTTP/HTTPSだけを受け、制御文字と認証情報を含むURLを拒否します。共通契約だけではbrowser.launchを提供せず、種類別アダプターは後続項目です。実機未検証です。

- P11-04 browser.launch：browser.launchのchrome実装を追加しました。browserは必須、url省略時はabout:blankで、指定URLは共通HTTP/HTTPS制約に従います。Chrome 115以上、空の固定引数、同一MAJOR.MINOR.BUILDのChromeDriverを要求します。専用プロファイル・remote-debugging-pipeで起動し、W3C能力の版・プロファイル・goog:processIDを管理プロセスと照合してbrowserIdと初期tabIdを返します。プロキシ・リダイレクト・再送を使わず、IPv4ループバック待受PIDを要求前後に照合します。通信は30秒、応答は4MiB・深さ32、待受確認は10秒です。Edge/Firefoxは後続実装までBROWSER_CAPABILITY_UNSUPPORTEDです。失敗・取消時に専用プロセスやプロファイルが残る場合があり、強制終了・削除はしません。ADR 0004を参照してください。実機未検証です。

- P11-05 Edge起動アダプター：browser.launchはbrowser:edgeに対応しました。Chromium版Edge 79以上のmsedge.exeと先頭3部の版が一致するmsedgedriver.exeを許可設定から使い、専用プロファイルをms:edgeOptionsで指定します。能力のbrowserName:MicrosoftEdge、版、msedge.msedgedriverVersion、userDataDirを照合します。ブラウザPIDは管理ドライバーの子孫のうち設定実行パス・世代・主ウィンドウが一致する一意な対象だけを採用します。複数候補はBROWSER_LAUNCH_FAILED、候補待ちは共通タイムアウトです。一般ユーザーの別インスタンスへ転送された起動は採用しません。Chromeとインスタンス・種類を混同せず、通信・プロファイル保管の条件はADR 0004に従います。Firefoxは後続実装です。実機未検証です。

- P11-06 Firefox起動アダプター：browser.launchはbrowser:firefoxに対応しました。初期対応はFirefox 115以上とGeckoDriver 0.36.0の組合せです。許可済みgeckodriver.exeの固定--version照会を3秒・標準出力と標準エラー各8KiBで確認し、専用プロファイルを-profileとmoz:firefoxOptionsへ渡します。moz:profile・moz:processID・moz:geckodriverVersion・ブラウザ版、OSのプロセス世代・実行パス・管理ドライバーとの親子関係を照合して登録します。ドライバーの標準出力/標準エラーをログへ転送せず、版照会の内容も保存しません。親プロセス消失や既存インスタンスへの転送を推測で補わず拒否します。別GeckoDriver版や英字付きFirefox開発版は未対応です。詳細はADR 0004を参照してください。実機未検証です。

- P11-07 browser.navigate：browser.navigateはbrowser、browserId、tabId、urlを必須とします。種類と管理タブを検証して選択後のハンドルを照合し、トップ文書へ戻してW3C Navigate Toを呼びます。pageLoadStrategy:eagerの完了応答を待ち、現在URL・urlChanged・navigationCompletedを返します。URLが同じ場合も完了応答を使い、ページ固有の通信・保存処理の完了は保証しません。遷移エラー時は取得可能な現在URLを1秒以内で照会し、currentUrlObservedとnavigationCompleted:falseをエラー結果へ添付します。TIMEOUTはTimedOut状態に統一し、要求の取消・期限を優先します。操作期限で既に取消済みの場合は追加照会を行いません。実機未検証です。

- P11-08 browser.back：browser.backはbrowser・browserId・tabIdを必須とし、管理タブを選択してWebDriver BiDiのbrowsingContext.traverseHistoryへdelta:-1を1回送ります。履歴範囲外のno such history entryだけをmoved:falseへ変換します。成功応答と送信時刻以降の対象historyUpdated/fragmentNavigated/domContentLoaded通知、および文書readyState:interactive/completeを待ち、現在URLを返します。Chrome/Edge 126以上・Firefox 141以上でBiDi能力を要求しますが、版だけでは能力を保証しません。利用不能ならBROWSER_CAPABILITY_UNSUPPORTED、完了通知が得られなければTIMEOUTです。IPv4ループバックと当該セッションパスだけを許可し、待受の所有PIDを照合します。メッセージ4MiB・深さ32・各待機1024件・30秒を上限とし、失敗後に別方式で再送しません。同じタブのユーザー・ページによる同時履歴操作は原子的に排他できません。実機未検証です。

- P11-09 browser.forward：browser.forwardはbrowser・browserId・tabIdを必須とし、browser.backと共通の管理タブ・BiDi処理でdelta:1を1回送ります。no such history entryはmoved:false、成功時は対象タブの完了通知と文書状態を待ち、現在URLを返します。BiDi能力、版の対応範囲、取消・タイムアウト、エラー時の現在URL観測、ログ非保存はbrowser.backと同じです。失敗後に別方式で再送しません。実機未検証です。

- P11-10 browser.exit：browser.exitはbrowser・browserIdを必須とします。管理対象を照合してW3C Delete Sessionを1回送り、保持したブラウザプロセスの終了を待ちます。応答前の接続終了もブラウザの終了を確認できる場合だけ後処理を続けます。ブラウザ終了後に専用WebDriverが残る場合は保持ハンドルと世代を確認してサービスだけを終了します。ブラウザや他のユーザープロセスは強制終了しません。成功時はexited:true、driverExited:true、profileRetained:trueを返し、管理参照を削除します。取消・タイムアウトは共通規則に従い、後処理やブラウザの子プロセスが残る場合があります。専用プロファイルは自動削除・再利用しません。実機未検証です。

- P12-01 dom.find：dom.findはbrowser・browserId・tabId・selector、任意のframePathとmaxResultsを受けます。framePathの各CSS条件は一意なiframe/frameを必要とし、各文書ルートとネイティブ要素を固定してd_形式のdomElementIdを発行します。応答はタグ名・入力型・可視・有効状態だけで、本文・入力値を取得しません。maxResultsは既定100、1～1000、各ネイティブ検索上限10000件、保持参照上限10000件です。結果上限を超える候補はtruncated:trueとmatchedCountで示し、参照登録は結果群をまとめて行います。セレクター最大4096文字、NUL禁止、フレーム最大16階層で、Shadow DOMは横断しません。詳細はADR 0005を参照してください。実機未検証です。

- P12-02 DOM参照失効：DOM操作の共通解決は各文書ルートと保存済みフレームIDを照合して対象へ戻り、保存した要素IDを検証します。削除・置換・文書変更はDOM_ELEMENT_STALEとし、CSSで再検索しません。失効IDの保持上限は10000件です。Kugutsuの通常遷移送信直前、履歴移動成功後、browser.exit成功時に対象参照を一括失効させます。通常遷移は後で失敗しても参照が失効します。履歴なしは失効させません。外部操作による未観測のBFCache往復や検証と操作の間の競合は完全追跡できず、後続のネイティブstale検証を使います。ADR 0005を参照してください。実機未検証です。

- P12-03 dom.fill：dom.fillはbrowser・browserId・tabId・domElementId・textを必須とします。可視・有効・編集可能・readOnlyを確認し、WebDriverのElement ClearとElement Send Keysで既存内容を置き換えます。空文字はクリアだけです。最大65536 UTF-16単位、不正サロゲート・NUL・WebDriver特殊キーU+E000～U+E05D・改行以外の制御文字を拒否し、inputへの改行も拒否します。各書込み前後に参照を再照合し、文字列を設定するJavaScriptは使いません。事後は固定スクリプトで一致・長さだけを確認し、通常値はexact_match、パスワードはlength_onlyとvalueRedacted:trueで返します。ページが文字列を制限・整形した場合はDOM_VALUE_VERIFICATION_FAILEDです。入力値は応答・ログへ返さず、ページの保存完了は保証しません。実機未検証です。

- P12-04 dom.click：dom.clickはbrowser・browserId・tabId・domElementIdを必須とします。固定参照と可視・有効状態を確認し、再照合後にWebDriverのElement Clickを1回送ります。スクロール・中心点の遮蔽判定をWebDriverで行い、遮蔽された対象はDOM_ELEMENT_NOT_INTERACTABLEとします。JavaScriptのclick()への置換や失敗後の再クリックをしません。成功応答はclickSent:trueで、ページ固有の処理完了を保証しません。クリックによる要素消失・遷移があり得るため、成功後に対象要素が残ることを要求しません。実機未検証です。

- P12-05 dom.send_keys：dom.send_keysはbrowser・browserId・tabId・domElementId・keySequenceを必須とし、最大100項目と共通key/modifiers形式を使います。全列の対応可否を事前確認し、空文字のElement Send Keysで対象へフォーカスして各項目直前に参照・可視・有効状態・activeElementを照合します。修飾キー押下、主キー押下/解放、修飾キー逆順解放をW3C Actionsへ送信します。F1～F12まで対応し、F13～F24は操作前にBROWSER_CAPABILITY_UNSUPPORTEDとします。WinはMetaに対応し、OS全体のWindowsキー動作は保証しません。途中失敗や取消時も2秒以内でRelease Actionsを要求し、解放不明はINPUT_RELEASE_FAILEDです。成功時はinputSent:trueとsentKeysだけを返し、キー列は保存しません。実機未検証です。

- P12-06 dom.get_value：dom.get_valueはbrowser・browserId・tabId・domElementIdを必須とし、input/textareaのvalue、contenteditableのtextContentを返します。パスワード欄は型を事前確認し、固定読取りスクリプト内でも再確認してvalue:null、utf16Length:null、redacted:trueを返します。伏せ方を解除する引数はありません。通常値は65536 UTF-16単位までで、超過時はDOM_VALUE_TOO_LARGEです。返却前に文書・要素参照を再照合します。取得値は既存の結果保持期間中メモリーへ保持しますが、ログ・ディスクへ保存しません。実機未検証です。

- P13-01 シグナル登録表：シグナル登録表は所有セッションと内部LockIdへ結び付くsig_形式のIDを保持します。ロック更新では同じLockId、再取得では別LockIdで、秘密のロックトークンを表へ保存しません。所有者・取得世代を照合し、別所有者や別取得世代をSIGNAL_ACCESS_DENIEDで拒否します。条件は最大64KiB、観測は16KiB、MaxSignalsは成立済み保持も含む有効登録数に適用します。状態・期限・成立時刻・終了理由を保持し、終了記録は結果保持期間と最大1000件を上限とします。登録表だけでは監視ツールを公開せず、監視開始とロック終了時の整理は後続項目です。詳細はADR 0006を参照してください。実機未検証です。

- P13-02 register_signal：register_signalをMCP管理ツールとJSON共通操作へ接続しました。MCPはlockToken・condition・任意のretentionSecondsを受け、共通操作は要求の所有者と取得ロックを引き継ぎます。監視プロバイダーの種類別検証、監視開始、初期評価の通知を確認してsignalId・state・expiresAt等を返します。開始待ちはOperationTimeoutSecondsを適用し、監視寿命はHTTP接続から分離します。初期成立はsignaledとして返し、条件本文・観測本文・秘密トークンをログへ保存しません。P13-02時点ではプロバイダー未接続のため各監視種類をSIGNAL_CONDITION_UNSUPPORTEDで拒否し、後続P13-06～P13-12で接続します。実機未検証です。

- P13-03 wait_for_signal：wait_for_signalを単体MCPと共通操作へ接続しました。単体はschemaVersion:1・requestId・lockToken・signalId・timeoutMs、共通操作はsignalId・timeoutMsを使い所有者と取得ロックを引き継ぎます。0～300000msの通常待機期限は単調時刻で比較し、期限までの成立はstatus:signaledで返します。成立しなければ操作成功のstatus:timeoutで、signaledAt/observedはnullです。要求・操作の期限と取消は共通キューのTIMEOUT/REQUEST_TIMEOUT/CANCELLEDで中断します。未知ID・期限切れ・解除・監視障害を区別し、待機は登録を消費・再武装しません。単体も非同期受付でget_requestから結果を取得します。監視種類の接続は後続項目です。実機未検証です。

- P13-04 unregister_signal：unregister_signalはMCPではlockToken・signalId、共通操作ではsignalIdを受けて所有者と取得ロックを照合します。登録をunregisteredへ変更して未成立の待機へSIGNAL_UNREGISTEREDを通知し、監視の取消と解放の完了を待ちます。同じ解除済みIDは保持期間内でalreadyUnregistered:trueを返します。未知ID・他所有者・期限切れ・監視障害を区別し、期限切れ等で既に終了した監視も後処理して元の終了理由を返します。解除以前に成立が記録された待機は最初の成立通知を優先します。条件・観測本文をログへ保存しません。実機未検証です。

- P13-05 シグナル寿命管理：SignalLifetimeServiceが200ms周期で登録期限とMCPロック世代を確認します。成立後の監視だけを停止して結果を保持し、期限切れはSIGNAL_EXPIRED、ロック終了・停止・強制解除はSIGNAL_UNREGISTEREDとして登録を終了します。次回の整理開始まで最大200msで、OSスケジューリングや監視解放の待ち時間は加算されます。登録表とキューの排他を同時に保持しません。開始処理途中の解除では開始用取消も通知し、資源生成の完了を待ってDisposeAsyncします。終了登録は結果保持期間と1000件上限で整理し、登録表から削除された監視も解放します。本体終了時に監視解放を待ちます。全体ビルド警告0・エラー0、実機・競合未検証です。

- P13-06 window.state監視：condition:{kind:"window.state",windowId,property:"exists"|"active"|"minimized"|"maximized",equals:boolean}を受けます。登録時に有効なwindowIdを必要とし、HWND・プロセス世代を固定します。初期評価を終えて登録結果を返し、初期成立も対応します。イベントを使わない状態監視としてSignalPollIntervalMillisecondsでポーリングし、観測間隔より短い変化は保証しません。観測値は{windowId,property,value}です。exists:falseはウィンドウ消失・別世代への置換を成立とし、それ以外の消失や参照マーカー不整合はSIGNAL_TARGET_INVALIDATEDとします。成立・解除・期限・ロック終了で取消と監視終了を待ちます。window.stateだけを接続し、他種類は後続です。実機未検証です。

- P13-07 window.opened監視：condition:{kind:"window.opened",processName?,launchTargetId?,titleContains?}を受け、processNameまたはlaunchTargetIdを必須とします。両方指定時は両方に一致する必要があります。開始前の一覧を基準として既存参照IDを除外し、SetWinEventHookのEVENT_OBJECT_CREATE/SHOWを受信して新しいトップレベルウィンドウを評価します。登録の開始点はフック設置時刻で、初期結果はpendingです。通知は専用スレッドのメッセージループで受け、固定した参照を256件上限のキューへ渡して読み取ります。通知の欠落・短命ウィンドウ・タイトルが通知後に変化するケースの検出は保証しません。基準一覧が上限を超えた場合やフック設置失敗はSIGNAL_CONDITION_UNSUPPORTED、キュー超過や監視障害はSIGNAL_MONITOR_FAILEDです。名前・タイトルは大文字小文字を区別せず比較し、launchTargetIdは検証済み設定の実行パスと実プロセスイメージを照合します。権限外・終了済み候補はスキップします。一致時のobservedは{windowId}です。成立・解除・期限・ロック終了で通知取消・フック解除・スレッドと読み取りの終了を待ちます。ポーリングへ代替しません。全体ビルド警告0・エラー0、実機未検証です。

- P13-08 control.state監視：condition:{kind:"control.state",windowId,elementId,property:"enabled"|"visible"|"focused"|"toggle"|"selected",equals}を受けます。toggleだけequalsは"on"|"off"|"indeterminate"、他は真偽値です。UIA以外の参照や必要な状態・パターンが提供されない場合はSIGNAL_CONDITION_UNSUPPORTEDです。visibleは!IsOffscreenで遮蔽判定ではありません。初期評価後に登録を返し、SignalPollIntervalMillisecondsで非同期観測します。UIA読み取りは既存ドライバのMTA処理とRuntimeId・プロセス・ルート所属の前後検証を利用し、参照の再探索・別要素への代替を行いません。観測値は{windowId,elementId,property,value}で、要素やウィンドウ失効はSIGNAL_TARGET_INVALIDATED、その他の観測障害はSIGNAL_MONITOR_FAILEDです。成立後と寿命終了では監視取消・終了を待ちます。外部UIAプロバイダーの応答待ちは協調取消であり、応答しないプロバイダーの強制中断は行いません。実機未検証です。

- P13-09 control.text監視：condition:{kind:"control.text",windowId,elementId,comparison:"equals"|"contains",text}を受けます。比較文字列は0～4096 UTF-16文字でNUL・不正サロゲートを拒否し、比較はOrdinalです。UIAのみ対応し、ValuePatternを優先、未提供ならTextPattern.DocumentRangeを読み取ります。IsPasswordがfalseと確定できない場合は登録をSIGNAL_CONDITION_UNSUPPORTEDで拒否し、読み取り前後でも同じ条件を確認します。全文65536文字を超えた値や不正文字列は切り詰めずSIGNAL_MONITOR_FAILEDとします。初期評価を終えて登録を返し、設定間隔でMTAと固定RuntimeIdの検証を利用して観測します。観測結果は{windowId,elementId,comparison,matched,textLength,source}で本文を含めません。要素失効と監視障害を区別し、本文をログ・ディスクへ保存しません。実機未検証です。

- P13-10 dom.state監視：condition:{kind:"dom.state",browser,browserId,tabId,selector,framePath?,property:"exists"|"visible"|"enabled",equals:boolean}を受けます。CSSは4096文字まで、framePathは16段までです。登録時にタブ・各文書ルート・一意なフレームのネイティブIDを保存し、設定間隔で同じ文書内のCSSを都度検索します。existsは1件以上ならtrueで、0件も初期成立できます。visible/enabledは一意な要素を必要とし、0件・複数件は既存DOMエラーです。初期評価後に登録結果を返し、観測は{browser,browserId,tabId,property,value,matchedCount}です。文書・フレーム入替えや対象タブ消失はSIGNAL_TARGET_INVALIDATEDで、別文書へ追従しません。BrowserOperationGateをbrowser.*・dom.*操作全体と監視読み取り全体に適用し、タブ・フレームの選択競合を防ぎます。register/wait/unregisterはこの排他を保持しないため、待機中に背景監視できます。監視もWebDriverのコンテキスト選択を行い、ブラウザ側の選択状態が変わる可能性があります。BFCacheの一時的往復検出の制約は既存DOM参照と同じです。詳細はADR 0006に従います。実機未検証です。

- P13-11 dom.text監視：condition:{kind:"dom.text",browser,browserId,tabId,selector,framePath?,comparison:"equals"|"contains",text}を受けます。比較文字列は4096文字まででNUL・不正サロゲートを拒否し、比較はOrdinalです。登録時の文書ルート・ネイティブフレームを固定し、その文書でCSSを都度検索して一意な要素のWebDriver rendered textを読みます。input.valueやtextContentは取得しません。直接対象がpassword入力なら登録拒否し、読取り前後でも確認します。全文65536文字を超えた応答や不正文字列は切り詰めず監視障害とします。観測結果は{browser,browserId,tabId,comparison,matched,textLength,source}で本文を含めません。初期評価、設定間隔での監視、文書・フレーム失効、ブラウザ操作全体との排他はdom.stateと同じ規則です。本文・比較文字列をログやディスクへ保存しません。実機未検証です。

- P13-12 browser.url監視：condition:{kind:"browser.url",browser,browserId,tabId,comparison:"equals"|"contains",url}を受けます。比較文字列は0～8192文字で制御文字・不正サロゲートを拒否し、比較はOrdinalです。初期URL評価後に登録結果を返し、同じ登録タブの現在URLを設定間隔で読みます。DOM監視と異なり文書の入替えを許容し、同じタブの遷移・履歴移動を観測します。URLへの遷移操作は行いません。ブラウザ操作全体との排他を使用し、タブ・フレーム選択は既存ドライバへ委譲します。observedは{browser,browserId,tabId,comparison,matched,urlLength}で、URL本文や比較文字列を観測結果・ログ・ディスクへ保存しません。タブ・ブラウザ消失と観測障害を区別し、成立・寿命終了時に監視を解放します。実機未検証です。

- P13-13 JSON signalKey参照：JSONバッチ内のregister_signalへ任意のsignalKeyを指定し、後段のwait_for_signal/unregister_signalが同じキーを参照できます。1～64文字のASCII英数字・アンダースコアのみ、Ordinalで比較します。全操作を構文検証した後、重複登録・未定義・前方参照・signalIdとsignalKeyの同時指定または両方欠落を、部分実行せずINVALID_ARGUMENTで拒否します。参照表は受付した1要求へ固定し、FIFO実行中の登録成功時に返されたsignalIdを保存します。別要求・別所有者へ共有しません。再送時は既存の要求指紋と重複受付規則に従います。単体MCPとexecute_operationはsignalIdを使用しsignalKeyを拒否します。単体の共通シグナル操作とMCP待機でも所有者・取得ロック世代を実行側へ渡すよう修正しました。キー参照表をログ・ディスクへ保存しません。全体ビルド警告0・エラー0、実機未検証です。

- P14-01 ローカルOCRアダプター：ILocalOcr/TesseractLocalOcrをDIへ接続しました。Tesseract NuGet [5.2.0]、同梱x86 DLL、tessdata_fast 4.1.0のjpn/engモデル（固定コミット65727574dfcd264acbb0c3e07860e4e9e9b22185）を使用し、ネイティブDLLとモデルのSHA-256をコードへ固定します。bin/x86とdat/ocr/tessdata_fast-4.1.0から読み、認識終了まで読み取りリースを保持し、欠落・改変・リパース・TESSDATA_PREFIXの設定等はUI_DRIVER_UNAVAILABLEです。画像は8MiB以下のメモリ内PNG、一辺4096以下、総画素8388608以下で、デコード寸法も確認します。LSTM-only/SparseTextで単語、画像内矩形、0～1へ換算したOCR信頼度を返します。OcrLanguageはeng/jpn/jpn+eng（既定jpn+eng）、OcrMaxWordsは1～10000（既定1000）です。単語上限超過はtruncated:true、異常な文字列・矩形・信頼度は拒否します。実行は1件ずつ、ネイティブ認識中は強制中断せず取消後も呼出し終了を待つ場合があります。画像と本文を外部へ送信せず、ログ・ディスクへ保存しません。モデルはビルド出力へ配置します。VC++ 2019互換x86ランタイムとライセンスの配布はMSIフェーズで接続します。OCR実行・精度は実機未検証、画像ドライバの公開は後続です。詳細はADR 0007に従います。

- P14-02 入力欄の形状検出：VisualRasterでPNGをメモリ内の輝度配列へ変換し、VisualShapeDetector.FindInputsが入力欄候補を返します。幅40以上、高さ12～128、縦横比2.5以上、左右境界支持率0.6以上、内部平坦率0.7以上を既定規則とします。境界の輝度差はVisualEdgeContrast（既定24、1～255）で設定します。水平境界4096本・矩形比較65536回・候補最大1000件で上限を適用し、打切りはtruncated:trueです。IoU0.85以上の同種候補を統合し、より高いrule_scoreを保持します。矩形とshape根拠を保持し、画像・本文をディスクやログへ保存しません。形状だけで内部型・編集可否・有効状態・フォーカスを断定せず、画像ドライバの公開は後続です。詳細はADR 0008に従います。全体ビルド警告0・エラー0、実機精度未検証です。

- P14-03 ボタンの形状検出：VisualShapeDetector.FindButtonsが幅20以上、高さ12～96、縦横比1～12の矩形をボタン候補として返します。入力欄と同じ境界・内部平坦率の検証と候補上限、IoUによる同種候補統合を適用し、rectangle_edges_v1/compact_flat_interior_v1を根拠へ付けます。input_candidate/button_candidateの両方へ適合する形状は両方を保持します。shapeのrule_scoreを正解率や内部コントロール型と扱わず、Invokeできる状態や業務完了は断定しません。OCRラベルとの対応や公開ドライバは後続です。全体ビルド警告0・エラー0、実機認識未検証です。

- P14-04 ラベル関連付け：VisualLabelAssociatorがOCR単語を近接行へまとめ、入力欄の左・上にあるラベルとボタン内部のラベルを関連付けます。結合行がコントロール境界を跨いだ場合にも対応できるよう単語単位の代替候補を保持し、入力欄の内部文字列をラベルと扱いません。1候補あたり16対応、結合行1000件、全ラベル2000件、1行4096文字で上限とtruncatedを適用します。ラベルのOCR信頼度、位置関係left/above/insideとrule_score、矩形を別属性で保持します。複数候補・複数ラベルを最高スコアだけで一意化しません。文字や矩形の範囲を検証し、本文を外部・ログ・ディスクへ保存しません。全体ビルド警告0・エラー0、実機関連付け未検証です。

- P14-05 プロファイル読込み：VisualProfileCatalogをDIへ接続しました。UiProfileDirectory直下のschemaVersion:1 JSONからprofileId/profileVersion/driver、target、screens、requiredLabels、rulesを読みます。文字列のリテラル照合規則のみで、任意コード・正規表現・外部URL・DLL指定は受け付けません。未知・重複項目、ID重複、版・型・サイズ・文字列・パス不正はUI_PROFILE_INVALIDです。IDはASCII英数字・アンダースコア・ハイフン・ドットの1～64文字、100ファイル、256KiB/ファイル、合計10MiB、JSON深さ16までです。インストールルート内だけを許可しリパースを拒否します。機密属性省略は不明、allowTextInputの既定はfalse、表示フォーカス規則は入力候補だけです。読込み結果は不変の一覧と内容SHA-256を持ち、メタデータ一覧は最後に成功した読込みを示します。フォーマットはdocs/UI_PROFILES.md、方針はADR 0008を参照してください。全体ビルド警告0・エラー0、文書JSON解析成功、実機未検証です。

- P14-06 画面識別：VisualScreenMatcherがプロファイルのdriver/processName/titleContainsと各画面のtitleContains/requiredLabelsを照合します。プロセス名とタイトルはOrdinalIgnoreCase、OCR行・単語ラベルはOrdinalです。切り詰められたタイトルでの推定を拒否し、requiredLabelsすべての存在を必要とします。一致画面は最大128件で複数を保持し、プロファイル・画面・対象プロセス・タイトル・周辺OCR矩形の根拠を返します。必要ラベルごとの根拠は最大4箇所、上限やOCR/行抽出の不足はtruncatedへ伝達します。明示profileIdが未知・別ドライバならUI_PROFILE_INVALID、対象・画面が確認できなければUI_TARGET_UNVERIFIEDです。自動選択で一致しなければ空の画面候補を返します。全体ビルド警告0・エラー0、実機識別未検証です。

- P14-07 意味判定：VisualMeaningInferenceが一致画面のshape/label/relationをリテラル照合し、semanticRoleを形状の種類とは別属性へ付けます。プロファイルID・版・内容SHA-256、画面ID、規則ID、ラベルと矩形の根拠を保持します。複数の意味規則に一致する場合はそれぞれ候補として返し、最高スコアへ一本化しません。未一致のsemanticRoleはnull、ラベルが複数の場合もLabelを推測で選びません。rule_scoreは形状と位置関係の規則スコアであり、内部型・編集可否・正解確率を示しません。最大1000候補・262144回の規則比較・1候補24件の画面根拠で打切りをtruncatedへ伝達します。本文をログ・ディスク・外部へ保存しません。全体ビルド警告0・エラー0、実機意味判定未検証です。

- P14-08 imgui_visualドライバ：ImGuiVisualDriverとVisualAnalysisPipelineを登録し、Test EngineやImGui内部IDを使わずメモリ内の画像・OCR・形状・ラベル・画面・意味規則から共通候補を作ります。OCRの固定資産・x86ネイティブDLL読込み・版を初回可用性確認で検査し、認識直前にも資産を照合します。初回確認失敗時は修復後に本体再起動が必要です。画像探索は操作可能な前面ウィンドウを要求し、暗黙に前面化しません。画像取得・解析前後にウィンドウ世代、矩形、DPI、タイトル、入力先を確認します。画像から推定したEdit/Buttonと用途を別属性へ保持し、未取得状態はnull、rule_scoreと根拠を返します。明示profileIdはそのプロファイル規則の候補に限定します。フレームは別表のsnapshotIdで参照し、VisualSnapshotLifetimeSeconds、最大16フレーム、PNG＋輝度配列合計128MiBで制限して要求時と1秒周期で整理します。途中失敗時はフレームと登録参照を解除します。操作能力はまだ未公開で、ui.snapshot/control.findの画像引数は後続P14-10/11で接続します。全体ビルド警告0・エラー0、依存・認識の実機動作未検証です。

- P14-09 generic_visualドライバ：GenericVisualDriverをVisualDriverBaseの共通画像処理へ接続しました。一般的な矩形・入力欄・ボタン・OCRラベルとgeneric_visual用の画面/意味プロファイルだけを使い、imgui_visualの参照・規則と混同しません。UIAや他ドライバへ自動切替せず、未確認状態はnull、形状と意味の根拠・rule_scoreを返します。任意の独自描画UIでの検出成功や内部型・値の取得は保証しません。可用性・期限・フレーム容量・前面入力先検証は画像基盤と同じです。ui.snapshot/control.findの画像引数と操作能力は後続です。全体ビルド警告0・エラー0、実機未検証です。

- P14-10 ui.snapshot（画像）：ui.snapshotのdriverはuia/imgui_visual/generic_visualに対応し、画像ドライバで任意profileIdを受けます。UIAでのprofileId指定は受付前にINVALID_ARGUMENTで拒否します。画像ドライバは操作可能な前面ウィンドウのPNGから共通候補を生成し、includeImage:trueの場合だけPNGを応答へ含めます。maxCandidatesを適用して打切りをtruncatedとして返し、階層を断定せずフラットな候補にします。maxDepthはUIAの階層だけに適用します。画像のEdit/Buttonは認識種類、semanticRoleは用途、未取得状態はnull、根拠とrule_scoreを保持します。一辺4096・総画素8388608を超える画像はCAPTURE_TOO_LARGEです。画像・OCR本文を外部・ログ・ディスクへ保存しません。UIAの既存経路を維持し、ドライバ自動切替はしません。全体ビルド警告0・エラー0、実機未検証です。

- P14-11 control.find（画像）：control.findのdriverは既定uiaで、imgui_visual/generic_visualを明示選択できます。画像ドライバはnameをOCRラベル、controlTypeをEdit/Buttonの認識種類として扱い、任意profileId/semanticRoleで絞ります。画像ドライバのautomationId指定、UIAへの画像専用条件、無効なID・認識種類は受付前にINVALID_ARGUMENTです。規則で確定したラベルと未分類の複数ラベル候補を区別して名前を照合し、意味規則の候補を別ラベルで誤って返しません。複数結果は一覧として返し暗黙に選択せず、候補なしはUI_ELEMENT_NOT_FOUNDでフレームを破棄します。maxCandidates・truncated・画像参照の期限・前面入力先・OCR資産検証は画像基盤に従います。ドライバ自動切替はしません。全体ビルド警告0・エラー0、実機探索未検証です。

- P14-12 画像参照の再検証：VisualReferenceValidatorをDIへ接続しました。画像参照のsnapshotId・期限・ウィンドウ世代・矩形・DPI・タイトル・プロファイル版と内容SHA-256を前後で確認し、候補自身と外部ラベル/文脈の局所BGRA画素を完全一致で比較します。画像変化はUI_ELEMENT_STALE、候補中心と内側4隅のヒット先が同じルートウィンドウに属すると確認できない場合はUI_TARGET_UNVERIFIEDです。再探索や別対象への再対応は行いません。フレーム容量の128MiB上限にはPNG・輝度・BGRA配列を含めます。意図した表示変化の後に候補自身だけの比較を除外する内部経路を用意しますが、文脈・期限・プロフィール・入力先の検証は維持し、後続の操作からだけ使用します。これは画像の同一性確認であり内部ウィジェットIDの証明ではありません。全体ビルド警告0・エラー0、実機の失効・遮蔽検証は未実施です。

- P14-13 画像フォーカス確認：画像入力欄のフォーカス確認は明示プロファイルのfocusIndicator（border_color、RGB許容差、内側位置、必要一致率）を使う。期限・世代・幾何・DPI・タイトル・プロファイル内容・外部根拠・前面入力先を各画像で再検証し、対象四辺の色一致率が50ms隔てた2回とも閾値以上の場合だけ表示根拠を返す。対象自身の画素はフォーカス表示変化を許す。この根拠は内部フォーカスIDや編集可否を証明しない。未定義・不足ならUI_TARGET_UNVERIFIED、期限や参照変化ならUI_ELEMENT_STALE等。確認は読取りのみで、暗黙のクリック・文字送信を行わない。

- P14-14 control.invoke（画像）：control.invokeは画像参照でmethod:coordinate_clickを必須とし、プロファイル規則に一致したbutton_candidateの中心へ左クリックを1回送る。UIAではmethod指定を拒否し従来Invokeを維持する。移動前と移動後のクリック直前に参照・局所画素を完全一致で確認し、ホバー表示が変わるボタンもUI_ELEMENT_STALEで拒否する。自動再探索・再送は行わず、クリック経路を開始した画像snapshotと全参照を失効させる。成功はinputSent:true、applicationResultVerified:falseで、アプリ内処理の完了を保証しない。

- P14-15 control.set_value（画像）：画像control.set_valueはmethod:coordinate_keyboard、プロファイルのallowTextInput:trueとfocusIndicatorを必須とする。クリック前は局所画素の完全一致を確認し、クリック後の表示根拠確認、Ctrl+A、Delete、Unicode入力の各段階で期限・文脈・フォーカス表示・利用者のキー押下を再確認する。空文字は削除だけ行い、制御文字を拒否しEnterを暗黙に送らない。途中失敗時に全選択や削除だけ完了している可能性があるため、再送やロールバックを行わない。成否にかかわらず入力経路開始後の画像参照を失効させる。応答は文字列を含まず長さとinputSent:true、applicationResultVerified:falseを返す。UIAへのmethod指定は拒否する。

- P14-16 control.send_keys（画像）：画像control.send_keysはmethod:coordinate_keyboard、allowTextInput:true、focusIndicator付きの入力欄だけを扱う。1回のクリックで入力対象を指定し、各キー組合せの送信直前に期限・ウィンドウ世代・文脈・表示上のフォーカス・利用者押下を再検証する。途中でTab/Enter等が明示指定され対象が変わった場合、後続キーは確認不能として送らない。最後のキーが画面を変えることは許されるが応答はinputSentとkeyCountのみでアプリの結果を保証しない。失敗時の入力再送・ロールバックはせず、経路開始後は全snapshot参照を失効させる。UIAへのmethod指定は拒否する。

- P14-17 control.get_value（画像）：画像control.get_valueはmethod:local_ocrを必須とし、入力欄のプロファイルでsensitive:falseが明示されている場合だけ許可する。機密属性がtrueまたは未指定ならUI_TARGET_UNVERIFIED。枠の内側を2ピクセル除いた画像だけをOCRへ渡し、文字・画像を保存やログ出力しない。読取り前後に参照と画素を照合する。推定value、source:ocr、exact:false、estimated:true、最小OCRconfidence、単語別の文字・信頼度・クライアント座標の矩形を返す。単語はX順に半角空白で連結するため空白の正確な復元・隠れた全文の取得は保証しない。文字未検出を空文字と断定せず、打切り・複数行・不正範囲とともにUI_TARGET_UNVERIFIEDとする。UIAへのmethod指定は拒否する。

- P14-18 Craywork用プロファイル：craywork.open_projectプロファイルをconfig/ui-profilesへ配置し、Craywork.Appプロセス、Crayworkタイトル、Open Project/Path/CancelのOCRラベル、入力枠右側のPathを照合してproject_file_path_inputを返す。右側ラベル対応を共通関連付けへ追加する。sensitive:falseで表示値取得を許可し、focusIndicator未定義・allowTextInput:falseのため文字入力は許可しない。Craywork側の変更は行わない。識別・入力の実機検証は未実施。根拠と設定変更手順はdocs/CRAYWORK_PROFILE.mdへ記載する。

- P15-01 Aloe依存版固定：Aloe依存アセンブリはCompilerLib.dll、Aloe.CommonLib.dll、Aloe.RuntimeLib.dllのAssemblyVersion:1.0.0.0とSHA-256を固定する。正本と値はdocs/adr/0009-fixed-aloe-dependencies.md、配布ファイルはbin/aloe/20261013。任意DLLパスや変更可能なハッシュ設定を受け付けない。利用時に再解析ポイント・サイズ・ハッシュ・名前・版を照合し、読取りロックを利用期間保持する。欠落や不一致はALOE_UNAVAILABLE、取消は要求へ伝達。P15-01だけではコンパイル・実行・aloe形式の受付を有効化しない。

- P15-02 コンパイルアダプター：AloeコンパイルはKugutsu.AloeHost.exeの内部プロセスで行う。親子とも固定依存を検証し、読取りリースを保持する。ソースは1～65536 UTF-16単位、NUL・不正UTF-16を拒否。私有パイプは最大1MiBの長さ付きUTF-8 JSON。Compile(string)のModuleは子メモリに保持し、親には命令・関数数だけ返す。字句・構文エラーはALOE_COMPILE_ERRORとstage:compile、任意の例外・ソース・推測行番号を返さない。コンパイル期限はOperationTimeoutSeconds、取消・失敗時は補助プロセス終了を確認してから戻る。実行とaloe受付は後続項目、実機コンパイル未検証。

- P15-03 実行アダプター：Aloe実行アダプターはコンパイル済み補助セッションを一度だけrunへ遷移させる。Moduleを別ファイルへ書かず同じ子メモリで実行する。標準入力・出力・sleep等の一般用途ホストI/O、Aloe診断ログ、外部設定読込みを提供しない。VM設定はホスト側で明示し、最大VMスレッドは1。期限はRequestTimeoutSeconds、取消・期限・失敗では子の終了を確認してから戻る。成否は固定ALOE_RUNTIME_ERROR等で、入力を含む例外本文は返さない。実行成功はexecuted:trueでアプリ業務結果を保証しない。共通操作API・詳細実行制限・受付は後続項目、実機実行未検証。

- P15-04 共通操作APIの公開：Aloeホスト関数execute_operation(name:string,args:string):stringを提供する。argsはJSONオブジェクト文字列、戻り値は操作データのJSON文字列。親だけが所有者・ロック文脈を保持し、OperationDispatcherの引数・登録・所有権検証とOperationTimeoutSecondsを適用する。子の私有ホスト呼出しは連番照合し、一般用途readLine/print/sleepや任意I/Oへ接続しない。固定プリリュードのexecute_operationと、シグナル関数の明示フレームだけが私有RPCを使用できる。操作失敗時はそのコードで停止し再送しない。Aloe受付はP15-07まで無効、実機ホスト呼出し未検証。

- P15-05 実行制限：MaxAloeInstructionsは既定1000000で1～10000000、AloeMemoryLimitMiBは既定256で128～1024。命令実行前に計数し、上限時はALOE_LIMIT_EXCEEDEDと命令位置・実行済み数を返す。ホスト呼出しはMaxBatchOperationsを適用し、未送出の上限位置を返す。私有フレームと単一結果は16MiB。一般用途I/Oは提供しない。Windows Jobはソース送信前に割当て、コミットメモリ、プロセス数1、親終了時停止を設定し、割当失敗時はALOE_UNAVAILABLE。メモリ失敗を命令上限超過と断定しない。GCSetDebugと並行tickを拒否し、命令観測を無効化させない。要求・操作期限と終了確認を維持する。設定JSONとビルドを確認、上限到達・停止・実機実行は未検証。

- P15-06 Aloe取消：AloeBatchRunnerはコンパイル、1回の実行、補助プロセスの終了確認を1件のQueuedOperationへ関連付ける。Aloe全体にはRequestTimeoutSeconds、各ホスト操作とコンパイルにはOperationTimeoutSecondsを適用する。要求取消、非常停止、ロック失効、サーバ終了のキュートークンを伝達し、子プロセスの終了確認が済むまでFIFO次要求へ進まない。所有者はキューのOwnerIdと照合し、ロック付き要求は各ホスト操作の前後で同じLockIdと所有者を確認する。停止未確認は解除完了としない。REQUEST_TIMEOUTはtimed_outへ変換しJSON要求への既存操作期限は維持する。実機取消・失効・停止は未検証、aloe受付はP15-07待ち。

- P15-08 Aloeシグナル連携：固定プリリュードにregister_signal(conditionJson, retentionSeconds)、wait_for_signal(signalId, timeoutMs)、unregister_signal(signalId)を追加する。各引数は文字列で、登録はsignalId、待機はsignaled／timeout、解除はunregisteredを返す。親側で条件JSONと数値を厳密に解析し、既存OperationDispatcherの同名操作へ渡す。所有者・ロック文脈、操作期限、要求取消を維持し、Aloe子へロック資格情報を渡さない。timeoutは通常結果としてAloe条件分岐に利用できる。ビルド警告0・エラー0、サンプルのコンパイル・実機動作は未確認。
- P15-07 Aloeバッチ受付：submit_batchはformat:jsonでoperations配列、format:aloeでsourceを受け付け、両方の有効値の同時指定をINVALID_ARGUMENTとする。ソースは1～65536 UTF-16単位、NULと不正UTF-16を拒否。Aloe依存を受付時と実行時に確認する。同じ所有者・requestId・同じソースは既存状態を返し、異なるソースはREQUEST_ID_CONFLICT。Aloeはコンパイルから補助終了確認まで1件のFIFO要求へ登録し、queuedをコンパイルや操作成功と扱わない。ロック中とシグナル呼出しには所有者のlockTokenを指定する。get_request/cancel_requestと結果保持は既存基盤を使用する。実機MCP受付・実行・取消は未検証。利用例はdocs/ALOE_BATCH.md。

- P15-08 Aloeシグナル連携：Aloeプリリュードにregister_signal(conditionJson, retentionSeconds)、wait_for_signal(signalId, timeoutMs)、unregister_signal(signalId)を追加する。各関数は既存ディスパッチの同名操作を親側で実行し、登録ID・signaled/timeout・解除済み結果をAloeへ返す。操作前後のロック所有者照合、既存の操作期限・要求取消を適用する。一般用途I/Oは増やさない。全体ビルド成功、Aloeサンプル実行・MCP実機動作は未検証。

- P16-01 配布マニフェスト：scripts/New-InstallationManifest.ps1はステージ済みbinを対象にKugutsu各アセンブリの版と全製品バイナリのSHA-256を記録し、Aloe依存物はAloeDependencies.csの固定版・名前・ハッシュと照合する。不足・不一致時は生成を中断する。installation.jsonは一時ファイルから置換し、x86/net10.0-windowsを宣言する。実際のDebug配置で11ファイルの生成とJSONパースを確認。MSI統合・署名は後続項目。

- P16-02 WiX MSI：installer/Kugutsu.wixprojはステージ済みペイロードからinstallation.jsonを生成後、WiX Files harvestingでbin/config/datの構造を保持して収録し、空のlogsを作る。32bit Program Filesを初期位置としてINSTALLFOLDERによる任意パス変更を許す。UpgradeCodeとMajorUpgradeを設定。E:\Kugutsu指定例とビルド手順をinstaller/README.mdへ記録した。WiX ICE検証込みのx86 MSIビルド成功（警告0・エラー0）。インストール・公開はしていない。

- P16-03 MCP資格情報初期生成：CredentialLoader.EnsureCreatedはユーザー単位Mutex下で既存資格情報を検証し、欠落時だけ32バイト乱数由来のURL-safe tokenをCredWriteで保存する。Mainの限定CLIフラグをFileRef deferred MSI actionがImpersonate=yesで起動し、値を引数・ログ・ファイルへ出力しない。修復・更新・再インストールは既存値を維持し、uninstallでは削除しない。対話的な導入ユーザーのCredential Managerを対象とし、SYSTEM無人配布で対話ユーザー用を生成できない制約を説明。ソリューションとWiX ICE検証付きMSIビルド成功（警告0・エラー0）。Windows実資格情報の作成は実行していない。

- P16-04 ユーザー単位ログオン起動：インストーラーはHKCU\Software\Microsoft\Windows\CurrentVersion\RunのKugutsu値に、インストールされたbin\Kugutsu.exeを引用符付きで設定する。値は導入ユーザーのみへ登録し、アンインストール時はMSI管理コンポーネントで除去する。アプリを実起動・インストールせず、WiX ICE検証付きx86 MSIビルド成功（警告0・エラー0）。

- P16-05 launch_target_detection：起動対象検出: MSIの遅延カスタムアクションから--detect-launch-targetsを実行し、App Paths／既知配置に存在する起動ファイルのみを検出設定ファイルへ記録します。設定ロードは明示的なappsettings.jsonを優先し、未検出対象を許可しません。アプリ別・シェル別ブラウザ別の検出結果と未検出は検証環境で確認する。

- P16-06 webdriver_distribution：WebDriver配布: Setupは検出／手動設定されたFirefox・Chrome・Edgeだけを対象にし、Chrome for Testingのbuild別JSON、Microsoft Edge WebDriverのbuild別配布、geckodriver 0.36.0をHTTPS取得します。win32/win64の単一exeだけを抽出し、Edge／Firefoxは期待発行者のAuthenticode署名を検証します。未署名ChromeDriverは公式HTTPS配布元・Chrome buildメタデータ・実行ファイル4部FileVersionの完全一致を要求し、方式とSHA-256をマニフェストへ記録します。Googleの公式署名・チェックサム不在とローカルSHA-256の限界をADR 0011へ記録します。

- P16-07 ocr_model_distribution：OCRモデル配布: MSI作成時にTesseractLocalOcr.csの固定モデル／ネイティブDLL名とSHA-256を正本として照合し、eng/jpn traineddata、x86 native DLL 2件、tessdata_fastライセンスをinstallation.jsonへ記録します。配布元データ・版・ハッシュ・ライセンスのいずれかが欠落または不一致ならMSI作成を失敗させます。実行時の既存SHA-256再検証を保持します。

- P16-08 dotnet_runtime_detection：.NET Runtime検出: Setup prerequisite script reads HKLM 32-bit view under InstalledVersions/x86, checks registered 10.0.x versions and the matching runtime directories for Microsoft.NETCore.App, Microsoft.WindowsDesktop.App, and Microsoft.AspNetCore.App. It does not accept x64-only installs. JSON report plus exit codes 0=satisfied, 1=missing framework, 2=detection failure is the interface for the Burn bundle. PowerShell syntax is parsed; execution and installer integration await the Burn phase.

- P16-09 burn_runtime_bundle：Build downloads latest stable x86 .NET 10 Desktop and ASP.NET Core Runtime installers from Microsoft release metadata, verifies HTTPS source, metadata SHA-512 and Microsoft Authenticode signature, records versions and hashes in dotnet-runtimes.json, and embeds both in Kugutsu.Setup.exe. Burn detects installed x86 10.x frameworks and installs only missing prerequisites. Build succeeded with zero warnings/errors. On 2026-10-14 the runtimes were already present and setup 1.0.1 installed successfully to E:\Kugutsu; Kugutsu.exe started successfully.

- P16-10 installation_lifecycle：appsettings.jsonと資格情報を更新・削除後も保持し、製品ファイルとRun登録を削除。生成済みドライバー・検出情報を清掃し、共有ランタイムとログを維持します。2026-10-14にBurnセットアップ1.0.1をE:\Kugutsuへ導入し、MSIログ成功・アプリ版1.0.1.0・HKCU Run登録を確認。更新・アンインストールは未検証。


## 9. 参照

- [.NET 公式サポートポリシー](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core)
- [MCP公式仕様 - Streamable HTTP](https://modelcontextprotocol.io/specification/2026-07-28/basic/transports)
- [MCP公式仕様 - Authorization](https://modelcontextprotocol.io/specification/2026-07-28/basic/authorization)
- [Windowsへの.NET導入](https://learn.microsoft.com/en-us/dotnet/core/install/windows)
- [Microsoft UI Automation Control Patterns](https://learn.microsoft.com/en-us/windows/win32/winauto/uiauto-controlpatternsoverview)
- [Seleniumの対応ブラウザ](https://www.selenium.dev/documentation/webdriver/browsers/)
- [Selenium WebDriver BiDi](https://www.selenium.dev/documentation/webdriver/bidi/)
- [Seleniumの要素操作](https://www.selenium.dev/documentation/webdriver/elements/interactions/)
- [Windowsの既定アプリ照会](https://learn.microsoft.com/en-us/windows/apps/develop/windows-integration/default-apps-platform)
- [WiXでの.NET検出と導入](https://docs.firegiant.com/wix/tools/wixext/dotnet/)
- Itoguruma: `MCP_SETUP.md`、`CONFIG.md`（登録済みItogurumaリポジトリを読み取り参照）
- Aloe-lang: `CompilerLib/AloeCompiler.cs`、`CompilerLib/CompilerLib.csproj`、`RuntimeLib/RuntimeLib.csproj`、`RuntimeLib/AloeVM.cs`（登録済みAloe-lang資料を読み取り参照）
