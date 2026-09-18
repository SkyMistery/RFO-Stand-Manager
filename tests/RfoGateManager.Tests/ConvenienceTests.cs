using RfoGateManager.Domain;
using Xunit;

namespace RfoGateManager.Tests;

/// <summary>
/// L'ordine di comodità di Napoli: stand 10-23, poi 51-57, poi 41-46 (tutti apron 1), poi
/// apron 2, poi apron 3. Il gruppo comanda; dentro il gruppo vince chi spreca meno spazio.
/// </summary>
public class ConvenienceTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 19, 10, 0, 0, TimeSpan.Zero);
    private static readonly AllocationOptions Opt = new();

    // Misure vere dall'AIP.
    private static Stand S(string id, double span, double length, int priority, int apron) => new()
    {
        Id = id, Icao = "LIRN", MaxSize = SizeCategory.C,
        MaxWingspanM = span, MaxLengthM = length, Priority = priority, Apron = apron,
    };

    private static readonly Stand S11 = S("11", 61, 60, 100, 1);
    private static readonly Stand S12 = S("12", 36, 45, 100, 1);
    private static readonly Stand S13 = S("13", 39, 48, 100, 1);
    private static readonly Stand S55 = S("55", 36, 45, 200, 1);
    private static readonly Stand S42 = S("42", 36, 45, 300, 1);
    private static readonly Stand S63 = S("63", 36, 45, 400, 2);
    private static readonly Stand S71 = S("71", 36, 45, 500, 3);
    private static readonly Stand S61 = S("61", 25, 31, 400, 2);

    private static StandRequest A320(string cs, int start = 0, string? airline = null) => new()
    {
        Key = $"{cs}:{start}", Callsign = cs, AircraftType = "A320",
        WingspanM = 35.8, LengthM = 37.57, AirlineCode = airline,
        From = T0.AddMinutes(start), To = T0.AddMinutes(start + 60),
    };

    private static string? Pick(IEnumerable<Stand> stands, params StandRequest[] reqs) =>
        new StandAllocator(stands.ToList(), Opt).Allocate(reqs, T0).Assignments.Last().StandId;

    [Fact]
    public void Si_riempie_prima_il_gruppo_piu_comodo()
    {
        Assert.Equal("12", Pick([S71, S63, S42, S55, S12], A320("AZA100")));
    }

    [Fact]
    public void I_gruppi_si_scalano_nell_ordine_voluto()
    {
        // Ogni volo occupa lo stand del precedente: l'ordine con cui si riempiono è l'ordine
        // di comodità, 10-23, 51-57, 41-46, apron 2, apron 3.
        var stands = new[] { S71, S63, S42, S55, S12 };
        var reqs = Enumerable.Range(0, 5).Select(i => A320($"V{i}")).ToArray();

        var result = new StandAllocator(stands, Opt).Allocate(reqs, T0);

        Assert.Equal(["12", "55", "42", "63", "71"],
            result.Assignments.OrderBy(a => a.Callsign).Select(a => a.StandId));
    }

    [Fact]
    public void Dentro_il_gruppo_vince_lo_stand_che_spreca_meno_spazio()
    {
        // 11 (61 m) e 13 (39 m) sono nello stesso gruppo: per un A320 va il 13, e l'11 resta
        // libero per un widebody.
        Assert.Equal("13", Pick([S11, S13], A320("AZA100")));
    }

    [Fact]
    public void Il_gruppo_comanda_sullo_spazio_sprecato()
    {
        // Libero nel primo gruppo c'è solo l'11, da 61 m: l'ordine di comodità lo preferisce
        // comunque a uno stand su misura nel gruppo dei 50.
        Assert.Equal("11", Pick([S11, S55], A320("AZA100")));
    }

    [Fact]
    public void Gli_stand_troppo_piccoli_restano_esclusi_anche_se_comodi()
    {
        Assert.Equal("71", Pick([S61, S71], A320("AZA100")));
    }

    [Fact]
    public void Uno_stand_gia_dato_non_si_cambia_se_se_ne_libera_uno_piu_comodo()
    {
        var req = A320("AZA100");
        var result = new StandAllocator([S12, S71], Opt).Allocate([req], T0,
            previous: new Dictionary<string, string> { [req.Key] = "71" });

        Assert.Equal("71", result.Assignments.Single().StandId);
    }

    [Fact]
    public void Lo_stand_della_compagnia_vince_anche_sull_ordine_di_comodita()
    {
        var itaStand = S71 with { Airlines = ["ITY"] };

        Assert.Equal("71", Pick([S12, itaStand], A320("ITY1275", airline: "ITY")));
    }

    // --- Suggerimenti -----------------------------------------------------------------

    [Fact]
    public void I_suggerimenti_mettono_prima_i_liberi_adatti_nell_ordine_di_comodita()
    {
        var stands = new List<Stand> { S71, S63, S61, S55, S12, S11 };
        var busy = A320("RYR200");
        var mine = A320("AZA100");

        var allocator = new StandAllocator(stands, Opt);
        var plan = allocator.Allocate([busy, mine], T0);   // RYR200 prende il 12, AZA100 l'11
        var options = allocator.Suggest(mine, [busy, mine], plan.Assignments);

        // L'11 viene prima del 55 anche se è da 61 m: è nel gruppo più comodo, e il gruppo
        // comanda sullo spazio sprecato.
        Assert.Equal(["11", "55", "63", "71"],
            options.Where(o => o.Fits && o.Free).Select(o => o.StandId));

        var occupied = options.Single(o => o.StandId == "12");
        Assert.False(occupied.Free);
        Assert.Equal("RYR200", occupied.BusyWith);

        Assert.False(options.Single(o => o.StandId == "61").Fits);
        Assert.Equal("61", options.Last().StandId);
    }

    [Fact]
    public void Lo_stand_attuale_del_volo_compare_fra_i_liberi()
    {
        var mine = A320("AZA100");
        var allocator = new StandAllocator([S12, S55], Opt);
        var plan = allocator.Allocate([mine], T0);

        var options = allocator.Suggest(mine, [mine], plan.Assignments);

        Assert.True(options.Single(o => o.StandId == "12").Free);
    }
}
