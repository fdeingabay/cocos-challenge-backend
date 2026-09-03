# Documentación técnica y funcional

API de portfolio y envío de órdenes al mercado — Cocos challenge backend.

> Para levantar el proyecto ver el [README](../README.md).
> Para un mapa visual con diagramas ver [`reference.html`](reference.html).

---

## Índice

1. [Alcance y supuestos](#1-alcance-y-supuestos)
2. [Documentación funcional](#2-documentación-funcional)
3. [Documentación técnica](#3-documentación-técnica)
4. [Operación](#4-operación)
5. [Limitaciones y evolución](#5-limitaciones-y-evolución)

---

# 1. Alcance y supuestos

La API expone tres capacidades: consultar el portfolio de un usuario, buscar instrumentos en
el mercado y enviar órdenes de compra o venta. Se agregan dos operaciones que el enunciado
implica pero no define: cancelar una orden y listar las órdenes del usuario.

**No se simula el mercado.** Las órdenes `MARKET` se ejecutan contra el último precio de cierre
conocido; las `LIMIT` quedan registradas en el libro y nunca se ejecutan solas, porque no existe
contraparte. El sistema está preparado para que esa pieza se agregue después.

Supuestos explícitos:

| Supuesto | Motivo |
|---|---|
| Liquidación inmediata (T+0) | El esquema no modela plazos de liquidación y agregarlos no aporta al ejercicio. El disponible es el saldo contable menos reservas. |
| Sin autenticación | El `userId` viaja en el request. Lo indica el enunciado. |
| Sin comisiones ni impuestos | No están en el modelo de datos provisto. |
| Precios en pesos | Todos los importes son ARS. |
| Una orden `MARKET` siempre encuentra contraparte | Consecuencia de no simular el mercado. |

---

# 2. Documentación funcional

## 2.1 Modelo conceptual

**Instrumento.** Un activo negociable. El seed trae 66: 65 acciones y una moneda (`ARS`), porque
**el efectivo está modelado como un instrumento de tipo `MONEDA`**. Esa decisión del esquema
original tiene una consecuencia importante: el cash no es una columna de saldo, es el resultado
de sumar movimientos.

**Orden.** Toda alteración del patrimonio del usuario es una orden. Incluidas las transferencias:

| `side` | Qué representa |
|---|---|
| `BUY` | Compra de un instrumento |
| `SELL` | Venta de un instrumento |
| `CASH_IN` | Transferencia entrante de pesos |
| `CASH_OUT` | Transferencia saliente de pesos |

Para `CASH_IN` y `CASH_OUT` el monto viaja en `size` con `price = 1`, de modo que
**`monto = size × price` vale para todos los sides sin excepción** y un solo cálculo cubre
transferencias y operaciones.

**Los tres roles de la tabla `orders`.** Esta es la observación que ordena todo el diseño:

```
orders  ├─ Ledger contable      → las órdenes ejecutadas determinan cash y tenencia
        ├─ Libro de pendientes  → las órdenes vivas esperando ejecución
        └─ Log de transferencias → CASH_IN / CASH_OUT
```

No hay saldo almacenado en ninguna parte. **Todo el estado del usuario es una proyección de esa
tabla**, y de ahí se derivan tanto las decisiones de consistencia como las de rendimiento.

## 2.2 Ciclo de vida de una orden

```
                          ┌──→ REJECTED   (terminal · no reserva ni mueve nada)
   POST /api/orders ──────┤
                          ├──→ FILLED     (MARKET · terminal · movimiento aplicado)
                          │
                          └──→ NEW ───┬──→ PARTIALLY_FILLED ──┬──→ FILLED
                                      │                       ├──→ CANCELLED
                                      │                       └──→ EXPIRED
                                      ├──→ CANCELLED   (usuario)
                                      └──→ EXPIRED     (cierre de jornada)
```

| Estado | Significado | ¿Afecta el disponible? |
|---|---|---|
| `NEW` | Orden LIMIT viva en el libro | Sí — **reserva** |
| `PARTIALLY_FILLED` | Ejecutada en parte, viva por el remanente | Sí — movió lo ejecutado y **reserva** el resto |
| `FILLED` | Ejecutada por completo | Sí — movimiento real |
| `REJECTED` | Rechazada por fondos o tenencia insuficientes | **No**, nunca |
| `CANCELLED` | Cancelada por el usuario | No — libera la reserva |
| `EXPIRED` | Vencida al cierre sin ejecutarse | No — libera la reserva |

`PARTIALLY_FILLED` y `EXPIRED` no están en el enunciado; se agregaron junto con la columna
`filledsize` y la vigencia diaria. La justificación está en [3.3](#33-modelo-de-datos).

**`PARTIALLY_FILLED` está soportado de punta a punta, pero esta API no lo produce.** Toda la
cadena lo contempla —el remanente reserva, la cancelación libera sólo el remanente, el
vencimiento hace lo mismo, el portfolio informa lo ejecutado y el PPP lo costea— y
`PartialFillTests` fija las tres transiciones. Lo que no existe es una operación que deje una
orden a medio ejecutar: eso requiere un motor de matching que cruce órdenes contra el libro, y el
enunciado establece que **no hace falta simular el mercado**. El estado es entonces una capacidad
del modelo, no un camino muerto: el día que exista matching, la aritmética ya está escrita y
verificada. Y `filledsize`, que es lo que lo hace posible, se paga igual por otra razón —separar
lo solicitado de lo ejecutado— sin la cual la reserva de una orden viva no se puede calcular.

## 2.3 El concepto de reserva

Es el núcleo funcional del sistema y lo que separa una implementación correcta de una que
permite descubiertos.

Una orden `LIMIT` de compra en estado `NEW` **no movió un peso**, pero compromete fondos futuros.
Si el disponible se calcula sumando únicamente las órdenes ejecutadas, ocurre esto:

```
Usuario con $100.000
  → LIMIT BUY por $100.000        queda NEW
  → el disponible sigue diciendo $100.000     ← no descontó nada
  → LIMIT BUY por $100.000        queda NEW
  → $200.000 comprometidos con $100.000 en la cuenta
```

Y su variante más grave:

```
  → LIMIT BUY por $100.000        queda NEW
  → CASH_OUT de $100.000          pasa, porque el disponible dice $100.000
  → el usuario retiró plata comprometida; la orden viva es impagable
```

Del lado de la venta es idéntico con nominales: dos `LIMIT SELL` de 100 acciones sobre una
tenencia de 100 comprometen 200 acciones que no existen.

**Por eso el disponible descuenta lo reservado por órdenes vivas.** Con los datos del seed la
diferencia es concreta:

| | |
|---|---|
| Cash contable | `$753.000` |
| Reservado por órdenes `NEW` | `$125.500` |
| **Disponible para operar** | **`$627.500`** |

Sin el concepto de reserva la API informaría `$753.000`, equivocándose por `$125.500`.

## 2.4 Reglas de negocio

| # | Regla |
|---|---|
| R1 | Una orden `MARKET` se ejecuta al instante contra el último `close` y queda `FILLED`. |
| R2 | Una orden `LIMIT` queda `NEW` y requiere precio. |
| R3 | Si no alcanzan los pesos (compra) o los nominales (venta), la orden se persiste como `REJECTED`. |
| R4 | El disponible descuenta las reservas de las órdenes vivas. |
| R5 | Solo se pueden cancelar órdenes vivas (`NEW` o `PARTIALLY_FILLED`). |
| R6 | Cancelar libera la reserva; una orden ejecutada no se puede cancelar. |
| R7 | Las órdenes `LIMIT` vencen al cierre de la jornada en que se enviaron. |
| R8 | Se puede enviar cantidad exacta **o** monto, nunca ambos. Con monto se compran las acciones enteras que entren. |
| R9 | Una orden `REJECTED` jamás afecta cash ni tenencia. |
| R10 | Las posiciones se calculan solo con órdenes ejecutadas. |
| R11 | El efectivo (`ARS`, tipo `MONEDA`) no se lista como posición: se informa como pesos disponibles. |
| R12 | Un reintento con la misma `Idempotency-Key` devuelve la orden original, no crea otra. |

## 2.5 Referencia de endpoints

Todas las respuestas son JSON con nombres en `camelCase`. Los errores usan
[`ProblemDetails`](https://datatracker.ietf.org/doc/html/rfc9457) e incluyen un campo `code`
estable para que el cliente pueda ramificar sin parsear textos.

---

### `GET /api/users/{userId}/portfolio`

Valor total de la cuenta, pesos disponibles y posiciones.

**Respuesta `200`**

```json
{
  "userId": 1,
  "totalAccountValue": 889756.0,
  "availableCash": 627500.0,
  "accountingCash": 753000.0,
  "reservedCash": 125500.0,
  "positions": [
    {
      "instrumentId": 54,
      "ticker": "METR",
      "name": "MetroGAS S.A.",
      "quantity": 500,
      "availableQuantity": 500,
      "close": 229.50,
      "marketValue": 114750.0,
      "averageCost": 250.0,
      "totalReturnPercent": -8.20,
      "dailyReturnPercent": -1.0775
    }
  ]
}
```

| Campo | Significado |
|---|---|
| `totalAccountValue` | Cash **contable** + valor de mercado de las posiciones |
| `accountingCash` | Saldo contable, sin descontar compromisos |
| `reservedCash` | Comprometido por órdenes `LIMIT` de compra vivas |
| `availableCash` | `accountingCash − reservedCash`. **Es el que puede operar.** |
| `quantity` | Acciones en cartera |
| `availableQuantity` | `quantity` menos lo reservado por ventas vivas |
| `averageCost` | Precio promedio ponderado de compra (PPP) |
| `totalReturnPercent` | Rendimiento de la posición contra su PPP — **métrica del usuario** |
| `dailyReturnPercent` | Variación del instrumento en el día — **igual para todos** |

`totalAccountValue` usa el cash contable y no el disponible: las reservas **no salieron de la
cuenta**, solo están comprometidas. Descontarlas ahí contaría de menos.

Los campos de precio son `null` —nunca `0`— cuando el instrumento no tiene marketdata. No es lo
mismo "no sé" que "cero".

**Errores:** `404 user.not_found`. Un usuario inexistente no devuelve un portfolio vacío.

---

### `GET /api/instruments`

Búsqueda de activos por ticker o por nombre, paginada.

| Query param | Default | Notas |
|---|---|---|
| `search` | — | Coincidencia parcial sobre ticker **y** nombre, ignorando mayúsculas **y acentos**: `zorraquin` encuentra `Garovaglio Y Zorraquín`. Sin valor (o en blanco) devuelve todo. El término se busca **literal**: `%` y `_` no son comodines. |
| `page` | `1` | |
| `pageSize` | `20` | Tope duro de `100` |

**Respuesta `200`**

```json
{
  "items": [ { "id": 47, "ticker": "PAMP", "name": "Pampa Holding S.A.", "type": "ACCIONES" } ],
  "page": 1, "pageSize": 20, "totalCount": 66,
  "totalPages": 4, "hasNextPage": true, "hasPreviousPage": false
}
```

---

### `POST /api/orders`

Envía una orden de compra o venta.

**Headers**

| Header | Obligatorio | Notas |
|---|---|---|
| `Idempotency-Key` | No, recomendado | Ver [2.6](#26-idempotencia). Máximo 128 caracteres. |

**Body**

```json
{ "userId": 1, "instrumentId": 47, "side": "BUY", "type": "MARKET", "size": 10 }
```

| Campo | Reglas |
|---|---|
| `side` | `BUY` o `SELL`. `CASH_IN`/`CASH_OUT` son transferencias, no se envían por acá. |
| `type` | `MARKET` o `LIMIT` |
| `size` | Cantidad exacta. **Excluyente** con `amount`. |
| `amount` | Monto en pesos; se calculan las acciones enteras que entren. **Excluyente** con `size`. |
| `price` | Obligatorio y positivo si `type = LIMIT`. Ignorado en `MARKET`. |

**Respuesta `201`** — incluye el caso rechazado

```json
{
  "id": 12, "userId": 1, "instrumentId": 47, "ticker": "PAMP",
  "side": "BUY", "type": "MARKET", "status": "FILLED",
  "size": 108, "filledSize": 108, "price": 925.85, "notional": 99991.80,
  "dateTime": "2026-08-19T01:40:12.3Z", "expiresAt": null, "rejectionReason": null
}
```

**Una orden rechazada devuelve `201`, no `400`.** El request se procesó correctamente y la orden
quedó persistida como exige el enunciado; el rechazo es un **resultado de negocio**, no un error
de protocolo. El cliente distingue mirando `status` y `rejectionReason`. Un `400` afirmaría que
el cliente mandó algo mal, y no es el caso: mandó una orden válida que el mercado rechazó.

El `201` incluye la cabecera `Location`, que apunta al recurso recién creado:
`/api/orders/{id}?userId={userId}`.

**Respuesta `200`** — reintento con una `Idempotency-Key` ya usada: devuelve la orden original.

El código distingue los dos desenlaces exitosos y no es cosmético: un `201` le afirma al cliente
que **acaba de crear** una orden. Si el reintento contestara `201`, cualquier cliente que cuente
altas contaría dos veces la misma compra —justo lo que la clave existe para impedir—. El handler
devuelve un `SubmitOrderOutcome` que dice cuál de los dos ocurrió; el controller lo traduce.

**Errores**

| Código HTTP | `code` | Cuándo |
|---|---|---|
| `400` | `order.invalid` | Falla la validación de forma (ver [2.4 R8](#24-reglas-de-negocio)) |
| `400` | `order.size_zero` | El monto no alcanza ni para una acción |
| `400` | `instrument.not_tradable` | El instrumento es una moneda |
| `400` | `instrument.no_market_price` | Sin marketdata para valuar una `MARKET` |
| `404` | `user.not_found` / `instrument.not_found` | |

Sobre `order.size_zero`: no es falta de fondos sino una orden que **no llega a formarse**.
Persistirla ensuciaría el libro con filas de tamaño cero que no aportan información.

---

### `GET /api/orders/{orderId}?userId={userId}`

Una orden puntual. Es el recurso al que apunta el `Location` del `201` del alta: sin este
endpoint, esa cabecera prometería una dirección que no lleva a ningún lado.

| Query param | Obligatorio | Notas |
|---|---|---|
| `userId` | **Sí** | Sin él el binding lo resuelve como `0` y la respuesta sería un `404` que dice que la orden no existe cuando en realidad existe. Falta un parámetro: eso es `400`. |

**Respuesta `200`**

```json
{
  "id": 12, "userId": 1, "instrumentId": 47, "ticker": "PAMP",
  "side": "BUY", "type": "LIMIT", "status": "CANCELLED",
  "size": 10, "filledSize": 0, "price": 900.00, "notional": 9000.00,
  "dateTime": "2026-08-19T01:40:12.3Z", "expiresAt": "2026-08-19T23:59:59.9Z",
  "cancelledAt": "2026-08-19T01:42:00Z"
}
```

Es la única lectura que informa `cancelledAt`: el listado devuelve un resumen y el alta todavía
no tiene nada que informar.

**Errores**

| Código | `code` | Cuándo |
|---|---|---|
| `400` | — | Falta `userId`. |
| `404` | `order.not_found` | No existe **para ese usuario**. Una orden ajena responde igual que una inexistente: contestar distinto le confirmaría que esa orden existe para otro. |

---

### `POST /api/orders/{orderId}/cancel?userId={userId}`

Cancela una orden viva y libera su reserva.

**Respuesta `200`**

```json
{ "id": 12, "status": "CANCELLED", "cancelledAt": "2026-08-19T01:42:00Z" }
```

**Errores**

| Código | `code` | Cuándo |
|---|---|---|
| `404` | `order.not_found` | No existe **para ese usuario**. Un usuario no puede saber si la orden existe para otro. |
| `409` | `order.not_cancellable` | Ya está `FILLED`, `CANCELLED`, `EXPIRED` o `REJECTED`. El mensaje informa el estado actual. |
| `409` | `order.no_longer_open` | Dejó de estar viva **entre la lectura y el registro**: la venció el job o la canceló otra sesión. Es la carrera real, distinta del caso anterior. |

---

### `GET /api/users/{userId}/orders`

Listado paginado de órdenes. Filtrar por `status=NEW` para saber cuáles se pueden cancelar.

| Query param | Default |
|---|---|
| `status` | — (todas). Cualquier capitalización: `new` y `NEW` son lo mismo. |
| `page` | `1` |
| `pageSize` | `20`, tope `100` |

Ordenado por fecha descendente.

**Errores**

| Código | `code` | Cuándo |
|---|---|---|
| `400` | `order.unknown_status` | El `status` pedido no existe. El mensaje enumera los válidos. |
| `404` | `user.not_found` | No existe el usuario. |

Los dos son deliberados y valen la pena explicarlos: **"no hay órdenes en ese estado" y "ese
estado no existe" son respuestas distintas**, igual que "el usuario no tiene órdenes" y "el
usuario no existe". Devolver una lista vacía en los cuatro casos hace que un error del cliente
—un typo en el query param, un id equivocado— llegue como un resultado legítimo. El filtro se
convierte en concepto de dominio (`OrderStatusFilter`) antes de tocar la base, así que lo que
llega al `WHERE` es siempre un literal que existe.

## 2.6 Idempotencia

La clave la **genera el cliente** y es opaca para el servidor: se acepta cualquier string de
hasta 128 caracteres.

- **Qué enviar:** un valor aleatorio, por ejemplo un UUID v4. **No** derivarlo del contenido de
  la orden. Comprar dos veces 10 PAMP es una operación legítima, y un hash del payload se
  comería la segunda en silencio. **La clave identifica el intento, no el contenido.**
- **Cuándo generarla:** una sola vez, cuando el usuario confirma, reusando ese valor en cada
  reintento de esa intención. Si el cliente la regenera por request, la protección no existe.
  Conviene persistirla junto a la orden pendiente para que sobreviva a que se cierre la app.
- **Alcance:** única **por usuario**, así que dos usuarios pueden usar el mismo valor sin
  interferir.

## 2.7 Fórmulas de cálculo

```
cash contable  = + CASH_IN            (size × price, solo FILLED)
                 − CASH_OUT           (size × price, solo FILLED)
                 + SELL ejecutado     (filledSize × price)
                 − BUY  ejecutado     (filledSize × price)

reservado      =   BUY vivo           ((size − filledSize) × price)

disponible     = cash contable − reservado


cantidad       = + BUY ejecutado (filledSize) − SELL ejecutado (filledSize)
reservado      =   SELL vivo     (size − filledSize)
disponible     = cantidad − reservado

valor mercado  = cantidad × close
ppp            = costo de la tenencia ACTUAL / cantidad en cartera
rendimiento %  = (close − ppp) / ppp × 100
retorno día %  = (close − previousClose) / previousClose × 100
```

### Por qué PPP y no FIFO

El enunciado pide "rendimiento total" pero no define el método de costeo, y **la elección cambia
el número informado**:

```
Compra 10 @ $100  →  10 acciones, costo $1.000
Compra 10 @ $200  →  20 acciones, costo $3.000
Venta  10 @ $250  →  quedan 10, ¿con qué costo?
```

| Método | Costo restante | Si hoy vale $300 |
|---|---|---|
| **PPP** (promedio ponderado) | 10 × $150 = $1.500 | **+100%** |
| FIFO | 10 × $200 = $2.000 | **+50%** |

Se eligió **PPP**: es el estándar para retail y no requiere rastrear lotes individuales.

**Lo que sí requiere es un recorrido ordenado**, y esto costó un bug. La primera versión lo
calculaba con una sola agregación —`Σ(compras) / Σ(cantidades compradas)`— que *no es* el PPP:
coincide con él sólo mientras no haya ventas de por medio. La regla del PPP es que una venta
reduce el costo **en la misma proporción** que la tenencia, dejando el promedio intacto; eso es
multiplicativo y no tiene forma cerrada. En cuanto una posición se cerraba y se volvía a abrir,
el promedio de por vida seguía arrastrando compras de una tenencia que ya no existía:

```
PAMP: 40 acciones, PPP 930   →  vender las 40  →  comprar 1 a 925,85
   promedio de todas las compras: 929,92   ← arrastra 50 compras de una posición cerrada
   PPP de la tenencia actual:     925,85   ← lo único que se pagó por la única acción
```

Hoy `PositionsSql` camina los movimientos ejecutados en orden con un CTE recursivo. Es la parte
cara de la consulta y se paga a conciencia: informar un costo que nadie pagó es peor que un
`WITH RECURSIVE`.

*Limitación conocida:* si una posición se cierra y se reabre, el PPP mezcla ambos ciclos.
Resolverlo correctamente implica resetear el costo al llegar a cero, lo que rompe la agregación
en una sola pasada.

### Dos métricas distintas que es fácil confundir

El enunciado menciona "rendimiento" con dos sentidos y devolverlos juntos sería un error:

| | Depende de | Es igual para todos los usuarios |
|---|---|---|
| `totalReturnPercent` | A qué precio compró **ese** usuario | No |
| `dailyReturnPercent` | Solo del instrumento | Sí |

Si compraste GGAL a $100 y hoy vale $150, tu rendimiento es +50% **sin importar** cuánto se movió
hoy el papel. Son campos separados en la respuesta.

---

# 3. Documentación técnica

## 3.1 Arquitectura

Clean Architecture como regla de dependencias, Vertical Slice como criterio de organización.

```
┌─────────────────────────────────────────────────┐
│ Cocos.Api            controllers · Swagger      │
│                      error handling · DI · job  │
└───────────────┬─────────────────────┬───────────┘
                │                     │
┌───────────────▼──────────┐  ┌───────▼───────────────────┐
│ Cocos.Application        │  │ Cocos.Infrastructure      │
│ features (slices)        │◄─┤ DbContext · configs       │
│ Result · abstracciones   │  │ conexión Dapper · DI      │
└───────────────┬──────────┘  └───────────────────────────┘
                │
┌───────────────▼──────────┐
│ Cocos.Domain             │   entidades · enums
│ sin dependencias         │   máquina de estados · cálculos
└──────────────────────────┘
```

La flecha `Infrastructure → Application` es deliberada: **la dependencia va hacia adentro**.
Infrastructure implementa las abstracciones que Application declara (`ICocosDbContext`,
`IDbConnectionFactory`), nunca al revés. Hay tests de arquitectura que lo verifican en cada
build, así que no es una convención escrita en un documento sino una regla ejecutable.

**Por qué Vertical Slice sobre Clean.** Las capas horizontales puras dispersan una funcionalidad
en cuatro carpetas. Acá cada slice es autocontenido:

```
Features/Orders/SubmitOrder/
    SubmitOrderCommand.cs      command + response (records)
    SubmitOrderValidator.cs    validación de forma
    SubmitOrderHandler.cs      resolución del caso de uso
    IAccountLedger.cs          el lock de cuenta y sus lecturas (contrato)
    IInstrumentReader.cs       instrumento y último precio (contrato)
```

El SQL de estos contratos vive en `Cocos.Infrastructure/Orders/`, del lado de la capa que
conoce la base.

Todo lo que hace falta para entender "enviar una orden" está en una carpeta. La regla de
dependencias de Clean se conserva; lo que cambia es el eje de agrupación.

## 3.2 Anatomía de un feature

Un slice tiene tres piezas y un flujo fijo:

```
Controller  ──InvokeAsync──►  Handler  ──►  Result<T>
                                │
                                ├─ Validator (forma del request)
                                ├─ SQL / EF (datos)
                                └─ Domain   (reglas y cálculos)
```

**Wolverine** es el mediador in-process. Casi todos los handlers son clases estáticas con un
método `Handle` y las dependencias se inyectan **por parámetro**, no por constructor:

```csharp
public static async Task<Result<PortfolioResponse>> Handle(
    GetPortfolioQuery query,
    IDbConnectionFactory connectionFactory,
    CancellationToken cancellationToken)
```

`SubmitOrderHandler` es la excepción: es una clase de instancia con inyección por constructor,
porque tiene un método privado (`ResolvePriceAsync`) que necesita las mismas dependencias, y con
inyección por parámetro habría que arrastrarlas hasta él. No es por tener colaboradores propios:
`CancelOrderHandler` también los tiene —el libro de órdenes— y sigue siendo estático. Wolverine
soporta las dos formas; lo que exige en ambas es que el tipo, el constructor y el método sean
públicos, y eso lo fija `HandlerConventionTests`.

Sin estado de instancia, sin ceremonia de constructor, y trivialmente testeable llamando al
método directamente. Wolverine genera el código de invocación **en tiempo de compilación** en
lugar de resolver por reflection en cada request.

## 3.3 Modelo de datos

El archivo `db/01-database.sql` es el provisto por el challenge, **sin una sola modificación**.
Todo lo agregado vive en `db/02-V2__challenge.sql`, con la justificación de cada cambio escrita
en el propio archivo.

### Cambios y qué funcionalidad habilita cada uno

| Cambio | Funcionalidad que soporta |
|---|---|
| `orders.filledsize` | **Fills parciales.** La tabla original no distingue cantidad *solicitada* de *ejecutada*: solo tiene `size`. Sin esa separación una orden ejecutada a medias calcula mal la tenencia, porque no hay forma de saber cuánto se movió realmente. |
| `orders.expiresat` + `EXPIRED` | **Vigencia diaria.** Sin vencimiento una orden `NEW` reserva fondos para siempre y el usuario nunca los recupera. |
| `orders.cancelledat` | **Auditoría de la cancelación.** La respuesta informa *cuándo* se canceló; sin la columna ese instante no quedaba registrado en ningún lado y nadie podía reproducirlo. `datetime` es el alta de la orden, y el estado `CANCELLED` dice *qué* pasó pero no *cuándo*. Un `CHECK` enuncia que solo una orden cancelada puede tenerla. |
| `orders.idempotencykey` + índice único parcial | **Protección contra reintentos.** Ver [3.5](#35-concurrencia-y-consistencia). |
| Estado `PARTIALLY_FILLED` | Consecuencia directa de `filledsize`. |
| **Tabla `user_accounts`** | **Punto de serialización de la cuenta.** Es la pieza central del control de concurrencia; se explica en detalle en [3.5](#35-concurrencia-y-consistencia). |
| Extensión `unaccent` + función `f_unaccent()` | **Búsqueda insensible a acentos.** `unaccent()` no es `IMMUTABLE`, así que no se puede indexar directamente: la V2 la envuelve en una función propia que sí lo es. El índice trigram se crea sobre **esa misma expresión**, y la consulta la repite literalmente — si dejan de coincidir, el planner descarta el índice y cada búsqueda vuelve a ser un full scan. |
| 5 índices | Rendimiento. La base provista no tenía ninguno más allá de las PK. Ver [3.10](#310-rendimiento). |
| `NOT NULL` + `CHECK` | Última línea de defensa. Todo era `VARCHAR` nullable, sin ninguna restricción. |

### Lo que deliberadamente no se cambió

**No se agregó una tabla de balances materializados.** El enunciado pide explícitamente calcular
tenencia y disponible desde `orders`, y respetarlo es parte del ejercicio. La evolución natural
—snapshot por `(userid, instrumentid)` actualizado en la misma transacción— está en
[limitaciones](#5-limitaciones-y-evolución).

### Dos detalles del esquema provisto que condicionan el código

**Los identificadores son minúsculas.** El DDL declara las columnas sin comillas y PostgreSQL las
pliega: las columnas reales son `instrumentid`, `previousclose`, `accountnumber`. Por eso cada
propiedad se mapea con `HasColumnName` explícito. Confiar en la convención PascalCase de EF Core
produce errores de *"column does not exist"* recién en runtime.

**`marketdata` tiene `date DATE`, no `datetime TIMESTAMP`.** El listado de tablas del enunciado
dice una cosa y el DDL real dice otra. Se implementó lo que dice el DDL.

## 3.4 El flujo crítico: `POST /api/orders`

Todo ocurre dentro de **una única transacción** cuyo primer paso es tomar el lock de la cuenta:

```
BEGIN
 1. SELECT 1 FROM user_accounts WHERE userid = @u FOR UPDATE   ← serializa la cuenta
 2. ¿existe orden con esta (userid, idempotencykey)?  → devolver la original (200)
 3. instrumento: ¿existe? ¿es negociable?
 4. precio:      MARKET → último close   |   LIMIT → el enviado
 5. cantidad:    la enviada  |  floor(monto / precio)
 6. disponible:  cash o nominales, YA descontando reservas
 7. ¿alcanza?    NO → INSERT REJECTED
                 SÍ → MARKET: INSERT FILLED   |   LIMIT: INSERT NEW + expiresAt
COMMIT                                        ← con CancellationToken.None
```

Dos propiedades importantes de este orden:

**Los pasos 6 y 7 están dentro del lock del paso 1.** Por eso "consultar el saldo" y "descontar
del saldo" son una operación indivisible. Es exactamente lo que impide el descubierto.

**El paso 2 también está dentro del lock.** Dos reintentos simultáneos del mismo usuario se
serializan ahí, así que el segundo ve la orden que creó el primero en lugar de insertar otra.

## 3.5 Concurrencia y consistencia

### Las invariantes

```
cash_disponible(user)        >= 0
tenencia(user, instrumento)  >= 0
```

Todo el resto —valor de mercado, rendimiento, retorno diario— es **derivado y tolera consistencia
eventual**: son cálculos sobre precios que cambian solos. Que el portfolio muestre el valor con
200 ms de desfasaje no rompe nada; que el cash quede negativo, sí.

Esa distinción es la que permite ser estricto donde importa y barato donde no. Tratar todo con el
mismo rigor produce un sistema lento que **igual** permite descubiertos.

### El problema: el conflicto no está en ninguna fila

Sin control, dos requests concurrentes del mismo usuario hacen esto:

```
T1: calcula disponible = $100.000
T2: calcula disponible = $100.000        ← lee antes de que T1 escriba
T1: valida orden de $80.000 ✓ → INSERT
T2: valida orden de $80.000 ✓ → INSERT
                                          saldo real: −$60.000
```

Lo importante es **por qué la base no lo detecta sola**: cada transacción inserta una fila
**distinta**. No hay conflicto de escritura sobre ningún registro. El conflicto vive en un
**agregado** —la suma de las órdenes— que no está materializado en ninguna parte.

Consecuencia práctica que suele sorprender: **`REPEATABLE READ` no lo evita.** Ambas transacciones
leen un snapshot consistente, escriben filas que no colisionan y ambas commitean. Es el patrón
conocido como *write skew*.

### La solución: materializar el conflicto

La tabla `user_accounts` tiene una fila por usuario y **ninguna otra razón de existir**: le da a
PostgreSQL una fila concreta que bloquear.

```sql
SELECT 1 FROM user_accounts WHERE userid = @UserId FOR UPDATE;
```

Con eso el conflicto sobre un agregado se convierte en un conflicto sobre una fila, que la base sí
sabe resolver:

```
T1: 🔒 LOCK ── lee 100.000 ── valida ✓ ── INSERT ── COMMIT 🔓
T2: ─────────── espera ──────────────────────────── lee 20.000 ── REJECTED
                                                          ▲
                                            ve el resultado de T1
```

**Por qué un lock explícito y no `SERIALIZABLE`.** Subir el aislamiento también resolvería el
write skew, pero bajo carga genera abortos frecuentes (`40001`) que obligan a implementar retry
en todo el pipeline. El lock por cuenta es **predecible**: no hay reintentos, no hay fallos
espurios, y el comportamiento bajo contención es fácil de razonar.

**Por qué por cuenta y no por instrumento.** Las cuentas son independientes entre sí, así que el
paralelismo entre usuarios distintos es prácticamente lineal. Un lock por instrumento serializaría
a todos los que operan el mismo papel — un punto caliente. Como en este alcance no hay matching
entre usuarios, el eje "instrumento" directamente no aparece.

### Las otras tres defensas

**Reservas en el cálculo del disponible.** Resuelto en [2.3](#23-el-concepto-de-reserva). Es una
regla de negocio, no de concurrencia, pero sostiene la misma invariante.

**El invariante, escrito una sola vez.** Hay dos lados que leen la misma cuenta y tienen que
coincidir siempre: el portfolio **informa** el disponible y el envío de órdenes **decide** contra
él. Estaban escritos por separado —las mismas expresiones `CASE`, palabra por palabra, en dos
capas distintas—, que es la clase de duplicación que no avisa al divergir: la API informaría un
número y el sistema aceptaría otro. Hoy los cuatro fragmentos viven en `LedgerSql` y los dos
lados los componen:

| Fragmento | Lo usa el portfolio como | Lo usa el envío como |
|---|---|---|
| `AccountingCash` | `accountingCash` | primer término del disponible |
| `ReservedCash` | `reservedCash` | segundo término (el que se olvida) |
| `ExecutedQuantity` | `quantity` de cada posición | primer término de los nominales libres |
| `ReservedQuantity` | `quantity − availableQuantity` | segundo término |

Los estados vivos no son un literal del SQL: llegan como `@OpenStatuses` desde `DbValues`, igual
que en la cancelación y el vencimiento. Y la coincidencia entre ambos lados está fijada por dos
tests que gastan **exactamente** el disponible que la API informa —y después un peso más—: si las
dos lecturas divergen, fallan.

**Lo ejecutado, en cambio, no filtra por estado.** `filledsize` *es* lo ejecutado, por definición:
una `NEW` o una `REJECTED` lo tienen en cero, y ningún estado terminal deshace una ejecución.
Cancelar el remanente de una orden a medio ejecutar libera la reserva y nada más; las acciones ya
compradas siguen compradas.

Esto fue un bug real, encontrado por una auditoría independiente contra el enunciado. Los cuatro
fragmentos condicionaban lo ejecutado a un `ExecutedStatuses` = `{FILLED, PARTIALLY_FILLED}`, así
que al pasar a `CANCELLED` o `EXPIRED` el `filledsize` dejaba de contar en las cuatro cuentas a la
vez:

```
antes de cancelar   contable 96.400 | reservado 5.400 | GGAL 4 acciones a 900
despues de cancelar contable 100.000 | reservado 0     | sin posiciones
```

$3.600 pagados por 4 acciones reales, devueltos; las acciones, borradas. Y por vencimiento era
peor: lo disparaba el job, no el usuario. La corrección quitó el filtro —el invariante perdió un
concepto en vez de ganar dos, y `DbValues.ExecutedStatuses` desapareció— y un `CHECK` de la V2
(`ck_orders_filled_solo_si_ejecuto`) volvió estructural lo que la sostiene: una orden `NEW` o
`REJECTED` no puede tener `filledsize > 0`. `PartialFillTests` lo fija en las tres transiciones.

**UPDATE condicional en la cancelación.** La condición de estado viaja en el `WHERE`, no se evalúa
antes en memoria:

```sql
UPDATE orders SET status = @NewStatus, cancelledat = @CancelledAt
WHERE id = @OrderId AND userid = @UserId AND status = ANY(@OpenStatuses);
```

El *cuándo* se escribe en el mismo statement que el *qué*: la cancelación es un solo hecho, y
guardar su estado sin su instante deja a la API informando una fecha que no existe en la base.

Se verifica que haya afectado exactamente una fila. Dos cancelaciones simultáneas —o una
cancelación compitiendo con el job de expiración— hacen que solo una afecte filas; la otra ve `0`
y sabe que perdió la carrera. **Sin esto la reserva se podría liberar dos veces.** Es concurrencia
optimista aplicada exactamente donde importa, sin ningún lock adicional.

Cuáles son los estados vivos **no es un literal del SQL**: llega por parámetro desde
`DbValues.OpenStatuses`. La regla de negocio se manda a la base en vez de estar escrita dos veces,
una en C# y otra en SQL, con un test que fija que ambas coincidan.

**El caso de uso, partido en dos mitades.** La *decisión* vive en el dominio —`Order.Cancel()` no
muta la orden: produce un `OrderCancellation`, el hecho ya decidido y todavía no registrado— y la
*garantía* vive en la sentencia de arriba, que lo registra sólo si la orden sigue viva. Por eso
`CancelOrderHandler` pregunta dos veces lo mismo, y las dos hacen falta:

| Pregunta | Para qué | ¿Puede quedar obsoleta? |
|---|---|---|
| `order.CanBeCancelled` | **Explicar** el 409 con el estado real | Sí, entre la lectura y la escritura |
| `status = ANY(@OpenStatuses)` | **Garantizar** que la reserva no se libere dos veces | No: se evalúa al escribir |

El handler no ve SQL, ni `rowsAffected`, ni un nombre de columna: sólo el libro de órdenes
(`IOrderBook`) y las reglas del dominio. Cancelar además **no toma el lock de cuenta** —a
diferencia del envío—: su conflicto vive en una fila, no en una suma, y una fila la defiende
PostgreSQL solo.

**El vencimiento: el mismo mecanismo, aplicado a un conjunto.** El barrido usa un `UPDATE` masivo
con la misma forma condicional:

```sql
UPDATE orders SET status = @NewStatus
WHERE status = ANY(@OpenStatuses) AND expiresat IS NOT NULL AND expiresat <= @AsOf;
```

Es **idempotente por construcción**: el filtro por estado hace que una segunda corrida afecte `0`
filas, porque las que ya vencieron dejaron de estar vivas. Por eso el job puede correr en N
instancias sin leader election ni claim —la primera gana y las demás no hacen nada— y por eso
`OrderExpirationService` puede reintentar en el próximo tick tras un fallo, sin efectos duplicados.

La diferencia con la cancelación es de naturaleza, y vale la pena marcarla: acá **no hay una
entidad que cargar**. Vencer no es una decisión *por orden* sino un **criterio evaluado a un
instante** —`OrderExpiry`—, y el `UPDATE` masivo es su expresión en conjunto. Cargar las órdenes
una por una para llamarles un método sería un N+1 disfrazado de pureza. La regla por entidad
existe igual, en `Order.HasExpired(now)`, y un test de integración fija que el barrido venza
exactamente las órdenes para las que esa regla es `true`.

> **Sobre el seed:** las dos órdenes `NEW` provistas son de julio de 2023 y la migración V2 les
> asignó el cierre de su jornada. El primer barrido en una base recién creada las vence y libera
> su reserva —50×710 + 60×1500 = **$125.500**, justo la diferencia entre el cash contable y el
> disponible del usuario 1—. No es un artefacto: es lo que pasaría en producción, y es el motivo
> por el que esa columna se backfilleó. Hay un test que lo fija.

**Idempotencia.** Índice único parcial `(userid, idempotencykey) WHERE idempotencykey IS NOT NULL`.

Es **parcial por costo, no por correctitud**: en PostgreSQL los `NULL` son distintos entre sí
dentro de un índice único, así que las órdenes sin clave nunca colisionarían aunque se indexaran
todas. Pero el header es opcional y la mayoría de las órdenes no lo llevan; sin el `WHERE` el
índice guardaría una entrada por cada fila de la tabla y cada `INSERT` pagaría ese mantenimiento
en el camino más caliente del sistema.

Va **compuesto con `userid`** porque la clave la genera el cliente: sin ese scope, la clave de un
usuario bloquearía la de otro y se rechazarían órdenes legítimas de un tercero.

Y aunque el chequeo del handler ya ocurre dentro del lock, el índice **no es decorativo**: si
alguien subiera el aislamiento a `REPEATABLE READ`, el `SELECT` de la segunda transacción usaría
un snapshot anterior al commit de la primera y no vería la fila. Ahí el índice es lo único que
impide el duplicado. Una invariante que solo vive en el código es una convención; en la base es
una garantía.

**CHECK constraints.** Última línea: si un bug de aplicación se cuela, la base rechaza.

## 3.6 Decisiones técnicas y la funcionalidad que soportan

| Decisión | Funcionalidad que habilita | Alternativa descartada |
|---|---|---|
| Lock por cuenta con `FOR UPDATE` | Que el disponible nunca quede negativo bajo concurrencia | `SERIALIZABLE`: correcto pero con abortos y retry en todo el pipeline |
| Tabla `user_accounts` dedicada | Darle a la base una fila que bloquear | Bloquear alguna fila de `orders`: no hay ninguna que represente "la cuenta" |
| Reservas en el disponible | `CASH_OUT` y compras concurrentes contra órdenes vivas | Contar solo ejecutadas: permite comprometer fondos inexistentes |
| `UPDATE` condicional | Cancelación segura ante carreras | Read-modify-write: decidir en memoria y escribir **sin** condición deja abierta la ventana entre el chequeo y la acción |
| Índice único de idempotencia | Que un reintento no duplique la compra | Tabla `idempotency_records` aparte: obliga a serializar y versionar la respuesta |
| EF Core en escritura | Transacción, tracking y `INSERT` de la orden | Solo Dapper: manejo manual de transacciones |
| Dapper en lectura | Portfolio en una query, proyectando a records | EF con `Include`: materializa entidades completas y arrastra N+1 |
| `LEFT JOIN LATERAL` a marketdata | Último precio por instrumento sin duplicar filas | `JOIN` simple: duplica cada posición (hay 2 días cargados) |
| `Result<T>` en vez de excepciones | Que "fondos insuficientes" sea un `201 REJECTED` y no un `500` | Excepciones: convierte una regla de negocio en un fallo técnico |
| `TimeProvider` | Testear el vencimiento sin esperar al cierre del día | `DateTime.UtcNow`: código intesteable, verificado por un test de arquitectura |
| `decimal` en todo el pipeline | `floor(monto/precio)` exacto | `double`: error de representación → una acción de más o de menos |
| Enums como string en la BD | Conservar los literales del seed provisto | Enteros: rompe los datos existentes y la legibilidad |
| Records inmutables | Contratos que no se pueden mutar tras validarlos | Clases con setters, verificado por test de arquitectura |
| Wolverine como mediador | Desacoplar controller de handler sin MediatR | Inyectar handlers a mano: ceremonia en cada controller |

## 3.7 Manejo de errores

Tres categorías, tratadas distinto a propósito:

| Categoría | Ejemplo | Mecanismo | Respuesta |
|---|---|---|---|
| **Resultado de negocio** | Fondos insuficientes | `Result` exitoso, orden persistida | `201` con `status: REJECTED` |
| **Error esperado** | Usuario inexistente, orden no cancelable | `Result` fallido con `Error` tipado | `400` / `404` / `409` + `ProblemDetails` |
| **Fallo inesperado** | La base no responde | Excepción → `GlobalExceptionHandler` | `500` + `ProblemDetails` |

No hay `try/catch` disperso por los handlers. El `Error` lleva un `ErrorType`
(`Validation`, `NotFound`, `Conflict`) que la capa Api traduce al status HTTP, de modo que la
capa de negocio **no conoce códigos HTTP**.

Deliberadamente **no existe** un `ErrorType` para fondos insuficientes: ese caso no es un error.

## 3.8 CancellationToken

Se propaga en todo método async como último parámetro y a través de todo el call stack. La
excepción `OperationCanceledException` **no se traga**: el handler global la trata de forma
específica, la loguea como información y devuelve `499`. Que el cliente corte la conexión no es
un error del servidor, y registrarlo como tal ensucia la señal de errores reales.

**La excepción deliberada:** el `SaveChanges` y el `Commit` de una orden usan
`CancellationToken.None`.

```csharp
// Sin CancellationToken a proposito: el trabajo pendiente se termina siempre, aunque
// el cliente ya haya cortado.
await db.SaveChangesAsync(CancellationToken.None);
await transaction.CommitAsync(CancellationToken.None);
```

Esto vive en `AccountLock.CommitAsync()`, y la interfaz lo refuerza: `IAccountLock.CommitAsync()`
**no recibe `CancellationToken`**. La decisión deja de depender de que alguien lea un comentario
—la firma directamente no admite el token.

Si el usuario cierra la app justo cuando se está confirmando su compra, la compra se confirma.
Lo contrario dejaría el sistema en un estado que nadie puede reconstruir.

La cancelación de una orden también: `IOrderBook.ApplyAsync()` **no recibe `CancellationToken`**,
igual que `CommitAsync()`. Con la decisión ya tomada, que el cliente corte la conexión no puede
dejar la reserva retenida.

El barrido de vencimiento es el tercer caso, y el más literal: `IOpenOrders.ApplyAsync()` tampoco
lo recibe, y `ExpireOrdersHandler` directamente **no lo toma como parámetro**. Sin conexión que
abrir ni escritura cancelable, no queda nada a lo que pasárselo, y la firma dice por sí sola que
ese caso de uso no se interrumpe a la mitad. El apagado del host lo maneja un nivel más arriba
`OrderExpirationService`, que corta en el `WaitForNextTickAsync` y captura
`OperationCanceledException` de forma específica.

Queda una sola regla en todo el codebase, ya sin excepciones: **la escritura que consume una
decisión de negocio no toma el token del request**, y en los tres casos la firma lo impone.

## 3.9 Seguridad

Sin autenticación por indicación del enunciado. Lo que sí se aplica:

| Riesgo | Mitigación |
|---|---|
| Inyección SQL | Todo parámetro viaja como tal, nunca concatenado. |
| Comodines de `ILIKE` | El término se escapa antes de armar el patrón (`LikePattern`). **Parametrizar no alcanza:** evita la inyección, pero dentro de un `LIKE` el valor sigue siendo *sintaxis*. Sin escapar, buscar `%` devolvía los 66 instrumentos y buscar `S_A` devolvía 39 —el `_` matcheaba el punto de "S.A."—. El `ESCAPE '\'` se declara explícito aunque sea el default de PostgreSQL, y el índice trigram se sigue usando. |
| Consumo de recursos | `pageSize` con tope duro de 100. Sin él, un cliente pide toda la tabla en un request. |
| Desbordes | `price` acotado por validación antes de llegar a la columna `numeric(10,2)`, que si no fallaría con un error opaco en vez de un `400` claro. |
| Índice inflado | `Idempotency-Key` limitada a 128 caracteres. Además el btree de PostgreSQL tiene un límite por entrada que una clave gigante haría estallar en runtime. |
| Fuga de información | Cancelar la orden de otro usuario devuelve `404`, no `403`: no se confirma que la orden exista. |
| Exposición de internals | El `GlobalExceptionHandler` no filtra stack traces al cliente. |
| Superficie del contenedor | La imagen final es `aspnet`, sin SDK, y corre con usuario sin privilegios. |

## 3.10 Rendimiento

**Cero N+1 por construcción.** El portfolio se resuelve con `QueryMultiple`: dos statements en un
round trip, uno para el cash y otro para las posiciones. Las posiciones traen el último precio con
`LEFT JOIN LATERAL`:

```sql
LEFT JOIN LATERAL (
    SELECT m."close", m.previousclose
    FROM marketdata m
    WHERE m.instrumentid = a.instrumentid
    ORDER BY m."date" DESC
    LIMIT 1
) md ON true
```

Sin el `LATERAL` hay dos caminos y los dos son malos: una query por instrumento (N+1), o un `JOIN`
simple que **duplica cada posición**, porque hay dos días de marketdata cargados por instrumento.

**Índices agregados** — la base provista no tenía ninguno:

| Índice | Consulta que sostiene |
|---|---|
| `orders(userid, status)` | Cálculo del disponible |
| `orders(userid, instrumentid, status)` | Tenencia y posiciones |
| `orders(status, expiresat)` parcial | Barrido del job de expiración |
| `marketdata(instrumentid, "date" DESC)` | El `LATERAL` del último precio |
| GIN trigram en `instruments` | Búsqueda por ticker y nombre |

El GIN trigram merece una nota: `ILIKE '%texto%'` **no puede aprovechar un btree**, porque el
comodín inicial impide usar el orden del índice. Sin trigram, cada búsqueda es un scan completo.

**Proyecciones, no entidades.** Las lecturas proyectan directo a `record` con los campos que la
respuesta necesita. Nunca se materializa una entidad completa para devolver tres campos.

## 3.11 Estrategia de testing

171 tests en tres niveles, con criterios distintos.

| Suite | Cantidad | Qué verifica |
|---|---|---|
| `Cocos.UnitTests` | 87 | Aritmética monetaria (`floor`, PPP, rendimientos), máquina de estados, cobertura de la cuenta y normalización de las entradas de texto. Con `FakeTimeProvider`. |
| `Cocos.ArchitectureTests` | 10 | Reglas de capas, contratos inmutables, convenciones de handlers, prohibición de `DateTime.Now`. |
| `Cocos.IntegrationTests` | 74 | La API completa contra PostgreSQL real. |

**Por qué PostgreSQL real y no el provider in-memory de EF.** In-memory **no implementa locking de
filas**, que es exactamente el mecanismo bajo prueba. Un test de concurrencia contra in-memory pasa
siempre y no demuestra absolutamente nada.

**Aislamiento entre tests.** Se siembra una base plantilla y cada clase de test saca su copia con
`CREATE DATABASE ... TEMPLATE`. Aislamiento total sin pagar el arranque de un servidor por clase.

**Portabilidad.** Por defecto la suite levanta PostgreSQL con TestContainers. Si existe la variable
`COCOS_TEST_DB` usa ese servidor y TestContainers ni se carga — lo que permite correr los tests
dentro del propio `docker compose` **sin exponer el socket de Docker ni recurrir a
Docker-in-Docker**.

**El test central.** El que demuestra que la arquitectura cumple su objetivo:

```csharp
// 20 órdenes en paralelo, cada una comprometiendo $100.000, contra $627.500 disponibles
respuestas.Count(o => o.Status == "NEW").Should().Be(6);
respuestas.Count(o => o.Status == "REJECTED").Should().Be(14);
final.AvailableCash.Should().BeGreaterThanOrEqualTo(0m);
```

Hay uno equivalente para ventas concurrentes sobre la tenencia, y otro que verifica que 10
reintentos simultáneos con la misma clave resuelvan a una sola orden.

**Un test documenta un hallazgo sobre los datos provistos**, no un comportamiento propio:

```csharp
[Fact]
public async Task El_seed_provisto_arrastra_una_posicion_negativa_en_BMA()
```

Ver [5](#5-limitaciones-y-evolución).

**Verificación de arquitectura sobre el fuente.** La prohibición de `DateTime.Now` se comprueba
leyendo los archivos `.cs`, no por reflection: una llamada estática no deja rastro en la superficie
de tipos, así que ningún análisis de dependencias entre ensamblados puede detectarla.

---

# 4. Operación

## 4.1 Ejecución

```bash
docker compose up --build                        # API + PostgreSQL con datos
docker compose --profile test run --rm tests     # las tres suites
```

| | |
|---|---|
| Swagger | http://localhost:8080 |
| PostgreSQL | `localhost:5432` · `cocos` / `cocos` |

La API espera a que la base esté **healthy**, no solo a que el contenedor arranque: el
`depends_on` usa `condition: service_healthy` contra un `pg_isready`.

Alternativa con el SDK instalado:

```bash
docker compose up -d db
dotnet run --project src/Cocos.Api      # Swagger en http://localhost:5080
dotnet test
```

## 4.2 Configuración

| Variable | Default | Uso |
|---|---|---|
| `ConnectionStrings__Cocos` | `Host=localhost;Port=5432;Database=cocos;…` | Conexión de la API |
| `Orders__ExpirationCheckInterval` | `00:05:00` | Frecuencia del barrido de vencidas |
| `COCOS_TEST_DB` | — | Servidor para los tests; si está, no se usa TestContainers |

## 4.3 El job de expiración

Un `BackgroundService` dispara periódicamente un único `UPDATE` masivo condicional:

```sql
UPDATE orders SET status = 'EXPIRED'
WHERE status IN ('NEW','PARTIALLY_FILLED') AND expiresat IS NOT NULL AND expiresat <= @Now;
```

**Es idempotente por construcción**: el filtro por estado hace que una segunda corrida afecte cero
filas. Por eso puede correr en N instancias sin *leader election* ni mecanismo de *claim* — la
primera gana y las demás no hacen nada. El "ahora" viene de `TimeProvider`, así que el
comportamiento se puede testear sin esperar al cierre de la jornada.

Un fallo puntual no mata el servicio: se loguea y el próximo tick reintenta, sin efectos
duplicados.

## 4.4 Observabilidad

Logging estructurado vía `ILogger`. Los eventos con nombre propio son: request cancelado por el
cliente (nivel *Information*, no *Error*), error no controlado con método y ruta, cantidad de
órdenes vencidas por corrida, y fallo del barrido con el intervalo de reintento.

---

# 5. Limitaciones y evolución

## 5.1 Inconsistencia detectada en los datos provistos

El usuario 1 arrastra una **posición negativa** en BMA:

```
BUY  20 @ 1540  FILLED   →  +20
SELL 30 @ 1530  FILLED   →  −30
                             ───
                   posición  −10
```

Existe una `BUY 60 @ 1500` en estado `NEW` que solo cerraría el número **si se contaran las
órdenes NEW**, cosa que el enunciado prohíbe explícitamente ("usar las órdenes en estado
`FILLED`").

**No se modificó el seed.** La API no puede *generar* este estado —las validaciones lo impiden—
pero sí lo refleja si ya está en los datos. El portfolio muestra la realidad, no la maquilla. Hay
un test que fija ese comportamiento para que un cambio futuro no lo oculte por accidente.

## 5.2 Limitaciones conocidas

| Limitación | Impacto | Cómo se resolvería |
|---|---|---|
| Misma `Idempotency-Key` con body distinto devuelve la orden original | El cliente cree que se ejecutó lo que mandó recién | Guardar un hash del request y responder `409` si no coincide |
| Las claves de idempotencia no expiran | Ninguno en la práctica con UUID aleatorio | TTL, como el de 24 h de Stripe |
| PPP mezcla ciclos si una posición se cierra y se reabre | Rendimiento distorsionado en ese caso | Resetear el costo al llegar a cero; rompe la agregación en una sola pasada |
| El disponible recorre todo el historial de órdenes | Degrada con el volumen por usuario | Snapshot materializado, ver abajo |
| Sin paginación por cursor | Páginas profundas son costosas (`OFFSET`) | Keyset pagination sobre `(datetime, id)` |

## 5.3 Evolución

Fuera de alcance **por decisión explícita**, no por omisión:

**Settlement T+0/T+1.** El poder de compra real de un broker depende del plazo de liquidación: lo
vendido hoy puede reinvertirse pero no necesariamente retirarse. Se agregaría una columna
`settlementdate` y el disponible pasaría a filtrar por fecha de liquidación.

**Matching engine.** Para que las órdenes `LIMIT` se ejecuten hace falta contraparte. El diseño
sería *single-writer* por instrumento: particionar por `instrumentid` con un único procesador por
partición, procesamiento secuencial dentro de cada una y prioridad precio-tiempo derivada de una
secuencia monotónica de la base — nunca del reloj de la aplicación, que difiere entre nodos.
Ahí aparecería el eje "instrumento" que hoy no existe, con sus *hot rows*.

**Balances materializados.** Snapshot por `(userid, instrumentid)` con cantidad, costo promedio y
reservas, actualizado dentro de la misma transacción que la orden. El ledger sigue siendo la
fuente de verdad; el snapshot es una caché consistente. Convierte el cálculo del disponible de
O(historial) a O(1).

**Transactional outbox.** Cuando aparezcan efectos externos —ruteo real al mercado,
notificaciones— no pueden ejecutarse dentro de la transacción de negocio: no existe commit atómico
entre una base de datos y un socket. Se escribiría el evento en una tabla `outbox` en la misma
transacción y un relay lo publicaría. Wolverine ya trae esa capacidad sobre EF Core.

**Otros:** horario de mercado y estados de rueda, política de precios stale, *slippage* y *price
collar* al reservar, corporate actions (splits y cambios de ratio de CEDEAR, que rompen el cálculo
de tenencia hecho sobre el histórico), comisiones e impuestos.
