using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using BiStudent.Contracts;
using BiStudent.Server.Services;
using ClosedXML.Excel;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace BiStudent.Tests;

public sealed class AuditFixesTests : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    private readonly string temporaryDirectory = Path.Combine(Path.GetTempPath(), $"BiStudent.Tests.{Guid.NewGuid():N}");
    private readonly WebApplicationFactory<Program> factory;
    private readonly HttpClient client;

    public AuditFixesTests()
    {
        Directory.CreateDirectory(temporaryDirectory);
        factory = new ApiFactory(temporaryDirectory);
        client = factory.CreateClient();
    }

    [Fact]
    public async Task GroupChangesRequireOwnScheduleOrAdmin()
    {
        await AuthenticateAsync();
        var password = await PostAsync<TemporaryPasswordDto>("/api/admin/teachers",
            new SaveTeacherRequest("teacher-b", "Преподаватель Б", false, true));
        using var other = factory.CreateClient();
        var token = await LoginAsync(other, password.Login, password.Password);
        other.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
        var thirdPassword = await PostAsync<TemporaryPasswordDto>("/api/admin/teachers",
            new SaveTeacherRequest("teacher-c", "Преподаватель В", false, true));
        using var third = factory.CreateClient();
        var thirdToken = await LoginAsync(third, thirdPassword.Login, thirdPassword.Password);
        third.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", thirdToken.AccessToken);

        var group = await PostAsync<GroupDto>("/api/groups", new CreateGroupRequest("Группа А"));
        var target = await PostAsync<GroupDto>("/api/groups", new CreateGroupRequest("Новая группа"));
        var student = await PostAsync<StudentDto>("/api/students", new SaveStudentRequest("Первое имя", group.Id));
        var date = DateOnly.FromDateTime(DateTime.Today);
        Assert.Equal(HttpStatusCode.OK, (await other.PostAsJsonAsync("/api/schedule/slots",
            new SaveSlotRequest(group.Id, null, "Алгебра", null, date.DayOfWeek,
                new TimeOnly(8, 0), new TimeOnly(9, 20), WeekParity.Every, null), JsonOptions)).StatusCode);

        Assert.Equal(HttpStatusCode.Forbidden, (await third.PostAsJsonAsync("/api/students",
            new SaveStudentRequest("Чужой студент", group.Id), JsonOptions)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await third.PutAsJsonAsync($"/api/students/{student.Id}",
            new SaveStudentRequest("Другое имя", group.Id), JsonOptions)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await third.PutAsJsonAsync($"/api/students/{student.Id}",
            new SaveStudentRequest("Первое имя", target.Id), JsonOptions)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await third.DeleteAsync($"/api/students/{student.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await third.GetAsync($"/api/groups/{group.Id}/enrollment-code")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await third.PostAsJsonAsync($"/api/groups/{group.Id}/enrollment-code",
            new CreateEnrollmentCodeRequest(), JsonOptions)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await third.DeleteAsync($"/api/groups/{group.Id}/enrollment-code")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await third.DeleteAsync($"/api/groups/{group.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await third.PostAsJsonAsync($"/api/students/{student.Id}/enroll",
            new EnrollRequest([]), JsonOptions)).StatusCode);

        var added = await other.PostAsJsonAsync("/api/students", new SaveStudentRequest("Студент Б", target.Id), JsonOptions);
        Assert.Equal(HttpStatusCode.OK, added.StatusCode);
        var newStudent = await added.Content.ReadFromJsonAsync<StudentDto>(JsonOptions);
        Assert.NotNull(newStudent);

        Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync($"/api/students/{student.Id}",
            new SaveStudentRequest("Имя администратора", group.Id), JsonOptions)).StatusCode);
        await PostAsync<EnrollmentCodeDto>($"/api/groups/{group.Id}/enrollment-code", new CreateEnrollmentCodeRequest());
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/groups/{group.Id}/enrollment-code")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/students/{student.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync($"/api/students/{newStudent!.Id}",
            new SaveStudentRequest("Студент Б", group.Id), JsonOptions)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/students/{newStudent!.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/groups/{target.Id}")).StatusCode);
    }

    [Fact]
    public async Task MissingTargetGroupDoesNotRenameStudent()
    {
        await AuthenticateAsync();
        var group = await PostAsync<GroupDto>("/api/groups", new CreateGroupRequest("Группа для перевода"));
        var student = await PostAsync<StudentDto>("/api/students", new SaveStudentRequest("Старое имя", group.Id));

        Assert.Equal(HttpStatusCode.NotFound, (await client.PutAsJsonAsync($"/api/students/{student.Id}",
            new SaveStudentRequest("Новое имя", int.MaxValue), JsonOptions)).StatusCode);
        var roster = await client.GetFromJsonAsync<List<StudentDto>>($"/api/groups/{group.Id}/students", JsonOptions);
        Assert.Contains(roster!, x => x.Id == student.Id && x.FullName == "Старое имя");
    }

    [Theory]
    [InlineData("Программирование С# · 505 · нед. 2–4")]
    [InlineData("Программирование С# · 505 · нед. 2—4")]
    public void ParserReadsDashWeekRanges(string entry)
    {
        Assert.True(CollegeScheduleParser.TryParseEntry(entry, out var subject, out var room, out var weeks));
        Assert.Equal("Программирование С#", subject);
        Assert.Equal("505", room);
        Assert.Equal((2, 4, false), Assert.Single(weeks));
    }

    [Fact]
    public void ParserSkipsOverflowingWeekNumber()
    {
        Assert.False(CollegeScheduleParser.TryParseEntry(
            "Программирование · 505 · нед. 999999999999999999999", out _, out _, out var weeks));
        Assert.Empty(weeks);
    }

    [Fact]
    public void ParserReadsSingleDigitHour()
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add("Расписание");
        sheet.Cell(6, 1).Value = "Дни";
        sheet.Cell(6, 5).Value = "23-ПО-103-4р-1";
        sheet.Cell(7, 1).Value = "Понедельник";
        sheet.Cell(7, 2).Value = "8:00 9:20";
        sheet.Range(7, 2, 8, 2).Merge();
        sheet.Cell(7, 5).Value = "Программирование С# · 505 · нед. 2-4";
        sheet.Cell(8, 5).Value = "Иванов И.И.";
        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        stream.Position = 0;

        var parsed = CollegeScheduleParser.Parse(stream, new DateOnly(2026, 9, 1));
        Assert.Contains(parsed.Lessons, x => x.Start == new TimeOnly(8, 0) && x.End == new TimeOnly(9, 20));
    }

    [Fact]
    public async Task OneOffSlotBecomesRegular()
    {
        await AuthenticateAsync();
        var group = await PostAsync<GroupDto>("/api/groups", new CreateGroupRequest("Регулярная группа"));
        var date = DateOnly.FromDateTime(DateTime.Today).AddDays(-7);
        var request = new SaveSlotRequest(group.Id, null, "Физика", null, date.DayOfWeek,
            new TimeOnly(19, 0), new TimeOnly(20, 20), WeekParity.Every, date);
        var slot = await PostAsync<SlotDto>("/api/schedule/slots", request);

        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"/api/schedule/slots/{slot.Id}",
            request with { OneOffDate = null }, JsonOptions)).StatusCode);
        var next = date.AddDays(7);
        var lessons = await client.GetFromJsonAsync<List<LessonDto>>($"/api/schedule/day?date={next:yyyy-MM-dd}", JsonOptions);
        Assert.Contains(lessons!, x => x.Slot.Id == slot.Id);
    }

    [Fact]
    public async Task JournalWithAncientStartReturnsQuickly()
    {
        await AuthenticateAsync();
        var group = await PostAsync<GroupDto>("/api/groups", new CreateGroupRequest("Большой период"));
        var today = DateOnly.FromDateTime(DateTime.Today);
        await PostAsync<SlotDto>("/api/schedule/slots", new SaveSlotRequest(group.Id, null, "История", null,
            today.DayOfWeek, new TimeOnly(19, 0), new TimeOnly(20, 20), WeekParity.Every, null));

        var timer = Stopwatch.StartNew();
        using var response = await client.GetAsync($"/api/journal/{group.Id}?from=0001-01-01&to={today:yyyy-MM-dd}");
        timer.Stop();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(5), $"Журнал формировался {timer.Elapsed}.");
    }

    private async Task<T> PostAsync<T>(string path, object value)
    {
        var response = await client.PostAsJsonAsync(path, value, JsonOptions);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<T>(JsonOptions))!;
    }

    private async Task AuthenticateAsync()
    {
        var token = await LoginAsync(client, "teacher", "teacher123");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
    }

    private static async Task<TokenResponse> LoginAsync(HttpClient target, string login, string password)
    {
        var response = await target.PostAsJsonAsync("/api/auth/login", new LoginRequest(login, password), JsonOptions);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<TokenResponse>(JsonOptions))!;
    }

    public void Dispose()
    {
        client.Dispose();
        factory.Dispose();
        Directory.Delete(temporaryDirectory, recursive: true);
    }

    private sealed class ApiFactory(string directory) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseContentRoot(ServerRoot());
            builder.UseSetting("ConnectionStrings:Default", $"Data Source={Path.Combine(directory, "test.db")};Pooling=False");
            builder.UseSetting("Demo:Enabled", "true");
        }

        private static string ServerRoot()
        {
            for (var current = new DirectoryInfo(AppContext.BaseDirectory); current is not null; current = current.Parent)
            {
                var candidate = Path.Combine(current.FullName, "BiStudent.Server");
                if (File.Exists(Path.Combine(candidate, "seed", "students.demo.csv"))) return candidate;
            }
            throw new DirectoryNotFoundException("BiStudent.Server/seed/students.demo.csv");
        }
    }
}
