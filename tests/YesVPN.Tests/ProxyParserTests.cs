using Xunit;
using YesVPN.Core.Parsing;

namespace YesVPN.Tests;

public sealed class ProxyParserTests
{
    [Fact]
    public void Parse_SupportsAllFormatsAndIgnoresCommentsAndBlanks()
    {
        const string input = """
            # Comment line
               
            socks5://alice:s3cr3t@proxy1.example.com:1080#US-East
            198.51.100.10:1080
            198.51.100.20:8080:bob:p@ss:with:colons#1
            """;

        var result = ProxyParser.Parse(input);

        Assert.False(result.HasErrors);
        Assert.Equal(3, result.Entries.Count);

        Assert.Equal("proxy1.example.com", result.Entries[0].Host);
        Assert.Equal(1080, result.Entries[0].Port);
        Assert.Equal("alice", result.Entries[0].Username);
        Assert.Equal("s3cr3t", result.Entries[0].Password);
        Assert.Equal("US-East", result.Entries[0].Label);

        Assert.Equal("198.51.100.10", result.Entries[1].Host);
        Assert.Equal(1080, result.Entries[1].Port);
        Assert.Null(result.Entries[1].Username);
        Assert.Null(result.Entries[1].Password);

        Assert.Equal("198.51.100.20", result.Entries[2].Host);
        Assert.Equal(8080, result.Entries[2].Port);
        Assert.Equal("bob", result.Entries[2].Username);
        Assert.Equal("p@ss:with:colons#1", result.Entries[2].Password);
    }

    [Fact]
    public void Parse_SupportsIpv6HostsInAllFormats()
    {
        const string input = """
            [2001:db8::1]:1080
            [2001:db8::2]:9050:user1:pass!@#
            socks5://u2:p%40ss@[::1]:1081#LocalV6
            """;

        var result = ProxyParser.Parse(input);

        Assert.False(result.HasErrors);
        Assert.Equal(3, result.Entries.Count);
        Assert.Equal("2001:db8::1", result.Entries[0].Host);
        Assert.Equal(1080, result.Entries[0].Port);

        Assert.Equal("2001:db8::2", result.Entries[1].Host);
        Assert.Equal("user1", result.Entries[1].Username);
        Assert.Equal("pass!@#", result.Entries[1].Password);

        Assert.Equal("::1", result.Entries[2].Host);
        Assert.Equal("u2", result.Entries[2].Username);
        Assert.Equal("p@ss", result.Entries[2].Password);
        Assert.Equal("LocalV6", result.Entries[2].Label);
    }

    [Fact]
    public void Parse_ReportsClearMaskedErrorsForMalformedLines()
    {
        const string input = """
            http://user:supersecret@bad.example.com:8080
            notaport:abc
            198.51.100.5:99999
            198.51.100.5:1080:onlyuser
            198.51.100.5:1080:validuser:validpass
            """;

        var result = ProxyParser.Parse(input);

        Assert.True(result.HasErrors);
        Assert.Equal(4, result.Errors.Count);
        Assert.Single(result.Entries);

        Assert.DoesNotContain("supersecret", result.Errors[0].MaskedLine);
        Assert.Equal(1, result.Errors[0].LineNumber);
        Assert.Equal(2, result.Errors[1].LineNumber);
        Assert.Equal(3, result.Errors[2].LineNumber);
        Assert.Equal(4, result.Errors[3].LineNumber);
    }
}
