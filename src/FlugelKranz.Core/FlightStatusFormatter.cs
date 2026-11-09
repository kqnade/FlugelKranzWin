namespace FlugelKranz.Core;

internal static class FlightStatusFormatter
{
    public static string Format(FlightStateSnapshot state) => state.Activity switch
    {
        FlightActivity.OffSuspended => "飛行 OFF — ダッシュボード表示中は Drag も停止します。",
        FlightActivity.OffDrag => "飛行 OFF — Drag のみ有効（慣性なし）。入力を離してから掴んでください。",
        FlightActivity.Off => "オフ — 現在の位置・姿勢を保持しています。",
        FlightActivity.Suspended => "操作を一時停止中 — ダッシュボードを閉じ、スティックと操作ボタンを戻してください。",
        FlightActivity.ReturningToWalking => "無限歩行モードへ戻しています…",
        FlightActivity.ResettingSpace => SpaceReset(state.Mode),
        FlightActivity.WaitingForHead => "HMD のトラッキングを待っています。",
        FlightActivity.WaitingForControllers => "コントローラーの姿勢・操作入力を待っています。",
        FlightActivity.HeadPilotOn => "頭部操縦 ON — X/A で OFF、長押しで慣性リセット。",
        FlightActivity.HeadPilotOff => "頭部操縦 OFF — X/A で ON、左スティックで推進。",
        FlightActivity.Ready => "オン — 操作入力を一度離してから使用してください。",
        _ => throw new ArgumentOutOfRangeException(nameof(state))
    };

    public static string ModeChanged(FlightMode mode) => mode == FlightMode.InfiniteWalking
        ? "無限歩行モードへ切り替えました。操作入力を離してから使用してください。"
        : "自由飛行モードへ切り替えました。操作入力を離してから使用してください。";

    public static string SpaceReset(FlightMode mode) => mode == FlightMode.InfiniteWalking
        ? "無限歩行の高さを戻しています…"
        : "自由飛行の水平へ戻しています…";
}
