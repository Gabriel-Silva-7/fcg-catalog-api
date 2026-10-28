# FCG Catalog API

Catálogo de jogos em .NET 10, com PostgreSQL próprio e autenticação JWT RS256. O serviço oferece criação administrativa, listagem/detalhe públicos e aceite assíncrono de compra; valida preço sem arredondar frações de centavo e registra o identificador do criador sem consultar o banco de Users.

## Rotas

| Método e rota | Acesso | Resultado |
|---|---|---|
| `POST /api/v1/games` | Bearer com papel `Administrator` | 201, jogo criado e Location para o detalhe |
| `GET /api/v1/games` | Público | Lista paginada dos jogos ativos |
| `GET /api/v1/games/{id}` | Público | Jogo ativo ou 404 |
| `POST /api/v1/me/library` | Bearer User ou Administrator |202 após pedido + evento duráveis; não concede jogo |

Criação: `{ "title": "Celeste", "description": "Platformer", "basePrice": 59.90 }`. Título tem 1–200 caracteres após trim; descrição é opcional e limitada a 2.000. Preço obrigatório entre 0 e 9999999999999999.99, com centavos exatos. Títulos repetidos são permitidos. Retorno: `id`, `title`, `description`, `basePrice`, `currentPrice`, `discountPercentage`, `isActive`. Neste incremento currentPrice=basePrice e desconto=0; promoções e alteração/exclusão de jogos ainda não estão expostas.

Lista: `page=1`, `pageSize=20`, `search` opcional, `sortBy=title|basePrice|createdAt`. Page vai de 1 a 1.000.000; pageSize de 1 a 100. Pesquisa ignora caixa e espaços externos; `%`, `_` e `\` são texto literal. A ordenação inclui desempate por identificador. Inativos ficam fora da lista e retornam 404 no detalhe. Leituras continuam públicas quando o cliente envia Bearer inválido.

Erros usam `application/problem+json`, com `status`, `type`, `code`, `instance` e `traceId`: 400 validation_error, 401 unauthenticated, 403 forbidden, 404 resource_not_found e 500 internal_error. Respostas de erro não revelam detalhes internos. O evento de log GameCreated contém ator, jogo e trace; não contém JWT nem corpo da requisição.

## Configuração

Requer SDK 10.0.400, fixado em global.json, PostgreSQL16, RabbitMQ e uma chave pública correspondente ao emissor Users. O banco/role Catalog devem ser provisionados antes de iniciar; a infraestrutura local está no repositório fcg-orchestration. A role precisa de conexão e privilégios no próprio banco/schema, sem superuser ou acesso ao banco de Users.

| Variável | Obrigatória / padrão | Origem esperada |
|---|---|---|
| `RabbitMq__Host` / `RabbitMq__Port` | Host obrigatório, porta padrão5672; sem URL com credenciais | ConfigMap |
| `RabbitMq__VirtualHost` | `/fcg`, obrigatório | ConfigMap |
| `RabbitMq__Username` / `RabbitMq__Password` | Credencial própria obrigatória, sem padrão; sem tag administrativa | Secret |
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

**Limite atual:** a autorização usa o papel do token. A projeção local de estado da conta e sua revogação por eventos ainda serão conectadas; esta versão não comprova bloqueio distribuído. O host publica pedidos pelo broker, mas ainda não consome pagamentos nem mantém biblioteca. A integração ponta a ponta e a consulta de pedidos serão acrescentadas nos incrementos correspondentes.

Para executar, configure as variáveis acima no ambiente e disponibilize a chave pública. Exemplo de configuração local, sem credenciais reais no Git:

```sh
export ConnectionStrings__Default='Host=localhost;Port=5432;Database=fcg_catalog;Username=fcg_catalog;Password=SUBSTITUA_LOCALMENTE'
export RabbitMq__Host=localhost
export RabbitMq__VirtualHost=/fcg
export RabbitMq__Username=fcg_catalog
export RabbitMq__Password=SUBSTITUA_LOCALMENTE
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

As migrations criam Games, Orders, o schema de outbox e o histórico EF no banco Catalog. InboxState é dependência estrutural do modelo MassTransit; nenhum consumidor/inbox de negócio está habilitado neste incremento. BasePrice é numeric(18,2), com constraint de não negativo. CreatedAtUtc usa timestamp com fuso; CreatedByUserId é UUID de auditoria sem FK externa. Aplicar novamente a migration preserva dados. Falha de configuração/migration impede iniciar; não há reset automático.

`GET /health/live` é anônimo e independente do PostgreSQL. `GET /health/ready` consulta Games/Orders/outbox, estado do bus e uma conexão AMQP real com limite de2s; retorna200 Healthy ou503 Unhealthy. Banco fora, schema ausente ou permissões insuficientes deixam readiness indisponível. Voltar a iniciar o container do banco não é prova de prontidão; aguarde a condição HTTP de saúde.

## Testes

Docker acessível é necessário para os testes PostgreSQL/RabbitMQ. O SDK local precisa corresponder ao global.json.

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


## Modelo de pedidos

`Order` guarda UserId/GameId, preço BRL exato e instante UTC de microssegundos como
snapshot imutável no domínio. Começa Pending, versão1, sem conclusão/motivo.
Finaliza Approved sem motivo ou Rejected/AmountAboveLimit, versão2. Repetir a mesma
decisão/data é idempotente; outra decisão/data não altera terminal. A versão é
um token de concorrência local, sem usar xmin como versão de negócio.

A tabela própria `Orders` tem CHECKs para IDs, preço e combinação de estado,
conclusão, motivo e versão. Índice único parcial `UX_Orders_OpenUserGame` permite
somente um Pending ou Approved por usuário/jogo. Rejected libera nova tentativa;
Approved ocupa o par permanentemente. UserId/GameId são identificadores do
snapshot, sem FK remota nem leitura de Users. A aplicação de colocação deverá
validar jogo ativo e selecionar preço do servidor antes de criar o agregado.
A colocação HTTP usa esse agregado; aplicação de pagamento e biblioteca continuam no incremento seguinte.

O repositório participa de transação do chamador. Escrita com versão obsoleta
falha; chamador precisa usar contexto/transação válidos para uma nova tentativa.
Readiness consulta também o schema de Orders. Fora de Development, eventos EF
que carregam exceção/stack são suprimidos; falhas têm descrição fixa no health.
Não habilitar logging de dados sensíveis em produção.


## Aceite de compra e publicação

`POST /api/v1/me/library` aceita somente `{ "gameId": "UUID" }`; preço e UserId no
body são recusados. O titular vem do JWT. Game ativo e preço são lidos pelo servidor
antes do commit: uma edição/desativação posterior à leitura pode ocorrer antes da
resposta, preservando o snapshot selecionado. Promoções serão conectadas em CAT06.

202 retorna `orderId,gameId,price,status:"Pending"` e Location
`/api/v1/me/orders/{id}`. Pedido/evento ficam no mesmo commit; 202 não representa
pagamento aprovado ou aquisição. As consultas dessa Location serão implementadas
em CAT07. Se a resposta se perder, consultar os próprios pedidos antes de tentar
outra compra: não há chave de idempotência de POST.

Jogo inexistente/inativo produz404 antes de duplicidade. Pending do mesmo par
produz409 `order_already_pending` com `orderId`, `location` e header Location.
Approved produz409 `game_already_acquired`. Rejected permite nova tentativa com
outro ID e preço corrente. Swagger documenta as duas formas de409 e o header202.

O commit usa lock transacional por usuário/jogo e índice único parcial. Colisão
do índice exato reabre scope/contexto/outbox e lê estado confirmado para classificar
o vencedor; não transforma Approved em Pending. Retries internos preservam o
snapshot/ID selecionados. Três colisões sem estabilizar retornam503
`order_placement_unavailable`, Retry-After1, sem inventar estado existente.

MassTransit8.5.11 bus outbox grava `OrderPlacedEvent` canônico com pedido, usando
MessageId e CorrelationId iguais ao OrderId. O entregador separado publica depois;
broker indisponível deixa intenção recuperável, sem exigir outro POST. Payload tem
OrderId/UserId/GameId/Price/PlacedAtUtc, BRL exato e UTC de seis casas. A topologia
durável de Payments deve estar provisionada antes da publicação. Não habilitar
topologia transitória só para esconder ausência de consumidor.

As provas separam stage/rollback de entrega real: testes de atomicidade retiram
somente o entregador bus outbox; testes HTTP/RabbitMQ mantêm o serviço real. Logs
OrderPlaced contêm somente titular, jogo, pedido e trace, sem body/JWT/credenciais.
O schema/outbox da aplicação não prova, sozinho, fluxo completo de pagamento,
biblioteca, Compose ou Kubernetes.
