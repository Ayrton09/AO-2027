using System.Reflection;
using CoverAO.Protocol;
using CoverAO.Server.Data;
using CoverAO.Server.Game;
using CoverAO.Server.Net;

namespace CoverAO.Protocol.Tests;

/// <summary>El aprendizaje se confirma por el libro que llega por protocolo; un intento perdido no es un rechazo definitivo.</summary>
public sealed class AprendizajeDePergaminosDelBotTests
{
    private const short Pergamino = 201, Hechizo = 3;
    private const byte Mochila = 8, Vitrina = 1;
    private static readonly Lazy<(GameData Datos, Navegante Mapas)> Mundo = new(() =>
    {
        var d = GameData.Load();
        return (d, new Navegante(d));
    });

    private sealed class Escena
    {
        public Bot Bot { get; }
        public SentidosDelBot Sentidos { get; private set; } = null!;
        public TransporteDeBot Transporte { get; private set; } = null!;
        public long Ahora = 10_000;

        public Escena(bool conPergamino = true)
        {
            var d = Mundo.Value.Datos;
            Assert.Equal(Hechizo, d.Obj(Pergamino)!.HechizoIndex);
            Assert.True(d.Spell(Hechizo)!.IsHeal);
            Bot = new Bot("Bruma", null, new EntornoDeBots(null!, new ServerIni(), 0, 0, d, Mundo.Value.Mapas, null!), 1);
            typeof(Bot).GetProperty("RelojDelAprendizaje", BindingFlags.Instance | BindingFlags.NonPublic)
                ?.SetValue(Bot, (Func<long>)(() => Ahora));
            NuevaVida(conPergamino);
        }

        public void NuevaVida(bool conPergamino = true)
        {
            Sentidos = new SentidosDelBot { MiNombre = "Bruma" };
            Transporte = new TransporteDeBot("Bruma");
            // El mismo relevo que hace UnaVidaAsync: cambia el transporte y reconstruye los sentidos desde el login.
            typeof(Bot).GetField("_transporte", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(Bot, Transporte);
            var yo = new Player
            {
                CharIndex = 500, Name = "Bruma", Account = "bruma_aprendizaje", Class = 2,
                Pos = new WorldPos(194, 52, 79), Level = 30, Hp = 200, MaxHp = 200,
                Mana = 1_000, MaxMana = 1_000, Stamina = 300, MaxStamina = 300, Gold = 100_000,
            };
            Sentidos.Recibir(ServerMessages.UserIndexInServer(500));
            Sentidos.Recibir(ServerMessages.UserCharIndexInServer(500));
            Sentidos.Recibir(ServerMessages.ChangeMap(194));
            Sentidos.Recibir(ServerMessages.CharacterCreate(yo));
            Sentidos.Recibir(ServerMessages.UpdateUserStats(yo));
            Sentidos.Recibir(ServerMessages.SendSkills(Enumerable.Repeat((byte)100, GameLimits.NumSkills + 1).ToArray()));
            Sentidos.Recibir(ServerMessages.ChangeSpellSlot(1, 2));
            foreach (var (ranura, obj) in new (byte, short)[] { (1, 24), (2, 43), (3, 38), (4, 37) })
                Sentidos.Recibir(ServerMessages.ChangeInventorySlot(ranura, obj, 200, false, 0, 1));
            if (conPergamino) Sentidos.Recibir(ServerMessages.ChangeInventorySlot(Mochila, Pergamino, 1, false, 0, 1));
        }

        public Task<bool> Aprender(CancellationToken ct = default) => Invocar("AprenderHechizosAsync", Transporte, Sentidos, ct);
        public Task<bool> Comprar() => Invocar("ComprarPergaminoAsync", Transporte, Sentidos, CancellationToken.None);
        private Task<bool> Invocar(string nombre, params object[] args) =>
            (Task<bool>)typeof(Bot).GetMethod(nombre, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(Bot, args)!;

        public void OfrecerElPergamino()
        {
            Sentidos.Recibir(ServerMessages.ChangeNpcInventorySlot(Vitrina, 100, Mundo.Value.Datos.Obj(Pergamino)!.Value, Pergamino));
            Sentidos.Recibir(ServerMessages.CommerceInit());
        }

        public async Task<byte[]> Proximo()
        {
            using var corte = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            var b = new byte[4096];
            int n = await Transporte.ReceiveAsync(b, corte.Token);
            return b.Take(n).Select(x => (byte)(x ^ Transporte.Clave)).ToArray();
        }

        public async Task ConfirmarAprendizaje(Task<bool> intento, byte libre = 2)
        {
            Assert.Equal(new byte[] { (byte)ClientPacketId.UseItem, Mochila }, await Proximo());
            Sentidos.Recibir(ServerMessages.ChangeSpellSlot(libre, Hechizo));
            Sentidos.Recibir(ServerMessages.ChangeInventorySlotUser(Mochila, 3, 0));
            Assert.True(await intento.WaitAsync(TimeSpan.FromSeconds(4)));
            Assert.Contains(Sentidos.Hechizos(), h => h.Hechizo == Hechizo);
            Assert.Equal(0, Sentidos.CuantoTiene(o => o == Pergamino));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Una_muerte_o_cierre_durante_el_intento_no_impide_aprender_el_pergamino_en_la_siguiente_vida(bool cerrar)
    {
        var e = new Escena();
        var perdido = e.Aprender();
        Assert.Equal(new byte[] { (byte)ClientPacketId.UseItem, Mochila }, await e.Proximo());
        if (cerrar) e.Transporte.Close("corte antes de que el servidor atendiera UseItem");
        else e.Sentidos.Recibir(ServerMessages.UpdateHP(0));
        Assert.True(await perdido.WaitAsync(TimeSpan.FromSeconds(4)));

        e.NuevaVida(); // Libro sin la cura y el mismo pergamino intacto, como vuelve a enviarlos el servidor.
        var reintento = e.Aprender();
        if (reintento.IsCompletedSuccessfully) Assert.True(await reintento);
        await e.ConfirmarAprendizaje(reintento);
    }

    [Fact]
    public async Task Un_intento_sin_confirmacion_se_reintenta_tras_una_espera_temporal_que_las_consultas_no_renuevan()
    {
        var e = new Escena();
        var primero = e.Aprender();
        Assert.Equal(new byte[] { (byte)ClientPacketId.UseItem, Mochila }, await e.Proximo());
        Assert.True(await primero.WaitAsync(TimeSpan.FromSeconds(4)));
        long fallo = e.Ahora;
        foreach (long transcurrido in new[] { 0L, 15_000L, 29_999L })
        {
            e.Ahora = fallo + transcurrido;
            Assert.False(await e.Aprender());
        }
        e.Ahora = fallo + 30_000;
        var reintento = e.Aprender();
        if (reintento.IsCompletedSuccessfully) Assert.True(await reintento);
        await e.ConfirmarAprendizaje(reintento);
    }

    [Fact]
    public async Task No_compra_otra_copia_del_pergamino_que_ya_tiene_y_todavia_no_aprendio()
    {
        var e = new Escena();
        e.OfrecerElPergamino();
        Assert.False(await e.Comprar().WaitAsync(TimeSpan.FromSeconds(4)));
        using var corte = new CancellationTokenSource();
        var b = new byte[4096];
        var pedido = e.Transporte.ReceiveAsync(b, corte.Token).AsTask();
        try { Assert.False(pedido.IsCompletedSuccessfully, "Envio CommerceBuy aunque conserva el pergamino en la mochila"); }
        finally { corte.Cancel(); }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pedido);
        Assert.Equal(1, e.Sentidos.CuantoTiene(o => o == Pergamino));
    }

    [Fact]
    public async Task Un_libro_lleno_espera_espacio_y_puede_aprender_cuando_llega_una_ranura_libre()
    {
        var e = new Escena();
        var otros = Mundo.Value.Datos.Spells.Keys.Where(h => h != Hechizo).Take(GameLimits.MaxUserSpells)
            .Select(h => checked((short)h)).ToArray();
        Assert.Equal(GameLimits.MaxUserSpells, otros.Length);
        for (byte i = 0; i < otros.Length; i++) e.Sentidos.Recibir(ServerMessages.ChangeSpellSlot((byte)(i + 1), otros[i]));
        Assert.False(await e.Aprender().WaitAsync(TimeSpan.FromSeconds(4)));
        e.Sentidos.Recibir(ServerMessages.ChangeSpellSlot(GameLimits.MaxUserSpells, 0));
        await e.ConfirmarAprendizaje(e.Aprender(), GameLimits.MaxUserSpells);
    }

    [Fact]
    public async Task Si_no_tiene_el_pergamino_si_lo_compra_y_confirma_la_mochila()
    {
        var e = new Escena(conPergamino: false);
        e.OfrecerElPergamino();
        var comprar = e.Comprar();
        Assert.Equal(new byte[] { (byte)ClientPacketId.CommerceBuy, Vitrina, 1, 0 }, await e.Proximo());
        e.Sentidos.Recibir(ServerMessages.ChangeInventorySlot(Mochila, Pergamino, 1, false, 0, 1));
        Assert.True(await comprar.WaitAsync(TimeSpan.FromSeconds(4)));
    }
}
