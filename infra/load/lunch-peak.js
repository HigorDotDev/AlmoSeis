// Teste de carga do pico do almoço (infra/load/RESULTS.md, docs/09-devops.md §9.11).
//
// Simula N estabelecimentos (semente em .out/seed.json) no pico 11h–14h, com o tempo COMPRIMIDO e a TAXA REAL:
// cada meia hora do pico vira SLOT (padrão 2 min; a meia hora de 12h00 vira PEAK_SLOT, padrão 3 min), sempre com as
// chegadas por hora do dia real. LOAD_MULTIPLIER=3 é o estresse (3× pedidos, 3× aparelhos por estabelecimento).
//
// Volume (por estabelecimento): 150 pedidos/dia, 70% entre 11h e 14h (105), por meia hora 10/20/30/20/12/8%.
// No pico (12h00–12h30): 31,5 pedidos em 30 min por estabelecimento → 30 estabelecimentos = 1.890 pedidos/h
// (0,525/s), 85% pelo app (1.607/h) e 15% pela página pública (284/h). Ver RESULTS.md para as taxas derivadas.
//
// Cenários:
//   devices    um VU por aparelho (2–4 por estabelecimento, × LOAD_MULTIPLIER): login, SignalR (/hubs/realtime),
//              refetch por evento como o app (TanStack invalidado pelo evento), /sync/changes no ritmo do syncEngine
//              (no máx. a cada 10 s quando houve evento, senão a cada 60 s), "Novos do site" a cada 30 s, painel da
//              cozinha, resumo do caixa; a cozinha avança a produção; o dono aceita e recebe os pedidos do site.
//   app_orders chegada de pedidos pelo app (ramping-arrival-rate): cria (Idempotency-Key, 2% com reenvio), inclui
//              itens ou pesagem (30%), recebe (1 ou 2 pagamentos, 5% fiado), marca entrega/retirada.
//   web_orders pedido pela página: SSR da página (web), GET do perfil/cardápio, POST (Turnstile desligado no
//              ambiente de teste), acompanhamento.
//   browse     visitas sem compra (4 por pedido do site): SSR + GET do perfil.
//
// Rodar: infra/load/run.sh (sobe o compose da VM com limites, semeia e chama o k6 em contêiner na rede do compose).
// O k6 fala direto com api:8080 (sem o Caddy) e manda X-Forwarded-For: a sub-rede do compose é o proxy confiável da
// API (ForwardedHeaders:KnownNetworks), então cada aparelho e cada cliente da página tem o seu IP, como na vida real,
// e os limites por IP (ADR-0041) continuam LIGADOS.

import http from 'k6/http';
import ws from 'k6/ws';
import { check, sleep } from 'k6';
import exec from 'k6/execution';
import encoding from 'k6/encoding';
import { Counter, Rate, Trend } from 'k6/metrics';

// ---------------------------------------------------------------------------------------------------------------------
// Configuração

const SEED = JSON.parse(open(__ENV.SEED_FILE || '/load/.out/seed.json'));
const API = __ENV.API_URL || 'http://api:8080';
const WS_API = API.replace(/^http/, 'ws');
const WEB = __ENV.WEB_URL || 'http://web:3000';
const MULT = Number(__ENV.LOAD_MULTIPLIER || 1);
const TENANTS = SEED.tenants.slice(0, Number(__ENV.TENANTS || SEED.tenants.length));
const SLOT_S = Number(__ENV.SLOT_SECONDS || 120);
const PEAK_SLOT_S = Number(__ENV.PEAK_SLOT_SECONDS || 180);
const WARMUP_S = 75; // logins dos aparelhos espalhados em 60 s + conexões do SignalR
const ORDERS_PER_DAY = Number(__ENV.ORDERS_PER_DAY || 150);
const PEAK_SHARE = 0.7;
const SLOT_SHARES = [0.10, 0.20, 0.30, 0.20, 0.12, 0.08]; // 11h00, 11h30, 12h00, 12h30, 13h00, 13h30
const WEB_SHARE = 0.15;
const BROWSE_PER_WEB_ORDER = 4;
const APP_VERSION = '1.0.0';
const COMPRESSION = 1800 / SLOT_S; // meia hora real → SLOT_S segundos: as esperas dentro de um pedido encolhem igual

const slotDurations = SLOT_SHARES.map((_, i) => (i === 2 ? PEAK_SLOT_S : SLOT_S));
const PEAK_TOTAL_S = slotDurations.reduce((a, b) => a + b, 0);
const END_S = WARMUP_S + PEAK_TOTAL_S;

/** Chegadas por HORA (tempo real do dia) somando todos os estabelecimentos, por meia hora do pico. */
function hourlyRate(share, fraction) {
  const perHalfHour = TENANTS.length * ORDERS_PER_DAY * PEAK_SHARE * share * MULT;
  return Math.max(1, Math.round(perHalfHour * 2 * fraction));
}

function stagesFor(fraction) {
  const stages = [];
  SLOT_SHARES.forEach((share, i) => {
    const target = hourlyRate(share, fraction);
    stages.push({ target, duration: '10s' });
    stages.push({ target, duration: `${slotDurations[i] - 10}s` });
  });
  return stages;
}

// Aparelhos: cada (estabelecimento, aparelho, réplica do multiplicador) é um VU.
const DEVICES = [];
TENANTS.forEach((t, ti) => {
  for (let m = 0; m < MULT; m++) {
    t.devices.forEach((d, di) => DEVICES.push({ ti, di, m, role: d.role, login: d.login, password: d.password }));
  }
});

const ROUTES_WRITE = [
  'POST /auth/login', 'POST /auth/refresh', 'POST /orders', 'POST /orders (replay)', 'POST /orders/{id}/items',
  'POST /orders/{id}/weighings', 'POST /orders/{id}/payments', 'PATCH /orders/{id}/status', 'PATCH /orders/{id}/fulfillment',
  'POST /orders/{id}/accept', 'POST /public/stores/{slug}/orders',
];
const ROUTES_READ = [
  'GET /sync/changes', 'GET /orders', 'GET /orders (awaiting)', 'GET /orders/{id}', 'GET /production/orders',
  'GET /business-days/current', 'GET /closing/summary', 'GET /public/stores/{slug}', 'GET /public/tracking/{token}',
  'POST /hubs/realtime/negotiate',
];
const ROUTES_PAGE = ['GET web /{slug}'];

const thresholds = {
  'http_req_duration{kind:write}': ['p(95)<500'],
  'http_req_duration{kind:read}': ['p(95)<300'],
  http_req_failed: ['rate<0.005'],
  checks: ['rate>0.995'],
  unexpected_responses: ['rate<0.005'],
  'realtime_event_lag_ms': ['p(95)<3000'],
};
// Submétricas só para aparecerem no resumo por rota (limite largo, informativo).
for (const name of [...ROUTES_WRITE, ...ROUTES_READ, ...ROUTES_PAGE]) {
  thresholds[`http_req_duration{name:${name}}`] = ['max>=0'];
}
thresholds['http_req_duration{kind:page}'] = ['max>=0'];

export const options = {
  discardResponseBodies: false,
  summaryTrendStats: ['avg', 'min', 'med', 'p(90)', 'p(95)', 'p(99)', 'max', 'count'],
  setupTimeout: '5m',
  thresholds,
  scenarios: {
    devices: {
      executor: 'per-vu-iterations',
      exec: 'device',
      vus: DEVICES.length,
      iterations: 1,
      maxDuration: `${END_S + 150}s`,
      gracefulStop: '30s',
    },
    app_orders: {
      executor: 'ramping-arrival-rate',
      exec: 'appOrder',
      startTime: `${WARMUP_S}s`,
      timeUnit: '1h',
      startRate: hourlyRate(SLOT_SHARES[0], 1 - WEB_SHARE),
      preAllocatedVUs: Math.ceil(60 * MULT),
      maxVUs: Math.ceil(400 * MULT),
      stages: stagesFor(1 - WEB_SHARE),
      gracefulStop: '120s',
    },
    web_orders: {
      executor: 'ramping-arrival-rate',
      exec: 'webOrder',
      startTime: `${WARMUP_S}s`,
      timeUnit: '1h',
      startRate: hourlyRate(SLOT_SHARES[0], WEB_SHARE),
      preAllocatedVUs: Math.ceil(10 * MULT),
      maxVUs: Math.ceil(80 * MULT),
      stages: stagesFor(WEB_SHARE),
      gracefulStop: '120s',
    },
    browse: {
      executor: 'ramping-arrival-rate',
      exec: 'browse',
      startTime: `${WARMUP_S}s`,
      timeUnit: '1h',
      startRate: hourlyRate(SLOT_SHARES[0], WEB_SHARE * BROWSE_PER_WEB_ORDER),
      preAllocatedVUs: Math.ceil(10 * MULT),
      maxVUs: Math.ceil(60 * MULT),
      stages: stagesFor(WEB_SHARE * BROWSE_PER_WEB_ORDER),
      gracefulStop: '30s',
    },
  },
};

// ---------------------------------------------------------------------------------------------------------------------
// Métricas próprias

const unexpected = new Rate('unexpected_responses');
const versionConflicts = new Counter('version_conflicts');
const realtimeLag = new Trend('realtime_event_lag_ms', true);
const realtimeEvents = new Counter('realtime_events');
const realtimeDisconnects = new Counter('realtime_disconnects');
const ordersCreated = new Counter('orders_created');
const webOrdersCreated = new Counter('web_orders_created');
const webOrdersAccepted = new Counter('web_orders_accepted');
const paymentsRegistered = new Counter('payments_registered');
const productionMoves = new Counter('production_moves');
const relogins = new Counter('relogins');

// ---------------------------------------------------------------------------------------------------------------------
// Utilitários

function rnd(a, b) { return a + Math.random() * (b - a); }
function rint(a, b) { return Math.floor(rnd(a, b + 1)); }
function pick(arr) { return arr[Math.floor(Math.random() * arr.length)]; }
function chance(p) { return Math.random() < p; }
function uuid() {
  return 'xxxxxxxx-xxxx-4xxx-yxxx-xxxxxxxxxxxx'.replace(/[xy]/g, (c) => {
    const r = (Math.random() * 16) | 0;
    return (c === 'x' ? r : (r & 0x3) | 0x8).toString(16);
  });
}
/** Espera comprimida: `realSeconds` do dia real viram realSeconds / COMPRESSION, com um mínimo. */
function think(realSecondsMin, realSecondsMax, minSeconds = 1) {
  sleep(Math.max(minSeconds, rnd(realSecondsMin, realSecondsMax) / COMPRESSION));
}
function cents(money) { return Math.round(Number(money) * 100); }
function money(c) { return (c / 100).toFixed(2); }
function deviceIp(ti, di, m) { return `10.${20 + m}.${ti + 1}.${di + 1}`; }
function orderingIp(ti) { return `10.10.${ti + 1}.1`; }
function customerIp() { return `100.${rint(64, 127)}.${rint(0, 255)}.${rint(1, 254)}`; }
function phoneFor(ti) { return `+55349${String(10000000 + ti * 1000 + rint(0, 199)).padStart(8, '0')}`; }
function nowSeconds() { return Math.floor(Date.now() / 1000); }
function jwtExp(token) {
  try {
    return JSON.parse(encoding.b64decode(token.split('.')[1], 'rawurl', 's')).exp;
  } catch (e) {
    return nowSeconds() + 14 * 60;
  }
}

function appHeaders(token, ip, extra) {
  const h = {
    'Content-Type': 'application/json',
    'X-App-Version': APP_VERSION,
    'X-App-Platform': 'ANDROID',
    'X-Forwarded-For': ip,
  };
  if (token) h.Authorization = `Bearer ${token}`;
  return Object.assign(h, extra || {});
}

/**
 * Uma chamada à API. `expected` = status aceitos (os outros contam em unexpected_responses e http_req_failed).
 * `kind` read | write define o limite (p95 300 / 500 ms).
 */
function call(method, path, { token, ip, body, name, kind, expected = [200, 201, 204], headers, base = API }) {
  const params = {
    headers: base === API ? appHeaders(token, ip, headers) : Object.assign({ 'X-Forwarded-For': ip }, headers || {}),
    tags: { name, kind },
    responseCallback: http.expectedStatuses(...expected),
    timeout: '30s',
  };
  const res = http.request(method, `${base}${path}`, body === undefined ? null : JSON.stringify(body), params);
  const ok = expected.includes(res.status);
  unexpected.add(!ok, { name });
  check(res, { [`${name} → ${expected.join('/')}`]: () => ok });
  if (!ok && __ENV.DEBUG_ERRORS) {
    console.warn(`${name} ${res.status} ${String(res.body).slice(0, 300)}`);
  }
  return res;
}

function json(res) {
  try { return res.json(); } catch (e) { return null; }
}

/**
 * Login. Com `retryOn429`, espera o Retry-After e tenta de novo, como o app (08 §8.15): no estresse, o mesmo usuário
 * entra em vários aparelhos e passa do limite por login (5/min, ADR-0041), o que é esperado e não é falha.
 */
function login(dev, ip, installationId, retryOn429 = false) {
  let res;
  for (let attempt = 0; attempt < 8; attempt++) {
    res = call('POST', '/api/v1/auth/login', {
      ip, name: 'POST /auth/login', kind: 'write', expected: retryOn429 ? [200, 429] : [200],
      body: { login: dev.login, password: dev.password, installationId, preferredTenantId: null },
    });
    if (res.status !== 429 || !retryOn429) break;
    sleep(Number(res.headers['Retry-After'] || 10) + Math.random());
  }
  const b = json(res);
  if (res.status !== 200 || !b) return null;
  return { token: b.tokens.accessToken, refresh: b.tokens.refreshToken, exp: jwtExp(b.tokens.accessToken), installationId, ip };
}

// ---------------------------------------------------------------------------------------------------------------------
// setup: uma sessão de "atendimento" por estabelecimento (o aparelho que lança os pedidos do app).

export function setup() {
  const sessions = TENANTS.map((t, ti) => {
    const owner = t.devices[0];
    const s = login(owner, orderingIp(ti), `load-ordering-${ti}-${uuid().slice(0, 8)}`);
    if (!s) throw new Error(`login da sessão de pedidos do tenant ${t.slug} falhou`);
    return s;
  });
  return { sessions, startedAt: Date.now() };
}

// Sessão de pedidos por VU: o token do setup; se vencer (15 min), o VU entra de novo e guarda (sem refresh
// compartilhado entre VUs, que giraria o refresh token de outra pessoa).
const vuSessions = {};
function orderingSession(data, ti) {
  const s = vuSessions[ti] || data.sessions[ti];
  if (s.exp - nowSeconds() > 180) return s; // a maior espera dentro de um pedido é ~120 s
  const fresh = login(TENANTS[ti].devices[0], orderingIp(ti), `load-ordering-${ti}-vu${exec.vu.idInTest}`, true);
  relogins.add(1);
  vuSessions[ti] = fresh || s;
  return vuSessions[ti];
}

// ---------------------------------------------------------------------------------------------------------------------
// Montagem dos pedidos (contrato 06 §6.9)

const STREETS = ['Rua das Flores', 'Av. Rondon Pacheco', 'Rua Goiás', 'Rua Bahia', 'Av. João Naves'];
const NAMES = ['João', 'Maria', 'Ana', 'Pedro', 'Carla', 'Lucas', 'Juliana', 'Marcos', 'Fernanda', 'Rafael'];

function address() {
  return { street: pick(STREETS), number: String(rint(10, 2000)), complement: null, neighborhood: 'Centro', reference: null };
}

function baseOrder(orderType, fulfillmentType) {
  return {
    orderType, fulfillmentType, customerId: null, customerName: null, customerPhone: null, saveCustomer: false,
    customerAddressId: null, deliveryAddress: null, deliveryFee: null, tableLabel: null, tabNumber: null, notes: null,
    expectedPaymentMethod: null, changeFor: null, items: [], weighings: [],
  };
}

function restaurantOrder(t, ti) {
  const c = t.catalog;
  const r = Math.random();
  if (r < 0.5) {
    const delivery = chance(0.4);
    const o = baseOrder('MARMITA', delivery ? 'DELIVERY' : 'PICKUP');
    o.customerName = `${pick(NAMES)} ${rint(1, 99)}`;
    if (delivery || chance(0.5)) o.customerPhone = phoneFor(ti);
    if (delivery) {
      o.deliveryAddress = address();
      o.expectedPaymentMethod = pick(['PIX', 'CASH', 'CARD']);
    }
    const n = rint(1, 3);
    for (let i = 0; i < n; i++) {
      o.items.push({
        itemKind: 'MARMITA', marmitaSizeId: pick(c.marmitaSizes).id, quantity: 1,
        mainDishProductIds: [pick(c.mainDishes)], sideDishProductIds: [c.sideDishes[0], c.sideDishes[1 + rint(0, c.sideDishes.length - 2)]],
        notes: chance(0.2) ? 'Sem cebola' : null,
        addons: chance(0.3) ? [{ productId: pick(c.addons).id, quantity: 1, notes: null }] : [],
      });
    }
    if (chance(0.5)) o.items.push({ itemKind: 'BEVERAGE', productId: pick(c.beverages).id, quantity: 1, notes: null });
    return o;
  }
  if (r < 0.75) {
    const o = baseOrder('PF', 'DINE_IN');
    o.tableLabel = String(rint(1, 20));
    o.items.push({ itemKind: 'PF', productId: pick(c.mainDishes), quantity: rint(1, 2), notes: null, addons: [] });
    if (chance(0.6)) o.items.push({ itemKind: 'BEVERAGE', productId: pick(c.beverages).id, quantity: rint(1, 2), notes: null });
    return o;
  }
  const o = baseOrder('KILO', 'DINE_IN');
  o.tableLabel = String(rint(1, 20));
  o.tabNumber = String(rint(1, 200));
  o.weighings.push({ weightGrams: rint(280, 900) });
  if (chance(0.5)) o.items.push({ itemKind: 'BEVERAGE', productId: pick(c.beverages).id, quantity: 1, notes: null });
  return o;
}

function fixedDishItem(c) {
  const d = pick(c.dishes);
  const addons = d.addonGroupIds.length > 0 && chance(0.4) ? [{ addonOptionId: pick(c.addonOptions), quantity: 1 }] : [];
  return {
    itemKind: 'FIXED_DISH', dishId: d.id, dishSizeId: d.sizeIds.length > 0 ? pick(d.sizeIds) : null,
    quantity: rint(1, 2), notes: null, groupAddons: addons,
  };
}

function fixedBeverageItem(c) {
  const b = pick(c.beverageSizes);
  return { itemKind: 'FIXED_BEVERAGE', beverageSizeId: b.id, beverageFlavorId: b.flavorIds.length > 0 ? pick(b.flavorIds) : null, quantity: 1, notes: null };
}

function comboItem(c) {
  const combo = pick(c.combos);
  return {
    itemKind: 'COMBO', comboId: combo.id, quantity: 1,
    parts: combo.components.map((k) => {
      if (k.kind === 'BEVERAGE') {
        const size = c.beverageSizes.find((b) => b.id === k.beverageSizeId);
        return { comboComponentId: k.id, beverageFlavorId: size && size.flavorIds.length > 0 ? pick(size.flavorIds) : null, notes: null, groupAddons: null };
      }
      return { comboComponentId: k.id, beverageFlavorId: null, notes: chance(0.2) ? 'Sem cebola' : null, groupAddons: null };
    }),
  };
}

function snackOrder(t, ti) {
  const c = t.catalog;
  const r = Math.random();
  const fulfillment = r < 0.5 ? 'DINE_IN' : r < 0.8 ? 'DELIVERY' : 'PICKUP';
  const o = baseOrder('A_LA_CARTE', fulfillment);
  if (fulfillment === 'DINE_IN') {
    o.tableLabel = String(rint(1, 15));
  } else {
    o.customerName = `${pick(NAMES)} ${rint(1, 99)}`;
    o.customerPhone = phoneFor(ti);
  }
  if (fulfillment === 'DELIVERY') {
    o.deliveryAddress = address();
    o.deliveryFee = '5.00';
    o.expectedPaymentMethod = 'CASH';
    o.changeFor = '300.00';
  }
  if (chance(0.5)) o.items.push(comboItem(c));
  const dishes = o.items.length === 0 ? rint(1, 2) : rint(0, 1);
  for (let i = 0; i < dishes; i++) o.items.push(fixedDishItem(c));
  if (chance(0.6)) o.items.push(fixedBeverageItem(c));
  return o;
}

// ---------------------------------------------------------------------------------------------------------------------
// Escritas com If-Match: em 409 VersionConflict o app recarrega e tenta de novo (06 §6.1, ADR-0034).

function writeWithVersion(method, path, { s, body, name, version, idempotent, retries = 3 }) {
  let v = version;
  for (let attempt = 0; attempt <= retries; attempt++) {
    const headers = { 'If-Match': `"${v}"` };
    if (idempotent) headers['Idempotency-Key'] = uuid();
    const res = call(method, path, { token: s.token, ip: s.ip, body, name, kind: 'write', headers, expected: [200, 201, 409] });
    if (res.status !== 409) return res;
    const b = json(res);
    if (!b || b.errorCode !== 'VersionConflict') {
      unexpected.add(true, { name: `${name} (409 ${b && b.errorCode})` });
      return res;
    }
    versionConflicts.add(1, { name });
    v = b.currentVersion;
  }
  return null;
}

function payOrder(s, orderId, version, balanceCents, split) {
  let v = version;
  let remaining = balanceCents;
  const parts = split && remaining >= 200 ? [Math.floor(remaining / 2), remaining - Math.floor(remaining / 2)] : [remaining];
  for (const amount of parts) {
    const method = pick(['CASH', 'PIX', 'PIX', 'CARD']);
    const body = { amount: money(amount), paymentMethod: method, cashReceived: method === 'CASH' ? money(Math.ceil(amount / 1000) * 1000) : null, label: split ? 'Pessoa' : null };
    const res = writeWithVersion('POST', `/api/v1/orders/${orderId}/payments`, { s, body, name: 'POST /orders/{id}/payments', version: v, idempotent: true });
    const b = res && json(res);
    if (!res || res.status !== 201 || !b) return v;
    paymentsRegistered.add(1);
    v = b.order.version;
    remaining -= amount;
  }
  return v;
}

// ---------------------------------------------------------------------------------------------------------------------
// Cenário app_orders: um pedido do app, do lançamento ao recebimento.

export function appOrder(data) {
  const ti = Math.floor(Math.random() * TENANTS.length);
  const t = TENANTS[ti];
  let s = orderingSession(data, ti);
  const body = t.behavior === 'RESTAURANT' ? restaurantOrder(t, ti) : snackOrder(t, ti);
  const key = uuid();
  const res = call('POST', '/api/v1/orders', { token: s.token, ip: s.ip, body, name: 'POST /orders', kind: 'write', expected: [201], headers: { 'Idempotency-Key': key } });
  let order = json(res);
  if (res.status !== 201 || !order) return;
  ordersCreated.add(1, { orderType: body.orderType });
  if (chance(0.02)) {
    // Reenvio da mesma operação (rede instável): mesma chave e corpo → a resposta original.
    const replay = call('POST', '/api/v1/orders', { token: s.token, ip: s.ip, body, name: 'POST /orders (replay)', kind: 'write', expected: [201], headers: { 'Idempotency-Key': key } });
    check(replay, { 'reenvio idempotente devolve a resposta original': (r) => r.headers['Idempotent-Replayed'] === 'true' });
  }
  let version = order.version;
  const id = order.id;

  // Incluir itens ou pesagem (30%): 1 a 8 min depois, no dia real.
  if (chance(0.3)) {
    think(60, 480);
    s = orderingSession(data, ti);
    let r;
    if (body.orderType === 'KILO') {
      r = writeWithVersion('POST', `/api/v1/orders/${id}/weighings`, { s, body: { weighings: [{ weightGrams: rint(150, 500) }] }, name: 'POST /orders/{id}/weighings', version, idempotent: true });
    } else {
      const item = body.orderType === 'A_LA_CARTE'
        ? fixedBeverageItem(t.catalog)
        : { itemKind: 'BEVERAGE', productId: pick(t.catalog.beverages).id, quantity: 1, notes: null };
      r = writeWithVersion('POST', `/api/v1/orders/${id}/items`, { s, body: { items: [item] }, name: 'POST /orders/{id}/items', version, idempotent: true });
    }
    const b = r && json(r);
    if (r && r.status === 200 && b) version = b.version;
  }

  // Refeição / preparo: 10 a 30 min no dia real. Depois o caixa abre o pedido (GET) e recebe.
  think(600, 1800, 5);
  s = orderingSession(data, ti);
  const current = call('GET', `/api/v1/orders/${id}`, { token: s.token, ip: s.ip, name: 'GET /orders/{id}', kind: 'read', expected: [200] });
  order = json(current);
  if (!order) return;
  version = order.version;
  if (!chance(0.05)) {
    version = payOrder(s, id, version, cents(order.balance), chance(0.15));
  }

  // Entrega e retirada: "Concluir e marcar" (conclui a produção junto) e, na entrega, "Entregue".
  if (body.fulfillmentType === 'DELIVERY' || (body.fulfillmentType === 'PICKUP' && chance(0.5))) {
    s = orderingSession(data, ti);
    const to = body.fulfillmentType === 'DELIVERY' ? 'OUT_FOR_DELIVERY' : 'PICKED_UP';
    const r = writeWithVersion('PATCH', `/api/v1/orders/${id}/fulfillment`, { s, body: { fulfillmentStatus: to, completeProduction: true }, name: 'PATCH /orders/{id}/fulfillment', version });
    const b = r && json(r);
    if (body.fulfillmentType === 'DELIVERY' && b && r.status === 200) {
      think(600, 1500, 3);
      s = orderingSession(data, ti);
      writeWithVersion('PATCH', `/api/v1/orders/${id}/fulfillment`, { s, body: { fulfillmentStatus: 'DELIVERED', completeProduction: false }, name: 'PATCH /orders/{id}/fulfillment', version: b.version });
    }
  }
}

// ---------------------------------------------------------------------------------------------------------------------
// Página pública

function storePage(t) {
  return call('GET', `/${t.slug}`, { base: WEB, ip: customerIp(), name: 'GET web /{slug}', kind: 'page', expected: [200] });
}

function storeApi(t, ip) {
  return call('GET', `/api/v1/public/stores/${t.slug}`, { ip, name: 'GET /public/stores/{slug}', kind: 'read', expected: [200] });
}

export function browse() {
  const t = pick(TENANTS);
  storePage(t);
  think(30, 120);
  storeApi(t, customerIp());
}

function webCart(t, store) {
  if (store.sections.includes('MARMITA') && store.menu) {
    const size = pick(store.menu.marmitaSizes);
    const mains = store.menu.mainDishes.filter((d) => d.isAvailable);
    const sides = store.menu.sideDishes.filter((d) => d.isAvailable);
    const bev = store.menu.beverages.length > 0 && chance(0.5) ? pick(store.menu.beverages) : null;
    const qty = rint(1, 2);
    const items = [{
      itemKind: 'MARMITA', marmitaSizeId: size.id, productId: null, quantity: qty,
      mainDishProductIds: [pick(mains).productId], sideDishProductIds: sides.slice(0, 2).map((d) => d.productId), addons: [], notes: null,
    }];
    let total = cents(size.price) * qty;
    if (bev) {
      items.push({ itemKind: 'BEVERAGE', marmitaSizeId: null, productId: bev.productId, quantity: 1, mainDishProductIds: null, sideDishProductIds: null, addons: null, notes: null });
      total += cents(bev.price);
    }
    return { orderType: 'MARMITA', items, total };
  }
  const fm = store.fixedMenu;
  const dishes = fm.categories.flatMap((cat) => cat.dishes);
  const d = pick(dishes);
  const size = d.hasSizes ? pick(d.sizes) : null;
  const items = [{ itemKind: 'FIXED_DISH', dishId: d.id, dishSizeId: size ? size.id : null, quantity: 1, notes: null, groupAddons: [] }];
  let total = cents(size ? size.price : d.price);
  if (fm.beverages.length > 0 && chance(0.6)) {
    const bsize = pick(pick(fm.beverages).sizes);
    const flavor = bsize.flavors.length > 0 ? pick(bsize.flavors) : null;
    items.push({ itemKind: 'FIXED_BEVERAGE', beverageSizeId: bsize.id, beverageFlavorId: flavor ? flavor.id : null, quantity: 1, notes: null });
    total += cents(bsize.price) + (flavor ? cents(flavor.extraPrice) : 0);
  }
  return { orderType: 'A_LA_CARTE', items, total };
}

export function webOrder() {
  const ti = Math.floor(Math.random() * TENANTS.length);
  const t = TENANTS[ti];
  const ip = customerIp();
  storePage(t);
  const storeRes = storeApi(t, ip);
  const store = json(storeRes);
  if (!store || store.status !== 'OPEN') return;
  think(60, 240);
  const cart = webCart(t, store);
  const body = {
    orderType: cart.orderType, fulfillmentType: 'PICKUP', customerName: `${pick(NAMES)} Site`, customerPhone: `(34) 9${rint(8000, 9999)}-${rint(1000, 9999)}`,
    deliveryAddress: null, deliveryQuoteId: null, expectedPaymentMethod: 'PIX', changeFor: null, notes: null,
    items: cart.items, expectedTotal: money(cart.total), website: null,
  };
  const res = call('POST', `/api/v1/public/stores/${t.slug}/orders`, {
    ip, body, name: 'POST /public/stores/{slug}/orders', kind: 'write', expected: [201],
    headers: { 'Idempotency-Key': uuid(), 'X-Turnstile-Token': 'loadtest-turnstile-disabled' },
  });
  const b = json(res);
  if (res.status !== 201 || !b) return;
  webOrdersCreated.add(1);
  for (let i = 0; i < 2; i++) {
    think(120, 600);
    call('GET', `/api/v1/public/tracking/${b.trackingToken}`, { ip, name: 'GET /public/tracking/{token}', kind: 'read', expected: [200] });
  }
}

// ---------------------------------------------------------------------------------------------------------------------
// Cenário devices: o aparelho com o app aberto durante o pico.

export function device() {
  const idx = exec.scenario.iterationInTest;
  const dev = DEVICES[idx];
  const t = TENANTS[dev.ti];
  // Logins espalhados nos primeiros 60 s (o pessoal chegando e abrindo o app).
  sleep((idx / DEVICES.length) * 60);
  const ip = deviceIp(dev.ti, dev.di, dev.m);
  let s = login(dev, ip, `load-dev-${dev.ti}-${dev.di}-${dev.m}-${uuid().slice(0, 8)}`, true);
  if (!s) return;
  const endAt = (exec.scenario.startTime) + END_S * 1000 + 60000;
  const isKitchen = dev.role === 'KITCHEN';
  const isCashier = dev.role === 'CASHIER';
  const acceptsWebOrders = dev.role === 'OWNER' && dev.m === 0;
  const st = { lastSeq: 0, lastSync: 0, lastEventAt: 0, dirtyOrders: false, dirtyProduction: false, dirtyClosing: false, dirtyAwaiting: false,
    lastOrders: 0, lastProduction: 0, lastAwaiting: 0, lastClosing: 0, lastDay: 0, production: null, productionAt: 0 };

  const get = (path, name) => call('GET', path, { token: s.token, ip, name, kind: 'read', expected: [200] });

  function refreshIfNeeded() {
    if (s.exp - nowSeconds() > 120) return;
    const res = call('POST', '/api/v1/auth/refresh', { ip, name: 'POST /auth/refresh', kind: 'write', body: { refreshToken: s.refresh, installationId: s.installationId } });
    const b = json(res);
    if (res.status === 200 && b) s = Object.assign({}, s, { token: b.accessToken, refresh: b.refreshToken, exp: jwtExp(b.accessToken) });
  }

  function sync(nowMs) {
    let more = true;
    let guard = 0;
    while (more && guard++ < 5) {
      const res = get(`/api/v1/sync/changes?since=${st.lastSeq}&limit=500`, 'GET /sync/changes');
      const b = json(res);
      if (!b) return;
      st.lastSeq = b.lastSeq;
      more = b.hasMore;
    }
    st.lastSync = nowMs;
  }

  function kitchenWork(nowMs) {
    if (!st.production) return;
    let moves = 0;
    const open = (st.production.groups.OPEN || []).slice(0, 3);
    const doing = (st.production.groups.IN_PROGRESS || []).slice(0, 3);
    for (const o of doing) {
      if (moves >= 4) break;
      const age = (nowMs - Date.parse(o.acceptedAt)) / 1000;
      if (age < 600 / COMPRESSION) continue; // ~10 min de preparo no dia real
      const r = writeWithVersion('PATCH', `/api/v1/orders/${o.id}/status`, { s, body: { productionStatus: 'COMPLETED' }, name: 'PATCH /orders/{id}/status', version: o.version, retries: 0 });
      if (r && r.status === 200) { productionMoves.add(1); moves++; }
    }
    for (const o of open) {
      if (moves >= 4) break;
      const age = (nowMs - Date.parse(o.acceptedAt)) / 1000;
      if (age < 120 / COMPRESSION) continue;
      const r = writeWithVersion('PATCH', `/api/v1/orders/${o.id}/status`, { s, body: { productionStatus: 'IN_PROGRESS' }, name: 'PATCH /orders/{id}/status', version: o.version, retries: 0 });
      if (r && r.status === 200) { productionMoves.add(1); moves++; }
    }
    if (moves > 0) st.production = null; // recarrega no próximo tique
  }

  function acceptWebOrders() {
    const res = get('/api/v1/orders?acceptanceStatus=AWAITING_ACCEPTANCE&limit=50', 'GET /orders (awaiting)');
    const b = json(res);
    if (!b) return;
    for (const o of b.items.slice(0, 5)) {
      const r = writeWithVersion('POST', `/api/v1/orders/${o.id}/accept`, { s, body: { estimatedReadyMinutes: 30 }, name: 'POST /orders/{id}/accept', version: o.version, idempotent: true });
      const accepted = r && json(r);
      if (!r || r.status !== 200 || !accepted) continue;
      webOrdersAccepted.add(1);
      payOrder(s, accepted.id, accepted.version, cents(accepted.balance), false);
    }
  }

  function tick() {
    const nowMs = Date.now();
    refreshIfNeeded();
    // syncEngine: com evento novo, no máximo a cada 10 s; sem evento, a cada 60 s (volta ao primeiro plano etc.).
    if ((st.lastEventAt > st.lastSync && nowMs - st.lastSync >= 10000) || nowMs - st.lastSync >= 60000) sync(nowMs);
    if (isKitchen) {
      if ((st.dirtyProduction && nowMs - st.lastProduction >= 2000) || nowMs - st.lastProduction >= 60000) {
        const b = json(get('/api/v1/production/orders', 'GET /production/orders'));
        if (b) { st.production = b; st.productionAt = nowMs; }
        st.lastProduction = nowMs;
        st.dirtyProduction = false;
      }
      kitchenWork(nowMs);
    } else {
      if ((st.dirtyOrders && nowMs - st.lastOrders >= 2000) || nowMs - st.lastOrders >= 30000) {
        get('/api/v1/orders?limit=50', 'GET /orders');
        st.lastOrders = nowMs;
        st.dirtyOrders = false;
      }
      if ((st.dirtyAwaiting && nowMs - st.lastAwaiting >= 2000) || nowMs - st.lastAwaiting >= 30000) {
        if (acceptsWebOrders) acceptWebOrders();
        else get('/api/v1/orders?acceptanceStatus=AWAITING_ACCEPTANCE&limit=50', 'GET /orders (awaiting)');
        st.lastAwaiting = nowMs;
        st.dirtyAwaiting = false;
      }
      if (isCashier && ((st.dirtyClosing && nowMs - st.lastClosing >= 5000) || nowMs - st.lastClosing >= 60000)) {
        get(`/api/v1/closing/summary?businessDayId=${t.businessDayId}`, 'GET /closing/summary');
        st.lastClosing = nowMs;
        st.dirtyClosing = false;
      }
    }
    if (nowMs - st.lastDay >= 60000) {
      get('/api/v1/business-days/current', 'GET /business-days/current');
      st.lastDay = nowMs;
    }
  }

  function onEvent(env) {
    realtimeEvents.add(1);
    if (env.occurredAt) realtimeLag.add(Date.now() - Date.parse(env.occurredAt));
    st.lastEventAt = Date.now();
    const type = env.eventType || '';
    if (type.startsWith('Order')) { st.dirtyOrders = true; st.dirtyProduction = true; }
    if (type === 'OrderAwaitingAcceptance' || type === 'OrderAccepted' || type === 'OrderRejected') st.dirtyAwaiting = true;
    if (type.startsWith('OrderPayment') || type === 'ClosingUpdated') st.dirtyClosing = true;
  }

  // Loop até o fim do teste; reconecta se o hub cair (o app reconecta em 0, 2, 5, 10, 30 s).
  while (Date.now() < endAt) {
    refreshIfNeeded();
    const q = `appVersion=${APP_VERSION}&appPlatform=ANDROID`;
    const neg = call('POST', `/hubs/realtime/negotiate?negotiateVersion=1&${q}`, { token: s.token, ip, name: 'POST /hubs/realtime/negotiate', kind: 'read', expected: [200] });
    const nb = json(neg);
    if (!nb) { sleep(5); continue; }
    const url = `${WS_API}/hubs/realtime?id=${encodeURIComponent(nb.connectionToken)}&access_token=${encodeURIComponent(s.token)}&${q}`;
    ws.connect(url, { headers: { 'X-Forwarded-For': ip }, tags: { name: 'WS /hubs/realtime' } }, (socket) => {
      socket.on('open', () => {
        socket.send('{"protocol":"json","version":1}\u001e');
        tick();
        socket.setInterval(() => {
          if (Date.now() >= endAt) { socket.close(); return; }
          tick();
        }, 1000);
        socket.setInterval(() => socket.send('{"type":6}\u001e'), 15000);
      });
      socket.on('message', (data) => {
        for (const frame of String(data).split('\u001e')) {
          if (!frame) continue;
          let m;
          try { m = JSON.parse(frame); } catch (e) { continue; }
          if (m.type === 1 && m.target === 'event' && m.arguments && m.arguments[0]) onEvent(m.arguments[0]);
          if (m.type === 7) socket.close();
        }
      });
      socket.on('error', () => realtimeDisconnects.add(1));
      socket.on('close', () => {});
      // O hub fecha quando o access token vence (CloseOnAuthenticationExpiration): reabre com o token renovado.
      socket.setTimeout(() => socket.close(), Math.max(1000, Math.min(endAt - Date.now(), (s.exp - nowSeconds() - 70) * 1000)));
    });
    if (Date.now() < endAt) { realtimeDisconnects.add(1); sleep(1); }
  }
}

// ---------------------------------------------------------------------------------------------------------------------
// Resumo: JSON completo em .out/ + tabela por rota no stdout.

export function handleSummary(data) {
  const label = __ENV.RUN_LABEL || `x${MULT}`;
  const lines = [];
  const m = data.metrics;
  const fmt = (v) => (v === undefined ? '-' : v.toFixed(1));
  lines.push(`\n=== ${label}: ${TENANTS.length} estabelecimentos, multiplicador ${MULT}, ${DEVICES.length} aparelhos ===`);
  lines.push('rota | n | p50 | p95 | p99 | max (ms)');
  for (const key of Object.keys(m).filter((k) => k.startsWith('http_req_duration{')).sort()) {
    const v = m[key].values;
    lines.push(`${key.replace('http_req_duration', '')} | ${v.count} | ${fmt(v.med)} | ${fmt(v['p(95)'])} | ${fmt(v['p(99)'])} | ${fmt(v.max)}`);
  }
  const rate = (k) => (m[k] ? (m[k].values.rate * 100).toFixed(3) + '%' : '-');
  const count = (k) => (m[k] ? m[k].values.count : 0);
  lines.push(`http_req_failed ${rate('http_req_failed')} | unexpected ${rate('unexpected_responses')} | checks ${rate('checks')}`);
  lines.push(`http_reqs ${count('http_reqs')} (${m.http_reqs ? m.http_reqs.values.rate.toFixed(1) : '-'}/s) | pedidos app ${count('orders_created')} | site ${count('web_orders_created')} aceitos ${count('web_orders_accepted')} | pagamentos ${count('payments_registered')} | produção ${count('production_moves')} | conflitos de versão ${count('version_conflicts')} | relogins ${count('relogins')}`);
  if (m.realtime_event_lag_ms) {
    const v = m.realtime_event_lag_ms.values;
    lines.push(`SignalR: ${count('realtime_events')} eventos, atraso occurredAt→cliente p50 ${fmt(v.med)} p95 ${fmt(v['p(95)'])} p99 ${fmt(v['p(99)'])} max ${fmt(v.max)} ms, reconexões ${count('realtime_disconnects')}`);
  }
  const failed = Object.entries(data.metrics).filter(([, v]) => v.thresholds && Object.values(v.thresholds).some((t) => !t.ok)).map(([k]) => k);
  lines.push(`limites violados: ${failed.length === 0 ? 'nenhum' : failed.join(', ')}`);
  const text = lines.join('\n') + '\n';
  return {
    stdout: text,
    [`/load/.out/summary-${label}.json`]: JSON.stringify(data, null, 1),
    [`/load/.out/summary-${label}.txt`]: text,
  };
}
