using System.Reflection;
using Cocos.Application;
using FluentAssertions;

namespace Cocos.ArchitectureTests;

/// <summary>
/// Wolverine genera en compilacion el código que invoca a los handlers, y para eso exige que
/// el tipo, su constructor y el metodo sean publicos. Nada de eso lo verifica el compilador:
/// un handler mal declarado simplemente no se descubre, o revienta al construirse, y el
/// sintoma aparece recien en runtime y lejos de la causa. Estos tests lo fijan.
/// </summary>
public class HandlerConventionTests
{
    private static readonly Assembly ApplicationAssembly = typeof(IApplicationMarker).Assembly;

    private static List<Type> Handlers() => ApplicationAssembly.GetTypes()
        .Where(t => t.IsClass && t.Name.EndsWith("Handler"))
        .ToList();

    [Fact]
    public void Los_handlers_son_publicos()
    {
        var handlers = Handlers();
        handlers.Should().NotBeEmpty("si no encuentra handlers, el test no esta probando nada");

        handlers.Where(t => !t.IsPublic).Select(t => t.Name)
            .Should().BeEmpty("Wolverine referencia el tipo desde el código que genera");
    }

    [Fact]
    public void Los_handlers_exponen_un_metodo_Handle_publico()
    {
        var sinHandle = Handlers()
            .Where(t => !t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
                          .Any(m => m.Name is "Handle" or "HandleAsync"))
            .Select(t => t.Name)
            .ToList();

        sinHandle.Should().BeEmpty("es la convencion por la que Wolverine descubre el handler");
    }

    [Fact]
    public void Los_handlers_de_instancia_tienen_constructor_publico()
    {
        // Los handlers estaticos resuelven sus dependencias por parámetro y no tienen
        // constructor; los de instancia las reciben por constructor, y Wolverine lo invoca
        // desde el código generado. Una clase estatica es abstract Y sealed a la vez: eso
        // es lo que la distingue de una simplemente sealed, como los de instancia.
        var sinConstructorPublico = Handlers()
            .Where(t => !(t.IsAbstract && t.IsSealed))
            .Where(t => t.GetConstructors(BindingFlags.Public | BindingFlags.Instance).Length == 0)
            .Select(t => t.Name)
            .ToList();

        sinConstructorPublico.Should().BeEmpty();
    }
}
