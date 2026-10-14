# Aloeバッチの利用

2026-10-14。固定版Aloeコンパイラーを使い、内部補助プロセスで実行します。構文・型・式は参照版Aloeの対応範囲です。一般用途のファイル・ネットワーク・コンソールI/Oは提供しません。[ホスト設計](adr/0010-isolated-aloe-host.md)が正本です。

## 受付

`submit_batch`で`schemaVersion:1`、要求UUID、`format:"aloe"`、`source`を指定します。`operations`は指定しません。JSON形式は`operations`を指定し`source`を省略します。ソースは1～65536 UTF-16単位で、不正UTF-16とNULを拒否します。

```json
{
  "schemaVersion": 1,
  "requestId": "11111111-2222-4333-8444-555555555555",
  "format": "aloe",
  "source": "function main(args: string[]): int { var windows = execute_operation(\"window.list\", \"{}\"); return 0; }"
}
```

例の要求IDは毎回新規UUIDへ変更してください。同じ所有者・要求ID・同じソースの再受付は既存結果を返し、ソースが違えばREQUEST_ID_CONFLICTです。queuedは受付成功で、コンパイルや操作の成功ではありません。実行・結果保持・取消・非常停止・ロック失効は既存のキューを使います。ロック中とシグナル操作には、その所有者のlockTokenを受付時に指定します。ロックの取得・更新・解除はプログラム外で行います。

## ホスト関数

execute_operation(name: string, args: string): string は既存操作のJSONデータをJSON文字列で返します。加えて、Aloeプリリュードから次のシグナル関数を呼べます。これらは既存ディスパッチの同名操作を親プロセスで実行し、所有者とMCPロックの検証、操作期限、要求取消を適用します。

| 関数 | 引数 | 戻り値 |
| --- | --- | --- |
| register_signal(conditionJson: string, retentionSeconds: string): string | 条件オブジェクトのJSON文字列と保持秒数（1～3600） | signalId |
| wait_for_signal(signalId: string, timeoutMs: string): string | 登録IDと待機時間（0～300000ミリ秒） | signaled または timeout |
| unregister_signal(signalId: string): string | 登録ID | 解除成功時は unregistered |

wait_for_signal の結果を文字列比較してtimeout時の分岐を書けます。シグナルの成立自体は既存の条件プロバイダーが監視します。シグナル関数の各引数は文字列です。解除・待機失敗は要求エラーとなり、プログラムはそこで停止します。Aloe要求には所有者のlockTokenが必要です。待機の実時間には既存の操作・要求期限が適用されます。

~~~aloe
function main(args: string[]): int {
    var conditionJson = "{...}";
    var signalId = register_signal(conditionJson, "60");
    var result = wait_for_signal(signalId, "1000");
    if (result == "timeout") {
        unregister_signal(signalId);
    }
    return 0;
}
~~~

この条件JSON部分は対象に合う有効なシグナル条件へ置き換えてください。上記プログラムは構文・実行を個別に確認していません。

一般のreadLine、print、sleepは使えません。プリリュードの内部readLineは固定関数フレームからの私有RPCに限り、コンソール入力を読みません。命令観測の無効化と並行tickも拒否します。

## 期限・制限・結果

- 全体はRequestTimeoutSeconds、コンパイルと各操作はOperationTimeoutSecondsです。取消後も補助プロセスの終了確認が済むまで次要求へ進みません。
- MaxAloeInstructions、MaxBatchOperations、AloeMemoryLimitMiBと私有フレーム16MiBを適用します。上限到達時はALOE_LIMIT_EXCEEDED等で失敗し、取得できた命令位置や呼出し位置を返します。操作が途中まで実行されている可能性があり、ロールバックしません。
- get_requestの最終dataには実行完了、コンパイル命令数・実行命令数・最終命令位置・ホスト呼出し数を返します。アプリ内処理の業務完了を保証しません。
- コンパイル失敗はALOE_COMPILE_ERRORとstage:compile、VM失敗はALOE_RUNTIME_ERROR、欠落・不一致の依存はALOE_UNAVAILABLEです。ソースや任意の例外本文を診断・ログへ含めません。

全体ビルドを確認しました。例の実行、MCPの実機受付、取消、上限到達、AIクライアント間の実機互換は未検証です。
