# FlugelKranzWin：導入・開発ガイド

このリポジトリの対象は Windows x64 / SteamVR です。Linux / Monado / WiVRn 版は [親リポジトリ](https://github.com/ReinaS-64892/FlugelKranz) を参照してください。操作と慣性の仕様は [README](README.md) にまとめています。

## 必要な環境

- Windows x64、SteamVR、SteamVR で追跡できる HMD と左右コントローラー。
- このリポジトリの専用 SteamVR ドライバー。
- ビルド時：.NET 10 SDK、MSVC x64 Build Tools、Windows SDK、CMake。

配布アプリは self-contained のため、実行だけなら .NET SDK の別途導入は不要です。現時点の実機確認構成は Quest 2 + Touch / Virtual Desktop です。

## ビルドと初回導入

リポジトリのルートで実行します。

```powershell
./build-windows.ps1
# PATH にない場合：
# ./build-windows.ps1 -Dotnet 'C:/path/to/dotnet.exe' -CMake 'C:/path/to/cmake.exe'
```

出力先は `artifacts/windows-x64` です。フォルダー全体を配置し、配置先を決めてから登録してください。アプリ名は `FlugelKranz`、実行ファイルは `FlugelKranz.exe` です。

1. SteamVR を終了します。
2. 下記のフレーム姿勢補正設定を、配置したドライバーの設定ファイルへ反映します。
3. 配布フォルダーで登録スクリプトを実行します。
4. SteamVR を起動し、HMD と両手の追跡を確認します。
5. 診断後、アプリを起動します。

```powershell
cd artifacts/windows-x64
./install-driver.ps1
# 設定と登録を終え、SteamVR を起動してから：
./FlugelKranz.exe --diagnose
./FlugelKranz.exe
```

登録スクリプトは SteamVR の `vrpathreg` を使用します。SteamVR の終了・再起動や、自動起動設定の追加は行いません。SteamVR のアドオン管理でドライバーを無効化している場合は有効にしてください。

### 単一EXE版

アプリ・.NETランタイム・依存DLL・SteamVRドライバー・説明書・ライセンスを `FlugelKranz.exe` 1本に同梱できます。

```powershell
./build-windows.ps1 -SingleFile
# 出力先を変更する場合（既存フォルダーへの上書きはしません）：
# ./build-windows.ps1 -SingleFile -OutputDirectory artifacts/single-file/my-build
```

既定の出力は `artifacts/single-file/windows-x64/FlugelKranz.exe` です。単一EXE版でも `-Dotnet` と `-CMake` を指定できます。EXEだけを任意の場所へコピーして起動できます。WebView / Electron は使用せず、従来のAvalonia / Win32 UIです。

**初回はSteamVRを終了した状態でEXEを開くだけで、ドライバーの展開・登録まで自動で行います。** 完了案内の後、SteamVRを起動してください。SteamVRのインストール先が未登録なら、SteamVRを一度起動・終了してからEXEを開き直します。

登録済みのFlugelKranzドライバーが内蔵DLLと同じなら、その配置を継続して使用します。異なる場合は `%LOCALAPPDATA%/FlugelKranz/packages` の版ごとのフォルダーへ展開し、既存の `default.vrsettings` を引き継いで、新しい登録を確認してから旧版の登録を解除します。旧DLLや旧フォルダーは削除・上書きしません。他の名前のドライバーは登録解除の対象外です。

登録変更が必要なときにSteamVRが起動していれば、自動準備を止めて終了を案内します。SteamVRを終了してEXEを開き直すと再試行します。準備に失敗した場合も、成功扱いでアプリの接続へ進まずエラーを表示します。SteamVRの終了・再起動は自動では行いません。

`VR_PATHREG_OVERRIDE` を使う環境では、その登録ファイルを読み書きの両方に使用します。自動展開先も登録ファイルごとに分け、異なる環境のドライバー設定が混ざらないようにします。

手動展開も引き続き利用できます。EXEのあるフォルダーで次を実行します。

```powershell
./FlugelKranz.exe --extract-package ./FlugelKranz-support
./FlugelKranz-support/install-driver.ps1
```

展開先にはドライバー、登録スクリプト、説明書、ライセンスが入ります。`--extract-package` は展開のみで、SteamVRへの登録や再起動は行いません。既存の展開先には上書きしません。登録後はこのフォルダーを削除・移動せず、補正設定はその中の `driver/flugelkranz/resources/settings/default.vrsettings` で変更します。自動登録は単一EXEの通常起動だけが対象です。`--help`、`--diagnose`、`--bindings`、`--extract-package` と従来のフォルダー配布版では実行しません。

配布ファイルはEXE1本ですが、実行中もすべてをメモリー内だけで扱う方式ではありません。アプリ用ネイティブDLLは.NETがキャッシュへ展開します。SteamVRの入力設定は必要時に `%LOCALAPPDATA%/FlugelKranz/assets` へ展開し、アプリの実際の配置先を起動先として記録します。ドライバーはSteamVRがDLLとして読み込むため、上記の固定フォルダーへ自動展開・登録します。

配布物の検証は次で再実行できます。EXEだけを別名・別フォルダーへコピーして起動し、ドライバーとライセンスの展開、DLLハッシュ、既存先への上書き拒否を確認します。SteamVRへの登録・接続は行いません。検証先には新しいフォルダーを指定してください。

```powershell
./tests/Verify-SingleFile.ps1 -Executable artifacts/single-file/windows-x64/FlugelKranz.exe -ExpectedDriver artifacts/driver-build/Release/driver_flugelkranz.dll -WorkDirectory artifacts/checks/single-file-smoke
```

## フレーム姿勢補正の設定

配置先の `driver/flugelkranz/resources/settings/default.vrsettings` を、SteamVR の起動前に設定します。実機で黒い縁・暗転の改善を確認した構成は次のとおりです。

```json
{
  "driver_flugelkranz": {
    "loadPriority": 100,
    "observeFrames": true,
    "correctFramePose": true,
    "timeBasedFramePose": true
  }
}
```

**ソース・配布の既定値は、実機確認済みの上記 3 つの boolean 設定がすべて true です。** ビルドスクリプトはソースの設定を配布先へコピーするため、再ビルド・更新後にも確認してください。SteamVR に同名のユーザー設定がある場合は、そちらが優先されます。

`correctFramePose` は描画・配信に渡す HMD 姿勢の補正を有効にします。`timeBasedFramePose` はフレームの予測時刻に対応する物理姿勢を補間・予測する方式で、`correctFramePose=true` と併用します。`observeFrames` は観測用フックを有効にする設定です。`observeFrames` と `correctFramePose` の両方が false ならフレームのフックを追加しません。

ゲームに渡す飛行後の頭部姿勢と、表示補正用の物理的な頭部姿勢は異なります。姿勢更新フックだけでは、この表示経路の整合性を満たせませんでした。恒等変換時は元のフレーム姿勢をそのまま渡し、変換保持中は元のフレーム予測を維持して補正します。これらの修正後、既知のふらつきは解消したとの実機報告があります。

`timeBasedFramePose=false` は以前の履歴照合方式へ戻します。同時に頭と飛行姿勢が動く場合に補正が外れる問題があった方式です。詳細は [ドライバー文書](native/driver/README.md) を参照してください。

## 更新と登録解除

更新時は FlugelKranz と SteamVR を終了し、DLL が使用されていない状態でビルド・配置します。配布フォルダー全体と補正設定を確認してから SteamVR、ゲーム、FlugelKranz を起動してください。配置先を移動した場合は、旧配置の登録を解除して新しい配置を登録します。

登録解除は配布フォルダーで実行します。

```powershell
./install-driver.ps1 -Uninstall
```

解除後は SteamVR を再起動します。スクリプト自身は再起動しません。

## 操作・設定・診断

アプリは OFF で起動します。`I` は無限歩行、`F` は自由飛行です。Touch はグリップ押下が Drag、左 X / 右 A 押下が Turn です。OFF は飛行と慣性を止めます。F モードで Drag が有効なら、OFF 中も掴んで移動できます（解放慣性なし）。Drag も無効なら現在の変換を保持します。`↻` は変換をリセットします。詳しい片手・両手操作と慣性設定は [README](README.md#操作仕様) を参照してください。

```powershell
./FlugelKranz.exe --help
./FlugelKranz.exe --bindings
./FlugelKranz.exe --diagnose
```

`--bindings` は SteamVR のバインド設定を開きます。`--diagnose` は UI を開かず、接続・物理姿勢・論理入力を確認します。診断時は別の FlugelKranz を終了し、HMD と左右コントローラーを追跡可能にしてください。入力が揃わない場合は非ゼロで終了します。

設定の保存先は `%USERPROFILE%/.config/FlugelKranz/config.json` です。`FLUGELKRANZ_CONFIG` でファイル、`XDG_CONFIG_HOME` でディレクトリを指定できます。既存の保存値は、アプリの既定値とは別に復元されます。

## 動作確認

2026-09-13、Quest 2 + Touch / Virtual Desktop / SteamVR で、`5b41cab` の時刻ベース補正を有効にして確認しました。

- HMD・左右 Touch の物理姿勢取得と、SteamVR 側への飛行変換の反映を確認。
- 回転保持中の頭部運動、および回転入力中の頭部運動で、以前の黒い縁・暗転・ジッターが改善したとの報告あり。
- その後に報告された視界のふらつきに対し、`9932816` で恒等変換時のパススルー、`c743e66` で変換保持中の元のフレーム予測を維持する修正を実施。2026-09-14、ユーザーからふらつき解消の確認を得ています。

SteamVR MirrorView は正常に見えていても、HMD 内の表示とは一致しない場合がありました。検証では HMD 内の見え方も確認してください。ゲーム別、全軸の系統的な試験、他の HMD、FBT、他の姿勢フックツールとの共存、長時間使用は未検証です。

現在の慣性と移動速度についても調整要望があります。OVRAS の慣性ブレーキ 10%・ドラッグ倍率 1.4 倍に近い操作感への変更は未実施で、README の既定値は現行実装の値です。

## 問題の切り分け

| 症状 | 確認する内容 |
| --- | --- |
| SteamVR に接続できない | SteamVR の起動、ドライバー登録、アドオンの有効状態、別クライアントの接続 |
| 両手を取得できない | HMD を装着し、両方のコントローラーが SteamVR で追跡されているか |
| 入力が反応しない | FlugelKranz の SteamVR バインド、ON 状態、ボタンを離してから押し直したか |
| 回転で黒い縁・暗転・ふらつき | 補正設定とログ、回転保持中か更新中か、頭部運動の有無、HMD 内と MirrorView の違い |
| 基準空間の変更で停止する | 接続後の Standing 原点変更や他の空間操作ツールの動作。再接続前に状態を確認 |

SteamVR の `logs/vrserver.txt` にフックと `FrameAudit` のログが出ます。通常のインストール先では `C:/Program Files (x86)/Steam/logs/vrserver.txt` です。`history-matched pose correction installed` は補正フックの導入、`timed=1` はそのレイヤーで時刻ベース補正を試みたことを示します。変換保持中は一定の逆変換だけを適用するため `timed=0`・`corrected=1` となり、`held` の件数が増えます。`XYZ body-pose transforms v2` という起動ログだけでは補正の有効化は判断できません。

## 開発と自動テスト

```powershell
dotnet restore FlugelKranz.slnx
dotnet build FlugelKranz.slnx
dotnet test FlugelKranz.slnx
cmake -S native/driver -B artifacts/driver-build -A x64
cmake --build artifacts/driver-build --config Release
ctest --test-dir artifacts/driver-build -C Release --output-on-failure
```

C# テストは入力、座標合成、慣性、モード切替、追跡喪失、復元、設定、UI を検証します。ネイティブテストは姿勢・速度の変換、恒等変換時のパススルー、レイヤー情報の保持、フレーム姿勢の補間・予測を検証します。自動テストの成功と実機での表示品質は区別してください。

Windows UI は `UseWin32()`、入力と接続は `FlugelKranz.OpenVR`、操作計算は `FlugelKranz.Core`、姿勢適用とフレーム補正は `native/driver` が担当します。上流由来の OpenXR / Monado プロジェクトは依存関係・参照用として残っていますが、ここで Linux の導入・開発を扱うものではありません。

## 頭部操縦試作の実行

頭部操縦と独立した Drag は main に統合済みです。アプリのみを別フォルダーへ発行し、既存の登録済みドライバーへ接続する場合は次のコマンドを使います。これはドライバー DLL を更新しないため、描画補正の修正も必要な場合は上記のビルド・ドライバー導入手順を使用してください。

```powershell
dotnet publish src/FlugelKranz -c Release -r win-x64 --self-contained true -o artifacts/head-pilot-windows-x64
```

通常版の FlugelKranz を終了してから、このフォルダーのアプリを起動します。同じアプリキーを使うため、SteamVR のアクション manifest 登録は最後に起動した版のパスになります。通常版へ戻す場合は通常版を起動し直してください。

SteamVR 開発者設定の `Experimental overlay input overrides` を有効にし、アプリの F モード設定で頭部操縦を有効にします。プログラムはこのグローバル設定を自動変更しません。入力が待機したままの場合は `/actions/pilot` の左スティック推進と X / A 切替の割当を確認してください。操作仕様と未確認事項は [README](README.md#頭部操縦の試作windows--f-モード) を参照してください。

### VRChat 起動時の入力診断

SteamVR の待機空間では移動できる一方、VRChat 内では動かないという報告があり、原因は調査中です。設定画面の入力診断には `requested`、`priority`、`dashboard`、`suspended`、`neutral`、`stickActive`、`y`、X/A の有効・押下状態、`scenePid` を表示します。SteamVR 待機空間と VRChat 内で比較し、送信中のオフセットも併せて確認してください。診断表示は姿勢変換や入力優先度を変更しません。

## 2026-11-09 のドライバー修正について

[Qwerty0525氏の派生版](https://github.com/Qwerty0525/FlugelKranzWin/tree/9ccd6912aa743089ca9cdf265f79849da06ebd83) から、SlimeVR/Rebocapへの飛行変換二重適用、IPC待ちによる姿勢更新遅延、原点確認ログの大量出力への修正を選択移植しました。移植時に読み戻しの世代照合を常時使用するよう調整し、古い更新の遅延完了・多数デバイスの同時更新に対する追加修正を行いました。原作者はhiyomi（Qwerty0525）氏、選択移植と追加修正はOpenAI Codexです。

この変更は自動テストで検証していますが、この版のVR実機検証は未実施です。新しいドライバーDLLと同梱default.vrsettingsの両方が必要です。SteamVRの終了・更新・再起動は従来の導入手順に従って手動で行ってください。今回の作業では登録先のDLLや稼働中のSteamVRを変更していません。
