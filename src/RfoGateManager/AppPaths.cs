namespace RfoGateManager;

/// <summary>
/// Trova le cartelle <c>data</c> e <c>secrets</c>. In esecuzione l'exe le ha accanto;
/// in sviluppo stanno alla radice del repository, qualche livello più in su.
/// </summary>
public static class AppPaths
{
    public static string Root { get; } = Resolve();

    public static string Data => Ensure(Path.Combine(Root, "data"));
    public static string Secrets => Ensure(Path.Combine(Root, "secrets"));

    private static string Resolve()
    {
        var baseDir = AppContext.BaseDirectory;

        // Accanto all'eseguibile: è il caso normale per l'utente finale.
        if (Directory.Exists(Path.Combine(baseDir, "data")) ||
            Directory.Exists(Path.Combine(baseDir, "secrets")))
            return baseDir;

        // In sviluppo risaliamo fino alla radice della soluzione. Cerchiamo il file di
        // soluzione e non le cartelle data/secrets: il progetto ha già una cartella
        // sorgente 'Data' che su Windows, dove i nomi non distinguono maiuscole,
        // verrebbe scambiata per la cartella dei dati.
        var dir = new DirectoryInfo(baseDir);
        for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            if (HasSolutionFile(dir.FullName)) return dir.FullName;
        }

        return baseDir;
    }

    private static bool HasSolutionFile(string directory)
    {
        try
        {
            return Directory.EnumerateFiles(directory)
                .Any(f => f.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)
                       || f.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase));
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string Ensure(string path)
    {
        Directory.CreateDirectory(path);
        return path;
    }
}
