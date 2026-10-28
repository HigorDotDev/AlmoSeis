/** Dados de sessão e do estabelecimento (08 §8.3: `['me']`, `['settings']`). */
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useEffect } from 'react';

import { authApi } from '@/api/endpoints/auth';
import { tenantApi } from '@/api/endpoints/tenant';
import { qk } from '@/api/queryKeys';
import type { Session, Tenant, TenantSettings } from '@/api/types';
import { useSession } from '@/stores/session';

const MIN = 60_000;

/** Mantém `me` (permissões, assinatura) atualizado; `SubscriptionStatusChanged` e `AccessRevoked` invalidam. */
export function useMeSync() {
  const signedIn = useSession((s) => s.status === 'signedIn');
  const setMe = useSession((s) => s.setMe);
  const query = useQuery({ queryKey: qk.me, queryFn: async () => (await authApi.me()).data, staleTime: 5 * MIN, enabled: signedIn });
  useEffect(() => {
    if (query.data) setMe(query.data);
  }, [query.data, setMe]);
  return query;
}

export function useSettings() {
  return useQuery({ queryKey: qk.settings, queryFn: async () => (await tenantApi.settings()).data, staleTime: 10 * MIN });
}

export function useTenant() {
  return useQuery({ queryKey: qk.tenant, queryFn: async () => (await tenantApi.get()).data, staleTime: 10 * MIN });
}

export function useSubscription(enabled = true) {
  return useQuery({ queryKey: qk.subscription, queryFn: async () => (await tenantApi.subscription()).data, staleTime: 5 * MIN, enabled });
}

export function useRoles() {
  return useQuery({ queryKey: qk.roles, queryFn: async () => (await tenantApi.roles()).data.items, staleTime: 60 * MIN });
}

export function useUpdateSettings() {
  const client = useQueryClient();
  return useMutation({
    mutationFn: async ({ body, version }: { body: Partial<Omit<TenantSettings, 'version'>>; version: number }) =>
      (await tenantApi.updateSettings(body, version)).data,
    onSuccess: (data) => client.setQueryData(qk.settings, data),
  });
}

export function useUpdateTenant() {
  const client = useQueryClient();
  return useMutation({
    mutationFn: async ({ body, version }: { body: Partial<Pick<Tenant, 'name' | 'city' | 'state' | 'timeZone'>>; version: number }) =>
      (await tenantApi.update(body, version)).data,
    onSuccess: (data) => {
      client.setQueryData(qk.tenant, data);
      void client.invalidateQueries({ queryKey: qk.me });
    },
  });
}

export function useSwitchTenant() {
  const signIn = useSession((s) => s.signIn);
  return useMutation({
    mutationFn: async (tenantId: string) => (await authApi.switchTenant(tenantId)).data,
    onSuccess: (session: Session) => signIn(session),
  });
}

export function useSetPin() {
  const client = useQueryClient();
  return useMutation({
    mutationFn: ({ pin, currentPassword }: { pin: string; currentPassword: string }) => authApi.setPin(pin, currentPassword),
    onSuccess: () => client.invalidateQueries({ queryKey: qk.me }),
  });
}
