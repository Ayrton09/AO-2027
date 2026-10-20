using CoverAO.Server.Data;
using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// LA PELEA CON GENTE CON MAGIA: el bot que pelea con gente compra Remover parálisis y uno de los de quietar (Paralizar o
/// Inmovilizar), quieta a la persona que está a más de un paso, y paralizado se saca la parálisis. Ver <see cref="Bot.QuietaMs"/>.
/// </summary>
public sealed class QuietarEnLaPeleaTests
{
    private static readonly SpellInfo Dano = new(7, "Descarga", SubeHp: 2, MinHp: 20, MaxHp: 40, ManaCost: 30, Target: 1);
    private static readonly SpellInfo Paralizar = new(9, "Paralizar", SubeHp: 0, MinHp: 0, MaxHp: 0, ManaCost: 350, Target: 3,
                                                      Paraliza: 1, StaRequerido: 90);
    private static readonly SpellInfo Remover = new(10, "Remover paralisis", SubeHp: 0, MinHp: 0, MaxHp: 0, ManaCost: 300, Target: 3,
                                                    RemoverParalisis: 1, StaRequerido: 80);
    private static readonly SpellInfo Inmovilizar = new(24, "Inmovilizar", SubeHp: 0, MinHp: 0, MaxHp: 0, ManaCost: 250, Target: 3,
                                                        StaRequerido: 60, Inmoviliza: 1);
    private static readonly SpellInfo Sanar = new(32, "Sanar", SubeHp: 1, MinHp: 60, MaxHp: 100, ManaCost: 750, Target: 1,
                                                  RemoverParalisis: 1, StaRequerido: 270);

    private static readonly (byte Ranura, short Hechizo)[] Nada = System.Array.Empty<(byte, short)>();

    [Fact]
    public void Los_de_la_pelea_con_gente_valen_sólo_para_el_que_pelea_con_gente()
    {
        Assert.False(Bot.ValeLaPenaElPergamino(Remover, Nada), "sin pelear con gente, como siempre: daño o cura");
        Assert.False(Bot.ValeLaPenaElPergamino(Paralizar, Nada));
        Assert.True(Bot.ValeLaPenaElPergamino(Remover, Nada, peleaConGente: true));
        Assert.True(Bot.ValeLaPenaElPergamino(Paralizar, Nada, peleaConGente: true));
        Assert.True(Bot.ValeLaPenaElPergamino(Inmovilizar, Nada, peleaConGente: true));
        Assert.False(Bot.ValeLaPenaElPergamino(Inmovilizar, Nada, peleaConGente: true, yaSabeQuietar: true), "con uno de quietar alcanza");
        Assert.True(Bot.ValeLaPenaElPergamino(Remover, Nada, peleaConGente: true, yaSabeQuietar: true));
        Assert.False(Bot.ValeLaPenaElPergamino(Remover, new (byte, short)[] { (3, 10) }, peleaConGente: true), "ya lo sabe");
        Assert.True(Bot.ValeLaPenaElPergamino(Dano, Nada, peleaConGente: true), "el de daño, igual que antes");
        Assert.True(Bot.PeleaConGente(Bot.NivelParaPelearConGente) && !Bot.PeleaConGente(Bot.NivelParaPelearConGente - 1));
    }

    [Fact]
    public void En_la_vitrina_va_primero_lo_que_pega_después_remover_y_al_final_quietar()
    {
        Assert.True(Bot.ValorDelPergamino(Dano) > Bot.ValorDelPergamino(Remover));
        Assert.True(Bot.ValorDelPergamino(Remover) > Bot.ValorDelPergamino(Paralizar));
        Assert.Equal(Bot.ValorDelPergamino(Paralizar), Bot.ValorDelPergamino(Inmovilizar));
        Assert.True(Bot.ValorDelPergamino(Inmovilizar) > 0, "con cero la vitrina no lo elige nunca");
    }

    [Fact]
    public void Elige_el_más_barato_que_cumple_y_puede_pagar()
    {
        var sabe = new (byte, SpellInfo?)[] { (1, Dano), (2, Paralizar), (3, Inmovilizar), (4, Remover), (5, Sanar), (6, null) };
        var nada = new HashSet<short>();
        Assert.Equal((byte)3, Bot.ElMasBarato(sabe, Bot.EsDeQuietar, 1000, 1000, nada));
        Assert.True(Bot.ElMasBarato(sabe, Bot.EsDeQuietar, 1000, 1000, new HashSet<short> { 24 }) == 2, "el rechazado se saltea");
        Assert.Null(Bot.ElMasBarato(sabe, Bot.EsDeQuietar, 200, 1000, nada));
        Assert.Null(Bot.ElMasBarato(sabe, Bot.EsDeQuietar, 1000, 50, nada));
        Assert.Equal((byte)4, Bot.ElMasBarato(sabe, h => h.IsRemoveParalysis, 1000, 1000, nada));
        Assert.True(Bot.ElMasBarato(new (byte, SpellInfo?)[] { (5, Sanar) }, h => h.IsRemoveParalysis, 1000, 1000, nada) == 5,
                    "Sanar también saca la parálisis");
    }

    [Fact]
    public void Quieta_sin_gastar_el_maná_de_la_cura()
    {
        var sabe = new (byte, SpellInfo?)[] { (3, Inmovilizar) };
        var nada = new HashSet<short>();
        Assert.True(Bot.ElMasBarato(sabe, Bot.EsDeQuietar, 300, 1000, nada) == 3);
        Assert.Null(Bot.ElMasBarato(sabe, Bot.EsDeQuietar, 300, 1000, nada, reserva: 60));
        Assert.True(Bot.ElMasBarato(sabe, Bot.EsDeQuietar, 310, 1000, nada, reserva: 60) == 3);
    }

    [Fact]
    public void La_da_por_quieta_el_rato_entero_sólo_si_el_hechizo_cobró()
    {
        Assert.Equal(1_000 + Bot.QuietaMs, Bot.QuietaHasta(1_000, cobro: true));
        Assert.Equal(1_000 + Bot.ReintentoDeQuietarMs, Bot.QuietaHasta(1_000, cobro: false));
        Assert.True(Bot.ReintentoDeQuietarMs < Bot.QuietaMs);
    }

    [Theory]
    [InlineData(7, 1_000, 50, 50, true)]
    [InlineData(8, 1_000, 50, 50, false)]    // otra persona
    [InlineData(7, 30_000, 50, 50, false)]   // ya pasó el rato
    [InlineData(7, 1_000, 51, 50, false)]    // se movió
    [InlineData(7, 1_000, 50, 49, false)]
    public void La_cuenta_quieta_mientras_es_la_misma_en_su_baldosa_y_dentro_del_rato(short quien, long ahora, byte x, byte y, bool quieta)
    {
        (short, long, byte, byte)? q = (7, Bot.QuietaMs, 50, 50);
        Assert.Equal(quieta, Bot.SigueQuieta(q, quien, x, y, ahora));
        Assert.False(Bot.SigueQuieta(null, 7, 50, 50, 0));
    }

    /// <summary>
    /// LA DATA: los tres pergaminos existen, los vende el Hechicero (NPC 51 y 63) y los casters de las armadas los pueden aprender
    /// (Mago, Clérigo, Druida, Bardo, Paladín, Asesino y Nigromante: clases 1, 2, 4, 6, 7, 9 y 18). Y la duración que supone
    /// <see cref="Bot.QuietaMs"/> queda por debajo de la del Server.ini.
    /// </summary>
    [Fact]
    public void Los_pergaminos_de_la_pelea_con_gente_están_en_la_data()
    {
        var d = GameData.Load();
        if (d.Spell(9) is not { } paralizar || d.Spell(10) is not { } remover || d.Spell(24) is not { } inmovilizar) return;
        Assert.True(Bot.EsDeQuietar(paralizar) && Bot.EsDeQuietar(inmovilizar) && remover.IsRemoveParalysis);
        var vendidos = d.Npcs.Values.Where(n => n.StockItems is not null).SelectMany(n => n.StockItems!.Select(s => s.ObjIndex)).ToHashSet();
        foreach (int obj in new[] { 206, 207, 530 })
        {
            Assert.True(d.Obj(obj) is { IsPergamino: true }, $"premisa: {obj} es un pergamino");
            Assert.Contains(obj, vendidos);
            foreach (byte clase in new byte[] { 1, 2, 4, 6, 7, 9, 18 })
                Assert.False(d.Obj(obj)!.ClassForbidden(clase), $"la clase {clase} no puede aprender el {obj}");
        }
        Assert.True(Bot.QuietaMs < new ServerIni().IntervaloParalizado * 40, "la da por quieta más de lo que dura");
    }
}
