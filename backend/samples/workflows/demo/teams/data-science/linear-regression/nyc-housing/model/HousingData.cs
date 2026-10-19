using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HousingSample;

public sealed record House(string PropertyId, double SquareFeet, double Price);
public sealed record Dataset(string Directory, bool Reused, DateTimeOffset DownloadedAt,
    string Source, int TrainingRows, int ValidationRows, int TestRows);
public sealed record Model(double Intercept, double Slope, double AreaMean, double AreaScale, double TrainingMean)
{
    public double Predict(double squareFeet) => Intercept + Slope * ((squareFeet - AreaMean) / AreaScale);
}
public sealed record LearningPoint(int Epoch, double TrainingRmse, double ValidationRmse);
public sealed record Fit(Model Model, LearningPoint[] Learning);
public sealed record Metrics(int Rows, double? R2, double Mae, double Rmse, double BaselineMae, double BaselineRmse);

[JsonSerializable(typeof(Dataset))]
[JsonSerializable(typeof(Model))]
[JsonSerializable(typeof(Metrics))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
public partial class HousingJsonContext : JsonSerializerContext;

public static class HousingData
{
    private const string Fields = "borough,block,lot,gross_square_feet,sale_price,sale_date";
    private const string Filter = "tax_class_at_time_of_sale=1 AND gross_square_feet BETWEEN 500 AND 5000 AND sale_price BETWEEN 100000 AND 3000000";
    public static readonly string Source = "https://data.cityofnewyork.us/resource/usep-8jbt.json?$select=" +
        Fields + "&$where=" + Uri.EscapeDataString(Filter) +
        "&$order=borough,block,lot,sale_date,sale_price&$limit=50000";
    private static readonly string[] DataFiles = ["source.json", "training.csv", "validation.csv", "test.csv"];
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private sealed record Manifest(int Format, string Source, DateTimeOffset DownloadedAt, Dictionary<string, string> Sha256);

    public static string CacheDirectory()
    {
        var root = Environment.GetEnvironmentVariable("WF_DATASET_ROOT") ??
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "wf", "datasets");
        if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root))
            throw new InvalidDataException("WF_DATASET_ROOT must be an absolute dataset directory.");
        return Path.Combine(root, "nyc-housing-v1");
    }

    public static async Task<Dataset> EnsureAsync(string directory, HttpClient http, Action<string> log, CancellationToken token)
    {
        Directory.CreateDirectory(directory);
        await using var cacheLock = await LockAsync(Path.Combine(directory, "cache.lock"), token);
        var manifestPath = Path.Combine(directory, "manifest.json");
        if (File.Exists(manifestPath))
        {
            var manifest = JsonSerializer.Deserialize<Manifest>(await File.ReadAllBytesAsync(manifestPath, token), Json)
                ?? throw new InvalidDataException("Dataset cache manifest is empty.");
            if (manifest.Format != 1 || manifest.Source != Source || manifest.Sha256.Count != DataFiles.Length)
                throw new InvalidDataException("Dataset cache manifest does not match this workflow.");
            foreach (var name in DataFiles)
                if (!manifest.Sha256.TryGetValue(name, out var expected) ||
                    !File.Exists(Path.Combine(directory, name)) ||
                    Hash(await File.ReadAllBytesAsync(Path.Combine(directory, name), token)) != expected)
                    throw new InvalidDataException($"Dataset cache verification failed for {name}. Remove only this dataset cache to download again.");
            log("CACHE HIT: verified source, training, validation and test files; no HTTP request.");
            return Describe(directory, true, manifest);
        }

        log("CACHE MISS: downloading official NYC residential sales and preparing fixed 70/15/15 splits.");
        using var response = await http.GetAsync(Source, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        await response.Content.LoadIntoBufferAsync(16 * 1024 * 1024, token);
        var bytes = await response.Content.ReadAsByteArrayAsync(token);
        var splits = Split(ParseSource(bytes));
        var contents = new Dictionary<string, byte[]>
        {
            ["source.json"] = bytes,
            ["training.csv"] = Encoding.UTF8.GetBytes(Csv(splits.Training)),
            ["validation.csv"] = Encoding.UTF8.GetBytes(Csv(splits.Validation)),
            ["test.csv"] = Encoding.UTF8.GetBytes(Csv(splits.Test))
        };
        var created = new Manifest(1, Source, DateTimeOffset.UtcNow,
            contents.ToDictionary(item => item.Key, item => Hash(item.Value)));
        contents["manifest.json"] = JsonSerializer.SerializeToUtf8Bytes(created, Json);
        var suffix = "." + Guid.NewGuid().ToString("N") + ".partial";
        try
        {
            foreach (var (name, content) in contents)
                await File.WriteAllBytesAsync(Path.Combine(directory, name + suffix), content, token);
            // Publish the manifest last: interrupted downloads never become valid cache hits.
            foreach (var name in DataFiles.Append("manifest.json"))
                File.Move(Path.Combine(directory, name + suffix), Path.Combine(directory, name), overwrite: true);
        }
        finally
        {
            foreach (var name in contents.Keys)
                File.Delete(Path.Combine(directory, name + suffix));
        }
        log($"Downloaded {splits.Training.Length + splits.Validation.Length + splits.Test.Length} unique properties; dataset cache committed.");
        return Describe(directory, false, created);
    }

    private static async Task<FileStream> LockAsync(string path, CancellationToken token)
    {
        var started = Environment.TickCount64;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (Environment.TickCount64 - started < 120_000)
            { await Task.Delay(250, token); }
        }
    }

    private static Dataset Describe(string directory, bool reused, Manifest manifest)
    {
        var training = Read(directory, "training");
        var validation = Read(directory, "validation");
        var test = Read(directory, "test");
        if (training.Concat(validation).Concat(test).Select(row => row.PropertyId).Distinct().Count() !=
            training.Length + validation.Length + test.Length)
            throw new InvalidDataException("Dataset splits overlap or contain duplicate properties.");
        return new(directory, reused, manifest.DownloadedAt, manifest.Source, training.Length, validation.Length, test.Length);
    }

    public static House[] ParseSource(byte[] bytes)
    {
        using var document = JsonDocument.Parse(bytes);
        if (document.RootElement.ValueKind != JsonValueKind.Array ||
            document.RootElement.GetArrayLength() >= 50_000)
            throw new InvalidDataException("Expected a bounded NYC sales array; a full page could be truncated.");
        var latest = new Dictionary<string, (string Date, House House)>(StringComparer.Ordinal);
        foreach (var row in document.RootElement.EnumerateArray())
        {
            var borough = Text(row, "borough");
            var block = Text(row, "block");
            var lot = Text(row, "lot");
            var date = Text(row, "sale_date");
            if (!int.TryParse(borough, out var boroughNumber) || boroughNumber is < 1 or > 5 ||
                !int.TryParse(block, out _) || !int.TryParse(lot, out _) ||
                !DateTime.TryParse(date, CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                throw new InvalidDataException("NYC property identifier or sale date is invalid.");
            var area = Number(row, "gross_square_feet");
            var price = Number(row, "sale_price");
            if (area is < 500 or > 5000 || price is < 100_000 or > 3_000_000)
                throw new InvalidDataException("NYC response contains rows outside the declared residential filters.");
            var key = $"{boroughNumber}-{int.Parse(block, CultureInfo.InvariantCulture)}-{int.Parse(lot, CultureInfo.InvariantCulture)}";
            if (!latest.TryGetValue(key, out var previous) || string.CompareOrdinal(date, previous.Date) > 0)
                latest[key] = (date, new(key, area, price));
        }
        if (latest.Count < 100)
            throw new InvalidDataException("At least 100 unique residential properties are required.");
        return latest.Values.Select(item => item.House).ToArray();
    }

    private static string Text(JsonElement row, string key) =>
        row.TryGetProperty(key, out var value) && value.ValueKind is JsonValueKind.String or JsonValueKind.Number
            ? value.ValueKind == JsonValueKind.String ? value.GetString()! : value.GetRawText()
            : throw new InvalidDataException($"NYC row is missing {key}.");

    private static double Number(JsonElement row, string key) =>
        double.TryParse(Text(row, key), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number)
            ? number : throw new InvalidDataException($"NYC row has an invalid {key}.");

    public static (House[] Training, House[] Validation, House[] Test) Split(House[] rows)
    {
        if (rows.Length < 100 || rows.Select(row => row.PropertyId).Distinct().Count() != rows.Length)
            throw new InvalidDataException("Splitting requires at least 100 unique properties.");
        var ordered = rows.OrderBy(row => Hash(Encoding.UTF8.GetBytes(row.PropertyId)), StringComparer.Ordinal).ToArray();
        var trainEnd = ordered.Length * 70 / 100;
        var validationEnd = ordered.Length * 85 / 100;
        return (ordered[..trainEnd], ordered[trainEnd..validationEnd], ordered[validationEnd..]);
    }

    private static string Csv(IEnumerable<House> rows) => "property_id,square_feet,price_usd\n" +
        string.Join("\n", rows.Select(row => FormattableString.Invariant($"{row.PropertyId},{row.SquareFeet:R},{row.Price:R}"))) + "\n";

    public static House[] Read(string directory, string split)
    {
        if (split is not ("training" or "validation" or "test"))
            throw new ArgumentException("Unknown dataset split.", nameof(split));
        var lines = File.ReadAllLines(Path.Combine(directory, split + ".csv"));
        if (lines.Length < 3 || lines[0] != "property_id,square_feet,price_usd")
            throw new InvalidDataException($"Invalid {split} dataset header or empty split.");
        return lines.Skip(1).Select(line =>
        {
            var fields = line.Split(',');
            if (fields.Length != 3 || string.IsNullOrWhiteSpace(fields[0]) ||
                !double.TryParse(fields[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var area) ||
                !double.TryParse(fields[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var price) ||
                !double.IsFinite(area) || !double.IsFinite(price) || area is < 500 or > 5000 || price is < 100_000 or > 3_000_000)
                throw new InvalidDataException($"Invalid {split} dataset row.");
            return new House(fields[0], area, price);
        }).ToArray();
    }

    public static Fit Train(House[] training, House[] validation, CancellationToken token = default)
    {
        if (training.Length < 2 || validation.Length < 2)
            throw new InvalidDataException("Training and validation each need at least two rows.");
        var mean = training.Average(row => row.SquareFeet);
        var scale = Math.Sqrt(training.Average(row => Math.Pow(row.SquareFeet - mean, 2)));
        if (!double.IsFinite(scale) || scale <= 0)
            throw new InvalidDataException("Training areas must have finite, nonzero variance.");
        var intercept = training.Average(row => row.Price);
        var slope = 0d;
        var history = new List<LearningPoint>();
        for (var epoch = 0; epoch <= 100; epoch++)
        {
            token.ThrowIfCancellationRequested();
            var model = new Model(intercept, slope, mean, scale, training.Average(row => row.Price));
            history.Add(new(epoch, Evaluate(model, training).Rmse, Evaluate(model, validation).Rmse));
            if (epoch == 100) return new(model, history.ToArray());
            var errors = training.Select(row => (X: (row.SquareFeet - mean) / scale, Error: model.Predict(row.SquareFeet) - row.Price)).ToArray();
            intercept -= 0.1 * errors.Average(row => row.Error);
            slope -= 0.1 * errors.Average(row => row.Error * row.X);
        }
        throw new InvalidOperationException("Training did not produce a model.");
    }

    public static Metrics Evaluate(Model model, House[] rows)
    {
        if (rows.Length < 2) throw new InvalidDataException("Evaluation requires at least two rows.");
        var mean = rows.Average(row => row.Price);
        var squared = rows.Sum(row => Math.Pow(model.Predict(row.SquareFeet) - row.Price, 2));
        var total = rows.Sum(row => Math.Pow(row.Price - mean, 2));
        var result = new Metrics(rows.Length, total > 0 ? 1 - squared / total : null,
            rows.Average(row => Math.Abs(model.Predict(row.SquareFeet) - row.Price)), Math.Sqrt(squared / rows.Length),
            rows.Average(row => Math.Abs(model.TrainingMean - row.Price)),
            Math.Sqrt(rows.Average(row => Math.Pow(model.TrainingMean - row.Price, 2))));
        if (!double.IsFinite(result.Rmse) || !double.IsFinite(result.Mae) ||
            !double.IsFinite(result.BaselineRmse) || !double.IsFinite(result.BaselineMae) ||
            result.R2 is { } r2 && !double.IsFinite(r2))
            throw new InvalidDataException("Model evaluation produced non-finite metrics.");
        return result;
    }

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
