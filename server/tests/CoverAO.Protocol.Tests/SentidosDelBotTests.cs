using CoverAO.Server.Data;
using CoverAO.Server.Game;
using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// LO QUE EL BOT ENTIENDE DE LO QUE LE MANDAN.
///
/// <para>Los paquetes de acá los arma <see cref="ServerMessages"/>, o sea el MISMO emisor que le habla a los
/// jugadores. Es a propósito: armarlos a mano con el layout que yo creo que tienen probaría que mi
/// decodificador coincide con mi idea, no con el servidor.</para>
/// </summary>
public class SentidosDelBotTests
{
    private const short MiIndice = 500, OtroIndice = 501;

    private static Player Personaje(short charIndex, string nombre, byte x, byte y) =>
        new() { CharIndex = charIndex, Name = nombre, Account = "cuenta_test", Pos = new WorldPos(1, x, y) };

    private static Npc Criatura(short charIndex, short ficha, byte x, byte y) => new()
    {
        CharIndex = charIndex,
        Template = new NpcInfo(ficha, "Lobo", 0, 0, 0, MaxHp: 20, MinHit: 1, MaxHit: 2, Def: 0,
                               GiveExp: 5, GiveGld: 0, PoderEvasion: 0, PoderAtaque: 0),
        Pos = new WorldPos(1, x, y),
    };

    /// <summary>
    /// EL ORDEN DEL LOGIN, que es donde estaba el bug. El servidor manda el <c>CharacterCreate</c> del propio
    /// personaje ANTES de decirle cuál de todos los índices es él (<c>UserCharIndexInServer</c> va después, y
    /// el 23 antes). Mirando sólo el 24, cuando llegaba el CharacterCreate el bot todavía no sabía que hablaba
    /// de él, no se guardaba la posición, y se quedaba para siempre esperando ubicarse: quieto, conectado y
    /// sin hacer nada. Se vio corriéndolo —seis bots, cinco minutos, una baldosa— y no lo habría visto ningún
    /// test que mandara los paquetes en el orden cómodo.
    /// </summary>
    [Fact]
    public void Se_ubica_aunque_el_indice_propio_llegue_despues_del_personaje()
    {
        var s = new SentidosDelBot();

        s.Recibir(ServerMessages.CharacterCreate(Personaje(MiIndice, "Aldo", 50, 60)));
        Assert.False(s.Ubicado);   // todavía no sabe cuál es él

        s.Recibir(ServerMessages.UserCharIndexInServer(MiIndice));

        Assert.True(s.Ubicado);
        Assert.Equal((byte)50, s.Posicion.X);
        Assert.Equal((byte)60, s.Posicion.Y);
    }

    /// <summary>Y en el otro orden también, que es como llega el paquete 23.</summary>
    [Fact]
    public void Se_ubica_si_el_indice_propio_llega_primero()
    {
        var s = new SentidosDelBot();
        s.Recibir(ServerMessages.UserIndexInServer(MiIndice));
        s.Recibir(ServerMessages.CharacterCreate(Personaje(MiIndice, "Aldo", 12, 34)));

        Assert.True(s.Ubicado);
        Assert.Equal(((byte)12, (byte)34), s.Posicion);
    }

    /// <summary>Criatura o persona: la única diferencia en el cable es que el "nombre" de una criatura es su
    /// número de ficha. De eso depende a quién le pega el bot — confundirse sería atacar jugadores.</summary>
    [Fact]
    public void Distingue_una_criatura_de_una_persona()
    {
        var s = Ubicado(50, 50);
        s.Recibir(ServerMessages.CharacterCreate(Criatura(600, ficha: 45, 51, 50)));
        s.Recibir(ServerMessages.CharacterCreate(Personaje(OtroIndice, "Rugura", 52, 50)));

        Assert.True(s.Donde(600)!.Value.EsNpc);
        Assert.Equal((short)45, s.Donde(600)!.Value.FichaNpc);
        Assert.False(s.Donde(OtroIndice)!.Value.EsNpc);
        // Y la búsqueda de presa NO puede devolver a la persona, por más cerca que esté.
        Assert.Equal((short)600, s.MasCercano(_ => true)!.Value.Quien.CharIndex);
    }

    [Fact]
    public void Elige_la_criatura_mas_cercana_de_las_que_sirven()
    {
        var s = Ubicado(50, 50);
        s.Recibir(ServerMessages.CharacterCreate(Criatura(601, ficha: 45, 50, 47)));   // a 3
        s.Recibir(ServerMessages.CharacterCreate(Criatura(602, ficha: 46, 51, 50)));   // a 1, pero no sirve
        s.Recibir(ServerMessages.CharacterCreate(Criatura(603, ficha: 45, 48, 50)));   // a 2

        var elegida = s.MasCercano(ficha => ficha == 45);
        Assert.Equal((short)603, elegida!.Value.Quien.CharIndex);
        Assert.Equal(2, elegida.Value.Pasos);
    }

    [Fact]
    public void La_que_desaparece_deja_de_estar_a_la_vista()
    {
        var s = Ubicado(50, 50);
        s.Recibir(ServerMessages.CharacterCreate(Criatura(604, ficha: 45, 51, 50)));
        Assert.NotNull(s.Donde(604));

        s.Recibir(ServerMessages.CharacterRemove(604, faded: false));
        Assert.Null(s.Donde(604));
        Assert.Null(s.MasCercano(_ => true));
    }

    [Fact]
    public void Sigue_a_la_criatura_que_se_mueve()
    {
        var s = Ubicado(50, 50);
        s.Recibir(ServerMessages.CharacterCreate(Criatura(605, ficha: 45, 55, 50)));
        s.Recibir(ServerMessages.CharacterMove(605, 51, 50));

        Assert.Equal(1, s.MasCercano(_ => true)!.Value.Pasos);
    }

    /// <summary>La poción roja del kit de novato: <c>ObjType 11</c>, <c>SubTipo 3</c>, ranura del inventario.
    /// Si esto fallara el bot no se curaría nunca, y morirse le cuesta el personaje — el cadáver se queda
    /// donde cayó.</summary>
    [Fact]
    public void Encuentra_la_pocion_en_el_inventario()
    {
        var s = new SentidosDelBot();
        s.Recibir(ServerMessages.ChangeInventorySlot(1, 574, 1, true, 0f, 1));      // martillo
        s.Recibir(ServerMessages.ChangeInventorySlot(5, 461, 1500, false, 0f, 1));  // Poción Roja (Newbies)

        Assert.Equal((byte)5, s.Ranura(obj => obj == 461));
        Assert.Equal((byte)0, s.Ranura(obj => obj == 999));
    }

    /// <summary>Una ranura vaciada deja de servir: el servidor la reescribe con el objeto en 0 al gastar el
    /// último. Sin esto el bot seguiría mandando "tomá la poción" de una ranura vacía.</summary>
    [Fact]
    public void Una_ranura_vaciada_ya_no_cuenta()
    {
        var s = new SentidosDelBot();
        s.Recibir(ServerMessages.ChangeInventorySlot(5, 461, 1, false, 0f, 1));
        Assert.Equal((byte)5, s.Ranura(obj => obj == 461));

        s.Recibir(ServerMessages.ChangeInventorySlot(5, 0, 0, false, 0f, 0));
        Assert.Equal((byte)0, s.Ranura(obj => obj == 461));
    }

    [Fact]
    public void Lee_la_vida_la_energia_y_el_nivel()
    {
        var s = new SentidosDelBot();
        var p = Personaje(MiIndice, "Aldo", 50, 50);
        p.MaxHp = 100; p.Hp = 40;
        p.MaxStamina = 50; p.Stamina = 5;
        p.Level = 7;
        p.Hunger = 0; p.Thirst = 3;

        s.Recibir(ServerMessages.UpdateUserStats(p));

        Assert.Equal(100, s.VidaMaxima);
        Assert.Equal(0.40, s.Salud, 3);
        Assert.Equal(0.10, s.Fuelle, 3);
        Assert.Equal((short)7, s.Nivel);
        Assert.True(s.ConHambre);
        Assert.False(s.ConSed);
        Assert.True(s.Vivo);
    }

    [Fact]
    public void Con_la_vida_en_cero_esta_muerto()
    {
        var s = new SentidosDelBot();
        var p = Personaje(MiIndice, "Aldo", 50, 50);
        p.MaxHp = 20; p.Hp = 20;
        s.Recibir(ServerMessages.UpdateUserStats(p));
        Assert.True(s.Vivo);

        s.Recibir(ServerMessages.UpdateHP(0));
        Assert.False(s.Vivo);
    }

    /// <summary>El servidor NO le avisa al que camina que se movió: sólo manda un <c>PosUpdate</c> cuando el
    /// paso se rechaza. El bot adelanta la posición y la corrección lo devuelve a donde está de verdad — que
    /// es además cómo se entera de que tiene una pared adelante.</summary>
    [Fact]
    public void La_posicion_se_predice_y_el_servidor_la_corrige()
    {
        var s = Ubicado(50, 50);

        s.PredecirPaso(2);   // este
        Assert.Equal(((byte)51, (byte)50), s.Posicion);

        s.Recibir(ServerMessages.PosUpdate(50, 50));   // había una pared
        Assert.Equal(((byte)50, (byte)50), s.Posicion);
    }

    [Fact]
    public void Los_cuatro_rumbos_van_a_donde_dicen()
    {
        Assert.Equal((0, -1), SentidosDelBot.Paso(1));
        Assert.Equal((1, 0), SentidosDelBot.Paso(2));
        Assert.Equal((0, 1), SentidosDelBot.Paso(3));
        Assert.Equal((-1, 0), SentidosDelBot.Paso(4));

        Assert.Equal((byte)2, SentidosDelBot.RumboHacia(1, 0));    // a la derecha -> este
        Assert.Equal((byte)4, SentidosDelBot.RumboHacia(-1, 0));   // a la izquierda -> oeste
        Assert.Equal((byte)3, SentidosDelBot.RumboHacia(0, 1));    // abajo -> sur
        Assert.Equal((byte)1, SentidosDelBot.RumboHacia(0, -1));   // arriba -> norte
    }

    /// <summary>Un paquete recortado no puede tumbar al bot.</summary>
    [Fact]
    public void Un_paquete_a_medias_se_descarta_sin_romper_nada()
    {
        var s = Ubicado(50, 50);
        s.Recibir(new byte[] { (byte)ServerPacketId.CharacterCreate, 1 });   // se corta en el segundo byte
        Assert.Equal(((byte)50, (byte)50), s.Posicion);
    }

    // ── Lo que hace falta para la IA completa ─────────────────────────────────────────────────────────

    /// <summary>
    /// LOS PUNTOS DE NIVEL SE SUMAN, Y LA CUENTA TIENE QUE SER EXACTA.
    ///
    /// <para>El mismo paquete significa dos cosas: al entrar trae el TOTAL que hay sin repartir, y al subir de
    /// nivel trae sólo los que se ganaron en ese nivel. Sumarlos es lo correcto para los dos casos. Y la
    /// cuenta importa de verdad: pedir más puntos de los que hay no es un pedido a recortar — el servidor lo
    /// registra como intento de hackeo y CIERRA LA CONEXIÓN (<c>HandleModifySkills</c>).</para>
    /// </summary>
    [Fact]
    public void Los_puntos_de_nivel_se_acumulan_y_se_descuentan()
    {
        var s = new SentidosDelBot();
        Assert.Equal(0, s.PuntosLibres);

        s.Recibir(ServerMessages.LevelUp(5));      // al entrar: los que ya tenía
        s.Recibir(ServerMessages.LevelUp(3));      // subió de nivel: tres más
        Assert.Equal(8, s.PuntosLibres);

        s.GastarPuntos(8);
        Assert.Equal(0, s.PuntosLibres);
        s.GastarPuntos(5);                          // de más: no queda negativo
        Assert.Equal(0, s.PuntosLibres);
    }

    [Fact]
    public void Lo_tirado_en_el_piso_se_ve_y_se_deja_de_ver()
    {
        var s = Ubicado(50, 50);
        Assert.Null(s.BotinMasCercano(_ => true));

        s.Recibir(ServerMessages.ObjectCreate(52, 50, 461, 3));
        var botin = s.BotinMasCercano(_ => true);
        Assert.NotNull(botin);
        Assert.Equal(((byte)52, (byte)50), botin!.Value.Donde);
        Assert.Equal(2, botin.Value.Pasos);

        s.Recibir(ServerMessages.ObjectDelete(52, 50));
        Assert.Null(s.BotinMasCercano(_ => true));
    }

    /// <summary>El servidor manda como objeto del piso lo tirado Y LAS PUERTAS, así que el bot filtra: sin
    /// eso caminaba hasta cada puerta del pueblo a agacharse, y no llegaba nunca a un botín de verdad.</summary>
    [Fact]
    public void El_botin_se_puede_filtrar_por_lo_que_es()
    {
        var s = Ubicado(50, 50);
        s.Recibir(ServerMessages.ObjectCreate(51, 50, 190, 1));    // una puerta, pegada
        s.Recibir(ServerMessages.ObjectCreate(54, 50, 461, 5));    // botín de verdad, más lejos

        Assert.Equal(((byte)51, (byte)50), s.BotinMasCercano(_ => true)!.Value.Donde);
        Assert.Equal(((byte)54, (byte)50), s.BotinMasCercano(obj => obj != 190)!.Value.Donde);
        Assert.Equal((short)461, s.EnElPiso((54, 50)));
        Assert.Equal((short)0, s.EnElPiso((99, 99)));
    }

    /// <summary>El mostrador se lee mientras la ventana está abierta, y se olvida al cerrarla: comprar de una
    /// ranura de una tienda que ya no está abierta le compraría cualquier cosa a cualquiera.</summary>
    [Fact]
    public void El_mostrador_se_lee_y_se_olvida_al_cerrar()
    {
        var s = new SentidosDelBot();
        s.Recibir(ServerMessages.CommerceInit());
        s.Recibir(ServerMessages.ChangeNpcInventorySlot(2, 100, 15.5f, 38));
        Assert.True(s.Comerciando);

        var oferta = s.EnLaTienda(obj => obj == 38);
        Assert.NotNull(oferta);
        Assert.Equal((byte)2, oferta!.Value.Ranura);
        Assert.Equal(15.5f, oferta.Value.Precio, 2);

        s.Recibir(ServerMessages.CommerceEnd());
        Assert.False(s.Comerciando);
        Assert.Null(s.EnLaTienda(obj => obj == 38));
    }

    /// <summary>Lo que lleva PUESTO no se ofrece para vender. Sin esto el bot le vende su propia arma al
    /// mercader y sigue cazando a mano limpia.</summary>
    [Fact]
    public void Lo_que_lleva_puesto_no_se_ofrece_para_vender()
    {
        var s = new SentidosDelBot();
        s.Recibir(ServerMessages.ChangeInventorySlot(3, 574, 1, true, 0f, 1));    // el arma, puesta
        s.Recibir(ServerMessages.ChangeInventorySlot(7, 1354, 1, false, 0f, 1));  // botín, suelto

        Assert.Null(s.EnLaMochila(obj => obj == 574));
        Assert.Equal((byte)7, s.EnLaMochila(obj => obj == 1354)!.Value.Ranura);
    }

    [Fact]
    public void Cuenta_lo_que_tiene_sumando_ranuras()
    {
        var s = new SentidosDelBot();
        s.Recibir(ServerMessages.ChangeInventorySlot(1, 461, 900, false, 0f, 1));
        s.Recibir(ServerMessages.ChangeInventorySlot(2, 461, 600, false, 0f, 1));
        s.Recibir(ServerMessages.ChangeInventorySlot(3, 38, 10, false, 0f, 1));

        Assert.Equal(1500, s.CuantoTiene(obj => obj == 461));
        Assert.Equal(1510, s.CuantoTiene(obj => obj is 461 or 38));
        Assert.Equal(0, s.CuantoTiene(obj => obj == 999));
    }

    [Fact]
    public void Los_hechizos_se_leen_de_sus_ranuras()
    {
        var s = new SentidosDelBot();
        s.Recibir(ServerMessages.ChangeSpellSlot(1, 8));
        s.Recibir(ServerMessages.ChangeSpellSlot(3, 77));

        var h = s.Hechizos();
        Assert.Equal(2, h.Count);
        Assert.Contains(((byte)1, (short)8), h);
        Assert.Contains(((byte)3, (short)77), h);
    }

    /// <summary>Al cambiar de mapa se olvida TODO lo que veía: los personajes y las cosas tiradas eran del
    /// mapa anterior, y sus coordenadas acá señalan otro lugar.</summary>
    [Fact]
    public void Al_cambiar_de_mapa_se_olvida_lo_que_veia()
    {
        var s = Ubicado(50, 50);
        s.Recibir(ServerMessages.CharacterCreate(Criatura(700, ficha: 45, 51, 50)));
        s.Recibir(ServerMessages.ObjectCreate(51, 50, 461, 1));
        Assert.NotNull(s.MasCercano(_ => true));

        s.Recibir(ServerMessages.ChangeMap(34));

        Assert.Null(s.MasCercano(_ => true));
        Assert.Null(s.BotinMasCercano(_ => true));
        Assert.True(s.CambioDeMapa());
        Assert.False(s.CambioDeMapa());   // se consume al leerlo
    }

    /// <summary>Con algo pegado no se descansa: es lo que corta una pausa larga cuando algo se le vino
    /// encima.</summary>
    [Fact]
    public void Sabe_si_tiene_algo_pegado()
    {
        var s = Ubicado(50, 50);
        Assert.False(s.AlgoPegado());

        s.Recibir(ServerMessages.CharacterCreate(Criatura(701, ficha: 45, 51, 50)));
        Assert.True(s.AlgoPegado());

        s.Recibir(ServerMessages.CharacterMove(701, 55, 50));
        Assert.False(s.AlgoPegado());
    }

    private static SentidosDelBot Ubicado(byte x, byte y)
    {
        var s = new SentidosDelBot();
        s.Recibir(ServerMessages.UserIndexInServer(MiIndice));
        s.Recibir(ServerMessages.CharacterCreate(Personaje(MiIndice, "Aldo", x, y)));
        return s;
    }
}
