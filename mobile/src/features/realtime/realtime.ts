/**
 * Tempo real (08 §8.6, 06 §6.14, ADR-0031/0036). O evento é só aviso: os dados vêm da API.
 * Deduplica por `eventId`, invalida pelo mapa de §8.3 e agenda o sync em salto de `seq`.
 */
import {
  HttpTransportType,
  HubConnection,
  HubConnectionBuilder,
  HubConnectionState,
  LogLevel,
} from '@microsoft/signalr';

import type { RealtimeEnvelope } from '@/api/types';
import { HUB_EXTRA_QUERY } from '@/api/transport';
import { API_BASE_URL, APP_VERSION } from '@/config';

export const RECONNECT_DELAYS_MS = [0, 2000, 5000, 10000, 30000];
/** Depois que a reconexão automática desiste: novas tentativas de `start` (a última se repete). */
export const GAVE_UP_RETRY_DELAYS_MS = [5_000, 15_000, 30_000, 60_000];
/** O hub pode fechar a conexão quando o token vence: renova antes de conectar se faltar menos que isso. */
export const REALTIME_REFRESH_MARGIN_MS = 60_000;
const DEDUP_SIZE = 500;

/** Memória das últimas 500 entradas. */
export class EventDeduper {
  private readonly seen = new Set<string>();
  private readonly order: string[] = [];

  constructor(private readonly size = DEDUP_SIZE) {}

  /** true na primeira vez que o id aparece. */
  accept(eventId: string): boolean {
    if (this.seen.has(eventId)) return false;
    this.seen.add(eventId);
    this.order.push(eventId);
    if (this.order.length > this.size) {
      this.seen.delete(this.order.shift()!);
    }
    return true;
  }
}

export type SeqDecision = 'advance' | 'gap' | 'ignore';

/**
 * Salto de `seq` agenda o sync. Algumas lacunas são esperadas (o feed é filtrado por permissão), mas o sync
 * é barato e compactado. Eventos sem `seq` (ex.: OrderAlertAcknowledged) não mexem no cursor.
 */
export function decideSeq(lastSeq: number | null, seq: number | null): SeqDecision {
  if (seq === null) return 'ignore';
  if (lastSeq === null) return 'gap';
  if (seq <= lastSeq) return 'ignore';
  return seq === lastSeq + 1 ? 'advance' : 'gap';
}

type Listener = (envelope: RealtimeEnvelope) => void;
const listeners = new Set<Listener>();

/** Assinatura dos eventos já deduplicados (aba Pedidos: som e destaque do card). */
export function onRealtimeEvent(listener: Listener): () => void {
  listeners.add(listener);
  return () => listeners.delete(listener);
}

export function emitRealtimeEvent(envelope: RealtimeEnvelope): void {
  for (const l of listeners) l(envelope);
}

export interface RealtimeHandlers {
  /**
   * Chamado antes de cada conexão e reconexão. Assíncrono: renova o token (pelo caminho único de renovação)
   * quando ele está vencido ou perto disso, para a reconexão não ir com um token vencido e levar 401.
   */
  getAccessToken(): Promise<string | null> | string | null;
  onEvent(envelope: RealtimeEnvelope): void;
  onReconnected(): void;
  /** A reconexão automática desistiu; a própria conexão já agendou outra tentativa com espera. */
  onGaveUp(): void;
}

export interface RealtimeOptions {
  retryDelaysMs?: number[];
  setTimer?(fn: () => void, ms: number): unknown;
  clearTimer?(handle: unknown): void;
}

/**
 * Token para o hub: renova antes (mesmo caminho único: `refreshSingleFlight` → coordenador entre abas na web)
 * se ele vence em menos de `REALTIME_REFRESH_MARGIN_MS`. Sem rede ou refresh recusado, devolve o que houver:
 * o 401 do hub cai na nova tentativa, e a sessão expirada é tratada pelas chamadas da API.
 */
export async function realtimeAccessToken(
  refreshIfExpiring: (marginMs: number) => Promise<boolean>,
  currentToken: () => string | null,
): Promise<string | null> {
  try {
    await refreshIfExpiring(REALTIME_REFRESH_MARGIN_MS);
  } catch {
    // Sem rede: segue com o token atual.
  }
  return currentToken();
}

export class RealtimeConnection {
  private connection: HubConnection | null = null;
  private stopped = false;
  private retryTimer: unknown = null;
  private retryAttempt = 0;
  private readonly retryDelaysMs: number[];
  private readonly setTimer: (fn: () => void, ms: number) => unknown;
  private readonly clearTimer: (handle: unknown) => void;

  constructor(
    private readonly handlers: RealtimeHandlers,
    options: RealtimeOptions = {},
  ) {
    this.retryDelaysMs = options.retryDelaysMs ?? GAVE_UP_RETRY_DELAYS_MS;
    this.setTimer = options.setTimer ?? ((fn, ms) => setTimeout(fn, ms));
    this.clearTimer = options.clearTimer ?? ((h) => clearTimeout(h as ReturnType<typeof setTimeout>));
  }

  get connected(): boolean {
    return this.connection?.state === HubConnectionState.Connected;
  }

  async start(): Promise<void> {
    this.stopped = false;
    this.cancelRetry();
    if (this.connection && this.connection.state !== HubConnectionState.Disconnected) return;
    const connection = new HubConnectionBuilder()
      .withUrl(`${API_BASE_URL}/hubs/realtime?appVersion=${encodeURIComponent(APP_VERSION)}${HUB_EXTRA_QUERY}`, {
        accessTokenFactory: async () => (await this.handlers.getAccessToken()) ?? '',
        transport: HttpTransportType.WebSockets,
        skipNegotiation: true,
      })
      .withAutomaticReconnect(RECONNECT_DELAYS_MS)
      .configureLogging(LogLevel.None)
      .build();
    connection.on('event', (envelope: RealtimeEnvelope) => this.handlers.onEvent(envelope));
    connection.onreconnected(() => this.handlers.onReconnected());
    connection.onreconnected(() => {
      this.retryAttempt = 0;
    });
    // Fim das tentativas automáticas (ou o servidor fechou com o token vencido e a reconexão não voltou):
    // agenda outro `start` com espera. Uma conexão parada por `stop()` não volta.
    connection.onclose(() => {
      if (this.connection !== connection || this.stopped) return;
      this.gaveUp();
    });
    this.connection = connection;
    try {
      await connection.start();
      this.retryAttempt = 0;
      this.handlers.onReconnected();
    } catch {
      if (this.connection === connection && !this.stopped) this.gaveUp();
    }
  }

  async stop(): Promise<void> {
    this.stopped = true;
    this.cancelRetry();
    const c = this.connection;
    this.connection = null;
    if (c) await c.stop();
  }

  private gaveUp(): void {
    this.handlers.onGaveUp();
    this.cancelRetry();
    const delay = this.retryDelaysMs[Math.min(this.retryAttempt, this.retryDelaysMs.length - 1)] ?? 60_000;
    this.retryAttempt++;
    this.retryTimer = this.setTimer(() => {
      this.retryTimer = null;
      if (!this.stopped) void this.start();
    }, delay);
  }

  private cancelRetry(): void {
    if (this.retryTimer !== null) this.clearTimer(this.retryTimer);
    this.retryTimer = null;
  }
}
