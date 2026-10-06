using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using GraphReportChat.Api;

var builder = FunctionsApplication.CreateBuilder(args);
builder.ConfigureFunctionsWebApplication();
builder.Services.AddSingleton<Settings>();
builder.Services.AddSingleton<GenerationSpec>();
builder.Services.AddSingleton<BusinessCalendar>();
builder.Services.AddSingleton<PeriodResolver>();
builder.Services.AddSingleton<EasyAuth>();
builder.Services.AddSingleton<RunStore>();
builder.Services.AddHttpClient<ITokenExchange, TokenExchange>();
builder.Services.AddHttpClient<GraphTransport>(client => client.Timeout = TimeSpan.FromSeconds(25))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
builder.Services.AddSingleton<GraphCollector>();
builder.Services.AddSingleton<FoundryService>();
builder.Services.AddSingleton<SharePointReports>();
builder.Services.AddSingleton<ReportOrchestrator>();
builder.Build().Run();
