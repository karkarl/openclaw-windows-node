using System.IO.Compression;
using System.Text.Json;
using OpenClaw.Connection.NativeGateway;
using OpenClaw.TestSupport;
using OpenClawTray.Services;

namespace OpenClaw.Tray.Tests;

public sealed class FeedbackTests
{
    [Fact]
    public void SubmissionEncodesFeedbackWithoutAllowingQueryInjection()
    {
        var submission = FeedbackSubmission.Create("  Crash & title=#unexpected\nDetails: + % / \u4e2d\u6587  ");
        Assert.False(submission.RequiresPaste);
        Assert.Equal("github.com", submission.IssueUri.Host);
        Assert.Equal("/openclaw/openclaw-windows-node/issues/new", submission.IssueUri.AbsolutePath);
        Assert.Equal("", submission.IssueUri.Fragment);
        var query = submission.IssueUri.Query.TrimStart('?').Split('&');
        Assert.Equal(2, query.Length);
        Assert.Equal("title=Crash & title=#unexpected", Uri.UnescapeDataString(query[0]));
        Assert.Equal("body=Crash & title=#unexpected\nDetails: + % / \u4e2d\u6587", Uri.UnescapeDataString(query[1]));
        Assert.Equal("Crash & title=#unexpected\nDetails: + % / \u4e2d\u6587", submission.Body);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" \r\n\t")]
    public void EmptyFeedbackCannotBeSubmitted(string feedback) =>
        Assert.Throws<ArgumentException>(() => FeedbackSubmission.Create(feedback));

    [Theory]
    [InlineData("x")]
    [InlineData("\u4e2d")]
    public void LongFeedbackUsesClipboardHandoffWithoutTruncation(string character)
    {
        string feedback = string.Concat(Enumerable.Repeat(character, 10_000));
        var submission = FeedbackSubmission.Create(feedback);
        Assert.True(submission.RequiresPaste);
        Assert.Equal(feedback, submission.Body);
        Assert.Equal("https://github.com/openclaw/openclaw-windows-node/issues/new", submission.IssueUri.AbsoluteUri);
    }

    [Fact]
    public void TitleLimitDoesNotSplitAnEmojiOrTruncateTheBody()
    {
        string feedback = new string('a', 99) + "\U0001F980 more details";
        var submission = FeedbackSubmission.Create(feedback);
        var query = submission.IssueUri.Query.TrimStart('?').Split('&');
        Assert.Equal("title=" + new string('a', 99), Uri.UnescapeDataString(query[0]));
        Assert.Equal("body=" + feedback, Uri.UnescapeDataString(query[1]));
    }

    [Fact]
    public void BrowserHandoffChangesOnlyAfterTheUrlLimit()
    {
        const string prefix = "https://github.com/openclaw/openclaw-windows-node/issues/new?title=Title&body=Title%0A";
        string feedback = "Title\n" + new string('a', 7000 - prefix.Length);
        var atLimit = FeedbackSubmission.Create(feedback);
        Assert.Equal(7000, atLimit.IssueUri.AbsoluteUri.Length);
        Assert.False(atLimit.RequiresPaste);
        var overLimit = FeedbackSubmission.Create(feedback + "a");
        Assert.True(overLimit.RequiresPaste);
        Assert.Equal(feedback + "a", overLimit.Body);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CollectorUsesResolvedPackageAndPhysicalOutputPath(bool pinned)
    {
        using var temp = new TempDirectory();
        var resolver = new Resolver(temp.Combine("physical"));
        string? invokedPath = null;
        var client = new NativeGatewayPackageClient((package, args, _) =>
        {
            Assert.Same(resolver.Package, package);
            Assert.Equal(["collect-logs", "--output"], args.Take(2));
            Assert.Equal("--json", args[3]);
            Assert.Equal(resolver.PhysicalDirectory, Path.GetDirectoryName(args[2]));
            invokedPath = args[2];
            using (var archive = ZipFile.Open(args[2], ZipArchiveMode.Create))
            {
                using var writer = new StreamWriter(archive.CreateEntry("diagnostics.txt").Open());
                writer.Write("fixture redacted diagnostics");
            }
            return Task.FromResult(Result(args[2], "host-only", ["session unavailable"]));
        });

        var result = await new FeedbackLogCollector(resolver, client).CollectAsync(
            temp.Combine("logical"), pinned ? resolver.Package.PackageFamilyName : null, CancellationToken.None);

        Assert.Equal(pinned ? resolver.Package.PackageFamilyName : null, resolver.ExpectedFamily);
        Assert.Equal(invokedPath, result.Path);
        Assert.True(result.HostOnly);
        Assert.True(result.HasNotes);
        using var zip = ZipFile.OpenRead(result.Path);
        Assert.Single(zip.Entries);
        using var reader = new StreamReader(zip.Entries[0].Open());
        Assert.Equal("fixture redacted diagnostics", reader.ReadToEnd());
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("empty")]
    [InlineData("invalid")]
    [InlineData("cancelled")]
    public async Task UnusableArchivesAreNotExposedAndOnlyOwnedOutputIsRemoved(string mode)
    {
        using var temp = new TempDirectory();
        var resolver = new Resolver(temp.Path);
        string unrelated = temp.Combine("keep.zip");
        File.WriteAllText(unrelated, "unrelated user file");
        var client = new NativeGatewayPackageClient((_, args, _) =>
        {
            if (mode == "empty")
            {
                using var archive = ZipFile.Open(args[2], ZipArchiveMode.Create);
            }
            else if (mode != "missing")
                File.WriteAllText(args[2], "partial archive");
            if (mode == "cancelled")
                throw new OperationCanceledException();
            return Task.FromResult(Result(args[2]));
        });

        var collector = new FeedbackLogCollector(resolver, client);
        var exception = await Record.ExceptionAsync(() =>
            collector.CollectAsync(temp.Path, null, CancellationToken.None));
        switch (mode)
        {
            case "missing": Assert.IsType<FileNotFoundException>(exception); break;
            case "cancelled": Assert.IsType<OperationCanceledException>(exception); break;
            default: Assert.IsType<InvalidDataException>(exception); break;
        }

        Assert.Equal([unrelated], Directory.GetFiles(temp.Path));
        Assert.Equal("unrelated user file", File.ReadAllText(unrelated));
    }

    private static NativeGatewayCommandResult Result(string path, string included = "host-and-session",
        string[]? notes = null) => new(0, JsonSerializer.Serialize(new
        {
            ok = true, schemaVersion = 1, command = "collect-logs",
            bundle = new { path, included, notes = notes ?? [] }
        }));

    private sealed class Resolver(string directory) : INativeGatewayPackageResolver
    {
        public NativeGatewayPackage Package { get; } = new(
            "OpenClaw.Gateway_123456789abcd", "1.0.0.0", @"C:\fixture\openclaw.exe", @"C:\fixture\clawctl.exe");
        public string PhysicalDirectory { get; } = directory;
        public string? ExpectedFamily { get; private set; }
        public Task<NativeGatewayPackage> ResolveAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Package);
        public Task<NativeGatewayPackage> ResolveAsync(string expectedFamily, CancellationToken cancellationToken)
        {
            ExpectedFamily = expectedFamily;
            return Task.FromResult(Package);
        }
        public string ResolveDataPath(string path) => PhysicalDirectory;
    }
}
