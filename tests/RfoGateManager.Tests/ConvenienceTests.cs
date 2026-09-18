using RfoGateManager.Domain;
using Xunit;

namespace RfoGateManager.Tests;

/// <summary>
/// L'ordine di comodità di Napoli: stand 10-20, poi 51-57, poi 41-46 (tutti apron 1), poi
/// apron 2, poi apron 3. Il gruppo comanda; dentro il gruppo vince chi spreca meno spazio.
/// Il 21 va di solito agli aerei grandi, il 22 e il 23 ai jet privati.
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

    private static readonly Stand S21 = S("21", 61, 59, 100, 1) with { ReservedFor = ["large"] };
    private static readonly Stand S22 = S("22", 36, 45, 100, 1) with { ReservedFor = ["ga"] };
    private static readonly Stand S23 = S("23", 32, 37, 100, 1) with { ReservedFor = ["ga"] };

    private static StandRequest B788(string cs) => new()
    {
        Key = $"{cs}:0", Callsign = cs, AircraftType = "B788", Size = SizeCategory.E,
        WingspanM = 60.12, LengthM = 56.72, From = T0, To = T0.AddMinutes(90),
    };

    private static StandRequest BizJet(string cs, string type = "C56X") => new()
    {
        Key = $"{cs}:0", Callsign = cs, AircraftType = type, Size = SizeCategory.B, Use = "ga",
        WingspanM = 16.97, LengthM = 14.91, From = T0, To = T0.AddMinutes(90),
    };

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
        // di comodità, 10-20, 51-57, 41-46, apron 2, apron 3.
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

    // --- Stand riservati di solito -----------------------------------------------------

    [Fact]
    public void Un_aereo_grande_va_sul_21_prima_che_sugli_altri_stand_capienti()
    {
        // L'11 è nello stesso gruppo e altrettanto largo: vince il 21, che è quello dei grandi.
        Assert.Equal("21", Pick([S11, S21], B788("AAL180")));
    }

    [Fact]
    public void Un_narrowbody_lascia_gli_stand_da_widebody_anche_se_sono_nel_gruppo_piu_comodo()
    {
        // Il caso di EJU14MA nel booking vero: nel primo gruppo è libero solo l'11, da 61 m.
        var s11 = S11 with { ReservedFor = ["large"] };

        Assert.Equal("55", Pick([s11, S55], A320("EJU14MA")));
    }

    [Fact]
    public void Un_757_non_occupa_uno_stand_da_widebody_se_ne_ha_uno_su_misura_nello_stesso_gruppo()
    {
        var s11 = S11 with { ReservedFor = ["large"] };
        var b752 = new StandRequest
        {
            Key = "B752:0", Callsign = "TOM1", AircraftType = "B752", Size = SizeCategory.D,
            WingspanM = 38.05, LengthM = 47.32, From = T0, To = T0.AddMinutes(60),
        };

        Assert.Equal("13", Pick([s11, S13], b752));
    }

    [Fact]
    public void Un_narrowbody_non_prende_il_21_finche_c_e_posto_altrove_anche_in_apron_3()
    {
        Assert.Equal("71", Pick([S21, S71], A320("AZA100")));
    }

    [Fact]
    public void Se_e_pieno_tutto_il_resto_anche_il_21_si_usa_e_il_motivo_lo_dice()
    {
        var result = new StandAllocator([S21, S71], Opt).Allocate([A320("RYR200"), A320("AZA100")], T0);

        var onReserved = result.Assignments.Single(a => a.StandId == "21");
        Assert.Contains("usato perché il resto è pieno", onReserved.Reason);
        Assert.Equal(0, result.Unassigned);
    }

    [Fact]
    public void Un_jet_privato_va_sul_22_o_23_prima_che_sugli_stand_comodi()
    {
        Assert.Equal("23", Pick([S12, S22, S23], BizJet("IABCD")));   // il 23 spreca meno spazio
    }

    [Fact]
    public void Un_aereo_di_linea_lascia_liberi_gli_stand_dei_jet_privati()
    {
        Assert.Equal("63", Pick([S22, S23, S63], A320("AZA100")));
    }

    [Fact]
    public void Nei_suggerimenti_gli_stand_riservati_ad_altri_stanno_in_fondo()
    {
        var mine = A320("AZA100");
        var allocator = new StandAllocator([S21, S22, S12, S71], Opt);
        var plan = allocator.Allocate([mine], T0);

        var free = allocator.Suggest(mine, [mine], plan.Assignments)
            .Where(o => o.Fits && o.Free).Select(o => o.StandId).ToList();

        Assert.Equal(["12", "71"], free.Take(2));
        Assert.Equal(["21", "22"], free.Skip(2).OrderBy(x => x));
    }
}
