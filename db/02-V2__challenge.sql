-- ============================================================================
--  Cocos challenge - cambios de esquema sobre la base provista
--  Cada cambio esta justificado. El archivo 01-database.sql NO se modifica.
--
--  Convencion de tiempo: todos los timestamps se guardan en UTC. Se mantiene
--  TIMESTAMP (sin timezone) por consistencia con la columna "datetime" que ya
--  existia; mezclar timestamp y timestamptz en la misma tabla obliga a convertir
--  en cada comparacion y es una fuente silenciosa de bugs.
-- ============================================================================

-- ----------------------------------------------------------------------------
-- 1. FILLED PARCIALES
--    La tabla original no distingue cantidad SOLICITADA de cantidad EJECUTADA:
--    solo tiene "size". Sin esa separacion los fills parciales son imposibles de
--    representar y la tenencia se calcula mal en cuanto una orden se ejecuta a
--    medias. "filledsize" es el acumulado realmente ejecutado.
-- ----------------------------------------------------------------------------
ALTER TABLE "orders" ADD COLUMN IF NOT EXISTS filledsize INT NOT NULL DEFAULT 0;

-- Backfill: las órdenes FILLED historicas se ejecutaron completas por definicion.
UPDATE "orders" SET filledsize = size WHERE status = 'FILLED' AND filledsize = 0;

-- ----------------------------------------------------------------------------
-- 2. VIGENCIA DIARIA DE LAS ORDENES LIMIT
--    Sin expiracion una orden NEW vive para siempre reservando fondos del usuario.
--    Las LIMIT son DAY: expiran al cierre de la jornada en que se enviaron.
-- ----------------------------------------------------------------------------
ALTER TABLE "orders" ADD COLUMN IF NOT EXISTS expiresat TIMESTAMP NULL;

-- Las NEW preexistentes del seed no tienen vencimiento asignado: se les da el
-- cierre del dia en que fueron enviadas, coherente con la regla que introducimos.
UPDATE "orders"
   SET expiresat = date_trunc('day', datetime) + INTERVAL '1 day' - INTERVAL '1 microsecond'
 WHERE status = 'NEW' AND expiresat IS NULL;

-- ----------------------------------------------------------------------------
-- 3. MOMENTO DE LA CANCELACION
--    La respuesta de POST /api/orders/{id}/cancel informa cuando se cancelo la orden,
--    y sin esta columna ese instante no quedaba en ningun lado: el cliente recibia un
--    dato que despues nadie podia reproducir ni auditar. "datetime" es el alta de la
--    orden y no se toca; el status CANCELLED dice QUE paso, no CUANDO.
--
--    Es NULL para todo lo demas, y el CHECK de la seccion 7 lo enuncia: solo una orden
--    CANCELLED puede tener el dato. Se acepta CANCELLED con NULL porque las del seed
--    son anteriores a la columna y no hay de donde sacarles la fecha.
-- ----------------------------------------------------------------------------
ALTER TABLE "orders" ADD COLUMN IF NOT EXISTS cancelledat TIMESTAMP NULL;

-- ----------------------------------------------------------------------------
-- 4. IDEMPOTENCIA DE COMANDOS
--    Un reintento del cliente (perdida de senal, doble tap) crea una orden
--    duplicada: el usuario compra dos veces. La propia orden hace de registro de
--    idempotencia, no hace falta una tabla aparte.
--
--    El indice es PARCIAL por costo, no por correctitud: en Postgres los NULL son
--    distintos entre si dentro de un indice unico (NULLS DISTINCT es el default), asi
--    que las órdenes sin clave nunca colisionarian ni aunque se indexaran todas. Pero
--    el header es opcional y la mayoria de las órdenes no lo llevan: sin el WHERE, el
--    indice guardaria una entrada por cada fila de la tabla y cada INSERT pagaria ese
--    mantenimiento, en el camino mas caliente del sistema. Ademas el predicado enuncia
--    la regla con precision: para las órdenes que declaran clave, esa clave es unica
--    dentro de la cuenta.
--
--    Va compuesto con userid porque la clave la genera el cliente: sin ese scope, la
--    clave de un usuario bloquearia la de otro y se rechazarian órdenes legitimas.
-- ----------------------------------------------------------------------------
ALTER TABLE "orders" ADD COLUMN IF NOT EXISTS idempotencykey TEXT NULL;

CREATE UNIQUE INDEX IF NOT EXISTS ux_orders_idempotency
    ON "orders"(userid, idempotencykey)
    WHERE idempotencykey IS NOT NULL;

-- ----------------------------------------------------------------------------
-- 5. PUNTO DE SERIALIZACION POR CUENTA  <-- la pieza central del diseno
--    El cash y la tenencia no son columnas: son agregados sobre "orders". Dos
--    requests concurrentes del mismo usuario pueden leer el mismo disponible,
--    validar cada uno por separado e insertar filas DISTINTAS. No colisionan
--    entre si, asi que ni REPEATABLE READ lo evita (write skew clasico): el
--    conflicto esta en la suma, no en ninguna fila.
--
--    Esta tabla existe solo para darle a Postgres una fila concreta que bloquear
--    con SELECT ... FOR UPDATE al inicio de la transacción, convirtiendo un
--    conflicto sobre un agregado en uno sobre una fila, que si sabe resolver.
--    Como las cuentas son independientes, no hay contencion entre usuarios.
-- ----------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS user_accounts (
    userid INT PRIMARY KEY REFERENCES users(id)
);

INSERT INTO user_accounts (userid)
SELECT id FROM users
ON CONFLICT (userid) DO NOTHING;

-- ----------------------------------------------------------------------------
-- 6. INDICES
--    La base provista no tiene ninguno mas alla de las PK. Estos son exactamente
--    los accesos que hace la API.
-- ----------------------------------------------------------------------------

-- calculo de pesos disponibles (agrega todas las órdenes del usuario)
CREATE INDEX IF NOT EXISTS ix_orders_user_status
    ON "orders"(userid, status);

-- tenencia y posiciones por instrumento
CREATE INDEX IF NOT EXISTS ix_orders_user_instrument_status
    ON "orders"(userid, instrumentid, status);

-- job de expiracion: barre solo las vivas ya vencidas
CREATE INDEX IF NOT EXISTS ix_orders_status_expiresat
    ON "orders"(status, expiresat)
    WHERE expiresat IS NOT NULL;

-- ultimo close por instrumento (el LEFT JOIN LATERAL del portfolio)
CREATE INDEX IF NOT EXISTS ix_marketdata_instrument_date
    ON "marketdata"(instrumentid, "date" DESC);

-- búsqueda por ticker y por nombre sin full scan.
-- ILIKE '%texto%' no puede usar un btree; trigram si.
CREATE EXTENSION IF NOT EXISTS pg_trgm;
CREATE EXTENSION IF NOT EXISTS unaccent;

-- Envoltorio IMMUTABLE de unaccent(). El unaccent() de la extension es STABLE -- depende del
-- diccionario, que se puede cambiar -- y Postgres no indexa expresiones no inmutables. Fijar el
-- diccionario por nombre lo vuelve determinista y habilita el indice de abajo. Es la receta
-- estandar; sin ella la búsqueda sin tildes seria un full scan.
CREATE OR REPLACE FUNCTION f_unaccent(text) RETURNS text
    LANGUAGE sql IMMUTABLE STRICT PARALLEL SAFE AS
$$ SELECT public.unaccent('public.unaccent', $1) $$;

-- El indice va sobre la MISMA expresion que usa la consulta, o el planner no lo puede usar.
-- Buscar "zorraquin" tiene que encontrar "Zorraquin S.A.": nadie escribe las tildes en un
-- buscador, y devolver cero resultados por una tilde es un error de producto, no una sutileza.
DROP INDEX IF EXISTS ix_instruments_trgm;
CREATE INDEX IF NOT EXISTS ix_instruments_trgm
    ON instruments USING GIN (f_unaccent(ticker) gin_trgm_ops, f_unaccent("name") gin_trgm_ops);

-- ----------------------------------------------------------------------------
-- 7. INVARIANTES EN LA BASE
--    Ultima linea de defensa. Si un bug de aplicacion se cuela, la base rechaza.
--    En un sistema que mueve dinero de terceros esto no es paranoia.
-- ----------------------------------------------------------------------------
ALTER TABLE "orders" ALTER COLUMN instrumentid SET NOT NULL;
ALTER TABLE "orders" ALTER COLUMN userid       SET NOT NULL;
ALTER TABLE "orders" ALTER COLUMN size         SET NOT NULL;
ALTER TABLE "orders" ALTER COLUMN price        SET NOT NULL;
ALTER TABLE "orders" ALTER COLUMN side         SET NOT NULL;
ALTER TABLE "orders" ALTER COLUMN status       SET NOT NULL;
ALTER TABLE "orders" ALTER COLUMN type         SET NOT NULL;
ALTER TABLE "orders" ALTER COLUMN datetime     SET NOT NULL;

ALTER TABLE "orders" DROP CONSTRAINT IF EXISTS ck_orders_status;
ALTER TABLE "orders" ADD  CONSTRAINT ck_orders_status
    CHECK (status IN ('NEW','FILLED','PARTIALLY_FILLED','REJECTED','CANCELLED','EXPIRED'));

ALTER TABLE "orders" DROP CONSTRAINT IF EXISTS ck_orders_side;
ALTER TABLE "orders" ADD  CONSTRAINT ck_orders_side
    CHECK (side IN ('BUY','SELL','CASH_IN','CASH_OUT'));

ALTER TABLE "orders" DROP CONSTRAINT IF EXISTS ck_orders_type;
ALTER TABLE "orders" ADD  CONSTRAINT ck_orders_type
    CHECK (type IN ('MARKET','LIMIT'));

ALTER TABLE "orders" DROP CONSTRAINT IF EXISTS ck_orders_sizes;
ALTER TABLE "orders" ADD  CONSTRAINT ck_orders_sizes
    CHECK (size > 0 AND filledsize >= 0 AND filledsize <= size);

ALTER TABLE "orders" DROP CONSTRAINT IF EXISTS ck_orders_price;
ALTER TABLE "orders" ADD  CONSTRAINT ck_orders_price
    CHECK (price > 0);

-- Una orden LIMIT viva tiene que tener vencimiento; una MARKET nunca queda viva.
ALTER TABLE "orders" DROP CONSTRAINT IF EXISTS ck_orders_limit_expires;
ALTER TABLE "orders" ADD  CONSTRAINT ck_orders_limit_expires
    CHECK (status NOT IN ('NEW','PARTIALLY_FILLED') OR expiresat IS NOT NULL);

-- "filledsize" es lo ejecutado, y las cuentas de cash y tenencia lo suman SIN mirar el
-- estado: una ejecucion es un hecho consumado y cancelar o vencer el remanente no la borra.
-- Ese CHECK es lo que vuelve segura esa lectura: las unicas filas con filledsize > 0 son
-- las que realmente ejecutaron algo. Una NEW todavia no ejecuto nada -- en cuanto lo hace
-- pasa a PARTIALLY_FILLED -- y una REJECTED no ejecuto nunca.
ALTER TABLE "orders" DROP CONSTRAINT IF EXISTS ck_orders_filled_solo_si_ejecuto;
ALTER TABLE "orders" ADD  CONSTRAINT ck_orders_filled_solo_si_ejecuto
    CHECK (status NOT IN ('NEW','REJECTED') OR filledsize = 0);

-- Solo una orden cancelada tiene fecha de cancelacion.
ALTER TABLE "orders" DROP CONSTRAINT IF EXISTS ck_orders_cancelledat;
ALTER TABLE "orders" ADD  CONSTRAINT ck_orders_cancelledat
    CHECK (cancelledat IS NULL OR status = 'CANCELLED');
