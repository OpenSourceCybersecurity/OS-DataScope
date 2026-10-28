using DataScope.Core.Privacy;

namespace DataScope.Core.Tests;

/// <summary>What a person search (D160) accepts: narrow, plain terms only. Every name and address is made up.</summary>
public sealed class PersonSearchTests
{
    [Fact]
    public void Names_are_tidied_and_duplicates_dropped()
    {
        var names = PersonSearch.Names(["  Jo   Bloggs ", "jo bloggs", null, "", "Ana María"], out var problem);
        Assert.Null(problem);
        Assert.Equal(["Jo Bloggs", "Ana María"], names);
    }

    [Theory]
    [InlineData("Al")]
    [InlineData("12345")]
    [InlineData("Jo\tBloggs\u0007")]
    public void Names_too_short_without_letters_or_with_control_characters_are_refused(string name)
    {
        Assert.Null(PersonSearch.Names([name], out var problem));
        Assert.NotNull(problem);
    }

    [Fact]
    public void At_most_five_names_and_five_addresses()
    {
        Assert.Null(PersonSearch.Names(["Ann One", "Ben Two", "Cat Three", "Dan Four", "Eve Five", "Fay Six"], out var names));
        Assert.NotNull(names);
        Assert.Null(PersonSearch.Addresses(["a@x.test", "b@x.test", "c@x.test", "d@x.test", "e@x.test", "f@x.test"], out var addresses));
        Assert.NotNull(addresses);
    }

    [Fact]
    public void Addresses_are_lower_cased_and_duplicates_dropped()
    {
        Assert.Equal(["jo.bloggs@contoso.test"], PersonSearch.Addresses([" Jo.Bloggs@Contoso.test", "jo.bloggs@contoso.test"], out var problem));
        Assert.Null(problem);
    }

    [Theory]
    [InlineData("jo")]
    [InlineData("jo@localhost")]
    [InlineData("Jo <jo@contoso.test>")]
    [InlineData("jo@contoso.test; ann@contoso.test")]
    [InlineData("%@contoso.test")]
    [InlineData("jo@@contoso.test")]
    [InlineData("jo bloggs@contoso.test")]
    public void Anything_but_a_plain_address_is_refused(string address)
    {
        Assert.False(PersonSearch.IsAddress(address));
        Assert.Null(PersonSearch.Addresses([address], out var problem));
        Assert.NotNull(problem);
    }

    [Fact]
    public void A_reason_is_required_and_plain()
    {
        Assert.NotNull(PersonSearch.ReasonProblem(null));
        Assert.NotNull(PersonSearch.ReasonProblem("  "));
        Assert.NotNull(PersonSearch.ReasonProblem(new string('x', 501)));
        Assert.Null(PersonSearch.ReasonProblem("SAR-2026-014"));
    }

    [Fact]
    public void An_account_adds_its_display_name_and_a_distinctive_logon_name()
    {
        Assert.Equal(["Jo Bloggs", "jbloggs"], PersonSearch.AccountNames("jbloggs", "Jo Bloggs"));
        Assert.Equal(["Jo Bloggs"], PersonSearch.AccountNames("jo", "Jo Bloggs"));
        Assert.Equal(["jbloggs"], PersonSearch.AccountNames("jbloggs", null));
        Assert.Equal(["Jo Bloggs"], PersonSearch.AccountNames("jo bloggs", "Jo Bloggs"));
    }

    [Fact]
    public void Contains_escapes_LIKE()
    {
        Assert.Equal(@"%100\%\\x%", PersonSearch.Contains(@"100%\x"));
    }
}
