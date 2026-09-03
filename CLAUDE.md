# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Qué es este repo

Resolución del challenge backend de Cocos Capital: una API de portfolio y envío de órdenes
al mercado. .NET 10 + PostgreSQL 16. El enunciado original está en
`cocos-challenge-backend.md` (pide Node.js; se resolvió en .NET a propósito).

Documentación de referencia — **leerla antes de cambiar reglas de negocio**:

- `docs/DOCUMENTACION.md` — documentación técnica y funcional completa (endpoints, fórmulas,
  concurrencia, decisiones). Es la fuente de verdad del comportamiento esperado.
- `README.md` — cómo ejecutarlo, supuestos y justificación de cada cambio de esquema.

## Comandos

```bash
# Todo (Postgres con esquema y datos + API en http://localhost:8080, Swagger en la raíz)
docker compose up --build

# Las tres suites, contra el Postgres del compose (no necesita socket de Docker)
docker compose --profile test run --rm tests

# Desarrollo local (requiere .NET 10 SDK)
docker compose up -d db
dotnet build Cocos.slnx
dotnet run --project src/Cocos.Api          # http://localhost:5080
dotnet test Cocos.slnx

# Una sola suite / un solo test
dotnet test tests/Cocos.UnitTests
dotnet test tests/Cocos.IntegrationTests --filter "FullyQualifiedName~ConcurrencyTests"
dotnet test tests/Cocos.UnitTests --filter "FullyQualifiedName~OrderMathTests.SizeFromAmount_trunca_siempre_hacia_abajo"
```

Los tests de integración levantan Postgres con TestContainers. Si hay un servidor disponible,
`COCOS_TEST_DB="Host=...;Username=...;Password=..."` lo usa y TestContainers ni se carga
(del connection string solo se toman host y credenciales: la suite crea su propia base
plantilla y saca una copia por clase con `CREATE DATABASE ... TEMPLATE`).
Desde WSL hacia un `dotnet` de Windows la variable no cruza sola: hace falta
`WSLENV="COCOS_TEST_DB"`.

## Arquitectura

Vertical Slice sobre capas Clean. Dependencias: `Domain` ← `Application` ← `Infrastructure` ← `Api`.
Hay tests de arquitectura que fallan si esto se rompe.

- **`Cocos.Domain`** — entidades, enums, `OrderMath` (aritmética monetaria) y `DbValues`
  (traducción enum ↔ literal de la base). Sin dependencias.
- **`Cocos.Application`** — un directorio por caso de uso en `Features/<Área>/<CasoDeUso>/`,
  con Command/Query + Handler + Response (+ Validator) juntos. Casi todos los handlers son
  **clases estáticas** con un método `Handle` cuyas dependencias entran **por parámetro**:
  Wolverine las inyecta. La excepción es `SubmitOrderHandler`, que es una **clase de
  instancia con inyección por constructor** — ver más abajo.
- **`Cocos.Infrastructure`** — `CocosDbContext` + `IEntityTypeConfiguration` por entidad,
  `NpgsqlConnectionFactory` para Dapper, y `Orders/` con la implementación del lock de cuenta.
- **`Cocos.Api`** — controllers finos que solo arman el comando, hacen
  `bus.InvokeAsync<Result<T>>` y traducen el `Result` a HTTP.

**Escritura con EF Core, lectura con Dapper.** Las queries de lectura son agregaciones que
proyectan directo a records inmutables. No hay Repository, Unit of Work, AutoMapper ni MediatR
y no deben agregarse: `DbContext` ya es la unidad de trabajo, `ICocosDbContext` existe solo
para invertir la dependencia entre capas.

**Result Pattern.** El flujo de negocio no usa excepciones. `Error` tiene tres tipos
(`Validation`/`NotFound`/`Conflict`) que `ResultExtensions.ToProblem()` mapea a 400/404/409.
Una excepción en este codebase significa un fallo genuinamente inesperado.

## Lo que no hay que romper

**El invariante.** No existe saldo almacenado: cash y tenencia son una **proyección** de
`orders`, que cumple tres roles a la vez (ledger de ejecutadas, libro de pendientes, log de
`CASH_IN`/`CASH_OUT`). El sistema garantiza siempre:

```
disponible = contable − Σ(BUY  vivas: (size − filledsize) × price)   >= 0
tenencia   = ejecutado − Σ(SELL vivas: (size − filledsize))          >= 0
```

El segundo término (la **reserva**) es la parte que se olvida con facilidad. Omitirlo hace que
la API informe $753.000 disponibles cuando en realidad son $627.500 con el seed provisto.

**Ese invariante está escrito una sola vez, en `LedgerSql` (`Application/Common`).** Los cuatro
fragmentos (`AccountingCash`, `ReservedCash`, `ExecutedQuantity`, `ReservedQuantity`) los
comparten los dos lados que tienen que coincidir siempre: `GetPortfolioHandler`, que **informa**
el disponible, y `AccountSql`, que **decide** contra él. Estaban duplicados palabra por palabra
en dos capas distintas — la clase de duplicación que no falla al divergir: la API informa un
número y el sistema acepta otro. No volver a inlinear ninguna de las dos cuentas.

Los fragmentos agregan sobre `orders` **sin alias de tabla** (por eso el CTE de `PositionsSql`
tampoco lo usa) y esperan un único parámetro, `@OpenStatuses`, que sale de `DbValues`.
`PortfolioTests` fija la costura con dos tests que gastan exactamente el disponible informado y
un peso más: si las dos lecturas divergen, fallan.

**Lo ejecutado no filtra por estado, y esa ausencia es la corrección de un bug con plata adentro.**
`filledsize` **es** lo ejecutado, por definición: una `NEW` o una `REJECTED` lo tienen en cero, y
ningún estado terminal deshace una ejecución. Antes había un `ExecutedStatuses` = `{FILLED,
PARTIALLY_FILLED}` en los cuatro fragmentos, y al cancelar o vencer una orden a medio ejecutar el
`filledsize` dejaba de contar **en las cuatro cuentas a la vez**: el usuario recuperaba los pesos
de acciones que sí había comprado y la posición desaparecía. Que las cuatro coincidieran no
salvaba nada — coincidían en el número equivocado. No volver a agregar ese filtro; si hace falta
excluir algo, el lugar es `filledsize`, no el estado.

Lo que vuelve segura esa lectura es un `CHECK` de la V2 (`ck_orders_filled_solo_si_ejecuto`): una
orden `NEW` o `REJECTED` no puede tener `filledsize > 0`. `PartialFillTests` fija las tres reglas
del ciclo de vida parcial — informar, cancelar el remanente y vencerlo — y falla con la diferencia
exacta si alguien reintroduce el filtro.

**El lock de cuenta.** `POST /api/orders` hace todo dentro de una transacción cuyo primer paso
es `SELECT 1 FROM user_accounts WHERE userid = @u FOR UPDATE`. La tabla `user_accounts` existe
únicamente para eso: el conflicto entre dos órdenes concurrentes vive en una **suma**, no en
ninguna fila, así que ni `REPEATABLE READ` lo detecta (write skew). Validar e insertar tienen
que quedar dentro del mismo lock.

El lock está modelado como objeto: `IAccountLedger.LockAsync` abre la transacción y devuelve
un `IAccountLock`, cuya vida **es** la vida del lock. Todas las lecturas de estado de la cuenta
cuelgan de él, así que consultar el disponible fuera del lock no es posible por construcción.
No es un Unit of Work: no trackea entidades (`DbContext` sigue haciendo eso) ni abstrae la
persistencia — modela un `SELECT ... FOR UPDATE`, que es algo que `DbContext` no representa.

**`AccountLedger` e `InstrumentReader` reciben `ICocosDbContext`, nunca `IDbConnectionFactory`.**
En Postgres la transacción es una propiedad de la **sesión**: una conexión distinta es otra
sesión, no ve el lock y deja las lecturas del disponible fuera de él. `ICocosDbContext` está
registrado como scoped, así que el handler y sus colaboradores comparten la misma conexión y la
misma transacción. Con el factory todo compila y el invariante se rompe en silencio.

**`CancellationToken.None` en el commit.** El token del request se propaga por todo el call
stack como último parámetro — excepto en el `SaveChangesAsync` y el `CommitAsync` de
`AccountLock.CommitAsync()`: que el cliente corte la conexión no puede dejar una orden aplicada
a medias. Por eso `IAccountLock.CommitAsync()` **no recibe `CancellationToken`**: la firma
impide "arreglarlo" pasando el token. Tampoco tragar `OperationCanceledException` como
excepción genérica: `GlobalExceptionHandler` la trata aparte y devuelve 499.

**El PPP necesita un recorrido ordenado, no una agregación.** `PositionsSql` tiene un
`WITH RECURSIVE` que camina los movimientos ejecutados en orden cronológico. No es adorno:
promediar todas las compras de la historia coincide con el PPP sólo mientras no haya ventas, y
al cerrar y reabrir una posición informa un costo que nadie pagó. Una venta reduce el costo en
la misma proporción que la tenencia —el promedio no se mueve, y al cerrar vuelve a cero—, y eso
es multiplicativo: no hay forma cerrada. No reemplazarlo por un `SUM`.

**Cancelar es un UPDATE condicional** con el estado en el `WHERE` y verificación de
`rowsAffected == 1`. Así la doble liberación de reserva es imposible por construcción. No
reemplazarlo por un read-modify-write.

Ese mismo `UPDATE` escribe `cancelledat`. No es un adorno: la respuesta informa el instante, y
antes no quedaba en ningún lado, así que la API devolvía una fecha que nadie podía reproducir. El
*cuándo* y el *qué* son un solo hecho y se escriben juntos o no se escriben. `Order.CancelledAt`
existe sólo para leerlo — el dominio no lo setea, `Cancel()` produce el hecho y no lo aplica.

Ese caso de uso está partido en dos mitades que no se conocen entre sí, y esa separación es
deliberada. La **decisión** vive en el dominio: `Order.Cancel()` no muta la orden, produce un
`OrderCancellation` — el hecho ya decidido y todavía no registrado. La **garantía** vive en
`OrderBookSql.CancelIfOpen`, que registra ese hecho sólo si la orden sigue viva. Por eso
`CancelOrderHandler` pregunta dos veces lo mismo y las dos hacen falta: `CanBeCancelled` para
poder *explicar* el 409 con el estado real, y el `WHERE` para *garantizar* que la reserva no se
libere dos veces. La primera puede quedar obsoleta entre la lectura y la escritura; la segunda no.

**El vencimiento es la misma idea aplicada a un conjunto.** `ExpireOrders` no carga entidades
—cargar N órdenes para vencerlas una por una sería un N+1 disfrazado de pureza—: vencer no es una
decisión *por orden* sino un **criterio evaluado a un instante**, y ese criterio es `OrderExpiry`.
La regla por entidad existe igual (`Order.HasExpired(now)`) y hay un test de integración que fija
que el barrido venza exactamente las órdenes para las que es `true`.

Los estados vivos ya **no** son un literal del SQL en ninguna de las dos escrituras: viajan por
parámetro desde `DbValues.OpenStatuses`, y hay un test que fija que esa lista coincida con la
noción de `Order.IsOpen`. La regla de negocio se manda a la base en vez de estar escrita dos veces.

`IOrderBook.ApplyAsync()`, `IOpenOrders.ApplyAsync()` e `IAccountLock.CommitAsync()` **no reciben
`CancellationToken`**, y `ExpireOrdersHandler` directamente no lo toma como parámetro. Es la regla
general del repo y ya no tiene excepciones: **la escritura que consume una decisión de negocio no
toma el token del request**, y la firma lo impone en vez de confiarlo a un comentario.

Ni cancelar ni vencer **necesitan el lock de cuenta**: su conflicto vive en filas concretas y no
en una suma, y eso Postgres lo defiende solo.

**`IDbConnectionFactory` es sólo lectura, sin excepciones.** Lo dice su propio doc comment y ahora
es cierto: toda escritura pasa por `ICocosDbContext`. Si aparece un `ExecuteAsync` colgado del
factory, es un bug.

## Trampas específicas de este proyecto

- **Los identificadores de la base son minúsculas.** El DDL provisto declara las columnas sin
  comillas y Postgres las pliega: las columnas reales son `instrumentid`, `previousclose`,
  `accountnumber`. Cada propiedad se mapea con `HasColumnName` explícito; confiar en la
  convención PascalCase de EF falla recién en runtime.
- **Las columnas de fecha son `timestamp without time zone`.** Hay que declarar
  `.HasColumnType("timestamp without time zone")` junto al converter a `DateTimeKind.Unspecified`,
  o Npgsql asume `timestamptz` y tira `ArgumentException` al escribir.
- **`marketdata` tiene `date DATE`, no `datetime`**, aunque el enunciado diga lo contrario.
- **Un `DateTime` que va a Dapper necesita `TimestampConverters.ToDb()`.** Dapper no pasa por los
  converters de EF, y Npgsql infiere `timestamptz` de un `Kind=Utc`. Contra una columna sin zona
  Postgres resuelve la diferencia con el **TimeZone de la sesión**: correcto mientras el server
  esté en UTC, silenciosamente corrido si no. Aplica tanto al escribir (`cancelledat`) como al
  comparar (`expiresat <= @AsOf`).
- **Los literales de la base no se tocan** (`BUY`, `CASH_IN`, `PARTIALLY_FILLED`, …). La
  conversión vive en `DbValues`; los enums viajan por JSON como esos mismos literales.
- **Wolverine**: necesita `WolverineFx.RuntimeCompilation` (ya no viene incluido) y
  `ServiceLocationPolicy.AlwaysAllowed`, porque `AddDbContext` registra `DbContextOptions<T>`
  con un factory que no se puede expresar de otra forma. Además los tipos que resuelve
  (`NpgsqlConnectionFactory`) tienen que ser **públicos**: el código generado los referencia.
  Registrar servicios con `AddScoped<Interfaz, Implementación>()`, no con lambdas opacas.
- **Nada de `DateTime.Now`/`UtcNow`** en producción: se usa `TimeProvider`. Hay un test de
  arquitectura que escanea el fuente y falla si aparece.

## Cambios de esquema

`db/01-database.sql` es el archivo provisto por el challenge y **no se modifica nunca**. Todo
cambio va a `db/02-V2__challenge.sql`, con un comentario que lo justifique en el propio archivo
(el enunciado exige justificar cada modificación). Los scripts corren en orden alfabético al
crear el volumen de Postgres, así que un cambio de esquema requiere `docker compose down -v`.

## Convenciones

- Comentarios, mensajes de error y documentación en **español, sin tildes en los comentarios
  del código** (así está escrito todo el codebase). Los nombres de tests son frases en español.
- Los comentarios explican **por qué**, no qué. La densidad actual es alta a propósito: es un
  challenge y las decisiones se van a discutir en una entrevista.
- DTOs y contratos son `record` inmutables; hay un test de arquitectura que lo verifica.
- Toda búsqueda pagina (`Paging.DefaultPageSize` 20, `MaxPageSize` 100) y parametriza el
  `ILIKE`; nunca concatenar SQL. Parametrizar **no alcanza**: dentro de un `LIKE` el valor
  sigue siendo sintaxis, así que el término va por `LikePattern.Contains()`, que escapa `%`,
  `_` y la barra. Sin eso, buscar `S_A` devuelve los 39 instrumentos cuyo nombre dice "S.A.".
  La búsqueda además **normaliza acentos en los dos lados** con `f_unaccent()`, el envoltorio
  `IMMUTABLE` que crea la V2: `zorraquin` tiene que encontrar `Zorraquín`. El índice GIN trigram
  está creado sobre **esa misma expresión** — si la consulta y el índice dejan de coincidir
  literalmente, el planner descarta el índice y cada búsqueda vuelve a ser un full scan.

## Comportamientos deliberados que parecen bugs

- Una orden sin fondos devuelve **201 con `status: REJECTED`**, no 400: el request se procesó
  bien y la orden se persiste como pide el enunciado.
- Un reintento con la misma `Idempotency-Key` devuelve **200, no 201**: no creó nada. Un 201 le
  afirma al cliente que acaba de dar de alta una orden, y contar altas contaría dos veces la misma
  compra — justo lo que la clave existe para impedir. El handler devuelve `SubmitOrderOutcome`,
  que distingue los dos desenlaces exitosos; traducirlos a HTTP es tarea del controller. No
  colapsar los dos casos en un solo código.
- Un monto que no alcanza para una acción devuelve **400** y no persiste nada: es una orden de
  tamaño cero, que no llega a formarse.
- El usuario 1 del seed tiene una **posición negativa en BMA (−10)**. Está en los datos
  provistos; no se tocó el seed y hay un test que fija ese comportamiento.
- **Ninguna operación produce una orden `PARTIALLY_FILLED`, y está bien así.** El estado está
  soportado de punta a punta y `PartialFillTests` lo verifica, pero producirlo requiere un motor
  de matching y el enunciado dice que no hace falta simular el mercado. No "arreglarlo" agregando
  fills sintéticos, ni sacar el estado porque no se alcanza: `filledsize` se paga por separar lo
  solicitado de lo ejecutado —sin eso la reserva de una orden viva no se calcula— y la aritmética
  del remanente ya está escrita y probada.
- El ARS es un instrumento `MONEDA` y **no aparece como posición**: se informa como
  `availableCash`.
- `userId` es **obligatorio** en `GET /api/orders/{id}` y en el cancel (`[BindRequired]`). Sin
  eso se bindea a 0 y la respuesta es un 404 que dice "no existe la orden N para el usuario 0"
  — un 404 que miente, porque la orden existe. Falta un parámetro: eso es 400.
- `GET /api/users/{id}/orders?status=BANANA` devuelve **400**, no una lista vacía, y un `userId`
  inexistente devuelve **404**. "No hay órdenes en ese estado" y "ese estado no existe" son
  respuestas distintas: la lista vacía hace pasar un error del cliente por un resultado. El
  filtro se convierte en dominio (`OrderStatusFilter`) antes de tocar la base. No "arreglarlo"
  al revés.
- **El primer barrido de vencimiento vence las dos órdenes `NEW` del seed** (ids 5 y 7, de julio
  de 2023) y libera $125.500 de reserva — justo la diferencia entre los $753.000 contables y los
  $627.500 disponibles del usuario 1. No es un bug: es para lo que la migración V2 backfilleó
  `expiresat`. Hay un test que lo fija.
