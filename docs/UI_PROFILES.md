# 画像UIプロファイル schemaVersion 1

2026-10-14時点。`UiProfileDirectory`（既定`config/ui-profiles`）直下のJSONを読みます。インストールルート内の相対ディレクトリだけを許可し、リパースポイントは拒否します。ディレクトリ未作成は空の一覧、指定IDが存在しない場合は後続の照合で`UI_PROFILE_INVALID`です。

```json
{
  "schemaVersion": 1,
  "profileId": "example.open_project",
  "profileVersion": "1.0.0",
  "driver": "imgui_visual",
  "target": { "processName": "ExampleApp", "titleContains": "Example" },
  "screens": [
    {
      "screenId": "open_project",
      "requiredLabels": ["Open Project", "Path"],
      "rules": [
        {
          "ruleId": "project_path",
          "shape": "input_candidate",
          "label": "Path",
          "relation": "left",
          "semanticRole": "folder_path_input",
          "sensitive": false,
          "allowTextInput": false
        }
      ]
    }
  ]
}
```

この例は書式説明であり、対象アプリの認識や入力成功を確認したものではありません。

| 項目 | 契約 |
| --- | --- |
| profileId / profileVersion / screenId / ruleId / semanticRole | ASCII英数字・アンダースコア・ハイフン・ドットの1～64文字 |
| driver | imgui_visual / generic_visual |
| target.processName | 必須、1～256文字。実プロセスの名前との照合用 |
| target.titleContains / screens[].titleContains | 任意、1024文字まで |
| requiredLabels | 必須、1～32件、1件512文字まで。重複禁止 |
| rules | 画面ごと1～256件 |
| shape / relation | input_candidateはleft/right/above、button_candidateはinside |
| label | 必須、1～512文字。正規表現やプログラムを評価しない |
| sensitive | 任意の真偽値。省略時は不明で、非機密と断定しない |
| allowTextInput | 任意、既定false。入力欄候補だけ指定可能。実際の編集可否を表さない |
| focusIndicator | 任意、入力欄候補だけ。フォーカス判定で使う明示的な表示規則 |

focusIndicatorを指定する場合は`{"kind":"border_color","color":"#RRGGBB","tolerance":12,"inset":0,"coverage":0.7}`です。colorは必須、toleranceは0～64、insetは0～8、coverageは0.5～1。色の意味や実際のキーボードフォーカスをプロファイル読込みだけで保証しません。

未知・重複プロパティ、不正な型・版・列挙値・NUL・制御文字・不正サロゲートを拒否します。profileIdは全ファイル間で、screenIdはプロファイル内で、ruleIdはプロファイルの全画面間で一意にします。最大100ファイル、1ファイル256KiB、合計10MiB、JSON深さ16です。不正なファイルを黙って無視せず`UI_PROFILE_INVALID`を返します。

読込み成功時にSHA-256も保持します。メタデータ一覧は最後に成功した読込みの結果です。画面照合・意味判定・操作前の変更確認へ接続しています。内部状態を推測するコードや外部資源のURL、実行ファイル・DLLの指定は受け付けません。
