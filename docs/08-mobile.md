# 08. Mobile (React Native + Expo) — arquitetura tela por tela

> Implementa os ADRs 0001, 0010, 0017, 0019–0023, 0027, 0030, 0032–0036, 0038–0041 e 0049 (push dos pedidos do site) sobre os contratos de `06-contratos-api.md`. Diz **quais telas existem, de onde vêm os dados, o que cada ação envia e como o app se comporta sem conexão**. Payloads e `errorCode`s vêm **só** do `06`.

## 8.1 Stack e alvo

| Item | Escolha |
|---|---|
| Alvos | Android e iOS; navegador desktop (Chrome/Edge) com o mesmo código, §8.15 (ADR-0053) |
| Base | Expo SDK estável mais recente (Dev Client + EAS Build/Update, `runtimeVersion: fingerprint`), React Native com a New Architecture, Hermes, TypeScript `strict` |
| Navegação | Expo Router; abas superiores com `@react-navigation/material-top-tabs` via `withLayoutContext` |
| Servidor/cache | TanStack Query v5 + `@tanstack/query-async-storage-persister` sobre `expo-sqlite/kv-store` |
| Estado local | Zustand (sessão, preferências, conectividade) |
| Formulários | React Hook Form + Zod |
| Banco local | `expo-sqlite` (rascunhos, operações pendentes, metadados de sync) |
| Segredos | `expo-secure-store` (tokens, `deviceSecret`) |
| Tempo real | `@microsoft/signalr` |
| Outros | `expo-keep-awake`, `expo-audio` (aviso sonoro), `expo-notifications` (push dos pedidos do site, §8.6), `expo-image`, `expo-network`, `expo-application`, `expo-crypto` (`randomUUID`), `@shopify/flash-list`, `qrcode-generator` (QR da página de pedidos, JavaScript puro) |
| Testes | Jest + React Native Testing Library; fluxos E2E com Maestro em emulador (homolog) |

**Aparelho de referência** (RNF-001): Android de entrada, 2 GB de RAM, tela de 5,5", rede 4G instável. Toda tela de lista usa `FlashList`, imagens passam pelo `expo-image` com cache em disco, e nenhuma tela carrega mais de 100 itens sem paginação.

**Fora:** Axios (um `fetch` encapsulado basta), Redux, bibliotecas de i18n (textos pt-BR num único `strings.ts`), qualquer cálculo oficial de dinheiro no app.

## 8.2 Estrutura de pastas

```text
mobile/
  app/                                 # rotas (Expo Router) — só composição de tela
    _layout.tsx                        # Providers: SQLite, QueryClient(+persist), Session, Connectivity, Realtime, Theme
    update-required.tsx                # bloqueio 426
    (auth)/
      login.tsx  signup.tsx  forgot-password.tsx  reset-password.tsx
      invitation/[code].tsx
    (shared-device)/
      select-employee.tsx  pin.tsx
    (app)/
      _layout.tsx                      # guarda de sessão + AppHeader (menu) + ConnectivityBar + SubscriptionBanner
      (tabs)/
        _layout.tsx                    # 4 abas superiores filtradas por permissão
        marmitas.tsx  salao.tsx  pedidos.tsx  fechamento.tsx
      business-day/open.tsx
      orders/
        new/marmita.tsx  new/pf.tsx  new/kilo.tsx
        [id]/index.tsx  [id]/add-items.tsx  [id]/weighings.tsx  [id]/pay.tsx
        [id]/history.tsx  [id]/alerts.tsx  [id]/edit.tsx
      closing/finalize.tsx  closing/cash-movement.tsx  closing/history.tsx  closing/[id].tsx  closing/monthly.tsx
      menu/                            # telas do menu expansível (§8.7)
        profile.tsx  tenant.tsx  employees/index.tsx  employees/[id].tsx  employees/invite.tsx  roles.tsx
        products/index.tsx  products/[id].tsx  products/new.tsx  sizes.tsx
        menus/today.tsx  menus/weekly.tsx  menus/history.tsx  menus/[id].tsx
        customers/index.tsx  customers/[id].tsx
        pricing.tsx  payment-methods.tsx  audit.tsx  settings.tsx  subscription.tsx  drafts.tsx  devices.tsx
        storefront.tsx                 # página de pedidos (EP-002 F-210)
  src/
    api/          client.ts  errors.ts  headers.ts  endpoints/*.ts (um arquivo por recurso do 06)
    features/     auth/ orders/ production/ payments/ closing/ menu/ catalog/ customers/ employees/ sync/ realtime/ drafts/ storefront/ push/
                  (cada uma com hooks/, components/, schemas.ts)
    components/ui/   design system (§8.10)
    db/           schema.ts  migrations.ts  draftsRepo.ts  pendingOpsRepo.ts  metaRepo.ts
    lib/          money.ts  weight.ts  dates.ts  phone.ts  ids.ts  strings.ts
    stores/       session.ts  connectivity.ts  preferences.ts
```

**Regra:** arquivos em `app/` só compõem componentes e hooks de `src/features`. Nenhum `fetch`, cálculo ou regra em `app/` (`docs/02-padroes.md` §2.3).

---

## 8.3 Camada de API

### `src/api/client.ts`

`apiRequest<T>(method, path, { body, idempotencyKey, ifMatch, signal })`:

1. Monta a URL `{API_BASE_URL}/api/v1{path}`. Acrescenta `Authorization`, `X-App-Version` (`Application.nativeApplicationVersion`), `X-App-Platform`, `Idempotency-Key` e `If-Match: "{n}"` quando aplicáveis.
2. Timeout de **15 s** (`AbortController`). Um timeout **não** é tratado como falha: vira `NetworkUnknownError` (resultado desconhecido, §8.5).
3. `401 Unauthenticated` → `refreshSingleFlight()` (uma renovação por vez; as demais requisições esperam) → repete **uma** vez. Falhou de novo → `session.signOut('expired')`.
4. `426` → navega para `/update-required`. `402` → `session.markReadOnly()`.
5. Resposta não-2xx → `ApiError { status, errorCode, title, detail, errors, extensions, traceId }`, a partir do `ProblemDetails`.
6. Lê o `ETag` e o expõe como `version` no resultado (`{ data, version, replayed }`).

`src/api/endpoints/orders.ts` (exemplo do padrão):

```ts
export const ordersApi = {
  list: (f: OrderFilters) => apiRequest<Page<OrderSummary>>('GET', `/orders${qs(f)}`),
  get: (id: string) => apiRequest<Order>('GET', `/orders/${id}`),
  create: (body: CreateOrderBody, key: string) => apiRequest<Order>('POST', '/orders', { body, idempotencyKey: key }),
  addItems: (id: string, body: AddItemsBody, key: string, version: number) =>
    apiRequest<Order>('POST', `/orders/${id}/items`, { body, idempotencyKey: key, ifMatch: version }),
  // ... um método por rota de 06 §6.9
};
```

Os tipos (`Order`, `OrderSummary`, `Payment`…) ficam em `src/api/types.ts`, **escritos à mão a partir do 06**, com o mesmo nome de campo. Dinheiro chega como `string` e assim permanece no tipo (`MoneyString`).

### Chaves do TanStack Query

| Chave | Fonte | `staleTime` |
|---|---|---|
| `['me']` | `GET /auth/me` | 5 min |
| `['businessDay', 'current']` | `GET /business-days/current` | 1 min |
| `['settings']` | `GET /tenant/settings` | 10 min |
| `['products', filtros]`, `['marmitaSizes']` | catálogo | 10 min |
| `['menus', 'current', menuType]`, `['menus', id]`, `['menus', 'list', filtros]` | cardápios | 2 min |
| `['orders', 'list', filtros]`, `['orders', id]` | pedidos | 30 s |
| `['orders', 'list', 'awaiting']` | `GET /orders?acceptanceStatus=AWAITING_ACCEPTANCE` (faixa "Novos do site", ADR-0046); sob `['orders', 'list']`, invalidada junto | 15 s, `refetchInterval` 30 s |
| `['production', businessDayId]` | painel da cozinha | 15 s |
| `['orders', id, 'alerts' \| 'history' \| 'payments']` | detalhes | 30 s |
| `['customers', 'search', termo]`, `['customers', id]`, `['customers', id, 'pending']` | clientes | 1 min |
| `['closing', 'summary', businessDayId]`, `['closing', 'history', filtros]`, `['closing', id]`, `['closing', 'monthly', mês]` | fechamento | 15 s / 5 min |
| `['storefront']` | `GET /storefront` (página de pedidos, 06 §6.18). Fora do feed e sem evento: nenhum `entityType` a invalida; a resposta do `PUT` (ou o `current` do `409`) substitui o cache | 1 min |

**Persistência:** o cache persiste em SQLite (RN-014.2) com `maxAge` de 24 h, chaveado por `tenantId`. Trocar de tenant ou sair limpa o cache. Os dados persistidos aparecem com o selo "dados de HH:mm" quando estão offline (RN-014.5).

**Mapa `entityType` → invalidação** (06 §6.12), usado pelo feed e pelos eventos:

| entityType | Invalida |
|---|---|
| `ORDER` | `['orders', id]`, `['orders', 'list']`, `['production']`, `['customers']` (pendências) |
| `PAYMENT`, `PAYMENT_REVERSAL` | `['orders', orderId]`, `['orders', 'list']`, `['closing', 'summary']` |
| `MENU` | `['menus']` |
| `PRODUCT`, `MARMITA_SIZE`, `TENANT_SETTINGS` | `['products']`, `['marmitaSizes']`, `['settings']` |
| `CUSTOMER` | `['customers']` |
| `EMPLOYEE` | `['me']` (se for o próprio), `['employees']` |
| `BUSINESS_DAY`, `CASH_MOVEMENT`, `DAILY_CLOSING`, `CLOSING_ADJUSTMENT` | `['businessDay']`, `['closing']` |

---

## 8.4 Banco local (`src/db/schema.ts`)

```sql
CREATE TABLE IF NOT EXISTS meta (
  key   TEXT PRIMARY KEY,          -- 'schemaVersion', 'lastSeq:{tenantId}', 'lastSyncAt:{tenantId}'
  value TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS drafts (
  id          TEXT PRIMARY KEY,    -- uuid local
  tenant_id   TEXT NOT NULL,
  kind        TEXT NOT NULL,       -- NEW_ORDER_MARMITA | NEW_ORDER_PF | NEW_ORDER_KILO | ADD_ITEMS | PAYMENT
  target_id   TEXT,                -- orderId quando o rascunho é de um pedido existente
  form_json   TEXT NOT NULL,       -- estado do formulário (RHF) serializado
  updated_at  TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS pending_operations (
  idempotency_key TEXT PRIMARY KEY,
  tenant_id       TEXT NOT NULL,
  employee_id     TEXT NOT NULL,   -- quem enviou: GET /operations só enxerga as chaves do próprio usuário (06 §6.12)
  draft_id        TEXT,
  method          TEXT NOT NULL,
  path            TEXT NOT NULL,
  body_json       TEXT,
  if_match        INTEGER,
  status          TEXT NOT NULL,   -- SENDING | UNKNOWN | FAILED
  description     TEXT NOT NULL,   -- "Pedido de marmita — João — R$ 55,00" (exibido ao usuário)
  created_at      TEXT NOT NULL,
  last_error      TEXT
);
```

- **Rascunhos (RN-014.3):** o formulário salva em `drafts` a cada mudança, com *debounce* de 500 ms, e o rascunho é apagado só depois de o servidor confirmar. Reabrir uma tela de criação oferece "Continuar rascunho de 11:42?". Rascunhos com mais de 3 dias são descartados ao abrir o app.
- **Operações pendentes:** só existem para escritas cujo resultado ficou **desconhecido** (§8.5). Elas **não** formam uma fila offline: sem conexão, as escritas ficam desabilitadas (ADR-0010).
- Tudo fica escopado por `tenant_id`; as pendências também por `employee_id` (no aparelho compartilhado, cada pessoa vê e resolve só as suas).
- Enquanto houver pendência da mesma ação (mesmo rascunho ou, sem rascunho, mesmo método e caminho), uma nova confirmação **não envia nada**: o app volta a acompanhar a pendência existente.
- Sair da conta apaga rascunhos e pendências **só depois de avisar**, se houver algum.

## 8.5 Escritas críticas e resultado desconhecido (ADR-0010, ADR-0033, ADR-0034)

Todo endpoint [I] ou [M] do 06 passa pelo hook `useCriticalMutation`, com uma exceção:

> **Exceção — telas de gestão (decisão do usuário, 2026-10-27).** Produto, tamanho, cardápio, cliente, funcionário, convite, configurações e estabelecimento usam mutação simples do TanStack Query. Essas rotas são só [M] (nenhuma é [I] no 06), então o `If-Match` continua indo sempre e um reenvio com a mesma versão dá `409`, nunca duplica. O `409 VersionConflict` aparece como mensagem de erro (sem a folha de diferença), e um `MutationCache` global recarrega as consultas ativas para a próxima tentativa já ir com a versão nova. Se alguma dessas rotas passar a ser [I], ela volta para `useCriticalMutation`.

```ts
const mutation = useCriticalMutation({
  describe: (vars) => `Pagamento de ${formatBRL(vars.body.amount)} — pedido #${order.orderNumber}`,
  request: (vars, key) => paymentsApi.register(order.id, vars.body, key, order.version),
  onConfirmed: (result) => { /* atualiza o cache de ['orders', id] com result.order */ },
  draftId,
});
```

Fluxo (RF-012, RN-014.4/14.11/14.12):

```text
1. Usuário toca "Salvar"/"Receber" → ConfirmDialog com operação + valores (RN-012.1/12.2). Cancelar → nada é gravado (RN-012.4).
2. Confirmou → key = randomUUID() → INSERT pending_operations (SENDING)            [antes de enviar]
3. Envia.
   ├─ 2xx                → onConfirmed; DELETE pending; DELETE draft; toast "Pedido #12 registrado"
   ├─ 4xx de negócio     → DELETE pending; mantém o draft; mostra a mensagem do errorCode (§8.11); formulário volta editável
   ├─ 409 VersionConflict→ DELETE pending; ConflictSheet com `current` (o que mudou) → "Refazer sobre a versão atual"
   └─ rede/timeout/5xx   → status = UNKNOWN; banner "Não sabemos se o pagamento foi registrado. Verificando…"
4. UNKNOWN → resolveUnknown(key) imediatamente se online, e de novo a cada reconexão:
     GET /operations/{key}
     ├─ COMPLETED  → GET do resourceId → onConfirmed → limpa pending/draft
     ├─ IN_PROGRESS→ nova consulta em 2 s (até 5 vezes; depois mantém UNKNOWN)
     └─ NOT_FOUND  → status FAILED + botão "Reenviar" (mesma key, mesmo corpo). Nunca reenvia sozinho.
5. Pendência com mais de 72 h (TTL da idempotência): `NOT_FOUND` já não prova que ela não chegou, então o app
   **nunca a reenvia**. Ela fica pendente com o aviso "confira no histórico"; se não estiver lá, a pessoa refaz a
   operação (a partir do rascunho) e descarta a pendência.
   `409 OperationInProgress` é tratado como resultado desconhecido (resolve pelo passo 4).
```

**Toque duplo / Enter repetido:** `run` tem uma trava síncrona (ref): enquanto uma execução do mesmo hook não terminou, uma segunda chamada devolve `{ kind: 'ignored' }` sem enviar nada, e a tela não reage a ela (a primeira ainda vai devolver o desfecho). O `state` do React sozinho não basta, porque só muda no próximo render. Entre hooks diferentes (duas telas, "Reenviar"), procurar a pendência da mesma ação e gravar a nova é uma seção exclusiva em `runCritical`: a segunda chamada concorrente recebe `blocked` com a pendência da primeira.

**Enquanto uma operação estiver `UNKNOWN`/`FAILED`:**
- o pedido afetado mostra o selo "Pendente de confirmação";
- o botão da mesma ação fica bloqueado, o que impede pagamento em dobro (RN-009.9);
- a tela `menu/drafts` lista rascunhos e pendências.

**Versão (`If-Match`):** sempre a `version` do cache no momento da confirmação. Uma escrita bem-sucedida atualiza o cache com o recurso devolvido, de modo que a próxima escrita já usa a versão nova.

## 8.6 Conectividade, sincronização e tempo real

### `stores/connectivity.ts`

`expo-network` + `NetInfo` → `online | offline`. Uma falha de requisição por rede marca `degraded` por 10 s.

A `ConnectivityBar` (topo, sempre visível) mostra:
- **Sem internet:** "Mostrando dados de 11:42. Pedidos novos ficam como rascunho." (RN-014.1);
- **Reconectando…**
- **Sincronizado.**

### `features/sync/syncEngine.ts`

Disparado ao voltar online, ao trazer o app para primeiro plano, a cada reconexão do SignalR e por evento com salto de `seq` (com intervalo mínimo de 10 s entre execuções):

```text
1. refreshIfExpiring()                                    // renova só se o access token vence em < 5 min (SYNC_REFRESH_MARGIN_MS);
                                                          // com ele válido, as chamadas abaixo revalidam a sessão (token revogado → 401 → refresh)
2. para cada pending UNKNOWN → resolveUnknown()           // §8.5 passo 4
3. loop GET /sync/changes?since={lastSeq}&limit=500 até hasMore=false
     → invalida as chaves de cada change (mapa §8.3) → grava lastSeq em meta
   410 SyncCursorExpired → queryClient.removeQueries(tenant) → lastSeq = extensions.currentSeq
4. realtime.ensureConnected()
5. drafts.unlockSubmit()                                  // libera o envio dos rascunhos válidos
```

Os conflitos (RN-014.9/14.10) aparecem no envio, via `409 VersionConflict` (§8.5). O app nunca mescla automaticamente.

### `features/realtime/useRealtime.ts`

- `HubConnectionBuilder().withUrl('/hubs/realtime?appVersion=…', { accessTokenFactory })`, com reconexão automática (0, 2, 5, 10, 30 s). O `accessTokenFactory` é assíncrono e renova o token antes se ele vence em menos de 1 min (`refreshIfExpiring`, mesmo caminho único de renovação). Depois que a automática desiste, a própria conexão agenda novo `start` em 5, 15, 30 e 60 s (depois a cada 60 s), além de reconectar ao voltar online ou ao primeiro plano (§8.15, "Token do hub").
- O evento `event`:
  1. ignora `eventId` repetido (memória com as últimas 500 entradas);
  2. invalida pelo mapa de §8.3;
  3. `seq` acima de `lastSeq + 1` → agenda o sync, porque algumas lacunas são esperadas: o feed é filtrado por permissão;
  4. `actorEmployeeId === me.employee.id` → **não** toca som nem mostra toast (RN-008.6).
- `AccessRevoked` → tenta renovar o token. Falhou → sai, com o motivo.
- `SubscriptionStatusChanged` → invalida `['me']`.
- Só fica conectado com o app em primeiro plano. Em segundo plano desconecta, e ao voltar sincroniza (ADR-0036).

### Avisos na cozinha (ADR-0036)

Na aba Pedidos:
- `useKeepAwake()` mantém a tela ligada;
- com a preferência local "Som de pedidos" ligada, `OrderCreated` e `OrderAlertCreated` com `affectsProduction` tocam um som curto (`expo-audio`, volume respeitando o sistema);
- o card do pedido pisca uma vez.

Em qualquer aba (`features/orders/useWebOrderSound.ts`, montado no layout de `(app)`): `OrderAwaitingAcceptance` (pedido do site chegou, ADR-0046 item 23) toca o som próprio dos pedidos do site (`assets/sounds/web_order.wav`, o mesmo do canal de push `web-orders`, ADR-0049 item 7) para quem tem `orders.view`, com a mesma preferência "Som de pedidos". Os eventos `OrderAwaitingAcceptance`, `OrderAccepted`, `OrderRejected` e `OrderFulfillmentChanged` têm `entityType: ORDER`: o mapa de §8.3 já invalida `['orders', 'list']` (inclusive a faixa) e `['production']`, e o sync faz o mesmo pelo feed.

### Push dos pedidos do site (ADR-0049, EP-002 F-209)

Só o pedido do site aguardando aceite e o lembrete aos 3 min geram push (06 §6.3 "Push dos pedidos do site"); o resto continua só em primeiro plano (ADR-0036). O push é **aviso, não estado**: tocar abre o pedido e o app busca tudo pela API.

| Peça | Onde | Como |
|---|---|---|
| Módulo | `expo-notifications` (plugin no `app.json` com `sounds: ["./assets/sounds/web_order.wav"]`, que também põe o som no pacote iOS; `icon` = `android-icon-monochrome.png`, `color` `#B23C0B`) | Módulo nativo: **muda o fingerprint e exige um build novo do Dev Client/APK**. Expo → FCM/APNs; sem `eas init` + credenciais FCM/APNs não há token |
| Interface | `features/push/notifications.ts` (nativo) / `notifications.web.ts` (no-op, `supported: false`) / `notifications.types.ts` | `init`, `getPermission`, `requestPermission`, `getToken`, `onTokenChange`, `openSettings` |
| Canal e som | `notifications.init` na raiz (`usePushNotifications` em `app/_layout.tsx`) | Canal Android `web-orders` ("Pedidos do site", importância alta, som `web_order.wav`, visível na tela bloqueada), criado antes de qualquer pedido de permissão (o Android 13+ exige um canal). `assets/sounds/web_order.wav`: dois toques sintetizados (dó–sol, duas vezes, 1,5 s), original. O mesmo arquivo toca dentro do app no `OrderAwaitingAcceptance` (`useOrderSound('webOrder')`) |
| Primeiro plano | `setNotificationHandler` | Sempre suprime (sem faixa, lista nem som): o SignalR + o som já avisaram (item 9). A de outro estabelecimento também |
| Toque | `init` → `parseWebOrderPushData` → `usePushTarget` (memória) → `usePushTargetOpener` no layout de `(app)` | `data` só com `tenantId`, `orderId` (UUID) e `kind` conhecido; o resto é ignorado. Inclui o toque que abriu o app fechado (`getLastNotificationResponse`, depois `clearLastNotificationResponse`; a mesma notificação não abre duas vezes). Sessão do mesmo `tenantId` → `router.push('/orders/{orderId}')` depois de 400 ms (a navegação do login assenta). Sem sessão → a guarda leva ao login (ou à seleção + PIN) e o destino espera; ao entrar no mesmo estabelecimento, abre. Outro estabelecimento → descarta. Toque com mais de 30 min → descarta |
| Registro | `features/push/registration.ts` + `usePushRegistration` no layout de `(app)` | `PUT /devices/push-token` `{ installationId, expoPushToken, platform, webOrdersEnabled }` ao entrar (senha, convite, PIN, troca de estabelecimento), a cada abertura, na volta ao primeiro plano e quando o token muda (`addPushTokenListener`). Com os mesmos dados (tenant, funcionário, instalação, token, opção) há menos de 12 h, não repete. Uma sincronização por vez. Só com a permissão concedida; sem ela, se este aparelho tinha registrado no estabelecimento (`meta.pushRegisteredTenant`), manda `DELETE`. A resposta (sem o token) vai para `usePushStatus`. `409 PushTokenInUse` (o token está com outra instalação em outro estabelecimento, 06 §6.1): nenhum erro na tela, o estado local fica como está e a próxima abertura tenta de novo, sem o intervalo de 12 h |
| Sem `projectId` | `notifications.getToken` lê `expoConfig.extra.eas.projectId` (ou `easConfig.projectId`) | Ausente (o `eas init` não foi feito): não pede token nem registra, um `console.warn` só em `__DEV__` e a tela de Configurações diz o motivo |
| Sair | `confirmSignOut` (`AppShell.tsx`) | Aparelho pessoal: `DELETE /devices/push-token` (5 s, erro ignorado) **antes** do `POST /auth/logout`, que também apaga. Aparelho compartilhado: não (o registro é do aparelho e vale o último PIN). Troca de estabelecimento, `AccessRevoked` e revogação: o servidor apaga |
| Permissão | `WebOrderPushPrompt` (`features/push/components.tsx`) | Nunca na abertura do app (item 6). Para quem tem `orders.accept`, no **primeiro uso dos pedidos do site**: dentro da faixa "Novos do site" (Marmitas e Pedidos) e na tela "Página de pedidos" (`menu/storefront`). Primeiro a explicação ("Para não perder pedidos do site, permita as notificações…") com "Permitir avisos" (só então o pedido do sistema) e "Agora não" (`pushPromptDismissed`, esconde de vez; a opção fica em Configurações). Negada (ou sem poder perguntar de novo): uma linha discreta com "Configurações" (`Linking.openSettings`) e fechar |
| Preferência | `menu/settings` → seção "Pedidos do site com o app fechado" (`WebOrderPushSettings`) | Chave "Avisar pedidos do site com o app fechado" = `usePreferences.webOrdersPush` (`null` até mexer: vai `null` e o servidor mantém a dele, ligada no primeiro registro). Mudar grava no aparelho e manda o `PUT` (ligar sem a permissão ainda pedida mostra o pedido do sistema). Mostra a permissão, o motivo sem `projectId`, a falha do registro, "Seu perfil não aceita pedidos do site…" quando `receivesWebOrders = false`, a nota do aparelho compartilhado e a da economia de bateria (Xiaomi, Samsung) |
| Web | `notifications.web.ts` | Nada de push nesta fase (Web Push é P2, Q-26; a API recusa `WEB`). O bundle web não inclui o `expo-notifications`; Configurações explica que a aba aberta avisa pelo som e pelo título |

---

## 8.7 Sessão, entrada e dispositivo compartilhado (ADR-0018, ADR-0027)

### `stores/session.ts` (Zustand)

`{ status, tokens (só em memória; persistidos no SecureStore), me, sharedDevice?, readOnly }`.

`signIn(tokensMe)`, `signOut(reason)` (chama `/auth/logout`, limpa o cache do tenant, avisa se houver rascunhos ou pendências) e `switchTenant(id)`.

O `installationId` é gerado uma vez (`randomUUID`) e guardado no SecureStore.

### Telas de entrada

| Rota | Conteúdo | API | Observações |
|---|---|---|---|
| `(auth)/login` | Telefone ou e-mail + senha; "Esqueci a senha"; "Criar conta"; "Tenho um código de convite" | `POST /auth/login` | Envia `preferredTenantId` (último usado). `401` com `lockedUntil` → "Tente novamente às HH:mm" |
| `(auth)/signup` | 3 passos: **Você** (nome, telefone, e-mail, senha), **Estabelecimento** (nome, CPF/CNPJ, cidade/UF), **Termos** (aceite) | `POST /auth/signup` | Máscaras de CPF/CNPJ/telefone. `409 EmailAlreadyRegistered` → oferece "Entrar com essa conta" (`existingAccount`). `TrialAlreadyClaimed` → explica e mostra o contato do suporte. **Nenhum preço** (ADR-0017) |
| `(auth)/invitation/[code]` | Deep link `almoseis://convite/K7P3QZ` ou `https://almoseis.app/convite/…`; também acessível por "Tenho um código" | `GET /invitations/{code}` → `POST …/accept` | Mostra o estabelecimento e os perfis. Abas "Sou novo" / "Já tenho conta" |
| `(auth)/forgot-password`, `reset-password` | E-mail → "Se existir uma conta, enviamos um link". Código do proprietário + nova senha | `/auth/password/*` | O link do e-mail abre `reset-password?emailToken=` |
| `update-required` | Bloqueante: "Atualize o AlmoSeis para continuar" + botão da loja (`extensions.storeUrl`) | — | ADR-0035 |

### Dispositivo compartilhado

| Rota | Conteúdo | API |
|---|---|---|
| `menu/devices` | Quem tem `devices.manage`: "Usar este aparelho como compartilhado" (nome) e a lista de aparelhos com "Revogar" | `POST/GET /devices/shared`, `…/revoke` |
| `(shared-device)/select-employee` | Grade de nomes grandes, mostrada sempre que o aparelho compartilhado está sem sessão | `GET /devices/shared/{installationId}/employees` (`X-Device-Secret`, `X-Tenant-Id`) |
| `(shared-device)/pin` | Teclado numérico próprio (sem teclado do sistema) | `POST /auth/pin-login`. `401` mostra `attemptsLeft`; `423` mostra `lockedUntil` |

Ao registrar o aparelho, o `deviceSecret` vai para o SecureStore. O app entra em modo compartilhado e desloga a pessoa que fez o registro.

Em modo compartilhado:
- a inatividade de `sharedDeviceIdleSeconds` (de `/tenant/settings`) volta para a seleção de funcionário;
- o cabeçalho mostra o nome de quem está usando e o botão grande "Trocar";
- `/auth/me/pin` fica acessível no perfil de quem ainda não tem PIN.

---

## 8.8 Navegação principal

### `(app)/_layout.tsx`

- **Guarda:** sem sessão → `(auth)/login`, ou `(shared-device)/select-employee` em modo compartilhado.
- **`AppHeader`:** nome do estabelecimento; "Expediente 27/10 · aberto" ou "Sem expediente"; botão do menu expansível (`MenuSheet`); avatar com quem está logado.
- **`SubscriptionBanner`:**
  - `TRIALING` com até 2 dias ou `PAST_DUE` → aviso **sem preço nem link** (ADR-0017);
  - `canWrite = false` → "Modo somente leitura", e todas as ações de escrita desabilitadas, com explicação.

### Abas superiores (`(tabs)/_layout.tsx`), filtradas por permissão (RF-001)

| Aba | Aparece com | Sem expediente aberto |
|---|---|---|
| Marmitas | `orders.view` | Card "Abrir expediente" (`cash.open`) ou "Peça para o caixa abrir o expediente" |
| Salão | `orders.view` | idem |
| Pedidos | `production.view` | "Nenhum expediente aberto" |
| Fechamento | `closing.view` | Histórico de fechamentos + "Abrir expediente" |

A aba inicial é a primeira visível. O app lembra a última aba usada por aparelho, o que permite a cozinha abrir direto em Pedidos.

### Menu expansível (épico §2.1)

| Item | Rota | Permissão |
|---|---|---|
| Perfil do usuário | `menu/profile` (nome, PIN, trocar senha com `PUT /auth/me/password`, trocar de estabelecimento) | — |
| Dados do estabelecimento | `menu/tenant` | ver: `orders.view`; editar: `settings.manage` |
| Funcionários | `menu/employees` | `employees.manage` |
| Perfis e permissões | `menu/roles` (somente leitura, ADR-0028) | — |
| Pratos e bebidas | `menu/products`, `menu/sizes` | ver: `orders.view`; editar: `catalog.manage` |
| Cardápio do dia | `menu/menus/today` | `menu.manage` |
| Cardápio semanal | `menu/menus/weekly` | `menu.manage` |
| Histórico e reutilização de cardápios | `menu/menus/history` | `menu.manage` |
| Clientes | `menu/customers` | `orders.view` |
| Página de pedidos | `menu/storefront` (EP-002 F-210) | ver e compartilhar: `orders.view`; alterar: `storefront.manage` |
| Configuração de preços | `menu/pricing` (preço do quilo, taxa de entrega) | `settings.manage` |
| Formas de pagamento | `menu/payment-methods` | `settings.manage` |
| Histórico de operações | `menu/audit` | `audit.view` |
| Histórico de fechamentos | `closing/history` | `closing.view` |
| Configurações gerais | `menu/settings` (som, aviso dos pedidos do site com o app fechado, aparelho compartilhado, tamanho da fonte) | — |
| Aparelhos compartilhados | `menu/devices` | `devices.manage` |
| Rascunhos e pendências | `menu/drafts` | — |
| Assinatura e plano | `menu/subscription` | `subscription.view` |
| Encerrar sessão | ação | — |

---

## 8.9 Telas operacionais

Convenções:
- "Confirma" = `ConfirmDialog` com operação e valores (RF-012);
- [I]/[M] = passa por `useCriticalMutation` (§8.5);
- toda tela tem os estados *carregando* (skeleton), *vazio*, *erro* (com "Tentar de novo") e *offline* (dados do cache com o selo de horário).

### Aba Marmitas — `(tabs)/marmitas`

- **Dados:** `['orders', 'list', { orderType: 'MARMITA' }]` do expediente aberto (a lista comum só traz pedidos **aceitos**, padrão do `acceptanceStatus`), mais `['menus', 'current', 'MARMITA']` e a faixa `['orders', 'list', 'awaiting']`.
- **Layout:**
  - **faixa "Novos do site (n)"** no topo (abaixo);
  - filtro segmentado *Todos / A pagar / Entrega / Retirada*;
  - busca por nome;
  - cards `OrderCard` com número, cliente, entrega ou retirada, total, `PaymentBadge` (financeiro), `ProductionBadge` (produção), `SiteBadge` (pedido do site), `FulfillmentBadge` (atendimento) e o ponto de alerta; em entrega/retirada, o **próximo passo** do atendimento (abaixo);
  - FAB "Nova marmita".
- **Faixa "Novos do site"** (`NewWebOrdersSection`, ADR-0046 item 21, EP-002 RF-206). Sem pedidos aguardando, não ocupa espaço. Visível para `orders.view`; os botões só com `orders.accept` (sem ela: "Aguardando o balcão aceitar."). Um `WebOrderCard` por pedido:
  - selo "Site", entrega/retirada e a **contagem regressiva** até `acceptanceDeadline` ("Aceite em 9:41"; âmbar nos 3 min finais; "Prazo esgotado" no zero). A contagem usa o relógio do aparelho e é só aviso: os botões não somem no zero, quem decide é o servidor (`409 WebOrderExpired`);
  - cliente e total em destaque; itens, endereço, telefone com "telefone não verificado" (ADR-0044) e observação, lidos de `GET /orders/{id}` (a lista só traz `itemCount`);
  - a forma de pagamento que o cliente escolheu (`expectedPaymentMethod`/`changeFor`, ADR-0044 item 19, EP-002 fatia 2): "Vai pagar em dinheiro, troco para R$ 50,00", "Vai pagar em dinheiro", "Vai pagar com Pix", "Vai pagar com cartão". É só intenção: **não** é pagamento e não muda o `PaymentBadge`; o recebimento continua pelo "Receber". Com `riskFlag = CAPTCHA_UNAVAILABLE` (ADR-0045 item 12), a linha discreta em âmbar "Verificação anti-robô indisponível no envio" (`WebOrderPaymentInfo`, também no detalhe);
  - entrega com a taxa da cotação da página (`deliveryFeeSource = DISTANCE`, ADR-0048 item 20, EP-002 fatia 4): "Entrega · 4,5 km · taxa R$ 3,60" (`DistanceFeeLine`, também no detalhe). Com `MANUAL` ou `FIXED_DEFAULT` não há distância;
  - **"Aceitar"** → Confirma ("Aceitar pedido do site?", cliente, atendimento, total) com o tempo estimado opcional em chips (*Sem estimativa / 15 / 30 / 45 / 60 min*, "o cliente vê") → `POST /orders/{id}/accept` [I][M] com `If-Match` = versão mais nova entre lista e detalhe. Sucesso: toast "Pedido do site aceito: #15", e o pedido sai da faixa e entra na lista (e na cozinha, pelo `OrderCreated`);
  - **"Recusar"** → Confirma de perigo com o motivo em chips (Item acabou, Fora da área de entrega, Muitos pedidos agora, Estamos fechando, Pedido suspeito, Outro motivo), a mensagem (opcional, 5–200 caracteres; obrigatória em "Outro"), a faixa "O cliente verá esta mensagem." e a prévia do que o cliente lê (em "Pedido suspeito", o texto não é mostrado) → `POST /orders/{id}/reject` [M];
  - `409 VersionConflict`: o `current` vira o texto do toast ("Já aceito por Bruno.", "Já recusado por Carla.", "O prazo acabou, o pedido expirou.", "O cliente desistiu deste pedido.") e nada é gravado; `409 WebOrderExpired` → "O prazo acabou, o pedido expirou."; `409 OrderNotAwaitingAcceptance` → texto pela extensão `acceptanceStatus`. Em todos, a faixa e o detalhe são recarregados;
  - resultado desconhecido do aceite: a pendência fica, o diálogo mostra "Verificando se o aceite entrou…" e confirmar de novo não reenvia (§8.5).
  - **"Possível duplicado"** (ADR-0046 item 21) ainda **não** aparece: o contrato não traz como saber se o mesmo telefone tem outro pedido nos últimos 30 min.
- **Sem cardápio** (`404 MenuNotFound`): o `MenuMissingCard` oferece "Usar o de ontem", "Usar o da semana passada" (`POST /menus/{id}/clone`), "Usar modelo semanal" e "Montar agora" (`menu/menus/today`), conforme `sources` (RN-003.2).
- **Primeiro uso — "Prepare seu restaurante"** (`docs/10-ux.md` D1): sem expediente aberto e com o cadastro incompleto (nenhum tamanho de marmita ativo **ou** nenhum prato principal ativo), quem tem `catalog.manage` vê o `SetupChecklist` no lugar do `NoBusinessDayCard`. Passos, cada um com uma frase de explicação e o atalho:
  1. Tamanhos de marmita (`/marmita-sizes`, feito com ≥ 1 ativo) → `menu/sizes` — "O preço da marmita vem do tamanho: P, M, G.";
  2. Pratos principais (`/products`, feito com ≥ 1 `MAIN_DISH` ativo) → `menu/products`;
  3. Formas de pagamento (só conferir, sem marca de feito) → `menu/payment-methods`, com `settings.manage`;
  4. Abrir o expediente → `business-day/open`, liberado depois dos passos 1 e 2 e com `cash.open`.

  O cardápio do dia vem depois da abertura, pelo `MenuMissingCard`. Sem `catalog.manage`, a pessoa vê o `NoBusinessDayCard` de sempre. Não há rota nova: tudo usa leituras que o app já faz.

### Nova marmita — `orders/new/marmita`

Formulário de uma página, **marmita primeiro** (`docs/10-ux.md` D5). O rascunho é salvo automaticamente.

1. **Tamanho:** blocos grandes com o preço (`/marmita-sizes`). Tocar num tamanho abre o `MarmitaBuilderSheet` já com ele escolhido:
   - tamanho (pode trocar), prato(s) principal(is) e acompanhamentos (multi-seleção) do cardápio do dia, com os indisponíveis desabilitados e riscados;
   - adicionais;
   - quantidade (`QuantityStepper`) e observação;
   - com o botão desabilitado, uma linha diz o que falta ("Escolha o prato principal").

   Cada marmita vira uma linha editável e duplicável ("+1 igual").
2. **Bebidas:** grade de bebidas disponíveis com stepper.
3. **Cliente:**
   - `CustomerPicker`: busca por telefone ou nome (`/customers?search=`), mostra as pendências do cliente ("Deve R$ 35,00");
   - com cliente salvo escolhido: **"Repetir último pedido"** — `GET /orders?customerId=&allDays=true&orderType=MARMITA&limit=1` e `GET /orders/{id}`; as marmitas e bebidas viram linhas do formulário, sem o que não está no cardápio de hoje ou não existe mais (avisa o que ficou de fora). Os preços são os de hoje;
   - ou nome e telefone livres + "Salvar cliente" (`saveCustomer`).
4. **Atendimento:** Retirada / Entrega. Na entrega:
   - escolher um endereço salvo ou digitar um;
   - taxa de entrega pré-preenchida com o padrão, editável só com `orders.delivery-fee.edit`.
5. **Observações do pedido.**
6. **Resumo fixo no rodapé:** "Prévia: R$ 55,00", calculada em centavos com os preços do cache, e o botão "Registrar R$ 55,00" → Confirma → `POST /orders` [I].

Sucesso → vai para `orders/[id]`, com o toast "Pedido #12 registrado".

### Aba Salão — `(tabs)/salao`

- **Layout:** segmentado *PF / Quilo*, com uma lista de comandas abertas do expediente (`isPaid=false` primeiro, depois as quitadas recentes).
- **Card:** mesa ou comanda, número, total e saldo.
- **FABs:** "Nova mesa (PF)" e "Nova comanda (quilo)".

### Nova mesa PF — `orders/new/pf`

1. Mesa (`tableLabel`, teclado numérico) e comanda opcional.
2. Pratos do cardápio PF em grade (nome + `pfPrice`), com stepper, observação e adicionais.
3. Bebidas.
4. "Abrir mesa" → Confirma → `POST /orders` [I].

### Nova comanda de quilo — `orders/new/kilo`

1. Mesa e número da comanda física (`tabNumber`).
2. `WeightInput`: o usuário digita em kg com 3 casas ("0,452") e o app envia `weightGrams: 452`. A prévia por pesagem usa o preço/kg de `['settings']`. Sem preço configurado → aviso com link para `menu/pricing`.
3. Bebidas (opcional).
4. "Registrar comanda" → Confirma → `POST /orders` [I].

### Detalhe do pedido — `orders/[id]`

- **Cabeçalho:** "#12 · Marmita · Entrega" ("Pedido do site · Marmita · Entrega" enquanto não há número), cliente/mesa, telefone ("telefone não verificado" em pedido `WEB`), a forma de pagamento esperada, o aviso de anti-robô e "Entrega · 4,5 km · taxa R$ 3,60" (os mesmos da faixa), "por Ana" ou "pelo site" (`createdBy: null`), `SiteBadge`, `ProductionBadge`, `PaymentBadge` (só em pedido aceito) e `FulfillmentBadge` lado a lado, com formas e cores distintas (RNF-001).
- **Aceite (pedido do site, ADR-0046):** aguardando → card "Aguardando aceite" com a contagem e "Aceitar"/"Recusar" (os mesmos da faixa); aceito → faixa "Pedido do site aceito por Bruno às 12:03 · pronto em ~30 min"; recusado, expirado ou desistência → faixa com o desfecho ("Recusado por Carla: Item acabou — "A costela acabou."", "Ninguém aceitou no prazo", "O cliente desistiu") e "Não é venda". **Enquanto não é `ACCEPTED`, nenhuma escrita aparece** (itens, receber, editar, cancelar): o servidor responde `409 OrderNotAccepted` (item 4); só "Histórico" fica no menu.
- **Atendimento** (entrega/retirada aceitas e ativas, pedidos do site **e** do app, uso opcional, ADR-0046 itens 18 e 20): card com o status ("Pendente", "Pronto para retirar" — derivado de `isReadyForPickup` —, "Saiu para entrega · 12:15", "Entregue", "Retirado") e os botões de `features/orders/fulfillment.ts`:
  - entrega: "Saiu para entrega" → "Entregue"; com saída, também "Voltou sem entregar"; entregue: "Desfazer "Entregue"" (correção);
  - retirada: "Retirado"; retirado: "Desfazer "Retirado"";
  - produção não concluída: o avanço vira **"Concluir e marcar saída"/"Concluir e marcar retirado"** (`completeProduction: true`) para quem também tem `production.update`; sem ela, "fica disponível quando a cozinha concluir";
  - Confirma com "Atendimento: Pendente → Saiu para entrega" (e "Produção: Em produção → Concluído" no concluir e marcar) e a nota "O financeiro não muda" → `PATCH /orders/{id}/fulfillment` [M]. `409 ProductionNotCompleted` (a cozinha voltou o status) mostra a mensagem no diálogo e recarrega o pedido.
- **Depois da saída** (`OUT_FOR_DELIVERY`, `DELIVERED`, `PICKED_UP`): "Adicionar itens" some e a barra explica "O pedido já saiu: para incluir itens, crie outro pedido."; a tela de inclusão mostra o mesmo aviso e trava o envio; "Editar dados" não oferece trocar entrega ↔ retirada. Se a saída acontecer com a tela aberta, o `409 OrderAlreadyDispatched` mostra "Este pedido já saiu (ou foi retirado). Para incluir itens, crie outro pedido." (item 19).
- **Lista de itens:** composição, observação e adicionais.
  - Ações por item (`orders.edit`, pedido não pago): alterar quantidade/observação (`PATCH …/items/{itemId}` [M]) e remover (`DELETE` [M]). Ambas passam pelo Confirma.
  - Pesagens (quilo), com remover.
- **Bloco financeiro:** total, pago, saldo, lista de pagamentos (estornados riscados, com o motivo).
- **Barra de ações** (cada botão some sem permissão e fica desabilitado offline ou em somente leitura):
  - "Adicionar itens" → `orders/[id]/add-items` (bebidas, marmitas/PFs, adicionais em linha existente) → `POST …/items` [I][M], com várias inclusões numa só confirmação (RN-012.3);
  - "Pesagem" (quilo) → `orders/[id]/weighings` → `POST …/weighings` [I][M];
  - "Receber" → `orders/[id]/pay`;
  - "Editar dados" → `orders/[id]/edit` (`PATCH /orders/{id}` [M]);
  - "Histórico" → `orders/[id]/history`;
  - menu ⋯: "Cancelar pedido" (`orders.cancel`, motivo obrigatório; com pagamentos, explica que é preciso estornar antes).
- **Pedido pago:** itens e valores ficam em modo leitura, com a faixa "Pedido quitado. Para corrigir, estorne um pagamento."
- **Alertas:** com `pendingAlertCount > 0`, a faixa "Este pedido mudou" leva a `orders/[id]/alerts`, que mostra as mudanças (`summary`/`details`) e o botão "Ciente" (`POST …/alerts/acknowledge`).

### Receber — `orders/[id]/pay`

- **Saldo em destaque.** Formas de pagamento habilitadas (`['settings']`) em botões grandes.
- **Valor:** `MoneyInput`, pré-preenchido com o saldo.
- **Dinheiro:** campo "Recebido" opcional, com atalhos "Exato" e as três próximas cédulas acima do valor (de R$ 5, 10, 20, 50, 100, 200), e o troco em destaque ("Troco R$ 15,00").
- **"Dividir conta":**
  - *Igual entre N pessoas*: gera N parcelas (a última absorve os centavos);
  - *Por itens*: soma os itens marcados.

  Cada parcela vira um pagamento separado com `label` "Pessoa N", confirmado um a um. Cada um é uma `useCriticalMutation` própria e usa a versão devolvida pelo anterior.
- "Confirmar recebimento" → Confirma ("Receber R$ 35,00 em dinheiro do pedido #12? Troco R$ 15,00") → `POST /orders/{id}/payments` [I][M].
- **Pedido de outro dia** (fiado, RF-011): a confirmação avisa "Pedido de 26/10. O recebimento entra no caixa de hoje."
- **Estornar** (lista de pagamentos, `payments.reverse`): motivo → Confirma → `POST /payments/{id}/reverse` [I][M].

### Aba Pedidos (produção) — `(tabs)/pedidos`

- **Dados:** `['production', businessDayId]` (`GET /production/orders`).
- **Layout:** três colunas no tablet (largura ≥ 720 dp); no celular, um segmentado **Abertos (n) / Em produção (n) / Concluídos (n)** mostra um grupo por vez (`docs/10-ux.md` D7). Cada grupo vai do mais antigo para o mais novo (RN-007.4/7.5).
- **`ProductionCard`:**
  - número grande, rótulo (cliente/mesa), há quanto tempo foi criado ("há 12 min");
  - itens com composição e observações em destaque;
  - selo de alerta, que expande as mudanças e permite dar ciência ali mesmo.
- **Ação:** um botão de avanço ("Iniciar" → "Concluir"), e "Voltar status" no menu do card. Ambos passam pelo Confirma (RN-007.3) e chamam `PATCH /orders/{id}/status` [M].
- **Pedidos do site e atendimento (ADR-0046):** o painel só traz pedidos aceitos, ordenados pelo aceite. No topo, a faixa "Novos do site (n)" **recolhida** (só a contagem; "Ver" abre os cards; a cozinha não tem `orders.accept` e vê "Aguardando o balcão aceitar."). O card leva o selo "Site" e o selo de atendimento; nos **concluídos** de entrega/retirada, o avanço do atendimento ("Saiu para entrega", "Entregue", "Retirado") aparece no próprio card, com Confirma; as voltas ficam no detalhe.
- **Comportamento da aba:** `useKeepAwake`, som (§8.6) e fonte maior, ajustável em `menu/settings`.

### Aba Fechamento — `(tabs)/fechamento`

- **Dados:** `['closing', 'summary', current]`.
- **Layout** (as tabelas do épico RF-010 viram cards):
  1. **Por modalidade:** Marmitas / PF / Quilo / **Total**, com Vendido, Recebido e Pendente;
  2. **Por forma de pagamento:** Dinheiro / Pix / Cartão, com estornos;
  3. **Caixa físico:** saldo inicial, recebido em dinheiro, suprimentos, retiradas, estornos em dinheiro, **esperado**;
  4. "Recebido de dias anteriores", Cancelamentos e Estornos;
  4a. **Pedidos do site e entregas** (só quando algum número não é zero; informativo, fora do vendido, RF-210, ADR-0046 itens 13 e 20): aguardando aceite (`webOrders.awaitingAcceptanceCount`, só no parcial), recusados ao fechar (`autoRejectedCount`, só no finalizado), não aceitos (`notAcceptedCount`) e "N entregas sem marcação de entregue" (`unmarkedDeliveryCount`);
  5. Botões: "Suprimento/Retirada" → `closing/cash-movement` ([I], motivo obrigatório); "Fechar expediente" → `closing/finalize` (`closing.finalize`); "Histórico".
- O resumo é atualizado por `ClosingUpdated`, ou puxando para baixo.

### Fechar expediente — `closing/finalize`

- **Passo 1, conferir:** o resumo, em modo leitura, e um aviso se existirem pedidos pendentes ("3 pedidos ficarão pendentes: R$ 220,00") e outro se houver pedidos do site aguardando aceite ("2 pedidos do site aguardando aceite serão recusados.", ADR-0046 item 13). O fechamento não fica bloqueado por eles; o mesmo aviso se repete no Confirma.
- **Passo 2, contar o dinheiro:** "Quanto há no caixa?" (`MoneyInput`), com uma calculadora opcional de cédulas e moedas que soma quantidades por valor de face. Mostra a diferença em tempo real, com cor e texto ("Faltam R$ 10,00").
- **Diferença ≠ 0:** justificativa obrigatória, com no mínimo 5 caracteres (RN-010.13).
- "Fechar expediente" → Confirma (valores e diferença) → `POST /closing/finalize` [I][M].
- `409 VersionConflict` → "Houve movimentação enquanto você conferia" → recarrega o resumo e volta ao passo 1.
- **Sucesso:** tela de comprovante (`closing/[id]`) e "Abrir próximo expediente".

### Abrir expediente — `business-day/open`

- **Saldo inicial**, sugerido com o dinheiro contado no último fechamento, se houver.
- "Abrir" → Confirma → `POST /business-days` [I].
- `409 BusinessDayClosedToday` → "Já houve um expediente hoje. Abrir outro?" → reenvia com `confirmSameDate: true` e uma **key nova**.
- **Sucesso:** informa os cardápios materializados ("Cardápio de marmitas criado a partir do modelo de terça").

### Histórico — `closing/history`, `closing/[id]`, `closing/monthly`

- **Lista:** fechamentos por data (`GET /closing/history`), com o total vendido e a diferença de caixa (vermelho quando ≠ 0).
- **Detalhe:** o mesmo layout do resumo, em modo leitura, com ajustes posteriores e o botão "Registrar ajuste" (`closing.adjust`, `POST /closing/{id}/adjustments` [I]).
- **Mensal:** seletor de mês, com totais e a lista por dia.

---

## 8.10 Telas de gestão (menu)

| Rota | Lista / formulário | API |
|---|---|---|
| `menu/products` | Abas Pratos / Acompanhamentos / Bebidas / Adicionais; busca; toggle de disponibilidade na própria linha (`PATCH` [M]) | `GET/POST/PATCH /products` |
| `menu/products/new`, `[id]` | Nome, tipo (bloqueado após venda), categoria, preço (regras por tipo, ADR-0020), "vai para a cozinha?", **"Aparece na página"** (`isVisibleOnPage`, só na edição de bebida e adicional, ADR-0044 item 5: vai no `PATCH`; o `POST` não tem o campo e o produto nasce visível; pratos e acompanhamentos mostram a nota "aparecem quando estão no cardápio do dia"; na lista, "· fora da página"), foto (câmera/galeria → `expo-image-manipulator` reduz para 1024 px JPEG 80% → `image-upload` → `PUT` no R2 → `PATCH imageStorageKey`) | |
| `menu/sizes` | Tamanhos P/M/G com preço e ordem | `/marmita-sizes` |
| `menu/menus/today` | Duas abas (Marmita / PF): itens do dia em seções Pratos e Acompanhamentos; "+ Adicionar do catálogo"; **"+ Novo prato"** abre `products/new` em modal e volta com o prato já incluído (RN-002.4/3.7); toggle "acabou" (`PATCH /menus/{id}/items/{itemId}`); preço de PF editável | `GET /menus/current`, `PUT …/items`, `POST /menus` |
| `menu/menus/weekly` | 7 dias × 2 tipos; cada célula abre o editor do modelo | `GET /menus?kind=WEEKLY_TEMPLATE`, `POST /menus` |
| `menu/menus/history` | Dias anteriores; "Usar para hoje/amanhã" e "Salvar como modelo de quarta" | `GET /menus?kind=DAILY`, `POST /menus/{id}/clone` |
| `menu/customers`, `[id]` | Busca; detalhe com endereços, pendências ("Receber" leva ao pedido) e "Anonimizar" (`settings.manage`, com confirmação forte digitando o nome) | `/customers/*` |
| `menu/employees` | Lista ativa/inativa; detalhe com perfis (multi-seleção), desativar/reativar, "Gerar código de redefinição de senha" | `/employees/*` |
| `menu/employees/invite` | Perfis → `POST /employees/invitations` → tela com código grande, link e botão "Enviar pelo WhatsApp" (`Share.share` com texto pronto); lista de convites pendentes com revogar | `/employees/invitations*` |
| `menu/roles` | O que cada perfil pode fazer, em linguagem simples | `GET /roles` |
| `menu/pricing`, `menu/payment-methods` | Preço do quilo, taxa padrão, formas habilitadas | `PATCH /tenant/settings` [M] |
| `menu/tenant` | Dados do estabelecimento | `GET/PATCH /tenant` |
| `menu/audit` | Filtros (tipo, funcionário, período); linhas "Ana estornou R$ 35,00 do pedido #12 — 28/10 10:05" | `GET /audit-logs` |
| `menu/subscription` | Situação, dias restantes e texto de orientação. **Sem preço, plano ou link** (ADR-0017) | `GET /subscription` |
| `menu/drafts` | Rascunhos (continuar / descartar) e pendências (verificar / reenviar / descartar com confirmação) | SQLite + `GET /operations/{key}` |
| `menu/settings` | Som de pedidos, tamanho da fonte da cozinha, "Avisar pedidos do site com o app fechado" (§8.6 "Push dos pedidos do site"), modo compartilhado, versão do app | local; a chave do push vai no `PUT /devices/push-token` |
| `menu/storefront` | **Página de pedidos** (EP-002 F-210, RF-205, US-208/US-209). Ver abaixo | `GET/PUT /storefront` [M] |

**`menu/storefront` — Página de pedidos** (`features/storefront`, ADR-0044, ADR-0048; tela de gestão, §8.5 exceção):

- **Estado** (`status` do `GET`) num `Banner`: Aberta / Pausada / Fechada agora (sem expediente; atalho "Abrir expediente" com `cash.open`) / Desativada (página inativa) / Indisponível (página ativa, mas a assinatura não permite: texto de orientação, sem preço, ADR-0017).
- **Chaves rápidas** (só com `storefront.manage`): "Página ativa" (o texto de apoio segue o valor salvo: "Ativa: quem abre o link vê o cardápio do dia e pode pedir." / "Desativada: o link mostra “indisponível”."), "Pausar pedidos pelo site" e, com `offersDelivery`, "Pausar entrega" (`isDeliveryEnabled`, RN-205.2: a página fica só com retirada). Cada uma passa por `confirmAction` e manda o **objeto inteiro do servidor** com só a chave trocada (`isPageActive` / `isAcceptingOrders` / `isDeliveryEnabled`), com `If-Match`. Travadas enquanto o formulário tem alteração não salva ("Salve ou desfaça as alterações abaixo…"), para nunca gravar edição pela metade. Ativar sem endereço completo ou sem tamanho de marmita ativo volta `422` em `isPageActive`; a mensagem do servidor aparece logo abaixo das chaves.
- **Link** `{EXPO_PUBLIC_PUBLIC_PAGE_BASE_URL}/{slug}` (padrão `http://localhost:3000` só em `__DEV__`; fora dele, sem a variável, a tela diz que o endereço não está configurado e não mostra link nem QR: o domínio é lacuna do ADR-0051 item 8). "Compartilhar link" (`lib/share.ts`: `Share` no celular; `share.web.ts`: `navigator.share`, senão copia e mostra "Link copiado"). "Mostrar QR code": `QrCode` (`qrcode-generator`, JavaScript puro, sem módulo nativo e sem novo build do Dev Client), preto no branco com margem de 4 módulos, inclusive no tema escuro; na web, "Imprimir QR code" abre uma folha com nome, QR (SVG) e link (`printQr.web.ts`); no celular, a orientação é mostrar no balcão ou tirar um print.
- **Formulário** (um "Salvar" para tudo, `PUT /storefront` [M] com o objeto inteiro): slug (normalizado ao digitar: minúsculas, sem acento, espaço vira hífen; formato conferido na tela, reservados e em uso só o servidor sabe), com a prévia do link e os links antigos (`previousSlugs`); trocar o slug pede confirmação ("o antigo continua levando para a página; depois de 5 trocas, só pelo suporte"); nome público; telefone de contato (máscara BR → E.164); horário (texto livre, "só informativo"); endereço (rua, número, complemento, bairro, cidade, UF, CEP com máscara); "Retirada no balcão"; "Entrega" (`offersDelivery`) e, ligada, a regra da taxa (ADR-0048): segmentado *Por distância* / *Taxa fixa* (`feeMode`); por distância, "Preço por km" (`MoneyInput`, zero vai `null`), "Taxa mínima" e "Raio máximo em km (opcional)" (digitado "6" ou "6,5", enviado `"6.0"`/`"6.5"`, vazio = `null`); taxa fixa mostra a `defaultDeliveryFee` de `['settings']` ("a taxa padrão de Configuração de preços", sem mapa). Erros `422` aparecem no campo (`errors` com as chaves `slug`, `publicName`, `contactPhone`, `address.state`, `delivery.maxDistanceKm`…). **O formulário nunca manda `location`**: sem o campo, o servidor mantém o ponto salvo; mudar o `address` o descarta e a API geocodifica de novo (06 §6.18).
- **Entrega pela página** (`pageDelivery`, do que está salvo): disponível → "A página oferece entrega agora."; senão, `missing` em linguagem simples, numa frase: "Para entregar pela página: ligue a entrega, informe o preço por km e confirme o ponto do restaurante." (`DELIVERY_OFF`, `PRICE_PER_KM`, `CONFIRMED_LOCATION`), e `MAPS_PROVIDER` à parte: "Cálculo de distância não configurado no servidor." Salvar com algo faltando não é erro.
- **Ponto do restaurante** (só com entrega por distância; ADR-0048 item 3). **Sem mapa nativo** (`react-native-maps` mudaria o fingerprint e exigiria novo build): as coordenadas com 6 casas e o selo "não confirmado"/"confirmado"; **"Ver no mapa"** abre `https://www.google.com/maps/search/?api=1&query=lat,lng` com `Linking.openURL` (navegador ou app de mapas; na web, nova aba); **"Confirmar este ponto"** (`confirmAction`) manda o objeto inteiro do servidor com `location: { latitude, longitude, isConfirmed: true }`. Sem ponto (endereço incompleto, não encontrado ou provedor desligado): orientação para salvar o endereço completo. **Modo avançado** "Corrigir o ponto à mão": latitude e longitude (ponto ou vírgula, arredondadas a 6 casas, −90…90 / −180…180), "Ver este ponto no mapa" e "Usar este ponto" (grava confirmado). Como as chaves rápidas, travado enquanto o formulário tem alteração não salva.
- **Conflito:** as mutações ficam no componente da tela, fora do formulário (que remonta a cada versão), para o aviso sobreviver à recarga. `409 VersionConflict` → o `current` entra no cache, o formulário volta com o que está no servidor e a faixa diz "Outra pessoa alterou a página enquanto você editava. Nada foi gravado…". Nunca reenvia sozinho.
- **Modo leitura** (só `orders.view`, ou assinatura em somente leitura): estado, link, compartilhar e QR; campos travados, sem chaves e sem "Salvar".

---

## 8.11 Design system (`src/components/ui`)

| Componente | Regra |
|---|---|
| `Button` | Altura ≥ 48 dp; variantes primária/secundária/perigo; estado `loading` bloqueia toque duplo |
| `ConfirmDialog` | Título com o verbo da ação, lista "o que vai acontecer" e valores; botão de confirmar com o verbo ("Receber R$ 35,00"), nunca "OK" |
| `MoneyText` | Recebe `MoneyString`, formata `R$ 1.234,56` (`Intl.NumberFormat('pt-BR')`) |
| `MoneyInput` | Teclado numérico; digitação da direita para a esquerda em centavos ("3550" → R$ 35,50); devolve `MoneyString` |
| `WeightInput` | kg com 3 casas → `weightGrams` inteiro; limite 50 kg |
| `QuantityStepper` | − / número / +, alvos de 48 dp |
| `ProductionBadge` | Forma de **pílula** com ícone: Aberto (relógio, cinza), Em produção (panela, âmbar), Concluído (check, verde) |
| `PaymentBadge` | Forma **quadrada** com ícone: A pagar (vermelho), Parcial ("R$ 20 de R$ 55", azul), Pago (cifrão, verde). Distinto do de produção mesmo para daltônicos (RNF-001) |
| `SyncStatePill` | Rascunho / Enviando / Pendente de confirmação / Confirmado (RN-014.5) |
| `AlertDot` | Ponto laranja com número; `accessibilityLabel` "1 alteração não vista" |
| `SiteBadge` | Etiqueta (raio 8, sem borda) "Site" com globo, em `primaryBg`: pedido com `origin = WEB` (ADR-0046 item 21) |
| `FulfillmentBadge` | **Pílula com contorno tracejado**, a terceira forma (produção = pílula, financeiro = quadrado): "Pronto para retirar" (derivado), "Saiu para entrega", "Entregue", "Retirado". Pendente não tem selo: a marcação é opcional |
| `ConnectivityBar`, `SubscriptionBanner`, `ConflictSheet`, `EmptyState`, `ErrorState`, `Skeleton`, `BottomSheet`, `Chip`, `SegmentedControl`, `PermissionGate` | `PermissionGate permission="payments.register"` esconde o filho; com `fallback="disabled"`, mostra desabilitado com explicação |

**Tema:** tokens de cor com modo claro e escuro; contraste AA. O modo segue o sistema (ou o navegador) até alguém tocar no botão de sol/lua do cabeçalho, ao lado do perfil: a escolha (`themeMode`: `system`/`light`/`dark`) fica nas preferências do aparelho e vale para quem usar o aparelho ou o navegador. `useColorMode()` decide o modo, `useColors()` devolve a paleta, e o `ThemeProvider` da raiz pinta o fundo das abas e das telas com a mesma paleta; fonte do sistema escalável (respeita a acessibilidade do Android), com limite de escala nos cards da cozinha. Todo ícone de ação tem `accessibilityLabel`.

**Tokens (`theme.ts`, desde 2026-10-28, `docs/10-ux.md` §10.4):** primária `#B23C0B` com `primaryBg` para seleção; fonte 14/16/18/22/28 e `display` 36 (saldo e nº do pedido na cozinha), números tabulares em `title`/`big`/`display`; espaçamento 4/8/12/16/24/32; raio 8/12/20; `TOUCH` 48 em botão, chip, segmentado e item de menu, e `TOUCH_LARGE` 56 no botão principal do rodapé. Chip selecionado = fundo `primaryBg`, borda 2 e ícone de check.

### Mensagens por `errorCode` (`lib/strings.ts`)

| errorCode | Mensagem / comportamento |
|---|---|
| `BusinessDayRequired` | "Abra o expediente para registrar pedidos e pagamentos." + atalho |
| `OrderAlreadyPaid` | "Este pedido já foi quitado. Para corrigir, estorne um pagamento." |
| `OrderHasPayments` | "Estorne os pagamentos antes de cancelar." |
| `PaymentExceedsBalance` | "O valor passa do saldo (R$ {balance})." + corrige o campo |
| `TotalBelowPaidAmount` | "O total ficaria menor que o já pago." |
| `ProductUnavailable` | "{produto} não está disponível hoje." + atualiza o cardápio |
| `KiloPriceNotConfigured` | "Configure o preço do quilo." + atalho |
| `PermissionDenied` | "Você não tem permissão para isso. Peça a um gerente." |
| `SubscriptionInactive` | Ativa o modo somente leitura e o banner |
| `ValidationFailed` | Mensagens de `errors` nos campos correspondentes |
| `VersionConflict` | `ConflictSheet` (§8.5); no aceite/recusa do pedido do site, toast com o `current` ("Já aceito por Bruno.") |
| `OrderNotAccepted` | "Este pedido do site ainda não foi aceito." |
| `OrderNotAwaitingAcceptance` | Pela extensão `acceptanceStatus`: "Este pedido do site já foi aceito." / "…já foi recusado." / "O cliente desistiu deste pedido." / "O prazo acabou, o pedido expirou." |
| `WebOrderExpired` | "O prazo acabou, o pedido expirou." |
| `OrderAlreadyDispatched` | "Este pedido já saiu (ou foi retirado). Para incluir itens, crie outro pedido." |
| `ProductionNotCompleted` | "A cozinha ainda não concluiu este pedido. Use "Concluir e marcar" ou espere a conclusão." |
| `TooManyRequests` | "Muitas tentativas. Aguarde {Retry-After} s." |
| outros / 5xx | "Algo deu errado. Código: {traceId curto}." (o `traceId` vai para o suporte) |

---

## 8.12 Utilitários de domínio (`src/lib`)

| Arquivo | Funções | Regra |
|---|---|---|
| `money.ts` | `toCents(MoneyString): number`, `fromCents(number): MoneyString`, `formatBRL`, `sumCents`, `splitEvenly(totalCents, n)` | **Nunca** `parseFloat` em dinheiro: o parse é por string (`"35.50"` → `3550`). A divisão distribui o resto em centavos nas últimas parcelas |
| `weight.ts` | `kgInputToGrams("0,452") → 452`, `gramsToKgLabel(452) → "0,452 kg"`, `previewKiloAmountCents(grams, pricePerKg)` | Mesma regra do ADR-0030, só com inteiros: `Math.round(grams * pricePerKgCents / 1000)` (metade para cima, que para valores positivos equivale a `AwayFromZero`). É só prévia: o valor oficial vem da API |
| `dates.ts` | `formatBusinessDate`, `relativeTime`, `toLocal(instant, timeZone)` | Datas operacionais (`"2026-10-27"`) **não** passam por fuso. Instantes UTC usam o `tenant.timeZone` |
| `phone.ts` | `maskBR`, `toE164("(34) 99999-1234") → "+5534999991234"` | |
| `ids.ts` | `newKey()` = `Crypto.randomUUID()` | |

---

## 8.13 Testes

| Tipo | Escopo |
|---|---|
| Unitários (Jest) | `money.ts` (inclusive `splitEvenly` e a ausência de float), `weight.ts` (mesmos casos do teste de DDL: 452 g × 59,90 = 27,07; 250 g × 1,42 = 0,36), parsers de `ProblemDetails`, mapa entityType → chaves |
| Hooks (RNTL + fetch simulado) | `useCriticalMutation`: sucesso, 4xx, 409 com `current`, timeout → `UNKNOWN` → `/operations` `COMPLETED`/`NOT_FOUND`; `syncEngine` com `410`; refresh single-flight com 3 requisições paralelas recebendo `401` |
| Componentes (RNTL) | `ConfirmDialog` (cancelar não chama a API), `MoneyInput`, `PermissionGate`, `OrderCard` com os dois badges |
| Pedidos do site (ADR-0046) | `webOrders.test.ts`: botões de atendimento por tipo/status/permissão, "Concluir e marcar", `isDispatched`, contagem regressiva, texto do conflito pelo `current`, validação da recusa, título da aba, avisos do fechamento, mensagens dos `errorCode`s novos. `webOrderFlow.test.tsx` (fetch simulado, exemplos literais do 06): faixa com prazo, itens, endereço e "telefone não verificado"; cozinha sem botões; aceite com `If-Match` + `Idempotency-Key` + tempo estimado; `409` "Já aceito por Bruno"; `WebOrderExpired`; rede caindo → pendência `UNKNOWN` e nenhum reenvio; recusa com motivo, "Outro" obrigatório e sem `Idempotency-Key`; "Concluir e marcar saída" com `completeProduction`; `ProductionNotCompleted`; selos "Site"/atendimento no `OrderCard`; "Pronto para retirar"; fechamento com os pedidos do site |
| Página de pedidos (EP-002 F-210) | `storefront.test.tsx` (fetch simulado, exemplo do 06 §6.18): slug normalizado e formato; chave rápida com o objeto inteiro e só a chave trocada; corpo do formulário (E.164, UF, vazios → `null`, taxa intocada); textos do estado; QR com os padrões de posição; ativar sem endereço → `PUT` com `If-Match` e o motivo do `422` na tela; pausar → `isAcceptingOrders: false` e "Pausada"; `409` → aviso, formulário recarregado, sem reenvio; chaves travadas com edição pendente; modo leitura com compartilhar; `share.web.ts` (copiar sem `navigator.share`, fechar a folha não copia); `isVisibleOnPage` no `PATCH` da bebida; "Vai pagar…" e o aviso de `riskFlag`. Fatia 4: texto do `pageDelivery.missing` (e `MAPS_PROVIDER` à parte); raio e coordenadas digitados; link do Google Maps; texto de "Página ativa" depois de ativar (defeito corrigido); "Ver no mapa" e "Confirmar este ponto" (`location` confirmado no objeto inteiro, `If-Match`); correção à mão; salvar a regra da taxa sem `location` no corpo; taxa fixa sem o ponto; "Entrega · 4,5 km · taxa R$ 3,60" |
| Push dos pedidos do site (EP-002 F-209) | `push.test.tsx` (`expo-notifications` e `expo-constants` simulados, exemplo literal do 06 §6.3): leitura do `data` (só `kind` conhecido e `orderId` UUID); decisão do toque (sem sessão espera, outro tenant descarta, toque velho descarta); corpo do `PUT` e resposta sem token; sem repetir com os mesmos dados e de novo com outro funcionário (PIN) ou token novo; sem permissão não registra e apaga o registro anterior; sem `projectId` não pede token, avisa uma vez e a tela diz o motivo; chave "Avisar pedidos do site" no `PUT`; `receivesWebOrders = false`; registro ao entrar e na troca de token; `DELETE` no "Encerrar sessão"; explicação antes do pedido do sistema, "Agora não", negada com atalho para as configurações, sem `orders.accept` não aparece; primeiro plano suprimido e canal `web-orders` com o som; toque abre o pedido, outro tenant ignorado, app fechado sem sessão abre depois do login, a mesma notificação uma vez só; `notifications.web.ts` no-op. **O push de verdade não foi testado** (exige `eas init`, credenciais FCM/APNs e o build novo) |
| E2E (Maestro, emulador Android de entrada) | 1) cadastro → abrir expediente → marmita com bebida → receber em dinheiro com troco → fechar com diferença e justificativa; 2) desligar a rede no meio do "Receber" → religar → app resolve sem duplicar; 3) PIN no aparelho compartilhado + inatividade |

Critério de pronto do mobile (`docs/02-padroes.md` §2.6): fluxos E2E 1 e 2 passando em homolog antes de cada release para as lojas.

---

## 8.14 Notas de implementação (aprendidas no código)

| Tema | Como ficou | Por quê |
|---|---|---|
| Versões | Expo SDK 58, React Native 0.88, React 19.3, TypeScript 6 (`types: ["jest","node"]` no `tsconfig`, porque o TS 6 não inclui mais `@types/*` sozinho). **Node ≥ 22.13** | Mínimo do SDK 58; o Node 21 não instala |
| Abas superiores | `TopTabs` de `expo-router/js-top-tabs` (já vem no Expo Router), filtradas com `TopTabs.Protected guard={permissão}` | Dispensa `@react-navigation/material-top-tabs` + `withLayoutContext`; aba sem permissão some do navegador |
| Rotas | Cada arquivo em `app/` só reexporta uma tela de `src/features/*/screens`; o ESLint proíbe `fetch` em `app/` | Regra de 8.2 garantida por ferramenta |
| Escritas [I] | `src/api/critical.ts` descreve cada uma como dado (`{method, path, body, ifMatch}`); a pendência guarda exatamente isso e o reenvio repete chave e corpo | Única fonte desses caminhos; reenvio idêntico ao envio original |
| Escritas só [M] do pedido | Status, alterar/remover item, remover pesagem, editar dados e cancelar usam `useCriticalMutation` com `idempotent: false`: sem `Idempotency-Key` e sem pendência. Falha de rede recarrega o pedido e pede conferência | Não há o que consultar em `/operations`; reenviar com a mesma versão dá `409`, nunca duplica |
| Escritas de gestão | Produto, tamanho, cardápio, cliente, funcionário, convite, configurações e estabelecimento usam mutação simples do TanStack Query; `409` aparece como erro e um `MutationCache` global recarrega as consultas ativas | Telas de cadastro, fora do caixa; nenhum ADR exige mais (o If-Match vai sempre). **Aceita** pelo usuário em 2026-10-27 como exceção registrada em 8.5 e em 02 §2.3 |
| `SENDING` antigo | No sync, pendência `SENDING` com mais de 30 s é tratada como `UNKNOWN` | É o app que morreu no meio do envio; a recente ainda pode estar em voo |
| `COMPLETED` com erro | `/operations` com `responseStatus` 4xx encerra a pendência e mantém o rascunho | A operação foi processada e recusada; reenviar daria a mesma recusa |
| Pendência > 72 h | Nunca reenviada pelo app (8.5 passo 5 reescrito); a pessoa confere o histórico e refaz | Não há como procurar o recurso de forma genérica, e uma chave nova poderia duplicar |
| Segunda confirmação | `runCritical` recusa (`blocked`) se houver pendência da mesma ação, em qualquer status; o `busy` do hook cobre `unknown`/`failed` | Revisão arquitetural: confirmar de novo depois de um timeout geraria outra chave |
| Pendências por funcionário | Migração 2 do SQLite: `pending_operations.employee_id` | `/operations/{key}` só vê as chaves do próprio usuário |
| Aparelho revogado | `AccessRevoked` com `DEVICE_REVOKED` apaga o `deviceSecret` e sai | 06 §6.14 |
| Primeiro sync | Sem `lastSeq` no aparelho, percorre o feed só para avançar o cursor, sem invalidar | O cache ainda está vazio |
| Cache persistido | `kv-store` do `expo-sqlite`, chave única, `buster` = `tenantId`; trocar de tenant ou sair chama `queryClient.clear()` + `removeClient()` | Escopo por tenant (RN-014.2) sem um persister por tenant |
| `me` offline | Guardado na tabela `meta` do SQLite; tokens continuam só no SecureStore | Abrir o app sem rede com permissões e nome do estabelecimento |
| Formulários | RHF + Zod; nos pedidos, `useWatch` (compatível com o React Compiler) e validação Zod no "Registrar" | O `watch()` do RHF desliga a compilação do componente |
| Divisão "por itens" | Soma `lineTotal` dos itens marcados (com adicionais), limitada ao saldo, num único pagamento | 8.9 não detalha; é uma parcela por vez |
| Senha no perfil | "Trocar senha" pede senha atual, nova e confirmação e chama `PUT /auth/me/password` (mutação simples, não é [I] nem [M]). Senha atual errada volta como `422` no campo. Escondido em modo compartilhado (sessão por PIN) | Rota criada no 06 em 2026-10-28, a pedido do usuário; os outros aparelhos da pessoa saem no próximo refresh |
| Inatividade compartilhada | `sharedDeviceIdleSeconds` é editado em "Formas de pagamento" (mesmo `PATCH /tenant/settings`) | `menu/settings` é só local (8.10) |
| Som | `assets/sounds/new-order.wav` (bipe curto gerado) via `expo-audio`; `assets/sounds/web_order.wav` (dois toques sintetizados, 1,5 s) para o pedido do site, no app e no canal de push | ADR-0036, ADR-0049 item 7 |
| Push e build | `expo-notifications` entrou na fatia 5 do EP-002: muda o fingerprint (`runtimeVersion: fingerprint`), então **nenhum EAS Update chega aos builds antigos** e é preciso gerar e instalar um Dev Client/APK novo. O token só sai com `extra.eas.projectId` (`eas init`) e as credenciais FCM (Android) e APNs (iOS) no EAS | Sem isso o app funciona, só não registra o push (Configurações diz o motivo) |
| E2E | Fluxos Maestro em `mobile/e2e/` escritos a partir de 8.13; **ainda não rodados** em emulador | Exigem homolog e Dev Client instalado |

---

## 8.15 Web desktop (ADR-0053)

O mesmo `mobile/` roda no navegador do computador do balcão (Chrome e Edge no Windows, RF-216, RNF-207), instalável como PWA. **Mesmas telas, permissões e regras.** As diferenças de plataforma ficam em arquivos `.web.ts(x)` ao lado do nativo, com a mesma interface; o Metro escolhe o `.web` no build web, e o Jest (preset nativo) e o `tsc` enxergam o arquivo nativo. As telas não testam `Platform.OS`; ele só aparece em estilo (largura máxima no desktop).

> **Armadilha:** dentro de `foo.web.ts`, `import … from './foo'` (ou `@/…/foo`) resolve para **ele mesmo** no bundle web (laço infinito). Nem `import type` é usado: os tipos da interface ficam em `foo.types.ts` e o código compartilhado num terceiro arquivo, ambos sem sufixo de plataforma (ex.: `secure.types.ts`, `sessionTransport.types.ts`, `tabTitleFormat.ts`, `configShared.ts`, `db/web/idb.ts`). O teste `webSelfImport.test.ts` falha se algum `.web.ts(x)` importar o próprio nome, por caminho relativo ou alias.

### Build e configuração

| Item | Como ficou |
|---|---|
| Dependências | `react-native-web`; `fake-indexeddb` (só testes) |
| `metro.config.js` | não existe (padrão do Expo). O `assetExts.push('wasm')` do spike saiu: o `expo-sqlite` não entra no bundle web |
| `app.json` → `web` | `output: "single"` (SPA), `bundler: "metro"`, `name`, `shortName`, `lang: pt-BR`, `themeColor` `#B23C0B`, `backgroundColor` `#F6F5F2`, `display: standalone` |
| `public/` | `index.html` (modelo do Expo + `<link rel="manifest">`, `robots noindex`, `:focus-visible`), `manifest.json`, `icons/icon-192.png` e `icon-512.png`, `favicon.png`. **Os ícones são o placeholder do Expo** até existir a marca |
| API | `config.web.ts`: `EXPO_PUBLIC_WEB_API_BASE_URL` antes de `EXPO_PUBLIC_API_BASE_URL`. O app web e a API precisam ser do mesmo *site* para o cookie `SameSite=Strict` (no desenvolvimento, `http://localhost:8082` → `http://localhost:5080`; o IP da máquina, usado pelo celular, não serve) |
| Exportar | `npx expo export --platform web` → `dist/` estático (hospedagem no `09-devops.md`) |

### Diferenças por arquivo

| Interface (nativo) | Web | O que muda |
|---|---|---|
| `config.ts` | `config.web.ts` | `APP_PLATFORM = 'WEB'` (`X-App-Platform` em toda requisição); `APP_VERSION` de `Constants.expoConfig.version` (o `version` do `app.json`, embutido no build). `TERMS_VERSION` e `REQUEST_TIMEOUT_MS` vêm de `configShared.ts` nos dois |
| `api/transport.ts` | `transport.web.ts` | `credentials: 'include'` em `/auth/*`, `POST /devices/shared` e `/invitations/{code}/accept` (rotas que leem ou emitem os cookies, 06 §6.3 "App no navegador"); hub com `&appPlatform=WEB` na query (o navegador não manda header no WebSocket, 06 §6.14) |
| `stores/secure.ts` | `secure.web.ts` | access token **só em memória**; o refresh nunca fica no cliente (é zerado mesmo se vier no corpo); `installationId` = `crypto.randomUUID()` no `localStorage`; aparelho compartilhado: só os dados não secretos no IndexedDB (`meta`), o `deviceSecret` fica no cookie `__Secure-almoseis-ds` |
| `stores/sessionTransport.ts` | `sessionTransport.web.ts` | **restaurar** (`restored` / `signedOut` / `retry`): celular lê o SecureStore (nunca `retry`); web chama `POST /auth/refresh` (cookie `__Secure-almoseis-rt`, corpo `{ installationId }`) e depois `GET /auth/me`. `401` (ou outro 4xx) → login. **429, 5xx e falha de rede → `retry`**, nunca login: a raiz fica no indicador com "Sem resposta do servidor. Reconectando…" (`session.reconnecting`) e tenta de novo após o `Retry-After` (ADR-0041) ou 2, 4, 8, 15 e 30 s (depois a cada 30 s). `GET /auth/me` falhando sem `me` em cache também é `retry`, e a nova tentativa reaproveita o token já obtido (o coordenador o devolve sem outro refresh). **Sair** (`endSession`): celular revoga só em "Sair" e só então limpa, como antes; web, em **todo** motivo (inclusive `expired`), usa o logout pendente (abaixo) |
| `features/auth/pendingLogout.ts` | (usado só pelo `sessionTransport.web.ts`) | **logout pendente.** Ordem ao sair: (1) marca `almoseis.pendingLogout` no `localStorage`, síncrona; (2) aviso `signedOut` às outras abas; (3) limpa a sessão local (a tela anterior some na hora, sem esperar a rede); (4) `POST /auth/logout` anônimo (sem token, sem corpo; o cookie vai sozinho), sob o mesmo Web Lock da renovação. Só `204` ou `401` apagam a marca; qualquer outra falha a mantém e agenda novas tentativas (2, 5, 15, 30, 60 s, depois a cada 60 s). **Com a marca presente, nenhuma aba restaura a sessão pelo cookie** (o `localStorage` é da origem): o `restore` vai direto para a entrada e tenta o logout em segundo plano. Antes de login, cadastro, convite ou PIN (`session.prepareSignIn`), o app espera um logout em andamento (nesta aba ou, pelo lock, em outra) e suspende as tentativas, porque a resposta do logout apagaria o cookie novo; entrar apaga a marca |
| `features/auth/tabCoordinator.ts` | (usado só pelo `sessionTransport.web.ts`) | **várias abas:** renovação sob Web Lock `almoseis.auth.refresh` (uma por vez entre todas as abas, senão o servidor revoga a família, ADR-0027); `BroadcastChannel` `almoseis.session` repassa o access token novo (quem esperou o lock e recebeu um token válido não renova de novo), o logout (as outras abas saem) e a entrada/troca de estabelecimento (as outras abas recarregam). Sem Web Locks, só o single-flight da aba |
| `api/endpoints/sharedDeviceAuth.ts` | `sharedDeviceAuth.web.ts` | lista: `GET /auth/shared-device/employees?installationId=` + `X-Tenant-Id`; `pin-login` sem `X-Device-Secret`; "deixar de usar como compartilhado" (`setSharedDevice(null)`) chama `DELETE /auth/shared-device` |
| `db/migrations.ts`, `draftsRepo.ts`, `pendingOpsRepo.ts`, `metaRepo.ts` | `*.web.ts` + `db/web/idb.ts` | **IndexedDB** `almoseis` v1, um *object store* por tabela (`meta`, `drafts`, `pending_operations`, índice `tenantId`) e `kv`. Toda escrita resolve no `complete` da transação; `pendingOpsRepo.insert` usa `durability: 'strict'` e `add` (chave repetida falha como o `INSERT`). A pendência está gravada **antes** do envio: F5 no meio do "Receber" cai no `GET /operations/{key}`. `openLocalDb` pede `navigator.storage.persist()` uma vez |
| `db/kvStorage.ts` | `kvStorage.web.ts` | cache persistido do TanStack Query no IndexedDB (`kv`); preferências (`usePreferences`) no `localStorage` |
| `features/sync/appLifecycle.ts` | `appLifecycle.web.ts` | **o SignalR não desconecta com a aba oculta** (ADR-0053 §7.1). Reconecta e sincroniza em `visibilitychange` visível, `online`, `pageshow` (bfcache) e ao detectar que o computador dormiu (intervalo de 30 s atrasado > 90 s) |
| `lib/keepAwake.ts` | `keepAwake.web.ts` | Wake Lock próprio: o do `expo-keep-awake` web não readquire o lock ao voltar a aba e lança erro sem suporte |
| `features/production/orderSound.ts` | `orderSound.web.ts` | `HTMLAudioElement`; `needsUnlock` enquanto a página não teve ativação do usuário (`navigator.userActivation`). A aba Pedidos mostra a faixa "Som desativado neste navegador — Ativar som"; o clique toca o aviso uma vez. No celular `needsUnlock` é sempre `false` |
| `features/shell/tabTitle.ts` | `tabTitle.web.ts` | com a aba oculta, `document.title` = "(2) Novos do site · AlmoSeis": quantos pedidos do site **aguardam aceite agora** (a mesma consulta da faixa, `['orders', 'list', 'awaiting']`, mantida em dia pelos eventos com a aba oculta). Aceito em outro computador ou expirado, o número cai; volta ao título normal quando a aba vem para a frente (EP-002 fatia 1, 2026-11-20). O som do pedido do site é o `web_order.wav` (§8.6) |
| `features/update/upgradeHandler.ts` + `UpdateRequiredScreen.tsx` | `*.web.ts(x)` | `426`: grava os rascunhos no debounce (`flushAllDrafts`) e recarrega a página **uma vez** (marca `almoseis.reloadedFor426` na `sessionStorage`, vale 10 min); se persistir, "Estamos atualizando o AlmoSeis. Tente de novo em alguns minutos." com "Recarregar", sem link de loja |
| `lib/keyboard.ts` | `keyboard.web.ts` | `useKeyDown`: `Enter` confirma o `ConfirmDialog` (exceto com o foco num botão, que já trata a tecla); tecla segurada (`repeat`) é ignorada; PIN aceita dígitos, `Backspace` e `Enter`. `Esc` fecha diálogos e *sheets* pelo `onRequestClose` do `Modal` do react-native-web. O peso do quilo já é um `TextInput` |
| `lib/share.ts` | `share.web.ts` | "Compartilhar link" da página de pedidos: no celular, `Share.share`; na web, `navigator.share` quando existe (fechar a folha não copia), senão `navigator.clipboard.writeText` e o aviso "Link copiado". Tipos em `share.types.ts` |
| `features/storefront/printQr.ts` | `printQr.web.ts` | "Imprimir QR code" só na web (`canPrintQr`): janela com nome, QR em SVG e link + `window.print()`; janela bloqueada → aviso. No celular não há impressão (exigiria módulo nativo). Tipos em `printQr.types.ts`, QR em `qrMatrix.ts` |
| `features/push/notifications.ts` | `notifications.web.ts` | Push dos pedidos do site (§8.6): no celular, `expo-notifications`; na web, **no-op** (`supported: false`, nunca registra nem pede permissão; Web Push é P2). Tipos em `notifications.types.ts`; o código comum (canal, `data`, destino do toque, estado) em `pushShared.ts` |
| `lib/confirm.ts` | `confirm.web.ts` | `confirmAction`: pergunta de sim/não antes de ação destrutiva (encerrar sessão, descartar pendência). No celular, `Alert.alert`; na web, `window.confirm`, porque o `Alert` do react-native-web não faz nada. Uma regra de lint proíbe importar `Alert` de `react-native` fora de `lib/confirm.ts` |

### Sem arquivo `.web`

- **Abas superiores:** o `react-native-tab-view` usa `PanResponderAdapter` na web (`Pager.tsx`), sem `react-native-pager-view`; o `TopTabs` funciona como está (clique na aba).
- **Conectividade:** o `expo-network` web já usa `navigator.onLine` e os eventos `online`/`offline`.
- **Layout:** janelas ≥ 720 px usam o layout de tablet existente (cozinha em 3 colunas). No desktop, `Screen`, listas das abas, detalhe do pedido, rodapé de envio, aba Fechamento, `ConfirmDialog` (520 px) e `BottomSheet` (640 px) têm largura máxima centralizada (`WEB_CONTENT_MAX`, 760 px, via `Platform.select`, vazio no celular).
- **Assinatura:** ADR-0017 igual; nenhuma tela nova mostra preço ou compra.

### Renovação e limite de taxa (ADR-0041)

O `POST /auth/refresh` tem limite de 10/min por IP, e um balcão pode ter várias abas e computadores atrás do mesmo IP. Na web o sync roda a cada `visibilitychange`, então o passo 1 do sync (§8.6) **só renova se o access token vence em menos de 5 min** (`SYNC_REFRESH_MARGIN_MS`, pelo `accessTokenExpiresAt`). Vale para todas as plataformas: com o token válido, a sessão continua revalidada pelas próprias chamadas do sync (token revogado por troca de `security_stamp` dá `401` em até 30 s, ADR-0027, e cai no refresh do 401). O coordenador entre abas continua renovando de verdade quando recebe o próprio token como "velho": esse caminho é o do `401` e do `AccessRevoked`, que precisam de um token novo; a reutilização do token válido da própria aba fica no `refreshIfExpiring`, que sabe que a chamada é oportunista. Na restauração, `429`/`5xx`/rede nunca levam ao login (tabela acima).

**Token do hub (todas as plataformas).** O servidor pode fechar a conexão quando o access token vence. O `accessTokenFactory` do SignalR é assíncrono: antes de cada conexão ou reconexão chama `refreshIfExpiring(REALTIME_REFRESH_MARGIN_MS)` (1 min), pelo mesmo caminho único de renovação (single-flight; coordenador entre abas na web), e só então devolve o token. Se a reconexão automática desistir (`onclose` depois das tentativas), `RealtimeConnection` agenda um novo `start` com espera crescente (5, 15, 30, 60 s, depois a cada 60 s) enquanto não for parada; `stop()` cancela.

### Testes

- `webLocalDb.test.ts`: repositórios web contra `fake-indexeddb` (upsert do rascunho, `latest` com alvo nulo, `purgeOld`, pendência que sobrevive ao "F5", chave repetida, filtros e ordem).
- `tabCoordinator.test.ts`: duas abas renovando juntas → uma renovação só e nunca simultâneas; sem canal, renovações em série; single-flight na aba; token vencendo não é reaproveitado; logout e entrada chegam às outras abas.
- `pendingLogout.test.ts`: falha mantém a marca e tenta de novo até o `204`; `401` limpa, `403` não; a marca vale para outra aba; chamadas simultâneas viram um logout; o login espera o lock; entrar para as tentativas.
- `webSession.test.ts` (store real + `sessionTransport.web`/`secure.web`, `localStorage` falso): a marca existe e a tela já está limpa quando o logout sai; logout com `503` → F5 não chama o refresh e vai à entrada; `204` libera a próxima restauração; `expired` também chama o logout; o login espera o logout em andamento; abrir com `429` (`Retry-After`), `503` e queda de rede → "reconectando" e depois entra, sem passar pelo login; `/auth/me` falhando reaproveita o token.
- `refreshPolicy.test.ts`: o sync não renova com o token valendo mais de 5 min e renova quando vence antes, sem validade conhecida ou sem token.
- `criticalMutationLock.test.tsx`: dois `run` no mesmo tick → um envio só e o segundo `ignored`; dois `runCritical` concorrentes da mesma ação → um envia e o outro fica `blocked`. `webKeyboard.test.tsx`: `Enter` com `repeat` é ignorado.
- `webSelfImport.test.ts`: nenhum `.web.ts(x)` importa o próprio nome.
- `realtimeToken.test.ts`: o hub pede o token por uma função assíncrona que renova antes de conectar quando ele vence em menos de 1 min; depois de desistir, a conexão agenda nova tentativa com espera.
- Pendentes: E2E web (Playwright) dos critérios de aceite do RF-216.
