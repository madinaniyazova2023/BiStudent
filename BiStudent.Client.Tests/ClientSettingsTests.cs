namespace BiStudent.Client.Tests;

[CollectionDefinition("settings", DisableParallelization = true)]
public sealed class SettingsCollection { }

[Collection("settings")]
public sealed class ClientSettingsTests : IDisposable
{
    private readonly string previousPath = ClientSettings.FilePath;
    private readonly string directory = Path.Combine(Path.GetTempPath(), "BiStudent.Tests", Guid.NewGuid().ToString("N"));

    public ClientSettingsTests()
    {
        ClientSettings.FilePath = Path.Combine(directory, "client.json");
    }

    public void Dispose()
    {
        ClientSettings.FilePath = previousPath;
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }

    [Fact]
    public void MissingFileLoadsDefaults()
    {
        var settings = ClientSettings.Load();
        Assert.Equal("http://localhost:5080", settings.ServerUrl);
        Assert.Equal("", settings.LastLogin);
        Assert.False(settings.RememberMe);
    }

    [Fact]
    public void SaveAndLoadPreservePreferences()
    {
        new ClientSettings
        {
            ServerUrl = "https://school.example",
            LastLogin = "teacher",
            RememberMe = true,
            CameraId = "camera-id",
            CameraName = "Камера",
            CameraIndex = 2
        }.Save();
        var loaded = ClientSettings.Load();
        Assert.Equal("https://school.example", loaded.ServerUrl);
        Assert.Equal("teacher", loaded.LastLogin);
        Assert.True(loaded.RememberMe);
        Assert.Equal("camera-id", loaded.CameraId);
        Assert.Equal("Камера", loaded.CameraName);
        Assert.Equal(2, loaded.CameraIndex);
    }

    [Fact]
    public void DamagedJsonLoadsDefaults()
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(ClientSettings.FilePath, "{bad json");
        Assert.Equal("http://localhost:5080", ClientSettings.Load().ServerUrl);
    }

    [Fact]
    public void RefreshTokenIsProtectedAndBoundToServer()
    {
        var settings = new ClientSettings { ServerUrl = "https://school.example" };
        settings.StoreRefreshToken("abc");
        settings.Save();
        Assert.DoesNotContain("abc", File.ReadAllText(ClientSettings.FilePath));
        var loaded = ClientSettings.Load();
        Assert.Equal("abc", loaded.LoadRefreshToken());
        loaded.ServerUrl = "HTTPS://SCHOOL.EXAMPLE/";
        Assert.Equal("abc", loaded.LoadRefreshToken());
        loaded.ServerUrl = "https://other.example";
        Assert.Null(loaded.LoadRefreshToken());
    }

    [Fact]
    public void ClearingOrCorruptingTokenReturnsNull()
    {
        var settings = new ClientSettings();
        settings.StoreRefreshToken("abc");
        settings.StoreRefreshToken(null);
        Assert.Null(settings.LoadRefreshToken());
        Assert.Null(settings.ProtectedRefreshToken);
        settings.ProtectedRefreshToken = "not base64";
        settings.RefreshTokenServer = settings.ServerUrl;
        Assert.Null(settings.LoadRefreshToken());
    }

    [Theory]
    [InlineData("http://localhost:5080", true)]
    [InlineData("http://127.0.0.1:5080/", true)]
    [InlineData("http://[::1]:5080/", true)]
    [InlineData("https://school.up.railway.app", false)]
    [InlineData("http://192.168.1.10:5080", false)]
    [InlineData("http://localhost:6080", false)]
    [InlineData("http://localhost:5080/api", false)]
    [InlineData("invalid", false)]
    public void OnlyBundledServerAddressNeedsLocalStartup(string url, bool expected)
    {
        Assert.Equal(expected, LocalServer.IsLocalAddress(url));
    }
}
