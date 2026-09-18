using RfoGateManager.Domain;
using Xunit;

namespace RfoGateManager.Tests;

public class StandAllocatorTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 19, 10, 0, 0, TimeSpan.Zero);

    private static Stand Stand(
        string id, SizeCategory max = SizeCategory.C, bool contact = false,
        string[]? uses = null, string[]? airlines = null, string[]? blocks = null,
        bool disabled = false, int priority = 100,
        double? maxSpan = null, double? maxLength = null) => new()
    {
        Id = id,
        Icao = "LIRN",
        Lat = 40.886,
        Lon = 14.291,
        MaxSize = max,
        MaxWingspanM = maxSpan,
        MaxLengthM = maxLength,
        Contact = contact,
        Uses = uses ?? [],
        Airlines = airlines ?? [],
        Blocks = blocks ?? [],
        Disabled = disabled,
        Priority = priority,
    };

    private static StandRequest Request(
        string callsign, int startMin, int durationMin,
        SizeCategory size = SizeCategory.C, string use = "pax",
        string? booked = null, string? actual = null,
        string type = "A320", double? span = null, double? length = null) => new()
    {
        Key = $"{callsign}:{startMin}",
        Callsign = callsign,
        AircraftType = type,
        Size = size,
        WingspanM = span,
        LengthM = length,
        Use = use,
        AirlineCode = callsign.Length >= 3 ? callsign[..3] : null,
        From = T0.AddMinutes(startMin),
        To = T0.AddMinutes(startMin + durationMin),
        BookedStand = booked,
        ActualStand = actual,
    };

    private static AllocationOptions Options(int buffer = 10) => new() { BufferMinutes = buffer };

    [Fact]
    public void Due_voli_sovrapposti_non_finiscono_sullo_stesso_stand()
    {
        var allocator = new StandAllocator([Stand("201"), Stand("202")], Options());

        var result = allocator.Allocate([
            Request("AZA100", 0, 60),
            Request("RYR200", 30, 60),
        ], T0);

        var stands = result.Assignments.Select(a => a.StandId).ToList();
        Assert.Equal(2, stands.Distinct().Count());
        Assert.DoesNotContain(null, stands);
    }

    [Fact]
    public void Uno_stand_si_riusa_quando_il_primo_volo_e_ripartito()
    {
        var allocator = new StandAllocator([Stand("201")], Options(buffer: 10));

        // Il secondo arriva 20 minuti dopo la partenza del primo: il margine di 10 minuti basta.
        var result = allocator.Allocate([
            Request("AZA100", 0, 60),
            Request("RYR200", 80, 60),
        ], T0);

        Assert.All(result.Assignments, a => Assert.Equal("201", a.StandId));
        Assert.Equal(0, result.Unassigned);
    }

    [Fact]
    public void Il_margine_fra_due_occupanti_viene_rispettato()
    {
        var allocator = new StandAllocator([Stand("201")], Options(buffer: 30));

        // Solo 15 minuti di stacco: con 30 di margine il secondo non ci sta.
        var result = allocator.Allocate([
            Request("AZA100", 0, 60),
            Request("RYR200", 75, 60),
        ], T0);

        Assert.Equal(1, result.Unassigned);
    }

    [Fact]
    public void Un_aereo_troppo_grande_non_entra_in_uno_stand_piccolo()
    {
        var allocator = new StandAllocator([Stand("G1", SizeCategory.B)], Options());

        var result = allocator.Allocate([Request("AZA100", 0, 60, SizeCategory.E)], T0);

        var a = Assert.Single(result.Assignments);
        Assert.Null(a.StandId);
        Assert.Contains("troppo piccoli", a.Reason);
    }

    [Fact]
    public void A_parita_di_condizioni_vince_lo_stand_della_misura_giusta()
    {
        var allocator = new StandAllocator(
            [Stand("BIG", SizeCategory.E), Stand("FIT", SizeCategory.C)], Options());

        var result = allocator.Allocate([Request("AZA100", 0, 60, SizeCategory.C)], T0);

        Assert.Equal("FIT", Assert.Single(result.Assignments).StandId);
    }

    [Fact]
    public void Lo_stand_assegnato_alla_compagnia_ha_la_precedenza()
    {
        var allocator = new StandAllocator(
            [Stand("101"), Stand("AZ1", airlines: ["AZA"])], Options());

        var result = allocator.Allocate([Request("AZA100", 0, 60)], T0);

        var a = Assert.Single(result.Assignments);
        Assert.Equal("AZ1", a.StandId);
        Assert.Contains("AZA", a.Reason);
    }

    [Fact]
    public void Occupare_uno_stand_MARS_rende_inagibile_il_gemello()
    {
        var allocator = new StandAllocator([
            Stand("10", SizeCategory.E, blocks: ["10L", "10R"]),
            Stand("10L"),
            Stand("10R"),
        ], Options());

        var result = allocator.Allocate([
            Request("AZA100", 0, 120, SizeCategory.E),  // prende il 10, che blocca 10L e 10R
            Request("RYR200", 30, 60),
            Request("RYR300", 40, 60),
        ], T0);

        var big = result.Assignments.Single(a => a.Callsign == "AZA100");
        Assert.Equal("10", big.StandId);

        // Gli altri due non hanno dove andare: 10L e 10R sono bloccati dal 10.
        Assert.Equal(2, result.Unassigned);
        Assert.All(result.Assignments.Where(a => a.Callsign != "AZA100"),
            a => Assert.Contains("MARS", a.Reason));
    }

    [Fact]
    public void Lo_stand_prenotato_viene_rispettato_anche_se_non_e_il_migliore()
    {
        var allocator = new StandAllocator(
            [Stand("201"), Stand("999", SizeCategory.E, priority: 900)], Options());

        var result = allocator.Allocate([Request("AZA100", 0, 60, booked: "999")], T0);

        var a = Assert.Single(result.Assignments);
        Assert.Equal("999", a.StandId);
        Assert.Contains("Da prenotazione", a.Reason);
        Assert.False(a.Conflict);
    }

    [Fact]
    public void Lo_stand_dove_l_aereo_si_trova_davvero_batte_la_prenotazione()
    {
        var allocator = new StandAllocator([Stand("201"), Stand("305")], Options());

        var result = allocator.Allocate([Request("AZA100", 0, 60, booked: "201", actual: "305")], T0);

        var a = Assert.Single(result.Assignments);
        Assert.Equal("305", a.StandId);
        Assert.Contains("A terra", a.Reason);
    }

    [Fact]
    public void Due_prenotazioni_sullo_stesso_stand_producono_un_conflitto_dichiarato()
    {
        var allocator = new StandAllocator([Stand("201"), Stand("202")], Options());

        var result = allocator.Allocate([
            Request("AZA100", 0, 60, booked: "201"),
            Request("RYR200", 30, 60, booked: "201"),
        ], T0);

        Assert.Equal(1, result.Conflicts);
        var clash = result.Assignments.Single(a => a.Conflict);
        Assert.Contains("si sovrappone", clash.Reason);
    }

    [Fact]
    public void Un_pin_manuale_vince_su_tutto()
    {
        var allocator = new StandAllocator([Stand("201"), Stand("202")], Options());
        var req = Request("AZA100", 0, 60, booked: "201");

        var result = allocator.Allocate([req], T0,
            pinned: new Dictionary<string, string> { [req.Key] = "202" });

        var a = Assert.Single(result.Assignments);
        Assert.Equal("202", a.StandId);
        Assert.True(a.Manual);
    }

    [Fact]
    public void Il_piano_precedente_viene_confermato_invece_di_rimescolare()
    {
        var stands = new[] { Stand("201"), Stand("202"), Stand("203") };
        var req = Request("AZA100", 0, 60);

        var first = new StandAllocator(stands, Options()).Allocate([req], T0);
        var chosen = first.Assignments[0].StandId!;

        // Forziamo un piano precedente diverso: deve vincere quello, non ricominciare da capo.
        var other = stands.First(s => s.Id != chosen).Id;
        var second = new StandAllocator(stands, Options()).Allocate([req], T0,
            previous: new Dictionary<string, string> { [req.Key] = other });

        Assert.Equal(other, second.Assignments[0].StandId);
        Assert.Contains("piano precedente", second.Assignments[0].Reason);
    }

    [Fact]
    public void Uno_stand_chiuso_non_viene_usato()
    {
        var allocator = new StandAllocator(
            [Stand("201", disabled: true), Stand("202")], Options());

        var result = allocator.Allocate([Request("AZA100", 0, 60)], T0);

        Assert.Equal("202", Assert.Single(result.Assignments).StandId);
    }

    [Fact]
    public void Lo_stand_con_uso_dedicato_non_accoglie_altro_traffico()
    {
        var allocator = new StandAllocator([Stand("C1", uses: ["cargo"])], Options());

        var result = allocator.Allocate([Request("AZA100", 0, 60)], T0);

        var a = Assert.Single(result.Assignments);
        Assert.Null(a.StandId);
        Assert.Contains("uso incompatibile", a.Reason);
    }

    [Fact]
    public void Uno_stand_sconosciuto_imposto_a_mano_viene_segnalato_non_ignorato()
    {
        var allocator = new StandAllocator([Stand("201")], Options());

        var result = allocator.Allocate([Request("AZA100", 0, 60, booked: "PIAZZALE_X")], T0);

        var a = Assert.Single(result.Assignments);
        Assert.True(a.Conflict);
        Assert.Contains("non presente nel file stand", a.Reason);
    }

    // --- Misure reali dall'AIP, che battono la lettera di codice ---------------

    [Fact]
    public void Un_A320_non_entra_in_uno_stand_codice_C_limitato_a_32_metri()
    {
        // Napoli stand 23: codice C, ma l'AIP pubblica 32 m di apertura massima.
        var allocator = new StandAllocator(
            [Stand("23", SizeCategory.C, maxSpan: 32, maxLength: 37)], Options());

        var result = allocator.Allocate(
            [Request("AZA100", 0, 60, type: "A320", span: 35.8, length: 37.57)], T0);

        var a = Assert.Single(result.Assignments);
        Assert.Null(a.StandId);
        Assert.Contains("troppo piccoli", a.Reason);
    }

    [Fact]
    public void Lo_stesso_stand_accoglie_un_regionale_che_ci_sta()
    {
        var allocator = new StandAllocator(
            [Stand("23", SizeCategory.C, maxSpan: 32, maxLength: 37)], Options());

        var result = allocator.Allocate(
            [Request("AZA100", 0, 60, type: "E190", span: 28.72, length: 36.24)], T0);

        Assert.Equal("23", Assert.Single(result.Assignments).StandId);
    }

    [Fact]
    public void Anche_la_lunghezza_esclude_uno_stand_con_apertura_sufficiente()
    {
        // Napoli stand 16: 36 m di apertura ma solo 39 m di lunghezza.
        // L'A321 ha la stessa apertura dell'A320 ed e' lungo 44,5 m.
        var allocator = new StandAllocator(
            [Stand("16", SizeCategory.C, maxSpan: 36, maxLength: 39)], Options());

        var result = allocator.Allocate(
            [Request("AZA100", 0, 60, type: "A321", span: 35.8, length: 44.51)], T0);

        Assert.Null(Assert.Single(result.Assignments).StandId);
    }

    [Fact]
    public void Lo_stand_prenotato_vince_sulle_misure_e_viene_assegnato_lo_stesso()
    {
        // Caso vero del booking RFO: un B787-8 prenotato sullo stand 12, che e' 36x45 m.
        // La prenotazione comanda, ma la riga deve gridarlo.
        var allocator = new StandAllocator(
            [Stand("12", SizeCategory.C, maxSpan: 36, maxLength: 45), Stand("11", SizeCategory.E, maxSpan: 61, maxLength: 60)],
            Options());

        var result = allocator.Allocate(
            [Request("ACA883", 0, 90, type: "B788", span: 60.12, length: 56.72, booked: "12")], T0);

        var a = Assert.Single(result.Assignments);
        Assert.Equal("12", a.StandId);
        Assert.True(a.Oversize);
        Assert.False(a.Conflict);
        Assert.Contains("ATTENZIONE", a.Reason);
        Assert.Contains("apertura alare", a.Reason);
    }

    [Fact]
    public void Fuori_misura_e_sovrapposizione_sono_due_cose_diverse()
    {
        var allocator = new StandAllocator(
            [Stand("12", SizeCategory.C, maxSpan: 36, maxLength: 45)], Options());

        var result = allocator.Allocate([
            Request("AZA100", 0, 60, type: "A320", span: 35.8, length: 37.57, booked: "12"),
            Request("ACA883", 30, 60, type: "B788", span: 60.12, length: 56.72, booked: "12"),
        ], T0);

        var big = result.Assignments.Single(a => a.Callsign == "ACA883");
        Assert.Equal("12", big.StandId);
        Assert.True(big.Oversize);   // non ci sta
        Assert.True(big.Conflict);   // e in piu' si sovrappone a un altro volo
    }

    [Fact]
    public void Anche_un_pin_manuale_vince_sulle_misure()
    {
        var allocator = new StandAllocator(
            [Stand("61", SizeCategory.B, maxSpan: 25, maxLength: 31), Stand("11", SizeCategory.E, maxSpan: 61, maxLength: 60)],
            Options());
        var req = Request("AZA100", 0, 60, type: "B788", span: 60.12, length: 56.72);

        var result = allocator.Allocate([req], T0,
            pinned: new Dictionary<string, string> { [req.Key] = "61" });

        var a = Assert.Single(result.Assignments);
        Assert.Equal("61", a.StandId);
        Assert.True(a.Oversize);
        Assert.True(a.Manual);
    }

    [Fact]
    public void La_scelta_automatica_invece_le_misure_le_rispetta()
    {
        // Senza uno stand gia' deciso non si forza nulla: se non ci sta, non ci va.
        var allocator = new StandAllocator(
            [Stand("12", SizeCategory.C, maxSpan: 36, maxLength: 45)], Options());

        var result = allocator.Allocate(
            [Request("ACA883", 0, 90, type: "B788", span: 60.12, length: 56.72)], T0);

        var a = Assert.Single(result.Assignments);
        Assert.Null(a.StandId);
        Assert.False(a.Oversize);
    }

    [Fact]
    public void Fra_due_stand_capienti_vince_quello_che_spreca_meno_metri()
    {
        var allocator = new StandAllocator([
            Stand("11", SizeCategory.E, maxSpan: 61, maxLength: 60),
            Stand("12", SizeCategory.C, maxSpan: 36, maxLength: 45),
        ], Options());

        var result = allocator.Allocate(
            [Request("AZA100", 0, 60, type: "A320", span: 35.8, length: 37.57)], T0);

        var a = Assert.Single(result.Assignments);
        Assert.Equal("12", a.StandId);
        Assert.Contains("misura giusta", a.Reason);
    }

    [Fact]
    public void Un_tipo_di_misure_ignote_ricade_sulla_lettera_di_codice()
    {
        var allocator = new StandAllocator(
            [Stand("61", SizeCategory.B, maxSpan: 25, maxLength: 31)], Options());

        // Nessuna misura nota: resta il confronto fra categorie, C non entra in B.
        var result = allocator.Allocate([Request("AZA100", 0, 60, type: "XXXX")], T0);

        Assert.Null(Assert.Single(result.Assignments).StandId);
    }
}
