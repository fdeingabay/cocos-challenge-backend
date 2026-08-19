using System.Text.RegularExpressions;
using FluentAssertions;

namespace Cocos.ArchitectureTests;

/// <summary>
/// Esta regla se verifica sobre el codigo fuente y no por reflection a proposito:
/// DateTime.Now se compila a una llamada estatica que no deja rastro en la superficie
/// de tipos, asi que ningun analisis de dependencias entre ensamblados puede detectarla.
/// </summary>
public partial class ClockUsageTests
{
    [GeneratedRegex(@"\bDateTime\s*\.\s*(Now|UtcNow|Today)\b")]
    private static partial Regex AmbientClock();

    [Fact]
    public void Ningun_archivo_de_produccion_lee_el_reloj_directamente()
    {
        var sourceRoot = Path.Combine(FindSolutionRoot(), "src");
        Directory.Exists(sourceRoot).Should().BeTrue("el test necesita encontrar el codigo fuente");

        var offenders = Directory
            .EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .SelectMany(path => File.ReadLines(path)
                .Select((line, index) => (path, line, number: index + 1))
                .Where(x => AmbientClock().IsMatch(StripComment(x.line))))
            .Select(x => $"{Path.GetFileName(x.path)}:{x.number}")
            .ToList();

        offenders.Should().BeEmpty(
            "el tiempo se inyecta con TimeProvider. Leer el reloj ambiente vuelve intesteable "
            + "todo lo que dependa de el -- empezando por el vencimiento de las ordenes LIMIT");
    }

    /// <summary>
    /// Descarta el comentario de linea antes de buscar. Sin esto, un comentario que
    /// explica por que NO se usa DateTime.Now hace fallar la regla que documenta.
    /// </summary>
    private static string StripComment(string line)
    {
        var comment = line.IndexOf("//", StringComparison.Ordinal);
        return comment < 0 ? line : line[..comment];
    }

    private static string FindSolutionRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        // .NET 10 genera el formato .slnx; se aceptan ambos para no atar el test al SDK.
        while (directory is not null && !directory.EnumerateFiles("Cocos.sln*").Any())
            directory = directory.Parent;

        return directory?.FullName
            ?? throw new InvalidOperationException("No se encontro la raiz de la solucion (Cocos.sln / Cocos.slnx).");
    }
}
