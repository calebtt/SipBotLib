using SipBot;
using Xunit;

namespace SipBotLib.Tests;

public class SipUriNormalizerTests
{
    [Theory]
    [InlineData("sip:102@slowcasting.com", "slowcasting.com", "sip:102@slowcasting.com")]
    [InlineData("102@slowcasting.com", "slowcasting.com", "sip:102@slowcasting.com")]
    [InlineData("102", "slowcasting.com", "sip:102@slowcasting.com")]
    [InlineData(" 102 ", "slowcasting.com", "sip:102@slowcasting.com")]
    [InlineData("102", "sip:slowcasting.com", "sip:102@slowcasting.com")]
    [InlineData("+12025550100", "pbx.example.com", "sip:12025550100@pbx.example.com")]
    [InlineData("tel:+1-202-555-0100", "pbx.example.com", "sip:12025550100@pbx.example.com")]
    [InlineData("tel:1 (202) 555-0100", "pbx.example.com", "sip:12025550100@pbx.example.com")]
    [InlineData("1-202-555-0100", "pbx.example.com", "sip:12025550100@pbx.example.com")]
    [InlineData("12025550100", "pbx.example.com", "sip:12025550100@pbx.example.com")]
    [InlineData("sip:+12025550100@pbx.example.com", "ignored", "sip:12025550100@pbx.example.com")]
    [InlineData("+12025550100@pbx.example.com", "ignored", "sip:12025550100@pbx.example.com")]
    [InlineData("sip:+12025550100@pbx.example.com:5060", "ignored", "sip:12025550100@pbx.example.com:5060")]
    [InlineData("*97", "pbx.example.com", "sip:*97@pbx.example.com")]
    [InlineData("alice", "pbx.example.com", "sip:alice@pbx.example.com")]
    public void Normalize_AcceptsCommonForms(string input, string server, string expected)
    {
        Assert.Equal(expected, SipUriNormalizer.Normalize(input, server));
    }

    [Theory]
    [InlineData("102", false)]
    [InlineData("1001", false)]
    [InlineData("alice", false)]
    [InlineData("*97", false)]
    [InlineData("12025550100", true)]
    [InlineData("+12025550100", true)]
    [InlineData("+44 20 7946 0958", true)]
    public void LooksLikePstn_DistinguishesExtensions(string user, bool expected)
    {
        Assert.Equal(expected, SipUriNormalizer.LooksLikePstn(user));
    }

    [Theory]
    [InlineData(0, '0')]
    [InlineData(5, '5')]
    [InlineData(9, '9')]
    [InlineData(10, '*')]
    [InlineData(11, '#')]
    [InlineData(12, 'A')]
    public void DtmfToneToChar_MapsRfc4733(byte tone, char expected)
    {
        Assert.Equal(expected, SipUriNormalizer.DtmfToneToChar(tone));
    }
}
