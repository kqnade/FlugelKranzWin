# FlugelKranzWin SteamVR driver

Windows x64 用の server driver です。[導入・設定](../../WINDOWS.md) と [操作仕様](../../README.md) はそれぞれの文書を参照してください。この派生リポジトリは Windows / SteamVR を対象とし、Linux 版は [親リポジトリ](https://github.com/ReinaS-64892/FlugelKranz) が扱います。

## 姿勢の流れ

```text
HMD / controller / tracker の DriverPose_t
  ├─ 変換前の物理姿勢 → 共有メモリー → C# Core の操作計算
  └─ 飛行変換を適用 → SteamVR → ゲームの仮想姿勢

DirectMode_009 SubmitLayer
  └─ フレーム時刻に対応する物理 HMD 姿勢を mHmdPose へ設定
       → 画像・投影・予測時間を保持して転送
```

[Kawaii Move Assist](https://github.com/ReinaS-64892/reina_s_kawaii_move_assist) の姿勢更新フック方式を参考に、MinHook で `IVRServerDriverHost_005 / _006::TrackedDevicePoseUpdated` をフックします。元の world-from-driver と飛行変換を `qRotation` / `vecPosition` へ合成し、world-from-driver を恒等変換にします。線速度・角速度なども同じ座標系へ回転し、body-local の頭部校正と姿勢時刻は保持します。

恒等変換の指令では元の `DriverPose_t` をそのまま転送します。Chaperone setter は呼びません。フックを通る HMD、コントローラー、トラッカーへ変換が作用しますが、他ドライバーとの共存や全機種への対応を意味するものではありません。

## 座標系

右手系、メートル、+Y 上、-Z 前方です。積は右側を先に適用します。

接続時の Standing → Raw を `S`、Raw 空間の物理姿勢を `R`、Core の飛行変換を `D` とすると、次の関係になります。

```text
Core が読む物理姿勢： P = S⁻¹ R
ドライバーへの指令： T = S D S⁻¹
ゲーム側の姿勢：     S⁻¹ T R = D P
```

物理姿勢キャッシュは `world-from-driver * body * driver-from-head` で構成します。Core は変換前のキャッシュを読み、飛行結果を入力へ再適用する循環を防ぎます。各デバイスの通知時刻は独立しており、HMD と両手が完全に同時刻のサンプルとは限りません。

## IPC と接続状態

セッションローカルの共有メモリー `Local\FlugelKranz.Driver.State.v1`（4704 bytes、64 device slots）を `Local\FlugelKranz.Driver.Mutex.v1` で保護します。対応するレイアウトは `Transform.h` と `src/FlugelKranz.OpenVR/DriverConnection.cs` にあります。

変換を所有できるクライアントは 1 つです。heartbeat が 500 ms 途絶えると所有権を解除し、恒等変換へ戻します。復帰には再接続が必要です。C# 側では 100 ms より古い物理姿勢を追跡中と扱いません。

OFF 中も heartbeat と変換を保持します。通常終了時は変換を解除します。SteamVR 自体の停止・異常終了時の復元を保証する仕組みではありません。

## 表示補正

ゲームへ渡す飛行後の HMD 姿勢を、そのまま配信・再投影用の物理姿勢として扱うと、HMD 内に黒い縁や暗転が生じることがありました。MirrorView が正常でも起きたため、姿勢更新とフレームの表示補正を別々に扱います。

HMD の登録と `GetComponent` を観測し、`IVRDriverDirectModeComponent_009::SubmitLayer` をフックします。補正時はレイヤーをコピーし、両眼の `mHmdPose` を変更します。テクスチャ、depth、projection、bounds、prediction interval は保持します。

設定は SteamVR 起動時に読みます。`driver_flugelkranz` のソース既定値は `loadPriority=100`、`observeFrames=false`、`correctFramePose=false`、`timeBasedFramePose=false` です。実機で改善を確認した設定例は [導入ガイド](../../WINDOWS.md#フレーム姿勢補正の設定) を参照してください。

- `correctFramePose=true`：補正フックを有効にします。
- `timeBasedFramePose=true`：下記の時刻ベース方式を使用します。`correctFramePose=true` が必要です。
- `observeFrames=true`：補正を無効にした状態でも観測用フックを有効にします。補正無効時は元のレイヤーをそのまま転送します。

`observeFrames` と `correctFramePose` の両方を false にして再起動すると、これらのフレームフックを追加しません。`observeFrames=false` だけでは、補正中のログが無効になるわけではありません。SteamVR の同名ユーザー設定は配布ファイルの既定値より優先されます。

### 時刻ベース方式

128 件の HMD 履歴に、変換後の姿勢と元の `DriverPose_t` を保存します。

```text
物理サンプルの時刻 = pose-update 受信時刻 + poseTimeOffset
求めるフレーム時刻 = SubmitLayer 受信時刻
                     + flHmdPosePredictionTimeInSecondsFromNow
```

対象時刻を挟む物理サンプルがあれば位置の線形補間と quaternion SLERP を使用します。そうでなければ線速度・角速度で最大 100 ms 前後まで予測します。履歴は受信後 250 ms 以内、補間区間は 100 ms 以内、最新 HMD サンプルは 50 ms 以内を必要とします。頭部 / IMU の校正を保持します。

求めた物理姿勢を各眼のフレーム姿勢へ設定します。飛行後の姿勢との類似度から飛行指令を選ぶ処理には依存しません。ただし、ローカル受信時刻と API の予測時間に基づく推定であり、SteamVR / VD 内部の予測を完全に再現するものではありません。条件を満たさず補正できない場合は元のレイヤーを転送します。

### 以前の履歴照合方式

`timeBasedFramePose=false` では、予測した変換後の HMD 履歴にフレーム姿勢を照合し、一致する飛行変換の逆変換を適用します。対応がない・曖昧な場合は元のレイヤーを転送します。厳密なフレーム ID による対応ではありません。

飛行変換が 250 ms 以上一定で、最新 HMD サンプルが 50 ms 以内なら、一定の逆変換を利用する保持中の経路もあります。変換変更・無効姿勢・100 ms 超のサンプル間隔で保持条件をやり直します。この方式では飛行回転と頭部運動の同時実行で補正が外れる報告があり、時刻ベース方式を追加しました。

## ログと実機確認

`vrserver.txt` の `FrameAudit` は最大毎秒 1 レイヤーを記録します。

| 項目 | 意味 |
| --- | --- |
| `corrected=1` | そのレイヤーの姿勢メタデータを補正した。HMD の表示品質の保証ではない |
| `timed=1` | 時刻ベースの物理姿勢を使用 |
| `physicalDt` | 物理姿勢の時刻差に関する診断値 |
| `matchError=-1` | 時刻ベース方式では姿勢照合誤差を計算しない |
| `layers` / `missed` / `held` | 前の記録以降のレイヤー数 / 未補正数 / 保持中の経路の使用数 |

複数レイヤーが 1 フレームに属する場合があるため、これらを HMD のフレーム数として解釈しないでください。未対応の Direct Mode バージョン、またはフック導入前に登録された HMD では記録されない場合があります。`loadPriority=100` は HMD 登録を早期に観測するための設定です。

2026-09-13、Quest 2 + Touch / VD で時刻ベース方式（`5b41cab`）による黒い縁・暗転・同時回転時のジッター改善の報告を得ました。その後、視界のふらつきと酔いやすさの追加報告があり、原因は未確定です。表示品質の問題は調査継続中です。他の HMD、ゲーム、FBT 構成の系統的な検証は未実施です。

## ビルド・テスト・依存コード

リポジトリのルートから実行します。

```powershell
./build-windows.ps1
ctest --test-dir artifacts/driver-build -C Release --output-on-failure
```

MSVC x64、Windows SDK、CMake、.NET 10 が必要です。`transform_tests`、`observer_tests`、`frame_tests` は座標合成・恒等変換・フレーム情報保持・物理姿勢の補間と予測を検証します。テストだけでは実機のフック互換性や表示品質を確認できません。

同梱コードの出典とライセンスを保持してください。

- OpenVR header / license：ValveSoftware/openvr `0924064316de3effbcd1acf1e309182a2deb1c05`。
- MinHook source / build files：TsudaKageyu/minhook `8af6b4acae5a9388fd742b56fa79ece89d96f823`。
