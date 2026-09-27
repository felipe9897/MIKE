# Compilando e testando

## Requisitos

- Windows 10 ou 11.
- SDK .NET 10.
- PHP CLI para o teste isolado do relay do celular.

## Testes

```powershell
dotnet test .\src\Mike.Tests\Mike.Tests.csproj -c Release
```

## Projetos

- `Mike.Common`: seguranca, agentes, modelos, mesh e IPC.
- `Mike.Service`: servico Windows, atualizacao e recuperacao.
- `Mike.Desktop`: aplicativo WPF, tradutor e ponte com o backend local.
- `Mike.Tray`: estado e controles na bandeja.
- `Mike.Cli`: interface de terminal.
- `Mike.Tests`: testes de seguranca e runtime.

O instalador completo e o nucleo operacional ainda sao publicados pelo pipeline privado. Isso impede que configuracoes de infraestrutura, integracoes de clientes e componentes de terceiros nao auditados sejam misturados ao codigo comunitario.
