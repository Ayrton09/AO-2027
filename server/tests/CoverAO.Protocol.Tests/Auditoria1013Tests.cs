using CoverAO.Server.Data;
using CoverAO.Server.Game;
using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>Los arreglos de la auditoría del 13/10 que se prueban sin levantar un mundo: los bloques de IP, el cupo con las
/// altas en curso, el código de recuperación de las cuentas del VB6, los clanes que se cierran, el Centro de Subastas, el
/// clima forzado y la ficha del /INFO.</summary>
public class Auditoria1013Tests
{
    [Theory]
    [InlineData("tcp:2001:db8:1:2:aaaa:bbbb:cccc:dddd", "2001:0db8:0001:0002::/64")]
    [InlineData("ws:[2001:db8:1:2::9]:5555", "2001:0db8:0001:0002::/64")]
    [InlineData("fd00:1:2:3:1111::1", "fd00:0001:0002:0003::/64")]
    [InlineData("tcp:1.2.3.4:7666", "1.2.3.4")]
    [InlineData("::ffff:1.2.3.4", "1.2.3.4")]
    [InlineData("bot:Milo:0", "Milo")]
    public void Una_ipv6_cuenta_su_bloque_64_y_una_ipv4_ella_misma(string endpoint, string bloque)
        => Assert.Equal(bloque, ConnectionGuard.Bloque(endpoint));

    [Fact]
    public void El_tope_agrupa_ipv6_con_primer_hexteto_alfabetico_aunque_ya_esten_normalizadas()
    {
        var guard = new ConnectionGuard(maxTotal: 100, maxPerIp: 1);
        string primera = ConnectionGuard.NormalizeIp("[fd00:1:2:3:1111::1]:1234");
        string segunda = ConnectionGuard.NormalizeIp("[fd00:1:2:3:2222::1]:4321");
        Assert.True(guard.TryAcquire(primera));
        Assert.False(guard.TryAcquire(segunda));
        Assert.True(guard.TryAcquire("fd00:1:2:4::1"));
        guard.Release(primera);
        Assert.True(guard.TryAcquire(segunda));
    }

    [Fact]
    public void El_tope_de_conexiones_es_por_bloque()
    {
        var guard = new ConnectionGuard(maxTotal: 100, maxPerIp: 2);
        Assert.True(guard.TryAcquire("2001:db8:1:2::1"));
        Assert.True(guard.TryAcquire("2001:db8:1:2::2"));
        Assert.False(guard.TryAcquire("2001:db8:1:2::3"));   // otra dirección del mismo /64
        Assert.True(guard.TryAcquire("2001:db8:1:3::1"));    // otro /64
        guard.Release("2001:db8:1:2::1");
        Assert.True(guard.TryAcquire("2001:db8:1:2::4"));
    }

    [Fact]
    public void El_cupo_cuenta_las_altas_que_todavia_se_estan_haciendo()
    {
        var cupo = new LoginThrottle(maxFails: 3, windowMs: 600_000, blockMs: 600_000);
        Assert.False(cupo.LlegariaAlTope("1.2.3.4", 2));
        Assert.True(cupo.LlegariaAlTope("1.2.3.4", 3));
        cupo.RecordFailure("1.2.3.4");
        Assert.True(cupo.LlegariaAlTope("1.2.3.4", 2));
        Assert.False(cupo.LlegariaAlTope("1.2.3.4", 1));
    }

    /// <summary>El código del VB6 es SHA-256(código + sal) con la MISMA sal que la clave (Cuentas.bas:83): migrar la clave
    /// borraba la sal y el código no volvía a verificar nunca.</summary>
    [Fact]
    public void El_codigo_de_una_cuenta_del_vb6_sigue_valiendo_despues_de_migrar_la_clave()
    {
        const string sal = "abcdefghij";
        var cuenta = new Account
        {
            Name = "vieja",
            PasswordHash = PasswordHasher.Hash("clave_vieja", sal),
            Salt = sal,
            UserCodeHash = PasswordHasher.Hash("1234", sal),
        };
        Assert.True(cuenta.VerifyPassword("clave_vieja", out bool migrada));
        Assert.True(migrada);
        Assert.True(PasswordHasher.IsModern(cuenta.PasswordHash));
        Assert.True(cuenta.VerifyUserCode("1234"));
        Assert.False(cuenta.VerifyUserCode("4321"));

        cuenta.UserCodeHash = PasswordHasher.HashModern("1234");   // la recuperación lo pasa al formato moderno
        cuenta.PonerClave(PasswordHasher.HashModern("otra_clave"));
        Assert.Equal("", cuenta.Salt);
        Assert.True(cuenta.VerifyUserCode("1234"));
    }

    [Fact]
    public void Cerrar_un_clan_lo_saca_de_las_guerras_y_alianzas_de_los_demas()
    {
        var world = new World();
        var a = new Guild { Name = "Lobos" };
        var b = new Guild { Name = "Osos" };
        a.Enemies.Add("Osos"); a.Allies.Add("Zorros"); a.PeaceOffers["Osos"] = "paz";
        b.Enemies.Add("Lobos");
        world.LoadGuilds(new[] { a, b });
        Assert.DoesNotContain("Zorros", a.Allies);   // al cargar se olvida el clan que ya no existe

        world.RemoveGuild("Osos");

        Assert.Empty(a.Enemies);
        Assert.Empty(a.PeaceOffers);
    }

    [Fact]
    public void El_centro_sabe_si_tiene_algo_de_un_personaje()
    {
        var centro = new CentroDeSubastas();
        Assert.False(centro.TieneAlgo("Aldo"));
        centro.Cargar(new[] { new Subasta(1, "Beto", 1, 1, 100, 0, 150, "Aldo", 9_999_999_999) }, Array.Empty<EntregaDeSubasta>());
        Assert.True(centro.TieneAlgo("aldo"));    // va ganando la oferta
        Assert.True(centro.TieneAlgo("Beto"));    // la vende
        Assert.False(centro.TieneAlgo("Ciro"));
    }

    [Fact]
    public void Un_lluvia_forzado_queda_hasta_que_el_reloj_lo_suelta()
    {
        try
        {
            Weather.Soltar();
            Assert.False(Weather.Forzado);
            Weather.Forzar(1);
            Assert.True(Weather.Forzado);
            Weather.Forzar(1);                 // el mismo otra vez lo apaga, pero sigue forzado (despejado)
            Assert.True(Weather.Forzado);
            Weather.Soltar();
            Assert.False(Weather.Forzado);
        }
        finally { Weather.Soltar(); }
    }

    [Fact]
    public void La_ficha_del_info_lleva_el_bando_y_los_matados()
    {
        var p = new Player { CharIndex = 1, Name = "Aldo", Account = "aldo", CiudadanosMatados = 7, RenegadosMatados = 3 };
        p.FactionStatus = Player.StatusArmada;
        byte[] b = ServerMessages.CharacterInfo(p);
        // Al final: Boolean armada, Boolean caos, Long ciudadanos, Long renegados.
        Assert.Equal(1, b[^10]);
        Assert.Equal(0, b[^9]);
        Assert.Equal(7, BitConverter.ToInt32(b, b.Length - 8));
        Assert.Equal(3, BitConverter.ToInt32(b, b.Length - 4));
    }
}
