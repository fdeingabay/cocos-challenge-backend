# Cocos — Challenge Backend

API de portfolio y envío de órdenes al mercado. .NET 10 + PostgreSQL.

📄 **[`docs/reference.html`](docs/reference.html)** — documento de referencia con diagramas del
flujo de ejecución, el cálculo del portfolio y el control de concurrencia. Abrilo en el navegador.

---

## Cómo ejecutarlo

**Único requisito: Docker.** No hace falta tener .NET instalado.

```bash
docker compose up --build
```

Eso levanta PostgreSQL (con el esquema y los datos ya cargados) y la API. Cuando termine:

| | |
|---|---|
| **Swagger** | http://localhost:8080 |
| Base de datos | `localhost:5432` · usuario `cocos` · password `cocos` |

Para probar los endpoints hay ejemplos ejecutables en [`rest/cocos.http`](rest/cocos.http),
con los valores esperados comentados en cada request.

### Correr los tests

```bash
docker compose --profile test run --rm tests
```

Las tres suites: unitarios, de arquitectura y de integración. Los de integración corren contra
un PostgreSQL real —no contra un provider in-memory— y **no** necesitan acceso al socket de
Docker: apuntan al mismo servicio `db` del compose.

### Alternativa sin Docker para la API

Si preferís correrla desde el IDE, con el .NET 10 SDK instalado:

```bash
docker compose up -d db                    # solo la base
dotnet run --project src/Cocos.Api         # Swagger en http://localhost:5080
dotnet test
```

---

## La tesis

En este challenge la tabla `orders` cumple **tres roles a la vez**: ledger contable (las
ejecutadas determinan cash y tenencia), libro de órdenes pendientes (las vivas) y log de
transferencias (`CASH_IN`/`CASH_OUT`). No hay saldo almacenado en ninguna parte: todo el
estado del usuario es una **proyección** de esa tabla.

De ahí salen las dos invariantes que el sistema no puede violar jamás:

```
cash_disponible(user)        >= 0
tenencia(user, instrumento)  >= 0
```

donde el disponible **descuenta las reservas de las órdenes vivas**:

```
disponible = contable − Σ(BUY  NEW/PARCIAL: remanente × precio)
tenencia   = ejecutado − Σ(SELL NEW/PARCIAL: remanente)
```

Con el seed provisto, ignorar ese segundo término hace que la API informe **$753.000**
disponibles cuando en realidad son **$627.500**. Se equivoca por $125.500.

---

## Endpoints

| Endpoint | ¿Lo pide el enunciado? |
|---|---|
| `GET /api/users/{userId}/portfolio` | Sí |
| `GET /api/instruments?search=&page=&pageSize=` | Sí |
| `POST /api/orders` | Sí |
| `POST /api/orders/{id}/cancel?userId=` | Implícito — define la regla pero no el endpoint |
| `GET /api/users/{userId}/orders?status=` | No — sin él no se sabe qué orden cancelar |
| Job de expiración diaria | No — sin él las LIMIT reservan fondos para siempre |

---

## Cómo se sostiene la consistencia

**1. Lock por cuenta.** `POST /api/orders` abre una transacción cuyo primer paso es
`SELECT 1 FROM user_accounts WHERE userid = @u FOR UPDATE`. Validar e insertar quedan dentro
del mismo lock, así que son una operación indivisible.

Por qué hace falta una tabla solo para esto: el conflicto entre dos órdenes concurrentes vive
en una **suma**, no en ninguna fila. Cada transacción inserta una fila distinta, no colisionan,
y ambas commitean — **ni `REPEATABLE READ` lo evita** (write skew). Bloquear una fila concreta
materializa el conflicto y lo vuelve detectable. Como las cuentas son independientes entre sí,
no hay contención entre usuarios distintos.

**2. Reservas.** Las LIMIT de compra reservan pesos y las de venta reservan nominales mientras
están vivas. Resuelve el `CASH_OUT` contra órdenes pendientes y la doble venta del mismo lote.

**3. UPDATE condicional.** La cancelación lleva la condición de estado en el `WHERE` y verifica
`rowsAffected == 1`. Dos cancelaciones simultáneas, o una cancelación compitiendo con el job de
expiración, no pueden liberar la reserva dos veces.

**4. Idempotencia.** Header `Idempotency-Key` + índice único parcial `(userid, idempotencykey)`.
La orden es su propio registro de idempotencia; no hace falta una tabla aparte.

**5. Constraints en la base.** `CHECK` sobre status/side/type/sizes. Última línea de defensa.

---

## Decisiones y supuestos

**Rendimiento con PPP, no FIFO.** El enunciado nombra "rendimiento" con dos sentidos distintos.
Se devuelven como campos separados:

- `totalReturnPercent` = `(close − ppp) / ppp` — es **del usuario**, depende de a qué precio compró.
- `dailyReturnPercent` = `(close − previousClose) / previousClose` — es **del instrumento**, igual para todos.

El costo se calcula por promedio ponderado. Con las mismas operaciones, FIFO daría otro número
(comprar 10@100 y 10@200, vender 10: PPP deja el remanente a 150, FIFO a 200), así que el método
elegido tiene que ser explícito. PPP no necesita rastrear lotes y se resuelve en una sola query
agregada. *Limitación conocida:* si una posición se cierra y se reabre, el PPP mezcla ambos ciclos.

**Una orden rechazada devuelve `201`, no `400`.** El request se procesó correctamente; el rechazo
es un resultado de negocio y la orden se persiste como pide el enunciado. Un `400` diría que el
cliente mandó algo mal, y no es el caso.

**Un monto que no alcanza para una acción devuelve `400`.** No es falta de fondos: es una orden de
tamaño cero, que no llega a formarse. Persistirla ensuciaría el libro sin aportar información.

**La `Idempotency-Key` la genera el cliente y es opaca para el servidor.** Se acepta cualquier
string de hasta 128 caracteres; el contrato es responsabilidad de quien consume la API:

- **Qué enviar:** un valor aleatorio, por ejemplo un UUID v4. *No* derivarlo del contenido de la
  orden — comprar dos veces 10 PAMP es una operación legítima, y un hash del payload se comería
  la segunda en silencio. La clave identifica el **intento**, no el contenido.
- **Cuándo generarla:** una sola vez, cuando el usuario confirma, reusando ese valor en cada
  reintento de esa intención. Si se regenera por request, la protección no existe. Conviene
  persistirla junto a la orden pendiente, para que sobreviva a que se cierre la app.
- **Alcance:** única por usuario (índice `(userid, idempotencykey)`), así que dos usuarios pueden
  usar el mismo valor sin interferir.

*Limitación conocida:* si llega la misma clave con un body distinto se devuelve la orden original
en vez de un `409`. Detectarlo requiere guardar un hash del request; está en consideraciones
futuras. Las claves tampoco expiran, a diferencia de las 24 h que usa Stripe.

**Liquidación inmediata (T+0).** No se modela settlement: el disponible es el saldo contable menos
reservas. En un broker real el poder de compra depende del plazo de liquidación. Queda anotado en
consideraciones futuras.

**El ARS no aparece como posición.** Es un instrumento `MONEDA` y representa el cash: se informa
como `availableCash`, no como una fila más de la cartera.

**`CASH_IN`/`CASH_OUT`:** en el seed usan `size` = monto y `price` = 1, así que **monto = `size × price`**
de forma uniforme para todos los sides. Un solo cálculo cubre transferencias y operaciones.

**Sin Repository ni Unit of Work.** `DbContext` ya *es* la unidad de trabajo. `ICocosDbContext`
existe solo para no invertir la dependencia entre capas (Application no puede referenciar
Infrastructure): no hay colección por agregado, no encapsula queries, no envuelve Begin/Commit.

**Escritura con EF Core, lectura con Dapper.** Las lecturas son agregaciones y proyecciones que
Dapper resuelve en una query proyectando directo a records inmutables.

**`CancellationToken`.** Se propaga en todo el call stack como último parámetro. **Excepción
deliberada:** el `SaveChanges` y el `Commit` de una orden usan `CancellationToken.None` — que el
cliente corte la conexión no puede dejar una orden aplicada a medias. Está comentado en el código.

---

## Cambios de esquema

`db/01-database.sql` es el provisto, **sin modificar**. Todo lo nuestro está en
`db/02-V2__challenge.sql`, con la justificación de cada cambio en el propio archivo.

| Cambio | Por qué |
|---|---|
| `orders.filledsize` | La tabla no distingue cantidad solicitada de ejecutada. Sin eso los fills parciales son imposibles de representar y la tenencia se calcula mal. |
| `orders.expiresat` + estado `EXPIRED` | Sin vencimiento una orden viva reserva fondos para siempre. |
| Estado `PARTIALLY_FILLED` | Consecuencia de `filledsize`. |
| `orders.idempotencykey` + índice único parcial | Un reintento del cliente crea una orden duplicada. |
| **Tabla `user_accounts`** | **Punto de serialización para el `FOR UPDATE`.** Ver arriba. |
| 5 índices | La base provista no tenía ninguno más allá de las PK. Incluye GIN trigram sobre `instruments`: `ILIKE '%x%'` no puede usar un btree. |
| `NOT NULL` + `CHECK` | Todo era `VARCHAR` nullable, sin ninguna defensa. |

**No se agregó** una tabla de balances materializados: el enunciado pide explícitamente calcular
tenencia y disponible desde `orders`.

### Detalle técnico: los identificadores son minúsculas

El DDL provisto declara las columnas sin comillas, y Postgres las pliega a minúsculas. Las columnas
reales son `instrumentid`, `previousclose`, `accountnumber`. Por eso cada columna se mapea con
`HasColumnName` explícito: confiar en la convención PascalCase de EF produce errores de
"column does not exist" recién en runtime.

### Además: `marketdata.date`, no `datetime`

El listado de tablas del enunciado dice `datetime`, pero el DDL real define `date DATE`.
Se usó lo que dice el DDL.

---

## ⚠️ Inconsistencia detectada en los datos provistos

El usuario 1 arrastra una **posición negativa** en BMA:

```
BUY  20 @ 1540  FILLED   →  +20
SELL 30 @ 1530  FILLED   →  −30
                             ───
                   posición  −10
```

Existe una `BUY 60 @ 1500` en estado `NEW` que solo cerraría el número **si se contaran las
órdenes NEW**, cosa que el enunciado prohíbe explícitamente ("usar las órdenes en estado FILLED").

**No se tocó el seed.** La API no puede *generar* este estado — las validaciones lo impiden — pero
sí lo refleja si ya está en los datos. El portfolio muestra la realidad, no la maquilla. Hay un test
que fija este comportamiento.

---

## Tests

| Proyecto | Qué cubre |
|---|---|
| `Cocos.UnitTests` | Aritmética monetaria (`floor`, PPP, rendimientos) y máquina de estados. `FakeTimeProvider` para el vencimiento. |
| `Cocos.ArchitectureTests` | Capas, contratos inmutables, y prohibición de `DateTime.Now` (verificada sobre el fuente: una llamada estática no deja rastro en la superficie de tipos). |
| `Cocos.IntegrationTests` | TestContainers con Postgres real. Los 8 casos de la tabla de decisión, el portfolio contra los números del seed, idempotencia, cancelación y **concurrencia**. |

**El test central** manda N órdenes en paralelo contra el mismo saldo y verifica que se acepten
*exactamente* las que entran, que el resto quede `REJECTED` y que el disponible nunca sea negativo.

Se usa Postgres real y **no** el provider in-memory de EF: in-memory no implementa locking de filas,
que es justamente el mecanismo bajo prueba. Un test de concurrencia contra in-memory pasa siempre y
no demuestra nada.

### Correr los tests sin Docker

Por defecto la suite levanta Postgres con TestContainers. Si ya tenés un servidor disponible
—una instalación nativa, una base remota, o el *service container* de un pipeline— indicalo por
variable de entorno y TestContainers ni se usa:

```bash
COCOS_TEST_DB="Host=127.0.0.1;Port=5432;Database=postgres;Username=cocos;Password=cocos" \
  dotnet test
```

Del connection string solo se toman **host y credenciales**: la suite crea su propia base
plantilla, la siembra con los dos scripts de `db/`, y saca una copia por clase de test con
`CREATE DATABASE ... TEMPLATE`. Al terminar las borra. Necesita PostgreSQL 13+ y permiso de
`CREATEDB`.

> Desde WSL hacia un `dotnet` de Windows, la variable no cruza sola: hay que agregar
> `WSLENV="COCOS_TEST_DB"`.

Sin la variable y sin un daemon de Docker, TestContainers falla con `DockerUnavailableException`.

---

## Consideraciones futuras

Fuera de alcance por decisión explícita, no por omisión:

- **Settlement T+0/T+1** — columna `settlementdate`; el disponible pasa a filtrar por fecha de liquidación.
- **Matching engine** — single-writer por instrumento y prioridad precio-tiempo por secuencia de la base.
- **Balances materializados** — snapshot por `(userid, instrumentid)` actualizado en la misma transacción, para dejar de recorrer el historial en cada request.
- **Transactional outbox** — cuando aparezcan efectos externos (ruteo al mercado, notificaciones).
- Horario de mercado, precios stale, slippage/collar, corporate actions, comisiones e impuestos.
