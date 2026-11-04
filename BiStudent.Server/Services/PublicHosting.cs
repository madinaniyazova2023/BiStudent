using Microsoft.AspNetCore.HttpOverrides;

namespace BiStudent.Server.Services;

public static class PublicHosting
{
    public static void Configure(WebApplicationBuilder builder)
    {
        var configuration = builder.Configuration;
        if (configuration.GetValue<bool>("Hosting:Public"))
        {
            if (configuration.GetValue<bool>("Demo:Enabled"))
                throw new InvalidOperationException("Публичный сервер нельзя запускать с Demo:Enabled=true.");
            var address = configuration["Enrollment:PublicUrl"]?.Trim();
            if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) ||
                uri.Scheme != Uri.UriSchemeHttps || uri.IsLoopback || uri.AbsolutePath != "/" ||
                uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.UserInfo.Length != 0)
                throw new InvalidOperationException("Для публичного сервера задайте Enrollment:PublicUrl: https://ваш-домен без /register/.");
        }

        if (!configuration.GetValue<bool>("Hosting:RailwayProxy")) return;
        // Только для сервиса, доступного через HTTP edge Railway. Его X-Real-IP заменяет адрес
        // прокси адресом клиента, чтобы лимит регистрации не был общим для всех студентов.
        // Не включать при прямом доступе к Kestrel (например, через TCP proxy).
        builder.Services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            options.ForwardedForHeaderName = "X-Real-IP";
            options.ForwardLimit = 1;
            options.KnownProxies.Clear();
            options.KnownIPNetworks.Clear();
        });
    }
}
