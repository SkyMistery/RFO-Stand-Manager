using System.Text.Json;
using System.Text.Json.Serialization;
using RfoGateManager.Domain;

namespace RfoGateManager.Data;

/// <summary>Attributi operativi di uno stand, che il file .gts non conosce.</summary>
public sealed class StandOverride
{
    public string Id { get; set; } = "";

    /// <summary>Categoria ICAO massima: A, B, C, D, E, F.</summary>
    public string? MaxSize { get; set; }

    public bool? Contact { get; set; }
    public List<string>? Uses { get; set; }
    public List<string>? Airlines { get; set; }

    /// <summary>Stand MARS resi inagibili occupando questo.</summary>
    public List<string>? Blocks { get; set; }

    public int? Priority { get; set; }
    public bool? Disabled { get; set; }
    public string? Note { get; set; }
}

public sealed class StandOverrideFile
{
    public string Airport { get; set; } = "LIRN";

    /// <summary>Categoria applicata agli stand non elencati esplicitamente.</summary>
    public string DefaultMaxSize { get; set; } = "C";

    public List<StandOverride> Stands { get; set; } = [];
}

public sealed record StandCatalogResult
{
    public List<Stand> Stands { get; init; } = [];
    public string? GtsPath { get; init; }
    public string? OverridePath { get; init; }
    public List<string> Warnings { get; init; } = [];
    public bool Loaded => Stands.Count > 0;
}

/// <summary>
/// Costruisce l'elenco stand unendo due sorgenti: il file <c>.gts</c> di Aurora (nomi e
/// coordinate) e un file di attributi operativi (misure, pontili, MARS) che il .gts non ha.
/// </summary>
public static class StandCatalog
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static StandCatalogResult Load(string dataDirectory, string airport)
    {
        var warnings = new List<string>();
        var icao = airport.ToUpperInvariant();

        var gtsPath = FindFile(dataDirectory, [$"{icao}.gts", $"{icao.ToLowerInvariant()}.gts"], "*.gts");
        if (gtsPath is null)
        {
            warnings.Add($"Nessun file .gts trovato in '{dataDirectory}'. Copiaci dentro {icao.ToLowerInvariant()}.gts del sector file.");
            return new StandCatalogResult { Warnings = warnings };
        }

        List<Stand> stands;
        try
        {
            stands = GtsParser.ParseFile(gtsPath, icao);
        }
        catch (Exception ex)
        {
            warnings.Add($"Il file '{gtsPath}' non è leggibile: {ex.Message}");
            return new StandCatalogResult { GtsPath = gtsPath, Warnings = warnings };
        }

        if (stands.Count == 0)
        {
            // Il file potrebbe contenere solo altri aeroporti: riproviamo senza filtro.
            stands = GtsParser.ParseFile(gtsPath);
            if (stands.Count > 0)
                warnings.Add($"Nessuno stand marcato {icao} in '{Path.GetFileName(gtsPath)}': caricati tutti i {stands.Count} stand del file.");
            else
                warnings.Add($"'{Path.GetFileName(gtsPath)}' non contiene stand riconoscibili (atteso 'ID;ICAO;LAT;LON;tipo;').");
        }

        var overridePath = Path.Combine(dataDirectory, $"stands.{icao}.json");
        var overrides = LoadOverrides(overridePath, warnings, out var defaultSize);

        var merged = stands.Select(s => Apply(s, overrides, defaultSize)).ToList();

        var unknown = overrides.Keys
            .Where(k => !merged.Any(s => s.Id.Equals(k, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        if (unknown.Count > 0)
            warnings.Add($"Stand presenti in stands.{icao}.json ma non nel .gts: {string.Join(", ", unknown)}.");

        return new StandCatalogResult
        {
            Stands = merged,
            GtsPath = gtsPath,
            OverridePath = File.Exists(overridePath) ? overridePath : null,
            Warnings = warnings,
        };
    }

    /// <summary>
    /// Scrive un file di attributi precompilato con tutti gli stand trovati, da rifinire a mano.
    /// Serve a non dover scrivere quaranta voci da zero.
    /// </summary>
    public static string WriteTemplate(string dataDirectory, string airport, IReadOnlyList<Stand> stands)
    {
        var icao = airport.ToUpperInvariant();
        var path = Path.Combine(dataDirectory, $"stands.{icao}.json");

        var file = new StandOverrideFile
        {
            Airport = icao,
            DefaultMaxSize = "C",
            Stands = stands.Select(s => new StandOverride
            {
                Id = s.Id,
                MaxSize = "C",
                Contact = false,
                Uses = [],
                Airlines = [],
                Blocks = [],
                Priority = 100,
                Disabled = false,
                Note = s.GtsType,
            }).ToList(),
        };

        Directory.CreateDirectory(dataDirectory);
        File.WriteAllText(path, JsonSerializer.Serialize(file, Options));
        return path;
    }

    private static Dictionary<string, StandOverride> LoadOverrides(
        string path, List<string> warnings, out SizeCategory defaultSize)
    {
        defaultSize = SizeCategory.C;

        if (!File.Exists(path))
        {
            warnings.Add($"'{Path.GetFileName(path)}' non trovato: tutti gli stand valgono categoria C, senza pontili né MARS.");
            return new Dictionary<string, StandOverride>(StringComparer.OrdinalIgnoreCase);
        }

        try
        {
            var file = JsonSerializer.Deserialize<StandOverrideFile>(File.ReadAllText(path), Options)
                       ?? new StandOverrideFile();

            defaultSize = ParseSize(file.DefaultMaxSize) ?? SizeCategory.C;

            var dict = new Dictionary<string, StandOverride>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in file.Stands)
            {
                if (string.IsNullOrWhiteSpace(s.Id)) continue;
                dict[s.Id.Trim()] = s;
            }
            return dict;
        }
        catch (Exception ex)
        {
            warnings.Add($"'{Path.GetFileName(path)}' non è JSON valido ({ex.Message}): attributi ignorati.");
            return new Dictionary<string, StandOverride>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static Stand Apply(Stand s, Dictionary<string, StandOverride> overrides, SizeCategory defaultSize)
    {
        if (!overrides.TryGetValue(s.Id, out var o))
            return s with { MaxSize = defaultSize };

        return s with
        {
            MaxSize = ParseSize(o.MaxSize) ?? defaultSize,
            Contact = o.Contact ?? false,
            Uses = o.Uses ?? [],
            Airlines = o.Airlines ?? [],
            Blocks = o.Blocks ?? [],
            Priority = o.Priority ?? 100,
            Disabled = o.Disabled ?? false,
            Note = o.Note,
        };
    }

    public static SizeCategory? ParseSize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        return Enum.TryParse<SizeCategory>(text.Trim(), ignoreCase: true, out var v) ? v : null;
    }

    private static string? FindFile(string directory, string[] preferred, string fallbackPattern)
    {
        if (!Directory.Exists(directory)) return null;

        foreach (var name in preferred)
        {
            var p = Path.Combine(directory, name);
            if (File.Exists(p)) return p;
        }

        return Directory.EnumerateFiles(directory, fallbackPattern).FirstOrDefault();
    }
}
