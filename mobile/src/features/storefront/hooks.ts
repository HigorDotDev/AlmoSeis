/** Página de pedidos (06 §6.18, EP-002 F-210). Tela de gestão: mutação simples com `If-Match` (08 §8.5, exceção). */
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';

import { isApiError } from '@/api/errors';
import { storefrontApi } from '@/api/endpoints/storefront';
import { qk } from '@/api/queryKeys';
import type { Storefront, UpdateStorefrontBody } from '@/api/types';

/** O estado da página (`status`) muda com o expediente e a pausa: revalida a cada minuto de uso da tela. */
export function useStorefront() {
  return useQuery({ queryKey: qk.storefront, queryFn: async () => (await storefrontApi.get()).data, staleTime: 60_000 });
}

/**
 * `PUT /storefront` [M]. Sucesso: o cache recebe a resposta (com o `status` recalculado). `409 VersionConflict`:
 * o `current` entra no cache na hora (o `MutationCache` global também recarrega), e a tela pede para refazer.
 */
export function useUpdateStorefront() {
  const client = useQueryClient();
  return useMutation({
    mutationFn: async ({ body, version }: { body: UpdateStorefrontBody; version: number }) => (await storefrontApi.update(body, version)).data,
    onSuccess: (data) => client.setQueryData(qk.storefront, data),
    onError: (error) => {
      if (isApiError(error, 'VersionConflict')) {
        const current = error.extensions.current as Storefront | undefined;
        if (current && typeof current.version === 'number') client.setQueryData(qk.storefront, current);
      }
    },
  });
}
