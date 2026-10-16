namespace Wf.Agent.Tests;

public sealed class PhysicalHostUnsupportedPrimitiveTests
{
    [Fact]
    public async Task Unavailable_native_primitive_quarantines_before_any_work_is_admitted()
    {
        if (Environment.GetEnvironmentVariable("WF_PHYSICAL_EXPECT_UNSUPPORTED") != "true") return;
        var root = PhysicalHostLifetimeTests.Root();
        var pool = PhysicalHostLifetimeTests.Pool(root);
        try
        {
            await Assert.ThrowsAnyAsync<Exception>(() => pool.PrewarmAsync(PhysicalHostLifetimeTests.Package,
                TestContext.Current.CancellationToken));
            Assert.Equal(1, pool.ProcessStartCount);
            await Assert.ThrowsAsync<InvalidOperationException>(() => pool.FenceAsync(TestContext.Current.CancellationToken));
            await Assert.ThrowsAsync<InvalidOperationException>(() => pool.PrewarmAsync(PhysicalHostLifetimeTests.Package,
                TestContext.Current.CancellationToken));
            Assert.Single(Directory.GetFiles(Path.Combine(root, "recovery"), "*.pending"));
        }
        finally
        {
            await Assert.ThrowsAnyAsync<Exception>(async () => await pool.DisposeAsync());
            Directory.Delete(root, recursive: true);
        }
    }
}
