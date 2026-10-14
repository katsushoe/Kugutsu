# Kugutsu 接続・操作手順書

この手順では、インストール後にKugutsuをMCPクライアントへ接続し、単体操作またはバッチ操作を実行して結果を確認します。Kugutsu本体はWindowsログオン時に起動してTaskTrayへ常駐します。AIクライアントはstdioプロキシを介して本体へ接続します。

## 1. インストールと起動

1. Kugutsu.Setup.exeを実行します。セットアップは不足しているx86版.NET 10 Desktop RuntimeとASP.NET Core Runtimeを導入し、KugutsuをE:\Kugutsuへインストールします。
2. セットアップを実行したWindowsユーザーのCredential ManagerにMCP用Bearer資格情報が作成されます。値をコピーしたり、設定ファイルやMCPクライアント設定へ記入したりする必要はありません。
3. WindowsへログオンするとE:\Kugutsu\bin\Kugutsu.exeが起動し、TaskTrayに常駐します。更新後も設定と資格情報は保持されます。
4. インストール先のconfig\appsettings.jsonを必要に応じて編集し、本体を再起動します。接続先は初期状態でhttp://127.0.0.1:47641/mcpです。外部ネットワークへ公開せず、同じWindowsユーザーで本体とstdioプロキシを実行してください。

MCPサーバへ直接HTTP接続する代わりに、次節のstdioプロキシを利用してください。プロキシが論理セッションの発行・再開と認証ヘッダーを管理します。

## 2. MCPクライアントへ登録

MCPクライアントの設定へ、stdioサーバとして次の情報を登録します。設定形式はクライアントに合わせてください。

~~~json
{
  "mcpServers": {
    "kugutsu": {
      "command": "E:\\Kugutsu\\bin\\Kugutsu.McpProxy.exe",
      "args": []
    }
  }
}
~~~

ServerUrlを一時的に上書きする場合だけ、argsに--urlとURLをこの順で指定します。通常はインストール済み設定ファイルのURLを使用します。プロキシはMCPメッセージを標準入出力で中継し、診断ログをインストール先のlogsへ記録します。クライアントからKugutsuのツール一覧を再読込し、get_versionが応答することを確認してください。

## 3. 単体操作と結果確認

操作依頼はexecute_operationで受け付けます。requestIdには毎回新しいUUIDを指定し、操作名と引数をoperationへ渡します。次の例はウィンドウ一覧を取得します。

~~~json
{
  "schemaVersion": 1,
  "requestId": "9f81c6d5-08da-4c73-9560-53eb9829517a",
  "operation": {
    "name": "window.list",
    "args": {}
  }
}
~~~

受付直後のqueuedは実行完了を意味しません。返された要求IDをget_requestへ渡し、状態が終端状態になるまで照会してから操作結果またはエラーを確認します。cancel_requestは待機中要求または協調取消に対応する実行中要求に使えます。入力操作の成功応答は入力の送出を示すもので、対象アプリが業務処理を完了したことまでは保証しません。

## 4. JSONバッチとAloeバッチ

順番に実行するJSON操作列はsubmit_batchへformat:"json"とoperationsを渡します。各項目はnameとargsを持ち、引数なしでもargs:{}を指定します。

~~~json
{
  "schemaVersion": 1,
  "requestId": "2b8bd5d3-0610-46a7-8e6e-4d3d6f65820b",
  "format": "json",
  "operations": [
    { "name": "window.list", "args": {} },
    { "name": "screen.capture_desktop", "args": {} }
  ]
}
~~~

受付時にバッチ全体が検証され、受け付けられた項目は単一キュー要求として順に実行されます。途中で失敗・取消・タイムアウトすると、未開始項目はskippedになります。返された要求IDをget_requestへ渡し、操作ごとの結果を確認してください。

Aloeプログラムを使う場合はformat:"aloe"とsourceを指定し、operationsは省略します。構文、対応範囲、制限、利用例は[Aloeバッチの利用](ALOE_BATCH.md)を参照してください。JSONとAloeの両形式を同時指定できません。

## 5. 複数操作の排他ロック

連続した作業中に他のAIセッションの操作を混ぜたくない場合は、同じMCPクライアントセッションからlock_mcpを呼びます。取得応答のlockTokenを、execute_operationまたはsubmit_batchのトップレベル引数へ付けて操作します。別の所有者はロック中の操作を実行できません。

作業中にリース期限が近づいたらrenew_mcp_lockで延長し、全ての要求が終わった後にunlock_mcpで解放します。unlock_mcpがMCP_LOCK_BUSYを返した場合は未完了の自分の要求が残っています。is_mcp_lockedはロック状態を確認します。lockTokenをログ、ファイル、他のセッションへ共有しないでください。ロック期限切れや緊急停止は、すでにアプリへ送信された副作用を巻き戻しません。

## 6. 画面画像と画像ベースUI操作

screen.captureまたはscreen.capture_desktopの要求が完了したら、get_requestの画像コンテンツで画面を確認できます。画面上の位置やコントロールの探索には、対応ドライバーとUIプロファイルが必要です。まずui.list_driversで利用可能なドライバーと能力を調べます。Windows標準のアクセシビリティ情報が取得できるアプリではuiaを優先します。

独自描画UIで画像ドライバーを使う場合はcontrol.findにドライバーとプロファイルを明示し、返されたelementIdを使います。画像経由の各control操作には対応するmethodを明示します。たとえば入力欄の文字読取はlocal_ocr、画像上のクリックはcoordinate_click、文字入力やキー送信はcoordinate_keyboardです。プロファイルが要素の種類・位置・安全条件を特定できない場合、操作は拒否されることがあります。OCRは推定値で、任意の独自描画UIの認識、値の完全一致、アプリ内部状態の取得は保証されません。画像操作の設計は[SPECIFICATION.md](../SPECIFICATION.md)のUIプロファイル節と[Crayworkプロファイル例](CRAYWORK_PROFILE.md)を参照してください。

## 7. エラーとログ

引数や操作名が不正ならINVALID_ARGUMENT、要求が見つからないか結果保持期限を過ぎているとREQUEST_NOT_FOUND、キュー容量超過ならQUEUE_FULLなど、応答のerror.codeを確認してください。接続・認証エラーではプロキシが終了する場合があります。本体が起動していること、同じWindowsユーザーで実行していること、設定URLが一致していることを確認し、必要なら本体とクライアントを再起動してください。認証資格情報を設定ファイルへコピーして回避しないでください。

本体とプロキシのログはE:\Kugutsu\logsにUTF-8 JSON Lines形式で出力されます。診断時は時刻、コンポーネント、イベント、エラーコードを確認してください。入力した文字列やBearer／セッション／ロック資格情報はログへ記録しません。

関連仕様：[MCP接続とツール](../SPECIFICATION.md)、[Aloeバッチ](ALOE_BATCH.md)、[UIプロファイル](UI_PROFILES.md)、[MSIセットアップ](../installer/README.md)。