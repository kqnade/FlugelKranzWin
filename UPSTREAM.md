# 上流更新の追跡

2026-10-02 に `ReinaS-64892/FlugelKranz` の `main` を `32126d6` まで確認しました。
上流の v1.0.0 タグは `79766ac` です。
Windows 版の分岐元は `bba6ed1` です。Linux 側の変更を一括マージせず、Windows の入力・頭部操縦・独立 Drag・描画補正を維持して移植します。

| 上流コミット | 判断 | Windows での扱い |
| --- | --- | --- |
| `1b628dd` | 対象外 | WayVR の入力競合説明。Windows は SteamVR Input を使用。 |
| `4721c60` | 見送り | Turn / I モードの既定値変更。Windows で調整済みの既定値を維持。 |
| `8bf3d08` | 取込み | 両手 Turn の軸・支点を掴み始めに固定し、軸外の動きによる意図しない回転を抑制。回帰テストも移植。 |
| `2c9e7dd` | 取込み | スムージングから慣性へ、実際の表示姿勢を起点に連続移行。頭部操縦の指令適用前に実施。 |
| `f61bca5` | 見送り | D-pad の片側保持時間を用いるモード切替・OpenXR 振動通知。Windows は左右 reset_hold を両方保持して1秒の既存仕様を維持。 |
| `3419efb` | 対象外 | Monado の再接続探索。SteamVR / IPC の接続管理には適用しない。 |
| `4721c38` | 移植 | 第三者通知をビルド・publish に同梱。依存版数とランタイム説明はWindows版に合わせた。 |
| `79766ac` | 見送り | 上流1.0.0の版番号・Linux dist除外。Windows版のリリース番号として転用しない。 |
| `b252ed5` | 取込み | global.json: SDK 10.0.200 / latestFeature。 |
| `1cf7c9c` | 取込み | Z加速の方向を、飛行回転を含むHMD正面と同じ座標系へ揃える。回帰テストも移植。 |
| `32126d6` | 対象外 | Monadoサブモジュール参照。AGENTS.mdの読み取り専用境界により変更しない。 |

マージコミット `da76830` / `335c587` の内容は、それぞれ `b252ed5` / `1cf7c9c` として確認済みです。

## Windows の検証環境

Avalonia.Headless.XUnit 12.1.2 は xUnit v3 API を使用しており、xUnit 4 では UI テストに MissingMethodException が発生します。
xunit.v3 を 3.2.2、VSTest runner を検証済み 3.1.4 に戻し、Renovate では両者を4未満に制限しています。
将来の更新時は Avalonia 側の互換性を確認し、UI テストを含む全件を実行してから制限を解除してください。

.NET SDK 10.0.401 で `dotnet test FlugelKranz.slnx -m:1` が193件成功しました。
頭部操縦、飛行OFFでの独立Drag、モード切替、姿勢変換の既存テストを含みます。
今回のCore変更は実機未確認です。SteamVRの再起動・稼働中の配布物の差し替えは行っていません。

## 次回の確認

```powershell
git fetch upstream --no-recurse-submodules
git log --oneline 32126d6..upstream/main
git diff 32126d6 upstream/main -- src/FlugelKranz.Core tests
```

確認後はこの基準コミットと取込み判断を更新します。追跡はこの記録による手動運用であり、自動マージや定期実行は設定していません。
