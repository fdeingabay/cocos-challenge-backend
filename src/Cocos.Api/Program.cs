using System.Text.Json.Serialization;
using JasperFx.CodeGeneration.Model;
using Cocos.Api.Infrastructure;
using Cocos.Application;
using Cocos.Application.Common;
using Cocos.Infrastructure;
using FluentValidation;
using Microsoft.OpenApi;
using Wolverine;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        // Los enums viajan como los literales que ya usa la base ("BUY", "LIMIT", "FILLED"),
        // no como numeros: un contrato de API con enteros magicos es ilegible para el cliente.
        options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
    });
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "Cocos Trading API",
        Version = "v1",
        Description = "Portfolio, busqueda de instrumentos y envio de ordenes al mercado."
    });

    var xml = Path.Combine(AppContext.BaseDirectory, $"{typeof(Program).Assembly.GetName().Name}.xml");
    if (File.Exists(xml)) options.IncludeXmlComments(xml);
});

builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();

// TimeProvider en vez de DateTime.Now: sin esto la expiracion de ordenes es intesteable
// y el codigo queda acoplado al reloj de la maquina.
builder.Services.AddSingleton(TimeProvider.System);

builder.Services.AddInfrastructure(
    builder.Configuration.GetConnectionString("Cocos")
    ?? throw new InvalidOperationException("Falta la connection string 'Cocos'."));

builder.Services.AddValidatorsFromAssemblyContaining<IApplicationMarker>();

// Wolverine como mediador in-process. Reemplaza a MediatR y ademas resuelve el
// scheduling del job de expiracion sin sumar otra dependencia.
builder.Host.UseWolverine(options =>
{
    options.Discovery.IncludeAssembly(typeof(IApplicationMarker).Assembly);

    // Wolverine genera el codigo de invocacion de los handlers en compilacion y por defecto
    // prohibe resolver dependencias por service location. EF Core registra
    // DbContextOptions<T> con un factory desde AddDbContext, que no se puede expresar de
    // otra forma, asi que se habilita explicitamente. Es una decision consciente, no un
    // descuido: sin esto todo handler que toque el DbContext falla en runtime.
    options.ServiceLocationPolicy = ServiceLocationPolicy.AlwaysAllowed;
});

builder.Services.AddHostedService<OrderExpirationService>();

var app = builder.Build();

app.UseExceptionHandler();

app.UseSwagger();
app.UseSwaggerUI(o => o.SwaggerEndpoint("/swagger/v1/swagger.json", "Cocos Trading API v1"));

// Abrir la raiz lleva directo a la documentacion: el revisor no tiene que adivinar la ruta.
app.MapGet("/", () => Results.Redirect("/swagger"))
   .ExcludeFromDescription();

app.MapControllers();

await app.RunAsync();

// Requerido por WebApplicationFactory en los tests de integracion.
public partial class Program;
