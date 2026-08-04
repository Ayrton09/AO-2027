namespace CoverAO.Server.Game;

/// <summary>
/// One entry in the account's character-selection list (the AddPJ packet). Fields mirror the VB6
/// <c>AccountUser</c> UDT (Cuentas.bas:13) in the exact order WriteAddPj serializes them.
/// </summary>
public sealed record CharacterSummary(
    string Name,
    short Head,
    short Body,
    short Helmet,
    short Weapon,
    short Shield,
    byte Level,
    byte Class,
    short Map,
    byte Color,
    bool GameMaster);
