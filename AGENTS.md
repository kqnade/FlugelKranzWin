# Repository Guidelines

## 対象と仕様

- FlugelKranzWin は [FlugelKranz](https://github.com/ReinaS-64892/FlugelKranz) の Windows x64 / SteamVR 向け派生版です。Reina_Sakiria の自由飛行の仕様・Core・UI を基礎にしています。
- このリポジトリの開発・配布・サポート対象は Windows のみです。Linux / Monado / WiVRn の利用・開発は親リポジトリへ案内します。残存する Linux コードは、サポート継続の約束ではありません。
- 操作・慣性・入力・モード移行の仕様は `README.md`、導入・実機確認状況は `WINDOWS.md`、座標変換・IPC・描画補正は `native/driver/README.md` を参照します。挙動の変更時は実装・対応する仕様・テストを合わせて更新します。
- API の機能や制約は対象バージョンのヘッダー・実装で確認します。実測と仮説、自動テストと実機結果を区別してください。過去の成功報告があっても、後続の不具合報告を反映します。

## 構成

- `src/FlugelKranz/`：Avalonia UI、ViewModel、設定保存、CLI。Windows は `UseWin32()`、OFF 起動です。
- `src/FlugelKranz.Core/`：RigidPose、Drag / Turn、慣性、モード移行。UI・ネイティブ API に依存させません。
- `src/FlugelKranz.OpenVR/`：SteamVR Input、`DriverFlightRuntime`、共有メモリー IPC。旧 Chaperone バックエンドは互換性検証用で、現在の飛行変換には使用しません。
- `native/driver/`：MSVC / CMake の SteamVR ドライバー。姿勢更新フック、物理姿勢キャッシュ、Direct Mode フレーム姿勢補正。
- `tests/FlugelKranz.Tests/`：xUnit v3 / Avalonia.Headless.XUnit。ネイティブテストは `native/driver/` 内にあります。
- `src/FlugelKranz.OpenXR/` と `src/MonadoXrApi/`：上流から残る実装と依存関係。Windows のランタイム経路ではありません。ドキュメント整理を理由に削除しないでください。
- `ControllerInputMapping` は Core にありますが、互換性のため namespace は `FlugelKranz.OpenXR` です。
- `src/FlugelKranz.OpenVR/Vendor/` の公式 OpenVR C# バインディングと win64 DLL は同一 SDK コミットから一緒に更新します。生成コードを直接編集しません。第三者ライセンスを保持します。

## 空間操作と表示

- 物理ボタンを Core へ直結せず、OpenVR 側で左右の論理 Drag / Turn / reset_hold として取得します。Windows に上流のタッチ組み合わせ・Index 感圧条件を持ち込みません。
- 操作計算はドライバーが飛行変換前に保存した物理姿勢を使います。SteamVR の変換済み姿勢を再び操作入力へ戻さないでください。
- 右手系、メートル、Y 上、-Z 前方です。変換積は右側から適用します。Standing、Raw、ゲーム内座標を区別し、変換順序を明記します。
- Chaperone や VRChat の水平線設定は書き換えません。現在の Windows 経路では Standing 原点の外部変更を検出すると停止します。
- ゲームへ渡す仮想頭部姿勢と、表示補正へ渡す物理頭部姿勢を区別します。DirectMode_009 SubmitLayer の補正では画像・投影・予測時間などを保持します。
- 時刻ベース補正は `correctFramePose=true` と `timeBasedFramePose=true` を併用します。observeFrames を含めソース既定値は true です。ビルドが設定をコピーする点に注意してください。
- Quest 2 + Touch / VD で黒い縁・暗転が改善し、恒等変換時のパススルーと変換保持中の元のフレーム予測を維持する修正後、2026-09-14 にユーザーからふらつき解消の確認を得ています。他の構成まで検証済みとは扱いません。詳細は `WINDOWS.md`。
- SteamVR の終了・再起動はユーザーが行います。稼働中のドライバー DLL を上書きしません。登録スクリプトも再起動しません。

## Monado サブモジュール

`src/MonadoXrApi/monado/` は読み取り専用です。ファイル追加・編集・削除、生成・整形・ビルド出力、キャッシュの書き込み、サブモジュール更新・チェックアウト・参照コミット変更は禁止します。上流 Monado への PR 作成・送信も行いません。参照が必要でもこの境界を維持してください。

## 設定と UI

- 設定は変更時に JSON 保存し、次回復元します。既定は `%USERPROFILE%/.config/FlugelKranz/config.json`。`FLUGELKRANZ_CONFIG` はファイル、`XDG_CONFIG_HOME` はディレクトリを上書きします。
- 保存形式は `schemaVersion`、`mode`、`freeFlight`、`infiniteWalking`、`valveIndex`。既存の共有スキーマに Linux 由来の項目が残っています。旧平坦形式は現行既定値で初期化・上書きします。形式変更時は読み書きと旧形式の扱いをテストします。
- 各 UI 設定には既定値へ戻す操作を設けます。ソースの既定値変更と、ユーザーの保存済み設定変更は区別してください。
- Avalonia.Markup.Declarative と CommunityToolkit.Mvvm を使います。FluentTheme の既定は Light、状態色はテーマリソースから取得します。
- 主操作は ON/OFF、I/F、⚙、↻。初期サイズは 1100×650、設定パネルは約 80%、幅 720px 以下では全幅。設定行はラベル・操作部・リセットを揃えます。
- スクロールバーは常時表示（`AllowAutoHide = false`）し、操作部との間に右マージンを確保します。

## 開発・検証

.NET 10 SDK、MSVC x64 Build Tools、Windows SDK、CMake を使用します。リポジトリのルートから実行します。

```powershell
dotnet restore FlugelKranz.slnx
dotnet build FlugelKranz.slnx
dotnet test FlugelKranz.slnx
cmake -S native/driver -B artifacts/driver-build -A x64
cmake --build artifacts/driver-build --config Release
ctest --test-dir artifacts/driver-build -C Release --output-on-failure
./build-windows.ps1
```

`build-windows.ps1` は配布先へ書き込みます。使用中の配布アプリ・SteamVR を終了してから更新してください。`-Dotnet PATH -CMake PATH` で実行ファイルを指定できます。

コード変更では関連するビルドとテストを実行します。入力切替の再基準化、軸制約、回転支点、慣性、座標合成、追跡喪失、OFF と復元、フレーム時刻・画像情報の保持を検証してください。ドキュメントのみの変更ではリンク・コマンド・設定値と実装の一致、差分の検査を行います。実機操作は自動テスト成功だけで確認済みとしません。

## コーディング規約

C# はスペース 4 個、PascalCase / camelCase、ファイルスコープ名前空間、nullable を使用します。unsafe は相互運用に必要な範囲に限定します。CLI は System.CommandLine、Avalonia 起動はエントリースレッド上で行います。UI・操作計算・ネイティブ接続の責務を分離します。`MonadoXrApi` に Core・OpenXR・Avalonia 依存を追加しません。

## Git とレビュー

- 意味のあるまとまりで Conventional Commits を作成します。検証結果と未確認事項を記載します。push はユーザーが許可した範囲で、origin の送り先を確認して実行します。
- 通常の Git コマンドを使用し、`.git` の直接編集や `commit-tree` / `update-ref` による履歴作成は行いません。無断の履歴書き換え・force push は行いません。
- AI の通常の Author / Committer は `OpenAI Codex <codex@openai.com>` とし、`-c commit.gpgSign=false` を指定します。ユーザーが特定コミットの Author を明示指定した場合は、そのコミットだけ `--author` で上書きし、恒久設定や過去の履歴を変更しません。
- PR には具体的な変更後の挙動、影響範囲、検証結果、必要なドライバー設定を記載します。
