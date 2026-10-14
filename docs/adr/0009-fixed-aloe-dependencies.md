# ADR 0009: Aloe依存アセンブリの固定

2026-10-14。Aloeを外部プロジェクトとしてビルドせず、既存の配布アセンブリを読み取り、Kugutsuのdependencies/aloe/20261013へコピーします。ライセンスを同梱します。

| ファイル | AssemblyVersion | SHA-256 |
| --- | --- | --- |
| CompilerLib.dll | 1.0.0.0 | 677EA9E2176E640D3DA5F6ABAA5C459751FCFF6CE1ECF271AA5DE0DC137BE1BB |
| Aloe.CommonLib.dll | 1.0.0.0 | 629EA56CB31F690C752BC4C669795638E73D453D2CE5CE99D7E622A75588EBB8 |
| Aloe.RuntimeLib.dll | 1.0.0.0 | FD9E932EB230CD1A5164F25DB507EBFAF93CDC472628FA38D411EC784542C034 |

ビルド出力はbin/aloe/20261013です。設定でDLLのパス・版・ハッシュを変更できません。OpenVerifiedは各ファイルをFileShare.Readで保持し、再解析ポイント、サイズ、SHA-256、アセンブリ名と版を検査します。欠落・不一致・読取り不可はALOE_UNAVAILABLE、取消は要求へ伝達します。ロードはこの段階では行わず、後続アダプターは検証リースを使用中保持します。

2026-10-14の参照ソースではAloeCompiler.Compile(string)が実装されています。従来仕様の「空のクラス」は過去の状態で、現在の参照ソースに一致しません。Kugutsuへのコンパイル・実行・ホストAPI接続は別ステップです。この依存固定だけではAloeバッチを有効にせず、submit_batchのaloe形式はALOE_UNAVAILABLEを維持します。

アセンブリ版・ハッシュとコピー結果を静的に確認し、Kugutsu全体をビルドします。Aloeプログラムの実行、DLLのロード、テストは実施しません。
