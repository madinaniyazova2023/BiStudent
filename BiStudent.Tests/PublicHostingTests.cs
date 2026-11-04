using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using BiStudent.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace BiStudent.Tests;

public sealed class PublicHostingTests : IDisposable
{
    private const string Password = "public-test-secret-123";
    private readonly string directory = Path.Combine(Path.GetTempPath(), $"BiStudent.Public.{Guid.NewGuid():N}");
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    [Theory]
    [InlineData("", false, Password, "Enrollment:PublicUrl")]
    [InlineData("http://school.example", false, Password, "Enrollment:PublicUrl")]
    [InlineData("https://school.example/register/", false, Password, "Enrollment:PublicUrl")]
    [InlineData("https://school.example", true, Password, "Demo:Enabled")]
    [InlineData("https://school.example", false, "admin", "Setup:AdminPassword")]
    public void UnsafePublicSetupStopsStartup(string url, bool demo, string password, string setting)
    {
        using var factory = CreateFactory(url, demo, password);
        var error = Assert.Throws<InvalidOperationException>(() => factory.CreateClient());
        Assert.Contains(setting, error.Message);
    }

    [Fact]
    public async Task PhoneCanSubmitWithoutTeacherTokenAndTeacherCanApproveOnSameServer()
    {
        using var factory = CreateFactory();
        using var teacher = factory.CreateClient();
        var login = await PostAsync<TokenResponse>(teacher, "/api/auth/login", new LoginRequest("admin", Password));
        teacher.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.AccessToken);
        var group = await PostAsync<GroupDto>(teacher, "/api/groups", new CreateGroupRequest("Интернет-регистрация"));
        var student = await PostAsync<StudentDto>(teacher, "/api/students", new SaveStudentRequest("Студент", group.Id));
        var code = await PostAsync<EnrollmentCodeDto>(teacher, $"/api/groups/{group.Id}/enrollment-code", new CreateEnrollmentCodeRequest());
        Assert.Equal("https://school.example/register/", Assert.Single(code.Links));

        using var phone = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://school.example") });
        Assert.Equal(HttpStatusCode.OK, (await phone.GetAsync("/register/")).StatusCode);
        var found = await PostAsync<PublicGroupDto>(phone, "/api/public/enrollment/check", new PublicCodeRequest(code.Code));
        Assert.Equal(student.Id, Assert.Single(found.Students).Id);
        var images = FaceFixtures.EnrollmentSet("armstrong").Select(x => Convert.ToBase64String(x)).ToList();
        var submitted = await PostAsync<PublicSubmitResultDto>(phone, "/api/public/enrollment/submit",
            new PublicSubmitRequest(code.Code, student.Id, images, true));
        Assert.True(submitted.Ok, submitted.Message);
        Assert.Equal(HttpStatusCode.Unauthorized, (await phone.GetAsync("/api/face-submissions")).StatusCode);
        var submissions = await teacher.GetFromJsonAsync<List<FaceSubmissionDto>>("/api/face-submissions", Json);
        var pending = Assert.Single(submissions!);
        Assert.Equal(FaceSubmissionStatus.Pending, pending.Status);
        Assert.Equal(HttpStatusCode.NoContent, (await teacher.PostAsync($"/api/face-submissions/{pending.Id}/approve", null)).StatusCode);
        found = await PostAsync<PublicGroupDto>(phone, "/api/public/enrollment/check", new PublicCodeRequest(code.Code));
        Assert.Equal(PublicFaceState.Registered, Assert.Single(found.Students).State);
    }

    [Theory]
    [InlineData(true, HttpStatusCode.NotFound)]
    [InlineData(false, HttpStatusCode.TooManyRequests)]
    public async Task RailwayClientAddressesHaveSeparateRateLimitsOnlyWhenProxyIsEnabled(bool proxy, HttpStatusCode secondClientStatus)
    {
        using var factory = CreateFactory(proxy: proxy);
        using var client = factory.CreateClient();
        async Task<HttpStatusCode> CheckAsync(string ip)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/public/enrollment/check")
            {
                Content = JsonContent.Create(new PublicCodeRequest("INVALID"), options: Json)
            };
            request.Headers.Add("X-Real-IP", ip);
            using var response = await client.SendAsync(request);
            return response.StatusCode;
        }
        for (var i = 0; i < 30; i++) Assert.Equal(HttpStatusCode.NotFound, await CheckAsync("203.0.113.1"));
        Assert.Equal(HttpStatusCode.TooManyRequests, await CheckAsync("203.0.113.1"));
        Assert.Equal(secondClientStatus, await CheckAsync("203.0.113.2"));
    }

    [Theory]
    [InlineData("", "https://school.up.railway.app/register/")]
    [InlineData("https://ВАШ-ДОМЕН.up.railway.app", "https://school.up.railway.app/register/")]
    [InlineData("https://YOUR-SERVICE.up.railway.app", "https://school.up.railway.app/register/")]
    [InlineData("https://register.school.example", "https://register.school.example/register/")]
    public async Task EnrollmentQrUsesActualRailwayDomainInsteadOfPlaceholder(string configured, string expected)
    {
        using var baseFactory = CreateFactory(url: configured);
        using var factory = baseFactory.WithWebHostBuilder(builder =>
            builder.UseSetting("RAILWAY_PUBLIC_DOMAIN", "school.up.railway.app"));
        using var client = factory.CreateClient();
        var login = await PostAsync<TokenResponse>(client, "/api/auth/login", new LoginRequest("admin", Password));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.AccessToken);
        var group = await PostAsync<GroupDto>(client, "/api/groups", new CreateGroupRequest("QR Railway"));
        var code = await PostAsync<EnrollmentCodeDto>(client, $"/api/groups/{group.Id}/enrollment-code", new CreateEnrollmentCodeRequest());
        Assert.Equal(expected, Assert.Single(code.Links));
    }

    private WebApplicationFactory<Program> CreateFactory(string url = "https://school.example", bool demo = false,
        string password = Password, bool proxy = true) => new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
    {
        builder.UseSetting("ConnectionStrings:Default", $"Data Source={Path.Combine(directory, "test.db")};Pooling=False");
        builder.UseSetting("Firebase:ApiKey", "");
        builder.UseSetting("Firebase:ProjectId", "");
        builder.UseSetting("Hosting:Public", "true");
        builder.UseSetting("Hosting:RailwayProxy", proxy.ToString());
        builder.UseSetting("Enrollment:PublicUrl", url);
        builder.UseSetting("Demo:Enabled", demo.ToString());
        builder.UseSetting("Setup:AdminPassword", password);
    });

    private static async Task<T> PostAsync<T>(HttpClient client, string path, object body)
    {
        using var response = await client.PostAsJsonAsync(path, body, Json);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<T>(Json))!;
    }

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }
}
