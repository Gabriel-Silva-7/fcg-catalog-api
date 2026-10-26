# FCG Catalog API

Catálogo de jogos em .NET 10, com PostgreSQL próprio e autenticação JWT RS256. O serviço oferece criação administrativa, listagem e detalhe públicos, valida preço sem arredondar frações de centavo e registra o identificador do criador sem consultar o banco de Users.

## Rotas

| Método e rota | Acesso | Resultado |
|---|---|---|
| `POST /api/v1/games` | Bearer com papel `Administrator` | 201, jogo criado e Location para o detalhe |
| `GET /api/v1/games` | Público | Lista paginada dos jogos ativos |
| `GET /api/v1/games/{id}` | Público | Jogo ativo ou 404 |

Criação: `{ "title": "Celeste", "description": "Platformer", "basePrice": 59.90 }`. Título tem 1–200 caracteres após trim; descrição é opcional e limitada a 2.000. Preço obrigatório entre 0 e 9999999999999999.99, com centavos exatos. Títulos repetidos são permitidos. Retorno: `id`, `title`, `description`, `basePrice`, `currentPrice`, `discountPercentage`, `isActive`. Neste incremento currentPrice=basePrice e desconto=0; promoções e alteração/exclusão de jogos ainda não estão expostas.

Lista: `page=1`, `pageSize=20`, `search` opcional, `sortBy=title|basePrice|createdAt`. Page vai de 1 a 1.000.000; pageSize de 1 a 100. Pesquisa ignora caixa e espaços externos; `%`, `_` e `\` são texto literal. A ordenação inclui desempate por identificador. Inativos ficam fora da lista e retornam 404 no detalhe. Leituras continuam públicas quando o cliente envia Bearer inválido.

Erros usam `application/problem+json`, com `status`, `type`, `code`, `instance` e `traceId`: 400 validation_error, 401 unauthenticated, 403 forbidden, 404 resource_not_found e 500 internal_error. Respostas de erro não revelam detalhes internos. O evento de log GameCreated contém ator, jogo e trace; não contém JWT nem corpo da requisição.

## Configuração

Requer SDK 10.0.400, fixado em global.json, PostgreSQL 16 e uma chave pública correspondente ao emissor Users. O banco/role Catalog devem ser provisionados antes de iniciar; a infraestrutura local está no repositório fcg-orchestration. A role precisa de conexão e privilégios no próprio banco/schema, sem superuser ou acesso ao banco de Users.

| Variável | Obrigatória / padrão | Origem esperada |
|---|---|---|
| `ConnectionStrings__Default` | Obrigatória; PostgreSQL do próprio Catalog | Secret |
| `Jwt__Issuer` | Obrigatória; exemplo `fcg-users` | ConfigMap |
| `Jwt__Audience` | Obrigatória; exemplo `fcg-api` | ConfigMap |
| `Jwt__Algorithm` | `RS256`; outro valor é recusado | ConfigMap |
| `Jwt__ExpirationMinutes` | `15`; outro valor é recusado | ConfigMap |
| `Jwt__ClockSkewSeconds` | `0`; outro valor é recusado | ConfigMap |
| `Jwt__PublicKeyPath` | Arquivo PEM `PUBLIC KEY` (SPKI), RSA de pelo menos 2048 bits | Caminho para Secret montado |
| `Database__MigrateOnStartup` | `false`; ligar explicitamente para aplicar migrations antes do host | ConfigMap |
| `Swagger__Enabled` | `false`; ligar para publicar Swagger UI/JSON | ConfigMap |
| `ASPNETCORE_HTTP_PORTS` | `8080` na imagem | ConfigMap |

Catalog recusa configuração de chave privada, PEM privado, PKCS#1 e chave fraca. Apenas Users deve guardar a privada. Assinatura, algoritmo, emissor, audiência, validade e claims únicas `sub`/`role`/`jti` são verificados; sub e jti são GUIDs não vazios, e role é User ou Administrator. JSON ambíguo com membros duplicados é recusado.

**Limite atual:** a autorização usa o papel do token. A projeção local de estado da conta e sua revogação por eventos ainda serão conectadas; esta versão não comprova bloqueio distribuído. Ainda não há consumidores, pedidos, compras, biblioteca ou broker no host.

Para executar, configure as variáveis acima no ambiente e disponibilize a chave pública. Exemplo de configuração local, sem credenciais reais no Git:

```sh
export ConnectionStrings__Default='Host=localhost;Port=5432;Database=fcg_catalog;Username=fcg_catalog;Password=SUBSTITUA_LOCALMENTE'
export Jwt__Issuer=fcg-users
export Jwt__Audience=fcg-api
export Jwt__PublicKeyPath="$PWD/.local/jwt-public.pem"
export Database__MigrateOnStartup=true
export Swagger__Enabled=true
dotnet restore FCG.Catalog.sln --locked-mode
dotnet run --project src/FCG.Catalog.Api --no-launch-profile --urls http://localhost:8080
```

Com Swagger habilitado: `/swagger` e `/swagger/v1/swagger.json`. Criação requer access token emitido por Users com o papel apropriado. O serviço não emite tokens nem cria um administrador automaticamente.

## Persistência e saúde

A migration cria Games e o histórico EF no banco Catalog. BasePrice é numeric(18,2), com constraint de não negativo. CreatedAtUtc usa timestamp com fuso; CreatedByUserId é UUID de auditoria sem FK externa. Aplicar novamente a migration preserva dados. Falha de configuração/migration impede iniciar; não há reset automático.

`GET /health/live` é anônimo e independente do PostgreSQL. `GET /health/ready` consulta a tabela Games e retorna 200 Healthy ou 503 Unhealthy. Banco fora, schema ausente ou permissões insuficientes deixam readiness indisponível. Voltar a iniciar o container do banco não é prova de prontidão; aguarde a condição HTTP de saúde.

## Testes

Docker acessível é necessário para os testes PostgreSQL. O SDK local precisa corresponder ao global.json.

```sh
dotnet restore FCG.Catalog.sln --locked-mode
dotnet build FCG.Catalog.sln -c Release --no-restore
dotnet test FCG.Catalog.sln -c Release --no-build --no-restore
```

`sh scripts/verify.sh` também reúne os checks. Outra instalação pode ser escolhida por `DOTNET=/caminho/para/dotnet`. Os testes usam PostgreSQL 16 por digest, bancos e roles sem superuser isolados, JWTs assinados com chaves efêmeras e migrações reais. Cobrem endpoints, limites, precisão, pesquisa literal, paginação, autorização, configuração, erros/logs, OpenAPI, persistência, queda/recuperação do banco, health/probe, contratos e fronteiras das camadas. Os recursos próprios são descartados; não se conectam a dados de desenvolvimento.

## Imagem

Build multi-stage com SDK 10.0.400 e ASP.NET 10.0.11 por digest, UID 1654 e porta 8080. Chaves, ambientes, testes e planejamento não entram na imagem. Prepare `.local/catalog.env` com a configuração acima, usando o hostname PostgreSQL alcançável pela rede escolhida e `Jwt__PublicKeyPath=/run/secrets/jwt-public.pem`.

```sh
docker --context orbstack build -t fcg-catalog-api:local .
container_id=$(docker --context orbstack run -d \
  --network "$CATALOG_DOCKER_NETWORK" -p 127.0.0.1::8080 \
  --env-file .local/catalog.env \
  --mount "type=bind,src=$PWD/.local/jwt-public.pem,dst=/run/secrets/jwt-public.pem,readonly" \
  fcg-catalog-api:local)
docker --context orbstack port "$container_id" 8080/tcp
docker --context orbstack inspect --format '{{.State.Health.Status}}' "$container_id"
# Ao encerrar somente esta instância:
docker --context orbstack rm -f "$container_id"
```

O probe executa `dotnet FCG.Catalog.Api.dll --healthcheck`, consultando readiness no próprio localhost:8080. Retorna 0 para sucesso e 1 para erro/timeout, sem iniciar outro servidor e sem exigir curl/shell. A porta interna deve permanecer 8080; a porta do host pode ser aleatória.

A solução preserva Domain sem frameworks, Application→Domain, Infrastructure→Application/Domain e Api→Application/Infrastructure. Manifestos Kubernetes e a jornada distribuída final pertencem aos próximos incrementos; a pasta k8s ainda documenta essa pendência.
