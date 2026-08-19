using Cocos.Application.Common;
using Dapper;

namespace Cocos.Application.Features.Instruments.SearchInstruments;

public static class SearchInstrumentsHandler
{
    // El patron viaja SIEMPRE como parametro, nunca concatenado en el SQL: concatenar la
    // entrada del usuario es inyeccion directa (OWASP A03). Los comodines se agregan del
    // lado del codigo, no del SQL, para que el valor siga siendo un dato y no sintaxis.
    //
    // ILIKE '%texto%' no puede aprovechar un btree; por eso la migracion crea un indice
    // GIN trigram sobre ticker y name. Sin el, cada busqueda es un full scan de la tabla.
    private const string WhereClause =
        """
        WHERE @Pattern IS NULL
           OR ticker ILIKE @Pattern
           OR name   ILIKE @Pattern
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
            Pattern = term is null ? null : $"%{term}%",
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
