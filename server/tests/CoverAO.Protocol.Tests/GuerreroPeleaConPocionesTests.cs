using System.Reflection;
using CoverAO.Protocol;
using CoverAO.Server.Data;
using CoverAO.Server.Game;
using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// El Guerrero pelea con pociones (pedido del tester del 20/11): antes de pelear con gente se toma la Verde y la Amarilla, y
/// paralizado sin magia, la de Remover Parálisis.
/// </summary>
public sealed class GuerreroPeleaConPocionesTests
{
    private static readonly Lazy<(GameData Datos, Navegante Mapas)> Mundo = new(() =>
    {
        var datos = GameData.Load();
        return (datos, new Navegante(datos));
    });
    private static readonly MethodInfo Cazar = typeof(Bot).GetMethod("CazarAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private const short Amarilla = 36, Verde = 39, RemoverParalisis = 893, Alquimista = 14, Espada = 569;
    private const byte RanuraDelArma = 1, RanuraVerde = 2, RanuraAmarilla = 3, RanuraRemover = 4;

    [Fact]
    public void Las_pociones_son_las_del_juego_y_las_vende_el_alquimista()
    {
        var datos = Mundo.Value.Datos;
        Assert.True(Bot.EsPocionDeAgilidad(datos.Obj(Amarilla)));
        Assert.True(Bot.EsPocionDeFuerza(datos.Obj(Verde)));
        // Duran más de lo que el bot las da por activas: 50.000 vueltas de 40 ms.
        Assert.True(datos.Obj(Amarilla)!.DuracionEfectoEnVueltas * 40L > Bot.EfectoDeAtributoMs);
        Assert.True(datos.Obj(Verde)!.DuracionEfectoEnVueltas * 40L > Bot.EfectoDeAtributoMs);
        var stock = datos.Npc(Alquimista)!.StockItems.Select(s => (short)s.ObjIndex).ToList();
        Assert.Contains(Amarilla, stock);
        Assert.Contains(Verde, stock);
        Assert.True(datos.Obj(RemoverParalisis) is { IsPotion: true, SubType: 5 } o
                    && datos.Spell((short)o.LanzaHechizo) is { IsRemoveParalysis: true });
    }

    [Theory]
    [InlineData(true, 1_000, 0, true)]
    [InlineData(false, 1_000, 0, false)]      // sin la poción
    [InlineData(true, 1_000, 5_000, false)]   // todavía le dura la anterior
    public void Cuando_se_la_toma(bool tiene, long ahora, long hasta, bool toma) =>
        Assert.Equal(toma, Bot.TomaLaPocionDeAtributo(tiene, ahora, hasta));

    [Fact]
    public void Por_ahora_solo_el_guerrero()
    {
        Assert.True(Bot.UsaPocionesDeAtributo(GameLimits.ClaseGuerrero));
        Assert.False(Bot.UsaPocionesDeAtributo(GameLimits.ClaseMago));
    }

    [Fact]
    public async Task Antes_de_pelear_se_toma_la_verde_y_la_amarilla_y_despues_pega()
    {
        var f = Armar(GameLimits.ClaseGuerrero);
        using var corte = new CancellationTokenSource();
        var pelea = Empezar(f, corte.Token);
        try
        {
            Assert.Equal(new byte[] { (byte)ClientPacketId.UseItem, RanuraVerde }, await Proximo(f));
            Assert.Equal(new byte[] { (byte)ClientPacketId.UseItem, RanuraAmarilla }, await Proximo(f));
            Assert.Equal((byte)ClientPacketId.ChangeHeading, (await Proximo(f))[0]);
            Assert.Equal((byte)ClientPacketId.attack, (await Proximo(f))[0]);
        }
        finally { await Cancelar(pelea, corte); }

        // Y en la pelea siguiente no se las vuelve a tomar: todavía le duran.
        using var otro = new CancellationTokenSource();
        var segunda = Empezar(f, otro.Token);
        try { Assert.Equal((byte)ClientPacketId.ChangeHeading, (await Proximo(f))[0]); }
        finally { await Cancelar(segunda, otro); }
    }

    [Fact]
    public async Task Las_otras_clases_no_se_las_toman()
    {
        var f = Armar(GameLimits.ClaseGladiador);
        using var corte = new CancellationTokenSource();
        var pelea = Empezar(f, corte.Token);
        try { Assert.Equal((byte)ClientPacketId.ChangeHeading, (await Proximo(f))[0]); }
        finally { await Cancelar(pelea, corte); }
    }

    [Fact]
    public async Task Paralizado_se_toma_la_de_remover_paralisis()
    {
        var f = Armar(GameLimits.ClaseGuerrero, distancia: 3, conRemover: true);
        f.Sentidos.Recibir(ServerMessages.ParalizeOK());
        using var corte = new CancellationTokenSource();
        var pelea = Empezar(f, corte.Token);
        try
        {
            Assert.Equal(new byte[] { (byte)ClientPacketId.UseItem, RanuraVerde }, await Proximo(f));
            Assert.Equal(new byte[] { (byte)ClientPacketId.UseItem, RanuraAmarilla }, await Proximo(f));
            Assert.Equal(new byte[] { (byte)ClientPacketId.UseItem, RanuraRemover }, await Proximo(f));
        }
        finally { await Cancelar(pelea, corte); }
    }

    private sealed record Fixture(Bot Bot, SentidosDelBot Sentidos, TransporteDeBot Transporte, TaskCompletionSource Cerrada);

    /// <summary>Un bot de nivel 30 en un claro del mapa 18 con espada, una Verde y una Amarilla, y una persona de otro bando al
    /// norte, a <paramref name="distancia"/>.</summary>
    private static Fixture Armar(byte clase, int distancia = 1, bool conRemover = false)
    {
        var (datos, mapas) = Mundo.Value;
        const short mapa = 18;
        var sitio = (from y in Enumerable.Range(20, 60)
                     from x in Enumerable.Range(20, 60)
                     where (from dy in Enumerable.Range(-4, 9)
                            from dx in Enumerable.Range(-4, 9)
                            select datos.IsBlockedTile(mapa, x + dx, y + dy) || datos.MapExits.ContainsKey((mapa, x + dx, y + dy)))
                           .All(b => !b)
                     select (X: (byte)x, Y: (byte)y)).First();
        var s = new SentidosDelBot { MiNombre = "Ciro" };
        var yo = new Player
        {
            CharIndex = 500, Name = "Ciro", Account = "ciro_test", Pos = new WorldPos(mapa, sitio.X, sitio.Y), Level = 30,
            FactionStatus = Player.StatusCiudadano, MaxHp = 300, Hp = 300, MaxStamina = 400, Stamina = 400, Class = clase,
        };
        s.Recibir(ServerMessages.UserIndexInServer(500));
        s.Recibir(ServerMessages.ChangeMap(mapa));
        s.Recibir(ServerMessages.CharacterCreate(yo));
        s.Recibir(ServerMessages.UpdateUserStats(yo));
        s.Recibir(ServerMessages.ChangeInventorySlot(RanuraDelArma, Espada, 1, true, 0, 1));
        s.Recibir(ServerMessages.ChangeInventorySlot(RanuraVerde, Verde, 1, false, 0, 1));
        s.Recibir(ServerMessages.ChangeInventorySlot(RanuraAmarilla, Amarilla, 1, false, 0, 1));
        if (conRemover) s.Recibir(ServerMessages.ChangeInventorySlot(RanuraRemover, RemoverParalisis, 1, false, 0, 1));
        s.Recibir(ServerMessages.CharacterCreate(new Player
        {
            CharIndex = 501, Name = "Kael", Account = "kael_test", FactionStatus = Player.StatusCriminal,
            Pos = new WorldPos(mapa, sitio.X, (byte)(sitio.Y - distancia)),
        }));
        var bot = new Bot("Ciro", null, new EntornoDeBots(null!, new ServerIni(), 0, 0, datos, mapas, null!), 1);
        typeof(Bot).GetField("_clase", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(bot, clase);
        return new Fixture(bot, s, new TransporteDeBot("Ciro"), new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
    }

    private static Task<bool> Empezar(Fixture f, CancellationToken ct) =>
        (Task<bool>)Cazar.Invoke(f.Bot, new object[] { f.Transporte, f.Sentidos, (short)501, f.Cerrada.Task, ct, true })!;

    private static async Task<byte[]> Proximo(Fixture f)
    {
        using var corte = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        var buffer = new byte[4_096];
        int n = await f.Transporte.ReceiveAsync(buffer, corte.Token);
        return buffer.Take(n).Select(b => (byte)(b ^ f.Transporte.Clave)).ToArray();
    }

    private static async Task Cancelar(Task tarea, CancellationTokenSource corte)
    {
        corte.Cancel();
        try { await tarea.WaitAsync(TimeSpan.FromSeconds(3)); }
        catch (OperationCanceledException) when (corte.IsCancellationRequested) { }
    }
}
