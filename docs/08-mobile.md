# 08. Mobile (React Native + Expo) — arquitetura tela por tela

> Implementa os ADRs 0001, 0010, 0017, 0019–0023, 0027, 0030, 0032–0036 e 0038–0041 sobre os contratos de `06-contratos-api.md`. Diz **quais telas existem, de onde vêm os dados, o que cada ação envia e como o app se comporta sem conexão**. Payloads e `errorCode`s vêm **só** do `06`.

## 8.1 Stack e alvo

| Item | Escolha |
|---|---|
| Base | Expo SDK estável mais recente (Dev Client + EAS Build/Update, `runtimeVersion: fingerprint`), React Native com a New Architecture, Hermes, TypeScript `strict` |
| Navegação | Expo Router; abas superiores com `@react-navigation/material-top-tabs` via `withLayoutContext` |
| Servidor/cache | TanStack Query v5 + `@tanstack/query-async-storage-persister` sobre `expo-sqlite/kv-store` |
| Estado local | Zustand (sessão, preferências, conectividade) |
| Formulários | React Hook Form + Zod |
| Banco local | `expo-sqlite` (rascunhos, operações pendentes, metadados de sync) |
| Segredos | `expo-secure-store` (tokens, `deviceSecret`) |
| Tempo real | `@microsoft/signalr` |
| Outros | `expo-keep-awake`, `expo-audio` (aviso sonoro), `expo-image`, `expo-network`, `expo-application`, `expo-crypto` (`randomUUID`), `@shopify/flash-list` |
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
  src/
    api/          client.ts  errors.ts  headers.ts  endpoints/*.ts (um arquivo por recurso do 06)
    features/     auth/ orders/ production/ payments/ closing/ menu/ catalog/ customers/ employees/ sync/ realtime/ drafts/
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
| `['production', businessDayId]` | painel da cozinha | 15 s |
| `['orders', id, 'alerts' \| 'history' \| 'payments']` | detalhes | 30 s |
| `['customers', 'search', termo]`, `['customers', id]`, `['customers', id, 'pending']` | clientes | 1 min |
| `['closing', 'summary', businessDayId]`, `['closing', 'history', filtros]`, `['closing', id]`, `['closing', 'monthly', mês]` | fechamento | 15 s / 5 min |

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
- Tudo fica escopado por `tenant_id`. Sair da conta apaga rascunhos e pendências **só depois de avisar**, se houver algum.

## 8.5 Escritas críticas e resultado desconhecido (ADR-0010, ADR-0033, ADR-0034)

Todo endpoint [I] ou [M] do 06 passa pelo hook `useCriticalMutation`:

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
5. Pendência com mais de 72 h (TTL da idempotência): "Reenviar" primeiro roda o sync (§8.6) e procura o recurso.
   Se não o encontrar, reenvia com uma key **nova**.
```

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
1. refreshSingleFlight()                                  // revalida a sessão (RN-014 "quando a conexão retornar")
2. para cada pending UNKNOWN → resolveUnknown()           // §8.5 passo 4
3. loop GET /sync/changes?since={lastSeq}&limit=500 até hasMore=false
     → invalida as chaves de cada change (mapa §8.3) → grava lastSeq em meta
   410 SyncCursorExpired → queryClient.removeQueries(tenant) → lastSeq = extensions.currentSeq
4. realtime.ensureConnected()
5. drafts.unlockSubmit()                                  // libera o envio dos rascunhos válidos
```

Os conflitos (RN-014.9/14.10) aparecem no envio, via `409 VersionConflict` (§8.5). O app nunca mescla automaticamente.

### `features/realtime/useRealtime.ts`

- `HubConnectionBuilder().withUrl('/hubs/realtime?appVersion=…', { accessTokenFactory })`, com reconexão automática (0, 2, 5, 10, 30 s) e reconexão manual depois disso, quando voltar online.
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
| Perfil do usuário | `menu/profile` (nome, PIN, trocar senha, trocar de estabelecimento) | — |
| Dados do estabelecimento | `menu/tenant` | ver: `orders.view`; editar: `settings.manage` |
| Funcionários | `menu/employees` | `employees.manage` |
| Perfis e permissões | `menu/roles` (somente leitura, ADR-0028) | — |
| Pratos e bebidas | `menu/products`, `menu/sizes` | ver: `orders.view`; editar: `catalog.manage` |
| Cardápio do dia | `menu/menus/today` | `menu.manage` |
| Cardápio semanal | `menu/menus/weekly` | `menu.manage` |
| Histórico e reutilização de cardápios | `menu/menus/history` | `menu.manage` |
| Clientes | `menu/customers` | `orders.view` |
| Configuração de preços | `menu/pricing` (preço do quilo, taxa de entrega) | `settings.manage` |
| Formas de pagamento | `menu/payment-methods` | `settings.manage` |
| Histórico de operações | `menu/audit` | `audit.view` |
| Histórico de fechamentos | `closing/history` | `closing.view` |
| Configurações gerais | `menu/settings` (som, aparelho compartilhado, tamanho da fonte) | — |
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

- **Dados:** `['orders', 'list', { orderType: 'MARMITA' }]` do expediente aberto, mais `['menus', 'current', 'MARMITA']`.
- **Layout:**
  - filtro segmentado *Todos / A pagar / Entrega / Retirada*;
  - busca por nome;
  - cards `OrderCard` com número, cliente, entrega ou retirada, total, `PaymentBadge` (financeiro), `ProductionBadge` (produção) e o ponto de alerta;
  - FAB "Nova marmita".
- **Sem cardápio** (`404 MenuNotFound`): o `MenuMissingCard` oferece "Usar o de ontem", "Usar o da semana passada" (`POST /menus/{id}/clone`), "Usar modelo semanal" e "Montar agora" (`menu/menus/today`), conforme `sources` (RN-003.2).

### Nova marmita — `orders/new/marmita`

Formulário de uma página, com seções recolhíveis. O rascunho é salvo automaticamente.

1. **Cliente:**
   - `CustomerPicker`: busca por telefone ou nome (`/customers?search=`), mostra as pendências do cliente ("Deve R$ 35,00");
   - ou nome e telefone livres + "Salvar cliente" (`saveCustomer`).
2. **Atendimento:** Entrega / Retirada. Na entrega:
   - escolher um endereço salvo ou digitar um;
   - taxa de entrega pré-preenchida com o padrão, editável só com `orders.delivery-fee.edit`.
3. **Marmitas:** "Adicionar marmita" abre o `MarmitaBuilderSheet`:
   - tamanho em chips com preço (`/marmita-sizes`);
   - prato(s) principal(is) e acompanhamentos (multi-seleção) do cardápio do dia, com os indisponíveis desabilitados e riscados;
   - adicionais;
   - quantidade (`QuantityStepper`) e observação.
   
   Cada marmita vira uma linha editável e duplicável ("+1 igual").
4. **Bebidas:** grade de bebidas disponíveis com stepper.
5. **Observações do pedido.**
6. **Resumo fixo no rodapé:** "Prévia: R$ 55,00", calculada em centavos com os preços do cache, e o botão "Registrar pedido" → Confirma → `POST /orders` [I].

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

- **Cabeçalho:** "#12 · Marmita · Entrega", cliente/mesa, `ProductionBadge` e `PaymentBadge` lado a lado, mas com formas e cores distintas (RNF-001).
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
- **Dinheiro:** campo "Recebido" opcional, com o troco calculado na hora ("Troco: R$ 15,00").
- **"Dividir conta":**
  - *Igual entre N pessoas*: gera N parcelas (a última absorve os centavos);
  - *Por itens*: soma os itens marcados.
  
  Cada parcela vira um pagamento separado com `label` "Pessoa N", confirmado um a um. Cada um é uma `useCriticalMutation` própria e usa a versão devolvida pelo anterior.
- "Confirmar recebimento" → Confirma ("Receber R$ 35,00 em dinheiro do pedido #12? Troco R$ 15,00") → `POST /orders/{id}/payments` [I][M].
- **Pedido de outro dia** (fiado, RF-011): a confirmação avisa "Pedido de 26/10. O recebimento entra no caixa de hoje."
- **Estornar** (lista de pagamentos, `payments.reverse`): motivo → Confirma → `POST /payments/{id}/reverse` [I][M].

### Aba Pedidos (produção) — `(tabs)/pedidos`

- **Dados:** `['production', businessDayId]` (`GET /production/orders`).
- **Layout:** três colunas no tablet (largura ≥ 720 dp) ou três seções empilhadas no celular, na ordem **Abertos → Em produção → Concluídos**, cada uma do mais antigo para o mais novo (RN-007.4/7.5). Concluídos ficam recolhidos por padrão.
- **`ProductionCard`:**
  - número grande, rótulo (cliente/mesa), há quanto tempo foi criado ("há 12 min");
  - itens com composição e observações em destaque;
  - selo de alerta, que expande as mudanças e permite dar ciência ali mesmo.
- **Ação:** um botão de avanço ("Iniciar" → "Concluir"), e "Voltar status" no menu do card. Ambos passam pelo Confirma (RN-007.3) e chamam `PATCH /orders/{id}/status` [M].
- **Comportamento da aba:** `useKeepAwake`, som (§8.6) e fonte maior, ajustável em `menu/settings`.

### Aba Fechamento — `(tabs)/fechamento`

- **Dados:** `['closing', 'summary', current]`.
- **Layout** (as tabelas do épico RF-010 viram cards):
  1. **Por modalidade:** Marmitas / PF / Quilo / **Total**, com Vendido, Recebido e Pendente;
  2. **Por forma de pagamento:** Dinheiro / Pix / Cartão, com estornos;
  3. **Caixa físico:** saldo inicial, recebido em dinheiro, suprimentos, retiradas, estornos em dinheiro, **esperado**;
  4. "Recebido de dias anteriores", Cancelamentos e Estornos;
  5. Botões: "Suprimento/Retirada" → `closing/cash-movement` ([I], motivo obrigatório); "Fechar expediente" → `closing/finalize` (`closing.finalize`); "Histórico".
- O resumo é atualizado por `ClosingUpdated`, ou puxando para baixo.

### Fechar expediente — `closing/finalize`

- **Passo 1, conferir:** o resumo, em modo leitura, e um aviso se existirem pedidos pendentes ("3 pedidos ficarão pendentes: R$ 220,00").
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
| `menu/products/new`, `[id]` | Nome, tipo (bloqueado após venda), categoria, preço (regras por tipo, ADR-0020), "vai para a cozinha?", foto (câmera/galeria → `expo-image-manipulator` reduz para 1024 px JPEG 80% → `image-upload` → `PUT` no R2 → `PATCH imageStorageKey`) | |
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
| `menu/settings` | Som de pedidos, tamanho da fonte da cozinha, modo compartilhado, versão do app | local |

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
| `ConnectivityBar`, `SubscriptionBanner`, `ConflictSheet`, `EmptyState`, `ErrorState`, `Skeleton`, `BottomSheet`, `Chip`, `SegmentedControl`, `PermissionGate` | `PermissionGate permission="payments.register"` esconde o filho; com `fallback="disabled"`, mostra desabilitado com explicação |

**Tema:** tokens de cor com modo claro e escuro; contraste AA; fonte do sistema escalável (respeita a acessibilidade do Android), com limite de escala nos cards da cozinha. Todo ícone de ação tem `accessibilityLabel`.

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
| `VersionConflict` | `ConflictSheet` (§8.5) |
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
| E2E (Maestro, emulador Android de entrada) | 1) cadastro → abrir expediente → marmita com bebida → receber em dinheiro com troco → fechar com diferença e justificativa; 2) desligar a rede no meio do "Receber" → religar → app resolve sem duplicar; 3) PIN no aparelho compartilhado + inatividade |

Critério de pronto do mobile (`docs/02-padroes.md` §2.6): fluxos E2E 1 e 2 passando em homolog antes de cada release para as lojas.
