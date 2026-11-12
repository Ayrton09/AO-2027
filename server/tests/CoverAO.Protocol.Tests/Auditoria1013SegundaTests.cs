using CoverAO.Server.Data;
using CoverAO.Server.Game;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>La segunda pasada de la auditoría de la 1.0.13: un nombre que cambia de cuenta entre el hash y la lectura no se lleva
/// el personaje del otro, ni al entrar ni al borrar.</summary>
public class Auditoria1013SegundaTests
{
    private static Account Cuenta(string nombre) => new() { Name = nombre, PasswordHash = "pbkdf2$1$a$b" };

    private static Persistence ConDosCuentas(out AlmacenSqlite almacen)
    {
        almacen = AlmacenSqlite.EnMemoria();
        var ana = Cuenta("Ana");
        almacen.GuardarCuenta(ana);
        Assert.True(almacen.CrearPersonaje(ana, FichasDePrueba.Llena("Ajeno")));
        almacen.GuardarCuenta(Cuenta("Beto"));
        return new Persistence(almacen, new Leveling(GameData.Load().LevelExp));
    }

    [Fact]
    public async Task La_lectura_de_entrada_exige_que_el_personaje_siga_siendo_de_la_cuenta()
    {
        using var db = ConDosCuentas(out _);
        Assert.NotNull((await db.LeerParaEntrarAsync("Ajeno", "Ana")).Ficha);   // control: el dueño lo lee
        Assert.Null((await db.LeerParaEntrarAsync("Ajeno", "Beto")).Ficha);     // otra cuenta, no
    }

    [Fact]
    public void El_borrado_con_cuenta_no_aparta_el_personaje_de_otra()
    {
        using var db = ConDosCuentas(out var almacen);
        Assert.False(db.ArchivarCharacter("Ajeno", "Beto"));
        Assert.NotNull(almacen.LeerPersonaje("Ajeno"));
        Assert.True(db.ArchivarCharacter("Ajeno", "Ana"));
        Assert.Null(almacen.LeerPersonaje("Ajeno"));
    }
}
