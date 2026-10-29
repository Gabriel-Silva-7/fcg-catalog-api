# FCG Catalog API

Catálogo de jogos em .NET 10, com PostgreSQL próprio e autenticação JWT RS256. O serviço oferece criação administrativa, listagem/detalhe públicos e aceite assíncrono de compra; valida preço sem arredondar frações de centavo e registra o identificador do criador sem consultar o banco de Users.

## Rotas

| Método e rota | Acesso | Resultado |
|---|---|---|
| `POST /api/v1/games` | Bearer com papel `Administrator` | 201, jogo criado e Location para o detalhe |
| `GET /api/v1/games` | Público | Lista paginada dos jogos ativos |
| `GET /api/v1/games/{id}` | Público | Jogo ativo ou 404 |
| `PUT /api/v1/games/{id}` | Administrator no JWT e na projeção | 200, edição de jogo ativo |
| `DELETE /api/v1/games/{id}` | Administrator no JWT e na projeção | 204, desativação lógica idempotente |
| `POST /api/v1/games/{gameId}/promotions` | Administrator no JWT e na projeção | 201, promoção criada |
| `POST /api/v1/me/library` | Bearer User ou Administrator |202 após pedido + evento duráveis; não concede jogo |
| `GET /api/v1/me/orders` | Bearer User ou Administrator |Pedidos próprios paginados |
| `GET /api/v1/me/orders/{id}` | Bearer User ou Administrator |Pedido próprio ou404 opaco |
| `GET /api/v1/me/library` | Bearer User ou Administrator |Biblioteca própria paginada, incluindo histórico inativo |

Criação: `{ "title": "Celeste", "description": "Platformer", "basePrice": 59.90 }`. Título tem 1–200 caracteres após trim; descrição é opcional e limitada a 2.000. Preço obrigatório entre 0 e 9999999999999999.99, com centavos exatos. Títulos repetidos são permitidos. Retorno: `id`, `title`, `description`, `basePrice`, `currentPrice`, `discountPercentage`, `isActive`. `currentPrice` e `discountPercentage` consideram a maior promoção ativa; sem promoção, são iguais a basePrice e zero. Edição, desativação e promoções estão descritas abaixo.

Lista: `page=1`, `pageSize=20`, `search` opcional, `sortBy=title|basePrice|createdAt`. Page vai de 1 a 1.000.000; pageSize de 1 a 100. Pesquisa ignora caixa e espaços externos; `%`, `_` e `\` são texto literal. A ordenação inclui desempate por identificador. Inativos ficam fora da lista e retornam 404 no detalhe. Leituras continuam públicas quando o cliente envia Bearer inválido.

Erros usam `application/problem+json`, com `status`, `type`, `code`, `instance` e `traceId`: 400 validation_error, 401 unauthenticated, 403 forbidden, 404 resource_not_found e 500 internal_error. Respostas de erro não revelam detalhes internos. O evento de log GameCreated contém ator, jogo e trace; não contém JWT nem corpo da requisição.

## Configuração

Requer SDK 10.0.400, fixado em global.json, PostgreSQL16, RabbitMQ e uma chave pública correspondente ao emissor Users. O banco/role Catalog devem ser provisionados antes de iniciar; a infraestrutura local está no repositório fcg-orchestration. A role precisa de conexão e privilégios no próprio banco/schema, sem superuser ou acesso ao banco de Users.

| Variável | Obrigatória / padrão | Origem esperada |
|---|---|---|
| `RabbitMq__Host` / `RabbitMq__Port` | Host obrigatório, porta padrão5672; sem URL com credenciais | ConfigMap |
| `Queues__Identity` | Fila durável própria; padrão `fcg-catalog-identity`, distinta de Payments | ConfigMap |
| `Queues__Payments` | Fila durável própria; padrão `fcg-catalog-payments` | ConfigMap |
| `RabbitMq__VirtualHost` | `/fcg`, obrigatório | ConfigMap |
| `RabbitMq__Username` / `RabbitMq__Password` | Credencial própria obrigatória, sem padrão; sem tag administrativa | Secret |
| `ConnectionStrings__Default` | Obrigatória; PostgreSQL do próprio Catalog | Secret |
| `PGGSSENCMODE` | Perfil local: `disable`; opção do Npgsql para autenticação por senha, independente de `SslMode` | Ambiente do processo |
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

As rotas protegidas combinam o JWT com a projeção local de autorização recebida por eventos de Users. Estado desconhecido retorna503; bloqueado/excluído retorna401. Escritas administrativas exigem papel administrativo no token e na projeção atual. A convergência é assíncrona, conforme a seção de estado distribuído. O host publica pedidos, consome resultados e mantém biblioteca própria; pedidos e biblioteca são consultáveis pelo titular.

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

As migrations criam Games, Promotions, Orders, LibraryEntries, UserAuthorizationStates, MessageConflicts, PaymentRecoveryAttempts, schema de outbox e histórico EF no banco Catalog. InboxState é dependência estrutural do modelo MassTransit; o consumidor de pagamento usa transação própria e deduplicação permanente de negócio, sem inbox finito do middleware. BasePrice é numeric(18,2), com constraint de não negativo. CreatedAtUtc usa timestamp com fuso; CreatedByUserId é UUID de auditoria sem FK externa. Aplicar novamente a migration preserva dados. Falha de configuração/migration impede iniciar; não há reset automático.

`GET /health/live` é anônimo e independente do PostgreSQL. `GET /health/ready` consulta jogos, promoções, pedidos, biblioteca, projeção de autorização, conflitos, histórico de recuperação e outbox, além do estado do bus e uma conexão AMQP real com limite de2s; retorna200 Healthy ou503 Unhealthy. Banco fora, schema ausente ou permissões insuficientes deixam readiness indisponível. Voltar a iniciar o container do banco não é prova de prontidão; aguarde a condição HTTP de saúde.

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

A solução preserva Domain sem frameworks, Application→Domain, Infrastructure→Application/Domain e Api→Application/Infrastructure. Os [manifestos Kubernetes próprios](k8s/README.md) são agregados pela orquestração, que também executa a jornada distribuída em Compose e Kind.


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
snapshot, sem FK remota nem leitura de Users. A aplicação de colocação valida jogo ativo e seleciona preço do servidor antes de criar o agregado.
A colocação HTTP e a aplicação de pagamento usam esse agregado. A biblioteca guarda a aquisição confirmada na mesma transação do resultado.

O repositório participa de transação do chamador. Escrita com versão obsoleta
falha; chamador precisa usar contexto/transação válidos para uma nova tentativa.
Readiness consulta também o schema de Orders. Fora de Development, eventos EF
que carregam exceção/stack são suprimidos; falhas têm descrição fixa no health.
Não habilitar logging de dados sensíveis em produção.


## Aceite de compra e publicação

`POST /api/v1/me/library` aceita somente `{ "gameId": "UUID" }`; preço e UserId no
body são recusados. O titular vem do JWT. Game ativo e preço são lidos pelo servidor
antes do commit: uma edição/desativação posterior à leitura pode ocorrer antes da
resposta, preservando o snapshot selecionado. A seleção inclui a maior promoção ativa, como descrito abaixo.

202 retorna `orderId,gameId,price,status:"Pending"` e Location
`/api/v1/me/orders/{id}`. Pedido/evento ficam no mesmo commit; 202 não representa
pagamento aprovado ou aquisição. A Location pode ser consultada com o mesmo titular. Se a resposta se perder, consultar os próprios pedidos antes de tentar
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
Os comportamentos locais e o transporte não comprovam, sozinhos, compra integrada
entre os quatro serviços, Compose completo ou Kubernetes.


## Pagamento, biblioteca e recuperação local

`PaymentProcessedEvent` chega à fila própria `fcg-catalog-payments`; Notifications
usa outra fila e recebe sua cópia independente. O consumidor valida o contrato
canônico e, no próprio banco, serializa por OrderId. Não faz transação aninhada com
middleware nem consulta dados de Users/Payments. Ack ocorre depois do commit local.

Antes de finalizar, UserId/GameId/Price precisam coincidir com o pedido persistido.
Approved altera Order e cria LibraryEntry com preço e instante originais no mesmo
commit; Rejected altera somente Order. Jogo inativo ou preço posterior não cancelam
aceite anterior. Não se revalida estado remoto da conta nessa finalização.
Duplicado equivalente, mesmo com outro MessageId, não duplica aquisição. Status,
razão ou data divergentes preservam o primeiro terminal e não compensam biblioteca.

LibraryEntries mantém PK UserId/GameId, AcquisitionPrice numeric18,2 e AcquiredAtUtc.
OriginOrderId opcional conserva associação ao pedido novo; histórico sem Order
permanece representável e bloqueia nova compra como já adquirido. FKs só apontam
para Game/Order do próprio Catalog, sem FKUser. Não remover Game fisicamente com
histórico; a operação de produto é desativação lógica. A API de consulta preservada
retorna somente os campos de histórico contratados, sem expor o modelo de persistência.

Pedido desconhecido ou payload incompatível gera MessageConflicts com tipo concreto,
IDs, payload canônico original, hash, razão e observações. Não cria Order retroativo.
Erros para gravar diagnóstico provocam rollback/retry; inválido/esgotamento de
falha transitória seguem para `_error` sem rejeição financeira fictícia. São até
três novas tentativas1s/5s/15s. O código não apaga estado para forçar sucesso.

A operação local `PaymentApplication.ReplayAsync` usa o registro original, valida
tipo/hash/identidade e reaplica a mesma regra numa transação própria. Sem pedido,
continua recuperável; após restauração autorizada e consistente pelo dono, efeito,
resolução e PaymentRecoveryAttempt comitam juntos. Repetição não duplica aquisição.
Conflito com terminal permanece diagnosticável, sem selecionar outra decisão.
Tipo desconhecido ou payload editado é recusado. Biblioteca ausente/incompatível
para Order jáApproved é diagnóstico de restauração inconsistente; o replay não
fabrica reparo silencioso. Não editar payload/hash, apagar registro ou recriar pedido
pela ferramenta. A CLI operacional com dry-run está descrita abaixo; não existe
endpoint HTTP público de recuperação.

Provas com PostgreSQL real cobrem perda de conexão no commit, rollback conjunto de
Order/biblioteca e de efeito/resolução/histórico. Transporte real cobre contrato
inválido, quatro tentativas esgotadas e redrive do mesmo envelope, sem reset. Captura
de logs Production confirma ausência de detalhes privados/stack. Essas evidências
continuam distintas da jornada final executada com os quatro serviços reais.


## Consultas do titular

Os três GETs de `/api/v1/me` exigem JWT e usam somente `sub` como titular, inclusive
para Administrator. Não há seletor administrativo de outro usuário. Pedido alheio
ou inexistente devolve404 `resource_not_found`, sem dados do titular. Parâmetros
extras de usuário não alteram o escopo da consulta.

Pedidos retornam `orderId,gameId,price,status,placedAtUtc,completedAtUtc,rejectionReason`.
Pending tem conclusão/motivo nulos; Approved tem conclusão original/motivo nulo;
Rejected tem conclusão original/AmountAboveLimit. O primeiro GET depois do202 pode
observar diretamente o terminal; não depender de visualizar Pending antes.

As listas usam `page`1–1000000 e `pageSize`1–100, padrão1/20, envelope
`items,page,pageSize,totalCount`. Orders ordena PlacedAtUtc decrescente e OrderId
crescente no empate. Library preserva AcquiredAtUtc decrescente/GameId crescente.
Página depois do fim fica vazia com total correto. Contagem/itens são lidos num
snapshot RepeatableRead/read-only; gravação concorrente entre as duas consultas
não produz contagem e página de estados diferentes.

Biblioteca mantém a forma herdada `gameId,title,acquiredAt,acquisitionPrice`, mesmo
para jogo posteriormente inativo e preço alterado. Title vem do registro atual;
AcquisitionPrice/AcquiredAt são históricos. UserId, versão, OriginOrderId, payloads
de diagnóstico e credenciais não fazem parte dessas respostas. Swagger descreve
rotas, paginação, schemas e autorização Bearer.

## Estado distribuído de autorização

As rotas protegidas validam assinatura RS256, emissor, audiência, claims e a janela
`nbf <= agora < exp`, com tolerância zero. Depois consultam a projeção local de
Users em cada requisição. Estado ausente ou banco indisponível devolve503
`auth_state_unavailable`, com `Retry-After: 1`; bloqueado/excluído devolve401.
Uma escrita administrativa exige Administrator tanto no token quanto na projeção
atual; rebaixamento resulta403, sem elevar um token User de uma conta promovida.
Rotas públicas, health e Swagger não consultam essa projeção por causa de um Bearer.

`Queues:Identity` (padrão `fcg-catalog-identity`) recebe os contratos canônicos
UserCreatedEvent, UserStateChangedEvent e UserStateSnapshotEvent. Deve ser distinta
da fila de pagamentos. A tabela UserAuthorizationStates guarda somente UserId,
EventVersion, Role e State, sem contato ou referência ao banco de Users. Uma
transação serializa versões por usuário; versão inferior é ignorada, mesma versão
com papel/estado equivalente é idempotente. Diferença apenas de contato não é
conflito no Catalog. Exclusão é terminal mesmo contra uma versão maior ativa.

Conflitos preservam a projeção e gravam diagnóstico antes do ack, com payload
mínimo de autorização e metadados, sem Name/Email. Falha desse commit causa retry;
as tentativas são1s/5s/15s, depois `_error`. Não interpretar o payload mínimo como
um evento completo para republicação: recuperação de identidade deve solicitar
snapshots canônicos ao processo de reconciliação do Users. A recuperação de
pagamentos continua separada e usa seus contratos completos.

A convergência depende de entrega do evento: enquanto o broker estiver indisponível,
um estado local ativo pode autorizar até receber a mudança ou o token expirar.
Uma requisição já autorizada não é cancelada retroativamente. Pedidos já aceitos
continuam sendo finalizados com seu snapshot, mesmo após bloqueio da conta.
A migração AuthorizationProjection é necessária antes de liberar readiness.

## Edição e desativação de jogos

`PUT /api/v1/games/{id}` exige administrador e aceita título, descrição opcional
e preço-base, com os mesmos limites de criação. Retorna200 com o jogo atualizado;
inexistente ou inativo retorna404. Mantém ID, autor e data de criação. O preço
atualizado não altera preço de pedido ou aquisição anterior.

`DELETE /api/v1/games/{id}` desativa logicamente e retorna204, inclusive se já
inativo. Inexistente retorna404. O registro permanece para preservar pedidos e
bibliotecas; lista/detalhe públicos deixam de exibi-lo e novas compras retornam404.
Pedidos aceitos antes da leitura da desativação continuam até o resultado.

As duas operações serializam pelo registro no PostgreSQL e confirmam a transação
antes do sucesso. Se DELETE vence a edição concorrente, PUT observa inativo e
retorna404; se PUT vence, DELETE pode desativar o conteúdo editado. Não há reativação
por PUT, token de versão exigido ou garantia de cancelamento retroativo de compra.

## Promoções e preço vigente

`POST /api/v1/games/{gameId}/promotions` exige administrador e retorna201 com
`Location` para o jogo. Corpo: `discountPercentage,startsAt,endsAt`; resposta:
`id,gameId,discountPercentage,startsAt,endsAt,isCurrentlyActive`. Jogo inexistente
ou inativo retorna404. Percentual deve ser maior que0 e no máximo100, com centésimos
exatos. Datas devem ser UTC, representáveis em microssegundos, e fim maior que
início. Entrada com fração submicrossegundo retorna400 em vez de sofrer truncamento.

Sobreposições são permitidas: aplica o maior desconto ativo, início inclusivo/fim
exclusivo, usando o relógio do servidor. Arredondamento a centavos é AwayFromZero:
R$59,90 com25% resultaR$44,93. Lista, detalhe e resposta de edição usam a mesma regra;
ordenação `basePrice` continua pelo preço-base. Promoções ficam no próprio banco,
com FK apenas para Game; o criador é identificador de auditoria sem FK para Users.

A leitura de jogo/disponibilidade/maior desconto usa uma instrução SQL e captura
o instante uma vez. A lista usa snapshot read-only para contagem e itens. A criação
de promoção serializa com edição/desativação pelo registro do Game. O preço
calculado na aceitação permanece em pedido, evento e aquisição, mesmo que a
promoção expire ou o jogo seja alterado/desativado antes do commit ou pagamento.
A migração Promotions deve ser aplicada antes da readiness; desativação mantém
promoções e histórico. Não existe edição de promoção ou GET individual de promoção
nesse contrato herdado.

Em Production, o logger padrão de requisições do host é desativado porque registra
a URL com a query completa. Os eventos explícitos da aplicação preservam metadados
de auditoria sem conteúdo de busca/contato. Development mantém diagnóstico local.

## Operação local de conflitos de pagamento

O processo da imagem também aceita comandos locais, sem iniciar HTTP, bus, migrations,
bootstrap ou carregar chaves JWT. Requer apenas `ConnectionStrings__Default` do
próprio Catalog. Aplique o schema antes, pelo procedimento normal do ambiente.

```sh
dotnet FCG.Catalog.Api.dll recover-payments list --limit 20
dotnet FCG.Catalog.Api.dll recover-payments show --id <conflict-guid>
dotnet FCG.Catalog.Api.dll recover-payments replay --id <conflict-guid> --dry-run
dotnet FCG.Catalog.Api.dll recover-payments replay --id <conflict-guid> --apply
```

A prévia é o padrão quando `--apply` não é informado. Usa snapshot read-only e não
altera pedido, aquisição, diagnóstico, contadores ou histórico. É uma previsão do
estado observado, que pode mudar antes da aplicação. A aplicação real reutiliza a
regra transacional de replay: efeito, resolução e tentativa confirmam juntos. Não
cria pedido ausente, escolhe outro terminal, altera preço ou edita payload/hash.
Repetições e operadores concorrentes não duplicam aquisição.

List/show exibem somente metadados de diagnóstico, sem payload/contato. Um conflito
de identidade contém somente estado mínimo e não pode ser reaplicado como pagamento;
a recuperação desse estado usa snapshots emitidos por Users. `_error` é transporte
e tem redrive separado, sem substituir o replay local de um conflito já confirmado.

Códigos de saída:0 sucesso ou já resolvido;1 falha segura de configuração/banco;
2 argumentos inválidos;3 registro inexistente;4 pendência ou conflito preservado.
Não use credenciais em argumentos nem publique a saída operacional sem revisar IDs.

[Manifestos Kubernetes próprios](k8s/README.md) para renderização e implantação pela orquestração.

## Ambiente integrado

A [orquestração](https://github.com/Gabriel-Silva-7/fcg-orchestration) prepara fontes limpas, credenciais locais, banco e broker; seus guias descrevem Compose, Kind, jornada real e recuperação. Use as revisões selecionadas da combinação, pois o README não certifica acesso público nem integração em `main`. Este repositório contém apenas o código e os dados de sua responsabilidade.
