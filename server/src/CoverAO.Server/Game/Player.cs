namespace CoverAO.Server.Game;

/// <summary>A tile position in the world.</summary>
public readonly record struct WorldPos(short Map, byte X, byte Y);

/// <summary>
/// A connected character. Clean replacement for the VB6 <c>UserList()</c> global + god <c>Type User</c>:
/// domain state only, no network queues welded in. Fields map to the .chr file sections (see
/// <c>Data/CharacterFile</c>): [INIT], [STATS], [Inventory], [HECHIZOS], [ATRIBUTOS].
/// </summary>
public sealed class Player
{
    public required short CharIndex { get; init; }
    public required string Name { get; set; }
    public required string Account { get; set; }

    /// <summary>The client this character is bound to. Set at login.</summary>
    public IConnection? Connection { get; set; }

    public WorldPos Pos { get; set; }
    public byte Heading { get; set; } = 3; // South

    // [INIT]
    public byte Gender { get; set; }
    public byte Race { get; set; }
    public byte Homeland { get; set; }
    public byte Class { get; set; }
    public int UsersKilled { get; set; }  // Stats.UsuariosMatados — shown in MiniStats
    public int NpcsKilled { get; set; }   // Stats.NPCsMuertos
    /// <summary>Veces que murió este personaje (VB6 <c>flags.MuertesUsuario</c>, +1 en UserDie,
    /// Modulo_UsUaRiOs.bas:1260). Va en MiniStats y persiste en el .chr como <c>FLAGS/Murio</c>.</summary>
    public int Deaths { get; set; }
    public string Description { get; set; } = "";
    public short Body { get; set; }
    public short Head { get; set; }
    public short OrigHead { get; set; }   // cabeza "viva" (OrigChar.Head del VB6); se restaura al revivir tras la cabeza de cadáver
    // Auto-revive en ARENA (VB6 flags.Resucitando + Counters.IntervaloRevive): al morir en una zona de pelea el
    // jugador queda cadáver con Resucitando=true, y DoResucitar lo levanta recién ~2500ms después (ReviveAtTick).
    public bool Resucitando { get; set; }
    public long ReviveAtTick { get; set; }
    public short WeaponAnim { get; set; }
    public short ShieldAnim { get; set; }
    public short HelmetAnim { get; set; }
    public long UpTimeSeconds { get; set; }

    // [FACCIONES] Status. El VB6 (eFaccion) tiene SEIS: 1=Renegado, 2=Ciudadano Imperial, 3=Republicano,
    // 4=Legión del Caos, 5=Armada Real, 6=Milicia Republicana. Son dos "imperios" enfrentados —Imperio
    // (ciudadano → Armada) y República (republicano → Milicia)— más el Caos, al que se entra desde renegado.
    // Un Convertidor pasa de renegado a ciudadano o republicano; el NPC de facciones del bando enlista;
    // RetirarFaccion devuelve al status civil correspondiente.
    //
    // Los valores son LOS DEL VB6. El slice arrancó corriendo los dos primeros (renegado a 0, ciudadano a 1) y
    // eso rompía la paridad de bytes del `Privileges` de CharacterCreate: el oráculo manda 2 para un ciudadano
    // (`fase5/vb6-oracle-bootstrap.txt`, ORACLEHERO) y nosotros mandábamos 1, así que el cliente VB6 original
    // habría pintado a todos los ciudadanos con el color del renegado. OJO: este byte se PERSISTE en el .chr
    // (sección FACCIONES), y no hay marca de versión para distinguir un archivo viejo de uno nuevo — un `data/`
    // guardado antes de este cambio hay que borrarlo para que se re-seedee, o sus ciudadanos quedan renegados.
    public byte FactionStatus { get; set; } = StatusCiudadano;
    public const byte StatusCriminal = 1, StatusCiudadano = 2, StatusRepublicano = 3,
                      StatusCaos = 4, StatusArmada = 5, StatusMilicia = 6;
    // OVERRIDE DE STAFF (Server.ini). Ser GM se resuelve EN CADA LOGIN desde las listas del ini, no se guarda:
    // el host saca un nombre y esa persona deja de ser GM la próxima vez que entra. Para que eso funcione de
    // verdad, lo que el ini pisa —privilegio y status de facción— no puede terminar en el .chr, o el personaje
    // se quedaría con el rango puesto. Y además pisar el status perdería la facción real: 7/8/9 no son valores
    // legales, y el saneo del cargador los devuelve como ciudadano, así que un Armada Real ascendido a GM y
    // después bajado volvería sin ejército. Acá se guarda lo de abajo del override; null = no hay override.
    /// <summary>Status de facción real, mientras el ini pisa el vivo con el de GM (7/8/9).</summary>
    public byte? StatusCivilBajoStaff { get; set; }
    /// <summary>Privilegio real, mientras el ini pisa el vivo con el del rango de staff.</summary>
    public byte? PrivilegioCivilBajoStaff { get; set; }
    /// <summary>El status que va al .chr: el civil de abajo si hay override de staff, si no el vivo.</summary>
    public byte FactionStatusPersistido => StatusCivilBajoStaff ?? FactionStatus;
    /// <summary>El privilegio que va al .chr: el civil de abajo si hay override de staff, si no el vivo.</summary>
    public byte PrivilegiosPersistidos => PrivilegioCivilBajoStaff ?? Privileges;

    /// <summary>Cambia la facción por GAMEPLAY (enlistarse, convertirse, volverse criminal, retirarse).
    /// Si hay override de staff en curso el cambio va ABAJO del override: el GM no pierde el color del nick a
    /// mitad de sesión por pegarle a un ciudadano, y el cambio igual se persiste. Sin override es un set común.
    /// Los sitios que ponen el status de STAFF no pasan por acá — ésos SÍ pisan el vivo, a propósito.</summary>
    public void CambiarFaccion(byte status)
    {
        if (StatusCivilBajoStaff is null) FactionStatus = status;
        else StatusCivilBajoStaff = status;
    }

    public bool IsCitizen => FactionStatus == StatusCiudadano;        // ciudadano imperial (VB6 esCiuda)
    public bool IsRepublicano => FactionStatus == StatusRepublicano;  // VB6 esRepu
    public bool IsArmada => FactionStatus == StatusArmada;
    public bool IsMilicia => FactionStatus == StatusMilicia;          // VB6 esMili
    public bool IsCaos => FactionStatus == StatusCaos;
    public bool IsRenegado => FactionStatus == StatusCriminal;        // VB6 esRene
    public bool IsEnlisted => IsArmada || IsMilicia || IsCaos;

    /// <summary>¿El mercader de este mapa me atiende? (VB6 Protocol.bas:5203-5220, repetido igual en
    /// Acciones.bas:180-197). Es el gate del <c>ChatOverHeadLocale(592, 4)</c>: "¡Largo de aquí!, no tengo
    /// ningún interés en comerciar con enemigos".
    ///
    /// <para>Los tres <c>If</c> del original, tal cual:</para>
    /// <code>
    /// (esCiuda Or esArmada) And battle_mode &lt;&gt; 2 And battle_mode &lt;&gt; 3  -> rechaza
    /// (esRepu  Or esMili)   And battle_mode &lt;&gt; 2 And battle_mode &lt;&gt; 3  -> rechaza
    /// (esRene  Or esCaos)   And battle_mode &lt;&gt; 3                            -> rechaza
    /// </code>
    ///
    /// <para>Y como TODO personaje nace con status (2 ciudadano, o 3 republicano si nace en Illiandor —
    /// TCP.bas:497), en la práctica la regla dice que <b>el comercio sólo existe en los mapas battle_mode 2 y
    /// 3</b>: Lindos, Rinkel, Nueva Esperanza y el Dungeon de Newbies. En Ullathorpe, que es battle_mode 1, el
    /// mercader NO te vende. Suena a bug y no lo es: medido contra el oráculo vivo el 2026-08-19 —un ciudadano
    /// recién nacido caminando del Dungeon de Newbies a Ullathorpe recibe el 592 del Carpintero, mientras que en
    /// el propio dungeon (battle_mode 3) el mismo personaje compra sin problema.</para>
    ///
    /// <para>El único que se salva es el STAFF: el original envuelve los tres <c>If</c> en
    /// <c>If .flags.Privilegios And PlayerType.User</c>, o sea que sólo gatea al jugador común. Eso lo decide el
    /// llamador, que es quien tiene los privilegios a mano.</para></summary>
    public bool MercaderMeAtiende(int battleMode)
    {
        if ((IsCitizen || IsArmada) && battleMode != 2 && battleMode != 3) return false;
        if ((IsRepublicano || IsMilicia) && battleMode != 2 && battleMode != 3) return false;
        if ((IsRenegado || IsCaos) && battleMode != 3) return false;
        return true;
    }

    /// <summary>Rango dentro del ejército (VB6 Faccion.Rango, 1..10; sube canjeando kills con el líder). 0 = sin facción.</summary>
    public byte FactionRank { get; set; }
    // Contadores de muertes POR BANDO DE LA VÍCTIMA (VB6 Faccion.*Matados, incrementados en ContarMuerte).
    // Cada ejército suma un subconjunto distinto, así que hay que llevarlos separados: el Imperio y la República
    // se cuentan mutuamente como enemigos, y el Caos cuenta a todos menos a los suyos.
    public int RenegadosMatados { get; set; }
    public int CiudadanosMatados { get; set; }
    public int RepublicanosMatados { get; set; }
    public int ArmadaMatados { get; set; }
    public int MilicianosMatados { get; set; }
    public int CaosMatados { get; set; }

    /// <summary>Enemigos que cuentan para la ARMADA REAL (VB6 EnlistarArmadaReal:308 / RecompensaArmadaReal:416:
    /// renegados + caos + republicanos + milicianos — o sea todo lo que no sea del Imperio).</summary>
    public int KillsEnemigosArmada => RenegadosMatados + CaosMatados + RepublicanosMatados + MilicianosMatados;
    /// <summary>Enemigos que cuentan para la MILICIA REPUBLICANA (VB6 EnlistarMilicia:229 / RecompensaMilicia:487:
    /// renegados + caos + armada + ciudadanos — o sea todo lo que no sea de la República).</summary>
    public int KillsEnemigosMilicia => RenegadosMatados + CaosMatados + ArmadaMatados + CiudadanosMatados;
    /// <summary>Enemigos que cuentan para el CAOS (VB6 EnlistarCaos:127 / RecompensaCaos:539: todos menos los
    /// propios caos).</summary>
    public int KillsEnemigosCaos => RenegadosMatados + ArmadaMatados + CiudadanosMatados + MilicianosMatados + RepublicanosMatados;

    // [INIT] Privilegios (PlayerType bit flags, Declares.bas:103-112): 1=User, 2=Consejero, 4=SemiDios,
    // 8=Dios, 16=Admin, 32=RoleMaster. (El comentario viejo decía "32=Admin", que es falso: Admin es 16.)
    // OJO: en ESTE install sólo el tier Dios es alcanzable (Server.ini sólo puebla esa lista), así que el
    // IsGm booleano de abajo no es una simplificación, es lo que el original distingue — ver GAPS-CODIGO-MUERTO.md.
    public byte Privileges { get; set; } = 1; // User by default
    public bool IsGm => Privileges > 1;        // any privilege above plain User is a GM
    public bool Invisible { get; set; }        // GM invisibility toggle

    // [GUILD] the guild this character belongs to ("" = none).
    public string GuildName { get; set; } = "";

    // [AMIGOS] friends list (character names).
    public List<string> Friends { get; } = new();

    /// <summary>El <c>QuienAmigo</c> del original (VB6 <c>UserList().QuienAmigo</c>): quién me mandó la última
    /// solicitud de amistad y está esperando mi <c>/FACCEPT</c>. Vive sólo en la sesión —el original tampoco lo
    /// guarda en el <c>.chr</c>—, así que una solicitud no sobrevive a la desconexión.</summary>
    public string QuienAmigo { get; set; } = "";

    // Spouse (casamiento), "" = single.
    public string SpouseName { get; set; } = "";
    /// <summary>A quién le mandó una propuesta de casamiento (VB6 <c>Casamiento.Candidato</c>). Es estado
    /// TRANSIENT: el original lo guarda en el UserList y no lo persiste, así que una propuesta no sobrevive
    /// a la desconexión.</summary>
    public string CandidatoCasamiento { get; set; } = "";

    // Home city id (SeleccionarHogar) — where RegresarHogar sends you. Default Ullathorpe (Cities.Ullathorpe=3),
    // consistente con el fallback de Cities; siempre lo sobreescribe CreateCharacter/FromIni.
    public byte HomeCity { get; set; } = 3;

    /// <summary>Minutos de condena que le quedan (VB6 <c>Counters.Pena</c>, seteado por /CARCEL). Se descuenta
    /// una vez por minuto y al llegar a 0 el preso sale a Libertad. Se persiste en el .chr: si no, bastaba
    /// con desloguear para salir de la cárcel.</summary>
    public int JailMinutesLeft { get; set; }

    /// <summary>
    /// Historial de sanciones, persistido en la sección <c>[PENAS]</c> del .chr (VB6 Protocol.bas:7355 para
    /// /CARCEL y Admin.bas:479/529 para las demás). Cada línea es
    /// <c>"&lt;gm&gt;: CARCEL &lt;n&gt;m, MOTIVO: &lt;motivo&gt; &lt;fecha&gt; &lt;hora&gt;"</c>, con el nick del
    /// GM y el motivo en minúsculas. Es el registro que hace auditable una sanción: sin él, encarcelar no
    /// deja rastro y no hay con qué resolver una disputa después.
    /// </summary>
    public List<string> Penalties { get; } = new();

    // [FLAGS] Ban — baneo POR PERSONAJE (VB6 flags.Ban), distinto del baneo por cuenta del panel admin.
    // Un char con esto en true no puede entrar al mundo, aunque su cuenta esté limpia. Lo pone /BAN.
    public bool Banned { get; set; }

    // Guild-fundation flow: chosen alignment (0 = not founding), and claimed level-milestone prizes.
    public byte FoundingAlignment { get; set; }
    public HashSet<int> ClaimedPrizes { get; } = new();

    /// <summary>El familiar del personaje (Mago, Druida y Cazador; el resto lo tiene vacio). Es un AGREGADO
    /// nuestro, no un port: ver <see cref="Familiar"/>. Va aparte de las mascotas a proposito -- ocupa su
    /// propio lugar y no come del cupo de tres, porque no se doma ni se invoca, es tuyo desde que lo elegis.</summary>
    public Familiar Familiar { get; } = new();

    // ---- MASCOTAS ----------------------------------------------------------------------------------------
    // El original lleva las mascotas del jugador en DOS arreglos paralelos de 3 lugares más un contador
    // (VB6 Declares.bas:1431-1433), y la distinción entre los dos es la que hace andar todo lo demás:
    //
    //   MascotasType[i]  = la FICHA de la criatura que ocupa el lugar i. Distinto de 0 quiere decir "tenés esa
    //                      mascota", esté o no puesta en el mundo.
    //   MascotasIndex[i] = el charIndex de la criatura VIVA. En 0 quiere decir que la mascota existe pero no
    //                      está en ningún mapa: es el lugar FANTASMA, que el original usa a propósito para la
    //                      que se doma en zona segura (`DoDomar` Trabajo.bas:1116) y para la que espera afuera
    //                      mientras su amo camina por una ciudad. Cuando el amo vuelve a un mapa inseguro,
    //                      `WarpMascotas` la trae de vuelta desde ese lugar.
    //   NroMascotas      = cuántos lugares están ocupados (por TYPE, no por criaturas en el mundo).
    //
    // Acá antes se contaban los NPCs vivos con `OwnerCharIndex` del jugador. Era más simple y no perdía lugares,
    // pero no podía representar una mascota que existe y no está en el mundo — y sin eso `WarpMascotas` no se
    // puede portar: cruzar de mapa te dejaba la mascota tirada en el mapa viejo para siempre.
    public readonly short[] MascotasType = new short[GameLimits.MaxMascotas + 1];   // 1..3, el 0 no se usa
    public readonly short[] MascotasIndex = new short[GameLimits.MaxMascotas + 1];  // 1..3, el 0 no se usa
    public int NroMascotas { get; set; }

    /// <summary>El primer lugar LIBRE, o 0 si están los tres ocupados. Mira los TYPE y no los índices, como el
    /// original (VB6 <c>FreeMascotaIndex</c>, Trabajo.bas:1038, con el comentario de ZaMa que aclara justamente
    /// eso): un lugar con una mascota fuera del mundo sigue ocupado.</summary>
    public int PrimerSlotLibre()
    {
        for (int i = 1; i <= GameLimits.MaxMascotas; i++)
            if (MascotasType[i] == 0) return i;
        return 0;
    }

    /// <summary>Cuántas mascotas de esa ficha tenés (VB6 <c>PuedeDomarMascota</c>, Trabajo.bas:1162).</summary>
    public int MascotasDeTipo(short ficha)
    {
        int n = 0;
        for (int i = 1; i <= GameLimits.MaxMascotas; i++)
            if (MascotasType[i] == ficha) n++;
        return n;
    }

    /// <summary>Ocupa un lugar con una criatura recién domada o invocada.</summary>
    public void OcuparSlotDeMascota(int slot, short charIndexNpc, short ficha)
    {
        if (slot is < 1 || slot > GameLimits.MaxMascotas) return;
        MascotasIndex[slot] = charIndexNpc;
        MascotasType[slot] = ficha;
        NroMascotas++;
    }

    /// <summary>La criatura salió del mundo: se le libera el lugar (VB6 <c>QuitarMascota</c>,
    /// MODULO_NPCs.bas:46). Ojo que el original lo llama desde <c>ResetNpcMainInfo</c>, o sea desde CUALQUIER
    /// salida del mundo —muerte, despawn, vencimiento—, y borra el TYPE además del índice. Los dos caminos que
    /// quieren conservar la mascota fuera del mundo (domar en zona segura y <c>WarpMascotas</c>) vuelven a poner
    /// el TYPE a mano DESPUÉS de sacarla, y eso es lo que crea el lugar fantasma.</summary>
    public void QuitarMascota(short charIndexNpc)
    {
        for (int i = 1; i <= GameLimits.MaxMascotas; i++)
        {
            if (MascotasIndex[i] != charIndexNpc) continue;
            MascotasIndex[i] = 0;
            MascotasType[i] = 0;
            NroMascotas--;
            return;   // el original corta en la primera (Exit For)
        }
    }

    /// <summary>Whether the player is mounted (equitación). Changes appearance + (client-side) speed.</summary>
    public bool Mounted { get; set; }

    /// <summary>¿Se muestra su nick? (VB6 <c>.showName</c>, Declares.bas:1404, default True al conectar). Lo
    /// conmuta <c>/SHOWNAME</c> y lo baja <c>DoAdminInvisible</c> junto con la invisibilidad. Con esto en false,
    /// el original manda el nick VACÍO en <c>UpdateTagAndStatus</c> y en <c>CharacterCreate</c>, y <c>LookatTile</c>
    /// no emite <c>CharMsgStatus</c> sobre ese personaje: es lo que hace que un GM pueda mirar sin que lo vean.</summary>
    public bool ShowName { get; set; } = true;

    /// <summary>Clan cuyo chat privado está escuchando este GM (VB6 <c>UserList().EscucheClan</c>, que guarda el
    /// guildIndex; acá el nombre, que es la clave del clan en el mundo). Lo pone y lo saca <c>/SHOWCMSG</c> y se
    /// lee al difundir <c>GuildMessage</c>. Uno a la vez, y NO se persiste: el original tampoco lo escribe al
    /// .chr, así que enganchar un clan dura lo que dure la sesión.</summary>
    public string EscucheClan { get; set; } = "";

    // Salida en curso (VB6 <c>Counters.Saliendo</c> / <c>Counters.Salir</c>): el /SALIR no cierra la sesión de
    // una — arranca una cuenta regresiva de IntervaloCerrarConexion segundos que el jugador puede CANCELAR
    // volviendo a mandar /SALIR. Es una mecánica anti-escape: en un mapa Pk no se sale de una pelea cerrando el
    // cliente. Ver Cerrar_Usuario (Modulo_UsUaRiOs.bas:1849).
    public bool Saliendo { get; set; }
    /// <summary>Segundos que faltan para que se cierre la conexión; 0 = sin salida en curso.</summary>
    public int SalirSegundos { get; set; }

    /// <summary>Anti-spam de avisos repetidos (VB6 <c>flags.UltimoMensaje</c>). El original manda ciertos avisos
    /// UNA sola vez y después calla mientras la condición se sostenga: el caso canónico es el 54 ("no podés
    /// moverte porque estás paralizado"), que si no diez teclas de dirección serían diez paquetes. Cada aviso
    /// tiene su propio número (1 = paralizado, y `Trabajo.bas` usa 5/6/8/9/10/22 para los oficios).
    ///
    /// OJO: en el VB6 este flag NUNCA se resetea a 0 — sólo lo PISA el siguiente aviso que lo use. O sea que el
    /// aviso se vuelve a mandar recién cuando otro mensaje distinto se adueña del flag. Se replica tal cual.</summary>
    public byte UltimoMensaje { get; set; }

    // Auras del equipo (VB6 <c>.Char.Arma_Aura</c>, <c>Body_Aura</c>, <c>Escudo_Aura</c>, <c>Head_Aura</c>,
    // <c>Anillo_Aura</c>): el brillo que rodea al personaje por lo que tiene puesto. Se llenan al equipar un
    // ítem con `Aura` y se limpian al desequiparlo; cada cambio se difunde con `AuraToChar`, cuyo tercer byte
    // es el TIPO de aura (1 arma, 2 cuerpo, 3 escudo, 4 casco, 6 anillo — InvUsuario.bas:958-1178).
    public byte WeaponAura { get; set; }
    public byte BodyAura { get; set; }
    public byte ShieldAura { get; set; }
    public byte HelmetAura { get; set; }
    public byte RingAura { get; set; }

    /// <summary>Partícula pegada al personaje (VB6 <c>.Char.ParticulaFx</c>). 0 = ninguna. La poción de Nhareth
    /// la deja fija en 23 y no se puede encimar otra: el original chequea este campo y abandona si ya hay una.
    /// Byte porque byte es lo que viaja en <c>CharacterCreate</c> (Protocol.bas:14926).</summary>
    public byte ParticulaFx { get; set; }

    /// <summary>Navegando en un barco (VB6 flags.Navegando): sólo se mueve por agua y su cuerpo es la barca.</summary>
    public bool Navigating { get; set; }
    /// <summary>Slot de inventario del barco con el que navega (VB6 Invent.BarcoSlot); 0 = ninguno.</summary>
    public byte BoatSlot { get; set; }

    // Toggle flags: resting (faster HP regen), resurrection-safe, and combat mode.
    public bool Resting { get; set; }
    public bool ResurrectionSafe { get; set; } = true;
    public bool CombatMode { get; set; }

    // [STATS]
    public int MaxHp { get; set; } = 100;
    public int Hp { get; set; } = 100;
    public int MaxMana { get; set; }
    public int Mana { get; set; }
    public int MaxStamina { get; set; } = 100;
    public int Stamina { get; set; } = 100;
    public int MaxHit { get; set; }
    public int MinHit { get; set; }
    public byte MaxHunger { get; set; } = 100;
    public byte Hunger { get; set; } = 100;
    public byte MaxThirst { get; set; } = 100;
    public byte Thirst { get; set; } = 100;
    public int Gold { get; set; }
    public long Bank { get; set; }
    // Donador (VB6 Declares.bas:1153 TDonador): flag de cuenta premium + moneda de canje de la tienda [SHOP].
    // Persiste en el .chr ([DONADOR]) y en el .cnt de la cuenta (el .cnt manda al loguear, TCP.bas:1863).
    public bool Donador { get; set; }
    public long CreditoDonador { get; set; }
    public byte Level { get; set; } = 1;
    public long Elu { get; set; }
    public double Exp { get; set; }
    public short FreeSkillPoints { get; set; }

    // [ATRIBUTOS] — AO order: 1=Fuerza, 2=Agilidad, 3=Inteligencia, 4=Carisma, 5=Constitucion
    public byte[] Attributes { get; } = new byte[6]; // 1-based (base values)
    // Temporary attribute effects (agility/strength potions): a bonus that applies until its expiry tick.
    public int[] AttrBonus { get; } = new int[6];        // 1-based, parallels Attributes
    public long[] AttrBonusExpiry { get; } = new long[6]; // game-loop tick after which the bonus clears
    public byte EffectiveAttr(int i) => (byte)Math.Clamp(Attributes[i] + AttrBonus[i], 0, GameLimits.MaxAttribute);
    public byte Strength => EffectiveAttr(1);
    public byte Agility => EffectiveAttr(2);
    public byte Carisma => EffectiveAttr(4);   // usado por el gate de domar (VB6 DoDomar: Carisma * SkillDomar)

    /// <summary>Applies a temporary attribute boost (from a potion). Bonuses stack up to the cap and
    /// expire together at now+durationTicks.</summary>
    public void ApplyAttrBonus(int attr, int amount, long nowTick, long durationTicks)
    {
        if (attr < 1 || attr >= AttrBonus.Length) return;
        AttrBonus[attr] += amount;
        AttrBonusExpiry[attr] = nowTick + durationTicks;
    }

    /// <summary>Clears temporary attribute effects whose expiry tick has passed. Returns true if any expired.</summary>
    public bool ExpireEffects(long nowTick)
    {
        bool any = false;
        for (int i = 1; i < AttrBonus.Length; i++)
            if (AttrBonus[i] != 0 && AttrBonusExpiry[i] != 0 && nowTick >= AttrBonusExpiry[i])
            { AttrBonus[i] = 0; AttrBonusExpiry[i] = 0; any = true; }
        return any;
    }

    /// <summary>Contadores del game-loop (VB6 <c>Counters.AGUACounter</c>/<c>COMCounter</c>/<c>HPCounter</c>/
    /// <c>STACounter</c>/<c>Veneno</c>). El original lleva UNO POR USUARIO y lo reinicia a 0 al disparar el
    /// efecto, en vez de mirar el reloj global: así el que recién entra no hereda el ciclo de otro, y el que
    /// tiene la vida llena no acumula curaciones. Se suma 1 por vuelta, que es el <c>DeltaTick</c> del original
    /// (mMainLoop.bas:136). Ver fase5/unidades-del-loop.md.</summary>
    public int ThirstCounter, HungerCounter, HpCounter, StaCounter, PoisonCounter, ColdCounter;
    /// <summary>INCINERADO (VB6 <c>flags.Incinerado</c> + <c>Counters.Incinerado</c>). NO es veneno, aunque el
    /// port lo trataba como tal: es un daño por tiempo BRUTAL —45 a 65 por golpe de fuego, contra el 1 a 5 del
    /// veneno— y ADEMÁS se apaga solo a las 50 vueltas del contador. Metido en el veneno quedaba permanente y
    /// pegando de menos. Lo apagan también la Sanación, el sacerdote y la muerte.</summary>
    public bool Incinerado { get; set; }
    public int IncineradoCounter;
    /// <summary>Golpes de fuego ya recibidos (VB6 <c>Counters.Incinerado</c>): a los 50 el fuego se apaga.</summary>
    public int IncineradoGolpes;

    /// <summary>
    /// PORTAL DE LUM, el hechizo 53 "Portal planar" (VB6 <c>flags.CasteandoPortal</c>,
    /// <c>Counters.CreoTeleport</c>, <c>Counters.TimeTeleport</c> y <c>flags.DondeTiro*</c>).
    ///
    /// El portal no aparece cuando se lo castea: el original arranca un reloj por SEGUNDO
    /// (<c>PasarSegundotelep</c>, General.bas:1855) y recién a los <b>5 s</b> revalida el tile y planta el
    /// objeto; a los <b>15 s</b> lo levanta. O sea que se tarda 5 segundos en abrirlo y dura 10.
    ///
    /// <c>CasteandoPortal</c> es "el conjuro está en curso pero el portal todavía no existe", y es lo que
    /// distingue las dos ramas de <c>ControlarPortalLum</c>: cancelar mientras se castea AVISA (LocaleMsg
    /// 356), levantar un portal ya abierto no dice nada.
    /// </summary>
    public bool CasteandoPortal { get; set; }
    public bool CreoTeleport { get; set; }
    /// <summary>Segundos transcurridos desde el casteo. También es el cooldown: mientras no sea 0 no se
    /// puede volver a tirar, y el aviso 457 informa los <c>15 - TimeTeleport</c> que faltan.</summary>
    public int TimeTeleport { get; set; }
    /// <summary>Vueltas del loop acumuladas para el paso de un segundo (25 ticks de 40 ms).</summary>
    public int PortalTickCounter;
    /// <summary>El tile donde se tiró el portal (VB6 <c>DondeTiroMap/X/Y</c>).</summary>
    public WorldPos PortalPos { get; set; }

    // Spell states (Fase 2): paralysis blocks movement; poison deals periodic damage. Both expire by tick.
    public long ParalyzedUntilTick { get; set; }
    /// <summary>Quién dejó paralizado a este jugador (VB6 <c>flags.ParalizedByIndex</c> / <c>ParalizedByNpcIndex</c>,
    /// Declares.bas:1275). No es decoración: <c>EfectoParalisisUser</c> lo usa para SOLTAR al paralizado si su
    /// captor se muere o se aleja. 0 = nadie (o lo paralizó algo que no se sigue, como un GM).</summary>
    public short ParalyzedByCharIndex { get; set; }
    public long PoisonedUntilTick { get; set; }
    public long BlindedUntilTick { get; set; }
    // Invisibilidad por hechizo (VB6 Case uEstado, Invisibilidad=1): expira por tick. El bool Invisible sigue
    // siendo la fuente de verdad para el filtrado de la IA/observadores; este tick es sólo el vencimiento del
    // hechizo (el /INVISIBLE de GM lo deja en 0 = permanente).
    public long InvisibleUntilTick { get; set; }
    // Ocultarse (VB6 flags.Oculto + Counters.TiempoOculto, Trabajo.bas): estado SEPARADO de Invisible. El VB6
    // los mantiene distintos porque Ocultar se ROMPE al actuar (caminar/atacar/castear) y se revela con
    // LocaleMsg 307, mientras que la invisibilidad de hechizo/GM no. El aggro de NPCs filtra por ambos.
    public bool Oculto { get; set; }
    public long OcultoUntilTick { get; set; }
    /// <summary>Tick hasta el que este jugador NO puede usar una salida de mapa (VB6 <c>Counters.TiempoDeMapeo</c>).
    ///
    /// <para>Es el anti-escape del PvP, y es hermano del <see cref="SalirSegundos"/>: aquél impide escaparse
    /// cerrando el cliente, éste impide escaparse pisando la baldosa de salida. <c>UsuarioAtacadoPorUsuario</c>
    /// (SistemaCombate.bas:1470) se lo pone en 7 <b>a los dos</b> —al que pega y al que recibe, así que tampoco
    /// se puede tirar un golpe y salir corriendo— y el movimiento lo consulta al pisar un <c>TileExit</c>
    /// (Modulo_UsUaRiOs.bas:731): rebota con <c>LocaleMsg</c> 488 y un <c>PosUpdate</c>.</para>
    ///
    /// <para>El original cuenta 7 y resta 1 por segundo desde <c>PasarSegundo</c> (el timer <c>Auditoria</c> de
    /// frmMain, Interval = 1000). Acá vence solo contra el tick del loop, que es lo mismo sin gastar una pasada
    /// por todos los conectados.</para></summary>
    public long MapeoBloqueadoHastaTick { get; set; }

    // Anti-cheat de cadencia (VB6 IntervaloParaCaminar / IntervaloParaGolpe): último desplazamiento/golpe en ms
    // de reloj monótono (Environment.TickCount64). El dispatcher rechaza el que llega antes del intervalo.
    public long LastWalkMs { get; set; }
    public long LastAttackMs { get; set; }
    // Los CUATRO contadores del original que faltaban (VB6 `Counters`, modNuevoTimer.bas). Junto con
    // LastAttackMs (= TimerPuedeAtacar) forman el juego completo de relojes de combate:
    //   LastCastMs    = TimerLanzarSpell — último casteo
    //   MagiaGolpeMs  = TimerMagiaGolpe  — marca que la ventana "pegar después de castear" ya se usó
    //   GolpeMagiaMs  = TimerGolpeMagia  — ídem para "castear después de pegar"
    //   GolpeUsarMs   = TimerGolpeUsar   — última vez que se habilitó usar un objeto tras golpear
    // Los dos del medio NO son cadencias: son marcas de consumo. Ver Game/CombatTiming.cs.
    public long LastCastMs { get; set; }
    public long MagiaGolpeMs { get; set; }
    public long GolpeMagiaMs { get; set; }
    public long GolpeUsarMs { get; set; }
    public long LastWorkMs { get; set; }   // VB6 IntervaloPermiteTrabajar: cadencia por-usuario de las acciones de trabajo (robar)

    /// <summary>La RANURA del mineral que se eligió para fundir (VB6 <c>flags.Lingoteando</c>,
    /// InvUsuario.bas:1836). Se prende al usar (U) un mineral crudo, que es cuando el original pide el objetivo
    /// con <c>WorkRequestTarget(FundirMetal)</c>, y la consume el clic sobre la fragua.
    ///
    /// <para>Guardar la RANURA y no el objeto es lo que hace el original, y no es un detalle: el jugador puede
    /// llevar hierro y oro a la vez, y lo que se funde es el que eligió — antes acá se fundía el primer mineral
    /// del inventario, que con dos tipos encima elegía por él.</para></summary>
    public byte Lingoteando { get; set; }

    /// <summary>TRABAJO CONTINUO (VB6 <c>flags.Trabajando</c>). Prendido, el game-loop ejecuta el oficio del
    /// jugador cada <c>IntervaloTrabajo</c> (700 ms) hasta que algo lo apaga: quedarse sin energía, o cualquier
    /// nueva acción sobre un tile. Así se pesca/mina/tala en el original: un doble clic arranca la faena y el
    /// personaje sigue solo, en vez de un clic por unidad de recurso.
    ///
    /// OJO que NO es <see cref="Trabajando"/>, que pese al nombre es otra cosa: el contador de faenas que lista
    /// el <c>/TRABAJANDO</c> del centinela. Son dos campos distintos del original con nombres parecidos
    /// (<c>flags.Trabajando</c> vs <c>Counters.Trabajando</c>).</summary>
    public bool Laborando { get; set; }
    public bool ClientParalyzed { get; set; } // what the client currently believes (ParalizeOK is a toggle)
    public bool ClientBlind { get; set; }     // Blind/BlindNoMore are distinct packets, tracked so expiry clears it
    public bool IsBlinded(long nowTick) => BlindedUntilTick > nowTick;
    public bool IsParalyzed(long nowTick) => ParalyzedUntilTick > nowTick;
    public bool IsPoisoned(long nowTick) => PoisonedUntilTick > nowTick;

    // [Inventory] (1-based, slot 0 unused), [HECHIZOS], [SKILLS] (eSkill 1..27), [BancoInventory]
    public InventorySlot[] Inventory { get; } = new InventorySlot[GameLimits.MaxInventorySlots + 1];
    public short[] Spells { get; } = new short[GameLimits.MaxUserSpells + 1];
    public byte[] Skills { get; } = new byte[GameLimits.NumSkills + 1];
    public InventorySlot[] BankInventory { get; } = new InventorySlot[GameLimits.MaxBankSlots + 1];

    // --- Target del click izquierdo (VB6 flags.Target*, seteados por LookatTile, GameLogic.bas:655) ---
    // El original guarda QUÉ clickeó el usuario por última vez y varios comandos GM operan sobre eso en vez de
    // pedir un nick por parámetro: /SEGUIR, /RESETINV y /TALKAS actúan sobre TargetNPC, y /DT sobre TargetMap,X,Y.
    // Es estado TRANSIENT (no se persiste al .chr) y se pisa entero en cada click. TargetUser/TargetNpc guardan
    // CharIndex (en el VB6 son índices de UserList/Npclist); si el objetivo se va del mundo, la búsqueda por
    // CharIndex devuelve null y el comando rebota — no hace falta limpiarlos al desconectar.

    /// <summary>CharIndex del jugador clickeado (0 = ninguno).</summary>
    public short TargetUser { get; set; }
    /// <summary>CharIndex del NPC clickeado (0 = ninguno).</summary>
    public short TargetNpc { get; set; }
    /// <summary>eNPCType del NPC clickeado (VB6 flags.TargetNpcTipo; 0 = Comun, el valor al que vuelve al limpiar).</summary>
    public byte TargetNpcTipo { get; set; }
    /// <summary>ObjIndex del objeto del tile clickeado (0 = ninguno).</summary>
    public short TargetObj { get; set; }
    public short TargetObjMap { get; set; }
    public byte TargetObjX { get; set; }
    public byte TargetObjY { get; set; }
    /// <summary>El TILE clickeado en sí (VB6 flags.TargetMap/TargetX/TargetY). A diferencia de TargetObj*, se
    /// setea SIEMPRE que el click caiga en un tile válido, haya algo ahí o no: es lo que usa /DT.</summary>
    public short TargetMap { get; set; }
    public byte TargetX { get; set; }
    public byte TargetY { get; set; }

    /// <summary>La RANURA de hechizo elegida y todavía sin lanzar (VB6 <c>flags.Hechizo</c>). El casteo del
    /// original son DOS paquetes: <c>CastSpell</c> elige y deja esto puesto, y el <c>WorkLeftClick(magia)</c> del
    /// clic siguiente lo lanza y lo vuelve a 0 (Protocol.bas:2174 y :2948-2950). Transient, no se persiste.</summary>
    public byte HechizoElegido { get; set; }

    /// <summary>¿Los NPCs pueden perseguirlo y pegarle? (VB6 <c>flags.AdminPerseguible</c>). El original lo pone
    /// en <b>True sólo para el usuario común</b> (TCP.bas:1925, la rama <c>Else</c> del reparto de privilegios):
    /// un GM de cualquier rango queda en False y las tres ramas de la IA lo saltean (AI_NPC.bas:88/160/229), así
    /// que ninguna criatura lo ataca ni lo sigue. Acá es DERIVADO de Privileges en vez de un campo: el original
    /// lo arranca en False (TCP.bas:1465) y lo prende en esa única rama, así que el valor siempre coincide con
    /// "no es GM" y un campo aparte sólo podría desincronizarse.</summary>
    public bool AdminPerseguible => !IsGm;

    /// <summary>CharIndex of the merchant this player is currently trading with (0 = none). Transient.</summary>
    public short TradingWith { get; set; }
    /// <summary>Whether the bank window is currently open. Transient.</summary>
    public bool BankOpen { get; set; }

    /// <summary>El <c>flags.Comerciando</c> del original, que es UNO SOLO para la tienda y para la bóveda:
    /// <c>IniciarComercioNPC</c> (Comercio.bas:202) e <c>IniciarDeposito</c> (modBanco.bas:45) prenden el mismo
    /// campo, y <c>HandleCommerceEnd</c> (Protocol.bas:1951) y <c>HandleBankEnd</c> (:1977) lo apagan. Acá el
    /// estado está partido en dos —con qué mercader y si la bóveda está abierta—, así que la condición se
    /// deriva: con una ventana abierta, pedir la otra tiene que rebotar con el 387 igual que en el original.</summary>
    public bool Comerciando => TradingWith != 0 || BankOpen;
    /// <summary>Whether the player is meditating (faster mana regen). Transient.</summary>
    public bool Meditating { get; set; }

    // Contadores del CENTINELA (VB6 Counters.Trabajando / Counters.Ocultando, Declares.bas:1207). No son flags
    // sino CRÉDITOS que se gastan: cada acción los sube de a 1 —fabricar en Trabajo.bas:427, ocultarse en :103—
    // y bajan de a 1 en el tick del usuario (Modulo_UsUaRiOs.bas:802/804) y al pegar (SistemaCombate.bas:1052).
    // O sea que valen > 0 sólo un ratito después de la acción: es lo que hace que /TRABAJANDO y /OCULTANDO
    // listen a quien está trabajando AHORA y no a quien trabajó alguna vez.
    public int Trabajando { get; set; }
    public int Ocultando { get; set; }

    public bool IsAlive => Hp > 0;
}
