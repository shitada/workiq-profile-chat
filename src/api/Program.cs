using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using WorkIqProfileChat.Api.Options;
using WorkIqProfileChat.Api.Security;
using WorkIqProfileChat.Api.Services;

FunctionsApplicationBuilder builder = FunctionsApplication.CreateBuilder(args);

builder.ConfigureFunctionsWebApplication();

builder.Services
    .AddApplicationInsightsTelemetryWorkerService()
    .ConfigureFunctionsApplicationInsights();

builder.Services
    .AddOptions<AuthenticationOptions>()
    .BindConfiguration(AuthenticationOptions.SectionName)
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services
    .AddOptions<FoundryOptions>()
    .BindConfiguration(FoundryOptions.SectionName)
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddHttpClient<OnBehalfOfTokenProvider>();
builder.Services.AddSingleton<EasyAuthUserContext>();
builder.Services.AddSingleton<FoundryChatService>();
builder.Services.AddSingleton<SecretExpiryService>();

builder.Build().Run();
