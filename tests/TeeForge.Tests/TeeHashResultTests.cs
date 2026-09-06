using System.Text;

namespace TeeForge.Tests;

public class TeeHashResultTests
{
    [Theory]
    [InlineData("", "", "")]
    [InlineData("f", "MY======", "Zg")]
    [InlineData("fo", "MZXQ====", "Zm8")]
    [InlineData("foo", "MZXW6===", "Zm9v")]
    [InlineData("foob", "MZXW6YQ=", "Zm9vYg")]
    [InlineData("fooba", "MZXW6YTB", "Zm9vYmE")]
    [InlineData("foobar", "MZXW6YTBOI======", "Zm9vYmFy")]
    public void Encodings_match_RFC_4648_vectors(string input, string base32, string base64Url)
    {
        var result = new TeeHashResult(TeeHashAlgorithm.SHA256, Encoding.ASCII.GetBytes(input));

        Assert.Equal(base32, result.Base32);
        Assert.Equal(base64Url, result.Base64Url);
    }

    [Theory]
    [InlineData("FF", "74======", "_w")]
    [InlineData("FFFF", "777Q====", "__8")]
    [InlineData("FFFFFF", "77776===", "____")]
    [InlineData("FFFFFFFF", "777777Y=", "_____w")]
    [InlineData("FFFFFFFFFF", "77777777", "______8")]
    [InlineData("FBFF", "7P7Q====", "-_8")]
    public void Encodings_handle_binary_bytes_and_URL_safe_alphabet(string hex, string base32, string base64Url)
    {
        var result = new TeeHashResult(TeeHashAlgorithm.XxHash3, Convert.FromHexString(hex));

        Assert.Equal(base32, result.Base32);
        Assert.Equal(base64Url, result.Base64Url);
    }
}
