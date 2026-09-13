# FlugelKranz Windows port（初期移植）

Windows x64 / SteamVR の Standing 空間を対象とした初期移植です。
自由飛行・無限歩行・慣性・モード移行・設定画面には既存の Core / UI を使用します。
Linux / Monado 経路も維持しています。

## 起動

.NET 10 SDK を使用します。

```powershell
dotnet build FlugelKranz.slnx
dotnet test FlugelKranz.slnx
dotnet run --project src/FlugelKranz -- --diagnose
dotnet run --project src/FlugelKranz
```

SteamVR を先に起動してください。Windows では OFF で起動し、ON を押すと接続します。
`--diagnose` は UI を開かず HMD・両手の有効な pose を確認します。
アクション登録・入力の読み取りは行いますが、空間を書き換えません。
実際のタッチ・Force 操作やゲームへの反映まで保証する診断ではありません。
Windows では `--lib-monado` は使用しません。

単体配布用のビルド:

```powershell
dotnet publish src/FlugelKranz -c Release -r win-x64 --self-contained true -o artifacts/windows-x64
```

出力フォルダー全体を配布します。`FlugelKranz.exe`、`openvr_api.dll`、`Actions/`、
`manifest.vrmanifest`、`LICENSE-OpenVR` を一緒に配置してください。GPL-3.0 の本体ソースも提供してください。
設定保存先は既存仕様の `%USERPROFILE%/.config/FlugelKranz/config.json` です。
`FLUGELKRANZ_CONFIG` と `XDG_CONFIG_HOME` による上書きも維持しています。

## 入力と空間

既定バインディングは Valve Index (`knuckles`) と Touch (`oculus_touch`) 用です。
Windows 版は左右の Drag / Turn / reset_hold を SteamVR の boolean action として公開します。
Linux のタッチ組み合わせ判定は使用しません。
Touch は左右グリップ押下が Drag、左 X / 右 A 押下が Turn です。
Index は左右トリガー押下が Drag、左右 A 押下が Turn です。タッチだけでは動きません。
reset_hold は既定で未割当です。割り当てると片手1秒保持でモード別リセット、
両手1秒保持でモード切替になります。

⚙ →「SteamVR のバインド設定を開く」で編集できます（OFF のままで可）。
または `FlugelKranz.exe --bindings` を実行します。SteamVR には固定キー
`org.flugelkranz.windows`、名前 `FlugelKranz` で登録されます。
SteamVR のコントローラーバインド一覧でもこの名前を選び、Drag / Turn を好きな
ボタンのクリックやタッチに割り当てられます。旧版の `system.generated.flugelkranz.exe`
とは別登録です。SteamVR 自動起動設定は追加しません。

HMD と両手は1回の `GetDeviceToAbsoluteTrackingPose(RawAndUncalibrated)` の配列から
読みます。旧版の HMD は IVRSystem、両手は pose action という入力取得の混在を解消しました。
I モードで報告されたジッターに対する修正候補です。実機での解消は再確認が必要です。

物理座標 P は接続時の Standing 空間です。OpenVR raw pose を R とし、
接続時の Standing→Raw を S0 とすると P = S0⁻¹ R です。
Core が出力する全軸変換 D をゲーム側へ適用するため、
現在の Standing→Raw に S = S0 D⁻¹ を設定します。
これによりゲームが受け取る姿勢は S⁻¹ R = D P となります。
座標系は右手系、メートル、Y 上、-Z 前方です。VRChat 内の水平線設定は読み書きしません。

`SetWorkingStandingZeroPoseToRawTrackingPose` と `ShowWorkingSetPreview` を使い、
ルーム設定をディスクへ Commit しません。OFF は変換を保持し、リセットと通常終了は
接続時へ戻します。外部変更を検出した場合は上書きせず停止します。
プロセス強制終了時の復元は保証できません。

OVR Advanced Settings など、同じ Chaperone の working set / preview を操作する
ツールは終了してから使用してください。API には排他的な所有権取得がないため、
競合検出と書き込みの間の同時変更まで防止できません。
Seated / Raw 空間を使うゲーム、SteamVR を経由しない OpenXR アプリは対象外です。

## 3つのプロジェクトの役割

* [FlugelKranz](https://github.com/ReinaS-64892/FlugelKranz): Core、操作仕様、Avalonia UI の移植元。
* [OpenVR Advanced Settings](https://github.com/OpenVR-Advanced-Settings/OpenVR-AdvancedSettings):
  `MoveCenterTabController::updateSpace` 周辺の working-set preview と空間操作の参照。
  同時起動の依存ソフトではありません。
* [Kawaii Move Assist](https://github.com/ReinaS-64892/reina_s_kawaii_move_assist):
  OpenVR driver pose と world-from-driver 変換を調査。
  取得時点の core は Hello World、driver は X に 5m 加える試作です。
  今回そのドライバーは配布・登録せず、入力と飛行変換の分離設計の参照としています。

Chaperone 方式でピッチ・ロールがゲームへ正しく反映されなければ、KMA の driver hook
方式を使う追加開発が必要です。その場合は全デバイスとトラッカーの姿勢、速度、
他ドライバーとの共存、IPC と強制終了時の復元を別途実装・検証します。

## 実機確認

この移植はビルドと自動テストだけで完成判定しません。SteamVR / VRChat で次を確認します。

1. `--diagnose` で HMD・両手が検出されること。
2. SteamVR で設定した Drag / Turn / reset_hold が仕様通り動くこと。
3. 無限歩行の高さ・Yaw、自由飛行の XYZ 移動と Yaw / Pitch / Roll が反映されること。
4. 片手・両手の切替、慣性、モード変更、HMD / コントローラー追跡喪失と復帰。
5. FBT 使用時に HMD・両手・全トラッカーが同じ変換を受けること。
6. OFF の保持、全体リセット、終了後の元の空間への復元。

自動テストでは行列の転置・回転順序、非ゼロ原点での全軸変換、入力への変換の
二重適用防止、復元、外部変更を上書きしないこと、書き込み失敗を検証します。

2026-09-13: Windows x64 ビルドと137件の自動テストを確認。
Quest 2 + Touch / Virtual Desktop + SteamVR で `--diagnose` が成功し、
HMD・両手の有効な姿勢取得を確認しました。ゲーム内での移動・全軸回転・復元は未検証です。
単体配布 EXE でも同じ診断に成功し、左右の thumb rest / trigger touch アクションが
active であることを確認しました（実際のタッチ切替の検証は未実施）。
