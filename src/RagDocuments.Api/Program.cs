using System.Text;
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

var limites = app.Configuration.GetSection("Api").Get<LimitesDaApi>() ?? new LimitesDaApi();

// Corpo inválido (JSON quebrado, texto fora de UTF-8) é erro de quem chamou: 400, não 500.
app.UseExceptionHandler(new ExceptionHandlerOptions
{
    StatusCodeSelector = ex => ex is BadHttpRequestException bad ? bad.StatusCode : StatusCodes.Status500InternalServerError,
});
app.UseStatusCodePages();

// Proteção contra CSRF: um site qualquer aberto no navegador consegue mandar um formulário para
// localhost (multipart não dispara preflight de CORS). Escrita com cabeçalho Origin só passa se a
// origem estiver na lista; curl, scripts e o próprio servidor não mandam Origin e seguem normais.
app.Use(async (contexto, proximo) =>
{
    var escrita = !HttpMethods.IsGet(contexto.Request.Method) && !HttpMethods.IsHead(contexto.Request.Method)
        && !HttpMethods.IsOptions(contexto.Request.Method);
    var origem = contexto.Request.Headers.Origin.ToString();
    if (escrita && origem.Length > 0 && !limites.OrigensPermitidas.Contains(origem, StringComparer.OrdinalIgnoreCase))
    {
        await Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Origem não permitida.",
            detail: $"Escrita vinda de '{origem}' foi recusada. Inclua a origem em Api:OrigensPermitidas se ela for sua.")
            .ExecuteAsync(contexto);
        return;
    }
    await proximo(contexto);
});

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.MapGet("/documentos", async (BancoVetorial banco, CancellationToken ct) =>
    Results.Ok(await banco.ListarAsync(ct)));

app.MapPost("/documentos", async (NovoDocumento pedido, IngestaoService ingestao, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(pedido.Nome) || string.IsNullOrWhiteSpace(pedido.Conteudo))
        return Invalido("documento", "Informe nome e conteúdo.");
    if (pedido.Conteudo.Length > limites.TamanhoMaximoDoDocumento)
        return Invalido("documento", $"O documento passa de {limites.TamanhoMaximoDoDocumento} caracteres.");
    return await Ingerir(ingestao, pedido.Nome, pedido.Conteudo, pedido.Origem, ct);
});

app.MapPost("/documentos/arquivo", async (IFormFile arquivo, IngestaoService ingestao, CancellationToken ct) =>
{
    var extensao = Path.GetExtension(arquivo.FileName).ToLowerInvariant();
    if (extensao is not (".md" or ".txt"))
        return Invalido("arquivo", "Envie .md ou .txt.");
    if (arquivo.Length > limites.TamanhoMaximoDoDocumento * 4L)
        return Invalido("arquivo", "Arquivo grande demais.");

    // UTF-8 estrito: um .txt em Latin-1 seria gravado com "f�rias" em silêncio; melhor recusar e dizer por quê.
    using var memoria = new MemoryStream();
    await arquivo.CopyToAsync(memoria, ct);
    string texto;
    try
    {
        texto = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
            .GetString(memoria.GetBuffer(), 0, (int)memoria.Length).TrimStart('﻿');
    }
    catch (DecoderFallbackException)
    {
        return Invalido("arquivo", "O arquivo não está em UTF-8. Converta e envie de novo.");
    }
    if (texto.Length > limites.TamanhoMaximoDoDocumento)
        return Invalido("arquivo", $"O documento passa de {limites.TamanhoMaximoDoDocumento} caracteres.");
    return await Ingerir(ingestao, arquivo.FileName, texto, "upload", ct);
}).DisableAntiforgery();

app.MapDelete("/documentos/{id:guid}", async (Guid id, BancoVetorial banco, CancellationToken ct) =>
    await banco.ApagarAsync(id, ct) ? Results.NoContent() : Results.NotFound());

app.MapPost("/perguntas", async (NovaPergunta pedido, RespostaService respostas, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(pedido.Pergunta))
        return Invalido("pergunta", "Informe a pergunta.");
    if (pedido.Pergunta.Length > limites.TamanhoMaximoDaPergunta)
        return Invalido("pergunta", $"A pergunta passa de {limites.TamanhoMaximoDaPergunta} caracteres.");
    return Results.Ok(await respostas.ResponderAsync(pedido.Pergunta, ct));
});

await app.RunAsync();
return 0;

static IResult Invalido(string campo, string mensagem) =>
    Results.ValidationProblem(new Dictionary<string, string[]> { [campo] = [mensagem] });

static async Task<IResult> Ingerir(IngestaoService ingestao, string nome, string conteudo, string? origem, CancellationToken ct)
{
    try
    {
        return Results.Ok(await ingestao.IngerirAsync(nome, conteudo, origem, ct));
    }
    catch (ArgumentException ex)
    {
        // Documento sem texto, só com títulos etc.: problema do que foi enviado, não do servidor.
        return Invalido("documento", ex.Message.Split(" (Parameter", 2)[0]);
    }
}

internal sealed record NovoDocumento(string Nome, string Conteudo, string? Origem);
internal sealed record NovaPergunta(string Pergunta);

internal sealed class LimitesDaApi
{
    public int TamanhoMaximoDaPergunta { get; set; } = 2_000;
    public int TamanhoMaximoDoDocumento { get; set; } = 2_000_000;
    public string[] OrigensPermitidas { get; set; } = [];
}

public partial class Program;
