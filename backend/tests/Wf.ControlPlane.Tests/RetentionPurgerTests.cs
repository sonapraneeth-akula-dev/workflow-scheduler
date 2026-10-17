using Wf.ControlPlane.Engine.Maintenance;

namespace Wf.ControlPlane.Tests;

public sealed class RetentionPurgerTests
{
    [Theory]
    [InlineData(1, 59, false)]
    [InlineData(2, 0, true)]
    [InlineData(3, 59, true)]
    [InlineData(4, 0, false)]
    [InlineData(14, 0, false)]
    public void Automatic_purge_is_limited_to_the_UTC_off_peak_window(int hour, int minute, bool allowed) =>
        Assert.Equal(allowed, RetentionPurger.IsOffPeak(new(2026, 10, 17, hour, minute, 0, TimeSpan.Zero)));

    [Fact]
    public void Off_peak_uses_UTC_not_the_callers_offset() =>
        Assert.True(RetentionPurger.IsOffPeak(new(2026, 10, 17, 7, 30, 0, TimeSpan.FromHours(5.5))));

    [Fact]
    public void Run_batches_are_never_larger_than_five_thousand_and_transactions_fit_the_existing_lease_fence()
    {
        Assert.Equal(5000, RetentionPurger.MaximumBatchSize);
        Assert.Equal(25, RetentionPurger.MaximumTransactionSize);
        Assert.Equal(32, RetentionPurger.MaximumBlobBatchSize);
    }

    [Theory]
    [InlineData(2026, 6, 90, true)]
    [InlineData(2026, 7, 90, false)]
    [InlineData(2026, 8, 90, false)]
    [InlineData(2025, 9, 365, true)]
    [InlineData(2025, 10, 365, false)]
    [InlineData(2026, 6, 180, false)]
    public void Whole_month_drop_respects_the_complete_partition_upper_bound_and_independent_audit_minimum(
        int year, int month, int days, bool expired) =>
        Assert.Equal(expired, PartitionMaintenance.IsExpiredMonth(new(year, month, 1), new(2026, 10, 17, 2, 0, 0, TimeSpan.Zero), days));
}
