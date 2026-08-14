using EdiToSscs.Core;

namespace EdiToSscs.Tests;

public class QueueRepaintPolicyTests
{
    [Fact]
    public void EveryQueueListRepaintsAfterEachModalPromptInAMultiFileBatch()
    {
        int ediRepaints = 0;
        int pdfRepaints = 0;

        // Two files each close an operator prompt while the same queue batch remains active.
        for (int file = 0; file < 2; file++)
            QueueRepaintPolicy.AfterPrompt(() => ediRepaints++, () => pdfRepaints++);

        Assert.Equal(2, ediRepaints);
        Assert.Equal(2, pdfRepaints);
    }

    [Fact]
    public void OneDisposedListCannotPreventTheOtherQueueFromRepainting()
    {
        int liveRepaints = 0;

        QueueRepaintPolicy.AfterPrompt(
            () => throw new ObjectDisposedException("stale list"),
            () => liveRepaints++);

        Assert.Equal(1, liveRepaints);
    }
}
