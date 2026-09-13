namespace FlugelKranz.OpenVR;

/// <summary>Dashboard and activation require a fresh neutral sample before motion resumes.</summary>
public sealed class PilotInputGate
{
    private bool armed;
    public static int Priority(bool requested, bool dashboard) => requested && !dashboard ? 0x01000000 : 0;
    public bool Update(bool requested, bool dashboard, bool neutral)
    {
        if (!requested || dashboard)
        {
            armed = false;
            return dashboard;
        }
        if (!armed)
        {
            armed = neutral;
            return true;
        }
        return false;
    }
}
