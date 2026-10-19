using System.Net;
using System.Text;
using System.Text.Json;
using HousingSample;

namespace Wf.Sdk.Tests;

public sealed class HousingSampleTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "wf-housing-test-" + Guid.NewGuid().ToString("N"));
    private static House[] Rows => Enumerable.Range(1, 200)
        .Select(index => new House($"1-{index}-1", 500 + index * 10, 150_000 + index * 3000)).ToArray();

    [Fact]
    public void Custom_outputs_round_trip_through_the_sdk_source_generated_contract()
    {
        var outputs = new NodeOutputs("dataset");
        var data = new Dataset(directory, false, DateTimeOffset.UtcNow, HousingData.Source, 140, 30, 30);
        outputs.Set("dataset", data, HousingJsonContext.Default.Dataset);
        Assert.Equal(data, outputs.Get("dataset", HousingJsonContext.Default.Dataset));
        var model = HousingData.Train(Rows, Rows, TestContext.Current.CancellationToken).Model;
        outputs.Set("model", model, HousingJsonContext.Default.Model);
        Assert.Equal(model, outputs.Get("model", HousingJsonContext.Default.Model));
        var metrics = HousingData.Evaluate(model, Rows);
        outputs.Set("metrics", metrics, HousingJsonContext.Default.Metrics);
        Assert.Equal(metrics, outputs.Get("metrics", HousingJsonContext.Default.Metrics));
    }

    [Fact]
    public void Splits_are_reproducible_disjoint_and_exactly_70_15_15()
    {
        var first = HousingData.Split(Rows);
        var second = HousingData.Split(Rows.Reverse().ToArray());
        Assert.Equal(140, first.Training.Length);
        Assert.Equal(30, first.Validation.Length);
        Assert.Equal(30, first.Test.Length);
        Assert.Equal(first.Training, second.Training);
        Assert.Equal(first.Validation, second.Validation);
        Assert.Equal(first.Test, second.Test);
        Assert.Equal(200, first.Training.Concat(first.Validation).Concat(first.Test).Select(row => row.PropertyId).Distinct().Count());
    }

    [Fact]
    public void Gradient_descent_learns_a_known_line_without_validation_leakage()
    {
        var split = HousingData.Split(Rows);
        var fit = HousingData.Train(split.Training, split.Validation, TestContext.Current.CancellationToken);
        var metrics = HousingData.Evaluate(fit.Model, split.Test);
        Assert.Equal(101, fit.Learning.Length);
        Assert.True(fit.Learning[^1].TrainingRmse < fit.Learning[0].TrainingRmse / 1000);
        Assert.InRange(metrics.R2!.Value, 0.999, 1);
        Assert.InRange(metrics.Mae, 0, 10);
        Assert.True(metrics.Rmse < metrics.BaselineRmse);
        Assert.Equal(split.Training.Average(row => row.SquareFeet), fit.Model.AreaMean);
        var differentValidation = split.Validation.Select(row => row with { Price = row.Price + 1_000_000 }).ToArray();
        Assert.Equal(fit.Model, HousingData.Train(split.Training, differentValidation, TestContext.Current.CancellationToken).Model);
    }

    [Fact]
    public void Regression_reports_undefined_r2_for_constant_targets_and_rejects_constant_features()
    {
        var model = new Model(200_000, 0, 1000, 1, 200_000);
        Assert.Null(HousingData.Evaluate(model, Rows.Select(row => row with { Price = 200_000 }).ToArray()).R2);
        Assert.Throws<InvalidDataException>(() => HousingData.Train(Rows.Select(row => row with { SquareFeet = 1000 }).ToArray(), Rows, TestContext.Current.CancellationToken));
        Assert.Throws<InvalidDataException>(() => HousingData.Split([Rows[0], Rows[0]]));
        Assert.Throws<InvalidDataException>(() => HousingData.Evaluate(model with { Slope = double.NaN }, Rows));
    }

    [Fact]
    public async Task Cache_hit_verifies_all_files_without_any_network_request_and_survives_a_new_client()
    {
        var handler = new SourceHandler();
        using var http = new HttpClient(handler);
        Assert.Null(await HousingData.TryLoadAsync(directory, _ => { }, TestContext.Current.CancellationToken));
        var first = await HousingData.EnsureAsync(directory, http, _ => { }, TestContext.Current.CancellationToken);
        var before = Directory.GetFiles(directory).Where(path => Path.GetFileName(path) != "cache.lock")
            .ToDictionary(path => Path.GetFileName(path)!, File.GetLastWriteTimeUtc);
        using var offline = new HttpClient(new SourceHandler { Offline = true });
        var messages = new List<string>();
        var cached = await HousingData.EnsureAsync(directory, offline, messages.Add, TestContext.Current.CancellationToken);
        Assert.False(first.Reused);
        Assert.True(cached.Reused);
        Assert.Equal(1, handler.Requests);
        Assert.Equal(first.DownloadedAt, cached.DownloadedAt);
        Assert.Equal(140, cached.TrainingRows);
        Assert.Equal(30, cached.ValidationRows);
        Assert.Equal(30, cached.TestRows);
        Assert.Equal(cached, await HousingData.TryLoadAsync(directory, messages.Add, TestContext.Current.CancellationToken));
        Assert.Contains(messages, message => message.Contains("CACHE HIT", StringComparison.Ordinal));
        foreach (var (file, modified) in before)
            Assert.Equal(modified, File.GetLastWriteTimeUtc(Path.Combine(directory, file!)));
        await File.AppendAllTextAsync(Path.Combine(directory, "test.csv"), "\ncorrupt", TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            HousingData.EnsureAsync(directory, offline, messages.Add, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            HousingData.TryLoadAsync(directory, messages.Add, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Concurrent_cache_misses_download_only_once()
    {
        var handler = new SourceHandler();
        using var http = new HttpClient(handler);
        var results = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ =>
            HousingData.EnsureAsync(directory, http, _ => { }, TestContext.Current.CancellationToken)));
        Assert.Equal(1, handler.Requests);
        Assert.Single(results, result => !result.Reused);
        Assert.Equal(2, results.Count(result => result.Reused));
    }

    [Fact]
    public async Task Http_failure_leaves_no_valid_manifest_and_can_retry()
    {
        using var offline = new HttpClient(new SourceHandler { Offline = true });
        await Assert.ThrowsAsync<HttpRequestException>(() =>
            HousingData.EnsureAsync(directory, offline, _ => { }, TestContext.Current.CancellationToken));
        Assert.False(File.Exists(Path.Combine(directory, "manifest.json")));
        using var online = new HttpClient(new SourceHandler());
        Assert.False((await HousingData.EnsureAsync(directory, online, _ => { }, TestContext.Current.CancellationToken)).Reused);
        Assert.Empty(Directory.GetFiles(directory, "*.partial"));
    }

    [Fact]
    public void Parsing_deduplicates_properties_and_rejects_invalid_source()
    {
        var source = SourceRows().ToList();
        source.Add(new { borough = "1", block = "1", lot = "1", gross_square_feet = "2500", sale_price = "800000", sale_date = "2026-02-01T00:00:00" });
        var rows = HousingData.ParseSource(JsonSerializer.SerializeToUtf8Bytes(source));
        Assert.Equal(200, rows.Length);
        Assert.Equal(800_000, Assert.Single(rows, row => row.PropertyId == "1-1-1").Price);
        Assert.Throws<InvalidDataException>(() => HousingData.ParseSource(Encoding.UTF8.GetBytes("{}")));
        Assert.Throws<InvalidDataException>(() => HousingData.ParseSource(Encoding.UTF8.GetBytes("[{}]")));
        Assert.Throws<InvalidDataException>(() => HousingData.ParseSource(Encoding.UTF8.GetBytes("[]")));
    }

    private static IEnumerable<object> SourceRows() => Rows.Select(row => (object)new
    {
        borough = "1",
        block = row.PropertyId.Split('-')[1],
        lot = "1",
        gross_square_feet = row.SquareFeet.ToString(System.Globalization.CultureInfo.InvariantCulture),
        sale_price = row.Price.ToString(System.Globalization.CultureInfo.InvariantCulture),
        sale_date = "2026-01-01T00:00:00"
    });

    private sealed class SourceHandler : HttpMessageHandler
    {
        public int Requests;
        public bool Offline { get; init; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Interlocked.Increment(ref Requests);
            if (Offline) throw new HttpRequestException("Offline cache test.");
            Assert.Equal(HousingData.Source, request.RequestUri!.OriginalString);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(SourceRows())) });
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }
}
