# 上流更新の追跡

2026-11-09 に `ReinaS-64892/FlugelKranz` の `main` を `e9b0cac3291c47f84ad867642dec420b6fefe3d6` まで確認しました。前回の基準は `32126d6` です。
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
git log --oneline e9b0cac..upstream/main
git diff e9b0cac upstream/main -- src/FlugelKranz.Core tests
```

確認後はこの基準コミットと取込み判断を更新します。追跡はこの記録による手動運用であり、自動マージや定期実行は設定していません。

## 2026-11-09 — Renovate 更新

### 1. ライブラリの概要

Avalonia はネイティブ描画の UI、Markup.Declarative は C# による UI 記述、System.CommandLine は起動引数、Evergine.Bindings.OpenXR は残存 OpenXR 経路、Microsoft.NET.Test.Sdk はテスト実行基盤です。Electron / WebView は導入していません。

### 2. バージョン情報

| PR | 対象 | 更新前 → 更新後 | 判断 |
| --- | --- | --- | --- |
| [#8](https://github.com/kqnade/FlugelKranzWin/pull/8) | Microsoft.NET.Test.Sdk | 18.10.0 → 18.11.0 | 取込み |
| [#9](https://github.com/kqnade/FlugelKranzWin/pull/9) | Avalonia Desktop / Wayland / Fluent / Headless.XUnit | 12.1.2 → 12.1.5 | 取込み |
| [#10](https://github.com/kqnade/FlugelKranzWin/pull/10) | Evergine.Bindings.OpenXR | 2026.9.1.18 → 2026.11.4.23 | 取込み |
| [#11](https://github.com/kqnade/FlugelKranzWin/pull/11) | xunit.v3 / runner | PRは4.0.3 / 4.0.1を提案 | 保留。ローカルの3.2.2 / 3.1.4を維持 |
| [#12](https://github.com/kqnade/FlugelKranzWin/pull/12) | System.CommandLine | 2.0.12 → 2.0.14 | 取込み |
| [#13](https://github.com/kqnade/FlugelKranzWin/pull/13) | Avalonia.Markup.Declarative | 12.1.1 → 12.2.0 | 取込み |

今回採用した更新にメジャー変更はありません。対象は上記Renovate提案に限定し、全パッケージの最新メジャー調査は行っていません。

### 3. 変更スコープ

3つのcsproj、配布用THIRD-PARTY-NOTICES.txt、Renovate制限の説明を更新しました。アプリケーションコード・テストコード・Monadoサブモジュールは変更していません。NuGetロックファイルはありません。復元されたアプリ依存一覧と第三者通知の版数を照合しました。

### 4. 依存関係

Avalonia関連の間接依存も12.1.5へ揃います。SkiaSharp 3.119.4、HarfBuzzSharp 8.3.1.3などの描画依存は従来どおりです。Test.Sdkに伴いTestHostとCodeCoverageが18.11.0になります。
配布されたAvalonia.Headless.XUnit 12.1.5のnuspecはxunit.v3.extensibility.core 3.2.2を参照しています。Markup.Declarative 12.2.0のAvalonia下限は12.1.1で、アプリの12.1.5と整合します。OpenXRバインディングとSystem.CommandLineの使用対象フレームワークには追加NuGet依存がありません。

### 5. 使用箇所

- src/FlugelKranz/FlugelKranz.csproj: UIとCLIの直接依存。
- src/FlugelKranz.OpenXR/FlugelKranz.OpenXR.csproj: OpenXRバインディング。
- tests/FlugelKranz.Tests/FlugelKranz.Tests.csproj: UIテストとテスト実行基盤。
- THIRD-PARTY-NOTICES.txt: 配布するアプリ依存の版数とライセンス。
- renovate.json: xUnit 4未満の互換性制限。
- UPSTREAM.md: 過去の版数は当時の検証記録として保持。

### 6. 公式変更内容

[Avalonia 12.1.3](https://github.com/AvaloniaUI/Avalonia/releases/tag/12.1.3)（9月22日）、[12.1.4](https://github.com/AvaloniaUI/Avalonia/releases/tag/12.1.4)（10月9日）、[12.1.5](https://github.com/AvaloniaUI/Avalonia/releases/tag/12.1.5)（11月2日）のリリースノートを確認。Headless起動・終了競合、テーマ切替、コマンド付きButtonのリーク、Windows入力などの修正を含みます。
[Markup.Declarative 12.2.0](https://github.com/AvaloniaCommunity/Avalonia.Markup.Declarative/releases/tag/v12.2.0)（11月3日）は公開拡張生成・重複警告と画素形式の修正です。
[Test.Sdk 18.10.1](https://github.com/microsoft/vstest/releases/tag/v18.10.1)（9月15日）と[18.11.0](https://github.com/microsoft/vstest/releases/tag/v18.11.0)（10月13日）はカバレッジ出力分離、ログ・引数処理、MTP testhostの既定無効化などを含みます。
[OpenXRの配布元コミット差分](https://github.com/EvergineTeam/OpenXR.NET/compare/a857a6d6f79be5ea1a3c3d18eda6cf07b05951e1...41ff2f5b85487c1f02733969403a750f92dedbdf)はレジストリ再生成と別名エントリーポイント生成修正を含みます。
System.CommandLine 2.0.13 / 2.0.14の個別リリースノートは取得した公式リリース一覧では確認できませんでした。配布nuspecの出典はdotnet/dotnetの0427b9abbead3fa64c6655eb4290c537397131faです。全更新の旧版・新版公開日時および中間版の個別変更の完全な確認は未実施です。

### 7. コード・設定と検証

API移行を必要とするビルドエラーはありません。版数の参照箇所と通知を更新し、既存テストを変更せず検証しました。

- .NET SDK 10.0.401、Releaseビルド: 警告0、エラー0。
- Releaseテスト: 193件成功、失敗0、スキップ0。3件のAvaloniaFactを含む。
- win-x64自己完結publish: 成功。出力先はartifacts/renovate-publish。
- CLI: --helpは終了コード0、値を欠いた--lib-monadoは終了コード1。
- 制限された実行環境ではNuGetのDNS解決とテスト完了に問題があったため、パッケージ取得・テストは通常環境で再実行。
- VR実機、Linux経路、ネイティブドライバーの再検証は未実施。配布先への上書き・SteamVR再起動は行っていません。

### 8. 今後利用できる変更

Markup.Declarativeの公開拡張生成は将来のUI部品共有に使えますが、今回の更新では利用コードを追加していません。

### 9. 過去の失敗と保留

過去のPR #7によるxUnit 4導入後、UIテストでMissingMethodExceptionが発生し、aece4d9で3系へ戻しています。今回も配布パッケージは3系に依存するため、#11は取り込まず制限を保持しました。4系の再採用には別途UIテストを含む互換性検証が必要です。
## 2026-11-09 — Linux参照元の追加修正

[比較範囲](https://github.com/ReinaS-64892/FlugelKranz/compare/32126d6...e9b0cac3291c47f84ad867642dec420b6fefe3d6)は9コミットです。

| コミット | 判断 | 理由・対応 |
| --- | --- | --- |
| 52736e2 | 見送り | モード・遷移の型共通化。Windows固有の頭部操縦・OFF時Drag・SteamVR入力を含む制御構造は維持し、必要な修正だけ移植。 |
| 2cbd8a4 | 見送り | 操作する手のflags enum化。挙動修正ではなく、今回の修正に必須ではない。 |
| 1b6da75 | 移植 | 無限歩行復帰時の回転支点の横ずれを補正。開始時に支点を取得し、回転補間後の並進を計算して支点を垂直移動させる。 |
| 9d43328 | 対象外 | Monado/OpenXRのデバイス校正を保持する修正。Windowsはドライバーの物理姿勢キャッシュとStanding変換を使うため該当しない。 |
| 67e691f / 193e84c / bf72f53 | 対象外 | OpenXRのSteam Frameプロファイルとボタン割当。WindowsはSteamVRの論理アクションを使用し、上流の物理ボタン割当は導入しない。 |
| 15eb318 / e9b0cac | 確認済み | 上記変更のマージコミット。 |

移植元: [ReinaS-64892/FlugelKranz 1b6da75](https://github.com/ReinaS-64892/FlugelKranz/commit/1b6da75cb286b7037eb75b1a4537966b4349b337)。数学的な補正と5姿勢の回帰ケースを移植し、Windows側の制御ループへ適応しました。支点pに対して開始位置a0=O0(p)、着地点a1=(a0.X, Ob(p).Y, a0.Z)をsmoothstep補間し、姿勢R(u)に対する並進をa(u)-R(u)pとします。WindowsではStandingのY軸が基準です。

遷移開始後の頭・手の移動によって支点を取り直しません。ダッシュボード表示・OFFによる中断と再開は既存Windows仕様を維持し、再開時には新しい遷移として支点を取得します。上流のセッション型への統合は行っていません。Monadoサブモジュール、入力割当、既定値、Electron/WebViewを含むUI基盤の変更はありません。
検証: .NET SDK 10.0.401 / Releaseで205件成功（失敗0、スキップ0）。新規12ケースは支点の垂直軌道5姿勢、不正支点2件、制御ループを通したHMD・中点・左右/両手Dragの5件です。補正を一時的に外すと軌道テスト5件がすべて失敗し、復元後に全205件の成功を再確認しました。win-x64自己完結publishも成功（artifacts/upstream-publish）。コード・テストの静的レビューで追加の問題はありませんでした。VR実機検証は未実施です。