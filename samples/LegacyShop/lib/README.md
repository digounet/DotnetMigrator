# DLLs locais do sample

Binarios que a solucao referencia por `HintPath` (fora de pacotes NuGet), como e comum em aplicacoes legadas.
O Migrator le os metadados de cada uma (sem executar) e aponta no item `PRJ-DLL` o que impede a DLL de rodar
em .NET 10 e em Linux.

| DLL | Compilada para | O que usa | O que o Migrator aponta |
|---|---|---|---|
| `Legacy.Barcode.dll` | .NET Framework 4.8 | `System.Web` | API removida do .NET 10: bloqueante no destino net10 |
| `Legacy.Impressao.dll` | .NET Framework 4.8 | Registro do Windows, P/Invoke em `winspool.drv`, Event Log, `BinaryFormatter`, `Encoding.GetEncoding(1252)`, `ConfigurationManager` | dependencia dura de Windows (Registro, P/Invoke): a hospedagem vira container Windows/EC2; Event Log, BinaryFormatter e code pages viram itens de modernizacao; `System.Configuration.ConfigurationManager` e adicionado ao projeto migrado |

`src/` guarda o fonte de `Legacy.Impressao` para regenerar o binario (o fonte nao faz parte da solucao e nao e carregado pelo Migrator):

```bash
dotnet new classlib -n Legacy.Impressao -f net48 -o /tmp/impressao && cp src/Legacy.Impressao/ImpressoraEtiquetas.cs /tmp/impressao/ && rm /tmp/impressao/Class1.cs
dotnet build /tmp/impressao -c Release -p:DebugType=none && cp /tmp/impressao/bin/Release/net48/Legacy.Impressao.dll .
```

`Microsoft.NETFramework.ReferenceAssemblies` e restaurado automaticamente pelo SDK, entao o build funciona em macOS e Linux.
