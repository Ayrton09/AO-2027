using CoverAO.Server.Data;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>Locks in the password hardening: new accounts use PBKDF2, and pre-existing SHA-256 .cnt
/// hashes verify once then upgrade to PBKDF2 in place (rehash-on-login).</summary>
public class PasswordHasherTests
{
    [Fact]
    public void Modern_hash_round_trips_and_rejects_wrong_password()
    {
        var hash = PasswordHasher.HashModern("s3cret");
        Assert.True(PasswordHasher.IsModern(hash));
        Assert.StartsWith("pbkdf2$", hash);
        Assert.True(PasswordHasher.VerifyModern("s3cret", hash));
        Assert.False(PasswordHasher.VerifyModern("wrong", hash));
        Assert.False(PasswordHasher.VerifyModern("s3cret", "not-a-valid-hash"));
    }

    [Fact]
    public void Modern_hash_is_salted_unique_per_call()
        => Assert.NotEqual(PasswordHasher.HashModern("same"), PasswordHasher.HashModern("same"));

    [Fact]
    public void IsModern_distinguishes_legacy_sha256()
    {
        var salt = PasswordHasher.NewSalt();
        Assert.False(PasswordHasher.IsModern(PasswordHasher.Hash("p", salt)));   // 64-char hex, not pbkdf2$
        Assert.False(PasswordHasher.IsModern(null));
    }

    [Fact]
    public void Legacy_hash_verifies_and_upgrades_on_login()
    {
        // An account carrying a legacy SHA-256(password+salt) hash, as a pre-existing .cnt would.
        var salt = PasswordHasher.NewSalt();
        var acc = new Account { Name = "old", Salt = salt, PasswordHash = PasswordHasher.Hash("pw", salt) };
        Assert.False(PasswordHasher.IsModern(acc.PasswordHash));

        // First login: verifies via the legacy path AND rehashes to PBKDF2 in place.
        Assert.True(acc.VerifyPassword("pw", out var upgraded));
        Assert.True(upgraded);
        Assert.True(PasswordHasher.IsModern(acc.PasswordHash));  // now modern
        Assert.Equal("", acc.Salt);                              // salt folded into the hash string

        // Subsequent logins verify via the modern path (no further upgrade).
        Assert.True(acc.VerifyPassword("pw", out var upgraded2));
        Assert.False(upgraded2);
        Assert.False(acc.VerifyPassword("wrong", out _));
    }

    [Fact]
    public void Wrong_legacy_password_does_not_upgrade()
    {
        var salt = PasswordHasher.NewSalt();
        var acc = new Account { Name = "old", Salt = salt, PasswordHash = PasswordHasher.Hash("pw", salt) };
        Assert.False(acc.VerifyPassword("nope", out var upgraded));
        Assert.False(upgraded);
        Assert.False(PasswordHasher.IsModern(acc.PasswordHash));  // still legacy, untouched
    }
}
