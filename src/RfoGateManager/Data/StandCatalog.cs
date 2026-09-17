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

    /// <summary>Apertura alare massima in metri (AIP). Se c'e', batte la categoria.</summary>
    public double? MaxWingspanM { get; set; }

    /// <summary>Lunghezza fuori tutto massima in metri (AIP).</summary>
    public double? MaxLengthM { get; set; }

    /// <summary>Piazzale di appartenenza, solo informativo.</summary>
    public int? Apron { get; set; }

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

    /// <summary>
    /// Costruisce l'elenco stand. Le due sorgenti sono indipendenti: il file di attributi
    /// basta da solo a far funzionare l'allocazione, il .gts aggiunge le coordinate (che
    /// servono solo per disegnare il piazzale). Se manca uno dei due si prosegue con l'altro.
    /// </summary>
    public static StandCatalogResult Load(string dataDirectory, string airport)
    {
        var warnings = new List<string>();
        var icao = airport.ToUpperInvariant();

        var overridePath = Path.Combine(dataDirectory, $"stands.{icao}.json");
        var overrides = LoadOverrides(overridePath, warnings, out var defaultSize);

        var gtsPath = FindFile(dataDirectory, [$"{icao}.gts", $"{icao.ToLowerInvariant()}.gts"], "*.gts");
        var fromGts = ReadGts(gtsPath, icao, warnings);

        // Gli stand dichiarati negli attributi ma assenti dal .gts restano utilizzabili:
        // senza coordinate non compaiono sulla mappa, ma si possono assegnare lo stesso.
        var known = fromGts.Select(s => s.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var extra = overrides.Values
            .Where(o => !known.Contains(o.Id))
            .Select(o => new Stand { Id = o.Id, Icao = icao })
            .ToList();

        if (fromGts.Count == 0 && extra.Count > 0)
            warnings.Add($"Stand presi da stands.{icao}.json: senza il .gts mancano solo le coordinate per la mappa.");
        else if (extra.Count > 0)
            warnings.Add($"Stand presenti in stands.{icao}.json ma non nel .gts: {string.Join(", ", extra.Select(e => e.Id))}.");

        var merged = fromGts.Concat(extra)
            .Select(s => Apply(s, overrides, defaultSize))
            .ToList();

        if (merged.Count == 0)
            warnings.Add($"Nessuno stand disponibile: serve almeno stands.{icao}.json oppure {icao.ToLowerInvariant()}.gts in '{dataDirectory}'.");

        return new StandCatalogResult
        {
            Stands = merged,
            GtsPath = gtsPath,
            OverridePath = File.Exists(overridePath) ? overridePath : null,
            Warnings = warnings,
        };
    }

    private static List<Stand> ReadGts(string? gtsPath, string icao, List<string> warnings)
    {
        if (gtsPath is null)
        {
            warnings.Add($"Nessun file .gts in cartella: gli stand non avranno coordinate. Copiaci {icao.ToLowerInvariant()}.gts del sector file.");
            return [];
        }

        try
        {
            var stands = GtsParser.ParseFile(gtsPath, icao);
            if (stands.Count > 0) return stands;

            // Il file potrebbe contenere solo altri aeroporti: riproviamo senza filtro.
            stands = GtsParser.ParseFile(gtsPath);
            if (stands.Count > 0)
                warnings.Add($"Nessuno stand marcato {icao} in '{Path.GetFileName(gtsPath)}': caricati tutti i {stands.Count} stand del file.");
            else
                warnings.Add($"'{Path.GetFileName(gtsPath)}' non contiene stand riconoscibili (atteso 'ID;ICAO;LAT;LON;tipo;').");

            return stands;
        }
        catch (Exception ex)
        {
            warnings.Add($"Il file '{gtsPath}' non è leggibile: {ex.Message}");
            return [];
        }
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
                MaxSize = s.MaxSize.ToString(),
                MaxWingspanM = s.MaxWingspanM,
                MaxLengthM = s.MaxLengthM,
                Contact = s.Contact,
                Uses = s.Uses.ToList(),
                Airlines = s.Airlines.ToList(),
                Blocks = s.Blocks.ToList(),
                Priority = s.Priority,
                Disabled = s.Disabled,
                Note = s.Note ?? s.GtsType,
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
            warnings.Add($"'{Path.GetFileName(path)}' non trovato: nessuna misura, nessun pontile, nessun MARS. Genera il file con POST /api/stands/template.");
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
            MaxWingspanM = o.MaxWingspanM,
            MaxLengthM = o.MaxLengthM,
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
