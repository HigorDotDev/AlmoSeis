/**
 * Orquestra conectividade, sync e SignalR enquanto há sessão (08 §8.6):
 * - online/offline pelo expo-network; ao voltar online → sync;
 * - primeiro plano → conecta o SignalR e sincroniza; segundo plano → desconecta (ADR-0036);
 * - evento → invalida; salto de `seq` → sync; AccessRevoked → renova ou sai.
 */
import { useQueryClient } from '@tanstack/react-query';
import * as Network from 'expo-network';
import { useEffect, useRef } from 'react';
import { AppState } from 'react-native';

import { refreshSingleFlight } from '@/api/client';
import { invalidateEntity, qk } from '@/api/queryKeys';
import type { RealtimeEnvelope } from '@/api/types';
import { metaRepo } from '@/db/metaRepo';
import { decideSeq, EventDeduper, emitRealtimeEvent, RealtimeConnection } from '@/features/realtime/realtime';
import { useConnectivity } from '@/stores/connectivity';
import { usePendingOps } from '@/stores/pendingOps';
import { useSession } from '@/stores/session';

import { runSync } from './syncEngine';

export function useAppRuntime(): void {
  const queryClient = useQueryClient();
  const tenantId = useSession((s) => s.me?.tenant.id ?? null);
  const employeeId = useSession((s) => s.me?.employee.id ?? null);
  const online = useConnectivity((s) => s.online);
  const lastSeq = useRef<number | null>(null);
  const connection = useRef<RealtimeConnection | null>(null);
  const deduper = useRef(new EventDeduper());

  // Conectividade.
  useEffect(() => {
    const apply = (s: Network.NetworkState) =>
      useConnectivity.getState().setOnline(Boolean(s.isConnected) && s.isInternetReachable !== false);
    void Network.getNetworkStateAsync().then(apply);
    const sub = Network.addNetworkStateListener(apply);
    return () => sub.remove();
  }, []);

  useEffect(() => {
    if (!tenantId) return;
    void usePendingOps.getState().load(tenantId);
    void metaRepo.lastSeq(tenantId).then((v) => {
      lastSeq.current = v;
    });
  }, [tenantId]);

  useEffect(() => {
    if (!tenantId) return;

    const sync = (force = false) =>
      runSync({ queryClient, tenantId, ownEmployeeId: employeeId, ensureRealtime: () => void connection.current?.start() }, force).then(
        async () => {
          lastSeq.current = await metaRepo.lastSeq(tenantId);
        },
      );

    const onEvent = (e: RealtimeEnvelope) => {
      if (!deduper.current.accept(e.eventId)) return;
      if (e.entityType) {
        invalidateEntity(queryClient, e.entityType, e.entityId, employeeId);
      }
      switch (e.eventType) {
        case 'OrderAlertCreated':
        case 'OrderAlertAcknowledged':
          void queryClient.invalidateQueries({ queryKey: ['orders'] });
          void queryClient.invalidateQueries({ queryKey: ['production'] });
          break;
        case 'BusinessDayOpened':
        case 'ClosingFinalized':
          void queryClient.invalidateQueries({ queryKey: ['businessDay'] });
          void queryClient.invalidateQueries({ queryKey: ['closing'] });
          void queryClient.invalidateQueries({ queryKey: ['menus'] });
          void queryClient.invalidateQueries({ queryKey: ['orders'] });
          void queryClient.invalidateQueries({ queryKey: ['production'] });
          break;
        case 'ClosingUpdated':
          void queryClient.invalidateQueries({ queryKey: ['closing'] });
          break;
        case 'MenuUpdated':
          void queryClient.invalidateQueries({ queryKey: ['menus'] });
          break;
        case 'CatalogUpdated':
          void queryClient.invalidateQueries({ queryKey: ['products'] });
          void queryClient.invalidateQueries({ queryKey: ['marmitaSizes'] });
          void queryClient.invalidateQueries({ queryKey: qk.settings });
          break;
        case 'SubscriptionStatusChanged':
          void queryClient.invalidateQueries({ queryKey: qk.me });
          void queryClient.invalidateQueries({ queryKey: qk.subscription });
          break;
        case 'AccessRevoked':
          // Perfis mudaram: um token novo traz as permissões novas. Desativado: o refresh é recusado e a sessão sai.
          void refreshSingleFlight()
            .then((ok) => (ok ? queryClient.invalidateQueries({ queryKey: qk.me }) : useSession.getState().signOut('revoked')))
            .catch(() => {});
          break;
      }
      const decision = decideSeq(lastSeq.current, e.seq);
      if (decision === 'advance') {
        lastSeq.current = e.seq;
        void metaRepo.setLastSeq(tenantId, e.seq!);
      } else if (decision === 'gap') {
        void sync();
      }
      emitRealtimeEvent(e);
    };

    const conn = new RealtimeConnection({
      getAccessToken: () => useSession.getState().tokens?.accessToken ?? null,
      onEvent,
      onReconnected: () => void sync(),
      onGaveUp: () => {},
    });
    connection.current = conn;

    const activate = () => {
      if (!useConnectivity.getState().online) return;
      void conn.start();
      void sync();
    };
    activate();

    const appState = AppState.addEventListener('change', (s) => {
      if (s === 'active') activate();
      else if (s === 'background') void conn.stop();
    });

    return () => {
      appState.remove();
      void conn.stop();
      connection.current = null;
    };
  }, [tenantId, employeeId, queryClient]);

  // Voltou online: sincroniza e reconecta.
  useEffect(() => {
    if (online && tenantId && connection.current) {
      void connection.current.start();
      void runSync({ queryClient, tenantId, ownEmployeeId: employeeId }, true);
    }
  }, [online, tenantId, employeeId, queryClient]);
}
