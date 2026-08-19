using System.Reflection;
using Cocos.Application;
using Cocos.Domain.Entities;
using Cocos.Infrastructure;
using FluentAssertions;
using NetArchTest.Rules;

namespace Cocos.ArchitectureTests;

public class LayeringTests
{
    private static readonly Assembly DomainAssembly = typeof(Order).Assembly;
    private static readonly Assembly ApplicationAssembly = typeof(IApplicationMarker).Assembly;
    private static readonly Assembly InfrastructureAssembly = typeof(DependencyInjection).Assembly;

    [Fact]
    public void El_dominio_no_conoce_a_ninguna_otra_capa()
    {
        var result = Types.InAssembly(DomainAssembly)
            .ShouldNot()
            .HaveDependencyOnAny("Cocos.Application", "Cocos.Infrastructure", "Cocos.Api")
            .GetResult();

        result.IsSuccessful.Should().BeTrue(
            "el dominio es el centro: si depende de una capa externa deja de ser testeable en aislamiento. Violan: {0}",
            Describe(result));
    }

    [Fact]
    public void El_dominio_no_conoce_la_persistencia()
    {
        var result = Types.InAssembly(DomainAssembly)
            .ShouldNot()
            .HaveDependencyOnAny("Microsoft.EntityFrameworkCore", "Npgsql", "Dapper")
            .GetResult();

        result.IsSuccessful.Should().BeTrue(
            "las entidades no pueden saber como se guardan. Violan: {0}", Describe(result));
    }

    [Fact]
    public void La_capa_de_aplicacion_no_depende_de_infraestructura()
    {
        var result = Types.InAssembly(ApplicationAssembly)
            .ShouldNot()
            .HaveDependencyOn("Cocos.Infrastructure")
            .GetResult();

        result.IsSuccessful.Should().BeTrue(
            "la dependencia va al reves: Infrastructure implementa lo que Application declara. Violan: {0}",
            Describe(result));
    }

    [Fact]
    public void Ninguna_capa_de_negocio_depende_de_AspNetCore()
    {
        foreach (var assembly in new[] { DomainAssembly, ApplicationAssembly, InfrastructureAssembly })
        {
            var result = Types.InAssembly(assembly)
                .ShouldNot()
                .HaveDependencyOn("Microsoft.AspNetCore")
                .GetResult();

            result.IsSuccessful.Should().BeTrue(
                "{0} no puede acoplarse al framework web. Violan: {1}",
                assembly.GetName().Name, Describe(result));
        }
    }

    private static string Describe(TestResult result)
        => result.FailingTypeNames is null ? "-" : string.Join(", ", result.FailingTypeNames);
}
