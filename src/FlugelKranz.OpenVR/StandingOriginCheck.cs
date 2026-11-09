namespace FlugelKranz.OpenVR;

/// <summary>
/// Rate limit for the Standing-origin change check. Each
/// <c>GetWorkingStandingZeroPoseToRawTrackingPose</c> call makes SteamVR log
/// "[CVRChaperoneClient] the working set is already updated" to the client's vrclient log,
/// so checking on every 3 ms loop tick wrote about 280 lines a second. An external change is
/// still caught within <see cref="Interval"/>, and the first check runs immediately.
/// </summary>
internal sealed class StandingOriginCheck(TimeProvider time)
{
    public static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(100);
    private long lastCheck;
    private bool checkedOnce;

    /// <summary>True when the origin should be read now (and marks that time).</summary>
    public bool Due()
    {
        long now = time.GetTimestamp();
        if (checkedOnce && time.GetElapsedTime(lastCheck, now) < Interval) return false;
        checkedOnce = true;
        lastCheck = now;
        return true;
    }
}
