using RfoGateManager.Domain;
using Xunit;

namespace RfoGateManager.Tests;

public class DepartureBoardTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 19, 10, 0, 0, TimeSpan.Zero);

    private static StandRequest Departure(string callsign, int eobtMin, string? state = null, bool online = false,
        string? actual = null) => new()
    {
        Key = $"{callsign}:{eobtMin}",
        Callsign = callsign,
        AircraftType = "A320",
        From = Now.AddMinutes(eobtMin - 60),
        To = Now.AddMinutes(eobtMin),
        ActualStand = actual,
        Outbound = new FlightLeg
        {
            Callsign = callsign, AircraftType = "A320", Departure = "LIRN", Arrival = "LEBL",
            Etd = Now.AddMinutes(eobtMin), Kind = LegKind.Departure,
            TrackState = state, IsOnline = online,
        },
    };

    private static List<DepartureStrip> Board(
        IEnumerable<StandRequest> requests,
        Dictionary<string, TrafficStatus>? traffic = null,
        Dictionary<string, bool>? called = null,
        IEnumerable<Assignment>? assignments = null) =>
        DepartureBoard.Build(
            requests.ToList(),
            (assignments ?? []).ToList(),
            traffic ?? new Dictionary<string, TrafficStatus>(StringComparer.OrdinalIgnoreCase),
            called ?? new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase),
            Now);

    [Fact]
    public void Le_partenze_escono_in_ordine_di_EOBT()
    {
        var strips = Board([Departure("C", 45), Departure("A", 5), Departure("B", 20)]);

        Assert.Equal(["A", "B", "C"], strips.Select(s => s.Callsign));
        Assert.Equal(5, strips[0].MinutesToEobt);
    }

    [Fact]
    public void Gli_arrivi_senza_ripartenza_non_sono_strip_di_partenza()
    {
        var arrival = new StandRequest
        {
            Key = "AZA100:0", Callsign = "AZA100", From = Now, To = Now.AddMinutes(90),
            Inbound = new FlightLeg { Callsign = "AZA100", Departure = "LIRF", Arrival = "LIRN", Kind = LegKind.Arrival },
        };

        Assert.Empty(Board([arrival]));
    }

    [Fact]
    public void Un_traffico_assunto_in_Aurora_risulta_che_ha_chiamato()
    {
        var traffic = new Dictionary<string, TrafficStatus>(StringComparer.OrdinalIgnoreCase)
        {
            ["VLG8AZ"] = new("VLG8AZ", OnGround: true, GroundSpeed: 0, Altitude: 300, AssumedBy: "LIRN_DEL"),
        };

        var s = Assert.Single(Board([Departure("VLG8AZ", 10)], traffic));

        Assert.True(s.Called);
        Assert.Equal("aurora", s.CalledSource);
        Assert.Equal("LIRN_DEL", s.AssumedBy);
        Assert.True(s.Online);
    }

    [Fact]
    public void Senza_assunzione_resta_fra_quelli_da_chiamare()
    {
        var traffic = new Dictionary<string, TrafficStatus>(StringComparer.OrdinalIgnoreCase)
        {
            ["VLG8AZ"] = new("VLG8AZ", true, 0, 300, AssumedBy: ""),
        };

        var s = Assert.Single(Board([Departure("VLG8AZ", 10)], traffic));

        Assert.False(s.Called);
        Assert.Null(s.CalledSource);
    }

    [Fact]
    public void La_scelta_manuale_vince_sulla_deduzione_in_tutti_e_due_i_sensi()
    {
        var traffic = new Dictionary<string, TrafficStatus>(StringComparer.OrdinalIgnoreCase)
        {
            ["ASSUNTO"] = new("ASSUNTO", true, 0, 300, "LIRN_GND"),
        };
        var manual = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
        {
            ["ASSUNTO"] = false,   // assunto, ma il GND dice che non ha ancora chiamato
            ["LIBERO"] = true,     // nessuno l'ha assunto, ma ha chiamato
        };

        var strips = Board([Departure("ASSUNTO", 10), Departure("LIBERO", 20)], traffic, manual);

        Assert.False(strips.Single(s => s.Callsign == "ASSUNTO").Called);
        Assert.True(strips.Single(s => s.Callsign == "LIBERO").Called);
        Assert.All(strips, s => Assert.Equal("manuale", s.CalledSource));
    }

    [Fact]
    public void Chi_e_gia_in_volo_esce_dalla_strippiera()
    {
        var traffic = new Dictionary<string, TrafficStatus>(StringComparer.OrdinalIgnoreCase)
        {
            ["DECOLLATO"] = new("DECOLLATO", OnGround: false, GroundSpeed: 180, Altitude: 3000, "LIRN_TWR"),
        };

        var strips = Board([
            Departure("DECOLLATO", -5),
            Departure("INROTTA", -40, state: "En Route"),
            Departure("ATERRA", 10, state: "Boarding"),
        ], traffic);

        Assert.Equal("ATERRA", Assert.Single(strips).Callsign);
    }

    [Fact]
    public void Lo_stand_mostrato_e_quello_dove_l_aereo_e_davvero_se_Aurora_lo_sa()
    {
        var assignments = new[]
        {
            new Assignment { Key = "PIANO:10", Callsign = "PIANO", StandId = "14", From = Now, To = Now },
        };

        var strips = Board([Departure("PIANO", 10), Departure("REALE", 20, actual: "22")], assignments: assignments);

        Assert.Equal("14", strips.Single(s => s.Callsign == "PIANO").Stand);
        Assert.Equal("22", strips.Single(s => s.Callsign == "REALE").Stand);
    }

    [Fact]
    public void Un_pilota_non_ancora_connesso_si_distingue()
    {
        var s = Assert.Single(Board([Departure("OFFLINE", 30)]));

        Assert.False(s.Online);
        Assert.False(s.Called);
    }
}
