using Npgsql;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using RagDocuments.Api;
using RagDocuments.Core;
using RagDocuments.Core.Armazenamento;
using RagDocuments.Core.Ingestao;
using RagDocuments.Core.Respostas;

var builder = WebApplication.CreateBuilder(args);

var opcoes = builder.Configuration.GetSection(OpcoesRag.Secao).Get<OpcoesRag>() ?? new OpcoesRag();
builder.Services.AddSingleton(opcoes);

var conexao = builder.Configuration.GetConnectionString("Rag")
    ?? throw new InvalidOperationException("Configure ConnectionStrings:Rag.");
builder.Services.AddSingleton(_ =>
{
    var fonte = new NpgsqlDataSourceBuilder(conexao);
    fonte.UseVector();
    return fonte.Build();
});
builder.Services.AddSingleton<BancoVetorial>();
builder.Services.AddScoped<IngestaoService>();
builder.Services.AddScoped<RespostaService>();
builder.Services.AddProvedoresDeIa(builder.Configuration);

// Observabilidade desde o dia um: spans de cada etapa (ingestão, busca, geração) e, das chamadas
// ao modelo, tokens e latência no padrão de convenções de GenAI do OpenTelemetry.
// O exportador OTLP liga quando OTEL_EXPORTER_OTLP_ENDPOINT está definido.
var otel = builder.Services.AddOpenTelemetry()
    .WithTracing(t => t.AddSource(Telemetria.Nome, Provedores.FonteDeTelemetria))
    .WithMetrics(m => m.AddMeter(Provedores.FonteDeTelemetria));
if (!string.IsNullOrEmpty(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
    otel.UseOtlpExporter();

builder.Services.AddProblemDetails();

var app = builder.Build();
await app.Services.GetRequiredService<BancoVetorial>().PrepararAsync();

// Comandos de linha: `dotnet run -- ingerir exemplos` e `dotnet run -- avaliar evals/perguntas.json`.
if (args is ["ingerir", var pasta, ..])
    return await Comandos.IngerirPastaAsync(app.Services, pasta);
if (args is ["avaliar", var arquivo, ..])
    return await Comandos.AvaliarAsync(app.Services, arquivo);

// Corpo inválido (JSON quebrado, texto fora de UTF-8) é erro de quem chamou: 400, não 500.
app.UseExceptionHandler(new ExceptionHandlerOptions
{
    StatusCodeSelector = ex => ex is BadHttpRequestException bad ? bad.StatusCode : StatusCodes.Status500InternalServerError,
});
app.UseStatusCodePages();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.MapGet("/documentos", async (BancoVetorial banco, CancellationToken ct) =>
    Results.Ok(await banco.ListarAsync(ct)));

app.MapPost("/documentos", async (NovoDocumento pedido, IngestaoService ingestao, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(pedido.Nome) || string.IsNullOrWhiteSpace(pedido.Conteudo))
        return Results.ValidationProblem(new Dictionary<string, string[]> { ["documento"] = ["Informe nome e conteúdo."] });
    var r = await ingestao.IngerirAsync(pedido.Nome, pedido.Conteudo, pedido.Origem, ct);
    return Results.Ok(r);
});

app.MapPost("/documentos/arquivo", async (IFormFile arquivo, IngestaoService ingestao, CancellationToken ct) =>
{
    var extensao = Path.GetExtension(arquivo.FileName).ToLowerInvariant();
    if (extensao is not (".md" or ".txt"))
        return Results.ValidationProblem(new Dictionary<string, string[]> { ["arquivo"] = ["Envie .md ou .txt."] });
    using var leitor = new StreamReader(arquivo.OpenReadStream());
    var r = await ingestao.IngerirAsync(arquivo.FileName, await leitor.ReadToEndAsync(ct), "upload", ct);
    return Results.Ok(r);
}).DisableAntiforgery();

app.MapDelete("/documentos/{id:guid}", async (Guid id, BancoVetorial banco, CancellationToken ct) =>
    await banco.ApagarAsync(id, ct) ? Results.NoContent() : Results.NotFound());

app.MapPost("/perguntas", async (NovaPergunta pedido, RespostaService respostas, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(pedido.Pergunta))
        return Results.ValidationProblem(new Dictionary<string, string[]> { ["pergunta"] = ["Informe a pergunta."] });
    return Results.Ok(await respostas.ResponderAsync(pedido.Pergunta, ct));
});

await app.RunAsync();
return 0;

internal sealed record NovoDocumento(string Nome, string Conteudo, string? Origem);
internal sealed record NovaPergunta(string Pergunta);

public partial class Program;
