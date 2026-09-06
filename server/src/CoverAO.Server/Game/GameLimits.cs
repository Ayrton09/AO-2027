namespace CoverAO.Server.Game;

/// <summary>Fixed game limits from the original server (Declares.bas). Must match the client's expectations.</summary>
public static class GameLimits
{
    public const byte MaxInventorySlots = 25; // Declares.bas:572
    public const byte MaxBankSlots = 40;      // Declares.bas:1085 MAX_BANCOINVENTORY_SLOTS
    public const byte MaxCorreos = 10;        // Declares.bas:1281 Max_Correos — tope de la casilla de correo
    public const int MaxClanes = 1000;        // modGuilds.bas:12 MAX_GUILDS — el registro de clanes tiene fondo
    public const int MaxAspirantes = 10;      // modGuilds.bas:24 MAXASPIRANTES — solicitudes pendientes por clan
    public const byte MaxUserSpells = 35;     // Declares.bas:308
    public const byte NumSkills = 27;         // Declares.bas NUMSKILLS
    public const int MaxMascotas = 3;         // Declares.bas:444 MAXMASCOTAS — cupo COMPARTIDO entre domar e invocar
    public const int MaxMascotasMismoTipo = 2; // VB6 PuedeDomarMascota (Trabajo.bas:1162): máx 2 mascotas del mismo NPC
    public const int MaxInventoryObjs = 10000; // MAX_INVENTORY_OBJS (tope por ranura al apilar, InvUsuario.bas)
    // MAXATRIBUTOS (Declares.bas:349): el techo que ponen las pociones verde/amarilla y los hechizos de fuerza y
    // agilidad. Acá decía 40 y el original dice 35 — y el cliente del original pinta el atributo en naranja
    // justo a partir de 35, así que con 40 el port dejaba subir cinco puntos más de lo que el juego enseña.
    public const int MaxAttribute = 35;

    // Índices de Player.Attributes (VB6 eAtributos): el orden en que viajan en el paquete `atributes`.
    public const int AttrFuerza = 1;
    public const int AttrAgilidad = 2;
    public const int AttrInteligencia = 3;
    public const int AttrCarisma = 4;
    public const int AttrConstitucion = 5;
    public const int NewbieMaxLevel = 15;     // Declares.bas:192 LimiteNewbie (EsNewbie := ELV <= 15)

    // Borde caminable del mapa (VB6 MoveToLegalPos, GameLogic.bas:510): el jugador no puede pisar el "anillo
    // muerto" del borde por el tamaño de la ventana de cámara. Con mapa 1..100, XWindow=17, YWindow=13
    // (Declares.bas:519-530): MinXBorder=1+(17\2)=9, MaxXBorder=100-8=92, MinYBorder=1+(13\2)=7, MaxYBorder=100-6=94.
    public const int MinXBorder = 9, MaxXBorder = 92, MinYBorder = 7, MaxYBorder = 94;

    // eSkill indices used by combat/crafting (Declares.bas eSkill enum)
    // Las clases, por su numero de `eClass` (Declares.bas:117). Estaban sueltas en Persistence como privadas;
    // el subsistema de familiares necesita las tres que llevan uno, asi que viven aca.
    public const byte ClaseClerigo = 1, ClaseMago = 2, ClaseGuerrero = 3, ClaseAsesino = 4, ClaseLadron = 5,
                      ClaseBardo = 6, ClaseDruida = 7, ClaseGladiador = 8, ClasePaladin = 9, ClaseCazador = 10,
                      ClaseNigromante = 18;

    /// <summary>El nombre de cada clase, EN EL ORDEN de <c>eClass</c> (Declares.bas:117) y no en el del AO
    /// clásico: Clérigo es 1 y Mago es 2. Vive acá, junto a los números que nombra, porque hay dos lugares
    /// que la muestran —el panel de administración y la página pública— y una segunda copia es una copia que
    /// un día va a divergir.</summary>
    public static readonly string[] NombresDeClase = {
        "Clérigo", "Mago", "Guerrero", "Asesino", "Ladrón", "Bardo", "Druida", "Gladiador",
        "Paladín", "Cazador", "Pescador", "Herrero", "Leñador", "Minero", "Carpintero",
        "Sastre", "Mercenario", "Nigromante",
    };

    /// <summary>El nombre de una clase por su número, o <c>"Clase N"</c> si el número no es de ninguna.</summary>
    public static string NombreDeClase(int clase) =>
        clase >= 1 && clase <= NombresDeClase.Length ? NombresDeClase[clase - 1] : $"Clase {clase}";

    public const int SkillTacticas = 1;
    public const int SkillMagia = 8;    // Declares.bas eSkill.magia
    public const int SkillResistencia = 9;
    public const int SkillMeditar = 10; // eSkill.Meditar
    public const int SkillComerciar = 15;
    public const int SkillWrestling = 3;
    public const int SkillArmas = 2;               // eSkill.armas — con arma equipada el acierto sale de ACÁ
    public const int SkillProyectiles = 6;         // eSkill.Proyectiles (arcos: sin modelar `proyectil` en ObjInfo)
    public const int SkillArmasArrojadizas = 5;    // eSkill.ArmasArrojadizas (idem)
    public const int SkillApunalar = 4;            // eSkill.Apuñalar (Declares.bas:470) — "Puñales" en el HUD
    public const int MinApunalar = 10;             // MIN_APUÑALAR (Declares.bas:401): el piso para apuñalar
                                                   // sin ser Asesino. Un asesino apuñala con la skill en 0.
    public const int SkillDefensa = 7;  // shield block/evasion (PoderEvasionEscudo)
    public const int SkillHerreria = 22;
    public const int SkillCarpinteria = 23;
    public const int SkillSastreria = 25;
    public const int SkillAlquimia = 24;
    public const int SkillDomar = 12; // taming
    public const int SkillSupervivencia = 16; // Declares.bas eSkill.Supervivencia (entra en el regen de stamina)
    public const int SkillNavegacion = 26;    // Declares.bas eSkill.Navegacion (requerida por los barcos)
    /// <summary>eSkill.Equitacion (Declares.bas:490), la que piden las MONTURAS. Es la hermana de
    /// <see cref="SkillNavegacion"/>: las 11 monturas de <c>obj.dat</c> traen su <c>MinSkill</c> (25 la común,
    /// 50 las reforzadas, 65 la de nigromante) igual que los tres barcos, y el original las gatea con la misma
    /// función. Faltaba, y por eso montarse no pedía nada.</summary>
    public const int SkillEquitacion = 27;
    // Gathering skills (eSkill): woodcutting, fishing, mining.
    public const int SkillTalar = 20;
    /// <summary>eSkill.botanica (Declares.bas:488): juntar raíces de los árboles con las TIJERAS (obj 885).
    /// Es el cuarto oficio de recolección, hermano de talar/pescar/minar.</summary>
    public const int SkillBotanica = 21;
    public const int SkillPesca = 18;
    public const int SkillMineria = 19;
    public const int SkillOcultar = 11; // eSkill.Ocultarse (Declares.bas): esconderse en el terreno
    public const int SkillRobar = 14;   // eSkill.robar (Declares.bas): hurtar oro/ítems a otro jugador
    public const int SkillFundir = 88;  // FundirMetal (Declares.bas): NO es un eSkill, es la constante de trabajo de fundición

    // Comercio con mercader: qué acepta comprarte (VB6 Comercio.bas:123).
    public const int TipoItemsCualquiera = 1000; // eOBJType.otCualquiera (Declares.bas:498): "compro de todo"
    public const int ObjOro = 12;                // iORO (Declares.bas:450): el oro no se le vende a nadie

    // Crafting material object indices (obj.dat).
    public const int IngotIronObj = 386;   // Lingote de Hierro
    public const int IngotSilverObj = 387; // Lingote de Plata
    public const int IngotGoldObj = 388;   // Lingote de Oro
    public const int OreIronObj = 192;     // Mineral crudo de Hierro (otMinerales) → se funde a IngotIronObj
    public const int OreSilverObj = 193;   // Mineral crudo de Plata → IngotSilverObj
    public const int OreGoldObj = 194;     // Mineral crudo de Oro → IngotGoldObj
    public const int WoodObj = 58;         // Leña (carpentry)
    public const int SkinWolfObj = 414;    // Piel de Lobo (tailoring)
    public const int SkinBrownBearObj = 415; // Piel de Oso Pardo
    public const int SkinPolarBearObj = 416; // Piel de Oso Polar
    public const int RootObj = 888;        // Raíz (alchemy)

    /// <summary>El objeto que planta /CT como teleport (VB6 HandleTeleportCreate: <c>ET.ObjIndex = 378</c>,
    /// hardcodeado). Es un otTeleport (ObjType 19); /DT borra el par por este índice.</summary>
    public const short ObjTeleport = 378;

    /// <summary><c>TipoAI.NPCDEFENSA</c> (AI_NPC.bas:37): el modo de movimiento "seguir al agresor". Lo pone
    /// DoFollow (/SEGUIR) y es a lo que responde SeguirAgresor.</summary>
    public const int TipoAiNpcDefensa = 4;

    /// <summary><c>TipoAI.SigueAmo</c> (AI_NPC.bas:39): el modo de una mascota que acompaña a su dueño. Es a lo
    /// que la devuelve <c>FollowAmo</c> cuando el freno de facción le corta la persecución.</summary>
    public const int TipoAiSigueAmo = 8;

    /// <summary><c>TipoAI.NpcAtacaNpc</c> (AI_NPC.bas:40): la mascota va a pelear con el NPC que su amo
    /// atacó. Se lo pone <c>CheckPets</c> junto con el TargetNPC.</summary>
    public const int TipoAiNpcAtacaNpc = 9;
}
