#!/usr/bin/env python3
"""
UAT: verifica la API contra el TEXTO del enunciado, no contra el código.

La diferencia con las tres suites de tests no es tecnica sino de origen. Aquellas nacen de
como entiendo yo el sistema; esta nace de las frases de cocos-challenge-backend.md, una por
una, y cada chequeo lleva la frase textual que verifica. Por eso es caja negra pura: HTTP y
JSON, sin tocar la base ni referenciar ningun ensamblado.

Uso:
    docker compose down -v && docker compose up --build -d     # seed limpio
    python3 uat/aceptacion.py [http://localhost:8080]

Sale con código 1 si algun requisito falla.
"""

import json
import sys
import urllib.error
import urllib.request

BASE = (sys.argv[1] if len(sys.argv) > 1 else "http://localhost:8080").rstrip("/")
USER = 1
PAMP, METR, DYCA, ARS = 47, 54, 1, 66


# --------------------------------------------------------------------------- HTTP

def _pedir(metodo, ruta, cuerpo=None, cabeceras=None):
    datos = json.dumps(cuerpo).encode() if cuerpo is not None else None
    pedido = urllib.request.Request(f"{BASE}{ruta}", data=datos, method=metodo)
    pedido.add_header("Accept", "application/json")
    if datos:
        pedido.add_header("Content-Type", "application/json")
    for k, v in (cabeceras or {}).items():
        pedido.add_header(k, v)

    try:
        with urllib.request.urlopen(pedido, timeout=30) as r:
            texto = r.read().decode()
            return r.status, (json.loads(texto) if texto else None)
    except urllib.error.HTTPError as e:
        texto = e.read().decode()
        try:
            return e.code, json.loads(texto)
        except json.JSONDecodeError:
            return e.code, texto


def get(ruta):
    return _pedir("GET", ruta)


def post(ruta, cuerpo=None, cabeceras=None):
    return _pedir("POST", ruta, cuerpo, cabeceras)


def portfolio():
    return get(f"/api/users/{USER}/portfolio")[1]


def posicion(ticker):
    return next((p for p in portfolio()["positions"] if p["ticker"] == ticker), None)


def enviar(**orden):
    return post("/api/orders", {"userId": USER, **orden})


def cerca(a, b, tol=0.01):
    return a is not None and b is not None and abs(a - b) <= tol


# --------------------------------------------------------------------------- registro

REQUISITOS = []


def requisito(seccion, cita):
    """Registra un chequeo. 'cita' es la frase del enunciado que verifica."""
    def envoltura(fn):
        REQUISITOS.append((seccion, cita, fn))
        return fn
    return envoltura


# --------------------------------------------------------------------------- endpoints

@requisito("Endpoints", "Portfolio: el valor total de la cuenta de un usuario")
def _():
    p = portfolio()
    esperado = p["accountingCash"] + sum(x["marketValue"] or 0 for x in p["positions"])
    assert cerca(p["totalAccountValue"], esperado), \
        f"totalAccountValue={p['totalAccountValue']} pero cash+posiciones={esperado}"
    return f"{p['totalAccountValue']:,.2f}"


@requisito("Endpoints", "Portfolio: sus pesos disponibles para operar")
def _():
    p = portfolio()
    assert cerca(p["availableCash"], p["accountingCash"] - p["reservedCash"]), \
        "el disponible tiene que ser el contable menos lo reservado por órdenes vivas"
    assert p["availableCash"] >= 0, "el disponible no puede ser negativo"
    return f"{p['availableCash']:,.2f} (contable {p['accountingCash']:,.2f} - reservado {p['reservedCash']:,.2f})"


@requisito("Endpoints", "Portfolio: listado de activos que posee, con cantidad de acciones")
def _():
    pos = {x["ticker"]: x["quantity"] for x in portfolio()["positions"]}
    assert pos.get("PAMP") == 40, f"PAMP deberia ser 40 y es {pos.get('PAMP')}"
    assert pos.get("METR") == 500, f"METR deberia ser 500 y es {pos.get('METR')}"
    return ", ".join(f"{t} {q}" for t, q in sorted(pos.items()))


@requisito("Endpoints", "Portfolio: el valor total monetario de la posicion ($)")
def _():
    pamp = posicion("PAMP")
    assert cerca(pamp["marketValue"], pamp["quantity"] * pamp["close"]), \
        "el valor de mercado tiene que ser cantidad x último close"
    return f"PAMP {pamp['quantity']} x {pamp['close']} = {pamp['marketValue']:,.2f}"


@requisito("Endpoints", "Portfolio: el rendimiento total (%)")
def _():
    pamp = posicion("PAMP")
    esperado = (pamp["close"] - pamp["averageCost"]) / pamp["averageCost"] * 100
    assert cerca(pamp["totalReturnPercent"], esperado, 0.0001), \
        f"rendimiento {pamp['totalReturnPercent']} contra PPP {pamp['averageCost']}"
    return f"PAMP {pamp['totalReturnPercent']:.4f}%"


@requisito("Endpoints", "Buscar activos: soportar búsqueda por ticker")
def _():
    _, r = get("/api/instruments?search=PAMP")
    assert any(i["ticker"] == "PAMP" for i in r["items"]), "buscar 'PAMP' no encontro PAMP"
    return f"{r['totalCount']} resultado(s)"


@requisito("Endpoints", "Buscar activos: soportar búsqueda por nombre")
def _():
    _, r = get("/api/instruments?search=pampa")
    assert any(i["ticker"] == "PAMP" for i in r["items"]), "buscar 'pampa' no encontro PAMP"
    return f"{r['totalCount']} resultado(s) buscando por nombre en minuscula"


# --------------------------------------------------------------------------- órdenes

@requisito("Ordenes", "Cuando un usuario manda una orden de tipo MARKET, se ejecuta inmediatamente y el estado es FILLED")
def _():
    código, o = enviar(instrumentId=PAMP, side="BUY", type="MARKET", size=1)
    assert código == 201, f"se esperaba 201 y vino {código}"
    assert o["status"] == "FILLED", f"status {o['status']}"
    return f"orden {o['id']} FILLED a {o['price']}"


@requisito("Ordenes", "Cuando se envía una orden de tipo MARKET, utilizar el último precio (close)")
def _():
    close = posicion("PAMP")["close"]
    _, o = enviar(instrumentId=PAMP, side="BUY", type="MARKET", size=1)
    assert cerca(o["price"], close), f"se ejecuto a {o['price']} y el último close es {close}"
    return f"ejecutada a {o['price']}"


@requisito("Ordenes", "Cuando un usuario manda una orden de tipo LIMIT, el estado tiene que ser NEW")
def _():
    _, o = enviar(instrumentId=PAMP, side="BUY", type="LIMIT", size=1, price=100)
    assert o["status"] == "NEW", f"status {o['status']}"
    return f"orden {o['id']} NEW"


@requisito("Ordenes", "side describe si la orden es de compra (BUY) o venta (SELL)")
def _():
    _, compra = enviar(instrumentId=PAMP, side="BUY", type="MARKET", size=1)
    _, venta = enviar(instrumentId=PAMP, side="SELL", type="MARKET", size=1)
    assert compra["side"] == "BUY" and venta["side"] == "SELL"
    return "BUY y SELL aceptados y persistidos"


@requisito("Ordenes", "Es necesario enviar la cantidad de acciones que quiere comprar o vender")
def _():
    _, o = enviar(instrumentId=PAMP, side="BUY", type="MARKET", size=7)
    assert o["size"] == 7, f"se pidieron 7 y quedaron {o['size']}"
    return "size=7 respetado"


@requisito("Ordenes", "Permitir enviar un monto total de inversion: calcular la cantidad máxima de acciones, no se admiten fracciones")
def _():
    close = posicion("PAMP")["close"]
    monto = 100_000
    _, o = enviar(instrumentId=PAMP, side="BUY", type="MARKET", amount=monto)
    esperado = int(monto // close)
    assert o["size"] == esperado, f"{monto}/{close} deberia dar {esperado} y dio {o['size']}"
    assert float(o["size"]).is_integer(), "no se admiten fracciones de accion"
    return f"{monto}/{close} = {o['size']} acciones enteras (sobran {monto - o['size'] * close:,.2f})"


@requisito("Ordenes", "Si la orden es por un monto mayor al disponible, se rechaza y se guarda en estado REJECTED")
def _():
    código, o = enviar(instrumentId=PAMP, side="BUY", type="MARKET", size=10_000_000)
    assert o["status"] == "REJECTED", f"status {o['status']}"
    _, listado = get(f"/api/users/{USER}/orders?status=REJECTED&pageSize=100")
    assert any(x["id"] == o["id"] for x in listado["items"]), "la rechazada tiene que quedar guardada"
    return f"orden {o['id']} REJECTED y persistida (HTTP {código})"


@requisito("Ordenes", "En la compra validar que el usuario tiene los pesos suficientes")
def _():
    disponible = portfolio()["availableCash"]
    _, o = enviar(instrumentId=PAMP, side="BUY", type="LIMIT", size=1_000_000, price=1000)
    assert o["status"] == "REJECTED", "una compra por encima del disponible no puede aceptarse"
    assert cerca(portfolio()["availableCash"], disponible), "una rechazada no puede reservar nada"
    return "compra por encima del disponible rechazada, sin reservar"


@requisito("Ordenes", "En la venta validar que el usuario tiene las acciones suficientes")
def _():
    tenencia = posicion("METR")["quantity"]
    _, o = enviar(instrumentId=METR, side="SELL", type="MARKET", size=tenencia + 1)
    assert o["status"] == "REJECTED", f"vender {tenencia + 1} teniendo {tenencia} no puede aceptarse"
    return f"vender {tenencia + 1} teniendo {tenencia}: REJECTED"


@requisito("Ordenes", "CANCELLED: cuando la orden es cancelada por el usuario")
def _():
    _, viva = enviar(instrumentId=PAMP, side="BUY", type="LIMIT", size=1, price=100)
    código, r = post(f"/api/orders/{viva['id']}/cancel?userId={USER}")
    assert código == 200 and r["status"] == "CANCELLED", f"HTTP {código} / {r}"
    return f"orden {viva['id']} CANCELLED"


@requisito("Ordenes", "Solo se pueden cancelar las órdenes con estado NEW")
def _():
    _, ejecutada = enviar(instrumentId=PAMP, side="BUY", type="MARKET", size=1)
    código, _r = post(f"/api/orders/{ejecutada['id']}/cancel?userId={USER}")
    assert código == 409, f"cancelar una FILLED deberia rechazarse y vino HTTP {código}"
    return "cancelar una FILLED devuelve 409"


@requisito("Ordenes", "La orden quedara grabada en la tabla orders con el estado y valores correspondientes")
def _():
    _, o = enviar(instrumentId=PAMP, side="BUY", type="LIMIT", size=3, price=111)
    _, listado = get(f"/api/users/{USER}/orders?pageSize=100")
    fila = next((x for x in listado["items"] if x["id"] == o["id"]), None)
    assert fila is not None, "la orden no aparece en el listado del usuario"
    assert (fila["side"], fila["type"], fila["status"], fila["size"], fila["price"]) \
        == ("BUY", "LIMIT", "NEW", 3, 111), f"los valores no coinciden: {fila}"
    return f"orden {o['id']} legible con side/type/status/size/price correctos"


# --------------------------------------------------------------------------- posiciones y cash

@requisito("Posiciones", "Cuando una orden es ejecutada, se tiene que actualizar el listado de posiciones del usuario")
def _():
    antes = posicion("PAMP")["quantity"]
    enviar(instrumentId=PAMP, side="BUY", type="MARKET", size=4)
    despues = posicion("PAMP")["quantity"]
    assert despues == antes + 4, f"{antes} + 4 deberia dar {antes + 4} y dio {despues}"
    return f"PAMP {antes} -> {despues}"


@requisito("Posiciones", "Una compra ejecutada de un activo no tenido agrega la posicion al listado")
def _():
    assert posicion("DYCA") is None, "el usuario 1 no deberia tener DYCA en el seed limpio"
    enviar(instrumentId=DYCA, side="BUY", type="MARKET", size=2)
    nueva = posicion("DYCA")
    assert nueva is not None and nueva["quantity"] == 2, f"{nueva}"
    return "DYCA aparece con 2 acciones"


@requisito("Posiciones", "Para el calculo de cada posicion usar las órdenes en estado FILLED")
def _():
    antes = posicion("METR")["quantity"]
    enviar(instrumentId=METR, side="SELL", type="LIMIT", size=10, price=10_000)
    assert posicion("METR")["quantity"] == antes, \
        "una LIMIT viva no esta ejecutada: no puede cambiar la cantidad"
    return f"METR sigue en {antes} con una venta LIMIT viva"


@requisito("Posiciones", "El cash (ARS) esta modelado como un instrumento de tipo MONEDA")
def _():
    _, r = get("/api/instruments?search=ARS")
    ars = next((i for i in r["items"] if i["ticker"] == "ARS"), None)
    assert ars is not None and ars["type"] == "MONEDA", f"{ars}"
    assert posicion("ARS") is None, "el cash no se lista como una posicion mas"
    return "ARS es MONEDA y se informa como pesos disponibles"


@requisito("Posiciones", "Las transferencias se modelan como órdenes: CASH_IN entrantes, CASH_OUT salientes")
def _():
    _, listado = get(f"/api/users/{USER}/orders?pageSize=100")
    lados = {x["side"] for x in listado["items"]}
    assert "CASH_IN" in lados, "no hay transferencias entrantes en el historial del usuario"
    assert portfolio()["accountingCash"] > 0, "el cash contable sale de esos movimientos"
    return f"lados presentes en el historial: {', '.join(sorted(lados))}"


@requisito("Posiciones", "Para calcular el retorno diario utilizar las columnas close y previousClose")
def _():
    pamp = posicion("PAMP")
    assert pamp["dailyReturnPercent"] is not None, "no se informa el retorno diario"
    return f"PAMP {pamp['dailyReturnPercent']:.4f}% (close {pamp['close']})"


# --------------------------------------------------------------------------- desviaciones

DESVIACIONES = [
    ("Tecnicas", "Desarrollar la aplicacion utilizando Node.js",
     "NO SE CUMPLE. Resuelto en .NET 10 deliberadamente. Justificado en README.md."),
    ("Tecnicas", "Implementar un test funcional sobre la funcion para enviar una orden",
     "Cumplido fuera del alcance de esta UAT: 14 tests de integracion sobre POST /api/orders "
     "mas concurrencia. Verificable con 'docker compose --profile test run --rm tests'."),
]


# --------------------------------------------------------------------------- corrida

def main():
    try:
        código, _ = get(f"/api/users/{USER}/portfolio")
    except OSError as e:
        print(f"No se pudo hablar con {BASE}: {e}\nLevantar la app con 'docker compose up -d'.")
        return 2
    if código != 200:
        print(f"{BASE} respondio {código} al portfolio del usuario {USER}.")
        return 2

    inicial = portfolio()
    limpio = cerca(inicial["accountingCash"], 753_000)
    print(f"\n  UAT contra {BASE}")
    print(f"  Seed {'limpio' if limpio else 'YA MODIFICADO -- los números fijos pueden fallar'}"
          f" (cash contable {inicial['accountingCash']:,.2f})\n")

    ancho = 96
    fallidos = 0
    seccion_actual = None

    for seccion, cita, fn in REQUISITOS:
        if seccion != seccion_actual:
            print(f"  {seccion.upper()}")
            seccion_actual = seccion
        try:
            detalle = fn()
            print(f"    PASS  {cita[:ancho]}")
            if detalle:
                print(f"          -> {detalle}")
        except Exception as e:  # noqa: BLE001 - cualquier fallo es un requisito no cumplido
            fallidos += 1
            print(f"    FAIL  {cita[:ancho]}")
            print(f"          -> {e}")

    print(f"\n  DESVIACIONES DECLARADAS")
    for _, cita, nota in DESVIACIONES:
        print(f"    !     {cita}")
        print(f"          -> {nota}")

    total = len(REQUISITOS)
    print(f"\n  {total - fallidos}/{total} requisitos verificados, {fallidos} fallidos, "
          f"{len(DESVIACIONES)} desviaciones declaradas.\n")
    return 1 if fallidos else 0


if __name__ == "__main__":
    sys.exit(main())
