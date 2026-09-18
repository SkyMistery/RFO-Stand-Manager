namespace RfoGateManager.Domain;

/// <summary>Un aereo fermo su uno stand, adesso. È un fatto, non un piano.</summary>
public sealed record PhysicalOccupant(
    string Callsign,
    string StandId,
    string Source,
    string? AircraftType = null);

/// <summary>
/// Porta nel piano quello che succede davvero sul piazzale. Un aereo parcheggiato occupa il
/// suo stand da adesso, che fosse previsto o no; chi è arrivato senza prenotazione diventa
/// un occupante a tutti gli effetti, così lo stand non viene promesso a qualcun altro.
/// </summary>
public static class Occupancy
{
    public static List<StandRequest> Apply(
        IReadOnlyList<StandRequest> requests,
        IReadOnlyCollection<PhysicalOccupant> occupants,
        DateTimeOffset now,
        AllocationOptions opt)
    {
        var byCallsign = occupants
            .GroupBy(o => o.Callsign, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var matched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<StandRequest>(requests.Count + occupants.Count);

        foreach (var r in requests)
        {
            // Un aereo in rotazione può essere online col callsign dell'andata o del ritorno.
            var occ = Find(byCallsign, r.Inbound?.Callsign)
                      ?? Find(byCallsign, r.Outbound?.Callsign)
                      ?? Find(byCallsign, r.Callsign);

            if (occ is null)
            {
                result.Add(r);
                continue;
            }

            matched.Add(occ.Callsign);

            // È lì adesso: l'occupazione parte da ora anche se il piano la dava più avanti,
            // e non finisce prima di adesso anche se il piano la dava già conclusa.
            var from = r.From < now ? r.From : now;
            var to = r.To > now ? r.To : now.AddMinutes(opt.UnscheduledOccupancyMinutes);

            result.Add(r with { ActualStand = occ.StandId, From = from, To = to });
        }

        foreach (var occ in occupants.Where(o => !matched.Contains(o.Callsign)))
        {
            var type = occ.AircraftType ?? "";
            var dims = AircraftCatalog.DimensionsOf(type);

            result.Add(new StandRequest
            {
                Key = $"occ:{occ.Callsign.ToUpperInvariant()}",
                Callsign = occ.Callsign.ToUpperInvariant(),
                AircraftType = type,
                Size = AircraftCatalog.SizeOf(type),
                Use = AircraftCatalog.IsBusinessOrGa(type, occ.Callsign) ? "ga" : "pax",
                WingspanM = dims?.WingspanM,
                LengthM = dims?.LengthM,
                // Non sappiamo quando se ne andrà: lo teniamo per una finestra che scorre con
                // il tempo. Finché resta lì, ogni ricalcolo la sposta in avanti.
                From = now,
                To = now.AddMinutes(opt.UnscheduledOccupancyMinutes),
                ActualStand = occ.StandId,
                Locked = true,
                Unscheduled = true,
            });
        }

        return result.OrderBy(r => r.From).ThenBy(r => r.Callsign, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// Lo stand su cui si trova una posizione, se ce n'è uno abbastanza vicino. Serve quando
    /// Aurora non c'è: con le coordinate del .gts ricaviamo lo stand dalla posizione Whazzup.
    /// </summary>
    public static string? NearestStand(double lat, double lon, IEnumerable<Stand> stands, double maxMeters)
    {
        string? best = null;
        var bestDistance = double.MaxValue;

        foreach (var s in stands)
        {
            if (s.Lat == 0 && s.Lon == 0) continue; // stand senza coordinate

            var d = DistanceMeters(lat, lon, s.Lat, s.Lon);
            if (d < bestDistance)
            {
                bestDistance = d;
                best = s.Id;
            }
        }

        return bestDistance <= maxMeters ? best : null;
    }

    /// <summary>Distanza sulla sfera, in metri. Per poche centinaia di metri è più che precisa.</summary>
    public static double DistanceMeters(double lat1, double lon1, double lat2, double lon2)
    {
        const double earthRadius = 6_371_000;
        var dLat = ToRad(lat2 - lat1);
        var dLon = ToRad(lon2 - lon1);
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                Math.Cos(ToRad(lat1)) * Math.Cos(ToRad(lat2)) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return 2 * earthRadius * Math.Asin(Math.Min(1, Math.Sqrt(a)));
    }

    private static double ToRad(double deg) => deg * Math.PI / 180;

    private static PhysicalOccupant? Find(Dictionary<string, PhysicalOccupant> map, string? callsign) =>
        callsign is not null && map.TryGetValue(callsign, out var o) ? o : null;
}
