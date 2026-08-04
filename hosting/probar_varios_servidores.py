"""Prueba que una maquina pueda hostear VARIOS servidores, cada uno con su instalacion.

    python hosting/probar_varios_servidores.py [--servidores N]

EL MODELO que se verifica es el de cualquier juego: una instalacion completa por servidor, cada una en su
carpeta. No se comparte nada entre ellos, y eso NO es desperdicio -- es lo que hace que cada servidor pueda
tener sus mapas, su data del juego, su configuracion y su version.

Lo que comprueba, en orden:
  1. cada servidor levanta sus 4 puertos (juego, WebSocket, panel, cliente web) sin pisar los del vecino
  2. CADA UNO CARGA SU PROPIA DATA DEL JUEGO: a uno se le saca la mitad de los hechizos y su log tiene que
     reportar un numero distinto al de los otros. Es la prueba de que se puede tener uno vanilla y otro
     modificado en la misma maquina.
  3. un personaje creado en uno NO existe en el otro, y el mismo nombre queda libre alla
  4. cada uno escribe su propio log, y el log dice de QUIEN es
  5. cuanta RAM cuesta cada servidor, que es lo que decide cuantos entran en una maquina
  6. el apagado es LIMPIO: guarda a los conectados en vez de morir de un kill

Corre con el MUNDO REAL (863 mapas, 5404 NPCs), no con --fixture: el fixture carga una fraccion y daria un
numero de memoria que no sirve para dimensionar nada.
"""
import json, os, re, shutil, socket, subprocess, sys, time
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(REPO / "fase0" / "parity"))
sys.path.insert(0, str(REPO / "fase0" / "codec"))
from compare_actions import Conn                                          # noqa: E402

FUENTE = REPO / "server/src/CoverAO.Server/bin/Release/net10.0"
GAMEDATA = REPO / "gamedata"
RAIZ = Path(os.environ.get("TEMP", "/tmp")) / "coverao-hosting"
N = int(sys.argv[sys.argv.index("--servidores") + 1]) if "--servidores" in sys.argv else 2
NOMBRES = ["rojo", "azul", "verde", "negro", "dorado", "plata"][:N]
PUERTO0 = 7700                     # de a 10: cada servidor usa base..base+3


def esperar_puerto(p, segundos=90):
    fin = time.time() + segundos
    while time.time() < fin:
        try:
            s = socket.create_connection(("127.0.0.1", p), 1); s.close(); return True
        except OSError:
            time.sleep(0.4)
    return False


def escucha(p):
    try:
        s = socket.create_connection(("127.0.0.1", p), 1); s.close(); return True
    except OSError:
        return False


def rss_mb(pid):
    """RAM del proceso. tasklist en Windows, /proc en Linux: la prueba corre en los dos."""
    if os.name == "nt":
        out = subprocess.run(["tasklist", "/FI", f"PID eq {pid}", "/FO", "CSV", "/NH"],
                             capture_output=True, text=True).stdout
        try:
            return int(out.split('","')[4].replace('"', "").replace(".", "").replace(",", "").split()[0]) / 1024
        except (IndexError, ValueError):
            return -1
    try:
        for linea in open(f"/proc/{pid}/status"):
            if linea.startswith("VmRSS:"):
                return int(linea.split()[1]) / 1024
    except OSError:
        pass
    return -1


def instalar(destino):
    """Una instalacion completa. Los binarios van con ENLACE DURO en vez de copia -- son el mismo archivo
    en disco, asi la prueba no tarda una eternidad, pero cada servidor sigue teniendo su propia CARPETA con
    su data del juego, su Server.ini y su data/. Un host de verdad copia y listo."""
    destino.mkdir(parents=True, exist_ok=True)
    for f in FUENTE.iterdir():
        if f.is_file():
            dst = destino / f.name
            if dst.exists(): continue
            try: os.link(f, dst)
            except OSError: shutil.copy2(f, dst)
    # La data del juego SI se copia de verdad: es justo lo que cada servidor tiene que poder cambiar.
    if not (destino / "gamedata").exists():
        shutil.copytree(GAMEDATA, destino / "gamedata")
    (destino / "data").mkdir(exist_ok=True)


fallos, procs = [], []
if RAIZ.exists():
    shutil.rmtree(RAIZ, ignore_errors=True)
print(f"servidores en: {RAIZ}\n")

for i, nombre in enumerate(NOMBRES):
    d = RAIZ / nombre
    instalar(d)
    (d / "Server.ini").write_text(f"[INIT]\nStartPort={PUERTO0 + i * 10}\nMinutosBackup=0\n", encoding="ascii")

# --- 2 (preparacion). Al SEGUNDO servidor se le recorta la data del juego ---
# Es la prueba de que cada uno corre SU mundo: se le sacan hechizos y su log tiene que decir otro numero.
hechizos = RAIZ / NOMBRES[1] / "gamedata" / "Hechizos.json"
datos = json.loads(hechizos.read_text(encoding="utf-8"))
# OJO con la forma: el archivo es un objeto {meta, HECHIZO:[...]}, no una lista. Recortando el objeto
# entero la mutacion no hacia NADA y la prueba fallaba culpando al servidor -- que en realidad estaba
# leyendo bien su propia carpeta (el log lo dice: "Datos cargados desde .../azul/gamedata").
datos["HECHIZO"] = datos["HECHIZO"][:len(datos["HECHIZO"]) // 2]
hechizos.write_text(json.dumps(datos), encoding="utf-8")
print(f"a '{NOMBRES[1]}' se le dejaron {len(datos['HECHIZO'])} hechizos de los 113 originales\n")

for i, nombre in enumerate(NOMBRES):
    p = subprocess.Popen([str(RAIZ / nombre / ("CoverAO.Server.exe" if os.name == "nt" else "CoverAO.Server"))]
                         if (RAIZ / nombre / ("CoverAO.Server.exe" if os.name == "nt" else "CoverAO.Server")).exists()
                         else ["dotnet", "CoverAO.Server.dll"],
                         cwd=RAIZ / nombre, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    procs.append((nombre, p, PUERTO0 + i * 10))

try:
    print("--- 1. puertos ---")
    for nombre, p, base in procs:
        if not esperar_puerto(base):
            fallos.append(f"{nombre}: no abrio el puerto {base}"); continue
        time.sleep(1.5)
        estado = {n: escucha(base + o) for n, o in (("juego", 0), ("websocket", 1), ("panel", 2), ("web", 3))}
        print(f"  {nombre:8} {base}..{base + 3}  " + "  ".join(f"{n}={'ok' if v else '--'}" for n, v in estado.items()))
        # El cliente web se sirve solo si la instalacion lo trae empaquetado (lo arma publish.ps1).
        obligatorios = ["juego", "websocket", "panel"] + (["web"] if (RAIZ / nombre / "webclient").exists() else [])
        faltan = [n for n in obligatorios if not estado[n]]
        if faltan: fallos.append(f"{nombre}: no escucha {faltan}")

    def log_de(nombre):
        logs = sorted((RAIZ / nombre / "data/logs").glob("*"))
        return logs[-1].read_text(encoding="utf-8", errors="replace") if logs else ""

    print("\n--- 2. cada uno con SU data del juego ---")
    cargas = {}
    for nombre, _, _ in procs:
        linea = next((l for l in log_de(nombre).splitlines() if "Datos cargados" in l), "")
        cargas[nombre] = linea.split("gamedata:")[-1].strip() if linea else ""
        print(f"  {nombre:8} {cargas[nombre][:88]}")
    if not cargas.get(NOMBRES[0]):
        fallos.append("no pude leer que data cargo el primer servidor")
    elif cargas.get(NOMBRES[0]) == cargas.get(NOMBRES[1]):
        fallos.append("los dos cargaron la MISMA data pese a que a uno le recorte los hechizos")
    else:
        print(f"  -> a '{NOMBRES[1]}' se le saco la mitad de los hechizos y cargo distinto: cada servidor")
        print("     corre SU mundo (sus mapas, sus objetos, sus hechizos), como tiene que ser.")

    print("\n--- 3. aislamiento de personajes ---")
    base_a, base_b = procs[0][2], procs[1][2]
    c = Conn(base_a, "cuenta_solo_en_a", "SoloEnRojo")
    if c.key is None:
        fallos.append("no pude crear el personaje en el primer servidor")
    else:
        c.close(); time.sleep(1.5)
        en_a = (RAIZ / NOMBRES[0] / "data/Charfile/SoloEnRojo.chr").exists()
        en_b = (RAIZ / NOMBRES[1] / "data/Charfile/SoloEnRojo.chr").exists()
        print(f"  el .chr creado en {NOMBRES[0]}:  en {NOMBRES[0]}={en_a}   en {NOMBRES[1]}={en_b}")
        if not en_a: fallos.append("el personaje no quedo en el disco de su propio servidor")
        if en_b: fallos.append("EL PERSONAJE APARECIO EN EL OTRO SERVIDOR: no hay aislamiento")
        c2 = Conn(base_b, "cuenta_solo_en_a", "SoloEnRojo")
        print(f"  el mismo nombre se puede crear de cero en {NOMBRES[1]}: {c2.key is not None}")
        if c2.key is None: fallos.append(f"{NOMBRES[1]} ve las cuentas de {NOMBRES[0]}")
        c2.close()

    print("\n--- 4. el log dice de quien es y que puertos usa ---")
    for nombre, _, base in procs:
        texto = log_de(nombre)
        dice = [l.strip()[:74] for l in texto.splitlines() if "Instalado en:" in l]
        print(f"  {nombre:8} {dice[0] if dice else 'NO DICE DONDE ESTA INSTALADO'}")
        if not dice: fallos.append(f"{nombre}: el log no identifica la instalacion")

        # EL MAPA DE PUERTOS TIENE QUE COINCIDIR CON LO QUE ESCUCHA DE VERDAD.
        # Un mapa impreso que no fuera el que se usa para abrir los sockets seria peor que no imprimir
        # nada: el host lo lee, abre eso en el router, y no entra nadie sin que nada explique por que.
        mapa = next((l for l in texto.splitlines() if "Puertos:" in l), "")
        print(f"           {mapa.split('Puertos:')[-1].strip()[:96] if mapa else 'NO IMPRIME EL MAPA DE PUERTOS'}")
        if not mapa:
            fallos.append(f"{nombre}: el log no dice que puertos usa"); continue
        anunciados = {int(n) for n in re.findall(r"\b(7[0-9]{3})\b", mapa)}
        escuchando = {base + o for o in (0, 1, 2, 3) if escucha(base + o)}
        # Se comparan solo los que estan efectivamente abiertos: el mapa puede anunciar el cliente web como
        # ausente ("—") y ahi no hay puerto que comparar.
        if not escuchando <= anunciados:
            fallos.append(f"{nombre}: escucha {sorted(escuchando - anunciados)} y el mapa no lo dice")
        de_mas = {p for p in anunciados if p not in escuchando}
        if de_mas:
            fallos.append(f"{nombre}: el mapa anuncia {sorted(de_mas)} pero ahi no escucha nadie")

    print("\n--- 5. RAM por servidor (mundo real) ---")
    total = 0
    for nombre, p, _ in procs:
        m = rss_mb(p.pid); total += max(m, 0)
        print(f"  {nombre:8} PID {p.pid:6}  {m:7.0f} MB")
    print(f"  {'TOTAL':8} {'':11} {total:7.0f} MB  ({total / max(len(procs), 1):.0f} MB por servidor)")

    # --- 6. apagado limpio, que es el propio desarme de la prueba ---
    #
    # En Windows un servidor de fondo NO TIENE VENTANA, asi que no hay a quien mandarle la senal de cierre:
    # medido, `taskkill /PID` sin /F salio en 87 ms sin guardar a nadie, igual que un Stop-Process -Force de
    # 24 ms. Un host que reinicia para actualizar le costaba a cada jugador conectado todo lo hecho desde el
    # ultimo autosave. El archivo `detener` existe por eso y esta prueba lo ejercita.
    print("\n--- 6. apagado limpio (archivo 'detener') ---")
    for nombre, _, _ in procs:
        (RAIZ / nombre / "detener").write_text("", encoding="ascii")
    for nombre, p, _ in procs:
        try:
            p.wait(timeout=120)
        except subprocess.TimeoutExpired:
            fallos.append(f"{nombre}: no se apago con el archivo 'detener'"); p.kill(); continue
        limpio = "Servidor detenido" in log_de(nombre)
        quedo = (RAIZ / nombre / "detener").exists()
        print(f"  {nombre:8} codigo de salida {p.returncode}   guardo y saludo={limpio}   quedo el archivo={quedo}")
        if not limpio: fallos.append(f"{nombre}: se apago sin pasar por el guardado")
        if quedo: fallos.append(f"{nombre}: quedo el archivo 'detener' (el proximo arranque se apagaria solo)")

finally:
    for _, p, _ in procs:
        if p.poll() is None:
            p.kill()

print()
if fallos:
    print("FALLOS:")
    for f in fallos: print("  - " + f)
else:
    print(f"OK: {N} servidores en la misma maquina, cada uno con su instalacion, sin pisarse.")
sys.exit(1 if fallos else 0)
