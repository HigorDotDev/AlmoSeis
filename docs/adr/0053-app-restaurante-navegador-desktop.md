# ADR-0053 — App do restaurante também no navegador do desktop (mesma base de código, PWA)

- **Status:** Proposto
- **Data:** 2026-11-19
- **Origem:** decisão do dono do produto em 2026-11-19 (o app do restaurante no computador do balcão, pelo navegador, em vez de um app Windows nativo); EP-002, Fase 1, RF-216; spike de `expo export --platform web` feito na mesma data
- **Relacionados:** ADR-0001 (complementado), ADR-0010, ADR-0017, ADR-0024, ADR-0027, ADR-0035, ADR-0036, ADR-0037, ADR-0041, ADR-0049, ADR-0051

## Contexto

Parte dos restaurantes do piloto tem um computador Windows no balcão (onde já usam o WhatsApp Web) e quer atender e aceitar os pedidos do site (ADR-0046) por ele, com tela grande, mouse e teclado. O ADR-0001 fez o produto do restaurante ser só mobile e descartou o PWA porque o armazenamento local (rascunhos) e o segundo plano ficariam piores. O dono do produto escolheu a web em vez de um app Windows nativo.

O spike de 2026-11-19 mostrou o seguinte:

- O `mobile/` (Expo Router) compila para web com `react-native-web` e `config.resolver.assetExts.push('wasm')` no `metro.config.js`. Todas as rotas exportam.
- No navegador, o app trava na inicialização. `expo-secure-store` não existe na web (`ExpoSecureStore.getValueWithKeyAsync is not a function`, no `hydrate` da sessão, ao ler o `installationId`).
- `expo-sqlite` compila na web (wa-sqlite/WASM), mas é recente, pode exigir os headers COOP/COEP e ainda não foi testado em execução.
- Também mudam na web: push (o Expo Push não chega ao navegador), `expo-keep-awake`, som (o navegador só toca áudio depois de uma interação), abas superiores com `react-native-pager-view` (não existe na web), CORS (a API hoje não tem), `expo-image-picker`, `expo-network` e `expo-application`.

O ponto mais sensível é a sessão. O ADR-0027 guarda o refresh token de 30 dias e o `deviceSecret` no SecureStore. Na web não existe armazenamento equivalente que o JavaScript leia e um script injetado (XSS) não leia.

Sem esta decisão, cada agente adaptaria o app à web do seu jeito: tokens em `localStorage`, CORS aberto, rascunhos que somem ao recarregar a página.

## Decisão

### 1. Uma base de código, exportada também para web

1. O app do restaurante continua sendo **um só**, em `mobile/`. Ele é exportado também para web (`react-native-web`, `npx expo export --platform web`). Recomendação: `web.output: "single"` (SPA). As rotas ficam atrás do login, então não há ganho em pré-renderizar.
2. As diferenças de plataforma ficam em arquivos `.web.ts` atrás da **mesma interface** do arquivo nativo: armazenamento seguro, banco local, som, tela ligada e identificador de instalação. As telas não testam `Platform.OS`, salvo no layout. O `Platform.OS` fica para diferenças só visuais.
3. **Instalável como PWA** no Chrome e no Edge, com manifesto (nome, ícones, cor, `display: standalone`), ícone na área de trabalho e janela própria. Não há cache do app para uso offline (§7).
4. **Mesmas telas, mesmas permissões e mesmas regras.** Nenhuma regra de negócio, permissão ou cálculo muda por plataforma. Janelas com largura ≥ 720 px usam o layout de tablet que já existe (por exemplo, a cozinha em 3 colunas, `08-mobile.md` §8.9).
5. **Não é o projeto `web/`** do ADR-0051 (página pública do consumidor, Next.js). Os dois não compartilham rota, build, deploy nem sessão. O único código comum é o de `packages/shared`.
6. **App Windows nativo** (Tauri ou Electron embrulhando o mesmo build web) só no futuro, se for preciso acesso a hardware (impressora de comanda, balança). Isso pede um ADR próprio.

### 2. Onde é servido

| Item | Decisão |
|---|---|
| Endereço | `app.{dominio}` em produção e `app-homolog.{dominio}` em homologação. Com o domínio atual do `09-devops.md`, ficam `app.almoseis.app` e `app-homolog.almoseis.app`. O domínio final depende da Q-07 do EP-002 |
| API | `api.{dominio}` / `api-homolog.{dominio}`, como já previsto. O app web e a API são **origens diferentes do mesmo *site*** (mesmo domínio registrável), o que permite cookie `SameSite=Strict` (§3) |
| Hospedagem | Recomendação: **Azure Static Web Apps**, com arquivos estáticos e sem servidor. O build é só HTML, JS e assets, sem dados nem renderização no servidor. Por isso a limitação do SWA para Next.js citada no ADR-0051 não se aplica aqui. Alternativa: Container App com nginx no mesmo Environment da API (ADR-0037), ao custo de 1 réplica. O plano (Free ou Standard) fica na Q-22 |
| DNS | No Cloudflare, como no ADR-0051 |
| Cache | `index.html` com `Cache-Control: no-cache`; arquivos com hash no nome com cache longo (`immutable`); rotas desconhecidas voltam para `index.html` |
| Cabeçalhos | `Content-Security-Policy` restrita: scripts só da própria origem e `connect-src` só da API (HTTPS e WSS) e do R2 das imagens. Também `X-Content-Type-Options: nosniff`, `Referrer-Policy: same-origin`, `frame-ancestors 'none'`, `robots: noindex`. **Sem scripts de terceiros** (o mesmo princípio do ADR-0051, item 10) |
| Deploy | Workflow próprio (`mobile-web.yml`), com homologação automática e produção por aprovação. A ordem é: API compatível primeiro, depois o app web (§6) |

### 3. Sessão e tokens na web (complementa o ADR-0027)

O mobile continua **exatamente** como está: tokens e `deviceSecret` no SecureStore, refresh token no corpo.

1. **Identificação da plataforma:** o app web envia o header que já existe, `X-App-Platform: WEB` (ADR-0035). Não cria um header novo. A API só usa o fluxo de cookie quando `X-App-Platform = WEB`. Nas outras plataformas, ignora cookies de sessão.
2. **Access token só em memória**, na aba. Ao recarregar a página ou abrir outra aba, o app pede um novo pelo `/auth/refresh`.
3. **Refresh token em cookie**, nunca no corpo da resposta para a web:
   - nome `__Secure-almoseis-rt`, `HttpOnly; Secure; SameSite=Strict; Path=/api/v1/auth`, sem `Domain` (cookie só do host da API, que separa homologação de produção), `Max-Age` igual à validade do refresh (30 dias, rotativo, ADR-0027);
   - é emitido por `login`, `signup`, `pin-login`, `switch-tenant`, aceite de convite e `refresh`. Na web, o campo `refreshToken` do objeto `Tokens` vem `null` e o resto não muda;
   - `/auth/refresh` e `/auth/logout` leem o refresh do cookie quando a plataforma é `WEB`. Um `refreshToken` no corpo vindo da web é recusado (`422`);
   - o refresh token continua ligado ao `installationId`, com rotação e revogação da família no reuso, como no ADR-0027;
   - `switch-tenant` revoga o refresh da mesma instalação antes de emitir o novo, em todas as plataformas, para não deixar um refresh órfão válido por 30 dias.
4. **CSRF**, nas rotas que aceitam cookie (refresh, logout, pin-login e lista de funcionários do aparelho compartilhado):
   - o `Origin` tem de estar na lista de origens permitidas (§4). Sem `Origin` ou com outra origem, a resposta é `403`;
   - o header customizado `X-App-Platform: WEB` é obrigatório. Ele força o *preflight* do CORS, que falha para qualquer origem fora da lista.
   - O `SameSite=Strict` sozinho não basta: outros subdomínios do mesmo site (inclusive a página pública `{dominio}`) contam como *same-site*. A checagem de `Origin` cobre esse caso.
   - A checagem de `Origin` acontece na própria leitura do cookie, não só por atributo na rota: uma rota nova que leia o cookie fica protegida sem depender de lembrar do atributo. Um teste de arquitetura impede leitura de cookie fora desse ponto.
5. **Logout** revoga o refresh no servidor e apaga o cookie (`Max-Age=0`). Um aviso entre abas (`BroadcastChannel`) faz as outras abas saírem também.
6. **Várias abas:** a renovação é feita por uma aba de cada vez (Web Locks API, `navigator.locks`). Quem obtém o lock e vê que outra aba acabou de renovar reaproveita o resultado, avisado pelo `BroadcastChannel`. Sem isso, duas abas renovando juntas apresentariam o mesmo refresh e o servidor revogaria a família inteira (ADR-0027).
7. **`installationId`:** não é segredo. Fica em `localStorage`, gerado uma vez com `crypto.randomUUID()`. Se o navegador for limpo, nasce outro: a pessoa entra de novo e o aparelho compartilhado é registrado de novo.
8. **Aparelho compartilhado com PIN: suportado na web.** Recomendação do agente, a confirmar na Q-24. O computador do balcão é justamente o aparelho que vários funcionários usam. Sem PIN, a equipe volta a usar uma conta só, que é o problema que o ADR-0027 resolve.
   - O `deviceSecret` vai em um cookie separado, `__Secure-almoseis-ds`, `HttpOnly; Secure; SameSite=Strict`, sem `Domain`, emitido por `POST /devices/shared` quando a plataforma é `WEB` (e **fora** do corpo da resposta).
   - Os dois usos do segredo (lista de funcionários e `pin-login`) ficam sob o mesmo prefixo, para que o `Path` do cookie seja restrito. Na web, a lista de funcionários é `GET /api/v1/auth/shared-device/employees?installationId=…`, com a mesma resposta da rota atual (06 §6.3).
   - Na web o segredo vem **só** do cookie: `X-Device-Secret` é ignorado e não está na lista de headers do CORS.
   - "Deixar de usar como compartilhado" chama `DELETE /api/v1/auth/shared-device`, que só apaga o cookie (o JavaScript não apaga cookie `HttpOnly`). Revogar no servidor continua pela rota de revogação do aparelho.
   - O Chrome limita cookies a 400 dias. Cada `pin-login` bem-sucedido reemite o cookie com o mesmo valor, o que estende a validade.
   - Revogar o aparelho (ADR-0027) invalida o segredo no servidor. O app trata `401 InvalidDeviceSecret` saindo do modo compartilhado. "Deixar de usar como compartilhado", no próprio navegador, apaga o cookie.
   - `SharedDevice.platform` e `X-App-Platform` ganham o valor `WEB`.
9. **Alternativa descartada:** refresh token ou `deviceSecret` em `localStorage` ou IndexedDB. Qualquer XSS ou extensão de navegador com acesso à página leria e levaria embora uma sessão de 30 dias, ou o acesso do aparelho sem prazo.
10. **Limite assumido:** com XSS ativo na página aberta, o atacante consegue agir *durante* a sessão (pode chamar o refresh e receber um access token de 15 min), mas não consegue levar embora o refresh. A defesa contra XSS é a CSP do §2, sem scripts de terceiros.

### 4. CORS na API

1. Lista de origens permitidas **por ambiente, em configuração** (por exemplo, `Cors:RestaurantAppOrigins`). Nenhuma origem curinga e nenhuma origem derivada do pedido.
   - Desenvolvimento: `http://localhost:8082`.
   - Homologação: `https://app-homolog.{dominio}`.
   - Produção: `https://app.{dominio}`.
   - Só `https://host[:porta]` é aceito; `http://` só para `localhost` e `127.0.0.1`. Curinga, caminho ou origem inválida impedem a API de subir.
   - Atrás do ingress, a API usa `X-Forwarded-For` só de proxies/redes configurados como confiáveis, para o *rate limit* por IP (ADR-0041) enxergar o cliente real. A web aumenta o número de `/auth/refresh`, então isso é condição para produção.
2. Política do app do restaurante: `AllowCredentials` **só** para essas origens, em todas as rotas autenticadas, nas rotas de `/auth/*` e no hub do SignalR. A conexão do hub também confere o `Origin`.
3. Headers permitidos: `Authorization`, `Content-Type`, `Idempotency-Key`, `If-Match`, `X-App-Version`, `X-App-Platform`, `X-Tenant-Id`.
4. Headers expostos: `ETag` (sem ele o JavaScript não lê a versão para o `If-Match`, ADR-0034), `Location`, `Retry-After` (ADR-0041) e `Idempotent-Replayed` (ADR-0033).
5. `Access-Control-Max-Age: 600`, para não repetir o *preflight* a cada requisição.
6. A política da página pública (`/public/*`, ADR-0051) é **outra**: a origem é a da página, sem credenciais. As duas listas não se misturam.
7. Em desenvolvimento, `localhost:8082` e a API em `localhost` são o mesmo *site*. O Chrome e o Edge aceitam cookie `Secure` em `http://localhost`.

### 5. Rascunhos e dados locais (complementa o ADR-0010)

1. Recomendação: **implementação web do repositório local sobre IndexedDB** (`src/db/*.web.ts`), com a mesma interface usada pelas telas e pelo `useCriticalMutation`. Um *object store* por tabela de hoje: `meta`, `drafts` e `pending_operations`. O cache persistido do TanStack Query também vai para o IndexedDB, no lugar do `expo-sqlite/kv-store`.
   - Isso exige que o código que chama o banco use um repositório (`drafts`, `pendingOperations`, `meta`) e não SQL direto. Se hoje houver SQL fora do repositório, ele é movido antes.
2. **`expo-sqlite` na web não é usado.** Exige WASM e os headers COOP/COEP. O COEP também bloquearia as imagens do R2 sem um header de recurso entre origens, e a biblioteca ainda é recente.
3. **`localStorage`** guarda só o `installationId` e preferências pequenas (última aba, som). Não guarda rascunhos nem pendências: tem só 5 MB, não tem transação entre abas e o cache de consultas pode passar disso.
4. A regra de ouro do ADR-0010 não muda: a pendência (`pending_operations`) é gravada **e confirmada no IndexedDB antes** do envio. Recarregar a página (F5) no meio de um pagamento cai no fluxo de resultado desconhecido (`GET /operations/{key}`), como no celular.
5. O app pede `navigator.storage.persist()` para o navegador não apagar os dados por falta de espaço. Limpar os dados do navegador apaga rascunhos e pendências. **Aceito:** rascunhos são rascunhos, e uma pendência perdida continua conferível pelo histórico do pedido, como quando se desinstala o app.
6. Sair da conta apaga os dados locais do tenant (cache, rascunhos e pendências), com o mesmo aviso do celular (`08-mobile.md` §8.4).

### 6. Versão mínima na web (complementa o ADR-0035)

1. `X-App-Platform: WEB` e `X-App-Version` igual à versão do app (`expo.version`), em toda requisição e na conexão do SignalR. A API ganha `MinSupportedAppVersion` para `WEB`.
2. Na web não há loja nem EAS Update: cada deploy já entrega a versão nova a quem abre ou recarrega a página. Só uma aba aberta há muito tempo fica com o código antigo.
3. **`426 AppVersionUnsupported` na web:**
   - o app grava os rascunhos pendentes de *debounce* e recarrega a página (`location.reload()`) **uma vez**, com uma marca na `sessionStorage` para evitar laço;
   - se depois de recarregar ainda receber `426` (o deploy do app web está atrasado em relação à API), mostra a tela bloqueante "Estamos atualizando o AlmoSeis. Tente de novo em alguns minutos", com o botão "Recarregar" e sem link de loja.
4. Ordem de deploy: a API sai compatível com o app web em produção; o app web sai depois; só então a versão mínima de `WEB` sobe.

### 7. Conexão, tempo real e alertas no desktop (complementa os ADRs 0036 e 0049)

1. **SignalR fica conectado enquanto a aba estiver aberta, mesmo oculta ou minimizada.** No celular, o app desconecta em segundo plano (ADR-0036). No desktop, a aba oculta é o caso normal: o atendente está no WhatsApp Web, em outra aba. Ao voltar a aba para a frente (`visibilitychange`), ao voltar a rede (`online`) e ao acordar o computador, o app reconecta e sincroniza pelo feed (ADR-0032).
   - A API fecha a conexão do hub quando o access token vence (`CloseOnAuthenticationExpiration`). O app renova o token antes de reconectar e, se a reconexão automática desistir, tenta de novo com espera. Sem isso, uma aba aberta por dias ficaria conectada com o token vencido.
2. **Som:** o navegador só toca áudio depois de uma interação na página. A cada carregamento, enquanto o som estiver bloqueado, o app mostra a faixa **"Som desativado neste navegador — Ativar som"**, e um clique libera o áudio. Depois disso, o som toca mesmo com a aba oculta. Valem os mesmos sons e regras: pedido novo na cozinha (ADR-0036) e o som próprio dos pedidos do site (ADR-0049, item 9).
3. **Tela ligada:** na aba Pedidos, a Wake Lock API (pela implementação web do `expo-keep-awake` ou por um `.web.ts`), readquirida ao voltar a aba para a frente. Fora do Chrome e do Edge, segue sem ela.
4. **Com a aba oculta:** o título da aba mostra o contador ("(2) Novos do site · AlmoSeis"). Recomendação P1: notificação **local** do navegador (Notifications API, sem servidor de push) para pedido do site aguardando aceite, com a aba aberta. Segue o mesmo conteúdo mínimo do ADR-0049 (sem nome, telefone, endereço nem valor) e a permissão é pedida no mesmo momento do ADR-0049, item 6.
5. **Web Push (VAPID) com o navegador fechado: fica para depois** (P2, Q-26). Motivos:
   - no Windows, o push só chega se o processo do navegador continuar rodando em segundo plano, o que nem sempre acontece;
   - exige *service worker*, chaves VAPID e um segundo caminho de envio no módulo Notifications, porque o push da Expo não atende a web;
   - o computador do balcão é, na prática, o "aparelho fixo" do ADR-0036.

   Quando entrar, encaixa no ADR-0049 sem mudar as regras: `PushRegistration` com `platform = WEB` e a assinatura do navegador (endpoint e chaves) no lugar do `expoPushToken`, o mesmo conteúdo sem dado pessoal, os mesmos destinatários e o envio pelo outbox.
6. Onboarding: no Edge, colocar `app.{dominio}` na lista "nunca suspender" das guias suspensas; no Chrome, nas exceções da Economia de memória. Uma aba suspensa não recebe pedido.

### 8. Bibliotecas afetadas

| Biblioteca | Na web |
|---|---|
| `expo-secure-store` | Não é usado. Tokens conforme o §3 (`src/stores/secure.web.ts`) |
| `expo-sqlite` | Não é usado. IndexedDB conforme o §5 |
| `expo-application` | `installationId` próprio em `localStorage` (§3.7) |
| `expo-network` | `navigator.onLine` e os eventos `online`/`offline`, mais a regra de `degraded` por falha de requisição que já existe |
| `expo-keep-awake` | Wake Lock API (§7.3) |
| `expo-audio` | Áudio do navegador, liberado pelo clique "Ativar som" (§7.2) |
| `expo-notifications` | Não é usado no web nesta fase (§7.5) |
| `react-native-pager-view` (abas superiores) | Não existe na web. Usar o fallback do `react-native-tab-view`, a confirmar. Se não houver, `_layout.web.tsx` com abas clicáveis sem deslizar |
| `expo-image-picker` | Seletor de arquivo do navegador. Ok |
| `expo-crypto` (`randomUUID`) | `crypto.randomUUID()` (exige HTTPS ou `localhost`) |

### 9. Suporte

1. **Navegadores alvo:** Chrome e Edge nas duas versões principais mais recentes, no Windows 10 e 11. Firefox e Safari funcionam sem garantia. O app não bloqueia o acesso: mostra um aviso discreto ("Navegador não testado"). Bloquear ou só avisar é a Q-28.
2. **Janela:** tamanho mínimo suportado de **1024 × 600 px CSS**, o que cobre um notebook de 1366 × 768 com a escala de 125% do Windows. Abaixo de 720 px de largura, o app usa o layout de celular: funciona, mas não é o alvo.
3. **Mouse e teclado:**
   - toda ação tem clique; nenhum gesto (deslizar, pressionar e segurar) é o único caminho;
   - campos com teclado físico, `Enter` confirma formulários, `Esc` fecha diálogos e *sheets*, e a ordem do `Tab` e o foco visível seguem a tela;
   - o teclado numérico do PIN e o campo de gramas do quilo aceitam também os dígitos do teclado físico.
4. **Atalhos de teclado** não entram agora.
5. **Hardware** (impressora de comanda, balança) não entra. O peso do quilo continua digitado (ADR-0030).

### 10. Assinatura (ADR-0017)

O ADR-0017 vale igual na web. O app no navegador **não mostra preço, botão nem link de compra** da assinatura, embora a web não tenha as regras das lojas. A base de código é a mesma, e a cobrança continua pelo link enviado ao proprietário. Mostrar preço só na web exigiria um ADR novo.

## Alternativas consideradas

- **App Windows nativo (React Native Windows, .NET MAUI, WPF):** outra base de código ou outra plataforma de build, e instalação manual em cada computador. Descartado pelo dono do produto.
- **Tauri ou Electron desde já:** dá acesso a hardware e a notificação fora do navegador, mas exige instalador, assinatura de código e atualização automática própria, sem necessidade de hardware hoje. Fica para o futuro (§1.6).
- **Dashboard web separado (outro projeto):** é a alternativa "App + dashboard web" que o ADR-0001 descartou por dobrar o front-end.
- **Reaproveitar o `web/` Next.js do ADR-0051:** mistura a página pública do consumidor (SEO, prévia, sem login) com o app autenticado do restaurante, num desenho de tela diferente.
- **Refresh token em `localStorage`:** descartado no §3.9 (XSS ou extensão roubam a sessão longa).
- **Sessão só com access token, sem refresh na web:** a pessoa entraria de novo a cada 15 min ou a cada F5.
- **Cookie de sessão no lugar do JWT (BFF):** muda o modelo de autenticação da API para um único cliente e exige um servidor para o app web.
- **`expo-sqlite` na web:** descartado no §5.2.
- **Aparelho compartilhado fora da web no MVP:** menos código, mas leva a equipe a usar uma conta só no computador do balcão (Q-24).
- **Web Push agora:** baixo ganho com o navegador fechado no Windows (§7.5).

## Consequências

- O ADR-0001 é **complementado**: o produto do restaurante passa a ser "o app em Android, iOS e no navegador do desktop". Continua sem dashboard web separado: é o mesmo app.
- As razões que levaram o ADR-0001 a descartar o PWA viram riscos aceitos e mitigados:
  - **armazenamento local:** IndexedDB pode ser apagado pelo navegador ou pela pessoa. Mitigação: `storage.persist()` e pendências conferíveis no histórico (§5);
  - **segundo plano:** não há alerta com o navegador fechado, e a aba pode ser suspensa. Mitigação: SignalR com a aba oculta, título com contador, notificação local, orientação de onboarding e expiração visível do pedido do site (§7). Web Push depois;
  - **aparelho de entrada:** não se aplica, porque o alvo é um computador. O build web não tem a meta de tamanho da página pública (RNF-201).
- **Sem uso offline ao recarregar:** sem *service worker* com cache, um F5 com a internet caída não abre o app. Com a aba já aberta, o comportamento é o do celular (cache e rascunhos, ADR-0010). Revisitar se o piloto mostrar o problema (Q-27).
- **Dados no navegador (ADR-0024):** o cache persistido inclui nomes e telefones de clientes do fiado. No computador do balcão, qualquer pessoa ou extensão com acesso ao perfil do navegador consegue lê-los. Mitigação: sair da conta apaga os dados do tenant; o onboarding orienta usar um perfil de navegador só para o AlmoSeis, instalado como PWA, e sem extensões.
- **Login pessoal no computador compartilhado:** um proprietário que entra pela própria conta e não sai deixa o acesso dele aberto por até 30 dias. Mitigação: orientar o modo compartilhado com PIN no balcão (Q-25).
- Trabalho novo:
  - API: CORS, cookies, CSRF, `WEB` em `X-App-Platform`, `MinSupportedAppVersion` e `SharedDevice.platform`, e a rota do aparelho compartilhado sob `/auth`;
  - mobile: arquivos `.web.ts`, repositório local sobre IndexedDB, lock entre abas, faixa "Ativar som" e manifesto PWA;
  - devops: Static Web Apps, DNS, CSP e o workflow `mobile-web.yml`;
  - testes: E2E web, por exemplo com Playwright, no lugar do Maestro para essa plataforma.
- Testes de segurança dedicados:
  - cookie nunca no corpo;
  - refresh vindo da web recusado no corpo;
  - `Origin` fora da lista recusado;
  - reuso de refresh entre abas;
  - `deviceSecret` revogado.
- Revisitar: impressora ou balança (Tauri/Electron); pedidos expirando no desktop (Web Push); muitos usuários em Firefox ou Safari.
