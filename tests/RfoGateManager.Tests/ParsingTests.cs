using RfoGateManager.Data;
using RfoGateManager.Domain;
using Xunit;

namespace RfoGateManager.Tests;

public class GtsParserTests
{
    [Fact]
    public void Legge_il_formato_documentato_ID_ICAO_LAT_LON_TIPO()
    {
        var stands = GtsParser.Parse([
            "201;LIRN;N040.53.02.000;E014.17.14.000;PAX;",
            "202;LIRN;N040.53.03.000;E014.17.16.000;PAX;",
        ], "LIRN");

        Assert.Equal(2, stands.Count);
        Assert.Equal("201", stands[0].Id);
        Assert.Equal("PAX", stands[0].GtsType);
        Assert.Equal(40.884, stands[0].Lat, 3);
        Assert.Equal(14.287, stands[0].Lon, 3);
    }

    [Fact]
    public void Legge_anche_le_coordinate_decimali()
    {
        var stands = GtsParser.Parse(["G1;LIRN;40.8842;14.2875;GA;"], "LIRN");

        var s = Assert.Single(stands);
        Assert.Equal(40.8842, s.Lat, 4);
        Assert.Equal(14.2875, s.Lon, 4);
    }

    [Fact]
    public void Gli_emisferi_sud_e_ovest_danno_valori_negativi()
    {
        Assert.True(GtsParser.TryParseCoordinate("S033.56.46.000", out var lat));
        Assert.True(GtsParser.TryParseCoordinate("W118.24.29.000", out var lon));

        Assert.Equal(-33.946, lat, 3);
        Assert.Equal(-118.408, lon, 3);
    }

    [Fact]
    public void Scarta_righe_vuote_commenti_e_intestazioni_di_sezione()
    {
        var stands = GtsParser.Parse([
            "[GATES]",
            "",
            "// stand del molo A",
            "201;LIRN;N040.53.02.000;E014.17.14.000;PAX;",
            "spazzatura",
        ], "LIRN");

        Assert.Single(stands);
    }

    [Fact]
    public void Gli_stand_di_altri_aeroporti_vengono_esclusi()
    {
        var stands = GtsParser.Parse([
            "201;LIRN;N040.53.02.000;E014.17.14.000;PAX;",
            "501;LIRF;N041.48.02.000;E012.14.24.000;PAX;",
        ], "LIRN");

        Assert.Single(stands);
        Assert.Equal("201", stands[0].Id);
    }

    [Fact]
    public void Gli_id_duplicati_non_vengono_caricati_due_volte()
    {
        var stands = GtsParser.Parse([
            "201;LIRN;N040.53.02.000;E014.17.14.000;PAX;",
            "201;LIRN;N040.53.09.000;E014.17.19.000;PAX;",
        ], "LIRN");

        Assert.Single(stands);
    }
}

public class BookingTimeTests
{
    private static readonly DateOnly Day = new(2026, 9, 19);

    [Theory]
    [InlineData("1230", 12, 30)]
    [InlineData("930", 9, 30)]
    [InlineData("12:30", 12, 30)]
    [InlineData("08:05", 8, 5)]
    public void Legge_gli_orari_nei_formati_che_girano(string raw, int hour, int minute)
    {
        var t = BookingClient.ParseTime(raw, Day);

        Assert.NotNull(t);
        Assert.Equal(hour, t!.Value.UtcDateTime.Hour);
        Assert.Equal(minute, t.Value.UtcDateTime.Minute);
        Assert.Equal(19, t.Value.UtcDateTime.Day);
    }

    [Fact]
    public void Legge_un_orario_ISO_completo()
    {
        var t = BookingClient.ParseTime("2026-09-19T14:45:00Z", Day);

        Assert.NotNull(t);
        Assert.Equal(14, t!.Value.UtcDateTime.Hour);
    }

    [Fact]
    public void Un_orario_assente_o_incomprensibile_resta_nullo()
    {
        Assert.Null(BookingClient.ParseTime(null, Day));
        Assert.Null(BookingClient.ParseTime("", Day));
        Assert.Null(BookingClient.ParseTime("domani mattina", Day));
    }
}

public class WhazzupTests
{
    [Fact]
    public void Estrae_le_marche_dai_remarks()
    {
        Assert.Equal("I-ABCD", WhazzupClient.ExtractRegistration("PBN/A1B1 DOF/260919 REG/I-ABCD OPR/AZA"));
        Assert.Equal("N388SB", WhazzupClient.ExtractRegistration("REG/N388SB PER/D"));
    }

    [Fact]
    public void Senza_marche_nei_remarks_non_inventa_nulla()
    {
        Assert.Null(WhazzupClient.ExtractRegistration("PBN/A1B1 DOF/260919"));
        Assert.Null(WhazzupClient.ExtractRegistration(null));
    }
}

public class AircraftCatalogTests
{
    [Theory]
    [InlineData("A320", SizeCategory.C)]
    [InlineData("B738", SizeCategory.C)]
    [InlineData("AT72", SizeCategory.B)]
    [InlineData("B77W", SizeCategory.E)]
    [InlineData("A388", SizeCategory.F)]
    [InlineData("C172", SizeCategory.A)]
    public void Assegna_la_categoria_giusta_ai_tipi_noti(string type, SizeCategory expected)
        => Assert.Equal(expected, AircraftCatalog.SizeOf(type));

    [Fact]
    public void Un_tipo_sconosciuto_ricade_su_narrowbody()
        => Assert.Equal(SizeCategory.C, AircraftCatalog.SizeOf("XXXX"));

    [Theory]
    [InlineData("C56X", "AZA1234", true)]     // Citation Excel: jet privato dal tipo
    [InlineData("C700", "AHS801D", true)]     // Citation Longitude di un operatore executive
    [InlineData("A320", "N900FZ", true)]      // marca americana
    [InlineData("A320", "IABCD", true)]       // marca italiana senza trattino
    [InlineData("A320", "D-IABC", true)]      // marca col trattino
    [InlineData("C172", "AZA1", true)]        // aviazione generale dal tipo
    [InlineData("A320", "AZA1234", false)]
    [InlineData("B738", "RYR54TG", false)]
    [InlineData("A388", "LGX17V", false)]
    public void Riconosce_i_jet_privati_dal_tipo_o_dalla_marca(string type, string callsign, bool expected)
        => Assert.Equal(expected, AircraftCatalog.IsBusinessOrGa(type, callsign));

    [Theory]
    [InlineData("AZA1234", "AZA")]
    [InlineData("RYR54TG", "RYR")]
    [InlineData("I-ABCD", null)]
    [InlineData("N12", null)]
    public void Riconosce_il_prefisso_compagnia(string callsign, string? expected)
        => Assert.Equal(expected, AircraftCatalog.AirlineOf(callsign));
}

public class RotationBuilderTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 19, 10, 0, 0, TimeSpan.Zero);
    private static readonly AllocationOptions Opt = new();

    private static FlightLeg Arrival(string cs, int min, string? reg = null) => new()
    {
        Callsign = cs,
        Registration = reg,
        AircraftType = "A320",
        Departure = "LIRF",
        Arrival = "LIRN",
        Eta = T0.AddMinutes(min),
        Kind = LegKind.Arrival,
    };

    private static FlightLeg Departure(string cs, int min, string? reg = null) => new()
    {
        Callsign = cs,
        Registration = reg,
        AircraftType = "A320",
        Departure = "LIRN",
        Arrival = "LIRF",
        Etd = T0.AddMinutes(min),
        Kind = LegKind.Departure,
    };

    [Fact]
    public void Arrivo_e_ripartenza_dello_stesso_callsign_occupano_lo_stand_senza_interruzioni()
    {
        var r = Assert.Single(RotationBuilder.Build(
            [Arrival("AZA100", 0), Departure("AZA100", 90)], "LIRN", Opt, T0));

        Assert.Equal(T0, r.From);
        Assert.Equal(T0.AddMinutes(90), r.To);
        Assert.NotNull(r.Inbound);
        Assert.NotNull(r.Outbound);
    }

    [Fact]
    public void La_rotazione_si_riconosce_dalle_marche_anche_col_callsign_diverso()
    {
        var r = Assert.Single(RotationBuilder.Build(
            [Arrival("AZA100", 0, "I-ABCD"), Departure("AZA101", 75, "I-ABCD")], "LIRN", Opt, T0));

        Assert.Equal("AZA101", r.Outbound!.Callsign);
        Assert.Equal(T0.AddMinutes(75), r.To);
    }

    [Fact]
    public void Un_arrivo_senza_ripartenza_occupa_per_la_sosta_predefinita()
    {
        var r = Assert.Single(RotationBuilder.Build([Arrival("AZA100", 0)], "LIRN", Opt, T0));

        Assert.Equal(T0.AddMinutes(Opt.DefaultTurnaroundMinutes), r.To);
        Assert.Null(r.Outbound);
    }

    [Fact]
    public void Una_partenza_senza_arrivo_occupa_lo_stand_prima_del_push_back()
    {
        var r = Assert.Single(RotationBuilder.Build([Departure("AZA100", 60)], "LIRN", Opt, T0));

        Assert.Equal(T0.AddMinutes(60), r.To);
        Assert.Equal(T0.AddMinutes(60 - Opt.DepartureOccupancyMinutes), r.From);
    }

    [Fact]
    public void Una_ripartenza_troppo_lontana_non_e_la_stessa_rotazione()
    {
        var requests = RotationBuilder.Build(
            [Arrival("AZA100", 0), Departure("AZA100", 60 * 20)], "LIRN", Opt, T0);

        Assert.Equal(2, requests.Count);
    }

    [Fact]
    public void La_rotazione_si_riconosce_dal_VID_quando_il_callsign_cambia()
    {
        // Nel booking dell'evento il callsign cambia quasi sempre fra andata e ritorno,
        // ma il pilota che ha prenotato e' lo stesso.
        var arr = Arrival("LHX1876", 15) with { Vid = 782237 };
        var dep = Departure("LHX9999", 65) with { Vid = 782237 };

        var r = Assert.Single(RotationBuilder.Build([arr, dep], "LIRN", Opt, T0));

        Assert.Equal("LHX9999", r.Outbound!.Callsign);
        Assert.Equal(T0.AddMinutes(65), r.To);
    }

    [Fact]
    public void Il_VID_zero_o_assente_non_accoppia_voli_estranei()
    {
        // Nei dati reali molte prenotazioni hanno booked_by 0 o nullo: non identificano nessuno.
        foreach (int? vid in new int?[] { 0, null })
        {
            var arr = Arrival("RYR354V", 15) with { Vid = vid };
            var dep = Departure("DAL233", 65) with { Vid = vid };

            var requests = RotationBuilder.Build([arr, dep], "LIRN", Opt, T0);

            Assert.Equal(2, requests.Count);
        }
    }

    [Fact]
    public void Una_partenza_precedente_all_arrivo_non_e_una_rotazione_nemmeno_con_lo_stesso_VID()
    {
        var arr = Arrival("EJU63AY", 200) with { Vid = 250140 };
        var dep = Departure("EJU4127", 5) with { Vid = 250140 };

        Assert.Equal(2, RotationBuilder.Build([arr, dep], "LIRN", Opt, T0).Count);
    }

    [Fact]
    public void I_voli_di_altri_aeroporti_vengono_ignorati()
    {
        var legs = new[]
        {
            Arrival("AZA100", 0),
            new FlightLeg
            {
                Callsign = "AZA900", AircraftType = "A320",
                Departure = "LIRF", Arrival = "LIMC",
                Eta = T0, Kind = LegKind.Arrival,
            },
        };

        Assert.Single(RotationBuilder.Build(legs, "LIRN", Opt, T0));
    }
}
