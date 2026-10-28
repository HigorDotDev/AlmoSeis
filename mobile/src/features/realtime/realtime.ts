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
import { API_BASE_URL, APP_VERSION } from '@/config';

export const RECONNECT_DELAYS_MS = [0, 2000, 5000, 10000, 30000];
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
  getAccessToken(): string | null;
  onEvent(envelope: RealtimeEnvelope): void;
  onReconnected(): void;
  onGaveUp(): void;
}

export class RealtimeConnection {
  private connection: HubConnection | null = null;

  constructor(private readonly handlers: RealtimeHandlers) {}

  get connected(): boolean {
    return this.connection?.state === HubConnectionState.Connected;
  }

  async start(): Promise<void> {
    if (this.connection && this.connection.state !== HubConnectionState.Disconnected) return;
    const connection = new HubConnectionBuilder()
      .withUrl(`${API_BASE_URL}/hubs/realtime?appVersion=${encodeURIComponent(APP_VERSION)}`, {
        accessTokenFactory: () => this.handlers.getAccessToken() ?? '',
        transport: HttpTransportType.WebSockets,
        skipNegotiation: true,
      })
      .withAutomaticReconnect(RECONNECT_DELAYS_MS)
      .configureLogging(LogLevel.None)
      .build();
    connection.on('event', (envelope: RealtimeEnvelope) => this.handlers.onEvent(envelope));
    connection.onreconnected(() => this.handlers.onReconnected());
    // Fim das tentativas automáticas: a reconexão volta a ser tentada quando o app ficar online/ativo.
    connection.onclose(() => this.handlers.onGaveUp());
    this.connection = connection;
    try {
      await connection.start();
      this.handlers.onReconnected();
    } catch {
      this.handlers.onGaveUp();
    }
  }

  async stop(): Promise<void> {
    const c = this.connection;
    this.connection = null;
    if (c) await c.stop();
  }
}
