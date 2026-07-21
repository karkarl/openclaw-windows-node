using OpenClaw.Shared;
using OpenClawTray.Chat;

namespace OpenClaw.Tray.Tests;

public class ChatComposerSubmissionPolicyTests
{
    [Fact]
    public void ShouldClearInput_OnlyClearsTheSubmittedDraft()
    {
        Assert.True(ChatComposerSubmissionPolicy.ShouldClearInput(3, 3));
        Assert.False(ChatComposerSubmissionPolicy.ShouldClearInput(3, 4));
    }

    [Fact]
    public void RemoveSubmittedAttachments_PreservesAttachmentsAddedWhileSending()
    {
        var submitted = new ChatAttachment { FileName = "submitted.txt" };
        var addedLater = new ChatAttachment { FileName = "later.txt" };

        var remaining = ChatComposerSubmissionPolicy.RemoveSubmittedAttachments(
            [submitted, addedLater],
            [submitted]);

        Assert.Equal([addedLater], remaining);
    }
}
