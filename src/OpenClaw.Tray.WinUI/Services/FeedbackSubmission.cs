namespace OpenClawTray.Services;

internal sealed record FeedbackSubmission(Uri IssueUri, string Body, bool RequiresPaste)
{
    private const string NewIssueUrl = "https://github.com/openclaw/openclaw-windows-node/issues/new";
    private const int MaximumPrefilledUrlLength = 7000;
    private const int MaximumTitleLength = 100;

    public static FeedbackSubmission Create(string feedback)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(feedback);
        string body = feedback.Trim();
        string title = body.Split(['\r', '\n'], 2)[0];
        if (title.Length > MaximumTitleLength)
        {
            int length = char.IsHighSurrogate(title[MaximumTitleLength - 1])
                ? MaximumTitleLength - 1 : MaximumTitleLength;
            title = title[..length];
        }
        string url = $"{NewIssueUrl}?title={Uri.EscapeDataString(title)}&body={Uri.EscapeDataString(body)}";
        return url.Length <= MaximumPrefilledUrlLength
            ? new(new Uri(url), body, false)
            : new(new Uri(NewIssueUrl), body, true);
    }
}
