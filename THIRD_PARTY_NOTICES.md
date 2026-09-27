# Componentes de terceiros

O codigo-fonte do MIKE usa os pacotes abaixo. Cada componente permanece sob sua propria licenca; consulte o pacote e seu repositorio oficial antes de redistribuir binarios.

| Componente | Uso | Licenca declarada pelo projeto |
|---|---|---|
| Microsoft.Extensions.Hosting | Servico e ciclo de vida .NET | MIT |
| Microsoft.Extensions.Hosting.WindowsServices | Integracao com Windows Service | MIT |
| Microsoft.Extensions.Logging / Abstractions | Logging | MIT |
| Microsoft.Web.WebView2 | Interface web no aplicativo Windows | Licenca Microsoft WebView2 SDK |
| NATS.Net | Mensageria entre computadores pareados | Apache-2.0 |
| xUnit.net | Testes | Apache-2.0 |
| Moq | Testes | BSD-3-Clause |

O runtime WebView2, Ollama, modelos de IA, PHP, FFmpeg e outras ferramentas instaladas ou integradas pelo produto nao sao relicenciados pela Licenca Comunitaria MIKE. Modelos e conteudos baixados pelo usuario possuem termos proprios.

Este inventario deve ser revisto em toda alteracao de dependencias. Divergencias devem bloquear uma publicacao publica ate serem resolvidas.
