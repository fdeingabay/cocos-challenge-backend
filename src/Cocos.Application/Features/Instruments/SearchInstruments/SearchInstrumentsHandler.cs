using Cocos.Application.Common;
using Dapper;

namespace Cocos.Application.Features.Instruments.SearchInstruments;

/// <summary>
/// Búsqueda de instrumentos por ticker o nombre. Sin término devuelve el listado completo,
/// siempre paginado.
/// </summary>
public static class SearchInstrumentsHandler
{
    // El patrón viaja SIEMPRE por parámetro, nunca concatenado: concatenar la entrada del
    // usuario es inyeccion directa (OWASP A03). Parametrizar tampoco alcanza para que el termino
    // sea un dato -- dentro de un ILIKE sigue siendo sintaxis -- asi que va escapado por
    // LikePattern: sin eso, buscar "S_A" devuelve los 39 instrumentos que dicen "S.A.".
    //
    // El ESCAPE '\' es el default de Postgres y se declara igual, para que la sentencia diga cual
    // es el caracter de escape en vez de dejarlo implicito.
    //
    // ILIKE '%texto%' no puede aprovechar un btree, asi que la migracion crea un indice GIN
    // trigram. Sin el, cada búsqueda es un full scan.
    //
    // f_unaccent va en los DOS lados: nadie escribe las tildes en un buscador, asi que
    // "zorraquin" tiene que encontrar "Zorraquin S.A.", y aplicarlo tambien sobre el patrón deja
    // funcionando al termino que si las trae. El indice esta creado sobre esta misma expresion:
    // si la consulta y el indice dejan de coincidir literalmente, el planner lo descarta.
    private const string WhereClause =
        """
        WHERE @Pattern IS NULL
           OR f_unaccent(ticker) ILIKE f_unaccent(@Pattern) ESCAPE '\'
           OR f_unaccent(name)   ILIKE f_unaccent(@Pattern) ESCAPE '\'
        """;

    public static async Task<Result<PagedResult<InstrumentResponse>>> Handle(
        SearchInstrumentsQuery query,
        IDbConnectionFactory connectionFactory,
        CancellationToken cancellationToken)
    {
        var page = Paging.NormalizePage(query.Page);
        var pageSize = Paging.NormalizePageSize(query.PageSize);

        var term = string.IsNullOrWhiteSpace(query.Search) ? null : query.Search.Trim();
        var parameters = new
        {
            Pattern = term is null ? null : LikePattern.Contains(term),
            Take = pageSize,
            Skip = (page - 1) * pageSize
        };

        await using var connection = await connectionFactory.OpenAsync(cancellationToken);

        var total = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            $"SELECT COUNT(*) FROM instruments {WhereClause};",
            parameters, cancellationToken: cancellationToken));

        var items = await connection.QueryAsync<InstrumentResponse>(new CommandDefinition(
            $"""
             SELECT id AS Id, ticker AS Ticker, name AS Name, type AS Type
             FROM instruments
             {WhereClause}
             ORDER BY ticker
             LIMIT @Take OFFSET @Skip;
             """,
            parameters, cancellationToken: cancellationToken));

        return new PagedResult<InstrumentResponse>(items.ToList(), page, pageSize, total);
    }
}
