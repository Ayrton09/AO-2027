using System;
using System.Linq;
using CoverAO.Server.Game;

namespace CoverAO.Server.Net;

/// <summary>
/// EL CENTINELA ANTI-MACRO (VB6 <c>modCentinela.bas</c>, 379 líneas).
///
/// <para>Un NPC aparece al lado del que está trabajando y le pide una clave al azar por chat. Si no la
/// escribe con <c>/CENTINELA &lt;clave&gt;</c> en dos minutos, lo encarcela quince, le anota la pena en el
/// <c>.chr</c> y lo desconecta. Es lo que impide dejar al personaje minando toda la noche solo.</para>
///
/// <para><b>Por qué esto es una REGLA DE LA CASA y no paridad.</b> El módulo está completo en el original y
/// su NPC (622 en tierra, 623 en agua) está en <c>NPCs.dat</c>, pero sus dos únicos puntos de entrada están
/// COMENTADOS en la fuente —<c>'Call modCentinela.PasarMinutoCentinela</c> (frmMain.frm:749) y
/// <c>'Call modCentinela.CallUserAttention</c> (mMainLoop.bas:21)—, así que en el VB6 no corre nunca: nadie
/// es elegido y el comando <c>/CENTINELA</c> del jugador no tiene con qué comparar. Enchufarlo es apartarse
/// del install, y por eso va detrás de un interruptor que arranca <b>apagado</b>: el toggle de GM
/// <c>/CENTINELAACTIVADO</c> que ya existía (y que hasta hoy no gateaba nada), más la clave
/// <c>Centinela=1</c> del Server.ini para el host que lo quiera puesto desde el arranque.</para>
///
/// <para>Con el interruptor apagado el comportamiento observable es EXACTAMENTE el de antes: el
/// <c>/CENTINELA</c> del jugador se lee y se descarta en silencio, que es lo que hace el original.</para>
///
/// <para>Los textos son los del VB6, palabra por palabra, incluida la coma de "Saludos &lt;nombre&gt;," y el
/// "¿No ves?" del que contesta sin que le hablen.</para>
/// </summary>
public sealed partial class PacketDispatcher
{
    // Los dos NPCs del centinela (modCentinela.bas:38). El de agua es para el que está pescando.
    private const short NpcCentinelaTierra = 622, NpcCentinelaAgua = 623;
    /// <summary>Minutos que tiene el jugador para contestar (<c>TIEMPO_INICIAL</c>, modCentinela.bas:45).</summary>
    private const int CentinelaMinutos = 2;
    /// <summary>Minutos de cárcel al que no contesta (<c>Encarcelar(..., 15)</c>, modCentinela.bas:159).</summary>
    private const byte CentinelaCondena = 15;
    /// <summary>Cada cuántas vueltas del game-loop corre <see cref="PasarMinutoCentinela"/>. Un minuto, que
    /// es la cadencia del original (su llamador era el timer de un minuto del formulario).
    ///
    /// <para>Es mutable por la misma razón que <c>DayNight.PeriodTicks</c>: <c>centinela_flow</c> prueba una
    /// cosa que un test unitario no puede —que el NPC aparezca, hable por el socket y encarcele al que no
    /// contesta— y para verlo hay que ESPERARLO. Con el reloj de verdad son tres minutos por corrida.
    /// <c>--relojes-de-test</c> lo baja a un segundo; con el flag ausente vale el minuto y nadie lo toca.</para></summary>
    public static long TicksEntreVueltas = 60_000 / Game.GameLoop.TickMs;

    /// <summary>Cada cuántos minutos se olvida quién ya contestó y todos vuelven a ser candidatos. El
    /// original no fija el número (ver la nota en <see cref="PasarMinutoCentinela"/>); quince es lo que
    /// separa dos interrogatorios seguidos al mismo jugador sin dejar de ser un control real.</summary>
    private const int CentinelaMinutosDeRonda = 15;
    private int _centinelaMinutosDeRondaCorridos;
    /// <summary><c>FONTTYPE_CENTINELA</c> (Protocol.bas:297, decimocuarta del enum que arranca en 0).</summary>
    private const byte FuenteCentinela = 14;
    /// <summary><c>FONTTYPE_SERVER</c>, la del aviso a los admins.</summary>
    private const byte FuenteServer = 8;

    // Estado del centinela: hay UNO solo para todo el servidor y revisa a un jugador por vez, igual que el
    // original (`Public Centinela As tCentinela`, una sola instancia).
    private Player? _centinelaRevisando;
    private int _centinelaClave;
    private int _centinelaMinutosRestantes;
    private short _centinelaNpcCharIndex;
    private long _centinelaSpawnMs;

    /// <summary>Los que YA contestaron en esta ronda. El original lleva un flag por usuario
    /// (<c>flags.CentinelaOK</c>) que <c>ResetCentinelaInfo</c> limpia cada tanto; acá va un conjunto de
    /// nombres, que es lo mismo y no le agrega un campo al <c>Player</c> por un subsistema opcional.</summary>
    private readonly System.Collections.Generic.HashSet<string> _centinelaRespondieron =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>¿Está prendido? El toggle de GM manda sobre el Server.ini: el host puede arrancarlo puesto y
    /// un Dios apagarlo en vivo sin reiniciar.</summary>
    private bool CentinelaEncendido => _admin?.CentinelActive == true;

    /// <summary>Una vuelta de minuto (VB6 <c>PasarMinutoCentinela</c>, modCentinela.bas:273). La engancha el
    /// bloque por minuto del game-loop, al lado de las condenas de /CARCEL.</summary>
    public void PasarMinutoCentinela()
    {
        if (!CentinelaEncendido) { CentinelaRetirar(); _centinelaRespondieron.Clear(); return; }

        // EL RELOJ DE LA RONDA (VB6 `ResetCentinelaInfo`, modCentinela.bas:228). El original lo tiene escrito
        // pero no dice cada cuánto corre: su llamador es el timer de un formulario que en este install ni
        // siquiera existe, así que el período lo elige el port. Sin él la ronda se reiniciaría en cuanto no
        // quede nadie por revisar, y el que acaba de contestar volvería a tener al centinela encima al minuto
        // siguiente — con un solo jugador trabajando, un interrogatorio por minuto para siempre.
        if (++_centinelaMinutosDeRondaCorridos >= CentinelaMinutosDeRonda)
        {
            _centinelaMinutosDeRondaCorridos = 0;
            // El que está siendo revisado AHORA no se toca: perdería el crédito de una respuesta en vuelo.
            var enVuelo = _centinelaRevisando?.Name;
            _centinelaRespondieron.RemoveWhere(n => !string.Equals(n, enVuelo, StringComparison.OrdinalIgnoreCase));
        }

        if (_centinelaRevisando is null) { CentinelaSiguienteTrabajador(); return; }

        // El que se desconectó en el medio no se encarcela: el original lo trata aparte
        // (`CentinelaUserLogout`) y sólo lo anota en el log.
        if (!_world.Players.Contains(_centinelaRevisando))
        {
            Console.WriteLine($"[centinela] {_centinelaRevisando.Name} se desconectó al pedírsele la clave");
            CentinelaSoltar();
            return;
        }

        _centinelaMinutosRestantes--;
        if (_centinelaMinutosRestantes <= 0) { CentinelaVeredicto(); CentinelaSiguienteTrabajador(); return; }

        var p = _centinelaRevisando;
        // Si se alejó, el centinela lo sigue (modCentinela.bas:290). El original mide con `Distancia`, que en
        // ese archivo es la euclídea del Matematicas.bas; acá alcanza con volver a plantarlo encima cuando ya
        // no está en la misma baldosa, que es lo que el jugador ve.
        var npc = _centinelaNpcCharIndex != 0 ? _world.NpcByIndex(_centinelaNpcCharIndex) : null;
        if (npc is null || npc.Pos.Map != p.Pos.Map
            || Math.Abs(npc.Pos.X - p.Pos.X) > 5 || Math.Abs(npc.Pos.Y - p.Pos.Y) > 5)
            CentinelaPlantar(p);

        CentinelaHabla(p, $"¡{p.Name}, tienes un minuto más para responder! Debes escribir /CENTINELA {_centinelaClave}.");
        p.Connection?.Send(ServerMessages.ConsoleMsg($"¡{p.Name}, tienes un minuto más para responder!", FuenteCentinela));
    }

    /// <summary>Cada vuelta del loop, mientras haya alguien bajo revisión: ruido y destello para que no se le
    /// pase (VB6 <c>CallUserAttention</c>, modCentinela.bas:58). Arranca a los 5 segundos de aparecer, para no
    /// atropellar al saludo.</summary>
    public void CentinelaLlamarAtencion(long nowMs)
    {
        if (!CentinelaEncendido || _centinelaRevisando is null || _centinelaNpcCharIndex == 0) return;
        if (nowMs - _centinelaSpawnMs < 5_000) return;
        _centinelaSpawnMs = nowMs;   // una vez cada 5 s, no en cada una de las 25 vueltas del segundo
        var p = _centinelaRevisando;
        p.Connection?.Send(ServerMessages.PlayWave(SndWarpCentinela, p.Pos.X, p.Pos.Y));
        p.Connection?.Send(ServerMessages.CreateFX(_centinelaNpcCharIndex, FxWarpCentinela, 0));
        CentinelaHabla(p, $"¡La clave que te he dicho es /CENTINELA {_centinelaClave}, escríbelo rápido!");
    }

    private const short SndWarpCentinela = 3;   // SND_WARP (Declares.bas:538)
    private const short FxWarpCentinela = 1;    // FXWARP (Declares.bas:222)

    /// <summary>La respuesta del jugador (VB6 <c>CentinelaCheckClave</c>, modCentinela.bas:201). Con el
    /// centinela apagado no contesta nada, que es lo que pasa en el original.</summary>
    public void CentinelaRecibirClave(Player p, int clave)
    {
        if (!CentinelaEncendido || _centinelaNpcCharIndex == 0) return;
        if (!ReferenceEquals(p, _centinelaRevisando))
        {
            CentinelaHabla(p, "No es a ti a quien estoy hablando, ¿No ves?");
            Console.WriteLine($"[centinela] {p.Name} respondió aunque no se le hablaba a él");
            return;
        }
        if (_centinelaRespondieron.Contains(p.Name))
        {
            CentinelaHabla(p, "Te agradezco, pero ya me has respondido. Me retiraré pronto.");
            return;
        }
        if (clave != _centinelaClave)
        {
            CentinelaHabla(p, $"¡La clave que te he dicho es /CENTINELA {_centinelaClave}, escríbelo rápido!");
            Console.WriteLine($"[centinela] {p.Name} respondió una clave incorrecta: {clave} - se esperaba {_centinelaClave}");
            return;
        }
        _centinelaRespondieron.Add(p.Name);
        CentinelaHabla(p, $"¡Muchas gracias {p.Name}! Espero no haber sido una molestia.");
        CentinelaSoltar();
    }

    /// <summary>Elige al próximo trabajador a revisar (VB6 <c>GoToNextWorkingChar</c>, modCentinela.bas:86):
    /// el primero conectado que sea jugador común, tenga créditos de faena y no haya contestado ya. Si no hay
    /// nadie trabajando, el NPC se retira.</summary>
    private void CentinelaSiguienteTrabajador()
    {
        var candidato = _world.Players.FirstOrDefault(
            pl => !pl.IsGm && pl.Trabajando > 0 && !_centinelaRespondieron.Contains(pl.Name));
        if (candidato is null) { CentinelaRetirar(); return; }

        _centinelaRevisando = candidato;
        _centinelaMinutosRestantes = CentinelaMinutos;
        _centinelaClave = _rng.Next(1, 32_001);   // VB6 RandomNumber(1, 32000), tope INCLUSIVE
        if (!CentinelaPlantar(candidato)) { CentinelaSoltar(); return; }
        CentinelaHabla(candidato,
            $"Saludos {candidato.Name}, soy el Centinela de estas tierras. Me gustaría que escribas " +
            $"/CENTINELA {_centinelaClave} en no más de dos minutos.");
        Console.WriteLine($"[centinela] revisando a {candidato.Name} (clave {_centinelaClave})");
    }

    /// <summary>Pone el NPC encima del jugador (VB6 <c>WarpCentinela</c>, modCentinela.bas:337). En agua va el
    /// 623 y en tierra el 622: el que está pescando lo tiene que ver flotando, no parado sobre el mar.</summary>
    private bool CentinelaPlantar(Player p)
    {
        CentinelaQuitarNpc();
        if (_npcs is null) return false;
        bool agua = _data.IsWater(p.Pos.Map, p.Pos.X, p.Pos.Y);
        var plantilla = _data.Npc(agua ? NpcCentinelaAgua : NpcCentinelaTierra);
        if (plantilla is null) return false;
        _centinelaNpcCharIndex = (short)_npcs.GmSpawn(plantilla, p.Pos);
        _centinelaSpawnMs = Environment.TickCount64;
        return _centinelaNpcCharIndex != 0;
    }

    /// <summary>El desenlace al agotarse el tiempo (VB6 <c>CentinelaFinalCheck</c>, modCentinela.bas:135):
    /// cárcel, pena anotada en el <c>.chr</c>, aviso a los admins y socket cerrado.</summary>
    private void CentinelaVeredicto()
    {
        var p = _centinelaRevisando;
        CentinelaSoltar();
        if (p is null || _centinelaRespondieron.Contains(p.Name)) return;

        Console.WriteLine($"[centinela] encarceló a {p.Name} por uso de macro inasistido");
        p.Penalties.Add($"Centinela : Macro inasistido. {DateTime.Now:dd/MM/yyyy HH:mm:ss}");
        p.JailMinutesLeft = CentinelaCondena;
        WarpOtroJugador(p, Cities.Prision);
        _db.SaveCharacter(p);   // la sanción se escribe en el acto, como el original
        BroadcastToAdmins(ServerMessages.ConsoleMsg($"Servidor> El centinela ha encarcelado a {p.Name}", FuenteServer));
        // El aviso a los admins sale ANTES de cerrar: un mensaje mandado después de cortar la sesión no llega
        // a ningún lado (es la misma lección del apagado del server).
        p.Connection?.Close("centinela: macro inasistido");
    }

    /// <summary>Deja de revisar a este jugador y se lleva el NPC, sin veredicto.</summary>
    private void CentinelaSoltar()
    {
        _centinelaRevisando = null;
        _centinelaClave = 0;
        _centinelaMinutosRestantes = 0;
        CentinelaQuitarNpc();
    }

    /// <summary>No hay nadie a quien revisar: el centinela se va del mundo. NO reinicia la ronda — de eso se
    /// encarga el reloj de <see cref="CentinelaMinutosDeRonda"/>.</summary>
    private void CentinelaRetirar() => CentinelaSoltar();

    private void CentinelaQuitarNpc()
    {
        if (_centinelaNpcCharIndex == 0) return;
        var npc = _world.NpcByIndex(_centinelaNpcCharIndex);
        _npcs?.CancelRespawn(_centinelaNpcCharIndex);
        _world.RemoveNpc(_centinelaNpcCharIndex);
        if (npc is not null)
            _world.BroadcastToObservers(npc.Pos, ServerMessages.CharacterRemove(_centinelaNpcCharIndex, faded: true));
        _centinelaNpcCharIndex = 0;
    }

    /// <summary>El centinela habla sobre su cabeza, que es como lo dice el original (<c>WriteChatOverHead</c>
    /// con el charIndex del NPC). Sólo lo oye el jugador al que le habla.</summary>
    private void CentinelaHabla(Player p, string texto)
    {
        if (_centinelaNpcCharIndex == 0) return;
        p.Connection?.Send(ServerMessages.ChatOverHead(texto, _centinelaNpcCharIndex));
    }
}
