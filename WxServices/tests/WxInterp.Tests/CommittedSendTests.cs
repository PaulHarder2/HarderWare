// Unit tests for CommittedSend (WX-78) and the empty ForecastSnapshotBody
// the WX-78 stub builder serializes.  Behavior-level tests of ReportWorker's
// persistence flow require a DI refactor of inline ClaudeClient/SmtpSender
// construction and live in a future ticket.

using MetarParser.Data.Entities;

using Xunit;

namespace WxInterp.Tests;

public class CommittedSendTests
{
    [Fact]
    public void Defaults_match_provisional_row_shape()
    {
        var cs = new CommittedSend();

        Assert.Equal(CommittedSend.SchemaVersionCurrent, cs.SchemaVersion);
        Assert.Null(cs.ReasoningTrace);
        Assert.Null(cs.EmailBody);
        Assert.Null(cs.StructuredReport);
        Assert.Null(cs.SentAtUtc);
        Assert.Equal("", cs.RecipientId);
        Assert.Equal(default, cs.CreatedAtUtc);
        Assert.Equal(0, cs.ForecastSnapshotId);
    }

    [Fact]
    public void Schema_version_constant_is_three()
    {
        // v2 added the StructuredReport column (WX-128); v3 added IsDiagnostic (WX-130).
        Assert.Equal(3, CommittedSend.SchemaVersionCurrent);
    }

    [Fact]
    public void Empty_forecast_snapshot_body_round_trips()
    {
        // The WX-78 stub builder persists an empty ForecastSnapshotBody until
        // WX-77 lands the real GfsSnapshotBuilder.  Guarding the round-trip
        // ensures a future schema-version bump or validation change doesn't
        // silently break the stub path.
        var body = new ForecastSnapshotBody();

        var json = body.Serialize();
        var roundTripped = ForecastSnapshotBody.Deserialize(json);

        Assert.Equal(ForecastSnapshotBody.SchemaVersionCurrent, roundTripped.SchemaVersion);
        Assert.Empty(roundTripped.Blocks);
    }
}