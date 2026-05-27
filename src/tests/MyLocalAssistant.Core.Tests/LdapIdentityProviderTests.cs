using MyLocalAssistant.Server.Auth;
using Xunit;

namespace MyLocalAssistant.Core.Tests;

public class LdapIdentityProviderTests
{
    [Theory]
    [InlineData("alice", "alice")]
    [InlineData("a*b", @"a\2ab")]
    [InlineData("a(b)c", @"a\28b\29c")]
    [InlineData(@"a\b", @"a\5cb")]
    [InlineData("a\0b", @"a\00b")]
    public void EscapeLdapFilter_escapes_metacharacters(string input, string expected)
    {
        Assert.Equal(expected, LdapIdentityProvider.EscapeLdapFilter(input));
    }

    [Fact]
    public void MatchesGroup_matches_full_dn_case_insensitive()
    {
        var groups = new[] { "CN=Engineers,OU=Groups,DC=corp,DC=local" };
        Assert.True(LdapIdentityProvider.MatchesGroup(groups, "cn=engineers,ou=groups,dc=corp,dc=local"));
    }

    [Fact]
    public void MatchesGroup_matches_cn_only()
    {
        var groups = new[] { "CN=Engineers,OU=Groups,DC=corp,DC=local" };
        Assert.True(LdapIdentityProvider.MatchesGroup(groups, "Engineers"));
        Assert.True(LdapIdentityProvider.MatchesGroup(groups, "engineers"));
    }

    [Fact]
    public void MatchesGroup_returns_false_when_not_member()
    {
        var groups = new[] { "CN=Engineers,DC=corp,DC=local" };
        Assert.False(LdapIdentityProvider.MatchesGroup(groups, "Finance"));
        Assert.False(LdapIdentityProvider.MatchesGroup(Array.Empty<string>(), "Engineers"));
        Assert.False(LdapIdentityProvider.MatchesGroup(groups, ""));
    }
}
