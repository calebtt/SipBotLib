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
    public void Normalize_AcceptsCommonForms(string input, string server, string expected)
    {
        Assert.Equal(expected, SipUriNormalizer.Normalize(input, server));
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
