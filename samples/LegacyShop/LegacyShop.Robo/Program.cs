using LegacyShop.Comum;
using LegacyShop.Robo.Data;
using Microsoft.Extensions.Configuration;

// Executado pelo Agendador de Tarefas a cada 15 minutos.
var configuration = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: false)
    .AddJsonFile($"appsettings.{Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") ?? "Production"}.json", optional: true)
    .AddEnvironmentVariables()
    .Build();

var repositorio = new ProtocoloRepository(configuration.GetConnectionString("DBSH281")!);
var pasta = configuration["SSI:DiretorioZip"]!;
var total = 0;
foreach (var arquivo in Directory.EnumerateFiles(pasta, "*.zip"))
{
    var protocolo = Protocolo.Gerar("SSI", DateTime.Now);
    await repositorio.AddAsync(protocolo, Path.GetFileName(arquivo), DateTime.Now);
    File.Move(arquivo, Path.Combine(configuration["SSI:DiretorioRejeitados"]!, Path.GetFileName(arquivo)), overwrite: true);
    total++;
}
Console.WriteLine($"{DateTime.Now:dd/MM/yyyy HH:mm} - {total} arquivo(s) protocolados.");
