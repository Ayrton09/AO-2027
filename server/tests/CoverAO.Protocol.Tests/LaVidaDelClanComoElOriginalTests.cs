using System.Collections.Concurrent;
using CoverAO.Protocol;
using CoverAO.Server.Data;
using CoverAO.Server.Game;
using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// SOLICITAR, NOTICIAS, ELECCIONES, DIPLOMACIA Y CIERRE DEL CLAN, COMO EL ORIGINAL (Protocol.bas y modGuilds.bas):
/// cada compuerta con su texto, las propuestas quedan en el otro clan sin avisar, y las firmas se avisan a los dos
/// clanes con los textos del original (sic incluidos).
/// </summary>
public class LaVidaDelClanComoElOriginalTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "coverao_vidaclan_" + Guid.NewGuid().ToString("N"));
    private readonly GameData _datos = GameData.Load();
    private readonly World _world = new();
    private readonly GameLoop _loop = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _corriendo;
    private readonly PacketDispatcher _dispatcher;
    private byte _x = 40;

    private sealed class Transporte : ITransport
    {
        public readonly ConcurrentQueue<byte[]> Recibidos = new();
        public string RemoteEndpoint => "test";
        public ValueTask<int> ReceiveAsync(byte[] buffer, CancellationToken ct) => new(0);
        public void Send(byte[] data) => Recibidos.Enqueue(data);
        public void Close(string reason) { }
        public bool Recibio(byte[] paquete) => Recibidos.Any(x => x.SequenceEqual(paquete));
        public List<string> Textos() => Recibidos.Where(x => x.Length > 0 && x[0] == (byte)ServerPacketId.ConsoleMsg)
            .Select(x => { var r = new ByteQueueReader(x); r.ReadByte(); return r.ReadAsciiString(); }).ToList();
    }

    private readonly AlmacenSqlite _almacen = AlmacenSqlite.EnMemoria();
    private readonly Persistence _db;

    public LaVidaDelClanComoElOriginalTests()
    {
        Directory.CreateDirectory(_root);
        var leveling = new Leveling(_datos.LevelExp);
        _db = new Persistence(_almacen, leveling);
        _dispatcher = new PacketDispatcher(_world, _loop, _db, _datos, leveling);
        _corriendo = _loop.RunAsync(_cts.Token);
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _corriendo.Wait(TimeSpan.FromSeconds(5)); } catch { /* cancelado */ }
        _db.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { /* el temp queda */ }
    }

    private (Player P, ClientSession S, Transporte T) Conectado(string nombre, string clan = "", byte status = Player.StatusCiudadano,
                                                                 byte nivel = 20, short mapa = 1)
    {
        var p = new Player
        {
            CharIndex = _world.AllocateCharIndex(), Name = nombre, Account = nombre.ToLowerInvariant(),
            Pos = new WorldPos(mapa, _x++, 50), Heading = 3, Hp = 100, MaxHp = 100, Level = nivel,
            GuildName = clan, FactionStatus = status,
        };
        var t = new Transporte();
        var sesion = new ClientSession(t, _dispatcher) { Player = p };
        p.Connection = sesion;
        _world.Add(p);
        return (p, sesion, t);
    }

    private Guild Clan(string nombre, string lider, byte alineacion = 2, params string[] miembros)
    {
        var g = _world.CreateGuild(nombre, lider, "desc")!;
        g.Alignment = alineacion;
        foreach (var m in miembros) g.Members.Add(m);
        return g;
    }

    private async Task Mandar(ClientSession s, ByteQueueWriter w)
    {
        _dispatcher.HandleOne(s, new ByteQueueReader(w.ToArray()));
        var atendido = new TaskCompletionSource();
        _loop.Enqueue(() => atendido.SetResult());
        await atendido.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    private static ByteQueueWriter Paquete(ClientPacketId id) => new ByteQueueWriter().WriteByte((byte)id);

    private static async Task Asentar(params Transporte[] ts)
    {
        int antes = -1;
        for (int i = 0; i < 50; i++)
        {
            int ahora = ts.Sum(t => t.Recibidos.Count);
            if (ahora == antes) break;
            antes = ahora;
            await Task.Delay(150);
        }
    }

    [Fact]
    public async Task Solicitar_con_las_compuertas_del_original()
    {
        var sur = Clan("Sur", "Aldo");
        var (_, _, tl) = Conectado("Aldo", "Sur");
        var (_, sn, tn) = Conectado("Novato", nivel: 10);
        var (_, sc, tc) = Conectado("Caotico", status: Player.StatusCaos);

        await Mandar(sn, Paquete(ClientPacketId.GuildRequestMembership).WriteAsciiString("Sur").WriteAsciiString("hola"));
        await Mandar(sc, Paquete(ClientPacketId.GuildRequestMembership).WriteAsciiString("Sur").WriteAsciiString("hola"));
        await Mandar(sc, Paquete(ClientPacketId.GuildRequestMembership).WriteAsciiString("Nadie").WriteAsciiString("hola"));
        await Asentar(tl, tn, tc);

        Assert.Contains("Los newbies no tienen derecho a entrar a un clan.", tn.Textos());
        Assert.Contains("Tú no puedes entrar a un clan de alineación Imperial", tc.Textos());
        Assert.Contains("Ese clan no existe, avise a un administrador.", tc.Textos());
        Assert.Empty(sur.JoinRequests);
        Assert.Empty(tl.Textos());
    }

    [Fact]
    public async Task Solicitar_a_otro_clan_retira_la_solicitud_anterior_y_el_lider_no_recibe_aviso()
    {
        var sur = Clan("Sur", "Aldo");
        var norte = Clan("Norte", "Bruma");
        var (_, _, tl) = Conectado("Aldo", "Sur");
        var (_, s, t) = Conectado("Ciro");

        await Mandar(s, Paquete(ClientPacketId.GuildRequestMembership).WriteAsciiString("Sur").WriteAsciiString("a"));
        await Mandar(s, Paquete(ClientPacketId.GuildRequestMembership).WriteAsciiString("Norte").WriteAsciiString("b"));
        await Asentar(t, tl);

        Assert.Empty(sur.JoinRequests);
        Assert.Equal("b", norte.JoinRequests["Ciro"]);
        Assert.Contains("Tu solicitud ha sido enviada. Espera prontas noticias del líder de Norte.", t.Textos());
        Assert.Empty(tl.Textos());
    }

    [Fact]
    public async Task Las_noticias_se_avisan_por_el_chat_del_clan()
    {
        Clan("Sur", "Aldo", 2, "Bruma");
        var (_, sa, ta) = Conectado("Aldo", "Sur");
        var (_, sb, tb) = Conectado("Bruma", "Sur");

        await Mandar(sb, Paquete(ClientPacketId.GuildUpdateNews).WriteAsciiString("no soy lider"));
        await Mandar(sa, Paquete(ClientPacketId.GuildUpdateNews).WriteAsciiString("reunion"));
        await Asentar(ta, tb);

        Assert.Equal("reunion", _world.GuildByName("Sur")!.News);
        Assert.True(tb.Recibio(ServerMessages.GuildChat("Aldo ha actualizado las noticias del clan!")));
        Assert.Empty(tb.Textos());
        Assert.Empty(ta.Textos());
    }

    [Fact]
    public async Task Abrir_elecciones_con_los_textos_del_original()
    {
        Clan("Sur", "Aldo", 2, "Bruma");
        var (_, sa, ta) = Conectado("Aldo", "Sur");
        var (_, sb, tb) = Conectado("Bruma", "Sur");
        var (_, sx, tx) = Conectado("Suelto");

        await Mandar(sx, Paquete(ClientPacketId.GuildOpenElections));
        await Mandar(sb, Paquete(ClientPacketId.GuildOpenElections));
        await Mandar(sa, Paquete(ClientPacketId.GuildOpenElections));
        await Mandar(sa, Paquete(ClientPacketId.GuildOpenElections));
        await Asentar(ta, tb, tx);

        Assert.Contains("Tú no perteneces a ningún clan.", tx.Textos());
        Assert.Contains("No eres el líder de tu clan", tb.Textos());
        Assert.Contains("¡Han comenzado las elecciones del clan! Puedes votar escribiendo /VOTO seguido del nombre del personaje, por ejemplo: /VOTO Aldo", tb.Textos());
        Assert.Contains("Las elecciones ya están abiertas.", ta.Textos());
    }

    [Fact]
    public async Task Declarar_la_guerra_avisa_a_los_dos_clanes_con_el_sonido_45()
    {
        var sur = Clan("Sur", "Aldo");
        var norte = Clan("Norte", "Bruma");
        var (_, sa, ta) = Conectado("Aldo", "Sur");
        var (_, _, tb) = Conectado("Bruma", "Norte");

        await Mandar(sa, Paquete(ClientPacketId.GuildDeclareWar).WriteAsciiString("Norte"));
        await Mandar(sa, Paquete(ClientPacketId.GuildDeclareWar).WriteAsciiString("Norte"));
        await Asentar(ta, tb);

        Assert.Contains("Norte", sur.Enemies);
        Assert.Contains("Sur", norte.Enemies);
        Assert.Contains("TU CLAN HA ENTRADO EN GUERRA CON Norte.", ta.Textos());
        Assert.Contains("Sur LE DECLARA LA GUERRA A TU CLAN.", tb.Textos());
        Assert.True(ta.Recibio(ServerMessages.PlayWave(45, 0, 0)) && tb.Recibio(ServerMessages.PlayWave(45, 0, 0)));
        Assert.Contains("Tu clan ya está en guerra con Norte.", ta.Textos());
    }

    [Fact]
    public async Task La_paz_se_propone_callada_y_se_firma_avisando_a_los_dos()
    {
        var sur = Clan("Sur", "Aldo");
        var norte = Clan("Norte", "Bruma");
        sur.Enemies.Add("Norte"); norte.Enemies.Add("Sur");
        var (_, sa, ta) = Conectado("Aldo", "Sur");
        var (_, sb, tb) = Conectado("Bruma", "Norte");

        await Mandar(sa, Paquete(ClientPacketId.GuildOfferPeace).WriteAsciiString("Norte").WriteAsciiString("basta"));
        await Asentar(ta, tb);
        Assert.Equal("basta", norte.PeaceOffers["Sur"]);
        Assert.Empty(ta.Textos());
        Assert.Empty(tb.Textos());

        await Mandar(sb, Paquete(ClientPacketId.GuildAcceptPeace).WriteAsciiString("Sur"));
        await Asentar(ta, tb);
        Assert.Empty(sur.Enemies);
        Assert.Contains("Tu clan ha firmado la paz con Sur.", tb.Textos());
        Assert.Contains("Tu clan ha firmado la paz con Norte.", ta.Textos());
    }

    [Fact]
    public async Task La_paz_sin_guerra_no_se_propone()
    {
        Clan("Sur", "Aldo");
        Clan("Norte", "Bruma");
        var (_, sa, ta) = Conectado("Aldo", "Sur");

        await Mandar(sa, Paquete(ClientPacketId.GuildOfferPeace).WriteAsciiString("Norte").WriteAsciiString("x"));
        await Mandar(sa, Paquete(ClientPacketId.GuildOfferPeace).WriteAsciiString("Sur").WriteAsciiString("x"));
        await Asentar(ta);

        Assert.Contains("No estás en guerra con Norte", ta.Textos());
        Assert.Contains("No puedes declarar relaciones con tu propio clan.", ta.Textos());
    }

    [Fact]
    public async Task Rechazar_la_paz_avisa_y_deja_la_noticia_en_el_otro_clan()
    {
        var sur = Clan("Sur", "Aldo");
        var norte = Clan("Norte", "Bruma");
        sur.Enemies.Add("Norte"); norte.Enemies.Add("Sur");
        norte.PeaceOffers["Sur"] = "basta";
        sur.News = "vieja";
        var (_, _, ta) = Conectado("Aldo", "Sur");
        var (_, sb, tb) = Conectado("Bruma", "Norte");

        await Mandar(sb, Paquete(ClientPacketId.GuildRejectPeace).WriteAsciiString("Sur"));
        await Asentar(ta, tb);

        Assert.Empty(norte.PeaceOffers);
        Assert.Contains("Tu clan rechazado la propuesta de paz de Sur.", tb.Textos());
        Assert.Contains("Norte ha rechazado nuestra propuesta de paz con su clan.", ta.Textos());
        Assert.Equal("Norte ha rechazado nuestra propuesta de paz. vieja", sur.News);
    }

    [Fact]
    public async Task La_alianza_firmada_le_dice_paz_al_otro_clan_como_el_original()
    {
        var sur = Clan("Sur", "Aldo");
        var norte = Clan("Norte", "Bruma");
        var (_, sa, ta) = Conectado("Aldo", "Sur");
        var (_, sb, tb) = Conectado("Bruma", "Norte");

        await Mandar(sa, Paquete(ClientPacketId.GuildOfferAlliance).WriteAsciiString("Norte").WriteAsciiString("juntos"));
        await Mandar(sb, Paquete(ClientPacketId.GuildAcceptAlliance).WriteAsciiString("Sur"));
        await Asentar(ta, tb);

        Assert.Contains("Norte", sur.Allies);
        Assert.Contains("Tu clan ha firmado la alianza con Sur.", tb.Textos());
        Assert.Contains("Tu clan ha firmado la paz con Norte.", ta.Textos());
    }

    [Fact]
    public async Task Cerrar_el_clan_pide_que_no_quede_nadie_mas()
    {
        var sur = Clan("Sur", "Aldo", 2, "Bruma");
        var (a, sa, ta) = Conectado("Aldo", "Sur", mapa: 1);
        var (_, _, tb) = Conectado("Bruma", "Sur");

        await Mandar(sa, Paquete(ClientPacketId.CloseGuild));
        await Asentar(ta);
        Assert.Contains("¡Debes hechar a todos los miembros del clan para cerrarlo!", ta.Textos());
        Assert.NotNull(_world.GuildByName("Sur"));

        sur.Members.Remove("Bruma");
        await Mandar(sa, Paquete(ClientPacketId.CloseGuild));
        await Asentar(ta, tb);
        Assert.Null(_world.GuildByName("Sur"));
        Assert.Equal("", a.GuildName);
        Assert.Contains("Servidor> Aldo ha cerrado el clan llamado: Sur", tb.Textos());
        Assert.Contains("¡Clan eliminado!", ta.Textos());
    }

    /// <summary>LA PAZ PROPUESTA, LAS ELECCIONES Y LOS VOTOS SOBREVIVEN A UN REINICIO, como en el original
    /// (clsClan.cls:785-787 las propuestas; :591-611 las elecciones y los votos). Vivían sólo en memoria.
    ///
    /// <para>El voto se vuelca SOLO, después de un volcado que ya limpió la marca de sucio: así lo que se mide es que
    /// el voto mismo manda el clan al disco, que era la otra mitad del hueco.</para></summary>
    [Fact]
    public async Task La_paz_las_elecciones_y_los_votos_sobreviven_a_un_reinicio()
    {
        var sur = Clan("Sur", "Aldo", 2, "Bruma", "Ciro");
        var norte = Clan("Norte", "Dana");
        sur.Enemies.Add("Norte"); norte.Enemies.Add("Sur");
        var (_, sa, _) = Conectado("Aldo", "Sur");
        var (_, sb, _) = Conectado("Bruma", "Sur");
        var (_, sd, _) = Conectado("Dana", "Norte");

        async Task Volcar()
        {
            var listo = new TaskCompletionSource();
            _loop.Enqueue(() => { _dispatcher.VolcarPersistenciaDiferida(forzar: true); listo.SetResult(); });
            await listo.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }

        await Mandar(sd, Paquete(ClientPacketId.GuildOfferPeace).WriteAsciiString("Sur").WriteAsciiString("basta"));
        await Mandar(sa, Paquete(ClientPacketId.GuildOpenElections));
        await Volcar();
        await Mandar(sb, Paquete(ClientPacketId.GuildVote).WriteAsciiString("Ciro"));
        await Volcar();
        _db.EsperarEscriturasPendientes();   // el volcado se encola en la base: el "reinicio" lee después

        // El "reinicio": otro Persistence sobre la MISMA base, que lee lo que quedó guardado.
        var releidos = new Persistence(_almacen, new Leveling(_datos.LevelExp)).LoadGuilds();
        var sur2 = releidos.Single(g => g.Name == "Sur");
        Assert.Equal("basta", sur2.PeaceOffers["Norte"]);
        Assert.True(sur2.ElectionsOpen);
        Assert.Equal("Ciro", sur2.Votes["Bruma"]);
        // CONTROL: un voto de tres miembros no alcanza la mayoría, así que la elección sigue abierta y el líder es el mismo.
        Assert.Equal("Aldo", sur2.LeaderName);
    }

    private static async Task<bool> Esperar(Func<bool> condicion, int topeMs = 15_000)
    {
        long fin = Environment.TickCount64 + topeMs;
        while (Environment.TickCount64 < fin)
        {
            if (condicion()) return true;
            await Task.Delay(25);
        }
        return false;
    }

    /// <summary>ACEPTAR A UN ASPIRANTE DESCONECTADO LE ANOTA EL CLAN, como el original: escribe el <c>GUILDINDEX</c> de su
    /// <c>.chr</c> esté o no conectado (clsClan.cls:299). Acá quedaba en la lista del clan y, al entrar, sin clan.</summary>
    [Fact]
    public async Task Aceptar_a_un_aspirante_desconectado_le_anota_el_clan()
    {
        var sur = Clan("Sur", "Aldo");
        var cuenta = _db.CreateAccount("cuenta_bruma", "clave_larga");
        _db.CreateCharacter(cuenta, "Bruma", 0, race: 1, gender: 1, @class: 1, homeland: 1, head: 1);
        sur.JoinRequests["Bruma"] = "quiero entrar";
        var (_, sa, _) = Conectado("Aldo", "Sur");

        await Mandar(sa, Paquete(ClientPacketId.GuildAcceptNewMember).WriteAsciiString("Bruma"));

        Assert.True(await Esperar(() => _db.LoadCharacter("Bruma", 0, "")!.GuildName == "Sur"), "el personaje no quedó en el clan");
        Assert.Contains("Bruma", sur.Members);
    }

    /// <summary>/BANCLAN LE ANOTA LA PENA A CADA MIEMBRO, conectado o no, como el original: "gm: BAN AL CLAN: clan fecha",
    /// con el clan como lo escribió el GM (Protocol.bas:9624). Acá sólo quedaba el baneo, sin el antecedente.</summary>
    [Fact]
    public async Task Banear_al_clan_le_anota_la_pena_a_cada_miembro()
    {
        Clan("Sur", "Aldo", 2, "Ciro");
        var cuenta = _db.CreateAccount("cuenta_ciro", "clave_larga");
        var ciro = _db.CreateCharacter(cuenta, "Ciro", 0, race: 1, gender: 1, @class: 1, homeland: 1, head: 1);
        ciro.GuildName = "Sur";
        _db.SaveCharacter(ciro);
        var (dios, sd, _) = Conectado("ElDios");
        dios.Privileges = 8;   // Dios

        await Mandar(sd, Paquete(ClientPacketId.GMCommands).WriteByte(50).WriteAsciiString("sur"));

        Assert.True(await Esperar(() => _db.LoadCharacter("Ciro", 0, "") is { Banned: true } c
                                        && c.Penalties.Any(x => x.StartsWith("eldios: BAN AL CLAN: sur ", StringComparison.Ordinal))),
            "al miembro desconectado no se le anotó la pena");
    }

    /// <summary>ECHAR A UN MIEMBRO DESCONECTADO LE SACA EL CLAN (clsClan.cls:343). Acá salía de la lista del clan y, al entrar,
    /// seguía con su tag.</summary>
    [Fact]
    public async Task Echar_a_un_miembro_desconectado_le_saca_el_clan()
    {
        var sur = Clan("Sur", "Aldo", 2, "Ciro");
        var cuenta = _db.CreateAccount("cuenta_ciro", "clave_larga");
        var ciro = _db.CreateCharacter(cuenta, "Ciro", 0, race: 1, gender: 1, @class: 1, homeland: 1, head: 1);
        ciro.GuildName = "Sur";
        _db.SaveCharacter(ciro);
        var (_, sa, _) = Conectado("Aldo", "Sur");

        await Mandar(sa, Paquete(ClientPacketId.GuildKickMember).WriteAsciiString("Ciro"));

        Assert.True(await Esperar(() => _db.LoadCharacter("Ciro", 0, "")!.GuildName == ""), "el personaje sigue con el clan");
        Assert.DoesNotContain("Ciro", sur.Members);
    }
}
