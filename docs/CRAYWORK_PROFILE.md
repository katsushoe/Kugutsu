# Craywork Open Projectプロファイル

2026-10-14。Kugutsuの`config/ui-profiles/craywork.open_project.json`に識別規則を配置します。Crayworkのソースは読取りだけを行いました。

## 読み取った根拠

- Crayworkの`source/src/Craywork.App/EditorApp.Document.cs`のDrawOpenPopupはOpen Projectモーダルを表示し、幅560fの`ImGui.InputText("Path", ref _openPath, 1024)`とOpen/Cancelボタンを描画します。
- `EditorApp.cs`の初期ウィンドウタイトルはCraywork - Flesh v2です。`Craywork.App.csproj`にはAssemblyNameの変更がなく、プロセス名の想定はCraywork.Appです。実機の実行名を変更した場合はtargetを修正します。
- 標準ImGuiのInputTextは入力枠の右側へラベルを描画します。[ImGui公式実装 v1.90.8](https://github.com/ocornut/imgui/blob/v1.90.8/imgui_widgets.cpp#L4664)を配置の参考にしています。Crayworkの実際の描画結果を測定したものではありません。

## 規則と利用例

プロセス名とタイトルに加え、OCRラベルOpen Project、Path、Cancelを必要とします。入力候補の右側のPathラベルへ`project_file_path_input`の意味を付けます。複数候補は全部返し、自動選択しません。

```json
{
  "name": "control.find",
  "args": {
    "windowId": "w_00000000000000000000000000000000",
    "driver": "imgui_visual",
    "profileId": "craywork.open_project",
    "semanticRole": "project_file_path_input",
    "controlType": "Edit"
  }
}
```

例のwindowIdは説明用です。前面の実ウィンドウ参照を指定します。認識できた候補はプロファイル・画面・規則・形状・OCRの根拠を含みます。`control.get_value`を使う場合は候補のelementIdと`method:"local_ocr"`を指定し、OCRの推定値として扱います。

## 入力の制限

Pathはこの規則では非機密のファイルパス表示として扱います。機密文字列を入力する運用ではsensitiveをtrueへ変更してください。focusIndicatorは設定せずallowTextInput:falseとします。ソースのInputText呼出しだけでは画像によるフォーカス表示を確定できないため、set_value/send_keysを公開しません。実際のテーマと表示根拠を確認してから入力許可と明示フォーカス規則を追加します。

ビルド時は既存の出力先プロファイルを上書きしません。配布・運用のプロファイル更新は版と内容変更を明示して行います。実機認識、OCR精度、入力成功は未検証です。
