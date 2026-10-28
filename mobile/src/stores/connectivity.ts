/** Conectividade (08 §8.6): `online | offline`, com `degraded` por 10 s depois de uma falha de rede. */
import { create } from 'zustand';

export type SyncState = 'idle' | 'syncing' | 'synced' | 'error';

interface ConnectivityState {
  online: boolean;
  degradedUntil: number;
  syncState: SyncState;
  /** Quando os dados em cache foram atualizados por último ("dados de 11:42"). */
  lastSyncAt: Date | null;
  setOnline(online: boolean): void;
  markDegraded(now?: number): void;
  setSyncState(state: SyncState): void;
}

export const DEGRADED_MS = 10_000;

export const useConnectivity = create<ConnectivityState>((set) => ({
  online: true,
  degradedUntil: 0,
  syncState: 'idle',
  lastSyncAt: null,
  setOnline: (online) => set({ online }),
  markDegraded: (now = Date.now()) => set({ degradedUntil: now + DEGRADED_MS }),
  setSyncState: (syncState) => set(syncState === 'synced' ? { syncState, lastSyncAt: new Date() } : { syncState }),
}));

/** Escritas exigem conexão (ADR-0010): sem ela, só rascunho. */
export function useIsOnline(): boolean {
  return useConnectivity((s) => s.online);
}
