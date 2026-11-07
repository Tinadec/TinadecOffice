using System.Text.Json;

namespace TinadecTools.Tools.Desktop;

/// <summary>
/// One measured row of the coordinate-compensation profile: for a specific model speaking a
/// specific protocol, how the coordinates that model REPORTS relate to the image it was
/// SHOWN, across a measured range of image shapes. The scales are multiplicative factors
/// from reported coordinates to shown-image pixels, measured offline with the clickloop
/// harness (real screenshots, real clicks, application state as the judge).
///
/// A row is data, not a guess: every bound marks a shape that was actually measured. A
/// lookup outside every row's bounds fails closed instead of extrapolating — a wrong scale
/// moves a click by a quarter screen without any error, which is worse than refusing.
/// </summary>
internal sealed class CoordinateProfileRow
{
    public string Model { get; init; } = "";
    public string Protocol { get; init; } = "";
    public double AspectMin { get; init; }
    public double AspectMax { get; init; }
    public double ShortEdgeMin { get; init; }
    public double ShortEdgeMax { get; init; }
    public double ScaleX { get; init; }
    public double ScaleY { get; init; }
    public int Samples { get; init; }
    public double HitRate { get; init; }
    public string? Tested { get; init; }

    public bool Covers(double aspect, int shortEdge) =>
        aspect >= AspectMin && aspect <= AspectMax && shortEdge >= ShortEdgeMin && shortEdge <= ShortEdgeMax;

    public override string ToString() =>
        $"{Model}/{Protocol} aspect {AspectMin:0.##}–{AspectMax:0.##}, short edge {ShortEdgeMin:0}–{ShortEdgeMax:0} "
        + $"(scale x{ScaleX:0.###}/y{ScaleY:0.###}, {Samples} samples, hit rate {HitRate:0%}, tested {Tested ?? "unknown"})";
}

/// <summary>
/// Reads TINADEC_HOME/config/coordinate-profiles.json. Malformed files are configuration
/// errors, never silently ignored: compensation enabled with a broken profile store must
/// refuse clicks, not click with made-up numbers.
/// </summary>
internal sealed class CoordinateProfileStore
{
    public string Path { get; }
    private readonly List<CoordinateProfileRow> _rows;

    private CoordinateProfileStore(string path, List<CoordinateProfileRow> rows)
    {
        Path = path;
        _rows = rows;
    }

    public IReadOnlyList<CoordinateProfileRow> Rows => _rows;

    /// <exception cref="DesktopToolRuntime.DesktopConfigurationException">the file is missing, unreadable, or malformed.</exception>
    public static CoordinateProfileStore Load(string? home)
    {
        if (string.IsNullOrWhiteSpace(home))
            throw new DesktopToolRuntime.DesktopConfigurationException(
                "desktop_config_invalid: coordinate compensation is enabled but TINADEC_HOME is not set; the profile store lives at TINADEC_HOME/config/coordinate-profiles.json.");
        var path = System.IO.Path.Combine(home, "config", "coordinate-profiles.json");
        if (!File.Exists(path))
            throw new DesktopToolRuntime.DesktopConfigurationException(
                $"desktop_config_invalid: coordinate compensation is enabled but {path} does not exist. Measure the model with the clickloop harness and create it (schema_version 1, profiles[]).");
        List<CoordinateProfileRow> rows;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            rows = Parse(document.RootElement, path);
        }
        catch (JsonException exception)
        {
            throw new DesktopToolRuntime.DesktopConfigurationException(
                $"desktop_config_invalid: {path} is not valid JSON ({exception.Message}).");
        }
        return new CoordinateProfileStore(path, rows);
    }

    private static List<CoordinateProfileRow> Parse(JsonElement root, string path)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("schema_version", out var version) || !version.TryGetInt32(out var parsed) || parsed != 1
            || !root.TryGetProperty("profiles", out var profiles) || profiles.ValueKind != JsonValueKind.Array)
            throw new DesktopToolRuntime.DesktopConfigurationException(
                $"desktop_config_invalid: {path} must be an object {{ \"schema_version\": 1, \"profiles\": [ ... ] }}.");
        var rows = new List<CoordinateProfileRow>();
        foreach (var node in profiles.EnumerateArray())
        {
            if (node.ValueKind != JsonValueKind.Object)
                throw new DesktopToolRuntime.DesktopConfigurationException($"desktop_config_invalid: every entry in {path} profiles[] must be an object.");
            var row = new CoordinateProfileRow
            {
                Model = Text(node, "model"),
                Protocol = Text(node, "protocol"),
                AspectMin = Number(node, "aspect_min"),
                AspectMax = Number(node, "aspect_max"),
                ShortEdgeMin = Number(node, "short_edge_min"),
                ShortEdgeMax = Number(node, "short_edge_max"),
                ScaleX = Number(node, "scale_x"),
                ScaleY = Number(node, "scale_y"),
                Samples = (int)Number(node, "samples"),
                HitRate = node.TryGetProperty("hit_rate", out var hitRate) && hitRate.ValueKind == JsonValueKind.Number ? hitRate.GetDouble() : 0,
                Tested = node.TryGetProperty("tested", out var tested) && tested.ValueKind == JsonValueKind.String ? tested.GetString() : null,
            };
            if (row.AspectMin <= 0 || row.AspectMax <= row.AspectMin
                || row.ShortEdgeMin <= 0 || row.ShortEdgeMax <= row.ShortEdgeMin
                || row.ScaleX is < 0.25 or > 16 || row.ScaleY is < 0.25 or > 16
                || row.Samples < 1 || row.HitRate is < 0 or > 1)
                throw new DesktopToolRuntime.DesktopConfigurationException(
                    $"desktop_config_invalid: profile row for {row.Model}/{row.Protocol} in {path} has out-of-range bounds or scales (aspect/short-edge min must be below max; scales within 0.25–16; samples ≥ 1; hit_rate 0–1).");
            rows.Add(row);
        }
        if (rows.Count == 0)
            throw new DesktopToolRuntime.DesktopConfigurationException(
                $"desktop_config_invalid: {path} contains no profile rows; an enabled compensation has nothing to compensate with.");
        return rows;
    }

    private static string Text(JsonElement node, string name) =>
        node.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!.Trim()
            : throw new DesktopToolRuntime.DesktopConfigurationException($"desktop_config_invalid: profile rows require a non-empty string '{name}'.");

    private static double Number(JsonElement node, string name) =>
        node.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble()
            : throw new DesktopToolRuntime.DesktopConfigurationException($"desktop_config_invalid: profile rows require a number '{name}'.");

    /// <summary>First row (file order) that matches the model, protocol and image shape.</summary>
    public CoordinateProfileRow? Find(string model, string protocol, double aspect, int shortEdge) =>
        _rows.FirstOrDefault(row =>
            string.Equals(row.Model, model, StringComparison.OrdinalIgnoreCase)
            && string.Equals(row.Protocol, protocol, StringComparison.OrdinalIgnoreCase)
            && row.Covers(aspect, shortEdge));

    /// <summary>
    /// For the refusal message: what this store does cover for the requested model, so the
    /// operator knows which row to measure instead of guessing at the schema.
    /// </summary>
    public string DescribeCoverage(string model, string protocol)
    {
        var forModel = _rows.Where(row =>
            string.Equals(row.Model, model, StringComparison.OrdinalIgnoreCase)
            && string.Equals(row.Protocol, protocol, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (forModel.Length == 0)
        {
            var known = string.Join("; ", _rows.Select(row => $"{row.Model}/{row.Protocol}").Distinct());
            return $"No profile row exists for that model/protocol at all (known: {known}).";
        }
        return $"Measured rows for {model}/{protocol} cover: {string.Join("; ", forModel.Select(row => row.ToString()))}.";
    }
}
