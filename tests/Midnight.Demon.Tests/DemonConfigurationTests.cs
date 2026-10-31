using Midnight.Demon.Core.Configuration;

namespace Midnight.Demon.Tests;

public class DemonConfigurationTests
{
    private static DemonConfiguration Valid() => new()
    {
        PanelUrl = "https://panel.example.com",
        TokenId = "abc",
        Token = "secret",
        DataPath = "/var/lib/midnight"
    };

    [Fact]
    public void Validate_AcceptsValidConfiguration()
    {
        Assert.Empty(Valid().Validate());
    }

    [Fact]
    public void Validate_RequiresToken()
    {
        var configuration = Valid();
        configuration.Token = "";

        Assert.Contains(configuration.Validate(), x => x.Contains("Token"));
    }

    [Theory]
    [InlineData("panel.example.com")]
    [InlineData("ftp://panel.example.com")]
    public void Validate_RequiresHttpPanelUrl(string url)
    {
        var configuration = Valid();
        configuration.PanelUrl = url;

        Assert.Contains(configuration.Validate(), x => x.Contains("PanelUrl"));
    }

    [Fact]
    public void Validate_RequiresAbsoluteDataPath()
    {
        var configuration = Valid();
        configuration.DataPath = "data";

        Assert.Contains(configuration.Validate(), x => x.Contains("DataPath"));
    }

    [Fact]
    public void Validate_RequiresCertificateWhenSslIsEnabled()
    {
        var configuration = Valid();
        configuration.Ssl.Enabled = true;

        Assert.Contains(configuration.Validate(), x => x.Contains("Ssl"));
    }

    [Fact]
    public void Load_ReadsCamelCaseJson()
    {
        var path = Path.GetTempFileName();

        try
        {
            File.WriteAllText(path, """{ "panelUrl": "https://panel.test", "tokenId": "id", "token": "t", "httpPort": 9000 }""");

            var configuration = DemonConfiguration.Load(path);

            Assert.Equal("https://panel.test", configuration.PanelUrl);
            Assert.Equal(9000, configuration.HttpPort);
            Assert.Equal(2022, configuration.SftpPort);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
