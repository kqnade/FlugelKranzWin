# FlugelKranz Windows / SteamVR driver port

Windows x64 / SteamVR用の実験段階の移植です。自由飛行・無限歩行・慣性・UIは
FlugelKranzのCoreを使用し、XYZ全3軸の変換は専用SteamVRドライバーで適用します。
Linux / Monado経路は維持しています。

## 重要な変更

以前のWindows版はChaperone working-set previewへ全軸回転を書いていましたが、
対象SteamVRではYawだけが保持され、Pitch / Rollは破棄されました。
これを外部変更と誤判定し、OFF・復元失敗・切断失敗が表示される問題がありました。
プレビューを行わない書き込み・読み戻し試験で、この制約を確認しました。
この方式はXYZ回転に使用しません。新しいWindows版には下記ドライバーが必要です。

## ビルドと導入

.NET 10 SDK、MSVC x64 Build Tools、Windows SDK、CMakeを使用します。

```powershell
./build-windows.ps1
# PATHにない場合は -Dotnet PATH -CMake PATH で指定
```

`artifacts/windows-x64` が単体配布フォルダーです。全体を同じ場所に配置し、
そのフォルダーの `install-driver.ps1` を実行してSteamVRへ登録します。
スクリプトはSteamVRのvrpathregで登録するだけで、SteamVRを終了しません。
登録後にSteamVRを終了して再起動してください。VRChatも再起動が必要になります。

```powershell
cd artifacts/windows-x64
./install-driver.ps1
# SteamVRを再起動してから:
./FlugelKranz.exe --diagnose
./FlugelKranz.exe
```

解除する場合は `./install-driver.ps1 -Uninstall` を実行し、SteamVRを再起動します。
SteamVRのアドオン管理でドライバーを無効化した場合は、ONにしても接続できません。
アプリはOFFで起動します。SteamVRの自動起動設定は追加しません。
`--diagnose` は姿勢と論理入力を読み、飛行変換を書き込みません。
`--lib-monado` はWindowsでは使用しません。

## 操作とバインド

* I: 無限歩行。Y移動とYaw回転。
* F: 自由飛行。XYZ移動とXYZ全3軸回転。
* Touch: 左右グリップ押下がDrag、左X / 右A押下がTurn。
* Index: 左右トリガー押下がDrag、左右A押下がTurn。
* 押しながら手を動かすと空間を引っ張り、Turnを押して手首を傾けると回転します。
* 操作を始める前にボタンを一度離します。OFFでは変換を保持し、↻で元へ戻します。
* reset_holdは既定で未割当。片手1秒保持でモード別リセット、両手1秒保持でモード切替。

⚙ →「SteamVR のバインド設定を開く」、または `FlugelKranz.exe --bindings` で
編集できます。SteamVRのアプリ名は `FlugelKranz`、固定キーは
`org.flugelkranz.windows` です。左右Drag / Turn / reset_holdを好きなボタンやタッチへ
割り当てられます。Linux用のタッチ組み合わせ・Index感圧設定はWindowsでは使用しません。

## 実装

* [FlugelKranz](https://github.com/ReinaS-64892/FlugelKranz): 操作仕様、Core、Avalonia UI。
* [OVR Advanced Settings](https://github.com/OpenVR-Advanced-Settings/OpenVR-AdvancedSettings):
  SteamVR Input、空間操作、Chaperoneの参照。旧Chaperoneバックエンドは互換性検証用に残します。
* [Kawaii Move Assist](https://github.com/ReinaS-64892/reina_s_kawaii_move_assist):
  ドライバーのpose-updateフック方式を参照。固定X+5mの試作ドライバーそのものは導入しません。

新しいドライバーはIVRServerDriverHost_005 / _006のpose updateへフックし、
HMD・手・トラッカーのworld-from-driver変換に全軸変換を合成します。
Chaperoneは書き換えません。変更前の物理姿勢を共有メモリーへ保存し、
それをアプリの操作計算へ渡すため、飛行結果を入力へ戻す循環はありません。

接続時のStanding→RawをS、物理姿勢をP=S⁻¹R、Coreの飛行変換をDとすると、
ドライバーへ渡す変換はT=SDS⁻¹です。ゲーム側の姿勢はS⁻¹TR=DPになります。
右手系、メートル、Y上、-Z前方です。VRChat内の水平線設定は読み書きしません。

ドライバーはアプリのheartbeatが500ms途絶えると所有権を解除して恒等変換へ戻します。
復帰時は再接続が必要です。100msより古い物理姿勢は追跡喪失として扱います。
OFF中はheartbeatを維持して変換を保持します。通常終了は変換を解除します。
IPCの詳細・制約は `native/driver/README.md` を参照してください。

## 検証と未確認事項

```powershell
dotnet test FlugelKranz.slnx
ctest --test-dir artifacts/driver-build -C Release --output-on-failure
```

C#では全軸の座標合成、入力への二重適用防止、復元、接続失敗、追跡喪失、
Chaperoneが回転を拒否する場合の明示エラーを検証します。
C++ではXYZ変換とdriver-space速度の維持を検証します。
以前のChaperone版ではQuest 2 + Touch / Virtual Desktopの入力取得に成功しています。
新ドライバーの実機フック・head calibrationの合成・VRChatでの全軸回転は導入後の確認が必要です。

実機では、最初にOFFのまま診断し、HMD・両手を確認します。その後、小さい移動と
各軸の回転、ボタン解放、OFF、↻、終了復元を確認します。他のposeフック系ツールとの
共存やFBT全機種は未検証です。問題があればドライバーを解除できます。

設定は `%USERPROFILE%/.config/FlugelKranz/config.json` に保存します。
`FLUGELKRANZ_CONFIG` と `XDG_CONFIG_HOME` で変更できます。
配布時は本体GPL-3.0ソースと同梱のOpenVR・MinHookライセンスを提供してください。
