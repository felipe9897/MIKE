# Mike Local 1.0.60

Publicada em 30 de setembro de 2026.

## Principais mudancas

- Dublagem local-first com identidade consistente para multiplos falantes.
- Prioridade para rotulos de falante fornecidos pela transcricao.
- Agrupamento acustico leve via FFmpeg quando nao ha rotulos.
- Perfis de voz clonada continuam opcionais e exigem autorizacao explicita.
- Relatorio registra mapa de vozes, metodo, confianca e revisao recomendada.
- Inicializacao e autocura corrigidas para contas `SYSTEM` sem Desktop interativo.
- Health Center passa a informar a versao realmente instalada.

## Verificacao

- Teste sintetico A-B-A-B: atribuicao `0,1,0,1`.
- MP4 de teste produzido com faixa de audio.
- 44 de 44 testes nativos aprovados.
- Parser PowerShell, JavaScript, WebView2, ConPTY e hashes aprovados.
- Instalacao de upgrade concluida com codigo zero.
- Backend instalado respondeu na porta local 47885.

## Integridade

`MikeLocalSetup.exe`

SHA-256: `2E98CB7A527A0BD8677D3B14C0C0331119BB761257DD34659E19655F72C41C2F`

Tamanho: `335103295` bytes.

O instalador ainda nao possui assinatura Authenticode comercial. O hash HTTPS
deve ser conferido antes da execucao.
