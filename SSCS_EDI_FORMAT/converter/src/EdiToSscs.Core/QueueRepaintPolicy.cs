namespace EdiToSscs.Core;

/// <summary>
/// The fixed modal-prompt repaint seam. A prompt can close while later queue items have already
/// changed the native ListView model; repaint every conversion list immediately after it closes.
/// </summary>
public static class QueueRepaintPolicy
{
    public static void AfterPrompt(params Action?[] repaintLists)
    {
        ArgumentNullException.ThrowIfNull(repaintLists);
        foreach (Action? repaint in repaintLists)
        {
            try { repaint?.Invoke(); }
            catch
            {
                // A disposed/in-transition native list must not prevent the other queue from
                // repainting, and repaint cleanup must never replace the operator's prompt result.
            }
        }
    }
}
