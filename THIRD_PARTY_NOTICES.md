# OCR依存コンポーネント

P14-01で使用する固定版の出所とライセンスです。MSI配布時にはこの文書と該当ライセンス文書を同梱します。

| コンポーネント | 固定版 | ライセンス・出所 |
| --- | --- | --- |
| Charles Weld .NET Tesseract wrapper | NuGet Tesseract 5.2.0 | Apache-2.0、Copyright 2012-2022 Charles Weld。[公式資料](https://github.com/charlesw/tesseract) |
| Tesseract OCR native | 上記パッケージ同梱 tesseract50.dll | Apache-2.0。[Tesseract](https://github.com/tesseract-ocr/tesseract) |
| Leptonica native | 同梱 leptonica-1.82.0.dll | BSD系。[ライセンス原文](dat/licenses/Leptonica-1.82.0.txt) |
| InteropDotNet | ラッパー内蔵 | MIT、Copyright 2014 Andrey Akinshin。[MITライセンス原文](dat/licenses/InteropDotNet-MIT.txt) |
| tessdata_fast eng/jpn | 4.1.0 / 65727574dfcd264acbb0c3e07860e4e9e9b22185 | Apache-2.0。[モデル付属ライセンス](dat/ocr/tessdata_fast-4.1.0/LICENSE) |

[Apache-2.0原文](dat/licenses/Apache-2.0.txt)を保存しています。MSIビルドはTesseractLocalOcr.csの固定値と照合したうえで、両モデル・両x86ネイティブDLL・モデルライセンスをinstallation.jsonへ記録します。実行時もモデルとDLLのSHA-256を再照合します。Visual C++ランタイムの導入・再配布条件はMSIフェーズで確認します。OCR本文・入力画像の保存は行いません。
