using System.Globalization;
using RfoGateManager.Domain;

namespace RfoGateManager.Data;

/// <summary>
/// Legge i file .gts di Aurora (sezione [GATES]): <c>ID;ICAO;LAT;LON;[type];</c>.
/// Tollerante sull'ordine dei campi e sul formato delle coordinate, perché i sector
/// file in giro non sono tutti uguali.
/// </summary>
public static class GtsParser
{
    public static List<Stand> ParseFile(string path, string? expectedIcao = null)
        => Parse(File.ReadAllLines(path), expectedIcao);

    public static List<Stand> Parse(IEnumerable<string> lines, string? expectedIcao = null)
    {
        var stands = new List<Stand>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var raw in lines)
        {
            var line = StripComment(raw);
            if (line.Length == 0) continue;

            // Alcuni file portano l'intestazione di sezione: la saltiamo.
            if (line.StartsWith('[')) continue;

            var f = line.Split(';', StringSplitOptions.TrimEntries);
            if (f.Length < 4) continue;

            var id = f[0];
            if (id.Length == 0) continue;

            // Individua l'ICAO (4 lettere) e la coppia di coordinate, ovunque siano.
            var icao = f.Skip(1).FirstOrDefault(x => x.Length == 4 && x.All(char.IsLetter)) ?? expectedIcao ?? "";

            var coords = new List<double>();
            var coordIdx = new List<int>();
            for (var i = 1; i < f.Length; i++)
            {
                if (TryParseCoordinate(f[i], out var v)) { coords.Add(v); coordIdx.Add(i); }
            }
            if (coords.Count < 2) continue;

            var lat = coords[0];
            var lon = coords[1];
            // Se il primo valore non può essere una latitudine, i due sono invertiti.
            if (Math.Abs(lat) > 90 && Math.Abs(lon) <= 90) (lat, lon) = (lon, lat);
            if (Math.Abs(lat) > 90 || Math.Abs(lon) > 180) continue;

            if (expectedIcao is not null && icao.Length == 4 &&
                !icao.Equals(expectedIcao, StringComparison.OrdinalIgnoreCase))
                continue;

            // Il campo tipo è il primo non vuoto dopo le coordinate.
            var afterCoords = coordIdx[^1] + 1;
            var type = afterCoords < f.Length && f[afterCoords].Length > 0 ? f[afterCoords] : null;

            if (!seen.Add(id)) continue;

            stands.Add(new Stand
            {
                Id = id,
                Icao = icao.ToUpperInvariant(),
                Lat = lat,
                Lon = lon,
                GtsType = type,
            });
        }

        return stands;
    }

    private static string StripComment(string raw)
    {
        var s = raw;
        var idx = s.IndexOf("//", StringComparison.Ordinal);
        if (idx >= 0) s = s[..idx];
        if (s.TrimStart().StartsWith(';')) return "";
        return s.Trim();
    }

    /// <summary>
    /// Accetta decimale (<c>40.886</c>, <c>-14.29</c>) e il formato Aurora/sector file
    /// (<c>N040.53.02.000</c>, <c>E014.17.14.000</c>, <c>N40 53 02.0</c>).
    /// </summary>
    public static bool TryParseCoordinate(string? text, out double value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var s = text.Trim();

        var sign = 1.0;
        var hemi = char.ToUpperInvariant(s[0]);
        if (hemi is 'N' or 'S' or 'E' or 'W')
        {
            if (hemi is 'S' or 'W') sign = -1;
            s = s[1..].Trim();
        }
        else
        {
            hemi = char.ToUpperInvariant(s[^1]);
            if (hemi is 'N' or 'S' or 'E' or 'W')
            {
                if (hemi is 'S' or 'W') sign = -1;
                s = s[..^1].Trim();
            }
        }

        if (s.Length == 0) return false;

        var parts = s.Split([' ', '.', ':'], StringSplitOptions.RemoveEmptyEntries);

        // Decimale puro: "40.886" -> 2 parti, ma anche "-14" -> 1 parte.
        if (parts.Length <= 2 && s.Count(c => c == '.') <= 1 && !s.Contains(' ') && !s.Contains(':'))
        {
            if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var dec)) return false;
            value = sign < 0 && dec > 0 ? -dec : dec;
            return true;
        }

        // Sessagesimale: DDD MM SS[.mmm]
        if (parts.Length < 3) return false;
        if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var deg)) return false;
        if (!double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var min)) return false;
        if (!double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var sec)) return false;

        // "N040.53.02.000": il quarto pezzo sono i millesimi di secondo.
        if (parts.Length >= 4 && double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var milli))
            sec += milli / 1000.0;

        if (min >= 60 || sec >= 60) return false;

        var abs = Math.Abs(deg) + min / 60.0 + sec / 3600.0;
        value = (deg < 0 ? -1 : sign) * abs;
        return true;
    }
}
