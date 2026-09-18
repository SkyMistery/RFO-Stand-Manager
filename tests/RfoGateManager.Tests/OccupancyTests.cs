using RfoGateManager.Domain;
using Xunit;

namespace RfoGateManager.Tests;

public class OccupancyTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 19, 10, 0, 0, TimeSpan.Zero);
    private static readonly AllocationOptions Opt = new();

    private static Stand Stand(string id, double span = 36, double length = 45, string[]? blocks = null) => new()
    {
        Id = id,
        Icao = "LIRN",
        MaxSize = SizeCategory.C,
        MaxWingspanM = span,
        MaxLengthM = length,
        Blocks = blocks ?? [],
    };

    private static StandRequest Arrival(string callsign, int etaMin, string? booked = null) => new()
    {
        Key = $"{callsign}:{etaMin}",
        Callsign = callsign,
        AircraftType = "A320",
        WingspanM = 35.8,
        LengthM = 37.57,
        From = Now.AddMinutes(etaMin),
        To = Now.AddMinutes(etaMin + 60),
        BookedStand = booked,
        Inbound = new FlightLeg
        {
            Callsign = callsign, Departure = "LIRF", Arrival = "LIRN",
            Eta = Now.AddMinutes(etaMin), Kind = LegKind.Arrival,
        },
    };

    private static List<StandRequest> WithSquatter(IEnumerable<StandRequest> requests, string callsign, string stand) =>
        Occupancy.Apply(requests.ToList(), [new PhysicalOccupant(callsign, stand, "aurora")], Now, Opt);

    // --- Lo scenario per cui esiste tutto questo ---------------------------------

    [Fact]
    public void Chi_arriva_su_uno_stand_occupato_da_un_non_programmato_riceve_un_altro_stand()
    {
        var stands = new[] { Stand("14"), Stand("15") };
        var requests = WithSquatter([Arrival("AZA100", 30, booked: "14")], "IABCD", "14");

        var result = new StandAllocator(stands, Opt).Allocate(requests, Now);

        var squatter = result.Assignments.Single(a => a.Callsign == "IABCD");
        Assert.Equal("14", squatter.StandId);
        Assert.True(squatter.Unscheduled);
        Assert.False(squatter.Conflict);

        var arrival = result.Assignments.Single(a => a.Callsign == "AZA100");
        Assert.Equal("15", arrival.StandId);
        Assert.True(arrival.Reassigned);
        Assert.Equal("14", arrival.DisplacedFrom);
        Assert.Equal("IABCD", arrival.DisplacedBy);
        Assert.False(arrival.Conflict);
        Assert.Contains("occupato da IABCD", arrival.Reason);
    }

    [Fact]
    public void Una_prenotazione_che_arriva_dopo_la_finestra_dell_occupante_tiene_il_suo_stand()
    {
        // L'occupante blocca lo stand per un'ora che scorre: chi arriva fra tre ore non va
        // spostato adesso, magari quello se ne sarà andato.
        var stands = new[] { Stand("14"), Stand("15") };
        var requests = WithSquatter([Arrival("AZA100", 180, booked: "14")], "IABCD", "14");

        var result = new StandAllocator(stands, Opt).Allocate(requests, Now);

        var arrival = result.Assignments.Single(a => a.Callsign == "AZA100");
        Assert.Equal("14", arrival.StandId);
        Assert.False(arrival.Reassigned);
    }

    [Fact]
    public void Due_prenotazioni_sullo_stesso_stand_restano_un_conflitto_e_non_si_spostano()
    {
        // Senza nessuno a terra non c'è un fatto da rispettare: decide il controllore.
        var stands = new[] { Stand("14"), Stand("15") };
        var requests = new List<StandRequest> { Arrival("AZA100", 0, booked: "14"), Arrival("RYR200", 20, booked: "14") };

        var result = new StandAllocator(stands, Opt).Allocate(requests, Now);

        Assert.All(result.Assignments, a => Assert.Equal("14", a.StandId));
        Assert.All(result.Assignments, a => Assert.False(a.Reassigned));
        Assert.Equal(1, result.Conflicts);
    }

    [Fact]
    public void Anche_uno_stand_fissato_a_mano_cede_all_aereo_fisicamente_presente()
    {
        var stands = new[] { Stand("14"), Stand("15") };
        var arrival = Arrival("AZA100", 30);
        var requests = WithSquatter([arrival], "IABCD", "14");

        var result = new StandAllocator(stands, Opt).Allocate(requests, Now,
            pinned: new Dictionary<string, string> { [arrival.Key] = "14" });

        var a = result.Assignments.Single(x => x.Callsign == "AZA100");
        Assert.Equal("15", a.StandId);
        Assert.True(a.Reassigned);
        Assert.True(a.WasPinned);
        Assert.Contains("ricomunicare", a.Reason);
    }

    [Fact]
    public void Un_occupante_su_uno_stand_MARS_adiacente_fa_riassegnare_anche_il_gemello()
    {
        var stands = new[] { Stand("10", blocks: ["10L"]), Stand("10L"), Stand("11") };
        var requests = WithSquatter([Arrival("AZA100", 30, booked: "10L")], "IABCD", "10");

        var result = new StandAllocator(stands, Opt).Allocate(requests, Now);

        var a = result.Assignments.Single(x => x.Callsign == "AZA100");
        Assert.Equal("11", a.StandId);
        Assert.True(a.Reassigned);
        Assert.Contains("MARS", a.Reason);
    }

    [Fact]
    public void Il_sostituto_rispetta_le_misure_anche_se_lo_stand_prenotato_non_le_rispettava()
    {
        // La regola "lo stand deciso vince sulle misure" vale per quello stand, non per il
        // sostituto: quello lo scegliamo noi, e un A320 non va su uno stand da 32 m.
        var stands = new[] { Stand("12"), Stand("23", span: 32, length: 37), Stand("20") };
        var requests = WithSquatter([Arrival("AZA100", 30, booked: "12")], "IABCD", "12");

        var result = new StandAllocator(stands, Opt).Allocate(requests, Now);

        Assert.Equal("20", result.Assignments.Single(x => x.Callsign == "AZA100").StandId);
    }

    [Fact]
    public void Se_non_c_e_nessun_altro_stand_libero_lo_dice_insieme_a_chi_occupa_il_suo()
    {
        var stands = new[] { Stand("14") };
        var requests = WithSquatter([Arrival("AZA100", 30, booked: "14")], "IABCD", "14");

        var result = new StandAllocator(stands, Opt).Allocate(requests, Now);

        var a = result.Assignments.Single(x => x.Callsign == "AZA100");
        Assert.Null(a.StandId);
        Assert.True(a.Reassigned);
        Assert.True(a.Conflict);
        Assert.Contains("occupato da IABCD", a.Reason);
        Assert.Contains("nessun altro", a.Reason);
    }

    [Fact]
    public void Dove_l_aereo_e_davvero_batte_anche_lo_stand_fissato_a_mano()
    {
        var stands = new[] { Stand("14"), Stand("15") };
        var arrival = Arrival("AZA100", 0);
        var requests = Occupancy.Apply([arrival], [new PhysicalOccupant("AZA100", "15", "aurora")], Now, Opt);

        var result = new StandAllocator(stands, Opt).Allocate(requests, Now,
            pinned: new Dictionary<string, string> { [arrival.Key] = "14" });

        Assert.Equal("15", Assert.Single(result.Assignments).StandId);
    }

    // --- Occupancy.Apply ----------------------------------------------------------

    [Fact]
    public void Un_aereo_del_piano_gia_a_terra_occupa_da_adesso_anche_se_il_piano_lo_dava_dopo()
    {
        var requests = Occupancy.Apply(
            [Arrival("AZA100", 25)], [new PhysicalOccupant("AZA100", "14", "aurora")], Now, Opt);

        var r = Assert.Single(requests);
        Assert.Equal("14", r.ActualStand);
        Assert.Equal(Now, r.From);
        Assert.False(r.Unscheduled);
    }

    [Fact]
    public void In_una_rotazione_l_aereo_si_riconosce_anche_col_callsign_del_ritorno()
    {
        var rotation = Arrival("AAL180", -30) with
        {
            Outbound = new FlightLeg
            {
                Callsign = "AAL781", Departure = "LIRN", Arrival = "KJFK",
                Etd = Now.AddMinutes(90), Kind = LegKind.Departure,
            },
        };

        var requests = Occupancy.Apply([rotation], [new PhysicalOccupant("AAL781", "21", "aurora")], Now, Opt);

        var r = Assert.Single(requests);
        Assert.Equal("21", r.ActualStand);
        Assert.False(r.Unscheduled);
    }

    [Fact]
    public void Un_aereo_fermo_che_non_e_nel_piano_diventa_un_occupante_non_programmato()
    {
        var requests = Occupancy.Apply([], [new PhysicalOccupant("IABCD", "14", "whazzup", "C172")], Now, Opt);

        var r = Assert.Single(requests);
        Assert.True(r.Unscheduled);
        Assert.True(r.Locked);
        Assert.Equal("14", r.ActualStand);
        Assert.Equal(Now, r.From);
        Assert.Equal(Now.AddMinutes(Opt.UnscheduledOccupancyMinutes), r.To);
        Assert.Equal("C172", r.AircraftType);
    }

    [Fact]
    public void Un_aereo_rimasto_oltre_la_fine_prevista_continua_a_occupare()
    {
        // Il piano lo dava già ripartito, ma è ancora lì: lo stand resta suo.
        var late = Arrival("AZA100", -120);
        var requests = Occupancy.Apply([late], [new PhysicalOccupant("AZA100", "14", "aurora")], Now, Opt);

        var r = Assert.Single(requests);
        Assert.True(r.To > Now);
    }

    // --- Stand più vicino --------------------------------------------------------

    [Fact]
    public void Lo_stand_piu_vicino_si_trova_solo_entro_la_distanza_massima()
    {
        // Coordinate vere del lirn.gts: stand 14 e 15 distano circa 48 metri.
        var s14 = new Stand { Id = "14", Icao = "LIRN", Lat = 40.878719, Lon = 14.282828 };
        var s15 = new Stand { Id = "15", Icao = "LIRN", Lat = 40.878775, Lon = 14.282256 };

        Assert.Equal("14", Occupancy.NearestStand(40.878725, 14.282800, [s14, s15], 40));
        Assert.Equal("15", Occupancy.NearestStand(40.878770, 14.282280, [s14, s15], 40));
        Assert.Null(Occupancy.NearestStand(40.8830, 14.2900, [s14, s15], 40));
    }

    [Fact]
    public void La_distanza_fra_due_punti_e_quella_giusta()
    {
        // Un millesimo di grado di latitudine è circa 111 metri.
        Assert.InRange(Occupancy.DistanceMeters(40.0, 14.0, 40.001, 14.0), 110, 112);
    }
}
