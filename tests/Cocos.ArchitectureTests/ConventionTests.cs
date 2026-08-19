using System.Reflection;
using Cocos.Application;
using FluentAssertions;

namespace Cocos.ArchitectureTests;

public class ConventionTests
{
    private static readonly Assembly ApplicationAssembly = typeof(IApplicationMarker).Assembly;

    [Fact]
    public void Los_contratos_publicos_son_records_inmutables()
    {
        var contracts = ApplicationAssembly.GetTypes()
            .Where(t => t is { IsClass: true, IsPublic: true })
            .Where(t => t.Name.EndsWith("Response") || t.Name.EndsWith("Query") || t.Name.EndsWith("Command"))
            .ToList();

        contracts.Should().NotBeEmpty("si no encuentra contratos, el test no esta probando nada");

        var noSonRecords = contracts.Where(t => !IsRecord(t)).Select(t => t.Name).ToList();

        noSonRecords.Should().BeEmpty(
            "los contratos que cruzan la frontera de la API tienen que ser inmutables");
    }

    [Fact]
    public void Los_contratos_no_tienen_setters_publicos()
    {
        var setters = ApplicationAssembly.GetTypes()
            .Where(t => t is { IsClass: true, IsPublic: true })
            .Where(t => t.Name.EndsWith("Response") || t.Name.EndsWith("Query") || t.Name.EndsWith("Command"))
            .SelectMany(t => t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                              .Where(p => p.SetMethod is { IsPublic: true } && !IsInitOnly(p))
                              .Select(p => $"{t.Name}.{p.Name}"))
            .ToList();

        setters.Should().BeEmpty("un contrato mutable se puede modificar despues de validarlo");
    }

    // Un setter 'init' tambien es publico a nivel reflection: se distingue por el
    // modificador requerido IsExternalInit que emite el compilador. Solo interesa
    // detectar los setters de verdad, los que permiten mutar despues de construir.
    private static bool IsInitOnly(PropertyInfo property)
        => property.SetMethod is not null
           && property.SetMethod.ReturnParameter
                      .GetRequiredCustomModifiers()
                      .Any(modifier => modifier.Name == "IsExternalInit");

    // Un record siempre expone EqualityContract generado por el compilador.
    private static bool IsRecord(Type type)
        => type.GetProperty("EqualityContract", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public) is not null;
}
